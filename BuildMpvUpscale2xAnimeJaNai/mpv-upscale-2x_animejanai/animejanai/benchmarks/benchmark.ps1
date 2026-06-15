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
# button). A fullscreen mpv window opens and closes on its own during the run -
# do not close, click, or alt-tab away from it, or the timings are invalid.
#
# Method: each cell runs mpvnet.com uncapped over a short clip on the REAL,
# zero-copy video output - the same pipeline as normal playback (gpu-next, the
# backend's gpu-api, --untimed + immediate swap so there's no vsync cap), and
# FULLSCREEN, so the measured cost includes the real present + post-processing
# (high-quality scaling, deband, dither, noise shaders) at the display's
# resolution, exactly as a viewer experiences it. It deliberately does NOT use
# --vo=null: for a GPU frame that forces a per-frame ~50 MB GPU->CPU readback of
# the 4K output and serializes the pipeline, which underreports real playback fps
# several-fold. A warmup run builds the TensorRT engine and fills the pipeline,
# then a probe run measures a rough rate, which sizes a second (window) run so the
# two timed points are subtracted to cancel fixed startup cost (process launch,
# decoder/session init, first engine activation):
#   fps = (window_frames - probe_frames) / (t_window - t_probe)
# Sizing the window from the probe keeps each cell to about the same wall time
# regardless of GPU speed. Each run is killed if it can't sustain $fpsFloor fps,
# so hopelessly slow cells (well below real-time) are skipped instead of running
# for many minutes; a skipped cell is written as -1, the sentinel the catalog
# renders as a red "-" (well-below-real-time), distinct from a real measurement.
#
# Reliability: every run is launched with --load-scripts=no (the player's lua
# scripts - the engine-build monitor that PAUSES playback, the update checker -
# would otherwise stall or skew a timed run) and is watched over mpv's JSON IPC.
# A run whose time-pos stops advancing while the process is alive and not paused
# is a STALL (a transient gpu-next/present hiccup, not slow hardware): it is
# killed and the whole cell is retried, and - unlike a genuine below-floor result
# - it does NOT mark the larger resolutions as too slow. That distinction is what
# keeps an occasional freeze from silently poisoning the rest of a row.

param(
    # Validation helpers (no effect on a normal Manager run):
    [switch]$Quick,        # only the two smallest resolutions, for a fast sanity check
    [string]$OnlyRes = '', # e.g. -OnlyRes 1280x720 to run a single cell column
    [switch]$ShowTiming    # print per-run probe/window timing detail
)

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
# frames (nvdec), DirectML/NCNN consume D3D11 frames (d3d11va). animejanai_backend.lua
# normally sets this, but the benchmark runs with --load-scripts=no, so set hwdec
# here explicitly. Without it, the conf default hwdec=nvdec is used and a non-NVIDIA
# machine fails with "Cannot load nvcuda.dll".
$backend = "TensorRT"
if (Test-Path $conf) {
    $inGlobal = $false
    foreach ($line in Get-Content $conf) {
        if ($line -match '^\[(.+)\]$') { $inGlobal = $Matches[1] -eq "global" }
        elseif ($inGlobal -and $line -match '^backend=(\S+)') { $backend = $Matches[1] }
    }
}
$hwdec = if ($backend -match '^(?i:directml|ncnn)$') { 'd3d11va' } else { 'nvdec' }
# The render API must match too, for a zero-copy present (no GPU->CPU readback):
# TensorRT's CUDA frames go through Vulkan (mpv's only CUDA<->render interop),
# DirectML's D3D11 frames through d3d11. With --load-scripts=no the backend lua
# does not run, so set it here.
$gpuApi = if ($backend -match '^(?i:directml|ncnn)$') { 'd3d11' } else { 'vulkan' }

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

$fpsFloor = 6   # referenced in the banner below; full description at the tunables block

Write-Host "AnimeJaNai playback benchmark - backend: $backend" -ForegroundColor Cyan
Write-Host "A fullscreen mpv window will open and close on its own. Do NOT close,"
Write-Host "click, or alt-tab away from it while the benchmark runs, or the results"
Write-Host "will be invalid."
Write-Host "(TensorRT builds an engine per resolution on the first run, about a"
Write-Host " minute each and cached afterward; the full sweep takes several minutes,"
Write-Host " longer on slower GPUs. Cells too slow to be usable, under $fpsFloor fps,"
Write-Host " are skipped and recorded as -1 (shown as '-' in the catalog).)"
Write-Host ""

