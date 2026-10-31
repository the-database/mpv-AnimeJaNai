# Playback throughput benchmark

The Manager's **Run Benchmarks** button launches the same .NET runner on Windows
and Linux. The default suite measures the original **Balanced** and
**Performance** templates (slots 1010/1011) at 480×360, 640×480, 768×576,
1280×720, and 1920×1080. It uses the installed inference library, models, engine
builder settings, and engine cache. It does not change playback profiles.

## What the score means

The default `native-display` mode measures maximum decode + 2× AI upscale +
`gpu-next` rendering throughput. The player opens fullscreen on the configured
display and retains the user's renderer settings, including shaders and
debanding. Presentation sync is disabled; audio, subtitles, and other playback
scripts are disabled. This measures processing headroom, not the FPS normally
presented when a 24 FPS movie plays at its intended speed.

The render target is read from mpv's actual `osd-dimensions`, not inferred from
the input, model output, desktop settings, or the screen connected later when
results are submitted. A 960×540 input becomes 1920×1080 after a 2× model; the
renderer then fits that image to the screen. A 2560×1440 display and a
3840×2160 display therefore have different rendering workloads. The catalog
lists this target and compares native-display results at the same resolution.
Refresh rate is recorded when mpv reports it; it is not the FPS cap.

`--mode pipeline` measures decode + upscale with
`vo=null --vo-null-accept-hwframes=yes`, omitting display rendering and keeping
the output on the GPU. The bundled player must support this option. The runner
rejects a downloaded CPU output frame; ordinary `vo=null` can otherwise copy
every output plane to RAM before discarding it. These scores have no output
display and are listed separately.
Renderer settings, backend, power limits, and hardware still affect results
within either category. These bundled synthetic clips provide a repeatable
workload; they do not represent every real video.

## Timing

1. Prepare each TensorRT engine with `aji_harness` before measuring. The selected
   builder settings and normal cache keys are preserved.
2. Load the clip through the real native `animejanai` filter. Require the expected
   input dimensions and exact 2× output before warming up for 3 seconds.
3. Measure for 10 seconds with mpv's monotonic clock. Count native
   `end video-flip` trace events within this window. FPS is the number of intervals
   between those events divided by their fractional elapsed time. No timeline
   position, source FPS, Lua polling count, or estimated-frame-number is used.
4. Run three rounds, alternating model order. Pool interval counts and elapsed
   times for each cell, rather than averaging FPS or selecting the fastest run.
5. Reject paused/minimized/resized runs, incorrect model dimensions, dropped
   frames, failed processes, or missing/insufficient trace coverage. Native
   render resolution must remain consistent across the whole table.

The trace marks the completion of mpv's VO flip call, including renderer/queue
backpressure. It is not a GPU kernel timer or a measurement of physical panel
scanout. Trace logging itself adds some overhead. Raw traces, native logs,
per-round timings, output interval statistics, config snapshots, and renderer
properties are saved under `animejanai/benchmark-results/` for inspection.
These local files are not included in catalog submissions.

The old PowerShell runner passed integer `0` to `Math.Max(0, elapsed)`, selecting
the integer overload. This rounded fractional seconds: 1200 frames over 1.49s
or 1.51s could become 1200 or 600 FPS, rather than about 805 or 795 FPS. Existing
scores cannot be repaired without the original timestamps. Both old Windows
and old Linux measurements remain categorized as **legacy / unversioned**,
separate from this method.

The bundled seed clips repeat their existing content into one-hour files using
FFmpeg stream copy (`-stream_loop -1 -i INPUT -t 3600 -c copy OUTPUT`). This keeps
the original encoded workload while avoiding the seek stalls of short clips
looped during a high-FPS sample. No frame interpolation or image changes are made.

## Run and inspect

The installed `animejanai/benchmarks/AnimeJaNaiBenchmark[.exe]` needs no .NET
installation. Launch it through `animejanai_benchmark_all.bat` or `.sh`, or run:

```sh
AnimeJaNaiBenchmark --resolutions 1920x1080 --rounds 1 --seconds 5
AnimeJaNaiBenchmark --mode pipeline
AnimeJaNaiBenchmark --video /path/to/video.mkv --resolutions 1920x1080 --start-seconds 144
```

Additional options: `--install-root`, `--output-dir`, `--warmup-seconds`, `--slots`
(custom profiles for local experiments), and `--quick` (one 360p round).
Keep native-render windows visible while a run is active. Esc/closing the
player or Ctrl+C in the runner aborts the run; partial results are not published
as a completed table. A failed run leaves the previous completed
`animejanai/benchmark.txt` intact.

Successful runs write the normal Markdown table to `animejanai/benchmark.txt`,
with a single-line metadata header immediately above it:

```text
Benchmark metadata: {"method":"mpv-output-trace-v2","mode":"native-display","output_resolution":"2560x1440","display_hz":144}
```

The Manager submits this as schema 2. Metadata and scores live in the same file,
so a separate stale sidecar cannot relabel another table. Model row names remain
`Balanced` and `Performance`, and column names remain the input resolutions.

## Build and checks

```sh
dotnet publish AnimeJaNaiBenchmark -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish AnimeJaNaiBenchmark -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet run --project tests/BenchmarkTiming -c Release
node --test tests/benchmark-catalog.test.mjs
```

Both release packaging paths install the executable beside the clips and Lua
controller. Automated checks cover fractional timing, output-event counting,
weighted aggregation, submission metadata, and catalog separation. Actual
GPU/display behavior still requires testing on the target OS/backend.
