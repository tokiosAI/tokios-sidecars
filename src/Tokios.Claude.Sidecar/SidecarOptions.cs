namespace Tokios.Claude.Sidecar;

/// <summary>
/// Sidecar configuration (command-line options + config file only — never env vars, matching the
/// connector/server convention). Bind from the "Sidecar" section, e.g.
/// <c>--Sidecar:Model=sonnet --Sidecar:MaxConcurrency=2</c>.
/// </summary>
public sealed class SidecarOptions
{
    /// <summary>Loopback URL the sidecar listens on. The connector points an upstream's BaseUrl here.</summary>
    public string ListenUrl { get; set; } = "http://127.0.0.1:11441";

    /// <summary>Claude CLI executable (name on PATH or absolute path).</summary>
    public string ClaudePath { get; set; } = "claude";

    /// <summary>Model alias/id passed to <c>claude --model</c>. Empty = the CLI's own default.</summary>
    public string Model { get; set; } = "";

    /// <summary>Default thinking effort passed to <c>claude --effort</c> (low|medium|high|xhigh|max).
    /// Empty = the CLI's own default. A per-request <c>reasoning_effort</c> overrides this.</summary>
    public string Effort { get; set; } = "";

    /// <summary>Model id announced in <c>/v1/models</c> and echoed in responses. Independent of
    /// <see cref="Model"/>: clients address this id, the CLI picks the real model.</summary>
    public string ServedModelId { get; set; } = "claude-sidecar";

    /// <summary>Max concurrent claude child processes. Subscription rate limits make high values
    /// pointless; 1–2 is realistic.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>How long a request may wait for a concurrency slot before getting a 429.</summary>
    public int QueueTimeoutSeconds { get; set; } = 30;

    /// <summary>Hard per-request wall clock; the child process is killed when it expires.</summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>Working directory for every claude child. Should be (and defaults to) a dedicated empty
    /// directory: <c>--restricted</c> confines file tools to it, and an empty dir means no project
    /// instruction files or git context leak into prompts. Empty = a directory under the system temp dir.</summary>
    public string WorkDir { get; set; } = "";
}

/// <summary>A chat.completions request flattened down to what the CLI can express.</summary>
public sealed record FlattenedRequest
{
    public required string Prompt { get; init; }

    /// <summary>Concatenated system/developer messages, passed via <c>--append-system-prompt</c>.</summary>
    public string? SystemPrompt { get; init; }

    public bool Stream { get; init; }

    /// <summary>From <c>stream_options.include_usage</c>: emit a final usage chunk before [DONE].</summary>
    public bool IncludeUsage { get; init; }

    /// <summary>Per-request effort from <c>reasoning_effort</c> (already mapped to a CLI level);
    /// null = use the sidecar default.</summary>
    public string? Effort { get; init; }
}
