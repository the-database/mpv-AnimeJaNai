# API 1.9 native streaming preview: validation and remaining work

This preview patches the framework for continued Plex addon development. It is
not a completed Plex integration or a hardware compatibility certification.
Read [the API contract](STREAMING-READINESS.md) before using it.

## Implemented

- Profile/resolution-aware finite native process and job memory budgets, with
  aggregate admission and release only after the worker tree exits.
- Required AI filters fail the stream instead of silently sending original video.
- Safe backend, model and processing-dimension evidence; independently probed
  encoded dimensions, profile, bit depth and audio metadata.
- `mediaStreams.check` and `prepare`, asynchronous status and cancellation;
  cached-only TensorRT streaming, with no automatic inference-backend switch.
- Hardware encoder initialization checks at the configured output size/FPS;
  separate NVENC and AMF options; explicit 8-bit H.264 default.
- Completed streams retain the last measured processing-speed interval.
- Headless TensorRT uses NVDEC, rather than the older CUDA decoder wrapper.
- Bounded operator-only native error logs under managed data's
  `media-workers/diagnostics` (16 files, at most 16,384 retained characters each).
  These are never returned to addons. Inspect/redact them before sharing.

## Actually tested on Windows / NVIDIA RTX 5090

A compiled Wasm addon opened a credentialed local HTTP Range source containing
1920x1080, 24 fps video, audio and an ASS subtitle track. The DirectML Balanced
profile (slot 1002) produced eight one-second MPEG-TS segments:

- Every segment independently decoded with FFmpeg's error-on-failure option.
- Actual video: 3840x2160, H.264 High, 8-bit yuv420p, 24 fps.
- Actual audio: AAC stereo, 48 kHz.
- Subtitle text was visibly present in the decoded frame.
- Readiness opened the NVENC encoder at the processing output dimensions.
- Reported model was HD V3.1 Balanced SPANF3; inference stayed DirectML.
- A deliberately invalid model failed with `ai_filter_failed`, no encoded-media
  result, and confirmed capacity cleanup. The isolated test model was restored.
- The final measured interval was approximately 1.80 media seconds per wall
  second over 1.09 seconds. This short interval is not a sustained-speed result.

The managed suite passed 189/189 before the final decoder/error-handling and
budget refinements. The four targeted streaming-policy tests passed again after
those refinements; the final DirectML GPU regression also passed. Native CI
passed 39/39, including the repeated libmpv load/unload lifetime regression.

Native source: mpv `4502d289069a1678c05931bb7c9f15d6922f0a08`;
inference `42194a8ae2d03df6605644394ae18ee1cae1ab28`.
Matching inference runtime: TensorRT 11.1.0.106 / CUDA 13.3.0,
ONNX Runtime 1.24.4 / DirectML 1.15.4.

## TensorRT: partial validation, not a passing output test

The real addon reached NVDEC input and returned `engine_missing` with the
requested TensorRT backend preserved. Starting preparation, observing `building`,
cancelling it, and releasing native capacity were exercised. A failed build
returned `preparation_failed` and did not publish output.

The initial builder process ceiling was too small and its compiler reported
allocation failure. Preparation now raises the per-process ceiling to the
combined job reservation (6 GiB for the HD Performance test). The final test PC
could not admit that reservation with the required available-commit headroom;
it returned `resource_exhausted` cleanly. No system memory settings were changed.

Successful preparation, cached-engine reuse, incompatible-engine replacement,
and TensorRT 4K output remain **unverified end to end**. Do not advertise them as
tested. Rerun the included regression on a machine with sufficient available
Windows commit memory. Models alone are included in the normal AJN package;
engines built for one GPU are not portable release assets.

## Other remaining qualification

- AMD AMF initialization and actual output on AMD hardware. Vendor selection,
  option mapping and unsupported-combination rejection have managed tests only.
- The user's actual remote shared Plex server, real episode, and official
  Android TV client; this test used a synthetic local HTTP source.
- Long playback, sustained speed, startup latency, A/V drift, forward/backward
  seeks, resume, audio selection, episode changes and network stalls.
- Multiple simultaneous high-resolution streams under realistic GPU pressure.
- Broad subtitle styling, fonts and animations beyond the tested ASS caption.

## Smallest developer retest

1. Extract the complete matching preview into a new folder. Keep its host,
   player and inference binaries together. Do not copy these into a public 3.6.1
   installation piecemeal.
2. Require API 1.9 and capability `mediaStreams` 1.1. Approve the source and a
   DirectML Balanced profile. Request H.264/MPEG-TS, `encoder: "nvenc"`,
   `bitDepth: 8`, AAC stereo, and a short duration.
3. Probe tracks; call `check`, poll `ready`, and close the handle. Then open the
   same plan. Verify `native.processing` and `encodedMedia` before serving it.
4. Repeat with the real authorized Plex source. Verify the TV receives the
   processed stream. Do not infer that from a successful connection alone.
5. Test TensorRT and AMD separately and retain their actual result evidence.

The source regression is
`addons/tests/AnimeJaNai.Addons.NativeTests/NativeStreamingPolicyChecks.cs`.
Build the project, then invoke its DLL with the AJN root, a new result directory,
dotnet, Wasmtime, Javy and FFmpeg paths, followed by `--streaming-policy-only`.
The optional test environment variable `AJN_STREAM_TEST_BACKEND` selects
`DirectML` or `TensorRT`; omission runs both. It intentionally corrupts and
restores a model for its negative test, so use an isolated test installation.
