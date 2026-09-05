using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tokios.ChatGPT.Sidecar.Tests;

/// <summary>
/// In-memory stand-in for <c>codex app-server --stdio</c>: reads newline-delimited JSON-RPC requests
/// from one pipe, writes responses/notifications to the other. The handshake and thread lifecycle are
/// handled automatically; each test supplies a turn script that plays the notifications a real turn
/// would emit (the sequence verified against codex v0.153.2: deltas → item/completed →
/// tokenUsage → turn/completed). No live codex process is involved.
/// </summary>
internal sealed class FakeCodexAppServer : IAsyncDisposable
{
    private readonly StreamReader _in;
    private readonly StreamWriter _out;
    private readonly Func<FakeCodexAppServer, JsonElement, Task>? _turnScript;
    private readonly Task _loop;
    private readonly object _requestsLock = new();
    private int _threadSeq;

    public string? LastThreadId { get; private set; }
    public string? LastTurnId { get; private set; }

    /// <summary>Every request line the client sent, cloned (method, id, params).</summary>
    public List<JsonElement> Requests { get; } = new();

    public FakeCodexAppServer(Stream incoming, Stream outgoing,
        Func<FakeCodexAppServer, JsonElement, Task>? turnScript = null)
    {
        _in = new StreamReader(incoming);
        _out = new StreamWriter(outgoing) { AutoFlush = true };
        _turnScript = turnScript;
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            string? line;
            while ((line = await _in.ReadLineAsync()) is not null)
            {
                if (line.Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("method", out var m) || m.ValueKind != JsonValueKind.String)
                    continue;
                var method = m.GetString();
                var id = root.TryGetProperty("id", out var i) ? i.Clone() : (JsonElement?)null;
                lock (_requestsLock)
                    Requests.Add(root.Clone());

                switch (method)
                {
                    case "initialize":
                        await RespondResultAsync(id, new { protocolVersion = "2" });
                        break;
                    case "thread/start":
                        LastThreadId = "thr_" + Interlocked.Increment(ref _threadSeq);
                        await RespondResultAsync(id, new { thread = new { id = LastThreadId, ephemeral = true } });
                        break;
                    case "turn/start":
                        LastTurnId = "turn_1";
                        if (_turnScript is not null)
                            await _turnScript(this, root.Clone());
                        else
                            await RespondResultAsync(id, new { turn = new { id = "turn_1", status = "inProgress" } });
                        break;
                    case "thread/archive":
                    case "turn/interrupt":
                        await RespondResultAsync(id, new { });
                        break;
                }
            }
        }
        catch { /* the pipes close during teardown */ }
    }

    /// <summary>JSON-RPC result response; <paramref name="result"/> is serialized to JSON.</summary>
    public Task RespondResultAsync(JsonElement? id, object result) =>
        id is null
            ? Task.CompletedTask
            : _out.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id.Value.GetRawText()},\"result\":{JsonSerializer.Serialize(result)}}}");

    /// <summary>JSON-RPC error response; the message is JSON-escaped.</summary>
    public Task RespondErrorAsync(JsonElement id, int code, string message) =>
        _out.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"error\":{{\"code\":{code},\"message\":{JsonSerializer.Serialize(message)}}}}}");

    /// <summary>Server→client notification; <paramref name="prms"/> is serialized to JSON.</summary>
    public Task NotifyAsync(string method, object prms) =>
        _out.WriteLineAsync($"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":{JsonSerializer.Serialize(prms)}}}");

    /// <summary>Waits until a request with the given method has arrived (best-effort archive/interrupt
    /// calls race the test's assertions).</summary>
    public async Task WaitForRequestAsync(string method, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (_requestsLock)
                if (Requests.Any(r => r.TryGetProperty("method", out var m) && m.GetString() == method))
                    return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"The fake app-server never received a '{method}' request.");
    }

    public async ValueTask DisposeAsync()
    {
        try { _out.Dispose(); } catch { }
        try { _in.Dispose(); } catch { }
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }
}

/// <summary>Wires a <see cref="CodexAppServerClient"/> to a <see cref="FakeCodexAppServer"/> over
/// in-memory anonymous pipes (client writes → server reads, server writes → client reads).</summary>
internal static class TestTransport
{
    public static (CodexAppServerClient Client, FakeCodexAppServer Server) Create(
        SidecarOptions? opt = null,
        Func<FakeCodexAppServer, JsonElement, Task>? turnScript = null)
    {
        var serverIn = new AnonymousPipeServerStream(PipeDirection.In);
        var clientOut = new AnonymousPipeClientStream(PipeDirection.Out, serverIn.GetClientHandleAsString());
        var serverOut = new AnonymousPipeServerStream(PipeDirection.Out);
        var clientIn = new AnonymousPipeClientStream(PipeDirection.In, serverOut.GetClientHandleAsString());

        var server = new FakeCodexAppServer(serverIn, serverOut, turnScript);
        var client = new CodexAppServerClient(opt ?? new SidecarOptions(), "test-workdir",
            NullLogger.Instance, clientOut, clientIn);
        return (client, server);
    }
}
