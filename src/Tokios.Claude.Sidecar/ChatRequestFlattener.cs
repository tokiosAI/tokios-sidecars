using System.Text;
using System.Text.Json;

namespace Tokios.Claude.Sidecar;

/// <summary>Thrown for any request-shape problem that maps to an HTTP 400.</summary>
public sealed class ChatRequestException(string message) : Exception(message);

/// <summary>
/// Flattens an OpenAI chat.completions request into a <see cref="FlattenedRequest"/>. The CLI is an agent
/// harness, not a model API: it takes one prompt string and brings its own system prompt and tools, so
/// client-supplied tools/function-calling do not map and are rejected, and sampling knobs the CLI does not
/// expose (temperature, top_p, max_tokens, stop, penalties) are accepted and ignored for client compatibility.
/// </summary>
public static class ChatRequestFlattener
{
    public static FlattenedRequest Flatten(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ChatRequestException("Request body must be a JSON object.");

        // No client tool-calling: the CLI runs its own (restricted) tools; emulating client tools on top
        // is out of scope for v1.
        foreach (var name in new[] { "tools", "tool_choice", "functions", "function_call" })
            if (root.TryGetProperty(name, out _))
                throw new ChatRequestException(
                    $"'{name}' is not supported: the Claude CLI runs its own tools. Send plain chat messages.");

        if (root.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.Number && n.GetInt32() != 1)
            throw new ChatRequestException("'n' other than 1 is not supported.");
        if (root.TryGetProperty("logprobs", out _))
            throw new ChatRequestException("'logprobs' is not supported.");

        // OpenAI reasoning_effort ("minimal"|"low"|"medium"|"high") → claude --effort levels.
        // The CLI's extra levels (xhigh, max) are accepted verbatim; anything else is rejected
        // rather than silently ignored, so a typo doesn't quietly run at the default.
        string? effort = null;
        if (root.TryGetProperty("reasoning_effort", out var re) && re.ValueKind == JsonValueKind.String)
        {
            effort = re.GetString()?.ToLowerInvariant() switch
            {
                "minimal" or "low" => "low",
                "medium" => "medium",
                "high" => "high",
                "xhigh" => "xhigh",
                "max" => "max",
                var other => throw new ChatRequestException(
                    $"Unsupported reasoning_effort '{other}'. Use minimal|low|medium|high (or xhigh|max)."),
            };
        }

        bool stream = root.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        bool includeUsage = stream
            && root.TryGetProperty("stream_options", out var so)
            && so.ValueKind == JsonValueKind.Object
            && so.TryGetProperty("include_usage", out var iu)
            && iu.ValueKind == JsonValueKind.True;

        if (!root.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array
            || messages.GetArrayLength() == 0)
            throw new ChatRequestException("'messages' must be a non-empty array.");

        var systemParts = new List<string>();
        var turns = new List<(string Role, string Text)>();
        foreach (var msg in messages.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Object
                || !msg.TryGetProperty("role", out var r)
                || r.ValueKind != JsonValueKind.String)
                throw new ChatRequestException("Every message must have a string 'role'.");

            var role = r.GetString()!;
            var text = ExtractText(msg);
            switch (role)
            {
                case "system":
                case "developer":
                    if (text.Length > 0) systemParts.Add(text);
                    break;
                case "user":
                case "assistant":
                    turns.Add((role, text));
                    break;
                case "tool":
                    throw new ChatRequestException("tool messages are not supported (no client tool-calling).");
                default:
                    throw new ChatRequestException($"Unsupported message role '{role}'.");
            }
        }

        if (turns.Count == 0)
            throw new ChatRequestException("'messages' contains no user/assistant turn.");

        // Single user turn: pass through untouched. Multi-turn: render as a labeled transcript — the model
        // sees the conversation as text, since the CLI has no structured message input in print mode.
        var prompt = turns.Count == 1 && turns[0].Role == "user"
            ? turns[0].Text
            : string.Join("\n\n", turns.Select(t => $"{(t.Role == "user" ? "User" : "Assistant")}:\n{t.Text}"));

        if (string.IsNullOrWhiteSpace(prompt))
            throw new ChatRequestException("The flattened prompt is empty.");

        return new FlattenedRequest
        {
            Prompt = prompt,
            SystemPrompt = systemParts.Count > 0 ? string.Join("\n\n", systemParts) : null,
            Stream = stream,
            IncludeUsage = includeUsage,
            Effort = effort,
        };
    }

    /// <summary>Content may be a plain string or an array of parts; only text parts are supported.</summary>
    private static string ExtractText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var c) || c.ValueKind == JsonValueKind.Null)
            return "";
        if (c.ValueKind == JsonValueKind.String)
            return c.GetString() ?? "";
        if (c.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in c.EnumerateArray())
            {
                var type = part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var t)
                    ? t.GetString()
                    : null;
                if (type == "text" && part.TryGetProperty("text", out var tx))
                    sb.Append(tx.GetString());
                else
                    throw new ChatRequestException(
                        $"Content part type '{type ?? "?"}' is not supported (text parts only).");
            }
            return sb.ToString();
        }
        throw new ChatRequestException("Unsupported 'content' shape; expected a string or a text-part array.");
    }
}
