[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$ScriptPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$previousCi = $env:CI
try
{
	$env:CI = 'false'
	try
	{
		& $ScriptPath `
			-Version '1.2.3' `
			-SetupPath 'missing-setup.exe' `
			-ZipPath 'missing.zip' `
			-WorkingDirectory 'unsafe' `
			-ExpectedAuthenticodeStatus Unsigned
		throw 'Local-machine installer smoke was accepted.'
	}
	catch
	{
		if (-not $_.Exception.Message.Contains('restricted to a disposable CI runner'))
		{
			throw
		}
	}
	Write-Output 'PASS: installer install/uninstall smoke refuses to mutate a developer workstation.'
}
finally
{
	$env:CI = $previousCi
}
