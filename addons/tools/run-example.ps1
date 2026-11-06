#requires -Version 7
param([string]$DataDirectory = '')
$ErrorActionPreference = 'Stop'
$bundle = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $bundle 'host/ajn-addon.exe'
$runtime = Join-Path $bundle 'runtime/wasmtime.exe'
if (Test-Path -LiteralPath $executable) {
    if (-not $DataDirectory) { $DataDirectory = Join-Path $bundle 'example-data' }
} else {
    $installation = [IO.Path]::GetFullPath((Join-Path $bundle '..'))
    $executable = Join-Path $installation 'addon-host/ajn-addon.exe'
    $runtime = Join-Path $installation 'addon-host/runtime/wasmtime.exe'
    if (-not $DataDirectory) { $DataDirectory = Join-Path $installation 'addon-work/example-data' }
}
if (-not (Test-Path -LiteralPath $executable) -or -not (Test-Path -LiteralPath $runtime)) {
    throw 'Extract the complete standalone developer bundle or full AJN addon preview before running this example.'
}
& $executable install-dev (Join-Path $bundle 'counter.ajnaddon') $DataDirectory 'log.write,storage.read,storage.write'
if ($LASTEXITCODE -ne 0) { throw 'Could not install the bundled example.' }
& $executable run org.animejanai.counter $DataDirectory $runtime
if ($LASTEXITCODE -ne 0) { throw 'Example execution failed.' }
Write-Host 'Run this script again: the saved start count should increase.'
