# AnimeJaNai playback benchmark driver.
#
# Measures real end-to-end mpv playback throughput - nvdec decode plus the aji
# upscale filter - for the built-in benchmark templates Balanced (slot 1010)
# and Performance (slot 1011) across the bundled source clips, on the backend
# configured in animejanai.conf. This is the fps that determines whether your
# hardware can actually play content at each resolution; it is lower than raw
# inference fps because, like real playback, it includes video decode.
#
# Launched by animejanai_benchmark_all.bat (the Manager's Run Benchmarks
# button). mpv windows open and close on their own during the run - do not
# close or click them, or the timings are invalid.
#
# Method: each cell runs mpvnet.com uncapped (--untimed --vo=null), looping a
# short clip. A warmup run builds the TensorRT engine and fills the pipeline,
# then two timed runs (low/high frame counts) are subtracted so the fixed
# startup cost cancels out:  fps = (high - low) / (t_high - t_low).

$ErrorActionPreference = "Stop"
$root        = Split-Path -Parent $PSScriptRoot       # animejanai/
$installRoot = Split-Path -Parent $root               # install root (mpvnet.com is here)
$conf        = Join-Path $root "animejanai.conf"
$mpvConf     = Join-Path $installRoot "portable_config\mpv-animejanai.conf"
$mpvnet      = Join-Path $installRoot "mpvnet.com"

if (-not (Test-Path $mpvnet)) {
    Write-Host "mpvnet.com not found at $mpvnet" -ForegroundColor Red
    exit 1
}

# Backend from [global] in animejanai.conf - for the report header only. The
# native filter dispatches to aji_trt/aji_dml itself and animejanai_backend.lua
# sets the right hwdec, so we must NOT override decoding here.
$backend = "TensorRT"
if (Test-Path $conf) {
    $inGlobal = $false
    foreach ($line in Get-Content $conf) {
        if ($line -match '^\[(.+)\]$') { $inGlobal = $Matches[1] -eq "global" }
        elseif ($inGlobal -and $line -match '^backend=(\S+)') { $backend = $Matches[1] }
    }
}

# Pull the aji filter string from the managed conf so paths stay in sync; only
# the slot is swapped per template below.
$vfBase = $null
foreach ($line in Get-Content $mpvConf) {
    if ($line -match '^\s*vf=(@aji:.+)$') { $vfBase = $Matches[1]; break }
}
if (-not $vfBase) {
    Write-Host "Could not find the aji vf line in $mpvConf" -ForegroundColor Red
    exit 1
}

Write-Host "AnimeJaNai playback benchmark - backend: $backend" -ForegroundColor Cyan
Write-Host "mpv windows will open and close on their own. Do NOT close or click"
Write-Host "them while the benchmark runs, or the results will be invalid."
Write-Host "(TensorRT builds an engine per resolution on the first run, about a"
Write-Host " minute each and cached afterward; the full sweep takes several minutes,"
Write-Host " longer on slower GPUs.)"
Write-Host ""

$slots = [ordered]@{ "Balanced" = 1010; "Performance" = 1011 }
$resolutions = Get-ChildItem $PSScriptRoot -Filter "*.mp4" | ForEach-Object {
    $_.BaseName
} | Sort-Object { [int]($_ -split 'x')[0] }

# Frame counts. No --loop-file: it defeats --frames (mpv never quits a looping
# file), so the high count must fit within one pass of the bundled clips (each
# is ~90 s, ~2157 frames at 23.976 fps), with margin.
$warmupFrames = 200
$lowFrames    = 500
$highFrames   = 1800

function Invoke-MpvFrames($video, $vf, $n) {
    # & with splatting quotes each arg correctly; -- guards the path so a clip
    # name with spaces/dashes can't be parsed as more options or a stdin '-'.
    $a = @(
        '--process-instance=multi', '--auto-load-folder=no', '--untimed', '--no-audio',
        '--vo=null', '--keep-open=no', '--idle=no', '--sid=no',
        '--no-resume-playback', '--save-position-on-quit=no', '--start=0',
        "--vf=$vf", "--frames=$n", '--', $video
    )
    return (Measure-Command { & $mpvnet @a *> $null }).TotalSeconds
}

$table = @{}
foreach ($name in $slots.Keys) { $table[$name] = [ordered]@{} }

# mpv.net auto-loads every file in the opened file's folder into a playlist, and
# --auto-load-folder=no on the command line does not reliably suppress it. If we
# played benchmarks/<res>.mp4 directly, each run would also play every other
# resolution and the timing would be meaningless. So copy each clip into its own
# clean temp folder and play it from there - a one-file folder has nothing to
# auto-load.
$clipRoot = Join-Path ([System.IO.Path]::GetTempPath()) "animejanai-bench"
Remove-Item $clipRoot -Recurse -Force -ErrorAction SilentlyContinue

foreach ($res in $resolutions) {
    $cellDir = Join-Path $clipRoot $res
    New-Item -ItemType Directory -Path $cellDir -Force | Out-Null
    $video = Join-Path $cellDir "$res.mp4"
    Copy-Item (Join-Path $PSScriptRoot "$res.mp4") $video -Force
    foreach ($name in $slots.Keys) {
        $vf = $vfBase -replace 'slot=\d+', ("slot=" + $slots[$name])
        Write-Host -NoNewline ("{0,-12} {1,-10} " -f $name, $res)
        try {
            [void](Invoke-MpvFrames $video $vf $warmupFrames)   # build engine + fill pipeline
            $tLow  = Invoke-MpvFrames $video $vf $lowFrames
            $tHigh = Invoke-MpvFrames $video $vf $highFrames
            $dt = $tHigh - $tLow
            if ($dt -le 0) { throw "timing anomaly (dt=$([math]::Round($dt,3))s)" }
            $fps = [math]::Round(($highFrames - $lowFrames) / $dt, 1)
            $table[$name][$res] = $fps
            Write-Host "$fps fps" -ForegroundColor Green
        } catch {
            $table[$name][$res] = ""
            Write-Host "failed: $_" -ForegroundColor Red
        }
    }
}

Remove-Item $clipRoot -Recurse -Force -ErrorAction SilentlyContinue

# Markdown table - same shape the Submit-to-Catalog parser expects.
$lines = @()
$lines += "AnimeJaNai playback benchmark - backend: $backend"
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
