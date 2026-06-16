# AnimeJaNai playback benchmark driver.
#
# Measures the throughput of the real mpv pipeline for the built-in benchmark
# templates: Balanced (slot 1010) and Performance (slot 1011) across the bundled
# source clips, plus an Off/no-upscale reference at 1920x1080. This is the fps that
# tells you whether your hardware can upscale each resolution faster than real time,
# and how much headroom remains above a no-upscale baseline.
#
# Launched by animejanai_benchmark_all.bat (the Manager's Run Benchmarks button).
# It runs OFFSCREEN (--vo=null), so there is no window and nothing to click - it
# just churns through the clips.
#
# Method: each cell runs mpvnet.com uncapped (--untimed) with --vo=null - decode +
# the upscale filter, with NO on-screen render/present. That is deliberate: the
# present path (gpu-next + the Windows compositor) adds a per-frame cost that
# depends on the monitor's refresh rate and, on slower GPUs, can even mask the
# Balanced-vs-Performance difference, making the numbers non-comparable across
# machines. --vo=null removes it, giving a refresh-independent, comparable measure
# of the GPU's decode+upscale capability. (It omits the gpu-next render
# post-processing - deband/shaders - a smaller, machine-comparable cost.)
#
# A warmup run builds/loads the TensorRT engine and warms GPU clocks; the timed run
# then samples mpv's estimated-frame-number over a short active-playback window and
# computes fps from the frame delta over wall time (so process launch is excluded),
# with the source looped so very fast vo=null cells cannot race to EOF before the
# sampler attaches. Sampling active playback (instead of the old --frames +
# two-point wall-clock over a ~20 s window) is what makes this several times faster.
# Each cell is killed if it can't sustain $fpsFloor fps and recorded as -1 (the
# catalog's red "-"), so hopelessly slow cells are skipped fast.
#
# Reliability: every run uses --load-scripts=no (the player's lua scripts - the
# engine-build monitor that PAUSES playback, the update checker - would otherwise
# stall or skew a timed run) and --cache=no (no mid-run cache pause). There is no
# present, so none of the gpu-next freeze/teardown hazards apply; the only liveness
# check is that estimated-frame-number keeps advancing (a wedged run is killed and
# the cell retried).

param(
    # Validation helpers (no effect on a normal Manager run):
    [switch]$Quick,        # only the two smallest resolutions, for a fast sanity check
    [string]$OnlyRes = '', # e.g. -OnlyRes 1280x720 to run a single cell column
    [switch]$ShowTiming,   # print per-sample timing detail
    [string]$MpvInstallRoot = '' # optional install root when running this file from outside animejanai\benchmarks
)

$ErrorActionPreference = "Stop"
$scriptDir   = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$benchRoot   = $scriptDir                             # animejanai/benchmarks/
$root        = Split-Path -Parent $benchRoot          # animejanai/
$installRoot = Split-Path -Parent $root               # install root (mpvnet.com is here)

if ($MpvInstallRoot) {
    $installRoot = (Resolve-Path -LiteralPath $MpvInstallRoot).Path
    $root        = Join-Path $installRoot "animejanai"
    $benchRoot   = Join-Path $root "benchmarks"
} else {
    $localMpvnet = Join-Path $installRoot "mpvnet.com"
    $localClips  = @(Get-ChildItem -LiteralPath $benchRoot -Filter "*.mp4" -ErrorAction SilentlyContinue)
    if (-not (Test-Path $localMpvnet) -or $localClips.Count -eq 0) {
        $fallbackInstall = Join-Path $env:LOCALAPPDATA "Programs\mpv-AnimeJaNai"
        $fallbackBench   = Join-Path $fallbackInstall "animejanai\benchmarks"
        if ((Test-Path (Join-Path $fallbackInstall "mpvnet.com")) -and (Test-Path $fallbackBench)) {
            $installRoot = $fallbackInstall
            $root        = Join-Path $installRoot "animejanai"
            $benchRoot   = $fallbackBench
        }
    }
}

