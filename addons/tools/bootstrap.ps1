param([string]$ToolsDirectory = (Join-Path $PSScriptRoot '../.tools'))
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess -or [Environment]::OSVersion.Platform -ne 'Win32NT') {
    throw 'The first development tool bundle supports Windows x64.'
}
$toolsRoot = [IO.Path]::GetFullPath($ToolsDirectory)
[IO.Directory]::CreateDirectory($toolsRoot) | Out-Null
$downloads = Join-Path $toolsRoot 'downloads'
[IO.Directory]::CreateDirectory($downloads) | Out-Null

function Get-VerifiedArchive([string]$Name, [string]$Url, [string]$Sha256) {
    $target = Join-Path $downloads $Name
    if (-not (Test-Path -LiteralPath $target)) {
        Invoke-WebRequest -Uri $Url -OutFile $target
    }
    $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) { throw "Checksum mismatch for $Name. Remove this download and retry." }
    return $target
}

$runtimeArchive = Get-VerifiedArchive 'wasmtime-v48.0.2-x86_64-windows.zip' `
    'https://github.com/bytecodealliance/wasmtime/releases/download/v48.0.2/wasmtime-v48.0.2-x86_64-windows.zip' `
    'a2e7fadc4f54387c3b0ac371ab984113999f7ce824332eb67201526dfbff2bfe'
$runtimeDir = Join-Path $toolsRoot 'wasmtime-v48.0.2-x86_64-windows'
if (-not (Test-Path -LiteralPath (Join-Path $runtimeDir 'wasmtime.exe'))) {
    Expand-Archive -LiteralPath $runtimeArchive -DestinationPath $toolsRoot
}
$compilerArchive = Get-VerifiedArchive 'javy-x86_64-windows-v9.1.0.gz' `
    'https://github.com/bytecodealliance/javy/releases/download/v9.1.0/javy-x86_64-windows-v9.1.0.gz' `
    '7148baab85d7426e8c18e2cc4deed4e9f03270cf3245dba5617a5be25a1de836'
$compilerPath = Join-Path $toolsRoot 'javy.exe'
$source = [IO.File]::OpenRead($compilerArchive)
try {
    $gzip = [IO.Compression.GZipStream]::new($source, [IO.Compression.CompressionMode]::Decompress)
    try {
        $output = [IO.File]::Create($compilerPath)
        try { $gzip.CopyTo($output) } finally { $output.Dispose() }
    } finally { $gzip.Dispose() }
} finally { $source.Dispose() }
$runtimePath = Join-Path $runtimeDir 'wasmtime.exe'
$metadata = [ordered]@{
    wasmtime = [ordered]@{ version = '48.0.2'; path = $runtimePath; sha256 = (Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant() }
    javy = [ordered]@{ version = '9.1.0'; path = $compilerPath; sha256 = (Get-FileHash -LiteralPath $compilerPath -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $toolsRoot 'tools.json') -Encoding UTF8
$metadata | ConvertTo-Json -Depth 4
