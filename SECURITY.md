# Security

A Tokios sidecar runs on your machine next to a signed-in Claude Code or Codex CLI — software
that can spend your subscription and, unless constrained, read and write files and run commands.
The sidecars' security posture is therefore about **constraining the CLI they spawn** and
**exposing as little as possible**. This document states the invariants the code enforces, the
threat model they assume, and the trade-offs we made deliberately.

## Reporting a vulnerability

Email **security@tokios.com**. Please do not open public issues for suspected vulnerabilities.
We aim to acknowledge reports within 3 business days. Only the latest release is supported with
security fixes.

In scope: everything in this repository (both sidecars and their tests) as shipped in official
releases. The Claude Code and Codex CLIs themselves belong to their vendors. The Tokios gateway
and the Tokios Connector are separate projects — reports about them are welcome at the same
address but are handled separately.

## Security model

### Invariant: fixed CLI lockdown, not configurable

Every CLI invocation carries a fixed set of restrictions that no configuration key or request
field can loosen:

- **Claude sidecar.** Each request spawns
  `claude --print --restricted --strict-mcp-config --mcp-config {"mcpServers":{}} --disable-slash-commands --no-session-persistence`.
  Per the CLI's documentation, `--restricted` removes the shell, REPL, and web-fetch tools,
  confines file tools to the working directory, and ignores user and project settings files;
  the strict, empty MCP config loads no MCP servers; slash commands and session persistence are
  off. Only `--model`, `--effort`, `--append-system-prompt`, and the output-format flags are added
  from configuration or the request.
- **ChatGPT sidecar.** Each request starts a codex thread with `sandbox: "read-only"`,
  `approvalPolicy: "never"`, `ephemeral: true`, and `cwd` set to the working directory. Only
  `model`, `developerInstructions`, and the per-turn `effort` come from configuration or the
  request. Server-initiated requests from the app-server (approvals, elicitations) are answered
  with a JSON-RPC denial, never forwarded to anyone.

### Invariant: empty working directory

CLI children run in a dedicated directory — by default one under the system temp directory,
otherwise `WorkDir` — that should contain nothing. Together with the lockdown above, this means
the CLI finds no project files, instruction files, or git history to pull into a prompt, and has
nowhere meaningful to write.

### Invariant: no client tool-calling

Requests carrying `tools`, `tool_choice`, `functions`, `function_call`, or `tool`-role messages
are rejected with 400. The CLIs run their own (restricted) tools; the sidecar never lets a client
define tools or inject tool results.

### Invariant: loopback by default

Each sidecar listens on `http://127.0.0.1:<port>` unless `ListenUrl` says otherwise. The intended
caller is the Tokios Connector on the same host, which itself only forwards the exact request
path the gateway is allowed to use (`/chat/completions`).

### Invariant: bounded work

A `MaxConcurrency` gate (default 2) limits concurrent CLI turns; requests still waiting after
`QueueTimeoutSeconds` are answered 429. Every request has a hard wall clock
(`RequestTimeoutSeconds`, default 300 s) after which the Claude child is killed as a process
tree, or the codex turn is interrupted, and the client sees 504. A client disconnect ends the
request's work too (the Claude child is killed; the codex thread is released and archived). CLI
output is capped: the Claude sidecar reads at most 32 MiB of non-streaming stdout and 256 KiB of
stderr; the ChatGPT sidecar keeps a 64 KiB stderr tail.

### Invariant: no prompt/response persistence

Prompts and responses pass through memory and are never written to disk or logged, at any log
level. The startup log line records configuration only (URL, CLI path, model, served id, work
dir, concurrency). The lockdown also asks the CLIs not to persist sessions
(`--no-session-persistence`, `ephemeral: true`). When a CLI fails, up to 500 characters of its
own error output are relayed to the client in the error message — and logged only if the failure
happened after a streaming response had already started — so a sign-in or quota problem can be
diagnosed.

### Invariant: no telemetry, no credentials of its own

A sidecar makes no network calls. It holds no API key or token: the CLI it spawns finds the
operator's existing sign-in the same way it does in a terminal, and talks to its vendor exactly
as it normally would. The ChatGPT sidecar identifies itself to the local app-server as
`tokios-chatgpt-sidecar`; nothing about the host is sent anywhere by the sidecar itself.

## Threat model and deliberate trade-offs

**The sidecar trusts its caller.** There is no authentication on the sidecar's HTTP listener:
anything that can reach the port can spend the subscription. On loopback that means processes on
the same machine, which is the intended deployment. Binding `ListenUrl` to a non-loopback address
is an explicit operator choice and should be paired with a firewall or a trusted network — the
sidecar will not refuse it.

**The child inherits the environment.** The CLI needs `HOME`, `PATH`, and its own variables to
locate credentials, so the sidecar does not scrub the environment. Isolation comes from the
lockdown flags and the empty working directory, not from environment hygiene. Run the sidecar
under an account whose environment you are comfortable exposing to the CLI.

**The lockdown is enforced by the CLI.** The sidecar passes the restrictions; the vendor's CLI
implements them. A CLI bug, or a release that changes what a flag means, changes what the
lockdown guarantees. Keep the CLIs updated, and read their documentation for what `--restricted`
(Claude Code) and `sandbox: read-only` (Codex) mean on your platform.

**Error classification is heuristic.** The CLIs report quota, sign-in, and overload problems as
prose, so the mapping onto 429/503/502 is best-effort text matching plus, for codex, the
structured `codexErrorInfo` and HTTP status when present. Treat the status codes as retry hints,
not as contracts.

**Multi-turn conversations are flattened to text.** Because the CLIs take one prompt string,
earlier turns are rendered as a labeled transcript. Anything in that text can claim to have been
said by the assistant — the same prompt-injection surface as any chat transcript rendered as
text. Clients that need strict role separation should not route through a sidecar.

**The ChatGPT sidecar keeps one app-server process alive.** Requests are multiplexed over a
single `codex app-server --stdio` child by thread id; each request's thread is ephemeral and
archived afterwards. If the child dies, every in-flight request fails with 502 and the next
request respawns it. On Windows a bare `codex` may resolve to the npm `.cmd` shim, which the
sidecar runs through `cmd.exe /c`; set `CodexPath` to the real binary to avoid the wrapper.

**Your subscription's terms apply.** The sidecars bypass no vendor limit or control; they drive
the vendor's own CLI under your own sign-in, and rate or usage limits surface as 429s. Whether
your plan permits this use is between you and the vendor.
