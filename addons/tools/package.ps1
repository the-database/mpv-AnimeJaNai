#requires -Version 7
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$addonRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$archive = $outputRoot + '.zip'
if ((Test-Path -LiteralPath $outputRoot) -or (Test-Path -LiteralPath $archive)) {
    throw 'Choose a new output directory and archive name.'
}
$metadata = Get-Content -LiteralPath (Join-Path $addonRoot '.tools/tools.json') -Raw | ConvertFrom-Json
$ajnLicense = Join-Path $addonRoot '../LICENSE'
if (-not (Test-Path -LiteralPath $ajnLicense)) { $ajnLicense = Join-Path $addonRoot 'LICENSE' }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$hostRoot = Join-Path $outputRoot 'host'
& $Dotnet publish (Join-Path $addonRoot 'src/AnimeJaNai.Addons/AnimeJaNai.Addons.csproj') -c Release -r win-x64 --self-contained true -o $hostRoot
if ($LASTEXITCODE -ne 0) { throw 'Host publication failed.' }
& $Dotnet publish (Join-Path $addonRoot 'src/AnimeJaNai.Addons.Launcher/AnimeJaNai.Addons.Launcher.csproj') -c Release -r win-x64 --self-contained true -o $hostRoot
if ($LASTEXITCODE -ne 0) { throw 'Lifecycle launcher publication failed.' }
foreach ($name in @('runtime', 'source', 'tools', 'examples', 'sdk', 'licenses')) {
    [IO.Directory]::CreateDirectory((Join-Path $outputRoot $name)) | Out-Null
}
Copy-Item -LiteralPath $metadata.wasmtime.path -Destination (Join-Path $outputRoot 'runtime/wasmtime.exe')
Copy-Item -LiteralPath (Join-Path (Split-Path $metadata.wasmtime.path) 'LICENSE') -Destination (Join-Path $outputRoot 'licenses/Wasmtime-LICENSE')
Copy-Item -LiteralPath $ajnLicense -Destination (Join-Path $outputRoot 'licenses/AJN-LICENSE')
Copy-Item -Path (Join-Path $addonRoot 'licenses/*') -Destination (Join-Path $outputRoot 'licenses')
$deps = Get-Content -LiteralPath (Join-Path $hostRoot 'ajn-addon.deps.json') -Raw | ConvertFrom-Json
$runtimePackage = $deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/*' }
if (@($runtimePackage).Count -ne 1) { throw 'Could not identify the published .NET runtime license.' }
$runtimeVersion = $runtimePackage.Split('/')[-1]
$assets = Get-Content -LiteralPath (Join-Path $addonRoot 'src/AnimeJaNai.Addons/obj/project.assets.json') -Raw | ConvertFrom-Json
$runtimePackageDirectory = $null
foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
    $candidate = Join-Path $folder "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
    if (Test-Path -LiteralPath (Join-Path $candidate 'LICENSE.TXT')) { $runtimePackageDirectory = $candidate; break }
}
if (-not $runtimePackageDirectory) { throw 'Could not locate the .NET runtime license and notices.' }
Copy-Item -LiteralPath (Join-Path $runtimePackageDirectory 'LICENSE.TXT') -Destination (Join-Path $outputRoot 'licenses/dotnet-LICENSE.TXT')
Copy-Item -LiteralPath (Join-Path $runtimePackageDirectory 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $outputRoot 'licenses/dotnet-THIRD-PARTY-NOTICES.TXT')
foreach ($name in @('README.md', 'API.md', 'ARCHITECTURE.md', 'ROADMAP.md', 'MANAGEMENT.md', 'LIFECYCLE.md', 'NATIVE-MEDIA.md', 'NATIVE-OUTPUT.md', 'OUTPUTS.md', 'REMOTE-INPUTS.md', 'FRAMES.md', 'FRAME-PERFORMANCE.md', 'NETWORK.md')) {
    Copy-Item -LiteralPath (Join-Path $addonRoot $name) -Destination $outputRoot
}
Copy-Item -LiteralPath (Join-Path $addonRoot 'tools/bootstrap.ps1') -Destination (Join-Path $outputRoot 'tools')
Copy-Item -LiteralPath (Join-Path $addonRoot 'tools/run-example.ps1') -Destination (Join-Path $outputRoot 'tools')
Copy-Item -LiteralPath (Join-Path $addonRoot 'BUNDLE-START.md') -Destination (Join-Path $outputRoot 'START-HERE.md')
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/counter') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/session-controller') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/sample-inspector') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/service-inspector') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/output-inspector') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/remote-inspector') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -Path (Join-Path $addonRoot 'sdk/*') -Destination (Join-Path $outputRoot 'sdk') -Recurse
# Include buildable AJN source without caches, binaries, or user data.
$sourceRoot = Join-Path $outputRoot 'source'
foreach ($source in Get-ChildItem -LiteralPath $addonRoot -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($addonRoot, $source.FullName)
    $normalized = $relative.Replace('\', '/')
    if ($relative -match '(^|[\\/])(bin|obj|\.work|\.tools)([\\/]|$)' -or $relative -match '\.wasm$' -or
        ($relative -match '\.ajnaddon$' -and $normalized -ne 'tests/fixtures/counter-api-1.0.ajnaddon')) { continue }
    $destination = Join-Path $sourceRoot $relative
    [IO.Directory]::CreateDirectory((Split-Path $destination)) | Out-Null
    Copy-Item -LiteralPath $source.FullName -Destination $destination
}
Copy-Item -LiteralPath $ajnLicense -Destination (Join-Path $sourceRoot 'LICENSE')
& (Join-Path $hostRoot 'ajn-addon.exe') build (Join-Path $outputRoot 'examples/counter') $metadata.javy.path (Join-Path $outputRoot 'counter.ajnaddon')
if ($LASTEXITCODE -ne 0) { throw 'Example compilation failed.' }
& (Join-Path $hostRoot 'ajn-addon.exe') build (Join-Path $outputRoot 'examples/session-controller') $metadata.javy.path (Join-Path $outputRoot 'session-controller.ajnaddon')
if ($LASTEXITCODE -ne 0) { throw 'Session controller compilation failed.' }
& (Join-Path $hostRoot 'ajn-addon.exe') build (Join-Path $outputRoot 'examples/sample-inspector') $metadata.javy.path (Join-Path $outputRoot 'sample-inspector.ajnaddon')
if ($LASTEXITCODE -ne 0) { throw 'Sample inspector compilation failed.' }
& (Join-Path $hostRoot 'ajn-addon.exe') build (Join-Path $outputRoot 'examples/service-inspector') $metadata.javy.path (Join-Path $outputRoot 'service-inspector.ajnaddon')
if ($LASTEXITCODE -ne 0) { throw 'Service inspector compilation failed.' }
& (Join-Path $hostRoot 'ajn-addon.exe') build (Join-Path $outputRoot 'examples/output-inspector') $metadata.javy.path (Join-Path $outputRoot 'output-inspector.ajnaddon')
if ($LASTEXITCODE -ne 0) { throw 'Output inspector compilation failed.' }
& (Join-Path $hostRoot 'ajn-addon.exe') build (Join-Path $outputRoot 'examples/remote-inspector') $metadata.javy.path (Join-Path $outputRoot 'remote-inspector.ajnaddon')
if ($LASTEXITCODE -ne 0) { throw 'Remote inspector compilation failed.' }
[IO.Compression.ZipFile]::CreateFromDirectory($outputRoot, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
