# NuGet license evidence

These files close license metadata that Microsoft SBOM Tool cannot resolve from
an SPDX expression alone:

- `Avalonia.Angle.Windows.Natives-LICENSE.txt` is copied from the `LICENSE`
  file in `Avalonia.Angle.Windows.Natives` 2.1.27548.20260419;
- `Microsoft.Web.WebView2-LICENSE.txt` and
  `Microsoft.Web.WebView2-NOTICE.txt` are copied from the corresponding files
  in `Microsoft.Web.WebView2` 1.0.4078.44;
- `HtmlAgilityPack-LICENSE.txt` is the license referenced by
  `HtmlAgilityPack` 1.11.42 at
  <https://github.com/zzzprojects/html-agility-pack/blob/v1.11.42/LICENSE>;
- `MS-PL.txt` is the Microsoft Public License text published at
  <https://licenses.nuget.org/MS-PL> for `Svg.Custom` 4.5.0.

`third_party/runtime-components.json` pins every override to a package version.
When one of those packages changes, review the new package metadata and license
files, update the evidence and manifest together, and run
`tests/powershell/PactSbom.Tests.ps1` plus the normal publication validator.
