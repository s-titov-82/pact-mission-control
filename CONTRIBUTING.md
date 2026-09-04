# Contributing to PACT:> Mission Control

Thank you for improving PACT:> Mission Control. Start with
[Development](docs/development.md) for prerequisites, project boundaries, and
the bounded build/test matrix.

## Before opening a change

Use a focused issue or pull request. Describe:

- the user-visible behavior or contributor problem;
- the intended outcome;
- the compatibility, persistence, security, or performance risk;
- the evidence that will show the change works.

For a defect, use the bug report form. For a proposal, describe the user
problem and constraints before selecting an implementation.

## Implementation expectations

- Keep changes focused and preserve unrelated worktree changes.
- Add tests for user-visible or business contracts. Do not add tests that only
  assert source text, private method names, XAML shape, or implementation
  details.
- Add XML documentation to public APIs and explain the contract or non-obvious
  constraint.
- Do not weaken analyzer severities to make a change pass.
- Do not include credentials, private hosts, personal paths, account usage,
  user data, or unredacted logs in fixtures, screenshots, issues, or commits.

Terminal and WebView changes need native Windows evidence. ConPTY changes must
run the `NativeIntegration` tests. WebView2 changes must use the interactive
native gate against the exact candidate executable; the gate's `-SelfTest`
alone is not native evidence.

## Build and test

Keep .NET work bounded on shared machines:

```powershell
dotnet restore Pact.slnx --disable-parallel --locked-mode
dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
dotnet test tests/Pact.Core.Tests/Pact.Core.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
```

Run the additional focused projects and native gates described in
[Development](docs/development.md). State exactly what passed, failed, or was
not run.

## Pull requests

Keep commits reviewable and complete the pull request template. Include the
behavioral risk, focused evidence, and any manual Windows result. Update
[CHANGELOG.md](CHANGELOG.md) when a user-visible change is intended for the
next release.

By submitting a contribution, you confirm that you have the right to submit
the work and agree that it will be licensed under the repository's
[MIT License](LICENSE). This project does not currently use a CLA bot or DCO
sign-off check.
