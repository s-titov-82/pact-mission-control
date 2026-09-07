[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($variableName in @(
	'PACT_SIGNTOOL_PATH',
	'PACT_SIGNING_CERTIFICATE_THUMBPRINT'))
{
	$value = [Environment]::GetEnvironmentVariable($variableName)
	if ([string]::IsNullOrWhiteSpace($value))
	{
		throw "$variableName is required to sign the installer."
	}
}

$targetPath = [IO.Path]::GetFullPath($Path)
& $env:PACT_SIGNTOOL_PATH sign `
	/fd SHA256 `
	/td SHA256 `
	/tr https://timestamp.digicert.com `
	/sha1 $env:PACT_SIGNING_CERTIFICATE_THUMBPRINT `
	/s My `
	$targetPath
if ($LASTEXITCODE -ne 0)
{
	throw "signtool failed to sign $targetPath."
}
& $env:PACT_SIGNTOOL_PATH verify /pa /all $targetPath
if ($LASTEXITCODE -ne 0)
{
	throw "signtool failed to verify $targetPath."
}
if ((Get-AuthenticodeSignature -LiteralPath $targetPath).Status -ne 'Valid')
{
	throw "Windows trust validation failed for $targetPath."
}