$conf        = Join-Path $root "animejanai.conf"
$mpvConf     = Join-Path $installRoot "portable_config\mpv-animejanai.conf"
$mpvnet      = Join-Path $installRoot "mpvnet.com"

if (-not (Test-Path $mpvnet)) {
    Write-Host "mpvnet.com not found at $mpvnet" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $benchRoot)) {
    Write-Host "Benchmark clips folder not found at $benchRoot" -ForegroundColor Red
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
# No --gpu-api: with --vo=null there is no render context. The aji filter still runs
# its own TensorRT/DirectML inference on the decoded frames the decoder hands it.

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
Write-Host "Runs offscreen (no window). Just let it churn through the clips."
Write-Host "(TensorRT builds an engine per resolution on the first run, about a"
Write-Host " minute each and cached afterward; the full sweep takes a few minutes,"
Write-Host " longer on slower GPUs. Cells too slow to be usable, under $fpsFloor fps,"
Write-Host " are skipped and recorded as -1 (shown as '-' in the catalog).)"
Write-Host ""

$slots = [ordered]@{ "Balanced" = 1010; "Performance" = 1011; "Off" = 0 }
# Ascending by pixel count: a larger input is always more work for the model, so
# once a template is too slow at one resolution every larger one is too (used to
# short-circuit the rest below).
$resolutions = Get-ChildItem -LiteralPath $benchRoot -Filter "*.mp4" | ForEach-Object {
    $_.BaseName
} | Sort-Object { $p = $_ -split 'x'; [int]$p[0] * [int]$p[1] }

# Validation shortcuts (Quick / OnlyRes) trim the resolution set; a normal run uses all.
if ($OnlyRes) { $resolutions = @($resolutions | Where-Object { $_ -eq $OnlyRes }) }
elseif ($Quick) { $resolutions = @($resolutions | Select-Object -First 2) }
if (-not $resolutions -or $resolutions.Count -eq 0) {
    Write-Host "No matching resolution clips found." -ForegroundColor Red
    exit 1
}

# Sampling. The timed run does not use --frames or graceful EOF: it loops the source,
# samples estimated-frame-number while playback is active, then kills mpv. A fast
# cell stops at sampleTargetFrames or sampleMaxSec; a slow cell keeps going to
# slowSampleMaxSec so it still gathers enough frames for a confident (below-floor)
# fps instead of a "too few frames" blank.
$warmupFrames       = 120
$sampleTargetFrames = 1200
$sampleMinSec       = 0.25
$sampleMaxSec       = 4.0
$slowSampleMaxSec   = 12
$sampleTimeoutSec   = 15
$offSampleTargetFrames = 1000000        # no-upscale is fast enough to race short samples; use a fixed window
$offSampleMinSec       = 2.0
$offSampleMaxSec       = 2.0
$offSlowSampleMaxSec   = 2.0
$offSampleTimeoutSec   = 8
$sampleStartSecs    = @(0, 10, 20, 35)   # varied clip positions reduce content/startup bias across retries
$minGoodSamples     = 2                  # median-of-2 minimum for stability
$maxGoodSamples     = 3                  # take a third sample only when the first two disagree
$sampleSpreadLimit  = 0.08               # relative spread allowed before taking the third sample
$startupGraceSec    = 8                  # no first frame by here -> the run is wedged
$progressStallSec   = 3.0                # no frame progress this long -> wedged (>2s so ~0.3-0.5 fps isn't misread)
$pollSleepMs        = 25                 # short so fast vo=null cells get multiple samples across loop wraps
$postRunCooldownMs  = 750

# Skip a cell once it can't sustain this many fps - well below the ~24 fps real-time
# bar, so the exact number doesn't matter and isn't worth the wait. Raising it skips
# more aggressively; lowering it measures slower hardware.
$fpsFloor        = 6
$decodeInitSec   = 12    # per-run startup allowance (decode/session init)
$buildAllowSec   = 300   # warmup only: also covers a first-time TensorRT engine build
$cellRetries     = 3     # whole-cell attempts before giving up (a transient sample failure)

# A run's overall timeout. Sample runs use $sampleTimeoutSec; a build-tolerant warmup
# gets the generous build budget. (Kept for parity with the harness build budget.)
function Get-RunTimeout($frames) { [int]($decodeInitSec + $frames / $fpsFloor) }

# TensorRT builds an engine on first use (one-time, can take minutes); DirectML and
# NCNN never build. So an engine build runs on the generous budget and a slow *build*
# is never read as "too slow to play" - only cached-engine sampling decides that.
$buildsEngines = -not ($backend -match '^(?i:directml|ncnn)$')

# mpvnet.com is the only bundled player executable in the current install. It hosts
# libmpv in a WPF shell, so --vo=null prevents video presentation but mpv.net can
# still briefly create an app window. Ask Windows to keep that process hidden too.
if (-not ('BenchWin32.Native' -as [type])) {
    Add-Type -Namespace BenchWin32 -Name Native -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool ShowWindowAsync(System.IntPtr hWnd, int nCmdShow);
"@
}
if (-not ('BenchWin32.JobNative' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace BenchWin32 {
    public static class JobNative {
        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public IntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}
"@
}
$script:benchmarkJob = [IntPtr]::Zero
function New-KillOnCloseJob() {
    $job = [BenchWin32.JobNative]::CreateJobObject([IntPtr]::Zero, $null)
    if ($job -eq [IntPtr]::Zero) { return [IntPtr]::Zero }

    $info = New-Object BenchWin32.JobNative+JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    $info.BasicLimitInformation.LimitFlags = 0x2000 # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    $size = [System.Runtime.InteropServices.Marshal]::SizeOf($info)
    $ptr = [System.Runtime.InteropServices.Marshal]::AllocHGlobal($size)
    try {
        [System.Runtime.InteropServices.Marshal]::StructureToPtr($info, $ptr, $false)
        if (-not [BenchWin32.JobNative]::SetInformationJobObject($job, 9, $ptr, [uint32]$size)) {
            [void][BenchWin32.JobNative]::CloseHandle($job)
            return [IntPtr]::Zero
        }
        return $job
    } finally {
        [System.Runtime.InteropServices.Marshal]::FreeHGlobal($ptr)
    }
}
$script:benchmarkJob = New-KillOnCloseJob
function Start-BenchmarkWatchdog($targetPid) {
    try {
        $watchdog = @"
`$parentPid = $PID
`$targetPid = $targetPid
while (`$true) {
    `$target = Get-Process -Id `$targetPid -ErrorAction SilentlyContinue
    if (-not `$target) { exit 0 }
    `$parent = Get-Process -Id `$parentPid -ErrorAction SilentlyContinue
    if (-not `$parent) {
        `$eap = `$ErrorActionPreference
        `$ErrorActionPreference = 'SilentlyContinue'
        & taskkill /T /F /PID `$targetPid *> `$null
        `$ErrorActionPreference = `$eap
        exit 0
    }
    Start-Sleep -Milliseconds 500
}
"@
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($watchdog))
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = "powershell.exe"
        $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand $encoded"
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        [void][System.Diagnostics.Process]::Start($psi)
    } catch {}
}
function Register-BenchmarkProcess($proc) {
    if ($null -eq $proc) { return }
    try {
        if ($script:benchmarkJob -ne [IntPtr]::Zero) {
            [void][BenchWin32.JobNative]::AssignProcessToJobObject($script:benchmarkJob, $proc.Handle)
        }
    } catch {}
    Start-BenchmarkWatchdog $proc.Id
}
function Hide-PlayerWindow($proc) {
    try {
        if ($null -eq $proc) { return }
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) {
            [void][BenchWin32.Native]::ShowWindowAsync($proc.MainWindowHandle, 0)
        }
    } catch {}
}

