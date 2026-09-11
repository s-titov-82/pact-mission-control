[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$DestinationDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lock = Get-Content -LiteralPath (
	Join-Path $repositoryRoot 'installer/dependencies.lock.json') -Raw |
	ConvertFrom-Json
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$compilerPath = Join-Path $destination 'ISCC.exe'

if ([IO.File]::Exists($compilerPath))
{
	$compilerHash = (Get-FileHash -LiteralPath $compilerPath -Algorithm SHA256).Hash
	if ($compilerHash -ieq $lock.innoSetup.compilerSha256)
	{
		Write-Output $compilerPath
		exit 0
	}
	throw "Existing Inno Setup compiler SHA-256 mismatch: $compilerPath"
}

$parent = [IO.Path]::GetDirectoryName($destination)
$null = [IO.Directory]::CreateDirectory($parent)
$installerPath = Join-Path $parent "innosetup-$($lock.innoSetup.version)-x64.exe"
try
{
	Invoke-WebRequest -Uri $lock.innoSetup.installerUrl -OutFile $installerPath
	$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
	if ($hash -ine $lock.innoSetup.installerSha256)
	{
		throw "Inno Setup installer SHA-256 mismatch. Expected $($lock.innoSetup.installerSha256); actual $hash."
	}
	$signature = Get-AuthenticodeSignature -LiteralPath $installerPath
	if ($signature.Status -ne 'Valid' -or
		-not $signature.SignerCertificate.Subject.Contains(
			$lock.innoSetup.publisher,
			[StringComparison]::OrdinalIgnoreCase))
	{
		throw 'Inno Setup installer does not have the expected valid publisher signature.'
	}

	$installProcess = Start-Process `
		-FilePath $installerPath `
		-ArgumentList @(
			'/VERYSILENT',
			'/SUPPRESSMSGBOXES',
			'/NORESTART',
			'/SP-',
			'/CURRENTUSER',
			"/DIR=`"$destination`"") `
		-Wait `
		-PassThru
	if ($installProcess.ExitCode -ne 0)
	{
		throw "Inno Setup installation failed with exit code $($installProcess.ExitCode)."
	}
}
finally
{
	if ([IO.File]::Exists($installerPath))
	{
		for ($attempt = 1; $attempt -le 20; $attempt++)
		{
			try
			{
				Remove-Item -LiteralPath $installerPath -Force
				break
			}
			catch [UnauthorizedAccessException]
			{
				if ($attempt -eq 20)
				{
					throw
				}
				Start-Sleep -Milliseconds 250
			}
		}
	}
}

if (-not [IO.File]::Exists($compilerPath))
{
	throw "Inno Setup compiler was not installed at $compilerPath."
}
$installedHash = (Get-FileHash -LiteralPath $compilerPath -Algorithm SHA256).Hash
if ($installedHash -ine $lock.innoSetup.compilerSha256)
{
	throw "Installed Inno Setup compiler SHA-256 mismatch: $installedHash"
}
Write-Output $compilerPath
