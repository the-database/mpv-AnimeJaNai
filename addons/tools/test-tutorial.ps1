#requires -Version 7
param(
    [Parameter(Mandatory)][string]$HostExecutable,
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$HostExecutable = [IO.Path]::GetFullPath($HostExecutable)
$Compiler = [IO.Path]::GetFullPath($Compiler)
$Runtime = [IO.Path]::GetFullPath($Runtime)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Choose a new tutorial test directory.' }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$guide = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../CREATOR-GUIDE.md') -Raw
function Read-Example([string]$Marker, [string]$Language) {
    $pattern = '<!-- tutorial:' + [regex]::Escape($Marker) + ' -->\s*```' + $Language + '\r?\n([\s\S]*?)\r?\n```'
    $match = [regex]::Match($guide, $pattern)
    if (-not $match.Success) { throw "Missing documented example: $Marker" }
    return $match.Groups[1].Value
}
$checks = [Collections.Generic.List[string]]::new()
function Invoke-Checked([string[]]$CommandArgs) {
    $raw = (& $HostExecutable @CommandArgs | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Tutorial command failed: $($CommandArgs[0])`n$raw" }
    if ($raw.StartsWith('{') -or $raw.StartsWith('[')) { return $raw | ConvertFrom-Json }
    return $raw
}
function Assert-Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "Tutorial check failed: $Name" }
    $checks.Add($Name)
}
$source = Join-Path $outputRoot 'my-greeting'
$data = Join-Path $outputRoot 'test-data'
$v1 = Join-Path $outputRoot 'greeting-0.1.0.ajnaddon'
$v2 = Join-Path $outputRoot 'greeting-0.2.0.ajnaddon'
try {
    $null = Invoke-Checked -CommandArgs @('new', $source, 'org.example.greeting')
    [IO.File]::WriteAllText((Join-Path $source 'manifest.json'), (Read-Example manifest json), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $source 'addon.js'), (Read-Example javascript javascript), [Text.UTF8Encoding]::new($false))
    $null = Invoke-Checked -CommandArgs @('build', $source, $Compiler, $v1)
    $inspected = Invoke-Checked -CommandArgs @('inspect', $v1)
    Assert-Check ($inspected.manifest.id -eq 'org.example.greeting' -and $inspected.manifest.version -eq '0.1.0') 'documented manifest and JavaScript compile'
    $null = Invoke-Checked -CommandArgs @('install-dev', $v1, $data)
    $greeting = Invoke-Checked -CommandArgs @('action', 'org.example.greeting', $data, $Runtime, 'greet')
    Assert-Check ($greeting.message -eq 'Hello from my addon!') 'greeting works with no storage permissions'
    $denied = Invoke-Checked -CommandArgs @('action', 'org.example.greeting', $data, $Runtime, 'remember')
    Assert-Check ($denied.message -like 'To remember greetings,*') 'denied permission returns useful guidance'
    $null = Invoke-Checked -CommandArgs @('install-dev', $v1, $data, 'storage.read,storage.write')
    $started = Invoke-Checked -CommandArgs @('run', 'org.example.greeting', $data, $Runtime)
    Assert-Check ($started.message -eq 'Ready. Choose Show greeting.') 'first run succeeds'
    foreach ($count in 1, 2) {
        $remembered = Invoke-Checked -CommandArgs @('action', 'org.example.greeting', $data, $Runtime, 'remember')
        Assert-Check ($remembered.message -like "Saved greeting ${count}:*") "saved data survives process restart $count"
    }
    $patch = Join-Path $outputRoot 'greeting-settings.json'
    [IO.File]::WriteAllText($patch, (@{ greeting = 'Good evening 日本語!' } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $null = Invoke-Checked -CommandArgs @('configure', 'org.example.greeting', $data, $patch)
    $settings = Invoke-Checked -CommandArgs @('settings', 'org.example.greeting', $data)
    Assert-Check ($settings.values.greeting -eq 'Good evening 日本語!') 'Unicode settings survive disk reload'
    $greeting = Invoke-Checked -CommandArgs @('action', 'org.example.greeting', $data, $Runtime, 'greet')
    Assert-Check ($greeting.message -eq 'Good evening 日本語!') 'action reads updated settings'
    $manifest = Read-Example manifest json | ConvertFrom-Json
    $manifest.version = '0.2.0'
    [IO.File]::WriteAllText((Join-Path $source 'manifest.json'), ($manifest | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    $null = Invoke-Checked -CommandArgs @('build', $source, $Compiler, $v2)
    $null = Invoke-Checked -CommandArgs @('install-dev', $v2, $data, 'storage.read,storage.write')
    $updated = Invoke-Checked -CommandArgs @('action', 'org.example.greeting', $data, $Runtime, 'remember')
    Assert-Check ($updated.message -eq 'Saved greeting 3: Good evening 日本語!') 'update preserves private data and settings'
    $null = Invoke-Checked -CommandArgs @('rollback', 'org.example.greeting', $data)
    $restored = Invoke-Checked -CommandArgs @('action', 'org.example.greeting', $data, $Runtime, 'remember')
    Assert-Check ($restored.message -eq 'Saved greeting 4: Good evening 日本語!') 'rollback preserves data and compatible behavior'
    $null = Invoke-Checked -CommandArgs @('disable', 'org.example.greeting', $data)
    @{ passed = $true; checks = $checks; package = $v1 } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'results.json') -Encoding utf8NoBOM
    Write-Host "PASS $($checks.Count) creator tutorial checks. $outputRoot"
} catch {
    @{ passed = $false; checks = $checks; error = $_.ToString() } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'results.json') -Encoding utf8NoBOM
    throw
}
