[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Destination,
    [switch]$Replace
)

$toolArguments = @(
    'dotnet', 'run',
    '--project', (Join-Path $PSScriptRoot 'Pact.ProfileTool\Pact.ProfileTool.csproj'),
    '--', '--source', $Source, '--destination', $Destination
)
if ($Replace) { $toolArguments += '--replace' }

& rtk @toolArguments
exit $LASTEXITCODE
