using System.Text;
using System.Text.Json;

namespace Tokios.ChatGPT.Sidecar;

/// <summary>A codex app-server failure mapped onto an HTTP status for the OpenAI surface.</summary>
public sealed class CodexCliException(int httpStatus, string message, string errorType, int? retryAfterSeconds = null)
    : Exception(message)
{
    public int HttpStatus { get; } = httpStatus;
    public string ErrorType { get; } = errorType;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// Maps one request/response cycle with the shared <see cref="CodexAppServerClient"/> onto the OpenAI
/// wire format: one ephemeral codex thread per request, text from <c>item/agentMessage/delta</c>
/// notifications, usage from <c>thread/tokenUsage/updated</c>, done at <c>turn/completed</c>.
/// </summary>
public static class CodexChat
{
    /// <summary>Non-streaming: accumulate the turn's deltas (falling back to the completed agent
    /// message) and emit one chat.completion with usage from the token-usage notification.</summary>
    public static async Task RunAsync(CodexAppServerClient client, SidecarOptions opt, FlattenedRequest req,
        HttpContext ctx, CancellationToken ct)
    {
        var turn = await client.StartTurnAsync(req, ct);
        var text = new StringBuilder();
        string? completedText = null;
        long? inputTokens = null, outputTokens = null;

        try
        {
            while (await turn.Notifications.WaitToReadAsync(ct))
            {
                while (turn.Notifications.TryRead(out var notif))
                {
                    var (method, prms) = Split(notif);
                    switch (method)
                    {
                        case "item/agentMessage/delta":
                            if (prms.ValueKind == JsonValueKind.Object
                                && prms.TryGetProperty("delta", out var d)
                                && d.ValueKind == JsonValueKind.String)
                                text.Append(d.GetString());
                            break;
                        case "item/completed":
                            completedText = CompletedAgentText(prms) ?? completedText;
                            break;
                        case "thread/tokenUsage/updated":
                            ReadUsage(prms, ref inputTokens, ref outputTokens);
                            break;
                        case "error":
                            throw ClassifyError(prms, "codex reported an error.");
                        case "turn/completed":
                            CheckTurnOutcome(prms);
                            goto TurnDone;
                    }
                }
            }
        TurnDone: ;
        }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            // The sidecar's own wall clock fired (client disconnects just let the request die).
            await client.InterruptTurnAsync(turn.ThreadId, turn.TurnId);
            throw;
        }
        finally
        {
            client.ReleaseThread(turn.ThreadId);
            await client.ArchiveThreadAsync(turn.ThreadId);
        }

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
                    message = new { role = "assistant", content = text.Length > 0 ? text.ToString() : completedText ?? "" },
                    finish_reason = "stop",
                },
            },
            usage = new
            {
                prompt_tokens = inputTokens ?? 0,
                completion_tokens = outputTokens ?? 0,
                total_tokens = (inputTokens ?? 0) + (outputTokens ?? 0),
            },
        };

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload), ct);
    }

    /// <summary>Streaming: one content chunk per <c>item/agentMessage/delta</c>. The HTTP response is
    /// only committed (200 + SSE headers) once the first delta arrives, so an early failure (auth, rate
    /// limit, spawn) can still surface as a normal HTTP error.</summary>
    public static async Task StreamAsync(CodexAppServerClient client, SidecarOptions opt, FlattenedRequest req,
        HttpContext ctx, CancellationToken ct)
    {
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = ctx.Response;

        var turn = await client.StartTurnAsync(req, ct);
        bool roleSent = false;
        string? completedText = null;
        long? inputTokens = null, outputTokens = null;

        try
        {
            while (await turn.Notifications.WaitToReadAsync(ct))
            {
                while (turn.Notifications.TryRead(out var notif))
                {
                    var (method, prms) = Split(notif);
                    switch (method)
                    {
                        case "item/agentMessage/delta":
                            if (prms.ValueKind == JsonValueKind.Object
                                && prms.TryGetProperty("delta", out var d)
                                && d.ValueKind == JsonValueKind.String
                                && d.GetString() is { Length: > 0 } delta)
                            {
                                if (!roleSent)
                                {
                                    await WriteChunkAsync(response, new
                                    {
                                        id, @object = "chat.completion.chunk", created, model = opt.ServedModelId,
                                        choices = new[] { new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null } },
                                    }, ct);
                                    roleSent = true;
                                }
                                await WriteChunkAsync(response, new
                                {
                                    id, @object = "chat.completion.chunk", created, model = opt.ServedModelId,
                                    choices = new[] { new { index = 0, delta = new { content = delta }, finish_reason = (string?)null } },
                                }, ct);
                            }
                            break;
                        case "item/completed":
                            completedText = CompletedAgentText(prms) ?? completedText;
                            break;
                        case "thread/tokenUsage/updated":
                            ReadUsage(prms, ref inputTokens, ref outputTokens);
                            break;
                        case "error":
                            throw ClassifyError(prms, "codex reported an error.");
                        case "turn/completed":
                            CheckTurnOutcome(prms);
                            goto TurnDone;
                    }
                }
            }
        TurnDone: ;
        }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await client.InterruptTurnAsync(turn.ThreadId, turn.TurnId);
            throw;
        }
        finally
        {
            client.ReleaseThread(turn.ThreadId);
            await client.ArchiveThreadAsync(turn.ThreadId);
        }

        // Fallback: codex can complete a very short answer with no deltas at all — emit the completed
        // agent message as one content chunk so the stream isn't empty.
        if (!roleSent && !string.IsNullOrEmpty(completedText))
        {
            await WriteChunkAsync(response, new
            {
                id, @object = "chat.completion.chunk", created, model = opt.ServedModelId,
                choices = new[] { new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null } },
            }, CancellationToken.None);
            await WriteChunkAsync(response, new
            {
                id, @object = "chat.completion.chunk", created, model = opt.ServedModelId,
                choices = new[] { new { index = 0, delta = new { content = completedText }, finish_reason = (string?)null } },
            }, CancellationToken.None);
        }

        await WriteChunkAsync(response, new
        {
            id, @object = "chat.completion.chunk", created, model = opt.ServedModelId,
            choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } },
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

    /// <summary>Notifications arrive as whole messages; the interesting half is method + params.</summary>
    private static (string? Method, JsonElement Params) Split(JsonElement notif)
    {
        var method = notif.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        var prms = notif.TryGetProperty("params", out var p) ? p : default;
        return (method, prms);
    }

    /// <summary>The full agent message text from an <c>item/completed</c> notification, or null for any
    /// other item type. The v2 schema names the type <c>agentMessage</c>; the original live probe saw
    /// <c>agent_message</c> — accept both.</summary>
    private static string? CompletedAgentText(JsonElement prms)
    {
        if (prms.ValueKind != JsonValueKind.Object
            || !prms.TryGetProperty("item", out var item)
            || item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("type", out var type)
            || type.GetString() is not ("agentMessage" or "agent_message"))
            return null;
        return item.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String
            ? tx.GetString()
            : null;
    }

    /// <summary><c>tokenUsage.last</c> is this turn's slice (<c>total</c> is cumulative for the thread,
    /// which is always one turn here). inputTokens/outputTokens map onto OpenAI prompt/completion.</summary>
    private static void ReadUsage(JsonElement prms, ref long? inputTokens, ref long? outputTokens)
    {
        if (prms.ValueKind == JsonValueKind.Object
            && prms.TryGetProperty("tokenUsage", out var tu)
            && tu.ValueKind == JsonValueKind.Object
            && tu.TryGetProperty("last", out var last)
            && last.ValueKind == JsonValueKind.Object)
        {
            inputTokens = ReadLong(last, "inputTokens") ?? inputTokens;
            outputTokens = ReadLong(last, "outputTokens") ?? outputTokens;
        }
    }

    /// <summary>A completed turn can still carry a failure: <c>turn.error</c> is the structured form,
    /// a non-"completed" <c>turn.status</c> the fallback.</summary>
    private static void CheckTurnOutcome(JsonElement prms)
    {
        if (prms.ValueKind != JsonValueKind.Object
            || !prms.TryGetProperty("turn", out var turn)
            || turn.ValueKind != JsonValueKind.Object)
            return;
        if (turn.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            throw ClassifyError(err, "codex turn failed.");
        var status = turn.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        if (status is not (null or "completed"))
            throw new CodexCliException(StatusCodes.Status502BadGateway,
                $"codex turn ended with status '{status}'.", "upstream_error");
    }

    /// <summary>Classifies a JSON-RPC error response (request rejected by the app-server itself).</summary>
    internal static CodexCliException ClassifyRpcError(JsonElement error) =>
        ClassifyError(error, "codex app-server rejected the request.");

    /// <summary>Maps a structured codex error onto an HTTP status: an upstream 429 (or the
    /// rateLimit/usageLimit/overloaded codexErrorInfo variants) → 429 with a retry hint; 401/403 or
    /// auth wording → 503 so the operator re-runs <c>codex login</c>; anything else → 502. The payload
    /// shapes vary (error notification <c>params</c>, <c>turn.error</c>, JSON-RPC error objects), so
    /// status/message/codexErrorInfo are dug out recursively and the text heuristics stay best-effort.
    /// </summary>
    internal static CodexCliException ClassifyError(JsonElement error, string fallback)
    {
        var message = FindString(error, "message") ?? fallback;
        var status = FindStatus(error);
        var info = FindString(error, "codexErrorInfo");
        var lower = message.ToLowerInvariant();

        if (status == StatusCodes.Status429TooManyRequests
            || info is "rateLimitExceeded" or "usageLimitExceeded" or "serverOverloaded"
            || lower.Contains("rate limit") || lower.Contains("rate_limit") || lower.Contains("429")
            || lower.Contains("overloaded") || lower.Contains("usage limit"))
            return new CodexCliException(StatusCodes.Status429TooManyRequests,
                "codex reports a rate/usage limit or overload. Retry later.", "rate_limit_error",
                retryAfterSeconds: 60);

        if (status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden
            || info == "unauthorized"
            || lower.Contains("not logged in") || lower.Contains("unauthorized") || lower.Contains("401")
            || lower.Contains("login") || lower.Contains("authentication"))
            return new CodexCliException(StatusCodes.Status503ServiceUnavailable,
                "codex is not authenticated on this host. Sign in interactively (`codex login`) and retry.",
                "auth_error");

        return new CodexCliException(StatusCodes.Status502BadGateway,
            $"codex upstream error{(status is null ? "" : $" ({status})")}: {Truncate(message, 500)}", "upstream_error");
    }

    /// <summary>First HTTP-style status (<c>status</c>/<c>httpStatusCode</c>, 400–599) found anywhere
    /// in the payload — codex forwards the upstream's when it has one.</summary>
    private static int? FindStatus(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in e.EnumerateObject())
            {
                if ((prop.NameEquals("status") || prop.NameEquals("httpStatusCode"))
                    && prop.Value.ValueKind == JsonValueKind.Number
                    && prop.Value.TryGetInt32(out var s)
                    && s is >= 400 and < 600)
                    return s;
                var found = FindStatus(prop.Value);
                if (found is not null) return found;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
            {
                var found = FindStatus(item);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private static string? FindString(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in e.EnumerateObject())
        {
            if (prop.NameEquals(name) && prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString();
            var found = FindString(prop.Value, name);
            if (found is not null) return found;
        }
        return null;
    }

    private static long? ReadLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

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
}
