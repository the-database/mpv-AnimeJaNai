#requires -Version 7
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Dotnet = 'dotnet',
    [string]$Git = 'git'
)
$ErrorActionPreference = 'Stop'
$addonRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repository = [IO.Path]::GetFullPath((Join-Path $addonRoot '..'))
function Git-Value([string[]]$CommandArgs) {
    $value = & $Git -C $repository @CommandArgs
    if ($LASTEXITCODE -ne 0) { throw 'Git could not identify the committed host source. Supply -Git if it is not on PATH.' }
    return ($value | Out-String).Trim()
}
if (Git-Value -CommandArgs @('status', '--porcelain', '--untracked-files=no')) { throw 'Commit tracked source changes before producing a host bundle.' }
$sourceCommit = Git-Value -CommandArgs @('rev-parse', 'HEAD')
$sourceObjects = [ordered]@{}
foreach ($name in @('addons', 'shared', 'LICENSE')) { $sourceObjects[$name] = Git-Value -CommandArgs @('rev-parse', "HEAD:$name") }
$tracked = & $Git -C $repository -c core.quotepath=false ls-files -- addons shared
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked host source files.' }
$dotnetSdk = (& $Dotnet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not identify the .NET SDK.' }
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
# The updater transaction tests share this source with the normal updater.
# Preserve the same relative layout for the bundle's buildable test project.
[IO.Directory]::CreateDirectory((Join-Path $outputRoot 'shared')) | Out-Null
foreach ($relative in $tracked | Where-Object { $_.StartsWith('shared/') }) {
    $destination = Join-Path $outputRoot $relative
    [IO.Directory]::CreateDirectory((Split-Path $destination)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository $relative) -Destination $destination
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
foreach ($name in @('README.md', 'USER-GUIDE.md', 'CREATOR-GUIDE.md', 'CREATOR-RECIPES.md', 'TROUBLESHOOTING.md', 'API.md', 'ARCHITECTURE.md', 'ROADMAP.md', 'MANAGEMENT.md', 'LIFECYCLE.md', 'NATIVE-MEDIA.md', 'NATIVE-OUTPUT.md', 'OUTPUTS.md', 'REMOTE-INPUTS.md', 'FRAMES.md', 'PLAYER-FRAMES.md', 'FRAME-PERFORMANCE.md', 'NETWORK.md')) {
    Copy-Item -LiteralPath (Join-Path $addonRoot $name) -Destination $outputRoot
}
Copy-Item -LiteralPath (Join-Path $addonRoot 'tools/bootstrap.ps1') -Destination (Join-Path $outputRoot 'tools')
Copy-Item -LiteralPath (Join-Path $addonRoot 'RELEASE-INTEGRATION.md') -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $addonRoot 'tools/run-example.ps1') -Destination (Join-Path $outputRoot 'tools')
Copy-Item -LiteralPath (Join-Path $addonRoot 'tools/test-tutorial.ps1') -Destination (Join-Path $outputRoot 'tools')
Copy-Item -LiteralPath (Join-Path $addonRoot 'tools/render-docs.ps1') -Destination (Join-Path $outputRoot 'tools')
Copy-Item -LiteralPath (Join-Path $addonRoot 'BUNDLE-START.md') -Destination (Join-Path $outputRoot 'START-HERE.md')
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/counter') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/session-controller') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/sample-inspector') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/service-inspector') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/output-inspector') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/remote-inspector') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -LiteralPath (Join-Path $addonRoot 'examples/player-inspector') -Destination (Join-Path $outputRoot 'examples') -Recurse
Copy-Item -Path (Join-Path $addonRoot 'sdk/*') -Destination (Join-Path $outputRoot 'sdk') -Recurse
# Include buildable AJN source without caches, binaries, or user data.
$sourceRoot = Join-Path $outputRoot 'source'
foreach ($trackedFile in $tracked | Where-Object { $_.StartsWith('addons/') }) {
    $relative = $trackedFile.Substring('addons/'.Length)
    $normalized = $relative.Replace('\', '/')
    if ($relative -match '(^|[\\/])(bin|obj|\.work|\.tools)([\\/]|$)' -or $relative -match '\.wasm$' -or
        ($relative -match '\.ajnaddon$' -and $normalized -ne 'tests/fixtures/counter-api-1.0.ajnaddon')) { continue }
    $destination = Join-Path $sourceRoot $relative
    [IO.Directory]::CreateDirectory((Split-Path $destination)) | Out-Null
    Copy-Item -LiteralPath (Join-Path $addonRoot $relative) -Destination $destination
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
& (Join-Path $hostRoot 'ajn-addon.exe') build (Join-Path $outputRoot 'examples/player-inspector') $metadata.javy.path (Join-Path $outputRoot 'player-inspector.ajnaddon')
if ($LASTEXITCODE -ne 0) { throw 'Player inspector compilation failed.' }
& (Join-Path $addonRoot 'tools/render-docs.ps1') -Directory $outputRoot
if ((Git-Value -CommandArgs @('rev-parse', 'HEAD')) -ne $sourceCommit -or (Git-Value -CommandArgs @('status', '--porcelain', '--untracked-files=no'))) {
    throw 'The committed source changed while the host was being packaged.'
}
$files = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $outputRoot -Recurse -File | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($outputRoot, $file.FullName).Replace('\', '/')
    $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
@{ schemaVersion = 1; platform = 'win-x64'; sourceCommit = $sourceCommit; sourceObjects = $sourceObjects;
   dotnetSdk = $dotnetSdk; files = $files } | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $outputRoot 'host-build.json') -Encoding utf8NoBOM
[IO.Compression.ZipFile]::CreateFromDirectory($outputRoot, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
