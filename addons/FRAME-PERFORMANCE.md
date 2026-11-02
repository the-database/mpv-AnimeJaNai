# Initial frame-sampling measurements

The sample producer reduces images on the GPU and reads back only the requested
small image. An unsubscribed producer does no sample GPU work. Slow consumers
receive the latest sample, and cannot create an accumulating frame queue. These
are bounded-work properties, not a promise of zero performance cost.

The first local measurement used Windows 11, an RTX 5090, a synthetic moving
480x360 24fps video, 2x DirectML processing to 960x720, and null output. Each case
had two seconds of warmup and four seconds of measurement. The second pass used
the reverse order. No other GPU tests ran concurrently.

| Configuration | Samples per 4s, both passes | Binary bytes per 4s | Reported decoder / output drops |
| --- | --- | --- | --- |
| No sample filter | 0 | 0 | 0 / 0 |
| Filter present, unsubscribed | 0 | 0 | 0 / 0 |
| 64x36, maximum 30 samples/s | 96 | 884736 | 0 / 0 |
| 320x180, maximum 60 samples/s | 96 | 22118400 | 0 / 0 |

Every run advanced the media timeline at real time. Both active cases were
limited by the 24fps source. Their payload rates were approximately 216 KiB/s
and 5.27 MiB/s. A 320x180 BGRA8 frame always contains 230400 bytes; 60 delivered
samples/s would carry about 13.18 MiB/s before framing. Addons should request the
size and rate they need, especially with concurrent sessions.

Native-process plus host-reader CPU time varied substantially between repeated
runs (including the unsubscribed baseline). This short test does not establish
a reliable CPU overhead percentage or GPU latency. It excludes Wasm callbacks,
their RPC transport, visible playback and display timing. Zero null-output drops
does not prove stutter-free display. It also does not establish performance for
1080p/4K inputs, slower GPUs, stacked models, RIFE or TensorRT.

Separate functional tests exercise actual Wasm callbacks, binary transport and
two simultaneous native sample streams. These validate behavior, not a broad
performance claim. Further measurements should include those paths and the
hardware/workloads AJN users actually run.

The reproducible harness is
`tests/AnimeJaNai.Addons.NativeTests/NativeFrameBenchmark.cs`. Run the native-test
executable with `<trusted-AJN-root> <new-output-directory> <dotnet> --frames-benchmark`.
It writes raw measurements to `results.json`. Corresponding initial results are
included with the frame preview's build evidence.
