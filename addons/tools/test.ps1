param([string]$Dotnet = 'dotnet', [switch]$UnitOnly, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$addonRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnetPath = (Get-Command $Dotnet -ErrorAction Stop).Source
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $addonRoot '.work/validation' }
$arguments = @('run', '--project', (Join-Path $addonRoot 'tests/AnimeJaNai.Addons.Tests/AnimeJaNai.Addons.Tests.csproj'), '-c', 'Release', '--', $OutputDirectory)
if (-not $UnitOnly) {
    $metadata = Get-Content -LiteralPath (Join-Path $addonRoot '.tools/tools.json') -Raw | ConvertFrom-Json
    $arguments += @($metadata.wasmtime.path, $metadata.javy.path, $dotnetPath)
}
& $dotnetPath @arguments
if ($LASTEXITCODE -ne 0) { throw "Addon validation failed with exit code $LASTEXITCODE." }
