Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$script:PactSupportedLicenseExpressions = @(
	"BSD-3-Clause",
	"MIT",
	"MS-PL")

function Get-PactPropertyValue
{
	param(
		[Parameter(Mandatory)][object]$InputObject,
		[Parameter(Mandatory)][string]$Name
	)

	$property = $InputObject.PSObject.Properties[$Name]
	if ($null -eq $property)
	{
		return $null
	}

	return $property.Value
}

function Set-PactPropertyValue
{
	param(
		[Parameter(Mandatory)][object]$InputObject,
		[Parameter(Mandatory)][string]$Name,
		[AllowNull()][object]$Value
	)

	$property = $InputObject.PSObject.Properties[$Name]
	if ($null -eq $property)
	{
		$InputObject | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
		return
	}

	$property.Value = $Value
}

function Get-PactTupleKey
{
	param(
		[Parameter(Mandatory)][string]$Name,
		[Parameter(Mandatory)][string]$Version
	)

	return $Name.ToLowerInvariant() + "|" + $Version
}

function Split-PactDependencyKey
{
	param([Parameter(Mandatory)][string]$Key)

	$separator = $Key.LastIndexOf("/", [StringComparison]::Ordinal)
	if ($separator -le 0 -or $separator -eq $Key.Length - 1)
	{
		throw "Invalid dependency key in the published .deps.json: $Key"
	}

	return [pscustomobject]@{
		Name = $Key.Substring(0, $separator)
		Version = $Key.Substring($separator + 1)
	}
}

function Assert-PactHttpsUrl
{
	param(
		[Parameter(Mandatory)][string]$Value,
		[Parameter(Mandatory)][string]$Description
	)

	$uri = $null
	if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
		$uri.Scheme -ne "https")
	{
		throw "$Description must be an absolute HTTPS URL: $Value"
	}
}

function Assert-PactSupportedLicenseExpression
{
	param([Parameter(Mandatory)][string]$License)

	if ($License -cnotin $script:PactSupportedLicenseExpressions)
	{
		throw "Unsupported SPDX license expression: $License"
	}
}

