# Contributing

Thanks for looking at the Tokios Sidecars. This repository is one open-source part of a larger
system (the Tokios gateway is not in this repo; the connector that fronts a sidecar has
[its own repository](https://github.com/tokiosAI/tokios-connector)), so contributions work best
when they stay inside the sidecars' own boundary: the chat.completions surface, request
flattening, CLI process management, and error mapping.

## Bugs and questions

- **Bugs**: open an issue with the sidecar release tag (the file name you downloaded, or the
  binary's file version), the CLI and its version (`claude --version` / `codex --version`), your
  OS, and the relevant log lines (logs never contain prompts or responses — see SECURITY.md — so
  they are safe to paste; still, skim before posting).
- **Security issues**: do **not** open a public issue — see [SECURITY.md](SECURITY.md).
- **Questions** about the hosted gateway or your account belong in the Tokios console/support,
  not this tracker. Problems with the CLIs themselves belong with their vendors.

## Pull requests

- For anything beyond a small fix, open an issue first so we can agree on the approach.
- **The CLI lockdown is not negotiable.** PRs that make the restriction flags configurable, add
  client tool-calling that reaches the CLI's tools, or relax the empty-working-directory rule
  will not be accepted (SECURITY.md explains why).
- The two sidecars are deliberately parallel: `Program.cs`, `SidecarOptions.cs`, and
  `ChatRequestFlattener.cs` are near-identical between them. A fix to shared behavior should land
  in both.
- Keep the codebase's conventions: nullable enabled, async end to end, one public type per file,
  configuration from `sidecar.json` + command line only (never environment variables), and the
  existing comment style (comments state *constraints and rationale*, not narration).
- `dotnet build TokiosSidecars.sln && dotnet test` must pass. New ChatGPT-sidecar behavior needs
  tests in `tests/Tokios.ChatGPT.Sidecar.Tests` (the fake app-server there lets you script a turn
  without a live codex). Claude-sidecar changes should describe how they were exercised against a
  real `claude` CLI, since there is no test double for it yet.
- New package dependencies need a strong reason — the sidecars have none today and ship as
  self-contained single files.

## Developer Certificate of Origin

Contributions are accepted under the [Developer Certificate of Origin 1.1](https://developercertificate.org/).
Sign off each commit (`git commit -s`), which adds:

```
Signed-off-by: Your Name <you@example.com>
```

By signing off you certify you have the right to submit the contribution under this repository's
Apache-2.0 license.
