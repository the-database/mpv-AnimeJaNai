# Native streaming readiness — API 1.9 preview

Use this contract with the matching API 1.9 host, player and inference DLLs.
Require `mediaStreams` **1.1**, not just the presence of `mediaStreams`.
See [validation and remaining work](STREAMING-VALIDATION.md) for tested versus
implemented behavior, especially TensorRT preparation and AMD hardware.
This extends the existing Wasm system; it grants no native DLL loading, process
execution, filesystem paths or additional guest permissions.

## What to call

The following calls accept the same `(source, profileId, options)` as
`ajn.mediaStreams.open` and return the same `AjnStreamHandle`:

- `ajn.mediaStreams.check(...)` validates the approved source and profile,
  loads cached engines without building or deleting them, and opens/closes the
  selected hardware encoder at the processing output dimensions and frame rate.
- `ajn.mediaStreams.prepare(...)` uses AJN's existing TensorRT builder to build
  missing engines or rebuild incompatible ones, then checks the encoder.
- `ajn.mediaStreams.status(handle.streamId)` reports progress, readiness and
  errors for either operation.
- `ajn.mediaStreams.requestClose(handle.streamId)` cancels preparation or releases
  a finished check. Keep polling until `nativeCapacityReleased` is true and the
  state is `closed` or `failed`. Closing is asynchronous.

Check and preparation handles produce **no playable output**. Close them before
opening the actual stream, including a failed check. A finished check is a
snapshot, not a reservation of an indefinitely usable engine or GPU. Stream open
revalidates the pipeline and uses cached engines only. It does not silently build,
replace the selected backend or continue without a failed AI filter.

`state: "ready"` / `ready: true` means the selected processing configuration and
encoder initialized for that source. It does not promise sustained real-time
speed, successful decoding by a particular TV, or that a later resource allocation
cannot fail. Verify the actual output metadata before offering it to a client.

## Permissions and source selection

Require `sessions.manage`, `media.input` and `media.output`. Remote input also
needs `network.connect` and an approved network destination; saved credentials
need `credentials.use`. Request-derived credential contexts retain the existing
API 1.7 rules and expire/revoke normally. The host obtains paths, models and
configuration only from the approved profile/source records.

For the manifest:

```json
"api": { "major": 1, "minMinor": 9 },
"requiredCapabilities": { "mediaStreams": { "major": 1, "minMinor": 1 } }
```

Select an approved profile through `ajn.sessions.selections()`. Retain its ID and
backend. Never respond to `runtime_missing`, `engine_missing`, or
`encoder_unavailable` by choosing a different inference backend automatically.
The user may explicitly select another approved profile.

## Encoding options

```javascript
const options = {
    mode: "segments", segmentSeconds: 1,
    encoding: {
        videoCodec: "h264", container: "mpegts", videoKbps: 10000,
        encoder: "nvenc", bitDepth: 8,
        audioCodec: "aac", audioKbps: 128, audioChannels: 2,
        lengthSeconds: 0
    },
    playback: { startSeconds: 0, audioTrack: "default", subtitles: { mode: "none" } }
};
```

`encoder` is `auto` (default), `nvenc`, or `amf`. Auto prefers a detected NVIDIA
adapter, then AMD. This selects an **encoder family**; it never changes DirectML
or TensorRT inference. This preview uses each encoder's default device; it does
not expose selection among multiple GPUs of the same vendor. No software fallback
is automatic. A missing adapter, missing compiled encoder, incompatible driver or
unsupported encoding combination produces an actionable failure.

| Encoder | Codec | Bit depth | Containers |
| --- | --- | --- | --- |
| NVENC | H.264 | 8 | MPEG-TS, Matroska, fragmented MP4 |
| NVENC | HEVC | 8 or 10 | MPEG-TS, Matroska, fragmented MP4 |
| NVENC | AV1 | 8 | Matroska, fragmented MP4 |
| AMF | H.264 | 8 | MPEG-TS, Matroska, fragmented MP4 |
| AMF | HEVC | 8 or 10 | MPEG-TS, Matroska, fragmented MP4 |

These are implemented choices, subject to the actual device/driver check.
**AMD hardware qualification is still required.** A DirectML test on NVIDIA is
not evidence about AMD. H.264 now defaults to an explicit 8-bit NV12 encoding
input. Ten-bit output must be explicitly selected with HEVC. The native host
converts frames after AI processing and uses software subtitle composition; full
frames never enter Wasm or JSON RPC. This transfer has a performance cost that
must be included in stream measurements.

For burn-in, probe the source, select the subtitle track ID, and use
`subtitles: { mode: "burn", trackId }`. Existing approved external subtitle sources
are also supported. Readiness validates that selection with the same source probe.
This preview admits progressive SDR sources with at most 3840×2160 total pixels
and at most 8192 pixels on either axis. HDR and
interlaced streaming remain unavailable.

## Handle lifecycle example