function Get-PactNormalizedSpdxFileName
{
	param([Parameter(Mandatory)][string]$FileName)

	$normalized = $FileName.Replace("\", "/")
	if ($normalized.StartsWith("./", [StringComparison]::Ordinal))
	{
		$normalized = $normalized.Substring(2)
	}
	if ([string]::IsNullOrWhiteSpace($normalized) -or
		$normalized.StartsWith("/", [StringComparison]::Ordinal) -or
		$normalized -match "^[A-Za-z]:")
	{
		throw "SPDX file inventory contains an unsafe path: $FileName"
	}

	$segments = @($normalized.Split("/"))
	if ($segments.Contains("") -or
		$segments.Contains(".") -or
		$segments.Contains(".."))
	{
		throw "SPDX file inventory contains an unsafe path: $FileName"
	}

	return $normalized
}

function Resolve-PactEvidencePath
{
	param(
		[Parameter(Mandatory)][string]$PublishRoot,
		[Parameter(Mandatory)][string]$RelativePath
	)

	$normalized = $RelativePath.Replace("\", "/")
	if ([string]::IsNullOrWhiteSpace($normalized) -or
		$normalized.StartsWith("/", [StringComparison]::Ordinal) -or
		$normalized -match "^[A-Za-z]:" -or
		@($normalized.Split("/")).Contains(".."))
	{
		throw "License evidence path must be relative and safe: $RelativePath"
	}

	$resolvedRoot = [IO.Path]::GetFullPath($PublishRoot)
	$resolvedPath = [IO.Path]::GetFullPath((Join-Path $resolvedRoot $normalized))
	$rootPrefix = $resolvedRoot.TrimEnd(
		[IO.Path]::DirectorySeparatorChar,
		[IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
	if (-not $resolvedPath.StartsWith(
			$rootPrefix,
			[StringComparison]::OrdinalIgnoreCase))
	{
		throw "License evidence path escapes the publish root: $RelativePath"
	}
	if (-not [IO.File]::Exists($resolvedPath) -or
		([IO.FileInfo]::new($resolvedPath)).Length -eq 0)
	{
		throw "License evidence is missing or empty: $normalized"
	}

	return $normalized
}

function Read-PactComponentManifest
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][string]$PublishRoot
	)

	if (-not [IO.File]::Exists($Path))
	{
		throw "Runtime component manifest is missing: $Path"
	}

	$manifest = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
	if ([int](Get-PactPropertyValue $manifest "schemaVersion") -ne 1)
	{
		throw "Unsupported runtime component manifest schema version."
	}

	$referencePackages =
		[Collections.Generic.Dictionary[string, string]]::new(
			[StringComparer]::OrdinalIgnoreCase)
	foreach ($mapping in @(Get-PactPropertyValue $manifest "referencePackages"))
	{
		$assetName = [string](Get-PactPropertyValue $mapping "assetName")
		$packageName = [string](Get-PactPropertyValue $mapping "packageName")
		if ([string]::IsNullOrWhiteSpace($assetName) -or
			[string]::IsNullOrWhiteSpace($packageName) -or
			-not $referencePackages.TryAdd($assetName, $packageName))
		{
			throw "Runtime component manifest contains an invalid or duplicate reference mapping."
		}
	}

	$licenseReferences =
		[Collections.Generic.Dictionary[string, string]]::new(
			[StringComparer]::Ordinal)
	$licenseReferenceObject = Get-PactPropertyValue $manifest "licenseReferences"
	if ($null -eq $licenseReferenceObject)
	{
		throw "Runtime component manifest must define licenseReferences."
	}
	foreach ($property in $licenseReferenceObject.PSObject.Properties)
	{
		$expression = [string]$property.Name
		$url = [string]$property.Value
		Assert-PactSupportedLicenseExpression $expression
		Assert-PactHttpsUrl $url "License reference for $expression"
		if ([string]::IsNullOrWhiteSpace($expression) -or
			-not $licenseReferences.TryAdd($expression, $url))
		{
			throw "Runtime component manifest contains an invalid license reference."
		}
	}

	$packageOverrides =
		[Collections.Generic.Dictionary[string, object]]::new(
			[StringComparer]::OrdinalIgnoreCase)
	foreach ($override in @(Get-PactPropertyValue $manifest "packageOverrides"))
	{
		$packageName = [string](Get-PactPropertyValue $override "packageName")
		$version = [string](Get-PactPropertyValue $override "version")
		$license = [string](Get-PactPropertyValue $override "license")
		Assert-PactSupportedLicenseExpression $license
		$licenseFiles = @(
			Get-PactPropertyValue $override "licenseFiles" |
				ForEach-Object {
					Resolve-PactEvidencePath $PublishRoot ([string]$_)
				})
		if ([string]::IsNullOrWhiteSpace($packageName) -or
			[string]::IsNullOrWhiteSpace($version) -or
			[string]::IsNullOrWhiteSpace($license) -or
			$license -eq "NOASSERTION" -or
			$licenseFiles.Count -eq 0)
		{
			throw "Runtime component manifest contains an incomplete package override."
		}

		$key = Get-PactTupleKey $packageName $version
		if (-not $packageOverrides.TryAdd(
				$key,
				[pscustomobject]@{
					Name = $packageName
					Version = $version
					License = $license
					LicenseEvidence = $licenseFiles
				}))
		{
			throw "Duplicate package override: $packageName $version"
		}
	}

	$vendoredPackages = [Collections.Generic.List[object]]::new()
	$vendorKeys = [Collections.Generic.HashSet[string]]::new(
		[StringComparer]::OrdinalIgnoreCase)
	foreach ($vendor in @(Get-PactPropertyValue $manifest "vendoredPackages"))
	{
		$name = [string](Get-PactPropertyValue $vendor "name")
		$version = [string](Get-PactPropertyValue $vendor "version")
		$license = [string](Get-PactPropertyValue $vendor "license")
		Assert-PactSupportedLicenseExpression $license
		$supplier = [string](Get-PactPropertyValue $vendor "supplier")
		$downloadLocation = [string](
			Get-PactPropertyValue $vendor "downloadLocation")
		$purl = [string](Get-PactPropertyValue $vendor "purl")
		$licenseFiles = @(
			Get-PactPropertyValue $vendor "licenseFiles" |
				ForEach-Object {
					Resolve-PactEvidencePath $PublishRoot ([string]$_)
				})
		Assert-PactHttpsUrl $downloadLocation "Download location for $name"
		$key = Get-PactTupleKey $name $version
		if ([string]::IsNullOrWhiteSpace($name) -or
			[string]::IsNullOrWhiteSpace($version) -or
			[string]::IsNullOrWhiteSpace($license) -or
			$license -eq "NOASSERTION" -or
			[string]::IsNullOrWhiteSpace($supplier) -or
			$licenseFiles.Count -eq 0 -or
			-not $vendorKeys.Add($key))
		{
			throw "Runtime component manifest contains an incomplete or duplicate vendored package."
		}

		$vendoredPackages.Add([pscustomobject]@{
			Name = $name
			Version = $version
			Origin = "vendored"
			License = $license
			LicenseEvidence = $licenseFiles
			Supplier = $supplier
			DownloadLocation = $downloadLocation
			Purl = $purl
		})
	}

	return [pscustomobject]@{
		ReferencePackages = $referencePackages
		LicenseReferences = $licenseReferences
		PackageOverrides = $packageOverrides
		VendoredPackages = @($vendoredPackages)
	}
}

function Test-PactRuntimeAsset
{
	param([Parameter(Mandatory)][object]$TargetEntry)

	foreach ($propertyName in @("runtime", "native", "resources", "runtimeTargets"))
	{
		if ($null -ne (Get-PactPropertyValue $TargetEntry $propertyName))
		{
			return $true
		}
	}

	return $false
}

function Get-PactRuntimePackages
{
	param(
		[Parameter(Mandatory)][string]$PublishRoot,
		[Parameter(Mandatory)][object]$ComponentManifest
	)

	$depsFiles = @(Get-ChildItem -LiteralPath $PublishRoot -File -Filter "*.deps.json")
	if ($depsFiles.Count -ne 1)
	{
		throw "Publish root must contain exactly one .deps.json file."
	}

	$deps = Get-Content -Raw -LiteralPath $depsFiles[0].FullName |
		ConvertFrom-Json
	$runtimeTarget = Get-PactPropertyValue `
		(Get-PactPropertyValue $deps "runtimeTarget") `
		"name"
	if ([string]::IsNullOrWhiteSpace([string]$runtimeTarget) -or
		-not ([string]$runtimeTarget).EndsWith(
			"/win-x64",
			[StringComparison]::OrdinalIgnoreCase))
	{
		throw "Published .deps.json does not target win-x64: $runtimeTarget"
	}

	$targets = Get-PactPropertyValue $deps "targets"
	$target = Get-PactPropertyValue $targets ([string]$runtimeTarget)
	$libraries = Get-PactPropertyValue $deps "libraries"
	if ($null -eq $target -or $null -eq $libraries)
	{
		throw "Published .deps.json is missing its selected target or libraries."
	}

	$packages = [Collections.Generic.List[object]]::new()
	$seen = [Collections.Generic.HashSet[string]]::new(
		[StringComparer]::OrdinalIgnoreCase)
	foreach ($property in $target.PSObject.Properties)
	{
		if (-not (Test-PactRuntimeAsset $property.Value))
		{
			continue
		}

		$dependency = Split-PactDependencyKey $property.Name
		$library = Get-PactPropertyValue $libraries $property.Name
		if ($null -ne $library)
		{
			$type = [string](Get-PactPropertyValue $library "type")
			if ($type -eq "project")
			{
				continue
			}
			if ($type -eq "package")
			{
				$packageName = $dependency.Name
			}
			elseif ($type -eq "reference")
			{
				$packageName = $null
				if (-not $ComponentManifest.ReferencePackages.TryGetValue(
						$dependency.Name,
						[ref]$packageName))
				{
					throw "Runtime reference has no owning package mapping: $($property.Name)"
				}
			}
			else
			{
				throw "Unsupported runtime library type '$type' for $($property.Name)."
			}
		}
		else
		{
			$packageName = $null
			if (-not $ComponentManifest.ReferencePackages.TryGetValue(
					$dependency.Name,
					[ref]$packageName))
			{
				throw "Runtime reference has no owning package mapping: $($property.Name)"
			}
		}

		$key = Get-PactTupleKey $packageName $dependency.Version
		if (-not $seen.Add($key))
		{
			throw "Duplicate runtime package tuple: $packageName $($dependency.Version)"
		}
		$packages.Add([pscustomobject]@{
			Name = $packageName
			Version = $dependency.Version
			Origin = "nuget"
		})
	}

	return @($packages | Sort-Object Name, Version)
}

function Get-PactNuGetDownloadLocation
{
	param(
		[Parameter(Mandatory)][string]$Name,
		[Parameter(Mandatory)][string]$Version
	)

	$lowerName = $Name.ToLowerInvariant()
	$lowerVersion = $Version.ToLowerInvariant()
	return "https://api.nuget.org/v3-flatcontainer/$lowerName/$lowerVersion/$lowerName.$lowerVersion.nupkg"
}

function Get-PactNuGetPurl
{
	param(
		[Parameter(Mandatory)][string]$Name,
		[Parameter(Mandatory)][string]$Version
	)

	return "pkg:nuget/$([Uri]::EscapeDataString($Name))@$([Uri]::EscapeDataString($Version))"
}

function Get-PactStableSpdxId
{
	param(
		[Parameter(Mandatory)][string]$Prefix,
		[Parameter(Mandatory)][string]$Value
	)

	$bytes = [Text.Encoding]::UTF8.GetBytes($Value)
	$hash = [Convert]::ToHexString(
		[Security.Cryptography.SHA256]::HashData($bytes))
	return "SPDXRef-$Prefix-$hash"
}

function Get-PactLicenseDecision
{
	param(
		[Parameter(Mandatory)][object]$RuntimePackage,
		[Parameter(Mandatory)][object]$SpdxPackage,
		[Parameter(Mandatory)][object]$ComponentManifest
	)

	$key = Get-PactTupleKey $RuntimePackage.Name $RuntimePackage.Version
	$override = $null
	if ($ComponentManifest.PackageOverrides.TryGetValue($key, [ref]$override))
	{
		$detected = [string](Get-PactPropertyValue $SpdxPackage "licenseDeclared")
		if (-not [string]::IsNullOrWhiteSpace($detected) -and
			$detected -ne "NOASSERTION" -and
			$detected -ne $override.License)
		{
			throw "Detected license for $($RuntimePackage.Name) $($RuntimePackage.Version) is $detected, but the reviewed override is $($override.License)."
		}

		return [pscustomobject]@{
			License = $override.License
			Evidence = @($override.LicenseEvidence)
		}
	}

	$license = [string](Get-PactPropertyValue $SpdxPackage "licenseDeclared")
	$licenseUrl = $null
	if ([string]::IsNullOrWhiteSpace($license) -or
		$license -eq "NOASSERTION" -or
		-not $ComponentManifest.LicenseReferences.TryGetValue(
			$license,
			[ref]$licenseUrl))
	{
		throw "Runtime package has no reviewed license: $($RuntimePackage.Name) $($RuntimePackage.Version)"
	}

	return [pscustomobject]@{
		License = $license
		Evidence = @($licenseUrl)
	}
}

function New-PactRuntimeIndex
{
	param(
		[Parameter(Mandatory)][object[]]$Packages,
		[Parameter(Mandatory)][string]$DepsFileName
	)

	return [ordered]@{
		schemaVersion = 1
		generatedFrom = $DepsFileName
		packages = @(
			$Packages |
				Sort-Object Name, Version |
				ForEach-Object {
					[ordered]@{
						name = $_.Name
						version = $_.Version
						origin = $_.Origin
						license = $_.License
						licenseEvidence = @($_.LicenseEvidence)
						downloadLocation = $_.DownloadLocation
						purl = $_.Purl
					}
				})
	}
}

function Add-PactIndexFileToSbom
{
	param(
		[Parameter(Mandatory)][object]$Sbom,
		[Parameter(Mandatory)][string]$IndexPath
	)

	$relativePath = "licenses/runtime-packages.json"
	$files = @(
		@(Get-PactPropertyValue $Sbom "files") |
			Where-Object {
				$normalizedName = Get-PactNormalizedSpdxFileName `
					-FileName ([string](Get-PactPropertyValue $_ "fileName"))
				$normalizedName -ne $relativePath
			})
	$bytes = [IO.File]::ReadAllBytes($IndexPath)
	$sha256 = [Convert]::ToHexString(
		[Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
	$sha1 = [Convert]::ToHexString(
		[Security.Cryptography.SHA1]::HashData($bytes)).ToLowerInvariant()
	$indexFile = [pscustomobject][ordered]@{
		fileName = "./$relativePath"
		SPDXID = Get-PactStableSpdxId "File" $relativePath
		checksums = @(
			[ordered]@{ algorithm = "SHA256"; checksumValue = $sha256 },
			[ordered]@{ algorithm = "SHA1"; checksumValue = $sha1 })
		licenseConcluded = "NOASSERTION"
		licenseInfoInFiles = @("NOASSERTION")
		copyrightText = "NOASSERTION"
	}
	Set-PactPropertyValue $Sbom "files" (@($files) + $indexFile)
	return $indexFile
}

function Write-PactJson
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][object]$Value
	)

	[IO.File]::WriteAllText(
		$Path,
		(($Value | ConvertTo-Json -Depth 100) + "`n"),
		[Text.UTF8Encoding]::new($false))
}

function Write-PactSbomChecksumSidecar
{
	param([Parameter(Mandatory)][string]$Path)

	$sha256 = [Convert]::ToHexString(
		[Security.Cryptography.SHA256]::HashData(
			[IO.File]::ReadAllBytes($Path))).ToLowerInvariant()
	[IO.File]::WriteAllText(
		"$Path.sha256",
		$sha256,
		[Text.UTF8Encoding]::new($false))
}

function Complete-PactSbom
{
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][string]$PublishRoot,
		[Parameter(Mandatory)][string]$ManifestPath,
		[Parameter(Mandatory)][string]$ProductName,
		[Parameter(Mandatory)][string]$ProductVersion,
		[Parameter(Mandatory)][string]$ProductLicense,
		[Parameter(Mandatory)][string]$ProductDownloadLocation
	)

	Assert-PactSupportedLicenseExpression $ProductLicense
	Assert-PactHttpsUrl $ProductDownloadLocation "Product download location"
	$componentManifest = Read-PactComponentManifest $ManifestPath $PublishRoot
	$runtimePackages = @(
		Get-PactRuntimePackages $PublishRoot $componentManifest)
	$sbom = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
	if ([string](Get-PactPropertyValue $sbom "spdxVersion") -ne "SPDX-2.2")
	{
		throw "Unexpected SPDX version."
	}

	$rawPackages = @(Get-PactPropertyValue $sbom "packages")
	$productMatches = @(
		$rawPackages |
			Where-Object {
				[string](Get-PactPropertyValue $_ "name") -eq $ProductName -and
				[string](Get-PactPropertyValue $_ "versionInfo") -eq $ProductVersion
			})
	if ($productMatches.Count -ne 1)
	{
		throw "Generated SBOM must contain exactly one product package."
	}

	$product = $productMatches[0]
	Set-PactPropertyValue $product "licenseDeclared" $ProductLicense
	Set-PactPropertyValue $product "licenseConcluded" $ProductLicense
	Set-PactPropertyValue $product "downloadLocation" $ProductDownloadLocation
	Set-PactPropertyValue $product "licenseComments" "Project license: ./LICENSE"

	$normalizedPackages = [Collections.Generic.List[object]]::new()
	$normalizedPackages.Add($product)
	$indexPackages = [Collections.Generic.List[object]]::new()
	foreach ($runtimePackage in $runtimePackages)
	{
		$matches = @(
			$rawPackages |
				Where-Object {
					[string](Get-PactPropertyValue $_ "name") -eq $runtimePackage.Name -and
					[string](Get-PactPropertyValue $_ "versionInfo") -eq $runtimePackage.Version
				})
		if ($matches.Count -eq 0)
		{
			throw "Runtime package is missing from the generated SBOM: $($runtimePackage.Name) $($runtimePackage.Version)"
		}
		if ($matches.Count -ne 1)
		{
			throw "Generated SBOM contains duplicate runtime package: $($runtimePackage.Name) $($runtimePackage.Version)"
		}

		$package = $matches[0]
		$licenseDecision = Get-PactLicenseDecision `
			$runtimePackage `
			$package `
			$componentManifest
		$downloadLocation = Get-PactNuGetDownloadLocation `
			$runtimePackage.Name `
			$runtimePackage.Version
		$purl = Get-PactNuGetPurl $runtimePackage.Name $runtimePackage.Version
		Set-PactPropertyValue $package "licenseDeclared" $licenseDecision.License
		Set-PactPropertyValue $package "licenseConcluded" $licenseDecision.License
		Set-PactPropertyValue $package "downloadLocation" $downloadLocation
		Set-PactPropertyValue `
			$package `
			"licenseComments" `
			("License evidence: " + ($licenseDecision.Evidence -join ", "))
		Set-PactPropertyValue $package "externalRefs" @(
			[ordered]@{
				referenceCategory = "PACKAGE-MANAGER"
				referenceType = "purl"
				referenceLocator = $purl
			})
		$normalizedPackages.Add($package)
		$indexPackages.Add([pscustomobject]@{
			Name = $runtimePackage.Name
			Version = $runtimePackage.Version
			Origin = "nuget"
			License = $licenseDecision.License
			LicenseEvidence = @($licenseDecision.Evidence)
			DownloadLocation = $downloadLocation
			Purl = $purl
		})
	}

	foreach ($vendor in $componentManifest.VendoredPackages)
	{
		$spdxId = Get-PactStableSpdxId `
			"Package" `
			("vendored|" + (Get-PactTupleKey $vendor.Name $vendor.Version))
		$externalRefs = @()
		if (-not [string]::IsNullOrWhiteSpace($vendor.Purl))
		{
			$externalRefs = @([ordered]@{
				referenceCategory = "PACKAGE-MANAGER"
				referenceType = "purl"
				referenceLocator = $vendor.Purl
			})
		}
		$normalizedPackages.Add([pscustomobject][ordered]@{
			name = $vendor.Name
			SPDXID = $spdxId
			downloadLocation = $vendor.DownloadLocation
			filesAnalyzed = $false
			licenseConcluded = $vendor.License
			licenseDeclared = $vendor.License
			licenseComments = "License evidence: $($vendor.LicenseEvidence -join ', ')"
			copyrightText = "NOASSERTION"
			versionInfo = $vendor.Version
			externalRefs = $externalRefs
			supplier = $vendor.Supplier
		})
		$indexPackages.Add($vendor)
	}

	$sortedThirdPartyPackages = @(
		$normalizedPackages |
			Where-Object { $_ -ne $product } |
			Sort-Object name, versionInfo)
	Set-PactPropertyValue $sbom "packages" (@($product) + $sortedThirdPartyPackages)

	$depsFile = Get-ChildItem -LiteralPath $PublishRoot -File -Filter "*.deps.json"
	$index = New-PactRuntimeIndex @($indexPackages) $depsFile.Name
	$indexPath = Join-Path $PublishRoot "licenses/runtime-packages.json"
	Write-PactJson $indexPath $index
	$null = Add-PactIndexFileToSbom $sbom $indexPath

	$relationships = [Collections.Generic.List[object]]::new()
	$productSpdxId = [string](Get-PactPropertyValue $product "SPDXID")
	$relationships.Add([pscustomobject][ordered]@{
		spdxElementId = "SPDXRef-DOCUMENT"
		relationshipType = "DESCRIBES"
		relatedSpdxElement = $productSpdxId
	})
	foreach ($package in $sortedThirdPartyPackages)
	{
		$relationships.Add([pscustomobject][ordered]@{
			spdxElementId = $productSpdxId
			relationshipType = "DEPENDS_ON"
			relatedSpdxElement = [string](
				Get-PactPropertyValue $package "SPDXID")
		})
	}
	foreach ($file in @(
			@(Get-PactPropertyValue $sbom "files") |
				Sort-Object {
					Get-PactNormalizedSpdxFileName `
						([string](Get-PactPropertyValue $_ "fileName"))
				}))
	{
		$relationships.Add([pscustomobject][ordered]@{
			spdxElementId = $productSpdxId
			relationshipType = "CONTAINS"
			relatedSpdxElement = [string](Get-PactPropertyValue $file "SPDXID")
		})
	}
	Set-PactPropertyValue $sbom "relationships" @($relationships)
	Set-PactPropertyValue $sbom "documentDescribes" @($productSpdxId)

	Write-PactJson $Path $sbom
	Write-PactSbomChecksumSidecar $Path
	Assert-PactSbom `
		-Path $Path `
		-PublishRoot $PublishRoot `
		-ManifestPath $ManifestPath `
		-ProductName $ProductName `
		-ProductVersion $ProductVersion `
		-ProductLicense $ProductLicense
}

function Assert-PactSbom
{
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][string]$PublishRoot,
		[Parameter(Mandatory)][string]$ManifestPath,
		[Parameter(Mandatory)][string]$ProductName,
		[Parameter(Mandatory)][string]$ProductVersion,
		[string]$ProductLicense = "MIT"
	)

	Assert-PactSupportedLicenseExpression $ProductLicense
	$componentManifest = Read-PactComponentManifest $ManifestPath $PublishRoot
	$runtimePackages = @(
		Get-PactRuntimePackages $PublishRoot $componentManifest)
	$sbom = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
	if ([string](Get-PactPropertyValue $sbom "spdxVersion") -ne "SPDX-2.2")
	{
		throw "Unexpected SPDX version."
	}

	$expectedTuples = @(
		"$ProductName|$ProductVersion"
		$runtimePackages | ForEach-Object { "$($_.Name)|$($_.Version)" }
		$componentManifest.VendoredPackages |
			ForEach-Object { "$($_.Name)|$($_.Version)" }) | Sort-Object
	$packages = @(Get-PactPropertyValue $sbom "packages")
	$actualTuples = @(
		$packages |
			ForEach-Object {
				"$([string](Get-PactPropertyValue $_ 'name'))|$([string](Get-PactPropertyValue $_ 'versionInfo'))"
			} |
			Sort-Object)
	if ([string]::Join("`n", $actualTuples) -cne
		[string]::Join("`n", $expectedTuples))
	{
		throw "SPDX package set does not exactly match the published runtime package set."
	}

	foreach ($package in $packages)
	{
		$name = [string](Get-PactPropertyValue $package "name")
		$version = [string](Get-PactPropertyValue $package "versionInfo")
		$declared = [string](Get-PactPropertyValue $package "licenseDeclared")
		$concluded = [string](Get-PactPropertyValue $package "licenseConcluded")
		$downloadLocation = [string](
			Get-PactPropertyValue $package "downloadLocation")
		if ([string]::IsNullOrWhiteSpace($declared) -or
			$declared -eq "NOASSERTION" -or
			$concluded -ne $declared)
		{
			throw "SPDX package has an unresolved license: $name $version"
		}
		if ([string]::IsNullOrWhiteSpace($downloadLocation) -or
			$downloadLocation -eq "NOASSERTION")
		{
			throw "SPDX package has no download location: $name $version"
		}
		if ($name -eq $ProductName -and
			$version -eq $ProductVersion -and
			$declared -ne $ProductLicense)
		{
			throw "SPDX product license does not match the repository license."
		}
	}

	$indexPath = Join-Path $PublishRoot "licenses/runtime-packages.json"
	if (-not [IO.File]::Exists($indexPath))
	{
		throw "Runtime package index is missing."
	}
	$index = Get-Content -Raw -LiteralPath $indexPath | ConvertFrom-Json
	if ([int](Get-PactPropertyValue $index "schemaVersion") -ne 1)
	{
		throw "Unsupported runtime package index schema version."
	}
	$indexPackages = @(Get-PactPropertyValue $index "packages")
	$expectedThirdPartyTuples = @(
		$runtimePackages | ForEach-Object { "$($_.Name)|$($_.Version)" }
		$componentManifest.VendoredPackages |
			ForEach-Object { "$($_.Name)|$($_.Version)" }) | Sort-Object
	$actualIndexTuples = @(
		$indexPackages |
			ForEach-Object {
				"$([string](Get-PactPropertyValue $_ 'name'))|$([string](Get-PactPropertyValue $_ 'version'))"
			} |
			Sort-Object)
	if ([string]::Join("`n", $actualIndexTuples) -cne
		[string]::Join("`n", $expectedThirdPartyTuples))
	{
		throw "Runtime package index does not exactly match the published runtime package set."
	}
	$runtimeByKey =
		[Collections.Generic.Dictionary[string, object]]::new(
			[StringComparer]::OrdinalIgnoreCase)
	foreach ($runtimePackage in $runtimePackages)
	{
		$runtimeByKey.Add(
			(Get-PactTupleKey $runtimePackage.Name $runtimePackage.Version),
			$runtimePackage)
	}
	$vendorByKey =
		[Collections.Generic.Dictionary[string, object]]::new(
			[StringComparer]::OrdinalIgnoreCase)
	foreach ($vendor in $componentManifest.VendoredPackages)
	{
		$vendorByKey.Add(
			(Get-PactTupleKey $vendor.Name $vendor.Version),
			$vendor)
	}

	foreach ($indexPackage in $indexPackages)
	{
		$name = [string](Get-PactPropertyValue $indexPackage "name")
		$version = [string](Get-PactPropertyValue $indexPackage "version")
		$key = Get-PactTupleKey $name $version
		$runtimePackage = $null
		$vendor = $null
		if ($runtimeByKey.TryGetValue($key, [ref]$runtimePackage))
		{
			$expectedOrigin = "nuget"
			$expectedDownloadLocation = Get-PactNuGetDownloadLocation $name $version
			$expectedPurl = Get-PactNuGetPurl $name $version
			$override = $null
			if ($componentManifest.PackageOverrides.TryGetValue(
					$key,
					[ref]$override))
			{
				$expectedLicense = $override.License
				$expectedEvidence = @($override.LicenseEvidence)
			}
			else
			{
				$expectedLicense = [string](
					Get-PactPropertyValue $indexPackage "license")
				Assert-PactSupportedLicenseExpression $expectedLicense
				$licenseUrl = $null
				if (-not $componentManifest.LicenseReferences.TryGetValue(
						$expectedLicense,
						[ref]$licenseUrl))
				{
					throw "Runtime package license does not match reviewed manifest: $name $version"
				}
				$expectedEvidence = @($licenseUrl)
			}
			$expectedSupplier = $null
		}
		elseif ($vendorByKey.TryGetValue($key, [ref]$vendor))
		{
			$expectedOrigin = "vendored"
			$expectedLicense = $vendor.License
			$expectedEvidence = @($vendor.LicenseEvidence)
			$expectedDownloadLocation = $vendor.DownloadLocation
			$expectedPurl = $vendor.Purl
			$expectedSupplier = $vendor.Supplier
		}
		else
		{
			throw "Runtime package index contains an unexpected package: $name $version"
		}

		$license = [string](Get-PactPropertyValue $indexPackage "license")
		$evidence = @(Get-PactPropertyValue $indexPackage "licenseEvidence")
		$origin = [string](Get-PactPropertyValue $indexPackage "origin")
		$downloadLocation = [string](
			Get-PactPropertyValue $indexPackage "downloadLocation")
		$purl = [string](Get-PactPropertyValue $indexPackage "purl")
		if ($license -ne $expectedLicense -or
			$origin -ne $expectedOrigin -or
			$downloadLocation -ne $expectedDownloadLocation -or
			$purl -ne $expectedPurl -or
			[string]::Join("`n", $evidence) -cne
				[string]::Join("`n", $expectedEvidence))
		{
			throw "Runtime package license does not match reviewed manifest: $name $version"
		}

		$spdxPackage = @(
			$packages |
				Where-Object {
					[string](Get-PactPropertyValue $_ "name") -eq $name -and
					[string](Get-PactPropertyValue $_ "versionInfo") -eq $version
				})
		if ($spdxPackage.Count -ne 1)
		{
			throw "Runtime package index package does not match SPDX: $name $version"
		}
		$spdxDeclared = [string](
			Get-PactPropertyValue $spdxPackage[0] "licenseDeclared")
		$spdxConcluded = [string](
			Get-PactPropertyValue $spdxPackage[0] "licenseConcluded")
		$spdxDownloadLocation = [string](
			Get-PactPropertyValue $spdxPackage[0] "downloadLocation")
		$spdxPurls = @(
			@(Get-PactPropertyValue $spdxPackage[0] "externalRefs") |
				Where-Object {
					[string](Get-PactPropertyValue $_ "referenceType") -eq "purl"
				} |
				ForEach-Object {
					[string](Get-PactPropertyValue $_ "referenceLocator")
				})
		$expectedPurls = if ([string]::IsNullOrWhiteSpace($expectedPurl))
		{
			@()
		}
		else
		{
			@($expectedPurl)
		}
		if ($spdxDeclared -ne $expectedLicense -or
			$spdxConcluded -ne $expectedLicense -or
			$spdxDownloadLocation -ne $expectedDownloadLocation -or
			[string]::Join("`n", [string[]]@($spdxPurls)) -cne
				[string]::Join("`n", [string[]]@($expectedPurls)))
		{
			throw "Runtime package SPDX claim does not match reviewed manifest: $name $version"
		}
		if ($null -ne $expectedSupplier -and
			[string](Get-PactPropertyValue $spdxPackage[0] "supplier") -ne
				$expectedSupplier)
		{
			throw "Vendored package supplier does not match reviewed manifest: $name $version"
		}
		foreach ($entry in $evidence)
		{
			$value = [string]$entry
			if ($value.StartsWith("https://", [StringComparison]::OrdinalIgnoreCase))
			{
				Assert-PactHttpsUrl $value "License evidence for $name"
			}
			else
			{
				$null = Resolve-PactEvidencePath $PublishRoot $value
			}
		}
	}

	$fileIds = [Collections.Generic.HashSet[string]]::new(
		[StringComparer]::Ordinal)
	$manifestFiles =
		[Collections.Generic.Dictionary[string, object]]::new(
			[StringComparer]::OrdinalIgnoreCase)
	$indexFileCount = 0
	foreach ($file in @(Get-PactPropertyValue $sbom "files"))
	{
		$fileId = [string](Get-PactPropertyValue $file "SPDXID")
		if ([string]::IsNullOrWhiteSpace($fileId) -or
			-not $fileIds.Add($fileId))
		{
			throw "Duplicate or empty SPDX element identifier: $fileId"
		}
		$fileName = Get-PactNormalizedSpdxFileName `
			-FileName ([string](Get-PactPropertyValue $file "fileName"))
		if (-not $manifestFiles.TryAdd($fileName, $file))
		{
			throw "SPDX file inventory contains a duplicate path: $fileName"
		}

		$filePath = Join-Path $PublishRoot $fileName
		if (-not [IO.File]::Exists($filePath))
		{
			throw "SPDX file inventory references a missing file: $fileName"
		}
		$sha256Entries = @(
			@(Get-PactPropertyValue $file "checksums") |
				Where-Object {
					[string](Get-PactPropertyValue $_ "algorithm") -eq "SHA256"
				})
		if ($sha256Entries.Count -ne 1)
		{
			throw "SPDX file inventory must contain one SHA256 checksum: $fileName"
		}
		$actualHash = [Convert]::ToHexString(
			[Security.Cryptography.SHA256]::HashData(
				[IO.File]::ReadAllBytes($filePath))).ToLowerInvariant()
		$expectedHash = [string](
			Get-PactPropertyValue $sha256Entries[0] "checksumValue")
		if ($actualHash -cne $expectedHash.ToLowerInvariant())
		{
			throw "SPDX file checksum mismatch: $fileName"
		}
		if ($fileName -eq "licenses/runtime-packages.json")
		{
			$indexFileCount++
		}
	}
	if ($indexFileCount -ne 1)
	{
		throw "SPDX file inventory must contain exactly one runtime package index."
	}
	$actualFiles = @(
		Get-ChildItem -LiteralPath $PublishRoot -Recurse -File |
			ForEach-Object {
				[IO.Path]::GetRelativePath($PublishRoot, $_.FullName).Replace("\", "/")
			} |
			Where-Object {
				$_ -cnotin @(
					"_manifest/spdx_2.2/manifest.spdx.json",
					"_manifest/spdx_2.2/manifest.spdx.json.sha256")
			} |
			Sort-Object)
	$manifestFileNames = @($manifestFiles.Keys | Sort-Object)
	if ([string]::Join("`n", $actualFiles) -cne
		[string]::Join("`n", $manifestFileNames))
	{
		throw "SPDX file inventory does not exactly match the publish tree."
	}

	$allowedElements = [Collections.Generic.HashSet[string]]::new(
		[StringComparer]::Ordinal)
	$null = $allowedElements.Add("SPDXRef-DOCUMENT")
	foreach ($package in $packages)
	{
		$packageId = [string](Get-PactPropertyValue $package "SPDXID")
		if ([string]::IsNullOrWhiteSpace($packageId) -or
			-not $allowedElements.Add($packageId))
		{
			throw "Duplicate or empty SPDX element identifier: $packageId"
		}
	}
	foreach ($fileId in $fileIds)
	{
		if (-not $allowedElements.Add($fileId))
		{
			throw "Duplicate or empty SPDX element identifier: $fileId"
		}
	}

	$product = @(
		$packages |
			Where-Object {
				[string](Get-PactPropertyValue $_ "name") -eq $ProductName -and
				[string](Get-PactPropertyValue $_ "versionInfo") -eq $ProductVersion
			})[0]
	$productSpdxId = [string](Get-PactPropertyValue $product "SPDXID")
	$documentDescribes = @(Get-PactPropertyValue $sbom "documentDescribes")
	if ($documentDescribes.Count -ne 1 -or
		[string]$documentDescribes[0] -ne $productSpdxId)
	{
		throw "SPDX documentDescribes must contain only the product package."
	}

	$actualRelationshipKeys = [Collections.Generic.List[string]]::new()
	$seenRelationshipKeys = [Collections.Generic.HashSet[string]]::new(
		[StringComparer]::Ordinal)
	foreach ($relationship in @(Get-PactPropertyValue $sbom "relationships"))
	{
		$source = [string](Get-PactPropertyValue $relationship "spdxElementId")
		$target = [string](Get-PactPropertyValue $relationship "relatedSpdxElement")
		$type = [string](Get-PactPropertyValue $relationship "relationshipType")
		if (-not $allowedElements.Contains($source) -or
			-not $allowedElements.Contains($target))
		{
			throw "SPDX relationship references a missing element: $source -> $target"
		}
		$key = "$source|$type|$target"
		if ([string]::IsNullOrWhiteSpace($type) -or
			-not $seenRelationshipKeys.Add($key))
		{
			throw "SPDX relationship set contains an empty or duplicate relationship."
		}
		$actualRelationshipKeys.Add($key)
	}

	$expectedRelationshipKeys = [Collections.Generic.List[string]]::new()
	$expectedRelationshipKeys.Add(
		"SPDXRef-DOCUMENT|DESCRIBES|$productSpdxId")
	foreach ($package in $packages | Where-Object { $_ -ne $product })
	{
		$id = [string](Get-PactPropertyValue $package "SPDXID")
		$expectedRelationshipKeys.Add("$productSpdxId|DEPENDS_ON|$id")
	}
	foreach ($fileId in $fileIds)
	{
		$expectedRelationshipKeys.Add("$productSpdxId|CONTAINS|$fileId")
	}
	$actualRelationshipKeys.Sort([StringComparer]::Ordinal)
	$expectedRelationshipKeys.Sort([StringComparer]::Ordinal)
	if ([string]::Join("`n", $actualRelationshipKeys) -cne
		[string]::Join("`n", $expectedRelationshipKeys))
	{
		throw "SPDX relationship set does not exactly match the normalized release."
	}

	$sidecarPath = "$Path.sha256"
	if (-not [IO.File]::Exists($sidecarPath))
	{
		throw "SPDX manifest checksum sidecar is missing."
	}
	$actualSbomHash = [Convert]::ToHexString(
		[Security.Cryptography.SHA256]::HashData(
			[IO.File]::ReadAllBytes($Path))).ToLowerInvariant()
	$sidecarHash = [IO.File]::ReadAllText($sidecarPath).Trim()
	if ($sidecarHash -cne $actualSbomHash)
	{
		throw "SPDX manifest checksum sidecar does not match the manifest."
	}

	return [pscustomobject]@{
		RuntimePackageCount = $runtimePackages.Count
		VendoredPackageCount = $componentManifest.VendoredPackages.Count
		PackageCount = $packages.Count
	}
}

Export-ModuleMember -Function Complete-PactSbom, Assert-PactSbom
