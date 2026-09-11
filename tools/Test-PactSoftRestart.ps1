[CmdletBinding()]
param(
	[string]$PublishDirectory,
	[string]$DataRoot,
	[ValidateRange(1, 600)][int]$TimeoutSeconds = 120,
	[switch]$LibraryOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-PactSoftRestartInputs
{
	param(
		[Parameter(Mandatory)][string]$PublishDirectory,
		[Parameter(Mandatory)][string]$DataRoot
	)

	if ([string]::IsNullOrWhiteSpace($PublishDirectory))
	{
		throw 'PublishDirectory is required.'
	}
	if ([string]::IsNullOrWhiteSpace($DataRoot))
	{
		throw 'DataRoot is required.'
	}

	$publish = [IO.Path]::GetFullPath($PublishDirectory)
	$data = [IO.Path]::GetFullPath($DataRoot)
	if (-not [IO.Directory]::Exists($publish))
	{
		throw "PublishDirectory does not exist: $publish"
	}
	if ([IO.Directory]::Exists($data) -or [IO.File]::Exists($data))
	{
		throw "DataRoot must not exist before the gate starts: $data"
	}

	$required = @(
		(Join-Path $publish 'Pact.App.Avalonia.exe'),
		(Join-Path $publish 'Pact.Updater.exe'),
		(Join-Path $publish 'conpty/OpenConsole.exe')
	)
	foreach ($path in $required)
	{
		if (-not [IO.File]::Exists($path))
		{
			throw "Published payload is missing: $path"
		}
	}

	return [pscustomobject]@{
		PublishDirectory = $publish
		DataRoot = $data
	}
}

function Read-PactSoftRestartEvidence
{
	param([Parameter(Mandatory)][string]$Path)

	try
	{
		$evidence = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
	}
	catch
	{
		throw "Soft-restart evidence is not valid JSON: $($_.Exception.Message)"
	}

	if ([string]$evidence.RestartId -notmatch '^[0-9a-f]{64}$')
	{
		throw 'Soft-restart evidence has an invalid restart id.'
	}
	if ([int]$evidence.ProcessId -le 0)
	{
		throw 'Soft-restart evidence has an invalid Pact process id.'
	}
	if (-not [IO.Path]::IsPathFullyQualified([string]$evidence.ExecutablePath))
	{
		throw 'Soft-restart evidence has an invalid Pact executable path.'
	}
	foreach ($name in @(
			'OpenConsoleProcessIds',
			'TerminalProcessIds',
			'RestoredTerminalIds',
			'ColdStartFallbacks',
			'RestoredWebPageIds',
			'UnreadTerminalIds',
			'Failures'))
	{
		if ($null -eq $evidence.$name)
		{
			throw "Soft-restart evidence is missing $name."
		}
	}
	return $evidence
}

function New-PactGateAgentControlSettings
{
	param([Parameter(Mandatory)][string]$Path)

	$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
	try
	{
		$listener.Start()
		$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
	}
	finally
	{
		$listener.Stop()
	}
	$null = [IO.Directory]::CreateDirectory((Split-Path -Parent $Path))
	[ordered]@{
		port = $port
		enabled = $false
	} | ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
	return $port
}

function Get-PactGateFixturePaths
{
	param([Parameter(Mandatory)][string]$DataRoot)

	$root = Join-Path ([IO.Path]::GetFullPath($DataRoot)) 'Temp/Retained/SoftRestartGate'
	return [pscustomobject]@{
		Root = $root
		SentinelPath = Join-Path $root 'terminal.keepalive'
		EvidencePath = Join-Path $root 'soft-restart-probe.json'
		TerminalScriptPath = Join-Path $root 'soft-restart-terminal.ps1'
		PagePath = Join-Path $root 'soft-restart-page.html'
	}
}

function Remove-PactGateDirectory
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][TimeSpan]$Timeout
	)

	$stopwatch = [Diagnostics.Stopwatch]::StartNew()
	do
	{
		if (-not [IO.Directory]::Exists($Path))
		{
			return
		}
		try
		{
			[IO.Directory]::Delete($Path, $true)
			return
		}
		catch [IO.IOException]
		{
			if ($stopwatch.Elapsed -ge $Timeout)
			{
				throw
			}
			[Threading.Thread]::Sleep(50)
		}
		catch [UnauthorizedAccessException]
		{
			if ($stopwatch.Elapsed -ge $Timeout)
			{
				throw
			}
			[Threading.Thread]::Sleep(50)
		}
	}
	while ($true)
}

