#requires -Version 7
param(
    [Parameter(Mandatory)][string]$BuiltRoot,
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$package = [IO.Path]::GetFullPath($BuiltRoot)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new installer test output directory.' }
if (-not (Test-Path -LiteralPath (Join-Path $package 'addon-package.json'))) { throw 'Supply a complete assembled addon preview.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
& $Compiler '/DInstallerTest' '/DEnableAddons' '/DAppVersion=0.0.0-test' "/DSourceDir=$package" "/O$output" (Join-Path $repo 'installer/animejanai.iss') *> (Join-Path $output 'compile.log')
if ($LASTEXITCODE -ne 0) { throw "Test installer compilation failed; see $output/compile.log." }
$setup = Join-Path $output 'mpv-AnimeJaNai-Setup-0.0.0-test.exe'
& $Dotnet run --project (Join-Path $repo 'addons/tests/AnimeJaNai.Addons.NativeTests') -c Release -- `
    $package (Join-Path $output 'run') $Dotnet $setup --installer-only *> (Join-Path $output 'test.log')
if ($LASTEXITCODE -ne 0) { throw "Installer lifecycle test failed; see $output/test.log." }
Get-Content -LiteralPath (Join-Path $output 'test.log')
