[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath(
	[IO.Path]::Combine($PSScriptRoot, "..", ".."))
$validatorPath = [IO.Path]::Combine(
	$repositoryRoot,
	"tools",
	"Test-PublicTree.ps1")
if (-not [IO.File]::Exists($validatorPath))
{
	throw "Public-tree validator is missing."
}

$fixtureRoot = [IO.Path]::Combine(
	[IO.Path]::GetTempPath(),
	"pact-public-tree-" + [Guid]::NewGuid().ToString("N"))
$fixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
	[IO.Path]::DirectorySeparatorChar) +
	[IO.Path]::DirectorySeparatorChar
if (-not $fixtureRoot.StartsWith(
	$tempRoot,
	[StringComparison]::OrdinalIgnoreCase))
{
	throw "Refusing to create a fixture outside the temporary directory."
}

$null = [IO.Directory]::CreateDirectory($fixtureRoot)
try
{
	& git -C $fixtureRoot init --quiet
	if ($LASTEXITCODE -ne 0)
	{
		throw "Could not initialize the fixture repository."
	}

	[IO.File]::WriteAllText(
		[IO.Path]::Combine($fixtureRoot, "safe.md"),
		"# Safe fixture`n")
	& pwsh -NoProfile -File $validatorPath -RepositoryRoot $fixtureRoot
	if ($LASTEXITCODE -ne 0)
	{
		throw "A safe public tree was rejected."
	}

	$privateUserPath = "C:" + [char]92 + "Users" + [char]92 + "developer"
	[IO.File]::WriteAllText(
		[IO.Path]::Combine($fixtureRoot, "unsafe.md"),
		$privateUserPath)
	& pwsh -NoProfile -File $validatorPath -RepositoryRoot $fixtureRoot 2>$null
	if ($LASTEXITCODE -eq 0)
	{
		throw "A private workstation path was not rejected."
	}

	[IO.File]::Delete([IO.Path]::Combine($fixtureRoot, "unsafe.md"))
	$historyRoot = [IO.Path]::Combine(
		$fixtureRoot,
		"docs",
		"superpowers")
	$null = [IO.Directory]::CreateDirectory($historyRoot)
	[IO.File]::WriteAllText(
		[IO.Path]::Combine($historyRoot, "history.md"),
		"# Private history`n")
	[IO.File]::WriteAllText(
		[IO.Path]::Combine($fixtureRoot, ".gitattributes"),
		"/docs/superpowers/** export-ignore`n")
	& pwsh -NoProfile -File $validatorPath -RepositoryRoot $fixtureRoot
	if ($LASTEXITCODE -ne 0)
	{
		throw "An export-ignored private history tree was rejected."
	}

	Write-Host "PASS: public-tree validator fixtures."
}
finally
{
	if ($fixtureRoot.StartsWith(
		$tempRoot,
		[StringComparison]::OrdinalIgnoreCase) -and
		[IO.Directory]::Exists($fixtureRoot))
	{
		[IO.Directory]::Delete($fixtureRoot, $true)
	}
}
