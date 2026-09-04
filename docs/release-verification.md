# Release verification

Each Windows x64 release contains three files:

- `pact-mission-control-<version>-win-x64.zip`;
- `manifest.spdx.json`;
- `SHA256SUMS.txt`.

The ZIP contains the framework-dependent application, bundled ConPTY,
WebView2/xterm assets, MIT license, third-party notices, license texts, and an
SPDX 2.2 manifest. It does not contain PDBs, source, tests, private design
history, or foreign runtime payloads.

`licenses/runtime-packages.json` inside the ZIP is the readable exact package
index for that build. Its package tuples are validated against the SPDX document
and the published `.deps.json`; license classifications and evidence are checked
against the version-pinned `third_party/runtime-components.json` manifest from
the matching source revision.

## Verify checksums

Keep the three files in one directory. Compare PowerShell's result with the ZIP
entry in `SHA256SUMS.txt`:

```powershell
Get-FileHash .\pact-mission-control-0.1.0-win-x64.zip -Algorithm SHA256
Get-FileHash .\manifest.spdx.json -Algorithm SHA256
```

Do not run the application when a digest differs.

## Verify GitHub attestations

With GitHub CLI installed:

```powershell
gh attestation verify .\pact-mission-control-0.1.0-win-x64.zip --repo s-titov-82/pact-mission-control
```

The release workflow publishes build provenance for both checksummed subjects
and an SPDX SBOM attestation for the ZIP. An attestation proves that GitHub
Actions produced the artifact for this repository; it is not a substitute for
code signing.

## Authenticode and SmartScreen

The initial `0.1.x` releases may be unsigned by Authenticode. The release
validator records the actual signing state and refuses a false `Signed` claim.
Windows SmartScreen may warn about an unsigned or low-reputation download.
Verify the checksum and GitHub attestation before choosing whether to run it.

Bundled `conpty.dll` and `OpenConsole.exe` retain their separate valid Microsoft
signatures; Pact does not re-sign them.

For packaging internals and local verification, see
[Development](development.md#packaging).
