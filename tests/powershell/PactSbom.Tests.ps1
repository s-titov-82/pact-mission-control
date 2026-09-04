[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$ModulePath,
	[Parameter(Mandatory)][string]$TemporaryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedModulePath = [IO.Path]::GetFullPath($ModulePath)
$resolvedTemporaryRoot = [IO.Path]::GetFullPath($TemporaryRoot)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$artifactsPrefix = $artifactsRoot.TrimEnd(
	[IO.Path]::DirectorySeparatorChar,
	[IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedTemporaryRoot.StartsWith(
		$artifactsPrefix,
		[StringComparison]::OrdinalIgnoreCase))
{
	throw "TemporaryRoot must resolve below the repository artifacts directory."
}

Import-Module -Name $resolvedModulePath -Force

function Write-JsonFile
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][object]$Value
	)

	$parent = [IO.Path]::GetDirectoryName($Path)
	$null = [IO.Directory]::CreateDirectory($parent)
	[IO.File]::WriteAllText(
		$Path,
		(($Value | ConvertTo-Json -Depth 20) + "`n"),
		[Text.UTF8Encoding]::new($false))
}

function Write-TextFile
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][string]$Value
	)

	$parent = [IO.Path]::GetDirectoryName($Path)
	$null = [IO.Directory]::CreateDirectory($parent)
	[IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Assert-Equal
{
	param(
		[Parameter(Mandatory)][AllowEmptyString()][string]$Actual,
		[Parameter(Mandatory)][AllowEmptyString()][string]$Expected,
		[Parameter(Mandatory)][string]$Scenario
	)

	if ($Actual -cne $Expected)
	{
		throw "$Scenario`nExpected: $Expected`nActual:   $Actual"
	}
}

function Assert-Throws
{
	param(
		[Parameter(Mandatory)][scriptblock]$Action,
		[Parameter(Mandatory)][string]$ExpectedText,
		[Parameter(Mandatory)][string]$Scenario
	)

	try
	{
		& $Action
	}
	catch
	{
		if (-not $_.Exception.Message.Contains(
				$ExpectedText,
				[StringComparison]::OrdinalIgnoreCase))
		{
			throw "$Scenario failed for the wrong reason: $($_.Exception.Message)"
		}

		return
	}

	throw "$Scenario passed unexpectedly."
}

function New-FixtureSpdxFile
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][string]$PublishRoot
	)

	$relativePath = [IO.Path]::GetRelativePath($PublishRoot, $Path).Replace("\", "/")
	$bytes = [IO.File]::ReadAllBytes($Path)
	$sha256 = [Convert]::ToHexString(
		[Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
	$sha1 = [Convert]::ToHexString(
		[Security.Cryptography.SHA1]::HashData($bytes)).ToLowerInvariant()
	return [ordered]@{
		fileName = "./$relativePath"
		SPDXID = "SPDXRef-File-Fixture-$sha1"
		checksums = @(
			[ordered]@{ algorithm = "SHA256"; checksumValue = $sha256 },
			[ordered]@{ algorithm = "SHA1"; checksumValue = $sha1 })
		licenseConcluded = "NOASSERTION"
		licenseInfoInFiles = @("NOASSERTION")
		copyrightText = "NOASSERTION"
	}
}

function Update-FixtureSpdxFileChecksum
{
	param(
		[Parameter(Mandatory)][string]$SbomPath,
		[Parameter(Mandatory)][string]$PublishRoot,
		[Parameter(Mandatory)][string]$RelativePath
	)

	$sbom = Get-Content -Raw -LiteralPath $SbomPath | ConvertFrom-Json
	$file = @(
		$sbom.files |
			Where-Object {
				$_.fileName.TrimStart(".", "/").Replace("\", "/") -eq
					$RelativePath
			})
	if ($file.Count -ne 1)
	{
		throw "Fixture SPDX does not contain exactly one $RelativePath file."
	}

	$bytes = [IO.File]::ReadAllBytes((Join-Path $PublishRoot $RelativePath))
	$sha256 = [Convert]::ToHexString(
		[Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
	($file[0].checksums | Where-Object algorithm -eq "SHA256").checksumValue =
		$sha256
	Write-JsonFile $SbomPath $sbom
}

function New-SbomFixture
{
	param([Parameter(Mandatory)][string]$Root)

	$publishRoot = Join-Path $Root "publish"
	$licenseRoot = Join-Path $publishRoot "licenses"
	$null = [IO.Directory]::CreateDirectory($licenseRoot)

	Write-TextFile (Join-Path $licenseRoot "runtime-one-LICENSE.txt") "MIT fixture`n"
	Write-TextFile (Join-Path $licenseRoot "webview-LICENSE.txt") "BSD fixture`n"
	Write-TextFile (Join-Path $licenseRoot "webview-NOTICE.txt") "Notice fixture`n"
	Write-TextFile (Join-Path $licenseRoot "xterm-LICENSE.txt") "MIT xterm fixture`n"
	Write-TextFile (Join-Path $licenseRoot "conpty-LICENSE.txt") "MIT ConPTY fixture`n"

	$manifestPath = Join-Path $licenseRoot "runtime-components.json"
	Write-JsonFile $manifestPath ([ordered]@{
		schemaVersion = 1
		referencePackages = @(
			[ordered]@{
				assetName = "Microsoft.Web.WebView2.Core"
				packageName = "Microsoft.Web.WebView2"
			})
		licenseReferences = [ordered]@{
			MIT = "https://licenses.nuget.org/MIT"
		}
		packageOverrides = @(
			[ordered]@{
				packageName = "Runtime.One"
				version = "1.2.3"
				license = "MIT"
				licenseFiles = @("licenses/runtime-one-LICENSE.txt")
			},
			[ordered]@{
				packageName = "Microsoft.Web.WebView2"
				version = "9.9.9"
				license = "BSD-3-Clause"
				licenseFiles = @(
					"licenses/webview-LICENSE.txt",
					"licenses/webview-NOTICE.txt")
			}
		)
		vendoredPackages = @(
			[ordered]@{
				name = "@xterm/xterm"
				version = "5.5.0"
				license = "MIT"
				supplier = "Organization: The xterm.js authors"
				downloadLocation = "https://registry.npmjs.org/@xterm/xterm/-/xterm-5.5.0.tgz"
				purl = "pkg:npm/%40xterm/xterm@5.5.0"
				licenseFiles = @("licenses/xterm-LICENSE.txt")
			},
			[ordered]@{
				name = "Microsoft Windows Terminal ConPTY"
				version = "1.25.260303002/win10-x64"
				license = "MIT"
				supplier = "Organization: Microsoft Corporation"
				downloadLocation = "https://github.com/microsoft/node-pty/tree/1.25.260303002/third_party/conpty"
				licenseFiles = @("licenses/conpty-LICENSE.txt")
			}
		)
	})

	$depsPath = Join-Path $publishRoot "Pact.App.Avalonia.deps.json"
	Write-JsonFile $depsPath ([ordered]@{
		runtimeTarget = [ordered]@{
			name = ".NETCoreApp,Version=v10.0/win-x64"
		}
		targets = [ordered]@{
			".NETCoreApp,Version=v10.0/win-x64" = [ordered]@{
				"Pact.App.Avalonia/0.1.0" = [ordered]@{}
				"Runtime.One/1.2.3" = [ordered]@{
					runtime = [ordered]@{ "lib/net10.0/Runtime.One.dll" = [ordered]@{} }
				}
				"Microsoft.Web.WebView2.Core/9.9.9" = [ordered]@{
					runtime = [ordered]@{ "Microsoft.Web.WebView2.Core.dll" = [ordered]@{} }
				}
			}
		}
		libraries = [ordered]@{
			"Pact.App.Avalonia/0.1.0" = [ordered]@{ type = "project" }
			"Runtime.One/1.2.3" = [ordered]@{ type = "package" }
			"Microsoft.Web.WebView2.Core/9.9.9" = [ordered]@{ type = "reference" }
		}
	})

	$sbomPath = Join-Path $publishRoot "_manifest/spdx_2.2/manifest.spdx.json"
	$fixtureFiles = @(
		Get-ChildItem -LiteralPath $publishRoot -Recurse -File |
			ForEach-Object {
				New-FixtureSpdxFile $_.FullName $publishRoot
			})
	Write-JsonFile $sbomPath ([ordered]@{
		files = $fixtureFiles
		packages = @(
			[ordered]@{
				name = "PACT:> Mission Control"
				SPDXID = "SPDXRef-RootPackage"
				versionInfo = "0.1.0"
				downloadLocation = "NOASSERTION"
				filesAnalyzed = $false
				licenseDeclared = "NOASSERTION"
				licenseConcluded = "NOASSERTION"
				copyrightText = "NOASSERTION"
			},
			[ordered]@{
				name = "Runtime.One"
				SPDXID = "SPDXRef-Package-RuntimeOne"
				versionInfo = "1.2.3"
				downloadLocation = "https://api.nuget.org/v3-flatcontainer/runtime.one/1.2.3/runtime.one.1.2.3.nupkg"
				filesAnalyzed = $false
				licenseDeclared = "MIT"
				licenseConcluded = "NOASSERTION"
				copyrightText = "NOASSERTION"
				externalRefs = @([ordered]@{
					referenceCategory = "PACKAGE-MANAGER"
					referenceType = "purl"
					referenceLocator = "pkg:nuget/Runtime.One@1.2.3"
				})
			},
			[ordered]@{
				name = "Microsoft.Web.WebView2"
				SPDXID = "SPDXRef-Package-WebView"
				versionInfo = "9.9.9"
				downloadLocation = "NOASSERTION"
				filesAnalyzed = $false
				licenseDeclared = "NOASSERTION"
				licenseConcluded = "NOASSERTION"
				copyrightText = "NOASSERTION"
			},
			[ordered]@{
				name = "Test.Only"
				SPDXID = "SPDXRef-Package-TestOnly"
				versionInfo = "7.0.0"
				downloadLocation = "https://example.invalid/test.nupkg"
				filesAnalyzed = $false
				licenseDeclared = "MIT"
				licenseConcluded = "MIT"
				copyrightText = "NOASSERTION"
			}
		)
		externalDocumentRefs = @()
		relationships = @(
			[ordered]@{
				spdxElementId = "SPDXRef-DOCUMENT"
				relationshipType = "DESCRIBES"
				relatedSpdxElement = "SPDXRef-RootPackage"
			},
			[ordered]@{
				spdxElementId = "SPDXRef-RootPackage"
				relationshipType = "DEPENDS_ON"
				relatedSpdxElement = "SPDXRef-Package-TestOnly"
			}
		)
		spdxVersion = "SPDX-2.2"
		dataLicense = "CC0-1.0"
		SPDXID = "SPDXRef-DOCUMENT"
		name = "PACT:> Mission Control 0.1.0"
		documentNamespace = "https://example.invalid/sbom/0.1.0"
		creationInfo = [ordered]@{
			created = "2026-09-03T00:00:00Z"
			creators = @("Tool: fixture")
		}
		documentDescribes = @("SPDXRef-RootPackage")
	})

	return [pscustomobject]@{
		PublishRoot = $publishRoot
		ManifestPath = $manifestPath
		SbomPath = $sbomPath
	}
}

if ([IO.Directory]::Exists($resolvedTemporaryRoot))
{
	[IO.Directory]::Delete($resolvedTemporaryRoot, $true)
}
$null = [IO.Directory]::CreateDirectory($resolvedTemporaryRoot)

try
{
	$valid = New-SbomFixture (Join-Path $resolvedTemporaryRoot "valid")
	Complete-PactSbom `
		-Path $valid.SbomPath `
		-PublishRoot $valid.PublishRoot `
		-ManifestPath $valid.ManifestPath `
		-ProductName "PACT:> Mission Control" `
		-ProductVersion "0.1.0" `
		-ProductLicense "MIT" `
		-ProductDownloadLocation "https://github.com/example/pact" | Out-Null
	Assert-PactSbom `
		-Path $valid.SbomPath `
		-PublishRoot $valid.PublishRoot `
		-ManifestPath $valid.ManifestPath `
		-ProductName "PACT:> Mission Control" `
		-ProductVersion "0.1.0" | Out-Null

	$hashSidecar = New-SbomFixture (Join-Path $resolvedTemporaryRoot "hash-sidecar")
	Write-TextFile "$($hashSidecar.SbomPath).sha256" ("0" * 64)
	Complete-PactSbom `
		-Path $hashSidecar.SbomPath `
		-PublishRoot $hashSidecar.PublishRoot `
		-ManifestPath $hashSidecar.ManifestPath `
		-ProductName "PACT:> Mission Control" `
		-ProductVersion "0.1.0" `
		-ProductLicense "MIT" `
		-ProductDownloadLocation "https://github.com/example/pact" | Out-Null
	$expectedSbomHash = [Convert]::ToHexString(
		[Security.Cryptography.SHA256]::HashData(
			[IO.File]::ReadAllBytes($hashSidecar.SbomPath))).ToLowerInvariant()
	Assert-Equal `
		-Actual (Get-Content -Raw -LiteralPath "$($hashSidecar.SbomPath).sha256") `
		-Expected $expectedSbomHash `
		-Scenario "Normalizer must refresh the Microsoft SBOM checksum sidecar."
	Write-TextFile "$($hashSidecar.SbomPath).sha256" ("f" * 64)
	Assert-Throws `
		-Action {
			Assert-PactSbom `
				-Path $hashSidecar.SbomPath `
				-PublishRoot $hashSidecar.PublishRoot `
				-ManifestPath $hashSidecar.ManifestPath `
				-ProductName "PACT:> Mission Control" `
				-ProductVersion "0.1.0"
		} `
		-ExpectedText "SPDX manifest checksum sidecar does not match the manifest." `
		-Scenario "Tampered SPDX manifest checksum sidecar"

	$completed = Get-Content -Raw -LiteralPath $valid.SbomPath | ConvertFrom-Json
	$actualPackages = @(
		$completed.packages |
			ForEach-Object { "{0}|{1}" -f $_.name, $_.versionInfo } |
			Sort-Object)
	$expectedPackages = @(
		"@xterm/xterm|5.5.0",
		"Microsoft Windows Terminal ConPTY|1.25.260303002/win10-x64",
		"Microsoft.Web.WebView2|9.9.9",
		"PACT:> Mission Control|0.1.0",
		"Runtime.One|1.2.3") | Sort-Object
	Assert-Equal `
		-Actual ($actualPackages -join "`n") `
		-Expected ($expectedPackages -join "`n") `
		-Scenario "Normalizer must keep exactly the shipped package tuples."

	$licensePairs = @(
		$completed.packages |
			ForEach-Object {
				"{0}|{1}|{2}" -f $_.name, $_.licenseDeclared, $_.licenseConcluded
			} |
			Sort-Object)
	$expectedLicenses = @(
		"@xterm/xterm|MIT|MIT",
		"Microsoft Windows Terminal ConPTY|MIT|MIT",
		"Microsoft.Web.WebView2|BSD-3-Clause|BSD-3-Clause",
		"PACT:> Mission Control|MIT|MIT",
		"Runtime.One|MIT|MIT") | Sort-Object
	Assert-Equal `
		-Actual ($licensePairs -join "`n") `
		-Expected ($expectedLicenses -join "`n") `
		-Scenario "Normalizer must conclude every package license."

	$indexPath = Join-Path $valid.PublishRoot "licenses/runtime-packages.json"
	$index = Get-Content -Raw -LiteralPath $indexPath | ConvertFrom-Json
	$indexPackages = @(
		$index.packages |
			ForEach-Object { "{0}|{1}" -f $_.name, $_.version } |
			Sort-Object)
	$expectedIndexPackages = @(
		"@xterm/xterm|5.5.0",
		"Microsoft Windows Terminal ConPTY|1.25.260303002/win10-x64",
		"Microsoft.Web.WebView2|9.9.9",
		"Runtime.One|1.2.3") | Sort-Object
	Assert-Equal `
		-Actual ($indexPackages -join "`n") `
		-Expected ($expectedIndexPackages -join "`n") `
		-Scenario "Dependency index must exactly match third-party runtime packages."

	$missing = New-SbomFixture (Join-Path $resolvedTemporaryRoot "missing")
	$missingSbom = Get-Content -Raw -LiteralPath $missing.SbomPath | ConvertFrom-Json
	$missingSbom.packages = @(
		$missingSbom.packages | Where-Object name -ne "Runtime.One")
	Write-JsonFile $missing.SbomPath $missingSbom
	Assert-Throws `
		-Action {
			Complete-PactSbom `
				-Path $missing.SbomPath `
				-PublishRoot $missing.PublishRoot `
				-ManifestPath $missing.ManifestPath `
				-ProductName "PACT:> Mission Control" `
				-ProductVersion "0.1.0" `
				-ProductLicense "MIT" `
				-ProductDownloadLocation "https://github.com/example/pact"
		} `
		-ExpectedText "Runtime package is missing from the generated SBOM: Runtime.One 1.2.3" `
		-Scenario "Missing runtime package"

	$unreviewed = New-SbomFixture (Join-Path $resolvedTemporaryRoot "unreviewed")
	$unreviewedManifest = Get-Content -Raw -LiteralPath $unreviewed.ManifestPath |
		ConvertFrom-Json
	$unreviewedManifest.packageOverrides = @(
		$unreviewedManifest.packageOverrides |
			Where-Object packageName -ne "Runtime.One")
	Write-JsonFile $unreviewed.ManifestPath $unreviewedManifest
	$unreviewedSbom = Get-Content -Raw -LiteralPath $unreviewed.SbomPath |
		ConvertFrom-Json
	($unreviewedSbom.packages | Where-Object name -eq "Runtime.One").licenseDeclared =
		"NOASSERTION"
	Write-JsonFile $unreviewed.SbomPath $unreviewedSbom
	Assert-Throws `
		-Action {
			Complete-PactSbom `
				-Path $unreviewed.SbomPath `
				-PublishRoot $unreviewed.PublishRoot `
				-ManifestPath $unreviewed.ManifestPath `
				-ProductName "PACT:> Mission Control" `
				-ProductVersion "0.1.0" `
				-ProductLicense "MIT" `
				-ProductDownloadLocation "https://github.com/example/pact"
		} `
		-ExpectedText "Runtime package has no reviewed license: Runtime.One 1.2.3" `
		-Scenario "Unreviewed runtime license"

	$badHash = New-SbomFixture (Join-Path $resolvedTemporaryRoot "bad-hash")
	Complete-PactSbom `
		-Path $badHash.SbomPath `
		-PublishRoot $badHash.PublishRoot `
		-ManifestPath $badHash.ManifestPath `
		-ProductName "PACT:> Mission Control" `
		-ProductVersion "0.1.0" `
		-ProductLicense "MIT" `
		-ProductDownloadLocation "https://github.com/example/pact" | Out-Null
	Write-TextFile `
		(Join-Path $badHash.PublishRoot "licenses/xterm-LICENSE.txt") `
		"tampered license fixture`n"
	Assert-Throws `
		-Action {
			Assert-PactSbom `
				-Path $badHash.SbomPath `
				-PublishRoot $badHash.PublishRoot `
				-ManifestPath $badHash.ManifestPath `
				-ProductName "PACT:> Mission Control" `
				-ProductVersion "0.1.0"
		} `
		-ExpectedText "SPDX file checksum mismatch: licenses/xterm-LICENSE.txt" `
		-Scenario "Tampered runtime license evidence"

	$tamperedLicense = New-SbomFixture (
		Join-Path $resolvedTemporaryRoot "tampered-license")
	Complete-PactSbom `
		-Path $tamperedLicense.SbomPath `
		-PublishRoot $tamperedLicense.PublishRoot `
		-ManifestPath $tamperedLicense.ManifestPath `
		-ProductName "PACT:> Mission Control" `
		-ProductVersion "0.1.0" `
		-ProductLicense "MIT" `
		-ProductDownloadLocation "https://github.com/example/pact" | Out-Null
	$tamperedSbom = Get-Content -Raw -LiteralPath $tamperedLicense.SbomPath |
		ConvertFrom-Json
	$tamperedSpdxPackage = $tamperedSbom.packages |
		Where-Object name -eq "Runtime.One"
	$tamperedSpdxPackage.licenseDeclared = "GPL-3.0-only"
	$tamperedSpdxPackage.licenseConcluded = "GPL-3.0-only"
	Write-JsonFile $tamperedLicense.SbomPath $tamperedSbom
	$tamperedIndexPath = Join-Path `
		$tamperedLicense.PublishRoot `
		"licenses/runtime-packages.json"
	$tamperedIndex = Get-Content -Raw -LiteralPath $tamperedIndexPath |
		ConvertFrom-Json
	$tamperedIndexPackage = $tamperedIndex.packages |
		Where-Object name -eq "Runtime.One"
	$tamperedIndexPackage.license = "GPL-3.0-only"
	$tamperedIndexPackage.licenseEvidence = @("https://example.invalid/gpl")
	Write-JsonFile $tamperedIndexPath $tamperedIndex
	Update-FixtureSpdxFileChecksum `
		$tamperedLicense.SbomPath `
		$tamperedLicense.PublishRoot `
		"licenses/runtime-packages.json"
	Assert-Throws `
		-Action {
			Assert-PactSbom `
				-Path $tamperedLicense.SbomPath `
				-PublishRoot $tamperedLicense.PublishRoot `
				-ManifestPath $tamperedLicense.ManifestPath `
				-ProductName "PACT:> Mission Control" `
				-ProductVersion "0.1.0"
		} `
		-ExpectedText "Runtime package license does not match reviewed manifest: Runtime.One 1.2.3" `
		-Scenario "Coordinated SPDX and dependency-index license tampering"

	$unsafePath = New-SbomFixture (Join-Path $resolvedTemporaryRoot "unsafe-path")
	Complete-PactSbom `
		-Path $unsafePath.SbomPath `
		-PublishRoot $unsafePath.PublishRoot `
		-ManifestPath $unsafePath.ManifestPath `
		-ProductName "PACT:> Mission Control" `
		-ProductVersion "0.1.0" `
		-ProductLicense "MIT" `
		-ProductDownloadLocation "https://github.com/example/pact" | Out-Null
	$unsafeSbom = Get-Content -Raw -LiteralPath $unsafePath.SbomPath |
		ConvertFrom-Json
	$unsafeFile = $unsafeSbom.files |
		Where-Object fileName -eq "./licenses/xterm-LICENSE.txt"
	$unsafeFile.fileName = "../../licenses/xterm-LICENSE.txt"
	Write-JsonFile $unsafePath.SbomPath $unsafeSbom
	Assert-Throws `
		-Action {
			Assert-PactSbom `
				-Path $unsafePath.SbomPath `
				-PublishRoot $unsafePath.PublishRoot `
				-ManifestPath $unsafePath.ManifestPath `
				-ProductName "PACT:> Mission Control" `
				-ProductVersion "0.1.0"
		} `
		-ExpectedText "SPDX file inventory contains an unsafe path" `
		-Scenario "SPDX path traversal"

	$extraManifest = New-SbomFixture (
		Join-Path $resolvedTemporaryRoot "extra-manifest")
	Complete-PactSbom `
		-Path $extraManifest.SbomPath `
		-PublishRoot $extraManifest.PublishRoot `
		-ManifestPath $extraManifest.ManifestPath `
		-ProductName "PACT:> Mission Control" `
		-ProductVersion "0.1.0" `
		-ProductLicense "MIT" `
		-ProductDownloadLocation "https://github.com/example/pact" | Out-Null
	Write-TextFile `
		(Join-Path $extraManifest.PublishRoot "_manifest/extra.txt") `
		"unexpected manifest payload`n"
	Assert-Throws `
		-Action {
			Assert-PactSbom `
				-Path $extraManifest.SbomPath `
				-PublishRoot $extraManifest.PublishRoot `
				-ManifestPath $extraManifest.ManifestPath `
				-ProductName "PACT:> Mission Control" `
				-ProductVersion "0.1.0"
		} `
		-ExpectedText "SPDX file inventory does not exactly match the publish tree" `
		-Scenario "Untracked file below the SPDX manifest directory"

	$extraRelationship = New-SbomFixture (
		Join-Path $resolvedTemporaryRoot "extra-relationship")
	Complete-PactSbom `
		-Path $extraRelationship.SbomPath `
		-PublishRoot $extraRelationship.PublishRoot `
		-ManifestPath $extraRelationship.ManifestPath `
		-ProductName "PACT:> Mission Control" `
		-ProductVersion "0.1.0" `
		-ProductLicense "MIT" `
		-ProductDownloadLocation "https://github.com/example/pact" | Out-Null
	$extraRelationshipSbom = Get-Content `
		-Raw `
		-LiteralPath $extraRelationship.SbomPath |
		ConvertFrom-Json
	$extraRelationshipSbom.relationships += [pscustomobject]@{
		spdxElementId = "SPDXRef-RootPackage"
		relationshipType = "DEPENDS_ON"
		relatedSpdxElement = "SPDXRef-RootPackage"
	}
	Write-JsonFile $extraRelationship.SbomPath $extraRelationshipSbom
	Assert-Throws `
		-Action {
			Assert-PactSbom `
				-Path $extraRelationship.SbomPath `
				-PublishRoot $extraRelationship.PublishRoot `
				-ManifestPath $extraRelationship.ManifestPath `
				-ProductName "PACT:> Mission Control" `
				-ProductVersion "0.1.0"
		} `
		-ExpectedText "SPDX relationship set does not exactly match the normalized release" `
		-Scenario "Unexpected SPDX relationship"

	$unknownLicense = New-SbomFixture (
		Join-Path $resolvedTemporaryRoot "unknown-license")
	$unknownManifest = Get-Content `
		-Raw `
		-LiteralPath $unknownLicense.ManifestPath |
		ConvertFrom-Json
	$unknownManifest.licenseReferences | Add-Member `
		-NotePropertyName "Not-A-License" `
		-NotePropertyValue "https://example.invalid/license"
	Write-JsonFile $unknownLicense.ManifestPath $unknownManifest
	Update-FixtureSpdxFileChecksum `
		$unknownLicense.SbomPath `
		$unknownLicense.PublishRoot `
		"licenses/runtime-components.json"
	Assert-Throws `
		-Action {
			Complete-PactSbom `
				-Path $unknownLicense.SbomPath `
				-PublishRoot $unknownLicense.PublishRoot `
				-ManifestPath $unknownLicense.ManifestPath `
				-ProductName "PACT:> Mission Control" `
				-ProductVersion "0.1.0" `
				-ProductLicense "MIT" `
				-ProductDownloadLocation "https://github.com/example/pact"
		} `
		-ExpectedText "Unsupported SPDX license expression: Not-A-License" `
		-Scenario "Unknown license expression in component manifest"

	$dangling = Get-Content -Raw -LiteralPath $valid.SbomPath | ConvertFrom-Json
	$dangling.relationships += [pscustomobject]@{
		spdxElementId = "SPDXRef-RootPackage"
		relationshipType = "DEPENDS_ON"
		relatedSpdxElement = "SPDXRef-Package-Missing"
	}
	Write-JsonFile $valid.SbomPath $dangling
	Assert-Throws `
		-Action {
			Assert-PactSbom `
				-Path $valid.SbomPath `
				-PublishRoot $valid.PublishRoot `
				-ManifestPath $valid.ManifestPath `
				-ProductName "PACT:> Mission Control" `
				-ProductVersion "0.1.0"
		} `
		-ExpectedText "SPDX relationship references a missing element" `
		-Scenario "Dangling SPDX relationship"

	Write-Output "PASS: exact runtime SBOM fixtures."
}
finally
{
	if ([IO.Directory]::Exists($resolvedTemporaryRoot))
	{
		[IO.Directory]::Delete($resolvedTemporaryRoot, $true)
	}
}