# ============================ IPC helpers ============================
# mpv's JSON IPC over a Windows named pipe. Used to read estimated-frame-number while
# playback is active (the fps measurement) and to notice if frame progress stalls.
function Connect-Ipc($pipeName, $proc, $timeoutMs) {
    $deadline = [Environment]::TickCount + $timeoutMs
    while ([Environment]::TickCount -lt $deadline) {
        $proc.Refresh()
        if ($proc.HasExited) { return $null }
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        try {
            $pipe.Connect(400)
            $reader = New-Object System.IO.StreamReader($pipe)
            $writer = New-Object System.IO.StreamWriter($pipe)
            $writer.NewLine = "`n"; $writer.AutoFlush = $true
            return @{ Pipe = $pipe; Reader = $reader; Writer = $writer; Pending = $null }
        } catch {
            $pipe.Dispose()
            Start-Sleep -Milliseconds 100
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
    return $null
}

$script:reqId = 0
# Returns @{ ok; data; gone }. ok=$true only on a successful property read.
function Get-Prop($conn, $prop, $budgetMs) {
    $script:reqId++
    $id = $script:reqId
    $cmd = @{ command = @('get_property', $prop); request_id = $id } | ConvertTo-Json -Compress
    try { $conn.Writer.WriteLine($cmd) } catch { return @{ ok = $false; gone = $true } }
    $deadline = [Environment]::TickCount + $budgetMs
    while ([Environment]::TickCount -lt $deadline) {
        $line = Read-LineQueued $conn ($deadline - [Environment]::TickCount)
        if ($null -eq $line) { return @{ ok = $false } }
        if ($line -notmatch '"request_id"') { continue }   # async event line, ignore
        $obj = $null; try { $obj = $line | ConvertFrom-Json } catch { continue }
        if ($obj.request_id -eq $id) { return @{ ok = ($obj.error -eq 'success'); data = $obj.data } }
    }
    return @{ ok = $false }
}

function Get-Median($values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0 }
    $mid = [int][math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return [double]$sorted[$mid] }
    return ([double]$sorted[$mid - 1] + [double]$sorted[$mid]) / 2.0
}

function Get-RelativeSpread($values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -lt 2) { return 0 }
    $median = Get-Median $sorted
    if ($median -le 0) { return 1 }
    return ([double]$sorted[-1] - [double]$sorted[0]) / $median
}

