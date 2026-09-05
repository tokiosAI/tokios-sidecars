namespace Tokios.ChatGPT.Sidecar;

/// <summary>
/// Sidecar configuration (command-line options + config file only — never env vars, matching the
/// connector/server convention). Bind from the "Sidecar" section, e.g.
/// <c>--Sidecar:Model=gpt-5-codex --Sidecar:MaxConcurrency=2</c>.
/// </summary>
public sealed class SidecarOptions
{
    /// <summary>Loopback URL the sidecar listens on. The connector points an upstream's BaseUrl here.</summary>
    public string ListenUrl { get; set; } = "http://127.0.0.1:11442";

    /// <summary>Codex CLI executable (name on PATH or absolute path). On Windows a bare name resolves
    /// to the npm <c>codex.cmd</c> shim, which the sidecar wraps in <c>cmd.exe /c</c>; pointing this at
    /// the real binary avoids that detour.</summary>
    public string CodexPath { get; set; } = "codex";

    /// <summary>Model id passed to <c>thread/start.model</c>. Empty = the CLI's own default.</summary>
    public string Model { get; set; } = "";

    /// <summary>Default reasoning effort passed as the per-turn <c>effort</c> (low|medium|high|xhigh).
    /// Empty = the CLI's own default. A per-request <c>reasoning_effort</c> overrides this.</summary>
    public string Effort { get; set; } = "";

    /// <summary>Model id announced in <c>/v1/models</c> and echoed in responses. Independent of
    /// <see cref="Model"/>: clients address this id, the CLI picks the real model.</summary>
    public string ServedModelId { get; set; } = "chatgpt-sidecar";

    /// <summary>Max concurrent requests in flight against the shared codex app-server process.
    /// Subscription rate limits make high values pointless; 1–2 is realistic.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>How long a request may wait for a concurrency slot before getting a 429.</summary>
    public int QueueTimeoutSeconds { get; set; } = 30;

    /// <summary>Hard per-request wall clock; the turn is interrupted (<c>turn/interrupt</c>) when it expires.</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>Working directory passed as <c>thread/start.cwd</c>. Should be (and defaults to) a
    /// dedicated empty directory: the thread runs with <c>sandbox: read-only</c>, and an empty dir means
    /// no project context leaks into prompts. Empty = a directory under the system temp dir.</summary>
    public string WorkDir { get; set; } = "";
}

/// <summary>A chat.completions request flattened down to what the CLI can express.</summary>
public sealed record FlattenedRequest
{
    public required string Prompt { get; init; }

    /// <summary>Concatenated system/developer messages, passed as <c>thread/start.developerInstructions</c>.</summary>
    public string? SystemPrompt { get; init; }

    public bool Stream { get; init; }

    /// <summary>From <c>stream_options.include_usage</c>: emit a final usage chunk before [DONE].</summary>
    public bool IncludeUsage { get; init; }

    /// <summary>Per-request effort from <c>reasoning_effort</c> (already mapped to a codex level);
    /// null = use the sidecar default.</summary>
    public string? Effort { get; init; }
}
