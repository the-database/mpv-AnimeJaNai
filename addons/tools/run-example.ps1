param([string]$DataDirectory = '')
$ErrorActionPreference = 'Stop'
$bundle = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DataDirectory) { $DataDirectory = Join-Path $bundle 'example-data' }
$executable = Join-Path $bundle 'host/ajn-addon.exe'
& $executable install-dev (Join-Path $bundle 'counter.ajnaddon') $DataDirectory 'log.write,storage.read,storage.write'
if ($LASTEXITCODE -ne 0) { throw 'Could not install the bundled example.' }
& $executable run org.animejanai.counter $DataDirectory (Join-Path $bundle 'runtime/wasmtime.exe')
if ($LASTEXITCODE -ne 0) { throw 'Example execution failed.' }
Write-Host 'Run this script again: the saved start count should increase.'