$slots = [ordered]@{ "Balanced" = 1010; "Performance" = 1011 }
# Ascending by pixel count: a larger input is always more work for the model, so
# once a template is too slow at one resolution every larger one is too (used to
# short-circuit the rest below).
$resolutions = Get-ChildItem $PSScriptRoot -Filter "*.mp4" | ForEach-Object {
    $_.BaseName
} | Sort-Object { $p = $_ -split 'x'; [int]$p[0] * [int]$p[1] }

# Validation shortcuts (Quick / OnlyRes) trim the resolution set; a normal run uses all.
if ($OnlyRes) { $resolutions = @($resolutions | Where-Object { $_ -eq $OnlyRes }) }
elseif ($Quick) { $resolutions = @($resolutions | Select-Object -First 2) }
if (-not $resolutions -or $resolutions.Count -eq 0) {
    Write-Host "No matching resolution clips found." -ForegroundColor Red
    exit 1
}

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

# Liveness watchdog (over mpv's JSON IPC). A healthy run keeps advancing time-pos;
# if it fails to advance by at least $minProgressSec (media seconds) within $stallMs
# while the process is alive and not paused, the run has STALLED (a transient
# gpu-next/present freeze) and is killed for a retry - distinct from "too slow"
# (decided by the frame/fps-floor timeout). The progress test runs every poll
# regardless of whether the IPC read succeeded, so it also catches a hard freeze
# that wedges IPC (time-pos stops responding) and a clock that only creeps - cases
# a "is the value identical" test missed, letting a frozen run survive until killed
# by hand. $minProgressSec is tiny, so even far-below-floor-but-alive playback
# clears it (that is caught as "too slow" by the timeout, not as a stall).
$pollMs          = 400
$stallMs         = 6000
$minProgressSec  = 0.1   # least time-pos advance (media s) per $stallMs window; below this = frozen
$startGraceSec   = 30    # froze before the first frame ever played (window opened then hung) -> STALL
$cellRetries     = 3     # whole-cell attempts before giving up on a persistently stalling cell

# Kill a run once it has clearly fallen below $fpsFloor (frames it should have
# finished by, plus a startup allowance). Warmup uses a fixed, larger budget so
# a legitimate one-time engine build is never mistaken for a slow GPU.
function Get-RunTimeout($frames) { [int]($decodeInitSec + $frames / $fpsFloor) }

# TensorRT builds an engine on first use (one-time, can take minutes); DirectML
# and NCNN never build. So the benchmark gives engine builds the generous build
# budget and never reads a slow *build* as "too slow to play" - only cached-engine
# playback (the tight fps-floor timeout) decides that. DirectML uses the tight
# timeout throughout, so it never waits on an engine that will never come. The
# per-cell warmup timeout/watchdog is chosen from whether the engine is already
# cached (see the main loop's $engineReady).
$buildsEngines = -not ($backend -match '^(?i:directml|ncnn)$')

# ============================ IPC helpers ============================
# mpv's JSON IPC over a Windows named pipe. Used only as a liveness watchdog
# (poll time-pos); the fps number still comes from the wall-clock two-point
# method below. frame-number is NOT used - on mpv.net it returns unreliable
# values - whereas time-pos is dependable.

function Connect-Ipc($pipeName, $proc, $timeoutMs) {
    $deadline = [Environment]::TickCount + $timeoutMs
    while ([Environment]::TickCount -lt $deadline) {
        if ($proc.HasExited) { return $null }
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        try {
            $pipe.Connect(400)
            $reader = New-Object System.IO.StreamReader($pipe)
            $writer = New-Object System.IO.StreamWriter($pipe)
            $writer.NewLine = "`n"; $writer.AutoFlush = $true
            # Pending holds the single outstanding ReadLineAsync (a StreamReader
            # forbids two concurrent reads, so it is carried across calls).
            return @{ Pipe = $pipe; Reader = $reader; Writer = $writer; Pending = $null }
        } catch {
            $pipe.Dispose()
            Start-Sleep -Milliseconds 200
        }
    }
    return $null
}

# Read one line with a timeout, reusing the single pending async read so we never
# start a second concurrent ReadLineAsync (which a StreamReader rejects).
function Read-LineQueued($conn, $ms) {
    if ($null -eq $conn.Pending) { $conn.Pending = $conn.Reader.ReadLineAsync() }
    if ($conn.Pending.Wait([int][math]::Max(1, $ms))) {
        $line = $conn.Pending.Result
        $conn.Pending = $null
        return $line
    }
    return $null   # still pending; keep it for the next call
}