function Assert-PactSoftRestartEvidence
{
	param(
		[Parameter(Mandatory)]$Evidence,
		[Parameter(Mandatory)][string]$AgentSessionId,
		[Parameter(Mandatory)][string]$ShellSessionId,
		[Parameter(Mandatory)][string]$WebPageId,
		[Parameter(Mandatory)][string]$ExpectedExecutablePath
	)

	$restoredTerminals = @($Evidence.RestoredTerminalIds)
	$actualTerminals = @($restoredTerminals | Sort-Object) -join '|'
	$expectedTerminals = @(@($AgentSessionId, $ShellSessionId) | Sort-Object) -join '|'
	if ($actualTerminals -ne $expectedTerminals)
	{
		throw 'The gate did not restore the exact terminal set.'
	}
	if (@($Evidence.RestoredWebPageIds).Count -ne 1 -or
		[string]$Evidence.RestoredWebPageIds[0] -ne $WebPageId)
	{
		throw 'The gate did not restore the expected browser tab.'
	}
	if (@($Evidence.ColdStartFallbacks) -notcontains "$ShellSessionId`:resume-not-supported")
	{
		throw 'The non-agent terminal did not report its expected cold-start fallback.'
	}
	if (@($Evidence.UnreadTerminalIds).Count -ne 1 -or
		[string]$Evidence.UnreadTerminalIds[0] -ne $ShellSessionId)
	{
		throw 'The unread marker was not restored exactly.'
	}
	if ([string]$Evidence.SelectedItemId -ne $AgentSessionId -or
		-not [bool]$Evidence.SelectionRestored)
	{
		throw 'The selected terminal was not restored.'
	}
	if (@($Evidence.Failures).Count -ne 0)
	{
		throw "Restoration reported failures: $(@($Evidence.Failures) -join ', ')"
	}
	if (-not [string]::Equals(
		[IO.Path]::GetFullPath([string]$Evidence.ExecutablePath),
		[IO.Path]::GetFullPath($ExpectedExecutablePath),
		[StringComparison]::OrdinalIgnoreCase))
	{
		throw 'The relaunched Pact did not report the exact disposable executable path.'
	}
	$terminalProcessIds = @($Evidence.TerminalProcessIds | ForEach-Object { [int]$_ })
	if ($terminalProcessIds.Count -ne 2 -or
		@($terminalProcessIds | Where-Object { $_ -le 0 }).Count -ne 0 -or
		@($terminalProcessIds | Sort-Object -Unique).Count -ne 2)
	{
		throw 'The relaunched Pact did not report exactly two unique positive terminal process ids.'
	}
	$openConsoleProcessIds = @($Evidence.OpenConsoleProcessIds | ForEach-Object { [int]$_ })
	if ($openConsoleProcessIds.Count -ne 2 -or
		@($openConsoleProcessIds | Where-Object { $_ -le 0 }).Count -ne 0 -or
		@($openConsoleProcessIds | Sort-Object -Unique).Count -ne 2)
	{
		throw 'The relaunched Pact did not report exactly two unique positive OpenConsole process ids.'
	}
}

function Wait-PactProbeEvidence
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][TimeSpan]$Timeout,
		[scriptblock]$OnPoll = {}
	)

	$directory = Split-Path -Parent $Path
	$null = [IO.Directory]::CreateDirectory($directory)
	$watcher = [IO.FileSystemWatcher]::new($directory, [IO.Path]::GetFileName($Path))
	$watcher.NotifyFilter = [IO.NotifyFilters]::FileName -bor [IO.NotifyFilters]::LastWrite
	$watcher.EnableRaisingEvents = $true
	$stopwatch = [Diagnostics.Stopwatch]::StartNew()
	try
	{
		while (-not [IO.File]::Exists($Path))
		{
			& $OnPoll
			$remaining = $Timeout - $stopwatch.Elapsed
			if ($remaining -le [TimeSpan]::Zero)
			{
				throw "Timed out waiting for soft-restart evidence: $Path"
			}
			$waitMilliseconds = [Math]::Min(250, [Math]::Max(1, [int]$remaining.TotalMilliseconds))
			$null = $watcher.WaitForChanged([IO.WatcherChangeTypes]::All, $waitMilliseconds)
		}
		& $OnPoll
	}
	finally
	{
		$watcher.Dispose()
	}
}

