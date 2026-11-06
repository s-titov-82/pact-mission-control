# Terminal clipboard correction

Use the existing terminal keyboard route and native xterm/ConPTY behavior.
Do not add agent-specific browser handlers, another paste queue, automatic Enter,
clipboard replacement, or image staging. Preserve the working Codex text/image
clipboard operation. The existing `AgentKeyEncoder.UsesWin32Keys` identifies
the console-key route; do not introduce another client classification.

The bundled xterm translates Ctrl+V into the VT control byte 0x16 and cancels
the browser's default paste. A VT application therefore receives a key, not
clipboard text. VS Code's terminal clipboard contribution explicitly reads text
and falls back to a file resource before performing terminal paste:
https://github.com/microsoft/vscode/blob/main/src/vs/workbench/contrib/terminalContrib/clipboard/browser/terminal.clipboard.contribution.ts

For keyboard Ctrl+V, resolve copied local file paths at the existing C# input
boundary and paste them without Enter. Quote paths containing whitespace and
separate multiple paths with spaces. Otherwise preserve the existing console-key
route when active. For other VT clients, use the existing keyboard-paste writer
for clipboard text; with neither text nor files, forward the original key so the
client can read an image itself. Clipboard reads must not redirect a pending paste
to another selection or a replacement runtime. Ordinary typing and right-click
mouse ownership are unchanged.

Verify text/newline, file paths, native-key preservation, no-text/image key,
selection/restart during async reads, and stopped targets. Run relevant controller,
platform clipboard and input-mode regressions. Build into `artifacts/local-diag`.
Native Codex/Claude text/image/file checks remain a separate evidence gate from
fake-backend tests; do not claim them from unit tests.

## Implementation evidence

2026-11-05: implemented at the existing C# keyboard boundary; browser code and
right-click ownership unchanged. `GetFilePathsAsync` uses Avalonia's clipboard
file API and local-path conversion on the UI dispatcher. No new queue, polling,
clipboard writes, image staging or process starts.

| Requirement | Evidence |
| --- | --- |
| Text paste for VT clients, no Enter | `Ctrl_v_pastes_clipboard_text_for_a_vt_client_without_submitting` (Claude and Codex before console-key mode); RED then GREEN |
| Local file paths, whitespace quoting, multiple files | `Ctrl_v_pastes_copied_file_paths_without_submitting`; RED then GREEN |
| Preserve native console clipboard for text/images; file fallback | `Ctrl_v_keeps_native_console_clipboard_except_for_copied_files` |
| Forward key with no text/files | `Ctrl_v_preserves_the_client_key_when_the_clipboard_has_no_text_or_files` |
| Drop delayed read after selection changes | `Ctrl_v_abandons_clipboard_read_after_selection_changes` |

App clipboard/paste/input/startup scope: 29 passed. The first run exposed a stale
orchestrator fixture expecting first-run autostart after the ROOT change; fixture
now explicitly starts its project session, and the repeat passed. App test build
passed; Release payload published to `artifacts/local-diag` (two existing Avalonia
Watermark deprecation warnings). Diff whitespace check passed.
Native WebView/ConPTY gate passed on the updated payload; evidence:
`.tmp/root-dashboard-validation/native-webview-clipboard.json`. This gate verifies
native terminal/browser transport and snapshots, not the agent clipboard matrix.

Live Codex/Claude text/image/file acceptance: NOT-RUN. Keep this backlog item
open until those native-agent checks pass.
