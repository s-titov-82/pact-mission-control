# Public release smoke protocol

## Purpose and prerequisites

Verify the exact public ZIP, native WebView engine, scenario transport, and
process lease. Start from a clean candidate ZIP on Windows x64. Record its
SHA-256, source commit, and extracted `Pact.exe` SHA-256 below.

Use a fresh `C:\pact-test\release` data root. Do not launch the candidate
without `--data-root`.

## Actions and expected results

1. Verify the published checksum and GitHub artifact attestation as described
   in `../release-verification.md`. Extract the ZIP. It contains the app,
   bundled ConPTY payload, licenses, notices, and no PDB or foreign runtime.
2. Run `Invoke-NativeWebViewGate.ps1` against that exact extracted executable.
   Sanitized direct engine-probe evidence identifies the executable hash and
   source commit and passes without using another build.
3. Launch the candidate with the isolated root, add a sample project, and
   complete one author/reviewer review-loop. Every task and response uses the
   run-owned `.pact-reviews` directory and exact footer; completion cleans it.
4. While the first instance owns the root, start a second non-elevated instance
   in the same Windows session. It refuses cleanly. Close the owner and verify a
   new instance can acquire the root.
5. Inspect notices and license links in the extracted package. Confirm the
   current unsigned-Authenticode and SmartScreen guidance matches observed
   Windows behavior without claiming a signature.

## Results

| Area | Status (`PASS`/`FAIL`) | Date/version | Direct evidence and notes |
| --- | --- | --- | --- |
| ZIP, checksum, SPDX, and notices |  |  |  |
| GitHub artifact attestation |  |  |  |
| Exact executable native WebView gate |  |  |  |
| Complete file-first review scenario |  |  |  |
| Same-session lease refusal and recovery |  |  |  |
| Unsigned Windows first-run behavior |  |  |  |

## Privacy cleanup

Close every candidate and agent process. Resolve and verify the exact
`C:\pact-test\release` path before removing it. Confirm `%APPDATA%\Pact` was
never opened or modified.
