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
# Method: each cell runs mpvnet.com uncapped (--untimed --vo=null) over a short
# clip. A warmup run builds the TensorRT engine and fills the pipeline, then a
# probe run measures a rough rate, which sizes a second (window) run so the two
# timed points are subtracted to cancel fixed startup cost:
#   fps = (window_frames - probe_frames) / (t_window - t_probe)
# Sizing the window from the probe keeps each cell to about the same wall time
# regardless of GPU speed. Each run is killed if it can't sustain $fpsFloor fps,
# so hopelessly slow cells (well below real-time) are skipped instead of running
# for many minutes; a skipped cell is written as -1, the sentinel the catalog
# renders as a red "-" (well-below-real-time), distinct from a real measurement.

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

# Backend from [global] in animejanai.conf. The native filter dispatches to
# aji_trt/aji_dml itself, but the decoder must match: TensorRT consumes CUDA
# frames (nvdec), DirectML/NCNN consume D3D11 frames (d3d11va). In normal
# playback animejanai_backend.lua sets this, but it does not run reliably under
# this mpvnet.com benchmark launch, so set hwdec here explicitly. Without it,
# the conf default hwdec=nvdec is used and a non-NVIDIA machine fails with
# "Cannot load nvcuda.dll".
$backend = "TensorRT"
if (Test-Path $conf) {
    $inGlobal = $false
    foreach ($line in Get-Content $conf) {
        if ($line -match '^\[(.+)\]$') { $inGlobal = $Matches[1] -eq "global" }
        elseif ($inGlobal -and $line -match '^backend=(\S+)') { $backend = $Matches[1] }
    }
}
$hwdec = if ($backend -match '^(?i:directml|ncnn)$') { 'd3d11va' } else { 'nvdec' }

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
Write-Host " longer on slower GPUs. Cells too slow to be usable, under $fpsFloor fps,"
Write-Host " are skipped and recorded as -1 (shown as '-' in the catalog).)"
Write-Host ""

$slots = [ordered]@{ "Balanced" = 1010; "Performance" = 1011 }
$resolutions = Get-ChildItem $PSScriptRoot -Filter "*.mp4" | ForEach-Object {
    $_.BaseName
} | Sort-Object { [int]($_ -split 'x')[0] }

# Frame counts. No --loop-file: it defeats --frames (mpv never quits a looping
# file), so counts must fit within one pass of the bundled clips (each is ~90 s,
# ~2157 frames at 23.976 fps), with margin.
$warmupFrames    = 120   # build the engine + fill the pipeline (throwaway)
$probeFrames     = 240   # first timed point; also estimates the rate
$windowSeconds   = 20    # target wall time of the measurement window
$minWindowFrames = 200   # smallest probe->window gap (keeps slow cells accurate)
$maxClipFrames   = 1900  # cap so a run always finishes within one clip pass

# Skip a cell once it can't sustain this many fps - well below the ~24 fps
# real-time bar, so the exact number doesn't matter and isn't worth the wait.
# Raising it skips more aggressively; lowering it measures slower hardware.
$fpsFloor        = 6
$decodeInitSec   = 12    # per-run startup allowance (decode/session init) on top of frames/fpsFloor
$buildAllowSec   = 300   # warmup only: also covers a first-time TensorRT engine build

# Kill a run once it has clearly fallen below $fpsFloor (frames it should have
# finished by, plus a startup allowance). Warmup uses a fixed, larger budget so
# a legitimate one-time engine build is never mistaken for a slow GPU.
function Get-RunTimeout($frames) { [int]($decodeInitSec + $frames / $fpsFloor) }

# Warmup budget: TensorRT may build an engine on first use (one-time, minutes),
# so allow that; DirectML/NCNN never build, so hold warmup to the same fps floor
# and skip a slow integrated GPU quickly instead of grinding through it.
$warmupTimeout = if ($backend -match '^(?i:directml|ncnn)$') { Get-RunTimeout $warmupFrames } else { $buildAllowSec }

