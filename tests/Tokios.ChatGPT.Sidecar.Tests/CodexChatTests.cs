using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Tokios.ChatGPT.Sidecar;

namespace Tokios.ChatGPT.Sidecar.Tests;

public sealed class CodexChatTests
{
    private static readonly SidecarOptions Opt = new();

    private static DefaultHttpContext Ctx()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string Body(HttpContext ctx) =>
        Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());

    /// <summary>Standard turn script: the notification sequence verified against codex v0.153.2 —
    /// turn started, agentMessage deltas, item/completed, tokenUsage, turn/completed.</summary>
    private static async Task HappyTurnAsync(FakeCodexAppServer srv, JsonElement req, string[] deltas)
    {
        await srv.RespondResultAsync(req.GetProperty("id"), new { turn = new { id = "turn_1", status = "inProgress" } });
        var t = srv.LastThreadId!;
        foreach (var delta in deltas)
            await srv.NotifyAsync("item/agentMessage/delta",
                new { threadId = t, turnId = "turn_1", itemId = "item_1", delta });
        var full = string.Concat(deltas);
        await srv.NotifyAsync("item/completed",
            new { threadId = t, turnId = "turn_1", item = new { type = "agentMessage", text = full } });
        await srv.NotifyAsync("thread/tokenUsage/updated",
            new
            {
                threadId = t,
                turnId = "turn_1",
                tokenUsage = new
                {
                    last = new { inputTokens = 10, cachedInputTokens = 0, outputTokens = 5, reasoningOutputTokens = 0, totalTokens = 15 },
                },
            });
        await srv.NotifyAsync("turn/completed",
            new { threadId = t, turn = new { id = "turn_1", status = "completed" } });
    }

    [Fact]
    public async Task RunAsync_AccumulatesDeltasIntoChatCompletion()
    {
        var (client, server) = TestTransport.Create(
            turnScript: (srv, req) => HappyTurnAsync(srv, req, ["2+2", " is 4."]));
        try
        {
            var ctx = Ctx();
            await CodexChat.RunAsync(client, Opt, new FlattenedRequest { Prompt = "What is 2+2?" },
                ctx, CancellationToken.None);

            Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
            using var doc = JsonDocument.Parse(Body(ctx));
            var root = doc.RootElement;
            Assert.Equal("chat.completion", root.GetProperty("object").GetString());
            Assert.Equal(Opt.ServedModelId, root.GetProperty("model").GetString());
            var choice = root.GetProperty("choices")[0];
            Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
            Assert.Equal("2+2 is 4.", choice.GetProperty("message").GetProperty("content").GetString());
            Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
            var usage = root.GetProperty("usage");
            Assert.Equal(10, usage.GetProperty("prompt_tokens").GetInt64());
            Assert.Equal(5, usage.GetProperty("completion_tokens").GetInt64());
            Assert.Equal(15, usage.GetProperty("total_tokens").GetInt64());

            // The ephemeral thread is archived best-effort once the request is done.
            await server.WaitForRequestAsync("thread/archive");
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamAsync_EmitsRoleThenDeltasThenFinishUsageThenDone()
    {
        var (client, server) = TestTransport.Create(
            turnScript: (srv, req) => HappyTurnAsync(srv, req, ["Hello", ", world"]));
        try
        {
            var ctx = Ctx();
            var req = new FlattenedRequest { Prompt = "hi", Stream = true, IncludeUsage = true };
            await CodexChat.StreamAsync(client, Opt, req, ctx, CancellationToken.None);

            Assert.Equal("text/event-stream", ctx.Response.ContentType);
            var frames = Body(ctx).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("data: [DONE]", frames[^1]);

            var chunks = frames[..^1]
                .Select(f => JsonDocument.Parse(f["data: ".Length..]).RootElement.Clone())
                .ToArray();
            // role chunk + 2 content chunks + finish chunk + usage chunk.
            Assert.Equal(5, chunks.Length);
            foreach (var c in chunks)
                Assert.Equal("chat.completion.chunk", c.GetProperty("object").GetString());

            var delta0 = chunks[0].GetProperty("choices")[0].GetProperty("delta");
            Assert.Equal("assistant", delta0.GetProperty("role").GetString());
            Assert.False(delta0.TryGetProperty("content", out _));

            Assert.Equal("Hello", chunks[1].GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
            Assert.Equal(", world", chunks[2].GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());

            var finish = chunks[3].GetProperty("choices")[0];
            Assert.Equal("stop", finish.GetProperty("finish_reason").GetString());

            Assert.Equal(0, chunks[4].GetProperty("choices").GetArrayLength());
            Assert.Equal(10, chunks[4].GetProperty("usage").GetProperty("prompt_tokens").GetInt64());
            Assert.Equal(15, chunks[4].GetProperty("usage").GetProperty("total_tokens").GetInt64());
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamAsync_OmitsUsageChunkUnlessRequested()
    {
        var (client, server) = TestTransport.Create(
            turnScript: (srv, req) => HappyTurnAsync(srv, req, ["hi"]));
        try
        {
            var ctx = Ctx();
            await CodexChat.StreamAsync(client, Opt, new FlattenedRequest { Prompt = "hi", Stream = true },
                ctx, CancellationToken.None);

            var frames = Body(ctx).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("data: [DONE]", frames[^1]);
            Assert.Equal(3, frames.Length - 1); // role + content + finish, no usage
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunAsync_RateLimitErrorNotification_MapsTo429WithRetryAfter()
    {
        var (client, server) = TestTransport.Create(turnScript: async (srv, req) =>
        {
            await srv.RespondResultAsync(req.GetProperty("id"), new { turn = new { id = "turn_1", status = "inProgress" } });
            await srv.NotifyAsync("error", new
            {
                threadId = srv.LastThreadId,
                turnId = "turn_1",
                willRetry = false,
                error = new { message = "Rate limit reached", codexErrorInfo = "rateLimitExceeded" },
            });
        });
        try
        {
            var ex = await Assert.ThrowsAsync<CodexCliException>(() =>
                CodexChat.RunAsync(client, Opt, new FlattenedRequest { Prompt = "hi" }, Ctx(), CancellationToken.None));
            Assert.Equal(StatusCodes.Status429TooManyRequests, ex.HttpStatus);
            Assert.Equal(60, ex.RetryAfterSeconds);
            Assert.Equal("rate_limit_error", ex.ErrorType);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunAsync_AuthErrorNotification_MapsTo503()
    {
        var (client, server) = TestTransport.Create(turnScript: async (srv, req) =>
        {
            await srv.RespondResultAsync(req.GetProperty("id"), new { turn = new { id = "turn_1", status = "inProgress" } });
            await srv.NotifyAsync("error", new
            {
                threadId = srv.LastThreadId,
                turnId = "turn_1",
                willRetry = false,
                error = new { message = "Unauthorized", codexErrorInfo = "unauthorized", status = 401 },
            });
        });
        try
        {
            var ex = await Assert.ThrowsAsync<CodexCliException>(() =>
                CodexChat.RunAsync(client, Opt, new FlattenedRequest { Prompt = "hi" }, Ctx(), CancellationToken.None));
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, ex.HttpStatus);
            Assert.Equal("auth_error", ex.ErrorType);
            Assert.Contains("codex login", ex.Message);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunAsync_JsonRpcErrorOnTurnStart_MapsTo502()
    {
        var (client, server) = TestTransport.Create(turnScript: (srv, req) =>
            srv.RespondErrorAsync(req.GetProperty("id"), -32000, "no such model"));
        try
        {
            var ex = await Assert.ThrowsAsync<CodexCliException>(() =>
                CodexChat.RunAsync(client, Opt, new FlattenedRequest { Prompt = "hi" }, Ctx(), CancellationToken.None));
            Assert.Equal(StatusCodes.Status502BadGateway, ex.HttpStatus);
            Assert.Equal("upstream_error", ex.ErrorType);
            Assert.Contains("no such model", ex.Message);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunAsync_FailedTurnInTurnCompleted_MapsTo502()
    {
        var (client, server) = TestTransport.Create(turnScript: async (srv, req) =>
        {
            await srv.RespondResultAsync(req.GetProperty("id"), new { turn = new { id = "turn_1", status = "inProgress" } });
            await srv.NotifyAsync("turn/completed", new
            {
                threadId = srv.LastThreadId,
                turn = new
                {
                    id = "turn_1",
                    status = "failed",
                    error = new { message = "context window exceeded", codexErrorInfo = "contextWindowExceeded" },
                },
            });
        });
        try
        {
            var ex = await Assert.ThrowsAsync<CodexCliException>(() =>
                CodexChat.RunAsync(client, Opt, new FlattenedRequest { Prompt = "hi" }, Ctx(), CancellationToken.None));
            Assert.Equal(StatusCodes.Status502BadGateway, ex.HttpStatus);
            Assert.Contains("context window exceeded", ex.Message);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StreamAsync_ErrorBeforeFirstDelta_LeavesResponseUncommitted()
    {
        var (client, server) = TestTransport.Create(turnScript: async (srv, req) =>
        {
            await srv.RespondResultAsync(req.GetProperty("id"), new { turn = new { id = "turn_1", status = "inProgress" } });
            await srv.NotifyAsync("error", new
            {
                threadId = srv.LastThreadId,
                turnId = "turn_1",
                willRetry = false,
                error = new { message = "boom", status = 500 },
            });
        });
        try
        {
            var ctx = Ctx();
            var ex = await Assert.ThrowsAsync<CodexCliException>(() =>
                CodexChat.StreamAsync(client, Opt, new FlattenedRequest { Prompt = "hi", Stream = true },
                    ctx, CancellationToken.None));
            Assert.Equal(StatusCodes.Status502BadGateway, ex.HttpStatus);
            // Uncommitted: Program.cs can still write a clean HTTP error instead of a truncated stream.
            Assert.False(ctx.Response.HasStarted);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartTurnAsync_SendsVerifiedProtocolShapes()
    {
        var opt = new SidecarOptions { Model = "gpt-5-codex", Effort = "high" };
        var (client, server) = TestTransport.Create(opt, turnScript: async (srv, req) =>
        {
            await srv.RespondResultAsync(req.GetProperty("id"), new { turn = new { id = "turn_1", status = "inProgress" } });
            await srv.NotifyAsync("turn/completed",
                new { threadId = srv.LastThreadId, turn = new { id = "turn_1", status = "completed" } });
        });
        try
        {
            await CodexChat.RunAsync(client, opt, new FlattenedRequest
            {
                Prompt = "hi",
                SystemPrompt = "Be terse.",
                Effort = "low", // per-request wins over the sidecar default
            }, Ctx(), CancellationToken.None);

            await server.WaitForRequestAsync("thread/archive");
            var reqs = server.Requests.ToList();

            var init = reqs.Single(r => r.GetProperty("method").GetString() == "initialize");
            Assert.Equal("tokios-chatgpt-sidecar",
                init.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());

            var thread = reqs.Single(r => r.GetProperty("method").GetString() == "thread/start");
            var tp = thread.GetProperty("params");
            Assert.Equal("read-only", tp.GetProperty("sandbox").GetString());
            Assert.Equal("never", tp.GetProperty("approvalPolicy").GetString());
            Assert.True(tp.GetProperty("ephemeral").GetBoolean());
            Assert.Equal("gpt-5-codex", tp.GetProperty("model").GetString());
            Assert.Equal("Be terse.", tp.GetProperty("developerInstructions").GetString());

            var turn = reqs.Single(r => r.GetProperty("method").GetString() == "turn/start");
            var up = turn.GetProperty("params");
            Assert.Equal("low", up.GetProperty("effort").GetString());
            var input = up.GetProperty("input")[0];
            Assert.Equal("text", input.GetProperty("type").GetString());
            Assert.Equal("hi", input.GetProperty("text").GetString());

            var archive = reqs.Single(r => r.GetProperty("method").GetString() == "thread/archive");
            Assert.Equal(server.LastThreadId, archive.GetProperty("params").GetProperty("threadId").GetString());
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
