<!-- Thanks for contributing to the Tokios Sidecars. Please read CONTRIBUTING.md first. -->

## What this changes

<!-- A short description of the change and the motivation. For anything beyond a
     small fix, link the issue where the approach was agreed first. -->

## Checklist

- [ ] `dotnet build TokiosSidecars.sln && dotnet test` passes locally.
- [ ] New ChatGPT-sidecar behavior has tests in `tests/Tokios.ChatGPT.Sidecar.Tests`;
      Claude-sidecar changes describe how they were exercised against a real `claude`.
- [ ] Behavior shared by both sidecars is changed in both.
- [ ] The CLI lockdown flags, the empty-working-directory rule, and the no-client-tools
      rule are untouched (see SECURITY.md).
- [ ] Commits are signed off for the [DCO](https://developercertificate.org/)
      (`git commit -s`) — see CONTRIBUTING.md.
- [ ] No new package dependency without a stated reason (the sidecars have none and
      ship as self-contained single files).
