# Third-party notices

PACT:> Mission Control redistributes the components below. Runtime component
licenses are included in the release under `licenses/`.

## xterm.js

- Component: `@xterm/xterm`
- Version: `5.5.0`
- Upstream: <https://github.com/xtermjs/xterm.js>
- Local runtime files:
  `src/Pact.App.Avalonia/WebAssets/vendor/xterm/xterm.js` and
  `src/Pact.App.Avalonia/WebAssets/vendor/xterm/xterm.css`
- License: MIT (`licenses/xterm-LICENSE.txt`)

## xterm.js fit addon

- Component: `@xterm/addon-fit`
- Version: `0.10.0`
- Upstream: <https://github.com/xtermjs/xterm.js>
- Local runtime file:
  `src/Pact.App.Avalonia/WebAssets/vendor/xterm/addon-fit.js`
- License: MIT (`licenses/xterm-addon-fit-LICENSE.txt`)

## Bundled ConPTY

- Component: Microsoft Windows Terminal ConPTY build vendored by
  `microsoft/node-pty`
- Version: `1.25.260303002/win10-x64`
- Upstream: <https://github.com/microsoft/node-pty>
- Local runtime files:
  `third_party/conpty/1.25.260303002/win10-x64/conpty.dll` and
  `third_party/conpty/1.25.260303002/win10-x64/OpenConsole.exe`
- License: MIT (`licenses/conpty-LICENSE.txt`)
- Checksums: `licenses/conpty-SHA256SUMS.txt`

## NuGet runtime dependencies

The framework-dependent publish includes runtime libraries restored from NuGet.
Each release derives its exact package list from the published
`Pact.App.Avalonia.deps.json`, records it in
`licenses/runtime-packages.json`, and carries the same package tuples in the
SPDX 2.2 document. The release validator rejects extra packages, missing
packages, and unresolved package licenses.

Automatically parsed MIT-expression packages link to the official NuGet license
reference in that index. Package-specific evidence is included locally when the
package metadata uses a license file or legacy URL:

- `Avalonia.Angle.Windows.Natives` — BSD-3-Clause;
- `HtmlAgilityPack` — MIT;
- `Microsoft.Web.WebView2` — BSD-3-Clause plus its package NOTICE;
- `Microsoft.Windows.SDK.NET.Ref` — Windows SDK license for the Windows notification API projections;
- `Svg.Custom` — Microsoft Public License (`MS-PL`).

This inventory and its license classifications are an engineering record, not
legal advice.

## Pact notification sounds

The bundled `Icq.wav` and `Station.wav` files are original Pact cues synthesized
from sine waves. The filenames preserve the existing sound preferences. The
cues contain no third-party recordings or samples.

## Agent tab logos

The Codex, Claude, and Hermes Agent marks in `Assets/TabIcons` are adapted from
[LobeHub Icons](https://github.com/lobehub/lobe-icons). The marks identify the
respective agents and belong to their respective owners.

MIT License

Copyright (c) 2023 LobeHub

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
