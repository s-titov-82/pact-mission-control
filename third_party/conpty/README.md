# Bundled ConPTY (conpty.dll + OpenConsole.exe)

Modern, redistributable ConPTY from the Windows Terminal project
(microsoft/terminal, MIT — see LICENSE). Binaries are the prebuilt copies
vendored by microsoft/node-pty at
`third_party/conpty/1.25.260303002/win10-x64/`.

Why: the inbox ConPTY of Windows 11 22H2 does not translate a client's
`SetConsoleMode(ENABLE_MOUSE_INPUT)` into VT mouse-mode sequences, so
crossterm-based TUIs (codex) never get mouse scroll under our terminal while
working fine in Windows Terminal / VS Code (openai/codex#12457). Loading the
bundled DLL (`Conpty*` exports) gives us the same behavior as those hosts.

Both files must stay in the same output folder (`conpty\`): conpty.dll
launches OpenConsole.exe from its own directory.

## Verify the vendored version

The current files come from
`microsoft/node-pty` version `1.25.260303002`, directory `win10-x64`:

<https://github.com/microsoft/node-pty/tree/1.25.260303002/third_party/conpty>

Download the two files from that exact version into
`1.25.260303002/win10-x64/`, then run this command from
`third_party/conpty/`:

```powershell
rtk sha256sum --check SHA256SUMS.txt
```

To update, create a new version directory, update `SHA256SUMS.txt`, verify both
files, and update the path in `Pact.Infrastructure.csproj`, the version in
`THIRD-PARTY-NOTICES.md`, and this README together. Review the upstream MIT
license before committing the new payload.