$script:reqId = 0
# Returns @{ ok; data; timeout; gone }. ok=$true only on a successful property read.
function Get-Prop($conn, $prop, $budgetMs) {
    $script:reqId++
    $id = $script:reqId
    $cmd = @{ command = @('get_property', $prop); request_id = $id } | ConvertTo-Json -Compress
    try { $conn.Writer.WriteLine($cmd) } catch { return @{ ok = $false; gone = $true } }
    $deadline = [Environment]::TickCount + $budgetMs
    while ([Environment]::TickCount -lt $deadline) {
        $line = Read-LineQueued $conn ($deadline - [Environment]::TickCount)
        if ($null -eq $line) { return @{ ok = $false; timeout = $true } }
        if ($line -notmatch '"request_id"') { continue }    # async event line, ignore
        $obj = $null; try { $obj = $line | ConvertFrom-Json } catch { continue }
        if ($obj.request_id -eq $id) { return @{ ok = ($obj.error -eq 'success'); data = $obj.data } }
    }
    return @{ ok = $false; timeout = $true }
}

# ============================ one timed run ============================
# Real zero-copy VO, fullscreen, uncapped (no vsync): measures the actual
# playback pipeline (decode + upscale + full present), unlike --vo=null which
# forces a 4K GPU->CPU readback and serializes it. Plays exactly $n frames then
# quits; the wall time is returned. On a built-engine run ($watchStall) an IPC
# time-pos watchdog runs alongside so a stall is caught and reported (Stalled)
# distinctly from a too-slow run (TimedOut). On a build-tolerant run
# ($watchStall=$false) the watchdog is OFF: the engine may still be building and
# the filter is playing passthrough meanwhile, so we just wait out the generous
# build budget and never mistake a legitimate first-time build for a freeze.
$script:pipeSeq = 0
function Invoke-MpvFrames($video, $vf, $n, $timeoutSec, $watchStall = $true) {
    $script:pipeSeq++
    $pipeName = "aji-bench-$PID-$($script:pipeSeq)"
    $flags = @(
        '--process-instance=multi', '--auto-load-folder=no', '--load-scripts=no',
        '--untimed', '--no-audio',
        '--vo=gpu-next', "--gpu-api=$gpuApi", '--force-window=immediate', '--fs',
        '--video-sync=display-desync', '--vulkan-swap-mode=immediate',
        '--d3d11-sync-interval=0', '--opengl-swapinterval=0',
        '--keep-open=no', '--idle=no', '--sid=no', "--hwdec=$hwdec",
        '--no-resume-playback', '--save-position-on-quit=no', '--start=0',
        "--input-ipc-server=\\.\pipe\$pipeName",
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

    $stalled = $false
    $timedOut = $false
    $conn = $null
    if (-not $watchStall) {
        # Build-tolerant: no stall watchdog, just wait out the (generous) budget.
        if (-not $p.WaitForExit([int]($timeoutSec * 1000))) { $timedOut = $true }
    } elseif (-not ($conn = Connect-Ipc $pipeName $p 8000)) {
        # IPC pipe could not be opened, so we cannot observe progress: fall back to
        # the timeout-only wait rather than false-stalling every run on the
        # never-produced-a-frame grace below. (mpv.net normally honors the pipe; this
        # only guards a future regression that breaks it.)
        if (-not $p.WaitForExit([int]($timeoutSec * 1000))) { $timedOut = $true }
    } else {
        $started = $false
        $anchorTp = 0.0                              # time-pos at the start of the current progress window
        $anchorMs = [Environment]::TickCount
        while ($true) {
            if ($p.WaitForExit(0)) { break }                                  # finished its $n frames
            if ($sw.Elapsed.TotalSeconds -ge $timeoutSec) { $timedOut = $true; break }
            # Froze before ever producing a frame (window opened then hung).
            if (-not $started -and $sw.Elapsed.TotalSeconds -ge $startGraceSec) { $stalled = $true; break }
            if ($conn) {
                $tp = Get-Prop $conn 'time-pos' 1200
                if ($tp.gone) { $conn = $null }
                elseif ($tp.ok -and $null -ne $tp.data) {
                    $cur = [double]$tp.data
                    if (-not $started) { $started = $true; $anchorTp = $cur; $anchorMs = [Environment]::TickCount }
                    elseif (($cur - $anchorTp) -ge $minProgressSec) { $anchorTp = $cur; $anchorMs = [Environment]::TickCount }
                }
            }
            # Progress check, every poll once started - NOT gated on a successful read,
            # so it catches a stuck clock, a creeping clock, and a hard freeze that
            # wedges IPC (no new value arrives, so the anchor never advances).
            if ($started -and (([Environment]::TickCount - $anchorMs) -ge $stallMs)) {
                $paused = $false
                if ($conn) { $pz = Get-Prop $conn 'pause' 800; $paused = ($pz.ok -and $pz.data) }
                if ($paused) { $anchorMs = [Environment]::TickCount }   # genuinely paused (not expected, scripts off)
                else { $stalled = $true; break }
            }
            Start-Sleep -Milliseconds $pollMs
        }
    }
    $sw.Stop()

    if (-not $p.HasExited) {
        # Kill the player and any child (e.g. a trtexec build) by tree.
        $eap = $ErrorActionPreference; $ErrorActionPreference = 'SilentlyContinue'
        & taskkill /T /F /PID $p.Id *> $null
        $ErrorActionPreference = $eap
        [void]$p.WaitForExit(5000)
    }
    if ($conn) { try { $conn.Pipe.Dispose() } catch {} }

    return [pscustomobject]@{
        Seconds  = $sw.Elapsed.TotalSeconds
        TimedOut = $timedOut
        Stalled  = $stalled
    }
}

# Two-point measurement for one cell: a probe run sizes the window run, then the
# frame/time difference cancels fixed startup cost (fps = frames / time delta).
#
# The engine build is decoupled from the "too slow to play" decision. When the
# engine is not yet cached (the harness pre-build was missing/failed), attempt 1
# may still be building, so it runs with the generous build budget AND with the
# stall watchdog OFF - a long build, which plays passthrough meanwhile, never
# trips the playback timeout nor the freeze watchdog. If the build lands in the
# probe the fewer-frame probe ends up slower than the cached-engine window
# (dt <= 0); if it lands in the window it deflates attempt 1's fps. Either way the
# engine is cached afterward, so we retry: attempt 2 runs entirely on the cached
# engine with the tight fps-floor timeout and the watchdog armed, and *that* is
# what decides the real playback rate (and a skip). When $engineReady (the normal
# path: harness pre-built it, or DirectML which builds nothing) every run is a
# cached-engine run - tight timeout, watchdog armed - so a slow GPU is skipped and
# a freeze is caught immediately.
#
# Returns Status = ok (with Fps) | skip (cached-engine playback below the floor) |
# stall (a run's time-pos froze - the caller retries the whole cell) |
# anomaly (dt still non-positive after the cached-engine retry).
function Measure-Cell($video, $vf, $engineReady) {
    $dt = 0
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        # A build can only land on attempt 1, and only if the engine wasn't ready.
        # Such a build pass gets the generous budget and no watchdog; every other
        # run is a cached-engine run (tight timeout, watchdog armed).
        $buildPass = (-not $engineReady) -and ($attempt -eq 1)
        $probeTimeout = if ($buildPass) { $buildAllowSec } else { Get-RunTimeout $probeFrames }

        $probe = Invoke-MpvFrames $video $vf $probeFrames $probeTimeout (-not $buildPass)
        if ($probe.Stalled)  { return [pscustomobject]@{ Status = 'stall' } }
        if ($probe.TimedOut) { return [pscustomobject]@{ Status = 'skip' } }

        # Size the window to ~$windowSeconds at the probed rate (probe time includes
        # startup, so this slightly under-shoots - fine). Floor the gap for accuracy,
        # cap so the run fits one clip pass.
        $fpsEst = $probeFrames / [math]::Max($probe.Seconds, 0.001)
        $windowFrames = [math]::Max([int]($fpsEst * $windowSeconds), $minWindowFrames)
        $windowFrames = [math]::Min($windowFrames, $maxClipFrames - $probeFrames)
        $highFrames = $probeFrames + $windowFrames

        $highTimeout = if ($buildPass) { $buildAllowSec } else { Get-RunTimeout $highFrames }
        $high = Invoke-MpvFrames $video $vf $highFrames $highTimeout (-not $buildPass)
        if ($high.Stalled)  { return [pscustomobject]@{ Status = 'stall' } }
        if ($high.TimedOut) { return [pscustomobject]@{ Status = 'skip' } }

        $dt = $high.Seconds - $probe.Seconds
        # Build landed in the probe (slower than the cached-engine window) -> retry.
        if ($dt -le 0) { continue }

        $fps = [math]::Round(($highFrames - $probeFrames) / $dt, 1)
        if ($ShowTiming) { Write-Host -NoNewline ("[probe {0:n1}s/{1}f  window {2:n1}s/{3}f] " -f $probe.Seconds, $probeFrames, $high.Seconds, $highFrames) -ForegroundColor DarkGray }
        # A build that landed in the window run deflates attempt 1's fps; if the
        # first pass looks below the floor, re-measure on the cached engine before
        # trusting it. The cached-engine pass is the authoritative read.
        if ($fps -lt $fpsFloor -and $buildPass) { continue }
        if ($fps -lt $fpsFloor) { return [pscustomobject]@{ Status = 'skip' } }
        return [pscustomobject]@{ Status = 'ok'; Fps = $fps }
    }
    return [pscustomobject]@{ Status = 'anomaly'; Dt = $dt }
}