function Invoke-MpvFrames($video, $vf, $n, $timeoutSec) {
    $flags = @(
        '--process-instance=multi', '--auto-load-folder=no', '--untimed', '--no-audio',
        '--vo=null', '--keep-open=no', '--idle=no', '--sid=no', "--hwdec=$hwdec",
        '--no-resume-playback', '--save-position-on-quit=no', '--start=0',
        "--vf=$vf", "--frames=$n"
    )
    # Every flag is space-free; only the clip path needs quoting and -- guards
    # it. Building the command line by hand (vs Start-Process -ArgumentList)
    # avoids the mangling of spaced/Unicode paths into a stdin '-', while still
    # giving a Process handle we can time out and kill. Windows PowerShell 5.1
    # has no ProcessStartInfo.ArgumentList, hence the explicit string.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $mpvnet
    $psi.Arguments              = ($flags -join ' ') + ' -- "' + $video + '"'
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p  = [System.Diagnostics.Process]::Start($psi)
    # Drain both pipes async so a chatty player can't deadlock on a full buffer
    # while we wait (output is discarded). Using a Process instead of the call
    # operator also means native stderr no longer trips ErrorActionPreference.
    [void]$p.StandardOutput.ReadToEndAsync()
    [void]$p.StandardError.ReadToEndAsync()

    $exited = $p.WaitForExit([int]($timeoutSec * 1000))
    $sw.Stop()
    if (-not $exited) {
        # Too slow - kill the player and any child (e.g. a trtexec build) by tree.
        $eap = $ErrorActionPreference; $ErrorActionPreference = 'SilentlyContinue'
        & taskkill /T /F /PID $p.Id *> $null
        $ErrorActionPreference = $eap
        [void]$p.WaitForExit(5000)
    }
    return [pscustomobject]@{ Seconds = $sw.Elapsed.TotalSeconds; TimedOut = (-not $exited) }
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
            # Warmup builds the engine + fills the pipeline. A timeout here means
            # the GPU is below the floor (or, on TensorRT, a build that overran
            # its budget) - either way not worth measuring.
            $warm = Invoke-MpvFrames $video $vf $warmupFrames $warmupTimeout
            if ($warm.TimedOut) {
                $table[$name][$res] = -1   # sentinel: skipped, ran too slow (catalog renders "-")
                Write-Host "skipped (under $fpsFloor fps)" -ForegroundColor DarkYellow
                continue
            }

            # Probe point: rough rate used both as the first timed point and to
            # size the window run.
            $probe = Invoke-MpvFrames $video $vf $probeFrames (Get-RunTimeout $probeFrames)
            if ($probe.TimedOut) {
                $table[$name][$res] = -1   # sentinel: skipped, ran too slow (catalog renders "-")
                Write-Host "skipped (under $fpsFloor fps)" -ForegroundColor DarkYellow
                continue
            }

            # Size the window so its wall time is ~$windowSeconds at the probed
            # rate (probe time includes startup, so this slightly under-shoots -
            # fine). Floor the gap for accuracy, cap so the run fits one clip pass.
            $fpsEst = $probeFrames / [math]::Max($probe.Seconds, 0.001)
            $windowFrames = [math]::Max([int]($fpsEst * $windowSeconds), $minWindowFrames)
            $windowFrames = [math]::Min($windowFrames, $maxClipFrames - $probeFrames)
            $highFrames = $probeFrames + $windowFrames

            $high = Invoke-MpvFrames $video $vf $highFrames (Get-RunTimeout $highFrames)
            if ($high.TimedOut) {
                $table[$name][$res] = -1   # sentinel: skipped, ran too slow (catalog renders "-")
                Write-Host "skipped (under $fpsFloor fps)" -ForegroundColor DarkYellow
                continue
            }

            $dt = $high.Seconds - $probe.Seconds
            if ($dt -le 0) { throw "timing anomaly (dt=$([math]::Round($dt,3))s)" }
            $fps = [math]::Round(($highFrames - $probeFrames) / $dt, 1)
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
