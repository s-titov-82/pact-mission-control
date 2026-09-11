[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$ScriptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. $ScriptPath -LibraryOnly

function Assert-ThrowsLike
{
	param(
		[Parameter(Mandatory)][scriptblock]$Action,
		[Parameter(Mandatory)][string]$Pattern
	)

	try
	{
		& $Action
		throw "Expected an exception matching '$Pattern'."
	}
	catch
	{
		if (-not $_.Exception.Message.Contains($Pattern, [StringComparison]::OrdinalIgnoreCase))
		{
			throw
		}
	}
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("PactSoftRestartSelfTest-{0}" -f [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $temporaryRoot 'publish'
$dataRoot = Join-Path $temporaryRoot 'data'
$child = $null
try
{
	$null = [IO.Directory]::CreateDirectory((Join-Path $publish 'conpty'))
	foreach ($relativePath in @('Pact.App.Avalonia.exe', 'Pact.Updater.exe', 'conpty/OpenConsole.exe'))
	{
		[IO.File]::WriteAllText((Join-Path $publish $relativePath), 'test')
	}
	Assert-ThrowsLike `
		-Pattern 'does not exist' `
		-Action { Resolve-PactSoftRestartInputs -PublishDirectory (Join-Path $temporaryRoot 'missing') -DataRoot $dataRoot }
	$null = [IO.Directory]::CreateDirectory($dataRoot)
	Assert-ThrowsLike `
		-Pattern 'must not exist' `
		-Action { Resolve-PactSoftRestartInputs -PublishDirectory $publish -DataRoot $dataRoot }
	[IO.Directory]::Delete($dataRoot)
	$resolved = Resolve-PactSoftRestartInputs -PublishDirectory $publish -DataRoot $dataRoot
	if ($resolved.PublishDirectory -ne [IO.Path]::GetFullPath($publish))
	{
		throw 'PublishDirectory was not resolved exactly.'
	}
	$currentProcessPath = (Get-Process -Id $PID).Path
	$currentSnapshot = @(Get-PactProcessesByExactPath -Paths @($currentProcessPath) |
		Where-Object { $_.Id -eq $PID })
	if ($currentSnapshot.Count -ne 1 -or
		[string]$currentSnapshot[0].ExecutablePath -ne $currentProcessPath)
	{
		throw 'Exact-path process discovery did not return an immutable path snapshot.'
	}
	$fixturePaths = Get-PactGateFixturePaths -DataRoot $dataRoot
	$retainedPrefix = Join-Path $dataRoot 'Temp/Retained/SoftRestartGate'
	foreach ($fixturePath in @(
			$fixturePaths.SentinelPath,
			$fixturePaths.EvidencePath,
			$fixturePaths.TerminalScriptPath,
			$fixturePaths.PagePath))
	{
		if (-not $fixturePath.StartsWith($retainedPrefix, [StringComparison]::OrdinalIgnoreCase))
		{
			throw "Gate fixture escaped retained Temp: $fixturePath"
		}
	}
	$settingsDirectory = Join-Path $temporaryRoot 'settings'
	$agentControlPath = Join-Path $settingsDirectory 'agent-control.json'
	$allocatedPort = New-PactGateAgentControlSettings -Path $agentControlPath
	$agentControl = Get-Content -LiteralPath $agentControlPath -Raw | ConvertFrom-Json
	if ([int]$agentControl.port -ne $allocatedPort -or [bool]$agentControl.enabled)
	{
		throw 'The gate did not isolate and disable its agent-control endpoint.'
	}
	$cleanupDirectory = Join-Path $temporaryRoot 'cleanup'
	$null = [IO.Directory]::CreateDirectory($cleanupDirectory)
	[IO.File]::WriteAllText((Join-Path $cleanupDirectory 'owned.txt'), 'owned')
	Remove-PactGateDirectory -Path $cleanupDirectory -Timeout ([TimeSpan]::FromSeconds(1))
	if ([IO.Directory]::Exists($cleanupDirectory))
	{
		throw 'Bounded gate cleanup left its owned directory behind.'
	}

	$missingEvidence = Join-Path $temporaryRoot 'missing/evidence.json'
	Assert-ThrowsLike `
		-Pattern 'Timed out' `
		-Action { Wait-PactProbeEvidence -Path $missingEvidence -Timeout ([TimeSpan]::Zero) }

	$evidencePath = Join-Path $temporaryRoot 'evidence.json'
	$restartId = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
	[ordered]@{
		RestartId = $restartId
		ProcessId = 42
		ExecutablePath = 'C:\Pact\Pact.App.Avalonia.exe'
		OpenConsoleProcessIds = @(201, 202)
		TerminalProcessIds = @(101, 102)
		RestoredTerminalIds = @('agent', 'shell')
		ColdStartFallbacks = @('shell:resume-not-supported')
		RestoredWebPageIds = @('web')
		UnreadTerminalIds = @('shell')
		SelectedItemId = 'agent'
		Failures = @()
		OrchestratorRestored = $false
		SelectionRestored = $true
		UpdateFailureCategory = $null
	} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM
	$evidence = Read-PactSoftRestartEvidence -Path $evidencePath
	Assert-PactSoftRestartEvidence `
		-Evidence $evidence `
		-AgentSessionId agent `
		-ShellSessionId shell `
		-WebPageId web `
		-ExpectedExecutablePath 'C:\Pact\Pact.App.Avalonia.exe'
	[IO.File]::WriteAllText($evidencePath, '{')
	Assert-ThrowsLike -Pattern 'not valid JSON' -Action { Read-PactSoftRestartEvidence -Path $evidencePath }

	$child = Start-Process `
		-FilePath (Join-Path $PSHOME 'pwsh.exe') `
		-ArgumentList @('-NoLogo', '-NoProfile', '-Command', 'Wait-Event') `
		-PassThru
	Stop-PactGateChildren -ProcessIds @($child.Id)
	if (-not $child.WaitForExit(5000))
	{
		throw 'Gate-owned child process was not stopped.'
	}
	Write-Output 'PASS: soft-restart gate validates paths/evidence, times out by deadline, and cleans only supplied child ids.'
}
finally
{
	if ($null -ne $child -and -not $child.HasExited)
	{
		Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue
	}
	if ([IO.Directory]::Exists($temporaryRoot))
	{
		[IO.Directory]::Delete($temporaryRoot, $true)
	}
}
