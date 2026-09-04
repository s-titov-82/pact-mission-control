# Vendored xterm.js assets

The terminal viewport uses exact npm releases locked by `package-lock.json`:

- `@xterm/xterm` 5.5.0;
- `@xterm/addon-fit` 0.10.0.

On 2026-07-29, the three checked-in runtime assets were compared with those
packages and were text-equivalent after LF normalization. The normalized
SHA-256 values are:

```text
1f991ac3b4b283ebf96e60ae23a00a52765dd3a2e46fa6fdda9f1aab032f7495 *src/Pact.App.Avalonia/WebAssets/vendor/xterm/xterm.js
ba8e6985669488981ccf40c0cefe3aba80722cb6c92de7ad628b0bd717faf2b6 *src/Pact.App.Avalonia/WebAssets/vendor/xterm/xterm.css
bdaefa370b1bfc42ee88d46fe6072400902a4d4b2d45cd93438dda9b23c97089 *src/Pact.App.Avalonia/WebAssets/vendor/xterm/addon-fit.js
b569f629d00f2626a8100df2a1798210535621e42164dfd426a6fe5aac7b0ccd *third_party/xterm/LICENSE.xterm.txt
e256f01188af527e4d06d21d06fbf785ae9c50d4b328bf03cbe0ba7f0aa4228f *third_party/xterm/LICENSE.addon-fit.txt
```

To update the lockfile and synchronized files:

```powershell
cd third_party/xterm
rtk npm install --package-lock-only --ignore-scripts
cd ../..
rtk pwsh -NoProfile -File tools/Sync-XtermAssets.ps1
```

To verify the checked-in files without changing them:

```powershell
rtk pwsh -NoProfile -File tools/Sync-XtermAssets.ps1 -Verify
```

Both modes run `npm ci --ignore-scripts`. The sync mode copies only the three
runtime files and the two package license texts; the verify mode writes to a
validated temporary directory and compares normalized bytes.
