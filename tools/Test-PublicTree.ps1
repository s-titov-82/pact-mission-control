[CmdletBinding()]
param(
	[string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepositoryRoot
{
	param([string]$RequestedRoot)

	if (-not [string]::IsNullOrWhiteSpace($RequestedRoot))
	{
		return [IO.Path]::GetFullPath($RequestedRoot)
	}

	$root = & git rev-parse --show-toplevel
	if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root))
	{
		throw "Could not resolve the Git repository root."
	}

	return [IO.Path]::GetFullPath($root.Trim())
}

function Get-ExportIgnoreEvidence
{
	param(
		[Parameter(Mandatory)][string]$Root,
		[Parameter(Mandatory)][string[]]$Files
	)

	$startInfo = [Diagnostics.ProcessStartInfo]::new()
	$startInfo.FileName = "git"
	$startInfo.WorkingDirectory = $Root
	$startInfo.UseShellExecute = $false
	$startInfo.RedirectStandardInput = $true
	$startInfo.RedirectStandardOutput = $true
	$startInfo.RedirectStandardError = $true
	$startInfo.ArgumentList.Add("check-attr")
	$startInfo.ArgumentList.Add("--stdin")
	$startInfo.ArgumentList.Add("export-ignore")

	$process = [Diagnostics.Process]::new()
	$process.StartInfo = $startInfo
	try
	{
		if (-not $process.Start())
		{
			throw "Could not start git check-attr."
		}

		$outputTask = $process.StandardOutput.ReadToEndAsync()
		$errorTask = $process.StandardError.ReadToEndAsync()
		foreach ($path in $Files)
		{
			$process.StandardInput.Write($path)
			$process.StandardInput.Write("`n")
		}
		$process.StandardInput.Close()

		$process.WaitForExit()
		$output = $outputTask.GetAwaiter().GetResult()
		$errorOutput = $errorTask.GetAwaiter().GetResult()
		if ($process.ExitCode -ne 0)
		{
			throw "Could not read export-ignore attributes: $errorOutput"
		}

		return @(
			$output -split "`n" |
				ForEach-Object { $_.TrimEnd("`r") } |
				Where-Object { $_.Length -gt 0 }
		)
	}
	finally
	{
		$process.Dispose()
	}
}