function Get-PactProcessesByExactPath
{
	param([Parameter(Mandatory)][string[]]$Paths)

	$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
	foreach ($path in $Paths)
	{
		$null = $expected.Add([IO.Path]::GetFullPath($path))
	}
	foreach ($process in Get-Process -ErrorAction SilentlyContinue)
	{
		try
		{
			$executablePath = [string]$process.Path
			if (-not [string]::IsNullOrWhiteSpace($executablePath))
			{
				$executablePath = [IO.Path]::GetFullPath($executablePath)
				if ($expected.Contains($executablePath))
				{
					[pscustomobject]@{
						Id = $process.Id
						ExecutablePath = $executablePath
					}
				}
			}
		}
		catch
		{
			# Processes that exit or deny Path access are not candidates for this unique installation.
		}
	}
}

function Stop-PactGateChildren
{
	param([Parameter(Mandatory)][int[]]$ProcessIds)

	foreach ($processId in @($ProcessIds | Where-Object { $_ -gt 0 } | Sort-Object -Unique))
	{
		$process = Get-Process -Id $processId -ErrorAction SilentlyContinue
		if ($null -ne $process)
		{
			Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
		}
	}
}

function Wait-PactGateProcesses
{
	param(
		[Parameter(Mandatory)][int[]]$ProcessIds,
		[Parameter(Mandatory)][TimeSpan]$Timeout
	)

	$stopwatch = [Diagnostics.Stopwatch]::StartNew()
	foreach ($processId in @($ProcessIds | Where-Object { $_ -gt 0 } | Sort-Object -Unique))
	{
		$process = Get-Process -Id $processId -ErrorAction SilentlyContinue
		if ($null -eq $process)
		{
			continue
		}
		$remaining = $Timeout - $stopwatch.Elapsed
		if ($remaining -le [TimeSpan]::Zero -or
			-not $process.WaitForExit([Math]::Max(1, [int]$remaining.TotalMilliseconds)))
		{
			throw "Timed out waiting for gate-owned process $processId to exit."
		}
	}
}

function Wait-PactInstalledFilesReleased
{
	param(
		[Parameter(Mandatory)][string[]]$Paths,
		[Parameter(Mandatory)][TimeSpan]$Timeout
	)

	$stopwatch = [Diagnostics.Stopwatch]::StartNew()
	do
	{
		$streams = [Collections.Generic.List[IDisposable]]::new()
		try
		{
			foreach ($path in $Paths)
			{
				$streams.Add([IO.File]::Open($path, 'Open', 'ReadWrite', 'None'))
			}
			return
		}
		catch [IO.IOException]
		{
			if ($stopwatch.Elapsed -ge $Timeout)
			{
				throw "Timed out waiting for published Pact files to unlock: $($Paths -join ', ')"
			}
			[Threading.Thread]::Sleep(50)
		}
		finally
		{
			foreach ($stream in $streams)
			{
				$stream.Dispose()
			}
		}
	}
	while ($true)
}

function Start-PactGateProcess
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][string[]]$Arguments,
		[Parameter(Mandatory)][string]$WorkingDirectory
	)

	$startInfo = [Diagnostics.ProcessStartInfo]::new($Path)
	$startInfo.UseShellExecute = $false
	$startInfo.WorkingDirectory = $WorkingDirectory
	foreach ($argument in $Arguments)
	{
		$startInfo.ArgumentList.Add($argument)
	}
	return [Diagnostics.Process]::Start($startInfo)
}

if ($LibraryOnly)
{
	return
}

if (-not $IsWindows)
{
	throw 'The Pact soft-restart gate requires Windows.'
}
if ([string]::IsNullOrWhiteSpace($PublishDirectory) -or [string]::IsNullOrWhiteSpace($DataRoot))
{
	throw 'PublishDirectory and DataRoot are required.'
}

