# AnimeJaNai native benchmark driver.
#
# Measures upscaling inference throughput (pre-processing + model +
# post-processing on the GPU; decode excluded) for the built-in
# Balanced (slot 1010) and Performance (slot 1011) templates across the
# bundled seed resolutions, on the backend configured in
# animejanai.conf. Run via animejanai_benchmark_all.bat (which the
# config editor's Benchmark button launches) from the animejanai
# directory.
#
# TensorRT builds an engine per model/resolution on the first run
# (about a minute each, cached afterwards - playback reuses them).

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot     # animejanai/
$conf = Join-Path $root "animejanai.conf"
$inference = Join-Path $root "inference"
$onnx = Join-Path $root "onnx"
$seeds = Join-Path $PSScriptRoot "seeds"

# backend from [global] in animejanai.conf (3.3.x semantics)
$backend = "TensorRT"
if (Test-Path $conf) {
    $inGlobal = $false
    foreach ($line in Get-Content $conf) {
        if ($line -match '^\[(.+)\]$') { $inGlobal = $Matches[1] -eq "global" }
        elseif ($inGlobal -and $line -match '^backend=(\S+)') { $backend = $Matches[1] }
    }
}
$isDml = $backend -match '^(?i)(directml|ncnn)$'
$harness = Join-Path $inference $(if ($isDml) { "aji_harness_dml.exe" } else { "aji_harness.exe" })
if (-not (Test-Path $harness)) {
    Write-Host "Benchmark tool not found: $harness" -ForegroundColor Red
    exit 1
}

Write-Host "AnimeJaNai benchmark - backend: $backend" -ForegroundColor Cyan
if (-not $isDml) {
    Write-Host "(TensorRT builds an engine per model/resolution on the first run,"
    Write-Host " about a minute each; they are cached and reused by playback.)"
}
Write-Host ""

$slots = [ordered]@{ "Balanced" = 1010; "Performance" = 1011 }
$resolutions = Get-ChildItem $seeds -Filter "*.raw" | ForEach-Object {
    $_.BaseName
} | Sort-Object { [int]($_ -split 'x')[0] }

$frames = 120
$table = @{}
foreach ($name in $slots.Keys) { $table[$name] = [ordered]@{} }

foreach ($res in $resolutions) {
    $w, $h = $res -split 'x'
    foreach ($name in $slots.Keys) {
        Write-Host -NoNewline ("{0,-12} {1,-10} " -f $name, $res)
        $argv = @("--input", (Join-Path $seeds "$res.raw"),
                  "--width", $w, "--height", $h,
                  "--frames", $frames, "--fps", "23.976",
                  "--conf", $conf, "--model-dir", $onnx,
                  "--slot", $slots[$name])
        if (-not $isDml) {
            $argv += @("--trtexec", (Join-Path $inference "trtexec.exe"))
        }
        # native commands write progress/warnings to stderr; with
        # ErrorActionPreference=Stop PowerShell would turn those into
        # fatal NativeCommandErrors, so relax it around the call
        $eap = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $out = & $harness @argv 2>&1 | Out-String
        $ErrorActionPreference = $eap
        if ($out -match '([\d.]+) ms/frame') {
            $fps = [math]::Round(1000.0 / [double]$Matches[1], 1)
            $table[$name][$res] = $fps
            Write-Host "$fps fps" -ForegroundColor Green
        } else {
            $table[$name][$res] = ""
            Write-Host "failed" -ForegroundColor Red
            Write-Host ($out | Select-String -Pattern "failed|error" | Select-Object -First 2)
        }
    }
}

# markdown table like the 3.3.x benchmark wrote
$lines = @()
$lines += "AnimeJaNai inference benchmark - backend: $backend"
$lines += ""
$lines += "|fps|" + ($resolutions -join "|") + "|"
$lines += "|-|" + (($resolutions | ForEach-Object { "-" }) -join "|") + "|"
foreach ($name in $slots.Keys) {
    $row = $resolutions | ForEach-Object { $table[$name][$_] }
    $lines += "|$name|" + ($row -join "|") + "|"
}
$outFile = Join-Path $root "benchmark.txt"
$lines | Set-Content $outFile
Write-Host ""
$lines | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "Saved to $outFile" -ForegroundColor Cyan