function Get-PublicTreeFiles
{
	param([Parameter(Mandatory)][string]$Root)

	Push-Location $Root
	try
	{
		$files = @(
			& git ls-files --cached --others --exclude-standard |
				ForEach-Object { $_.Replace("\", "/") }
		)
		if ($LASTEXITCODE -ne 0)
		{
			throw "Could not enumerate repository files."
		}

		if ($files.Count -eq 0)
		{
			return @()
		}

		$exportIgnored = [Collections.Generic.HashSet[string]]::new(
			[StringComparer]::OrdinalIgnoreCase)
		$attributeLines = @(Get-ExportIgnoreEvidence `
			-Root $Root `
			-Files $files)

		foreach ($line in $attributeLines)
		{
			if ($line -match "^(?<path>.*): export-ignore: set$")
			{
				$null = $exportIgnored.Add($Matches.path.Replace("\", "/"))
			}
		}

		return @($files | Where-Object { -not $exportIgnored.Contains($_) })
	}
	finally
	{
		Pop-Location
	}
}

function Get-SearchableContent
{
	param(
		[Parameter(Mandatory)][string]$Root,
		[Parameter(Mandatory)][string]$RelativePath
	)

	$fullPath = [IO.Path]::GetFullPath(
		[IO.Path]::Combine($Root, $RelativePath.Replace("/", "\")))
	$rootPrefix = $Root.TrimEnd("\") + "\"
	if (-not $fullPath.StartsWith(
		$rootPrefix,
		[StringComparison]::OrdinalIgnoreCase))
	{
		throw "Refusing to scan a path outside the repository: $RelativePath"
	}

	if (-not [IO.File]::Exists($fullPath))
	{
		return ""
	}

	return [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($fullPath))
}

function Test-ChangeMarkerAllowed
{
	param([Parameter(Mandatory)][string]$Path)

	$allowed = @(
		"docs/agent-onboarding.md",
		"docs/configuration.md",
		"docs/manual-tests/web-tab-monitoring.md",
		"src/Pact.Core/Web/Monitoring/WebMonitorRuleCompiler.cs",
		"src/Pact.Infrastructure/Settings/SettingsFileStore.cs",
		"src/Pact.Presentation/Settings/SettingsHelpContent.cs",
		"src/Pact.Presentation/Settings/ViewModels/WebMonitorRuleItemViewModel.cs",
		"tests/Pact.App.Avalonia.Tests/Views/SettingsWindowInteractionTests.cs",
		"tests/Pact.Core.Tests/Web/Monitoring/WebMonitorRuleCompilerTests.cs",
		"tests/Pact.Infrastructure.Tests/Settings/SettingsFileStoreTests.cs",
		"tests/Pact.Presentation.Tests/Settings/WebMonitoringRulesSectionViewModelTests.cs"
	)

	return $allowed -contains $Path
}

$root = Resolve-RepositoryRoot $RepositoryRoot
$files = @(Get-PublicTreeFiles $root)
$findings = [Collections.Generic.List[string]]::new()

$historyRule = "/docs/" + "superpowers/** export-ignore"
$historyFiles = @(
	$files | Where-Object {
		$_.StartsWith("docs/superpowers/", [StringComparison]::OrdinalIgnoreCase)
	}
)
if ($historyFiles.Count -gt 0)
{
	$attributesPath = [IO.Path]::Combine($root, ".gitattributes")
	$hasExactHistoryRule = [IO.File]::Exists($attributesPath) -and
		(Get-Content $attributesPath) -ccontains $historyRule
	if (-not $hasExactHistoryRule)
	{
		$findings.Add(
			".gitattributes: missing exact private-history export-ignore rule")
	}

	foreach ($path in $historyFiles)
	{
		$findings.Add("$path`: private history would be exported")
	}
}

$forbiddenPathRules = @(
	@{ Pattern = "(^|/)\.pact-reviews(/|$)"; Reason = "scenario transport data" },
	@{ Pattern = "(^|/)(bin|obj|TestResults|artifacts)(/|$)"; Reason = "build or test output" },
	@{ Pattern = "\.pdb$"; Reason = "debug symbols" },
	@{ Pattern = "(^|/)(Logs|Temp|WebView)(/|$)"; Reason = "runtime data" },
	@{ Pattern = "(^|/)Settings/(projects|shell-profiles|web-link-templates|web-monitor-rules|prompt-templates|scenarios|git-helpers|recent-directories)\.json$"; Reason = "runtime settings data" },
	@{ Pattern = "(^|/)(\.env(?:\..*)?|secrets?\.json|credentials?(?:\.[^/]*)?|[^/]+\.(pem|pfx|key))$"; Reason = "secret-bearing filename" },
	@{ Pattern = "(^|/)(spike|spikes)(/|$)"; Reason = "experimental spike" },
	@{ Pattern = "(^|/)Pact\.Spike(?:\.|/|$)"; Reason = "obsolete spike project" }
)

foreach ($path in $files)
{
	foreach ($rule in $forbiddenPathRules)
	{
		if ($path -match $rule.Pattern)
		{
			$findings.Add("$path`: $($rule.Reason)")
		}
	}
}

$skippedBinaryPaths = @(
	"third_party/conpty/1.25.260303002/win10-x64/OpenConsole.exe",
	"third_party/conpty/1.25.260303002/win10-x64/conpty.dll"
)
$skippedBinaryPathSet = [Collections.Generic.HashSet[string]]::new(
	[StringComparer]::OrdinalIgnoreCase)
foreach ($path in $skippedBinaryPaths)
{
	$null = $skippedBinaryPathSet.Add($path)
}

$fileSet = [Collections.Generic.HashSet[string]]::new(
	[StringComparer]::OrdinalIgnoreCase)
foreach ($path in $files)
{
	$null = $fileSet.Add($path)
}
$requiredConPtyProvenance = @(
	"third_party/conpty/LICENSE",
	"third_party/conpty/README.md",
	"third_party/conpty/SHA256SUMS.txt"
)
if ($files | Where-Object {
		$skippedBinaryPathSet.Contains($_)
	})
{
	foreach ($path in $requiredConPtyProvenance)
	{
		if (-not $fileSet.Contains($path))
		{
			$findings.Add("$path`: required ConPTY provenance file is missing")
		}
	}
}

$userRoot = "C:" + [char]92 + "Users" + [char]92
$privateWorkspace = "D:" + [char]92 + "Personal" + [char]92 + "AgentTerminal"
$privateUser = "s" + "." + "titov"
$privateDomain = "pravo" + "." + "tech"
$privateNames = @(
	"Pravo" + "Tech",
	"Pravo" + "Ru",
	"Case" + "book"
)
$obsoleteSpikeName = "Pact" + "." + "Spike"
$changeMarker = "CHANGE" + "-ME-"
$todoMarker = "TO" + "DO"
$tbdMarker = "T" + "BD"

foreach ($path in $files)
{
	if ($skippedBinaryPathSet.Contains($path))
	{
		continue
	}

	$content = Get-SearchableContent -Root $root -RelativePath $path
	$contentRules = @(
		@{ Value = $userRoot; Reason = "Windows user-profile path" },
		@{ Value = $privateWorkspace; Reason = "private workspace path" },
		@{ Value = $privateUser; Reason = "private user identity" },
		@{ Value = $privateDomain; Reason = "private organization domain" },
		@{ Value = $obsoleteSpikeName; Reason = "obsolete spike assertion" }
	)
	foreach ($name in $privateNames)
	{
		$contentRules += @{
			Value = $name
			Reason = "private organization or product identity"
		}
	}

	foreach ($rule in $contentRules)
	{
		if ($content.Contains(
			$rule.Value,
			[StringComparison]::OrdinalIgnoreCase))
		{
			$findings.Add("$path`: $($rule.Reason)")
		}
	}

	$lines = $content -split "\r?\n"
	foreach ($line in $lines)
	{
		$containsTodo = $line -match (
			"(?<![A-Za-z0-9_-])(?:" +
			[Regex]::Escape($todoMarker) + "|" +
			[Regex]::Escape($tbdMarker) +
			")(?![A-Za-z0-9_-])")
		$containsChangeMarker = $line.Contains(
			$changeMarker,
			[StringComparison]::Ordinal)
		if ($containsTodo -and -not $path.StartsWith(
			"src/Pact.App.Avalonia/WebAssets/vendor/xterm/",
			[StringComparison]::OrdinalIgnoreCase))
		{
			$findings.Add("$path`: unreviewed work marker")
			break
		}

		if ($containsChangeMarker -and -not (Test-ChangeMarkerAllowed $path))
		{
			$findings.Add("$path`: unreviewed starter-rule marker")
			break
		}

		if ($line.Contains(
			"registry.json",
			[StringComparison]::OrdinalIgnoreCase) -and
			$line -match "(?i)\b(active|current)\b" -and
			$line -notmatch "(?i)\b(no|not|never|obsolete|historical|removed)\b")
		{
			$findings.Add("$path`: obsolete active registry assertion")
			break
		}

		if ($line -match "(?i)\bWPF\b.{0,80}\bproduct head\b")
		{
			$findings.Add("$path`: obsolete WPF product-head assertion")
			break
		}
	}
}

if ($findings.Count -gt 0)
{
	$findings |
		Sort-Object -Unique |
		ForEach-Object { [Console]::Error.WriteLine($_) }
	exit 1
}

Write-Host "PASS: $($files.Count) public-tree files passed privacy checks."