$inputs = Resolve-PactSoftRestartInputs -PublishDirectory $PublishDirectory -DataRoot $DataRoot
$timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
$installation = Join-Path ([IO.Path]::GetTempPath()) ("PactSoftRestartGate-{0}" -f [Guid]::NewGuid().ToString('N'))
$installationPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$ownedProcessIds = [Collections.Generic.HashSet[int]]::new()
$ownedPactProcessIds = [Collections.Generic.HashSet[int]]::new()
$fixturePaths = Get-PactGateFixturePaths -DataRoot $inputs.DataRoot
$sentinelPath = $fixturePaths.SentinelPath
$evidencePath = $fixturePaths.EvidencePath
$agentSessionId = 'soft-restart-agent'
$shellSessionId = 'soft-restart-shell'
$webPageId = 'soft-restart-web'
$appPath = Join-Path $installation 'Pact.App.Avalonia.exe'
$openConsolePath = Join-Path $installation 'conpty/OpenConsole.exe'
$terminalScriptPath = $fixturePaths.TerminalScriptPath
$gateSucceeded = $false

try
{
	$null = [IO.Directory]::CreateDirectory($installation)
	foreach ($publishedItem in Get-ChildItem -LiteralPath $inputs.PublishDirectory -Force)
	{
		Copy-Item -LiteralPath $publishedItem.FullName -Destination $installation -Recurse -Force
	}
	$null = [IO.Directory]::CreateDirectory((Join-Path $inputs.DataRoot 'Settings'))
	$null = [IO.Directory]::CreateDirectory($fixturePaths.Root)
	$agentControlPort = New-PactGateAgentControlSettings `
		-Path (Join-Path $inputs.DataRoot 'Settings/agent-control.json')
	[IO.File]::WriteAllText($sentinelPath, 'keep-running')
	$terminalScript = @'
Write-Output 'PACT_SOFT_RESTART_TERMINAL_READY'
while (Test-Path -LiteralPath '__SENTINEL__')
{
	Start-Sleep -Milliseconds 100
}
'@.Replace('__SENTINEL__', $sentinelPath.Replace("'", "''"))
	[IO.File]::WriteAllText($terminalScriptPath, $terminalScript)
	$pagePath = $fixturePaths.PagePath
	[IO.File]::WriteAllText($pagePath, '<!doctype html><title>Pact soft restart gate</title><p>ready</p>')
	$pwshPath = Join-Path $PSHOME 'pwsh.exe'
	$launchCommand = '"{0}" -NoLogo -NoProfile -File "{1}"' -f $pwshPath, $terminalScriptPath
	$resumeId = '01234567-89ab-cdef-0123-456789abcdef'
	$now = [DateTimeOffset]::UtcNow.ToString('O')
	$projects = [ordered]@{
		schemaVersion = 1
		projects = @([ordered]@{
			id = 'soft-restart-project'
			name = 'Soft restart gate'
			rootPath = $inputs.DataRoot
			createdAt = $now
			lastActiveAt = $now
			notes = $null
			status = 'active'
			activeItemId = $webPageId
			sessions = @(
				[ordered]@{
					id = $agentSessionId
					kind = 'codex'
					title = 'Resumable probe'
					workingDirectory = $inputs.DataRoot
					startCommand = $launchCommand
					resumeCommand = "$launchCommand resume $resumeId"
					status = 'stopped'
					createdAt = $now
					lastActiveAt = $now
				},
				[ordered]@{
					id = $shellSessionId
					kind = 'pwsh'
					title = 'Shell probe'
					workingDirectory = $inputs.DataRoot
					startCommand = $launchCommand
					resumeCommand = $null
					status = 'stopped'
					createdAt = $now
					lastActiveAt = $now
				}
			)
			webPages = @([ordered]@{
				id = $webPageId
				title = 'Soft restart page'
				startUrl = ([uri]$pagePath).AbsoluteUri
				resumeUrl = ([uri]$pagePath).AbsoluteUri
				createdAt = $now
				lastActiveAt = $now
			})
		})
	}
	$projects | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $inputs.DataRoot 'Settings/projects.json') -Encoding utf8NoBOM

	$trackedPaths = @($appPath, $openConsolePath)
	$collectProcesses = {
		foreach ($process in Get-PactProcessesByExactPath -Paths $trackedPaths)
		{
			$null = $ownedProcessIds.Add($process.Id)
			if ([string]::Equals(
				$process.ExecutablePath,
				[IO.Path]::GetFullPath($appPath),
				[StringComparison]::OrdinalIgnoreCase))
			{
				$null = $ownedPactProcessIds.Add($process.Id)
			}
		}
	}
	$initial = Start-PactGateProcess `
		-Path $appPath `
		-WorkingDirectory $installation `
		-Arguments @(
			'--data-root',
			$inputs.DataRoot,
			'--soft-restart-probe-output',
			$evidencePath)
	$null = $ownedProcessIds.Add($initial.Id)
	$null = $ownedPactProcessIds.Add($initial.Id)
	Write-Output "Gate started Pact PID $($initial.Id) with isolated agent-control port $agentControlPort."
	Wait-PactProbeEvidence -Path $evidencePath -Timeout $timeout -OnPoll $collectProcesses
	& $collectProcesses
	$evidence = Read-PactSoftRestartEvidence -Path $evidencePath
	Write-Output (
		"Evidence: restart={0}; pactPid={1}; openConsole=[{2}]; terminals=[{3}]; cold=[{4}]; pages=[{5}]; unread=[{6}]; selected={7}; failures=[{8}]" -f
		$evidence.RestartId,
		$evidence.ProcessId,
		(@($evidence.OpenConsoleProcessIds) -join ','),
		(@($evidence.RestoredTerminalIds) -join ','),
		(@($evidence.ColdStartFallbacks) -join ','),
		(@($evidence.RestoredWebPageIds) -join ','),
		(@($evidence.UnreadTerminalIds) -join ','),
		$evidence.SelectedItemId,
		(@($evidence.Failures) -join ','))
	Assert-PactSoftRestartEvidence `
		-Evidence $evidence `
		-AgentSessionId $agentSessionId `
		-ShellSessionId $shellSessionId `
		-WebPageId $webPageId `
		-ExpectedExecutablePath $appPath
	$null = $ownedProcessIds.Add([int]$evidence.ProcessId)
	$null = $ownedPactProcessIds.Add([int]$evidence.ProcessId)
	foreach ($processId in @($evidence.OpenConsoleProcessIds))
	{
		$null = $ownedProcessIds.Add([int]$processId)
	}
	& $collectProcesses

	$ticketPath = Join-Path $inputs.DataRoot ("Temp/Retained/Updates/Handoffs/{0}/update-resume.json" -f $evidence.RestartId)
	if ([IO.File]::Exists($ticketPath))
	{
		throw 'The relaunched Pact did not consume its exact ticket.'
	}
	$allObservedProcessIds = @($ownedProcessIds) + @($evidence.TerminalProcessIds)
	Wait-PactGateProcesses -ProcessIds $allObservedProcessIds -Timeout $timeout
	Wait-PactInstalledFilesReleased -Paths $trackedPaths -Timeout $timeout
	$gateSucceeded = $true
	Write-Output "PASS: soft restart $($evidence.RestartId) restored terminals, browser, unread state, and selection; Pact and bundled OpenConsole files are released."
}
catch
{
	Write-Output "FAIL: $($_.Exception.Message)"
	throw
}
finally
{
	$cleanupErrors = [Collections.Generic.List[string]]::new()
	[IO.File]::Delete($sentinelPath)
	foreach ($process in Get-PactProcessesByExactPath -Paths @($appPath, $openConsolePath))
	{
		$null = $ownedProcessIds.Add($process.Id)
	}
	if ($ownedProcessIds.Count -gt 0)
	{
		Stop-PactGateChildren -ProcessIds @($ownedProcessIds)
		try
		{
			Wait-PactGateProcesses `
				-ProcessIds @($ownedProcessIds) `
				-Timeout ([TimeSpan]::FromSeconds(5))
		}
		catch
		{
			$cleanupErrors.Add($_.Exception.Message)
		}
	}
	if ([IO.Directory]::Exists($installation) -and
		[IO.Path]::GetFullPath($installation).StartsWith($installationPrefix, [StringComparison]::OrdinalIgnoreCase))
	{
		try
		{
			Remove-PactGateDirectory -Path $installation -Timeout ([TimeSpan]::FromSeconds(5))
		}
		catch
		{
			$cleanupErrors.Add("installation: $($_.Exception.Message)")
		}
	}
	if ([IO.Directory]::Exists($inputs.DataRoot))
	{
		try
		{
			Remove-PactGateDirectory -Path $inputs.DataRoot -Timeout ([TimeSpan]::FromSeconds(5))
		}
		catch
		{
			$cleanupErrors.Add("data root: $($_.Exception.Message)")
		}
	}
	if ($cleanupErrors.Count -gt 0)
	{
		$cleanupMessage = "Gate cleanup failed: $($cleanupErrors -join '; ')"
		if ($gateSucceeded)
		{
			throw $cleanupMessage
		}
		Write-Output "WARNING: $cleanupMessage"
	}
}
