using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Tokios.ChatGPT.Sidecar;

/// <summary>Handle for one in-flight turn: the ids codex assigned plus that thread's notification
/// stream (routed here by the client's read loop). The elements are whole notification messages
/// (<c>{jsonrpc, method, params}</c>), already cloned out of their parse document.</summary>
public sealed record CodexTurn(string ThreadId, string TurnId, ChannelReader<JsonElement> Notifications);

/// <summary>
/// One persistent <c>codex app-server --stdio</c> child speaking newline-delimited JSON-RPC 2.0 over
/// stdin/stdout, shared by every request the sidecar serves. Unlike the Claude sidecar's
/// spawn-per-request model, codex only streams real token deltas through this protocol
/// (<c>codex exec --json</c> emits a single blob at the end), so the child is spawned lazily on the
/// first request, respawned if it dies, and killed when the sidecar shuts down. Each request runs on
/// its own ephemeral thread; a single stdout read loop dispatches responses by <c>id</c> and routes
/// notifications to the owning request by <c>params.threadId</c>.
/// </summary>
public sealed class CodexAppServerClient : IAsyncDisposable
{
    private const int MaxStderrChars = 64 * 1024;

    private readonly SidecarOptions _opt;
    private readonly string _workDir;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, Channel<JsonElement>> _threads = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private readonly object _stderrLock = new();

    // Injected streams (tests only); when set, no process is spawned or killed.
    private readonly Stream? _injectedStdin;
    private readonly Stream? _injectedStdout;

    private Process? _proc;
    private StreamWriter? _writer;
    private Task? _readLoop;
    private long _nextId;
    private volatile bool _ready;

    public CodexAppServerClient(SidecarOptions opt, string workDir, ILogger log)
    {
        _opt = opt;
        _workDir = workDir;
        _log = log;
    }

    /// <summary>Test constructor: speak the protocol over caller-supplied streams instead of a real
    /// child process (<paramref name="stdin"/> is written as the child's stdin, <paramref name="stdout"/>
    /// is read as its stdout). The initialize handshake still runs; process management does not.</summary>
    internal CodexAppServerClient(SidecarOptions opt, string workDir, ILogger log, Stream stdin, Stream stdout)
        : this(opt, workDir, log)
    {
        _injectedStdin = stdin;
        _injectedStdout = stdout;
    }

