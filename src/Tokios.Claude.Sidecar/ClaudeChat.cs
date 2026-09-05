using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tokios.Claude.Sidecar;

/// <summary>A claude CLI failure mapped onto an HTTP status for the OpenAI surface.</summary>
public sealed class ClaudeCliException(int httpStatus, string message, string errorType, int? retryAfterSeconds = null)
    : Exception(message)
{
    public int HttpStatus { get; } = httpStatus;
    public string ErrorType { get; } = errorType;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// Runs one <c>claude -p</c> child per request (spawn-per-request: stateless, no session leakage, at the
/// cost of CLI cold-start latency) and maps its output onto the OpenAI wire format. Every child gets a
/// fixed lockdown flag set; operators cannot loosen it through configuration.
/// </summary>
public static class ClaudeChat
{
    private const int MaxStdoutChars = 32 * 1024 * 1024;
    private const int MaxStderrChars = 256 * 1024;

    /// <summary>Non-streaming: run to completion with <c>--output-format json</c>, emit one chat.completion.</summary>
    public static async Task RunAsync(SidecarOptions opt, FlattenedRequest req, string workDir,
        HttpContext ctx, CancellationToken ct)
    {
        using var p = Start(opt, req, stream: false, workDir);
        await WritePromptAsync(p, req.Prompt, ct);

        var stdoutTask = ReadBoundedAsync(p.StandardOutput, MaxStdoutChars, ct);
        var stderrTask = ReadBoundedAsync(p.StandardError, MaxStderrChars, ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { KillTree(p); throw; }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(stdout); }
        catch (JsonException) { throw ClassifyError(p.ExitCode, stderr, $"claude produced no JSON result (exit {p.ExitCode})."); }

        using (doc)
        {
            var root = doc.RootElement;
            bool isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
            var resultText = root.TryGetProperty("result", out var rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString() ?? ""
                : "";
            if (p.ExitCode != 0 || isError)
                throw ClassifyError(p.ExitCode, stderr, resultText);

            long promptTokens = ReadUsageLong(root, "input_tokens") ?? 0;
            long completionTokens = ReadUsageLong(root, "output_tokens") ?? 0;

            // Surface the CLI's own cost accounting for whoever fronts the subscription.
            if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                ctx.Response.Headers["X-Claude-Cost-Usd"] = cost.GetDouble().ToString("0.######", CultureInfo.InvariantCulture);

            var payload = new
            {
                id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24],
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = opt.ServedModelId,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = resultText },
                        finish_reason = "stop",
                    },
                },
                usage = new
                {
                    prompt_tokens = promptTokens,
                    completion_tokens = completionTokens,
                    total_tokens = promptTokens + completionTokens,
                },
            };

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload), ct);
        }
    }

    /// <summary>Streaming: <c>--output-format stream-json --verbose --include-partial-messages</c> emits NDJSON
    /// whose <c>stream_event</c> entries are raw Anthropic events; text deltas map ~1:1 onto chat.completion
    /// chunks. The HTTP response is only committed (200 + SSE headers) once the first chunk is ready, so an
    /// early CLI failure (auth, rate limit) can still surface as a normal HTTP error.</summary>
    public static async Task StreamAsync(SidecarOptions opt, FlattenedRequest req, string workDir,
        HttpContext ctx, CancellationToken ct)
    {
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = ctx.Response;

        using var p = Start(opt, req, stream: true, workDir);
        await WritePromptAsync(p, req.Prompt, ct);
        var stderrTask = ReadBoundedAsync(p.StandardError, MaxStderrChars, ct);

        bool roleSent = false;
        string? stopReason = null;
        long? inputTokens = null, outputTokens = null;
        string? errorText = null;

        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (line.Length == 0) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                switch (type)
                {
                    case "stream_event":
                        if (!root.TryGetProperty("event", out var ev)) break;
                        var eventType = ev.TryGetProperty("type", out var et) ? et.GetString() : null;
                        switch (eventType)
                        {
                            case "message_start":
                                if (ev.TryGetProperty("message", out var m) && m.TryGetProperty("usage", out var mu))
                                    inputTokens = ReadLong(mu, "input_tokens");
                                if (!roleSent)
                                {
                                    await WriteChunkAsync(response, new
                                    {
                                        id, @object = "chat.completion.chunk", created, model = opt.ServedModelId,
                                        choices = new[] { new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null } },
                                    }, ct);
                                    roleSent = true;
                                }
                                break;

                            case "content_block_delta":
                                // Only text deltas map onto chat content; thinking/input_json deltas are dropped.
                                if (ev.TryGetProperty("delta", out var d)
                                    && d.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta"
                                    && d.TryGetProperty("text", out var txt))
                                {
                                    var text = txt.GetString();
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        await WriteChunkAsync(response, new
                                        {
                                            id, @object = "chat.completion.chunk", created, model = opt.ServedModelId,
                                            choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } },
                                        }, ct);
                                    }
                                }
                                break;

                            case "message_delta":
                                if (ev.TryGetProperty("delta", out var md)
                                    && md.TryGetProperty("stop_reason", out var sr)
                                    && sr.ValueKind == JsonValueKind.String)
                                    stopReason = sr.GetString();
                                if (ev.TryGetProperty("usage", out var u))
                                    outputTokens = ReadLong(u, "output_tokens");
                                break;

                            case "error":
                                errorText = ev.GetRawText();
                                break;
                        }
                        break;

                    case "result":
                        if (root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True)
                            errorText = root.TryGetProperty("result", out var rt) ? rt.GetString() : "claude reported an error.";
                        if (root.TryGetProperty("usage", out var ru))
                        {
                            inputTokens ??= ReadLong(ru, "input_tokens");
                            outputTokens ??= ReadLong(ru, "output_tokens");
                        }
                        break;

                        // "system" (init) and full "assistant"/"user" message events are ignored:
                        // the partial stream_event deltas above carry the incremental text.
                }
            }
        }
        catch (OperationCanceledException) { KillTree(p); throw; }

        await p.WaitForExitAsync(CancellationToken.None);
        var stderr = await stderrTask;

        // The caller distinguishes via Response.HasStarted: uncommitted → clean HTTP error;
        // committed → the client sees a truncated stream and the failure is logged.
        if (errorText is not null || p.ExitCode != 0)
            throw ClassifyError(p.ExitCode, stderr, errorText ?? "");

        await WriteChunkAsync(response, new
        {
            id, @object = "chat.completion.chunk", created, model = opt.ServedModelId,
            choices = new[] { new { index = 0, delta = new { }, finish_reason = MapFinishReason(stopReason) } },
        }, CancellationToken.None);

        if (req.IncludeUsage)
        {
            await WriteChunkAsync(response, new
            {
                id, @object = "chat.completion.chunk", created, model = opt.ServedModelId,
                choices = Array.Empty<object>(),
                usage = new
                {
                    prompt_tokens = inputTokens ?? 0,
                    completion_tokens = outputTokens ?? 0,
                    total_tokens = (inputTokens ?? 0) + (outputTokens ?? 0),
                },
            }, CancellationToken.None);
        }

        await response.WriteAsync("data: [DONE]\n\n", CancellationToken.None);
    }

    /// <summary>Builds and starts the claude child with the fixed lockdown flag set. The child inherits the
    /// environment (it needs HOME/PATH &amp; friends to find its credentials); the isolation comes from
    /// <c>--restricted</c> + the empty working directory, not from env scrubbing.</summary>
    private static Process Start(SidecarOptions opt, FlattenedRequest req, bool stream, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = opt.ClaudePath,
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The CLI's json/stream-json output is UTF-8. On Windows the inherited default would be the
            // console's OEM codepage (e.g. CP437), which corrupts non-ASCII text (' → ΓÇÖ and friends).
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var a = psi.ArgumentList;
        a.Add("--print");
        // Lockdown (fixed, not configurable): removes Bash/PowerShell/REPL/WebFetch and confines file tools to
        // the (empty) work dir, ignores user/project settings files, no MCP servers, no skills, no session writes.
        a.Add("--restricted");
        a.Add("--strict-mcp-config");
        a.Add("--mcp-config");
        a.Add("""{"mcpServers":{}}""");
        a.Add("--disable-slash-commands");
        a.Add("--no-session-persistence");
        if (!string.IsNullOrWhiteSpace(opt.Model))
        {
            a.Add("--model");
            a.Add(opt.Model);
        }
        // Per-request reasoning_effort wins over the sidecar-wide default; both were validated upstream.
        var effort = req.Effort ?? (string.IsNullOrWhiteSpace(opt.Effort) ? null : opt.Effort);
        if (effort is not null)
        {
            a.Add("--effort");
            a.Add(effort);
        }
        if (!string.IsNullOrEmpty(req.SystemPrompt))
        {
            a.Add("--append-system-prompt");
            a.Add(req.SystemPrompt);
        }
        if (stream)
        {
            a.Add("--output-format");
            a.Add("stream-json");
            a.Add("--verbose");
            a.Add("--include-partial-messages");
        }
        else
        {
            a.Add("--output-format");
            a.Add("json");
        }

        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new ClaudeCliException(StatusCodes.Status503ServiceUnavailable,
                $"Could not start the Claude CLI ('{opt.ClaudePath}'). Is it installed and on PATH? ({ex.Message})",
                "cli_unavailable");
        }
    }

    /// <summary>The prompt goes via stdin: no command-line length limits, no shell quoting issues.</summary>
    private static async Task WritePromptAsync(Process p, string prompt, CancellationToken ct)
    {
        try
        {
            await p.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            p.StandardInput.Close();
        }
        catch (OperationCanceledException) { KillTree(p); throw; }
    }

    private static async Task WriteChunkAsync(HttpResponse response, object chunk, CancellationToken ct)
    {
        if (!response.HasStarted)
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            await response.StartAsync(ct);
        }
        await response.WriteAsync("data: " + JsonSerializer.Serialize(chunk) + "\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static string MapFinishReason(string? stopReason) => stopReason switch
    {
        "max_tokens" => "length",
        "refusal" or "refuse" => "content_filter",
        _ => "stop",
    };

    /// <summary>Coarse stderr/result-text heuristics → status codes. The CLI is not an API and reports quota,
    /// auth, and overload problems as prose; treat matches as best-effort signals, not contracts.</summary>
    private static ClaudeCliException ClassifyError(int exitCode, string stderr, string resultText)
    {
        var detail = (stderr + "\n" + resultText).Trim();
        var lower = detail.ToLowerInvariant();

        if (lower.Contains("rate limit") || lower.Contains("rate_limit") || lower.Contains("429")
            || lower.Contains("overloaded") || lower.Contains("usage limit"))
            return new ClaudeCliException(StatusCodes.Status429TooManyRequests,
                "The Claude CLI reports a rate/usage limit or overload. Retry later.", "rate_limit_error",
                retryAfterSeconds: 60);

        if (lower.Contains("not logged in") || lower.Contains("unauthorized") || lower.Contains("401")
            || lower.Contains("oauth") || lower.Contains("login"))
            return new ClaudeCliException(StatusCodes.Status503ServiceUnavailable,
                "The Claude CLI is not authenticated on this host. Sign in interactively (`claude`) and retry.",
                "auth_error");

        return new ClaudeCliException(StatusCodes.Status502BadGateway,
            $"claude exited {exitCode}: {Truncate(detail, 500)}", "upstream_error");
    }

    private static long? ReadUsageLong(JsonElement resultRoot, string name) =>
        resultRoot.TryGetProperty("usage", out var u) ? ReadLong(u, name) : null;

    private static long? ReadLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static void KillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* best effort: the process may have raced us to exit */ }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            sb.Append(buffer, 0, read);
            if (sb.Length > maxChars)
                throw new ClaudeCliException(StatusCodes.Status502BadGateway,
                    "claude output exceeded the sidecar's size cap.", "upstream_error");
        }
        return sb.ToString();
    }
}