# Pre-build one slot+resolution's TensorRT engine via the harness. The harness
# builds synchronously - it blocks until the engine is built and cached, unlike
# the player's async (passthrough-while-building) path - and the player reuses
# the same on-disk cache (same aji_trt, conf, model, slot and resolution => same
# cache key). So after this, the timed mpv runs land on a warm engine and never
# build mid-measurement. No-op on DirectML (no engine build). Best-effort: any
# failure is swallowed - the mpv warmup + Measure-Cell retry still cover it.
#
# Returns $true if the engine is now cached and ready (so the timed runs can use
# the tight timeout + freeze watchdog), $false if the build was missing/failed (so
# the warmup/probe stay build-tolerant: generous budget, watchdog off).
$buildLog = Join-Path $root 'benchmark-build.log'
function Invoke-Build($w, $h, $slot) {
    if (-not $buildsEngines) { return $true }
    try {
        $harness = Join-Path $root 'inference\aji_harness.exe'
        if (-not (Test-Path $harness)) {
            Write-Host -NoNewline "[no harness] " -ForegroundColor DarkGray
            return $false
        }
        # Prefer a real seed frame; fall back to a zero nv12 buffer (Y + CbCr/2).
        # Content is irrelevant - the build depends only on the model and the WxH
        # input shape, not the pixels.
        $dummy = Join-Path $PSScriptRoot "seeds\${w}x${h}.raw"
        if (-not (Test-Path $dummy)) {
            $dummy = Join-Path $clipRoot 'warm.raw'
            $size = [int]([long]$w * [long]$h * 3 / 2)
            [System.IO.File]::WriteAllBytes($dummy, [byte[]]::new($size))
        }
        $flags = @(
            '--input', $dummy, '--width', "$w", '--height', "$h", '--frames', '1',
            '--conf', $conf, '--model-dir', (Join-Path $root 'onnx'),
            '--rife-model-dir', (Join-Path $root 'rife'),
            '--trtexec', (Join-Path $root 'inference\trtexec.exe'), '--slot', "$slot"
        )
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $harness
        # Quote any arg with spaces (install paths can); the harness takes plain argv.
        $psi.Arguments = (($flags | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $p = [System.Diagnostics.Process]::Start($psi)
        $outTask = $p.StandardOutput.ReadToEndAsync()
        $errTask = $p.StandardError.ReadToEndAsync()
        $exited = $p.WaitForExit([int]($buildAllowSec * 1000))
        if (-not $exited) {
            $eap = $ErrorActionPreference; $ErrorActionPreference = 'SilentlyContinue'
            & taskkill /T /F /PID $p.Id *> $null
            $ErrorActionPreference = $eap
            [void]$p.WaitForExit(5000)
        }
        # Log the harness output so a failed/mismatched build is diagnosable
        # instead of silently leaving the timed runs in passthrough.
        $code = try { $p.ExitCode } catch { 'killed' }
        Add-Content $buildLog ("=== slot $slot ${w}x${h}  exit=$code  cmd: " + $psi.Arguments + "`n" +
                               $outTask.Result + $errTask.Result + "`n")
        if ($code -ne 0) { Write-Host -NoNewline "[build exit=$code] " -ForegroundColor DarkYellow }
        return ($code -eq 0)
    } catch {
        Add-Content $buildLog "=== slot $slot ${w}x${h}  PS error: $_`n"
        Write-Host -NoNewline "[build err] " -ForegroundColor DarkYellow
        return $false
    }
}

$table = @{}
foreach ($name in $slots.Keys) { $table[$name] = [ordered]@{} }

# Once a template is too slow at one resolution, every larger resolution for it
# is skipped without running (it can only be slower). Keyed by template name.
$skipRest = @{}

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
        # A smaller resolution for this template already fell below the floor;
        # this larger one can only be slower, so record it without running.
        if ($skipRest[$name]) {
            $table[$name][$res] = -1
            Write-Host "skipped (lower resolution already too slow)" -ForegroundColor DarkYellow
            continue
        }

        # Whole-cell retry loop: a STALL (transient gpu-next/present freeze, caught
        # by the time-pos watchdog) re-runs the cell instead of recording it. Only a
        # genuine timeout/below-floor result marks the cell (and the larger ones) slow.
        $done = $false
        $engineReady = (-not $buildsEngines)   # DirectML/NCNN: nothing to build
        for ($cellAttempt = 1; $cellAttempt -le $cellRetries -and -not $done; $cellAttempt++) {
            try {
                # Pre-build this slot+resolution's TensorRT engine synchronously via the
                # harness so the timed mpv runs land on a warm cache (identical engine:
                # same aji_trt + conf + model-dir + slot + WxH => same cache key). $engineReady
                # then drives the warmup/probe budget + watchdog (ready => tight + armed;
                # not ready => build-tolerant). Only needed once (cached after attempt 1).
                if ($cellAttempt -eq 1 -and $buildsEngines) {
                    $wh = $res -split 'x'
                    $engineReady = Invoke-Build $wh[0] $wh[1] $slots[$name]
                }

                # Warmup fills the pipeline and warms GPU clocks. If the engine is ready it
                # runs cached (tight timeout, freeze watchdog armed); if not, it stays build-
                # tolerant (generous budget, watchdog off) and builds the engine as a fallback.
                $warmTimeout = if ($engineReady) { Get-RunTimeout $warmupFrames } else { $buildAllowSec }
                $warm = Invoke-MpvFrames $video $vf $warmupFrames $warmTimeout $engineReady
                if ($warm.Stalled) {
                    Write-Host -NoNewline "[stall, retry] " -ForegroundColor DarkCyan
                    continue
                }
                if ($warm.TimedOut) {
                    $table[$name][$res] = -1   # sentinel: skipped, ran too slow (catalog renders "-")
                    $skipRest[$name] = $true   # every larger resolution for this template is slower too
                    Write-Host "skipped (under $fpsFloor fps)" -ForegroundColor DarkYellow
                    $done = $true
                    continue
                }
                # The warmup completed, so the engine is now cached regardless of how it
                # got there - the measured runs are all cached-engine runs.
                $engineReady = $true

                # Probe + window, retrying once internally if the engine build lands in
                # the probe (see Measure-Cell).
                $r = Measure-Cell $video $vf $engineReady
                if ($r.Status -eq 'stall') {
                    Write-Host -NoNewline "[stall, retry] " -ForegroundColor DarkCyan
                    continue
                } elseif ($r.Status -eq 'skip') {
                    $table[$name][$res] = -1   # sentinel: skipped, ran too slow (catalog renders "-")
                    $skipRest[$name] = $true   # every larger resolution for this template is slower too
                    Write-Host "skipped (under $fpsFloor fps)" -ForegroundColor DarkYellow
                } elseif ($r.Status -eq 'anomaly') {
                    throw "timing anomaly (dt=$([math]::Round($r.Dt, 3))s)"
                } else {
                    $table[$name][$res] = $r.Fps
                    Write-Host "$($r.Fps) fps" -ForegroundColor Green
                }
                $done = $true
            } catch {
                $table[$name][$res] = ""
                Write-Host "failed: $_" -ForegroundColor Red
                $done = $true
            }
        }
        if (-not $done) {
            # Every attempt stalled - a real, repeatable freeze on this cell. Record it
            # as a failure (blank), NOT as -1, and do NOT poison the larger resolutions.
            $table[$name][$res] = ""
            Write-Host "failed: repeated stalls (mpv froze on every attempt)" -ForegroundColor Red
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
