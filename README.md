# Tokios Sidecars

The Tokios Sidecars are two small, open-source local services that expose a **Claude** or a
**ChatGPT** subscription as an OpenAI-compatible `/v1/chat/completions` endpoint on your own
machine, by driving the vendor's own CLI (**Claude Code** or **Codex**) under your own sign-in.
Point the open-source [Tokios Connector](https://github.com/tokiosAI/tokios-connector) at a
sidecar and it becomes one more model your [Tokios](https://tokios.com) gateway can route to,
next to your local models and BYOK providers.

| Sidecar | Binary | Drives | Default listen URL | Default model id |
| --- | --- | --- | --- | --- |
| Claude sidecar | `tokios-claude-sidecar` | `claude` (Claude Code CLI): one `claude -p` child per request | `http://127.0.0.1:11441` | `claude-sidecar` |
| ChatGPT sidecar | `tokios-chatgpt-sidecar` | `codex` (Codex CLI): one persistent `codex app-server` child | `http://127.0.0.1:11442` | `chatgpt-sidecar` |

A sidecar holds no credential of its own. The CLI it spawns finds your existing sign-in the same
way it does in a terminal, and every request counts against that subscription.

- **[SECURITY.md](SECURITY.md)** — the lockdown each sidecar imposes on the CLI it runs, the
  threat model, and how to report vulnerabilities.

## Why this is open source

A sidecar runs on your machine next to a signed-in CLI that can spend your subscription. You
should not have to take our word for what it does with that access. The code here is what ships:
release binaries are built by public CI from this repository, with checksums and build
provenance you can verify (see [Verifying a download](#verifying-a-download)).

The short version of what you'll find in the code (details and exact rules in SECURITY.md):

- **Loopback by default.** Each sidecar listens on `127.0.0.1` unless you configure otherwise.
- **Fixed CLI lockdown.** The Claude sidecar always runs `claude` with `--restricted`, no MCP
  servers, no slash commands, and no session persistence. The ChatGPT sidecar always starts codex
  threads with a read-only sandbox, `approvalPolicy: never`, and `ephemeral: true`. None of this
  is configurable.
- **Empty working directory.** CLI children run in a dedicated empty directory, so no project
  files, instruction files, or git history leak into prompts.
- **No client tool-calling, no prompt logging, no telemetry.**

## Quickstart

### 1. Install and sign in to the CLI

- Claude sidecar: install [Claude Code](https://github.com/anthropics/claude-code) and run
  `claude` once interactively to sign in.
- ChatGPT sidecar: install the [Codex CLI](https://github.com/openai/codex) and run
  `codex login`.

The sidecar spawns the CLI as the user account it runs under, so sign in as that user.

### 2. Get the binary

Download the single-file, self-contained binary for your platform from the
[**latest release**](https://github.com/tokiosAI/tokios-sidecars/releases/latest), or build from
source (below):

| Platform | Claude sidecar | ChatGPT sidecar |
| --- | --- | --- |
| Linux x64 | [`tokios-claude-sidecar-linux-x64`](https://github.com/tokiosAI/tokios-sidecars/releases/latest/download/tokios-claude-sidecar-linux-x64) | [`tokios-chatgpt-sidecar-linux-x64`](https://github.com/tokiosAI/tokios-sidecars/releases/latest/download/tokios-chatgpt-sidecar-linux-x64) |
| Linux arm64 | [`tokios-claude-sidecar-linux-arm64`](https://github.com/tokiosAI/tokios-sidecars/releases/latest/download/tokios-claude-sidecar-linux-arm64) | [`tokios-chatgpt-sidecar-linux-arm64`](https://github.com/tokiosAI/tokios-sidecars/releases/latest/download/tokios-chatgpt-sidecar-linux-arm64) |
| Windows x64 | [`tokios-claude-sidecar-win-x64.exe`](https://github.com/tokiosAI/tokios-sidecars/releases/latest/download/tokios-claude-sidecar-win-x64.exe) | [`tokios-chatgpt-sidecar-win-x64.exe`](https://github.com/tokiosAI/tokios-sidecars/releases/latest/download/tokios-chatgpt-sidecar-win-x64.exe) |
| Windows arm64 | [`tokios-claude-sidecar-win-arm64.exe`](https://github.com/tokiosAI/tokios-sidecars/releases/latest/download/tokios-claude-sidecar-win-arm64.exe) | [`tokios-chatgpt-sidecar-win-arm64.exe`](https://github.com/tokiosAI/tokios-sidecars/releases/latest/download/tokios-chatgpt-sidecar-win-arm64.exe) |
| macOS (Apple silicon) | [`tokios-claude-sidecar-osx-arm64`](https://github.com/tokiosAI/tokios-sidecars/releases/latest/download/tokios-claude-sidecar-osx-arm64) | [`tokios-chatgpt-sidecar-osx-arm64`](https://github.com/tokiosAI/tokios-sidecars/releases/latest/download/tokios-chatgpt-sidecar-osx-arm64) |

Each release also ships `SHA256SUMS` and build provenance — verify before running (see
[Verifying a download](#verifying-a-download)). On macOS and Linux, mark the file executable
(`chmod +x tokios-*-sidecar-*`) after downloading.

Prefer a smaller download? Every binary is also published as a compressed archive — `.tar.gz`
(Linux/macOS) or `.zip` (Windows) — on the
[releases page](https://github.com/tokiosAI/tokios-sidecars/releases/latest). Each archive bundles
the license files, and the `.tar.gz` preserves the executable bit (no `chmod` needed after extracting).

### 3. Run it

```
tokios-claude-sidecar --Sidecar:Model=sonnet
tokios-chatgpt-sidecar --Sidecar:Model=gpt-5-codex
```

Leave `Model` out to use the CLI's own default. The sidecar logs its listen URL, the CLI it will
spawn, and the model id it serves, then waits for requests. Check it with:

```
curl http://127.0.0.1:11441/v1/models
curl http://127.0.0.1:11441/v1/chat/completions -H "Content-Type: application/json" -d "{\"model\":\"claude-sidecar\",\"messages\":[{\"role\":\"user\",\"content\":\"Say hello.\"}]}"
```

### 4. Point the connector at it

In the Tokios Connector's first-run wizard, pick **Enter a local URL + model id manually** and
give it the sidecar's base URL: `http://127.0.0.1:11441/v1` (Claude) or
`http://127.0.0.1:11442/v1` (ChatGPT). The wizard probes the sidecar's `/v1/models`, offers the
served model id, and writes the route for you. The equivalent hand-written connector config:

```jsonc
{
  "Connector": {
    "Routes": [ { "Model": "claude-sidecar", "UpstreamId": "claude" } ],
    "Upstreams": {
      "claude": {
        "BaseUrl": "http://127.0.0.1:11441/v1",
        "AllowedPaths": [ "/chat/completions" ]
      }
    }
  }
}
```

Deploy the model in your Tokios console as usual. Clients that address `claude-sidecar` through
the gateway are then served by your subscription.

## Configuration

Configuration comes from an optional `sidecar.json` **next to the binary** plus command-line
overrides (`--Sidecar:Key=value`) — never environment variables. Both sidecars share the same
keys; only the CLI-path key differs.

```jsonc
{
  "Sidecar": {
    "ListenUrl": "http://127.0.0.1:11441",  // loopback by default; the connector points here
    "ClaudePath": "claude",                  // ChatGPT sidecar: "CodexPath": "codex"
    "Model": "",                             // passed to the CLI; empty = the CLI's own default
    "Effort": "",                            // default reasoning effort; empty = the CLI's own default
    "ServedModelId": "claude-sidecar",       // what /v1/models announces and clients address
    "MaxConcurrency": 2,                     // concurrent CLI turns; subscription limits keep this low
    "QueueTimeoutSeconds": 30,               // how long to wait for a slot before answering 429
    "RequestTimeoutSeconds": 300,            // hard per-request wall clock (504)
    "WorkDir": ""                            // the CLI's working directory; empty = a dedicated dir under temp
  }
}
```

| Key | Claude sidecar | ChatGPT sidecar |
| --- | --- | --- |
| `ClaudePath` / `CodexPath` | Executable name on `PATH` or an absolute path. | Same. On Windows a bare `codex` resolves to the npm `codex.cmd` shim, which is run through `cmd.exe /c`; point at the real binary to skip that. |
| `Model` | Alias or id for `claude --model` (for example `sonnet`, `opus`). | Model id for the codex thread (for example `gpt-5-codex`). |
| `Effort` | `low`, `medium`, `high`, `xhigh`, or `max`. | `low`, `medium`, `high`, or `xhigh`. |

A per-request `reasoning_effort` (`minimal`/`low`/`medium`/`high`, plus the CLI-specific extra
levels) overrides `Effort`. Unknown values are rejected rather than silently ignored.

## What the sidecars can and cannot do

A sidecar is a thin adapter over an **agent CLI**, not over a model API. The CLI brings its own
system prompt and tools, so the chat.completions surface is deliberately narrow:

- **Supported:** `messages` with `system`/`developer`/`user`/`assistant` roles; string content
  or arrays of `text` parts; `stream: true` (SSE chunks ending in `data: [DONE]`);
  `stream_options.include_usage`; `reasoning_effort`; `GET /v1/models`; `GET /healthz`.
- **Flattened:** a single user turn is passed to the CLI untouched. A multi-turn conversation is
  rendered as a labeled `User:` / `Assistant:` transcript, because the CLIs take one prompt
  string. System and developer messages become the CLI's appended system prompt (Claude) or the
  thread's developer instructions (Codex).
- **Rejected with 400:** `tools`, `tool_choice`, `functions`, `function_call`, `tool`-role
  messages, non-text content parts (images), `n` other than 1, `logprobs`.
- **Accepted and ignored:** sampling knobs the CLIs do not expose (`temperature`, `top_p`,
  `max_tokens`, `stop`, penalties).
- **Usage:** `prompt_tokens` and `completion_tokens` come from the CLI's own accounting. The
  Claude sidecar also returns the CLI's cost estimate in an `X-Claude-Cost-Usd` response header
  on non-streaming responses.

Errors use the OpenAI error envelope (`{"error":{"message","type","code"}}`):

| HTTP | `type` | When |
| --- | --- | --- |
| 400 | `invalid_request_error` | Malformed JSON, unsupported field or role, non-UTF-8 body. |
| 429 | `rate_limit_error` | No concurrency slot within `QueueTimeoutSeconds` (`Retry-After: 5`), or the CLI reports a rate/usage limit or overload (`Retry-After: 60`). |
| 502 | `upstream_error` | The CLI failed, produced no result, exceeded the output cap, or the codex app-server died. |
| 503 | `auth_error` / `cli_unavailable` | The CLI is not signed in, or could not be started at all. |
| 504 | `timeout_error` | `RequestTimeoutSeconds` elapsed; the Claude child was killed or the codex turn interrupted. |

If a failure happens after a streaming response has started, the stream ends early and the
sidecar logs the reason; the HTTP status is already committed by then.

## Building from source

Requires the .NET SDK pinned in [`global.json`](global.json).

```
dotnet build TokiosSidecars.sln
dotnet test
```

Publish single-file binaries:

```
dotnet publish src/Tokios.Claude.Sidecar  -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/Tokios.ChatGPT.Sidecar -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

Swap the runtime identifier for `linux-arm64`, `win-x64`, `win-arm64`, or `osx-arm64`.

Versions are date stamps (`yyyy.MMdd.HHmm`). Local builds stamp themselves from the clock;
release builds pin the stamp to the release tag (`-p:SidecarBuildStamp=…`), so a binary's file
version equals the tag it was built from.

## Verifying a download

Every release is built by the public GitHub Actions workflow in this repository. Each release
ships a `SHA256SUMS` file and GitHub build provenance attestations:

```
# checksum
sha256sum -c SHA256SUMS --ignore-missing

# provenance: proves this exact binary was built by this repo's release workflow
gh attestation verify tokios-claude-sidecar-linux-x64 --repo tokiosAI/tokios-sidecars
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Bug reports that include the CLI version and the
sidecar's log lines are especially welcome; for anything security-sensitive use the process in
[SECURITY.md](SECURITY.md) instead of a public issue.

## License

Apache License 2.0 — see [LICENSE](LICENSE). "Tokios" and the Tokios logo are trademarks of the
Tokios project and are not licensed by this repository; forks should use their own name.

Claude and Claude Code are trademarks of Anthropic; ChatGPT and Codex are trademarks of OpenAI.
This project is not affiliated with or endorsed by either company. The sidecars drive those
vendors' CLIs under your own sign-in, so your use of the CLIs remains subject to the vendors'
terms; check that your plan permits the way you use a sidecar.
