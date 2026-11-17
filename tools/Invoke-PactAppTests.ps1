[CmdletBinding()]
param([string]$ResultsDirectory = 'artifacts/test-results/app')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ResultsDirectory))
$project = 'tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj'
$arguments = @($project, '--no-build', '--no-restore', '-m:1', '-nr:false')
Push-Location $repositoryRoot
try {
    $discovery = @(& dotnet test @arguments --list-tests -v q -- NUnit.NumberOfTestWorkers=2)
    if ($LASTEXITCODE -ne 0) { throw 'Avalonia test discovery failed.' }
    $expected = @($discovery | Where-Object { $_ -match '^    \S' }).Count
    if ($expected -eq 0) { throw 'Avalonia test discovery returned no tests.' }

    # Headless Avalonia retains style/template caches across recreated windows.
    # Give each view fixture its own host while keeping the same bounded heap.
    $fixtures = @(Get-ChildItem tests/Pact.App.Avalonia.Tests/Views -Filter '*Tests.cs' -Recurse -File |
        ForEach-Object {
            $source = [IO.File]::ReadAllText($_.FullName)
            $namespace = [regex]::Match($source, '(?m)^namespace ([\w.]+);').Groups[1].Value
            foreach ($class in [regex]::Matches($source, '(?m)^public (?:sealed |partial )*class (\w+)')) {
                "$namespace.$($class.Groups[1].Value)"
            }
        } | Sort-Object -Unique)
    if ($fixtures.Count -eq 0) { throw 'No Avalonia view fixtures were found.' }
    $filters = @($fixtures | ForEach-Object { "FullyQualifiedName~$_." }) + @(
        'FullyQualifiedName!~Pact.App.Avalonia.Tests.Views.')
    $total = 0
    for ($index = 0; $index -lt $filters.Count; $index++) {
        $name = 'app-{0:D3}.trx' -f $index
        Write-Output "Avalonia batch $($index + 1)/$($filters.Count): $($filters[$index])"
        & dotnet test @arguments --filter $filters[$index] --logger "trx;LogFileName=$name" --results-directory $resultsRoot -- NUnit.NumberOfTestWorkers=2
        if ($LASTEXITCODE -ne 0) { throw "Avalonia test batch failed: $($filters[$index])" }
        [xml]$report = [IO.File]::ReadAllText((Join-Path $resultsRoot $name))
        $count = [int]$report.TestRun.ResultSummary.Counters.total
        if ($count -eq 0) { throw "Avalonia batch ran no tests: $($filters[$index])" }
        $total += $count
    }
    if ($total -ne $expected) {
        throw "Avalonia coverage mismatch: discovered $expected tests, batch reports contain $total."
    }
    Write-Output "PASS: all $total discovered Avalonia tests completed in bounded isolated batches."
}
finally { Pop-Location }
