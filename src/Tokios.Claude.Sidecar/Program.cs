using System.Text.Json;
using Tokios.Claude.Sidecar;

// Tokios convention: command-line options + config file only (never env vars) for Tokios's own settings.
// The sidecar binds its own config explicitly instead of relying on the host's default sources.
var sidecarConfig = new ConfigurationBuilder()
    .AddJsonFile("sidecar.json", optional: true)
    .AddCommandLine(args)
    .Build();

var options = sidecarConfig.GetSection("Sidecar").Get<SidecarOptions>() ?? new SidecarOptions();
if (options.MaxConcurrency < 1) options.MaxConcurrency = 1;
if (!string.IsNullOrWhiteSpace(options.Effort))
{
    options.Effort = options.Effort.ToLowerInvariant();
    if (options.Effort is not ("low" or "medium" or "high" or "xhigh" or "max"))
        throw new InvalidOperationException(
            $"Sidecar:Effort '{options.Effort}' is invalid; use low|medium|high|xhigh|max (or leave empty for the CLI default).");
}

var workDir = string.IsNullOrWhiteSpace(options.WorkDir)
    ? Path.Combine(Path.GetTempPath(), "tokios-claude-sidecar-work")
    : options.WorkDir;
Directory.CreateDirectory(workDir);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(options.ListenUrl);

var app = builder.Build();
var log = app.Logger;
var gate = new SemaphoreSlim(options.MaxConcurrency);

log.LogInformation(
    "tokios-claude-sidecar listening on {Url} (claude: {ClaudePath}, model: {Model}, served id: {ServedId}, workdir: {WorkDir}, concurrency: {Concurrency})",
    options.ListenUrl, options.ClaudePath,
    string.IsNullOrWhiteSpace(options.Model) ? "(cli default)" : options.Model,
    options.ServedModelId, workDir, options.MaxConcurrency);

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", model = options.ServedModelId }));

app.MapGet("/v1/models", () => Results.Ok(new
{
    @object = "list",
    data = new[] { new { id = options.ServedModelId, @object = "model", created = 0, owned_by = "tokios-claude-sidecar" } }
        .Concat(options.Models.Select(m => new { id = m, @object = "model", created = 0, owned_by = "tokios-claude-sidecar" }))
        .ToArray(),
}));

app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    FlattenedRequest req;
    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        req = ChatRequestFlattener.Flatten(doc.RootElement);
    }
    catch (ChatRequestException ex)
    {
        await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, ex.Message, "invalid_request_error");
        return;
    }
    catch (JsonException)
    {
        await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
            "Request body is not valid JSON.", "invalid_request_error");
        return;
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("UTF-8"))
    {
        // JsonDocument parses lazily: a body with valid JSON syntax but non-UTF-8 bytes inside a string
        // (e.g. CP1252 sent from a legacy Windows console) only fails when the value is materialized.
        await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
            "Request body contains text that is not valid UTF-8. Send UTF-8 JSON.", "invalid_request_error");
        return;
    }

    // Per-request model selection: the served id (or no model at all) means the sidecar default;
    // anything else must be on the Sidecar:Models allow-list — fail closed, like connector AllowedHosts.
    if (!string.IsNullOrWhiteSpace(req.Model) && req.Model != options.ServedModelId
        && !options.Models.Contains(req.Model))
    {
        var valid = string.Join(", ", new[] { options.ServedModelId }.Concat(options.Models));
        await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
            $"Unknown model '{req.Model}'. This sidecar serves: {valid}.", "invalid_request_error");
        return;
    }
    if (req.Model == options.ServedModelId)
        req = req with { Model = null }; // the served id maps onto the configured default model

    // Queue briefly rather than fail fast: subscription rate limits make real concurrency low anyway,
    // and a short queue smooths over a client firing two requests at once.
    if (!await gate.WaitAsync(TimeSpan.FromSeconds(options.QueueTimeoutSeconds), ctx.RequestAborted))
    {
        ctx.Response.Headers.RetryAfter = "5";
        await WriteErrorAsync(ctx, StatusCodes.Status429TooManyRequests,
            "The sidecar is busy (max concurrency reached). Retry shortly.", "rate_limit_error");
        return;
    }

    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

        if (req.Stream)
            await ClaudeChat.StreamAsync(options, req, workDir, ctx, cts.Token);
        else
            await ClaudeChat.RunAsync(options, req, workDir, ctx, cts.Token);
    }
    catch (ClaudeCliException ex)
    {
        if (ctx.Response.HasStarted)
        {
            // Mid-stream failure: the status line is already gone. The client sees a truncated stream;
            // all we can do is log loudly and end the response.
            log.LogWarning("claude failed mid-stream: {Message}", ex.Message);
        }
        else
        {
            if (ex.RetryAfterSeconds is { } retryAfter)
                ctx.Response.Headers.RetryAfter = retryAfter.ToString();
            await WriteErrorAsync(ctx, ex.HttpStatus, ex.Message, ex.ErrorType);
        }
    }
    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        // Our own timeout fired (client disconnects just let the request die with the aborted child).
        if (!ctx.Response.HasStarted)
            await WriteErrorAsync(ctx, StatusCodes.Status504GatewayTimeout,
                $"claude did not finish within {options.RequestTimeoutSeconds}s.", "timeout_error");
    }
    finally
    {
        gate.Release();
    }
});

app.Run();

static async Task WriteErrorAsync(HttpContext ctx, int status, string message, string type)
{
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
    {
        error = new { message, type, code = type },
    }));
}