Perform one operation per callback; never wait in a Wasm callback for an engine
build or a stream. For example, keep the returned handle in addon memory and poll
it using `ajn.timers.set("readiness", 500)`:

```javascript
// plan contains an approved source, profileId and the options above.
let preparation = null;
function startCheck(ajn, plan) {
    preparation = ajn.mediaStreams.check(plan.source, plan.profileId, plan.options);
    ajn.timers.set("readiness", 500);
}
function pollCheck(ajn) {
    const s = ajn.mediaStreams.status(preparation.streamId);
    if (s.ready || s.error) {
        ajn.timers.clear("readiness");
        // Save s for your UI, then release the handle. Poll cleanup separately.
        ajn.mediaStreams.requestClose(preparation.streamId);
    }
    return s;
}
// If s.error.code is engine_missing or engine_incompatible, offer the user
// preparation. After cleanup, call prepare with the same plan, poll, close,
// and then open the stream. The same requestClose cancels a running build.
```

Check/preparation handles count toward the existing four retained handles. They
release native admission on completion but must still be closed. Preparation is
bounded to 20 minutes. Only one TensorRT preparation can own the host's engine
cache at a time; TensorRT sessions and preparation cannot overlap in that host.
Native process teardown cancels its builder and keeps capacity reserved until
the process tree is gone. Incompatible cache checks do not delete the old engine;
an explicit prepare uses AJN's existing compatibility check and rebuild path.
Cache keys retain AJN's model/settings/shapes/precision, runtime and GPU identity.
Engines generated on the development PC are not distributed as portable engines.

## Evidence and errors

`status.native.processing` contains `state`, `actualBackend`, `slot`,
`inputWidth`, `inputHeight`, `outputWidth`, `outputHeight`, `outputFrameRate`,
`rifeActive`, and `activeModels` (bounded model identifiers). `selectedBackend`
records the request independently. Processing states include `active`, `bypass`,
`building`, `engineMissing`, `engineIncompatible`, `preparationFailed`, and `failed`.
An approved profile intentionally selecting no chain reports `bypass`; this is
not evidence of upscaling. A failed required filter terminates output rather than
substituting unprocessed video.

`native.encoder` identifies the selected family, concrete codec, input pixel
format and default-device policy. On a successful check, its `validation` is
`openedForOutputFormat`. `encodedMedia.tracks` describes an independently probed
completed encoded object, including actual dimensions, codec, profile, bit depth,
pixel format and audio format. Treat that as the evidence for client negotiation.
No raw native logs, private paths, source URLs or credentials are included in the
processing evidence.

| Error code | Action |
| --- | --- |
| `native_update_required` | Install the matching host, player and inference build. |
| `runtime_missing` | Install the selected TensorRT runtime through AJN Manager. |
| `engine_missing`, `engine_incompatible` | Close the check; offer preparation of the same profile. |
| `preparation_busy` | Wait for or close the existing preparation/TensorRT session. |
| `preparation_failed`, `preparation_timeout` | Show the failure; do not switch backend or claim readiness. |
| `encoder_unavailable`, `encoder_failed` | Choose a supported explicit encoder/format or fix its driver/runtime. |
| `unsupported_format`, `invalid_encoding` | Correct the requested codec/container/bit-depth combination. |
| `unsupported_media`, `stream_format_unavailable` | Select a source within the supported input envelope. |
| `resource_exhausted`, `capacity_exceeded` | Release other processing work or explicitly select a lighter profile. |
| `ai_filter_failed`, `native_processing_failed` | Stop playback; processing did not succeed. |

## Resource and performance semantics

Native jobs have separate finite RAM limits; Wasm limits are unchanged. The host
reserves a budget before starting native processing and releases it only after
cleanup. HD built-in lightweight/balanced DirectML profiles reserve 4 GiB per
process. Known single HD V3.1 Performance/Balanced custom chains without RIFE
also use 4 GiB; UHD inputs, Quality, unknown custom chains, stacking and RIFE use
an 8 GiB envelope.
TensorRT profiles reserve at least 6 GiB except built-in Performance (4 GiB).
Preparation additionally reserves 2 GiB for the Performance builder or 4 GiB for
other TensorRT builders. During preparation, that combined reservation is also
the per-process ceiling so the builder can use it; the whole job still shares
one finite limit. Cached-only streaming has no builder reservation.
Aggregate reservation is capped at the smaller of 32 GiB and 75% of physical RAM,
and also by a shared available-commit snapshot with 512 MiB headroom. Runtime
allocation can still fail; these limits do not reserve or cap GPU VRAM.

`processingMediaSecondsPerWallSecond` measures produced media time divided by
monotonic wall time over `speedMeasurementWallSeconds`. It excludes deliberately
paused/buffer-limited intervals. Completion retains the last measured running
interval; retaining completed files does not turn that value into an artificial
zero. Null means no usable interval was observed. Do not substitute bitrate,
requested FPS, or source download speed. Measure startup separately, and qualify
longer playback, seeks, stalls, client decoding and sustained speed on the target
hardware before advertising a finished Plex integration.