# ============================ one timed run ============================
# Launches mpv (--vo=null, uncapped), samples estimated-frame-number over a short
# window mid-playback, then kills it. Returns the measured frames/seconds/fps and a
# TimedOut flag (no first frame, a progress stall, or too few frames sampled).
$script:pipeSeq = 0
function Invoke-MpvFrames($video, $vf, $n, $timeoutSec, $startSec = 0, $minSec = $sampleMinSec, $maxSec = $sampleMaxSec, $slowMaxSec = $slowSampleMaxSec) {
    $script:pipeSeq++
    $pipeName = "aji-bench-$PID-$($script:pipeSeq)"
    $flags = @(
        '--process-instance=multi', '--auto-load-folder=no', '--load-scripts=no',
        '--untimed', '--no-audio', '--loop-file=inf',
        '--vo=null', '--force-window=no', '--window-minimized=yes',
        '--keep-open=no', '--idle=no', '--sid=no', "--hwdec=$hwdec",
        '--cache=no', '--cache-pause=no', '--cache-pause-initial=no', '--demuxer-cache-wait=no',
        '--no-resume-playback', '--save-position-on-quit=no', "--start=$startSec",
        "--input-ipc-server=\\.\pipe\$pipeName",
        "--vf=$vf"
    )
    # Every flag is space-free; only the clip path needs quoting and -- guards it.
    # Building the command line by hand (vs Start-Process -ArgumentList) avoids the
    # mangling of spaced/Unicode paths into a stdin '-', while still giving a Process
    # handle we can time out and kill. Windows PowerShell 5.1 has no
    # ProcessStartInfo.ArgumentList, hence the explicit string.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $mpvnet
    $psi.Arguments              = ($flags -join ' ') + ' -- "' + $video + '"'
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true
    $psi.WindowStyle            = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p  = [System.Diagnostics.Process]::Start($psi)
    Register-BenchmarkProcess $p
    try { [void]$p.WaitForInputIdle(1000) } catch {}
    Hide-PlayerWindow $p
    # Drain both pipes async so a chatty player can't deadlock on a full buffer while
    # we wait (output is discarded). Using a Process instead of the call operator also
    # means native stderr no longer trips ErrorActionPreference.
    [void]$p.StandardOutput.ReadToEndAsync()
    [void]$p.StandardError.ReadToEndAsync()

    $timedOut = $false
    $timeoutReason = ''
    $conn = Connect-Ipc $pipeName $p 8000
    $firstFrame = $null; $lastFrame = $null; $previousFrame = $null
    $estimatedFrameCount = $null; $advancedFrames = 0
    $firstSec = $null; $lastSec = $null; $lastProgressSec = $null

    if (-not $conn) {
        $timedOut = $true
        $timeoutReason = 'no-ipc'
    } else {
        $countProp = Get-Prop $conn 'estimated-frame-count' 500
        if ($countProp.ok -and $null -ne $countProp.data -and [double]$countProp.data -gt 0) {
            $estimatedFrameCount = [double]$countProp.data
        }
        while ($true) {
            if ($timedOut) { break }
            Hide-PlayerWindow $p
            $p.Refresh()
            if ($p.HasExited) {
                if ($null -ne $firstFrame) { $lastSec = $sw.Elapsed.TotalSeconds }
                break
            }
            if ($sw.Elapsed.TotalSeconds -ge $timeoutSec) { $timedOut = $true; $timeoutReason = 'timeout'; break }
            # Froze before ever producing a frame.
            if ($null -eq $firstFrame -and $sw.Elapsed.TotalSeconds -ge $startupGraceSec) { $timedOut = $true; $timeoutReason = 'no-first-frame'; break }

            $r = Get-Prop $conn 'estimated-frame-number' 800
            # mpv or its IPC pipe exited. That is not automatically a failure: keep
            # what we sampled, and let the frames/seconds checks below decide whether
            # it was a usable short sample or a real early death.
            if ($r.gone) {
                if ($null -ne $firstFrame) { $lastSec = $sw.Elapsed.TotalSeconds }
                break
            }
            if ($r.ok -and $null -ne $r.data) {
                $frame = [double]$r.data
                $now = $sw.Elapsed.TotalSeconds
                if ($null -eq $firstFrame) {
                    $firstFrame = $frame; $firstSec = $now; $lastProgressSec = $now
                    $previousFrame = $frame
                } else {
                    $delta = 0
                    if ($frame -ge $previousFrame) {
                        $delta = $frame - $previousFrame
                    } elseif ($null -ne $estimatedFrameCount -and $estimatedFrameCount -gt $previousFrame) {
                        $delta = ($estimatedFrameCount - $previousFrame) + $frame
                    } else {
                        # Loop wrap fallback when mpv cannot report total frames. Polling
                        # is frequent, so the uncounted tail is small and only affects
                        # clips that wrap inside the short sample window.
                        $delta = $frame
                    }
                    if ($delta -gt 0) {
                        $advancedFrames += $delta
                        $lastProgressSec = $now
                    }
                    $previousFrame = $frame
                }
                $lastFrame = $frame; $lastSec = $now

                $elapsed = $lastSec - $firstSec
                $advanced = $advancedFrames
                # Fast cell: stop at the target frame count or sampleMaxSec. Slow cell:
                # keep sampling to slowSampleMaxSec so it accumulates enough frames for a
                # confident (below-floor) fps rather than a "too few frames" blank.
                if ($advanced -gt 0 -and $elapsed -ge $minSec -and `
                    ($advanced -ge $n -or ($advanced -ge 10 -and $elapsed -ge $maxSec) -or $elapsed -ge $slowMaxSec)) {
                    break
                }
            }
            # No frame progress for a while = a wedged run (not just slow).
            if ($null -ne $lastProgressSec -and (($sw.Elapsed.TotalSeconds - $lastProgressSec) -ge $progressStallSec)) {
                $timedOut = $true
                $timeoutReason = 'stall'
                break
            }
            Start-Sleep -Milliseconds $pollSleepMs
        }
    }
    $p.Refresh()
    $sw.Stop()

    # Kill the looped player instead of waiting for graceful teardown (and kill any
    # child, e.g. a trtexec build) by tree.
    if (-not $p.HasExited) {
        $eap = $ErrorActionPreference; $ErrorActionPreference = 'SilentlyContinue'
        & taskkill /T /F /PID $p.Id *> $null
        $ErrorActionPreference = $eap
        [void]$p.WaitForExit(5000)
    }
    if ($conn) { try { $conn.Pipe.Dispose() } catch {} }
    Start-Sleep -Milliseconds $postRunCooldownMs

    $frames = 0; $seconds = 0
    if ($null -ne $firstFrame -and $null -ne $lastFrame -and $null -ne $firstSec -and $null -ne $lastSec) {
        $frames = [math]::Max(0, $advancedFrames)
        $seconds = [math]::Max(0, $lastSec - $firstSec)
        if ($frames -ge 3 -and $seconds -le 0) {
            $seconds = [math]::Max(0, $sw.Elapsed.TotalSeconds - $firstSec)
        }
    }
    # A few frames over a few seconds is enough to decide "below the fps floor" (a
    # skip); only essentially-no-progress is a failed/wedged run.
    if ($frames -lt 3 -or $seconds -le 0) {
        $timedOut = $true
        if (-not $timeoutReason) { $timeoutReason = 'too-few-frames' }
    }

    return [pscustomobject]@{
        Seconds  = $seconds
        Frames   = $frames
        Fps      = if ($seconds -gt 0) { [math]::Round($frames / $seconds, 1) } else { 0 }
        TimedOut = $timedOut
        Reason   = $timeoutReason
    }
}

# Measure one cell from active-playback progress. Startup and teardown are excluded
# (the sample clock starts with the first observed frame and mpv is killed after the
# short active window instead of being allowed to reach graceful EOF/teardown).
# Retries at a few clip positions if a sample fails. Returns Status = ok | skip | fail.
function Measure-Cell($video, $vf, $engineReady, $targetFrames = $sampleTargetFrames, $minSec = $sampleMinSec, $maxSec = $sampleMaxSec, $slowMaxSec = $slowSampleMaxSec, $runTimeoutSec = $sampleTimeoutSec) {
    $good = @()
    for ($attempt = 1; $attempt -le $sampleStartSecs.Count; $attempt++) {
        # A build can only land on attempt 1, and only if the engine wasn't ready;
        # that pass gets the generous budget, every other run the normal sample timeout.
        $buildPass = (-not $engineReady) -and ($attempt -eq 1)
        $timeout = if ($buildPass) { $buildAllowSec } else { $runTimeoutSec }
        $startAt = $sampleStartSecs[$attempt - 1]

        $sample = Invoke-MpvFrames $video $vf $targetFrames $timeout $startAt $minSec $maxSec $slowMaxSec
        if ($sample.TimedOut) {
            if ($ShowTiming) {
                Write-Host -NoNewline ("[sample +{0}s {1} {2:n4}s/{3:n0}f {4:n1}fps] " -f $startAt, $sample.Reason, $sample.Seconds, $sample.Frames, $sample.Fps) -ForegroundColor DarkYellow
            }
            continue
        }

        if ($ShowTiming) {
            Write-Host -NoNewline ("[sample +{0}s {1:n1}s/{2:n0}f {3:n1}fps] " -f $startAt, $sample.Seconds, $sample.Frames, $sample.Fps) -ForegroundColor DarkGray
        }
        if ($sample.Fps -lt $fpsFloor -and $buildPass) { continue }   # a build deflated it; re-measure cached
        if ($sample.Fps -lt $fpsFloor) { return [pscustomobject]@{ Status = 'skip' } }

        $good += [double]$sample.Fps
        if ($good.Count -lt $minGoodSamples) { continue }

        $spread = Get-RelativeSpread $good
        if ($spread -le $sampleSpreadLimit -or $good.Count -ge $maxGoodSamples) {
            return [pscustomobject]@{
                Status = 'ok'
                Fps    = [math]::Round((Get-Median $good), 1)
                Spread = $spread
                Samples = $good.Count
            }
        }
    }
    if ($good.Count -gt 0) {
        return [pscustomobject]@{
            Status = 'ok'
            Fps    = [math]::Round((Get-Median $good), 1)
            Spread = Get-RelativeSpread $good
            Samples = $good.Count
        }
    }
    return [pscustomobject]@{ Status = 'fail' }
}

# Pre-build one slot+resolution's TensorRT engine via the harness. The harness builds
# synchronously - it blocks until the engine is built and cached, unlike the player's
# async (passthrough-while-building) path - and the player reuses the same on-disk
# cache (same aji_trt, conf, model, slot and resolution => same cache key). So after
# this, the timed mpv runs land on a warm engine and never build mid-measurement.
# No-op on DirectML (no engine build). Best-effort: failures are swallowed (the warmup
# + Measure-Cell retries still cover it). Returns $true if the engine is now cached.
$buildLog = Join-Path $root 'benchmark-build.log'
function Invoke-Build($w, $h, $slot) {
    if (-not $buildsEngines) { return $true }
    try {
        $harness = Join-Path $root 'inference\aji_harness.exe'
        if (-not (Test-Path $harness)) {
            Write-Host -NoNewline "[no harness] " -ForegroundColor DarkGray
            return $false
        }
        # nv12 dummy frame (Y + CbCr/2); content is irrelevant - the build depends only
        # on the model and the WxH input shape, not the pixels.
        $dummy = Join-Path $clipRoot 'warm.raw'
        $size = [int]([long]$w * [long]$h * 3 / 2)
        [System.IO.File]::WriteAllBytes($dummy, [byte[]]::new($size))
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
        Register-BenchmarkProcess $p
        $outTask = $p.StandardOutput.ReadToEndAsync()
        $errTask = $p.StandardError.ReadToEndAsync()
        $exited = $p.WaitForExit([int]($buildAllowSec * 1000))
        if (-not $exited) {
            $eap = $ErrorActionPreference; $ErrorActionPreference = 'SilentlyContinue'
            & taskkill /T /F /PID $p.Id *> $null
            $ErrorActionPreference = $eap
            [void]$p.WaitForExit(5000)
        }
        # Log the harness output so a failed/mismatched build is diagnosable.
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

# Once a template is too slow at one resolution, every larger resolution for it is
# skipped without running (it can only be slower). Keyed by template name.
$skipRest = @{}

# mpv.net auto-loads every file in the opened file's folder into a playlist, and
# --auto-load-folder=no on the command line does not reliably suppress it. If we
# played benchmarks/<res>.mp4 directly, each run would also play every other
# resolution and the timing would be meaningless. So copy each clip into its own
# clean temp folder and play it from there - a one-file folder has nothing to auto-load.
$clipRoot = Join-Path ([System.IO.Path]::GetTempPath()) "animejanai-bench"
Remove-Item $clipRoot -Recurse -Force -ErrorAction SilentlyContinue

foreach ($res in $resolutions) {
    $cellDir = Join-Path $clipRoot $res
    New-Item -ItemType Directory -Path $cellDir -Force | Out-Null
    $video = Join-Path $cellDir "$res.mp4"
    Copy-Item (Join-Path $benchRoot "$res.mp4") $video -Force
    foreach ($name in $slots.Keys) {
        $isOff = ($name -eq 'Off')
        $vf = if ($isOff) { "" } else { $vfBase -replace 'slot=\d+', ("slot=" + $slots[$name]) }
        if ($isOff -and $res -ne '1920x1080') {
            $table[$name][$res] = ""
            continue
        }
        Write-Host -NoNewline ("{0,-12} {1,-10} " -f $name, $res)
        # A smaller resolution for this template already fell below the floor; this
        # larger one can only be slower, so record it without running.
        if ($skipRest[$name]) {
            $table[$name][$res] = -1
            Write-Host "skipped (lower resolution already too slow)" -ForegroundColor DarkYellow
            continue
        }

        # Whole-cell retry loop: if mpv fails to produce a usable sample (a transient,
        # or a wedged run), retry. A genuine below-floor sample marks this cell - and
        # the larger ones - too slow.
        $done = $false
        $engineReady = $isOff -or (-not $buildsEngines)   # Off/DirectML/NCNN: nothing to build
        for ($cellAttempt = 1; $cellAttempt -le $cellRetries -and -not $done; $cellAttempt++) {
            try {
                # Pre-build this slot+resolution's TensorRT engine synchronously via the
                # harness so the timed mpv runs land on a warm cache (identical engine:
                # same aji_trt + conf + model-dir + slot + WxH => same cache key). Only
                # needed once (cached after attempt 1); no-op on DirectML.
                if ($cellAttempt -eq 1 -and $buildsEngines -and -not $isOff) {
                    $wh = $res -split 'x'
                    $engineReady = Invoke-Build $wh[0] $wh[1] $slots[$name]
                }

                # Warmup warms GPU clocks (and builds the engine as a fallback if the
                # harness pre-build was missing/failed).
                $warmTimeout = if ($engineReady) { $sampleTimeoutSec } else { $buildAllowSec }
                $warmStart = $sampleStartSecs[(($cellAttempt - 1) % $sampleStartSecs.Count)]
                $warm = Invoke-MpvFrames $video $vf $warmupFrames $warmTimeout $warmStart
                if ($warm.TimedOut) {
                    if ($ShowTiming) {
                        Write-Host -NoNewline ("[warm +{0}s {1} {2:n4}s/{3:n0}f {4:n1}fps] " -f $warmStart, $warm.Reason, $warm.Seconds, $warm.Frames, $warm.Fps) -ForegroundColor DarkYellow
                    }
                    if ($warm.Frames -lt 3) {
                        Write-Host -NoNewline "[retry] " -ForegroundColor DarkCyan
                        continue
                    }
                }
                # The warmup completed, so the engine is now cached regardless of how it
                # got there - the measured runs are all cached-engine runs.
                $engineReady = $true

                $r = if ($isOff) {
                    Measure-Cell $video $vf $true $offSampleTargetFrames $offSampleMinSec $offSampleMaxSec $offSlowSampleMaxSec $offSampleTimeoutSec
                } else {
                    Measure-Cell $video $vf $engineReady
                }
                if ($r.Status -eq 'fail') {
                    Write-Host -NoNewline "[retry] " -ForegroundColor DarkCyan
                    continue
                } elseif ($r.Status -eq 'skip') {
                    $table[$name][$res] = -1   # sentinel: skipped, ran too slow (catalog renders "-")
                    $skipRest[$name] = $true   # every larger resolution for this template is slower too
                    Write-Host "skipped (under $fpsFloor fps)" -ForegroundColor DarkYellow
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
            # Every attempt failed to produce a usable sample. Record a failure (blank),
            # NOT -1, and do NOT poison the larger resolutions.
            $table[$name][$res] = ""
            Write-Host "failed: no usable sample after retries" -ForegroundColor Red
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
    if (($row | Where-Object { $_ -ne $null -and $_ -ne "" }).Count -eq 0) { continue }
    $lines += "|$name|" + ($row -join "|") + "|"
}
$outFile = Join-Path $root "benchmark.txt"
$lines | Set-Content $outFile
Write-Host ""
$lines | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "Saved to $outFile" -ForegroundColor Cyan
if ($script:benchmarkJob -ne [IntPtr]::Zero) {
    [void][BenchWin32.JobNative]::CloseHandle($script:benchmarkJob)
    $script:benchmarkJob = [IntPtr]::Zero
}
