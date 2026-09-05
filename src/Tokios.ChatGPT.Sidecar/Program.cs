using System.Text.Json;
using Tokios.ChatGPT.Sidecar;

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
    if (options.Effort is not ("low" or "medium" or "high" or "xhigh"))
        throw new InvalidOperationException(
            $"Sidecar:Effort '{options.Effort}' is invalid; use low|medium|high|xhigh (or leave empty for the CLI default).");
}

var workDir = string.IsNullOrWhiteSpace(options.WorkDir)
    ? Path.Combine(Path.GetTempPath(), "tokios-chatgpt-sidecar-work")
    : options.WorkDir;
Directory.CreateDirectory(workDir);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(options.ListenUrl);

var app = builder.Build();
var log = app.Logger;
var gate = new SemaphoreSlim(options.MaxConcurrency);

// One persistent codex app-server child for the whole app lifetime (spawned lazily on first request,
// respawned if it dies); requests are multiplexed over it by thread id.
var codex = new CodexAppServerClient(options, workDir, log);

log.LogInformation(
    "tokios-chatgpt-sidecar listening on {Url} (codex: {CodexPath}, model: {Model}, served id: {ServedId}, workdir: {WorkDir}, concurrency: {Concurrency})",
    options.ListenUrl, options.CodexPath,
    string.IsNullOrWhiteSpace(options.Model) ? "(cli default)" : options.Model,
    options.ServedModelId, workDir, options.MaxConcurrency);

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", model = options.ServedModelId }));

app.MapGet("/v1/models", () => Results.Ok(new
{
    @object = "list",
    data = new[] { new { id = options.ServedModelId, @object = "model", created = 0, owned_by = "tokios-chatgpt-sidecar" } }
        .Concat(options.Models.Select(m => new { id = m, @object = "model", created = 0, owned_by = "tokios-chatgpt-sidecar" }))
        .ToArray(),
}));

app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
{
    FlattenedRequest req;
    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        req = ChatRequestFlattener.Flatten(doc.RootElement, options.AllowClientTools);
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
            await CodexChat.StreamAsync(codex, options, req, ctx, cts.Token);
        else
            await CodexChat.RunAsync(codex, options, req, ctx, cts.Token);
    }
    catch (CodexCliException ex)
    {
        if (ctx.Response.HasStarted)
        {
            // Mid-stream failure: the status line is already gone. The client sees a truncated stream;
            // all we can do is log loudly and end the response.
            log.LogWarning("codex failed mid-stream: {Message}", ex.Message);
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
        // Our own timeout fired (the turn was already sent a best-effort turn/interrupt downstream).
        if (!ctx.Response.HasStarted)
            await WriteErrorAsync(ctx, StatusCodes.Status504GatewayTimeout,
                $"codex did not finish within {options.RequestTimeoutSeconds}s.", "timeout_error");
    }
    finally
    {
        gate.Release();
    }
});

app.Run();
await codex.DisposeAsync();

static async Task WriteErrorAsync(HttpContext ctx, int status, string message, string type)
{
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
    {
        error = new { message, type, code = type },
    }));
}