    /// <summary>Starts one turn on a fresh ephemeral thread and returns its notification stream. The
    /// caller consumes until <c>turn/completed</c> (or an <c>error</c> notification), then must call
    /// <see cref="ReleaseThread"/> and <see cref="ArchiveThreadAsync"/>.</summary>
    public async Task<CodexTurn> StartTurnAsync(FlattenedRequest req, CancellationToken ct)
    {
        await EnsureStartedAsync(ct);

        // Lockdown (fixed, not configurable): read-only sandbox, no approvals, ephemeral thread that
        // leaves no session state behind. The CLI keeps its own system prompt and tools.
        var threadParams = new Dictionary<string, object?>
        {
            ["cwd"] = _workDir,
            ["sandbox"] = "read-only",
            ["approvalPolicy"] = "never",
            ["ephemeral"] = true,
        };
        // Per-request model (already validated against the allow-list by the endpoint) wins over the
        // sidecar-wide default; both absent = the CLI's own default model.
        var model = req.Model ?? (string.IsNullOrWhiteSpace(_opt.Model) ? null : _opt.Model);
        if (model is not null)
            threadParams["model"] = model;
        // System prompt: codex takes developer instructions per thread (the analog of the Claude
        // sidecar's --append-system-prompt); threads are ephemeral and per-request, so nothing leaks.
        if (!string.IsNullOrEmpty(req.SystemPrompt))
            threadParams["developerInstructions"] = req.SystemPrompt;

        var threadResult = await SendCheckedAsync("thread/start", threadParams, ct);
        var threadId = threadResult.ValueKind == JsonValueKind.Object
            && threadResult.TryGetProperty("thread", out var th)
            && th.TryGetProperty("id", out var ti)
            && ti.ValueKind == JsonValueKind.String
                ? ti.GetString()
                : null;
        if (string.IsNullOrEmpty(threadId))
            throw new CodexCliException(StatusCodes.Status502BadGateway,
                "codex app-server returned no thread id.", "upstream_error");

        // Register before turn/start so no notification for this thread can be missed.
        var channel = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true });
        _threads[threadId] = channel;

        try
        {
            var turnParams = new Dictionary<string, object?>
            {
                ["threadId"] = threadId,
                ["input"] = new object[]
                {
                    new Dictionary<string, object?> { ["type"] = "text", ["text"] = req.Prompt },
                },
            };
            // Per-request reasoning_effort wins over the sidecar-wide default; both were validated upstream.
            var effort = req.Effort ?? (string.IsNullOrWhiteSpace(_opt.Effort) ? null : _opt.Effort);
            if (effort is not null)
                turnParams["effort"] = effort;

            var turnResult = await SendCheckedAsync("turn/start", turnParams, ct);
            var turnId = turnResult.ValueKind == JsonValueKind.Object
                && turnResult.TryGetProperty("turn", out var tu)
                && tu.TryGetProperty("id", out var tui)
                && tui.ValueKind == JsonValueKind.String
                    ? tui.GetString() ?? ""
                    : "";
            return new CodexTurn(threadId, turnId, channel.Reader);
        }
        catch
        {
            ReleaseThread(threadId);
            await ArchiveThreadAsync(threadId);
            throw;
        }
    }

    /// <summary>Stops routing notifications for the thread; called once the request is done with them.</summary>
    public void ReleaseThread(string threadId)
    {
        if (_threads.TryRemove(threadId, out var ch))
            ch.Writer.TryComplete();
    }

    /// <summary>Best-effort <c>turn/interrupt</c>, used when the sidecar's own wall-clock timeout fires.</summary>
    public async Task InterruptTurnAsync(string threadId, string turnId)
    {
        try
        {
            if (string.IsNullOrEmpty(turnId)) return;
            await SendRequestAsync("turn/interrupt", new Dictionary<string, object?>
            {
                ["threadId"] = threadId,
                ["turnId"] = turnId,
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { /* best effort: the process may already be gone */ }
    }

    /// <summary>Best-effort <c>thread/archive</c> so ephemeral threads don't accumulate in codex state.</summary>
    public async Task ArchiveThreadAsync(string threadId)
    {
        try
        {
            await SendRequestAsync("thread/archive", new Dictionary<string, object?>
            {
                ["threadId"] = threadId,
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { /* best effort: the process may already be gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        _ready = false;
        if (_proc is { } proc)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* best effort: the process may have raced us to exit */ }
            proc.Dispose();
            _proc = null;
        }
        if (_injectedStdin is not null) await _injectedStdin.DisposeAsync();
        if (_injectedStdout is not null) await _injectedStdout.DisposeAsync();
        // Only a real child guarantees its stdout closes (on kill) so the read loop ends; with injected
        // streams the far end is owned by the test and a pending read may never unblock — don't wait.
        if (_readLoop is not null && _injectedStdin is null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* the read loop ends when the process dies */ }
        }
        FailAll(new CodexCliException(StatusCodes.Status502BadGateway,
            "codex app-server is shutting down.", "upstream_error"));
        _startLock.Dispose();
        _writeLock.Dispose();
    }

    /// <summary>Spawns the child on first use (and again after it dies), then runs the
    /// <c>initialize</c> handshake. Concurrent callers are serialized through <c>_startLock</c>.</summary>
    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_ready && _proc is null or { HasExited: false }) return;
        await _startLock.WaitAsync(ct);
        try
        {
            if (_ready && _proc is null or { HasExited: false }) return;

            // Anything still outstanding belonged to the previous (dead) process generation.
            FailAll(new CodexCliException(StatusCodes.Status502BadGateway,
                "codex app-server exited unexpectedly.", "upstream_error"));
            if (_proc is { } old)
            {
                old.Dispose();
                _proc = null;
            }
            _writer = null;
            _ready = false;

            try
            {
                if (_injectedStdin is not null)
                {
                    _writer = new StreamWriter(_injectedStdin, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
                    _readLoop = Task.Run(() => ReadLoopAsync(new StreamReader(_injectedStdout!, Encoding.UTF8), null));
                }
                else
                {
                    var proc = Spawn();
                    _proc = proc;
                    _writer = proc.StandardInput;
                    _writer.AutoFlush = true;
                    _readLoop = Task.Run(() => ReadLoopAsync(proc.StandardOutput, proc));
                    _ = Task.Run(() => DrainStderrAsync(proc));
                }

                await SendRequestAsync("initialize", new Dictionary<string, object?>
                {
                    ["clientInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = "tokios-chatgpt-sidecar",
                        ["version"] = "1.0.0",
                    },
                }, ct);
                _ready = true;
                _log.LogInformation("codex app-server started and initialized (path: {CodexPath}).", _opt.CodexPath);
            }
            catch
            {
                if (_proc is { } proc)
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                    catch { /* best effort */ }
                    proc.Dispose();
                    _proc = null;
                }
                _writer = null;
                throw;
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Builds and starts the codex child. The child inherits the environment (it needs
    /// HOME/PATH &amp; friends to find its ChatGPT credentials); the isolation comes from the read-only
    /// sandbox + the empty working directory, not from env scrubbing.</summary>
    private Process Spawn()
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = _workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // JSON-RPC is UTF-8 by spec. On Windows the inherited default would be the console's OEM
            // codepage (e.g. CP437), which corrupts non-ASCII text both ways (' → ΓÇÖ and friends).
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var (fileName, prefixArgs) = ResolveCodexPath(_opt.CodexPath);
        psi.FileName = fileName;
        foreach (var a in prefixArgs)
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("app-server");
        psi.ArgumentList.Add("--stdio");

        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new CodexCliException(StatusCodes.Status503ServiceUnavailable,
                $"Could not start the Codex CLI ('{_opt.CodexPath}'). Is it installed and on PATH? ({ex.Message})",
                "cli_unavailable");
        }
    }

    /// <summary>Windows quirk: a bare <c>codex</c> on PATH is the npm <c>codex.cmd</c> shim, which
    /// <see cref="Process.Start"/> cannot run with <c>UseShellExecute=false</c>. Resolve a bare name by
    /// searching PATH for <c>codex.exe</c> first; if only a <c>.cmd</c>/<c>.bat</c> exists, spawn it via
    /// <c>cmd.exe /c</c> (killed as a tree on shutdown, so the wrapper costs nothing). Pointing
    /// <see cref="SidecarOptions.CodexPath"/> at the real binary avoids this entirely. Non-Windows
    /// resolves bare names through PATH natively.</summary>
    private static (string FileName, string[] PrefixArgs) ResolveCodexPath(string codexPath)
    {
        if (!OperatingSystem.IsWindows())
            return (codexPath, []);

        if (codexPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || codexPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return (ComSpec(), ["/c", codexPath]);

        bool hasDirectory = codexPath.Contains(Path.DirectorySeparatorChar)
            || codexPath.Contains(Path.AltDirectorySeparatorChar);
        if (hasDirectory)
            return (codexPath, []);

        var exe = FindOnPath(codexPath, ".exe");
        if (exe is not null)
            return (exe, []);
        var cmd = FindOnPath(codexPath, ".cmd") ?? FindOnPath(codexPath, ".bat");
        if (cmd is not null)
            return (ComSpec(), ["/c", cmd]);

        // Not found anywhere: let Process.Start fail with a clear "not on PATH" error.
        return (codexPath, []);
    }

    private static string ComSpec() => Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";

    private static string? FindOnPath(string name, string extension)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            try
            {
                var candidate = Path.Combine(dir, name + extension);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* an unparseable PATH entry is not fatal */ }
        }
        return null;
    }

    /// <summary>The single stdout read loop: responses go to their waiting request by <c>id</c>,
    /// notifications to the owning request's channel by <c>params.threadId</c>, and server-initiated
    /// requests (approvals/elicitations — impossible under <c>approvalPolicy: "never"</c> + read-only)
    /// get a defensive JSON-RPC denial. When the loop ends, every outstanding wait fails with 502.
    /// Elements are cloned: their parse document is disposed at the end of this method.</summary>
    private async Task ReadLoopAsync(StreamReader stdout, Process? proc)
    {
        try
        {
            string? line;
            while ((line = await stdout.ReadLineAsync()) is not null)
            {
                if (line.Length == 0) continue;
                try
                {
                    Dispatch(line);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("codex app-server sent an unhandled line: {Error}", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "codex app-server stdout read loop ended with an error.");
        }
        finally
        {
            // Only the current generation's death is fatal; a stale loop just lets EnsureStarted's
            // FailAll (which already ran for it) stand.
            if (proc is null || ReferenceEquals(proc, _proc))
            {
                _ready = false;
                FailAll(new CodexCliException(StatusCodes.Status502BadGateway,
                    "codex app-server exited unexpectedly." + StderrTail(), "upstream_error"));
            }
        }
    }

    private void Dispatch(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.TryGetProperty("method", out var methodEl))
        {
            var method = methodEl.GetString() ?? "";
            if (root.TryGetProperty("id", out var requestId))
            {
                _log.LogWarning("codex app-server sent an unexpected request '{Method}'; denying it.", method);
                var denial = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = requestId.Clone(),
                    ["error"] = new Dictionary<string, object?>
                    {
                        ["code"] = -32601,
                        ["message"] = "The sidecar does not answer server-initiated requests (approvalPolicy is 'never').",
                    },
                });
                _ = Task.Run(async () =>
                {
                    try { await WriteLineAsync(denial, CancellationToken.None); }
                    catch { /* best effort: the process may be dying */ }
                });
                return;
            }

            if (root.TryGetProperty("params", out var prms)
                && prms.ValueKind == JsonValueKind.Object
                && prms.TryGetProperty("threadId", out var tid)
                && tid.ValueKind == JsonValueKind.String
                && _threads.TryGetValue(tid.GetString()!, out var ch))
            {
                ch.Writer.TryWrite(root.Clone());
            }
            // Notifications without a threadId (account/*, config warnings, ...) belong to no request.
            return;
        }

        if (root.TryGetProperty("id", out var idEl)
            && idEl.ValueKind == JsonValueKind.Number
            && idEl.TryGetInt64(out var id)
            && _pending.TryRemove(id, out var tcs))
        {
            if (root.TryGetProperty("error", out var err))
                tcs.TrySetException(CodexChat.ClassifyRpcError(err));
            else
                tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
        }
    }

    /// <summary>Sends a request and translates transport failures (dead process, broken pipe) into a
    /// 502; JSON-RPC error responses arrive as <see cref="CodexCliException"/> already classified.</summary>
    private async Task<JsonElement> SendCheckedAsync(string method, Dictionary<string, object?> prms, CancellationToken ct)
    {
        try
        {
            return await SendRequestAsync(method, prms, ct);
        }
        catch (CodexCliException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new CodexCliException(StatusCodes.Status502BadGateway,
                $"codex app-server is not reachable ({ex.Message}).", "upstream_error");
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, Dictionary<string, object?> prms, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = prms,
        });
        try
        {
            await WriteLineAsync(payload, ct);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            tcs.TrySetCanceled();
            throw;
        }
        return await tcs.Task.WaitAsync(ct);
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        var writer = _writer ?? throw new IOException("codex app-server is not running.");
        await _writeLock.WaitAsync(ct);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FailAll(CodexCliException ex)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetException(ex);
        foreach (var key in _threads.Keys)
            if (_threads.TryRemove(key, out var ch))
                ch.Writer.TryComplete(ex);
    }

    /// <summary>Drains stderr in the background so the child never blocks on a full pipe; the tail is
    /// kept for diagnostics when the process dies.</summary>
    private async Task DrainStderrAsync(Process proc)
    {
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = await proc.StandardError.ReadAsync(buffer)) > 0)
            {
                lock (_stderrLock)
                {
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > MaxStderrChars)
                        _stderr.Remove(0, _stderr.Length - MaxStderrChars);
                }
            }
        }
        catch { /* the process exited */ }
    }

    private string StderrTail()
    {
        lock (_stderrLock)
        {
            var tail = _stderr.ToString().Trim();
            if (tail.Length == 0) return "";
            if (tail.Length > 500) tail = tail[^500..];
            return $" stderr: {tail}";
        }
    }
}
