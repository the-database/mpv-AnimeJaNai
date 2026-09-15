# Custom RIFE scene detectors — API 1.8 preview

This optional API lets a WebAssembly addon decide whether RIFE should interpolate
between a pair of frames. Creators can port motion-aware detectors to Wasm.
The `scene-detector` example demonstrates the contract using average luma
difference. It is **not an MVTools port** and does not reproduce `sc_mode=2`.

## Requirements and installation

Use a Windows x64 API 1.8 host, matching player with `privateSceneAbi: 1`, and
inference dispatcher/backend implementing the optional scene ABI. Enable RIFE
in the selected AJN profile first. This API does not enable RIFE or select/build
its model. It currently targets ordinary AJN players, not independent headless
addon sessions. Samples support progressive mono SDR; other inputs fall back.
CUDA and D3D11 hardware downloads are implemented; consult the build's test
results for which backend/hardware combinations have been qualified.

Build the example with the ordinary AJN build command. Install its `.ajnaddon`
in Manager's Addons page and approve `player.sceneDetection` and `frames.read`.
Start RIFE playback and the addon, then use **Enable on available AJN players**.
Use **Show detector status** to check progress. **Restore AJN scene detection**,
disabling the addon or closing its worker releases its attachments. Installation
alone does not change playback: the example uses manual activation. No remote
host address is needed. A replacement player gets a new ID; attach again.

The example's **Developer test mode** offers `cut`, `continuous` and `default`
for connection tests; `analyze` runs its demonstration algorithm. Suspension
requires detach/attach. A production addon may manage player discovery with
bounded timers and reset its own history when playback changes.

## Manifest and compatibility

Declare `api: {"major":1,"minMinor":8}`, permissions
`["player.sceneDetection","frames.read"]`, and
`requiredCapabilities: {"sceneDetection":{"major":1,"minMinor":0}}`.
The package remains `manifest.json` plus `module.wasm`. Ported C/C++ code must
produce compatible core Wasm and implement the same RPC protocol. Native DLLs
are not accepted package contents. `player.observe` does not grant this control.

The host advertises this capability only with matching native player metadata.
An older inference backend reports `backendUnavailable` and retains AJN's
detector. Existing addon calls, inference struct layouts and `aji_infer_rife`
callers keep their behavior; this is an additive optional API.

## JavaScript contract

| Call | Result and limits |
| --- | --- |
| `ajn.sceneDetection.list()` | `{playerId,inUse,format:"gray8",stage:"beforeInterpolation"}[]`. Same opaque player IDs as normal observations; no titles/paths. |
| `attach(playerId,{width?,height?,deadlineMs?})` | `{detectorId,width,height,deadlineMs,format,stage}`. Defaults 160×90, 25 ms; width 1–320, height 1–180, deadline 5–100 ms. |
| `read(detectorId)` | Latest pending pair once, or `null` when consumed, expired or unavailable. |
| `submit(detectorId,requestId,decision)` | `{accepted:boolean}`; decision is `cut`, `continuous` or `default`. |
| `status(detectorId)` | `{state,acceptedPairs,timedOutPairs}`. |
| `detach(detectorId)` | Releases the caller's detector and restores built-in detection. |

There is one exclusive detector per player, at most four players per host.
A conflicting attachment returns `scene_detector_in_use`. Detector handles
belong to their addon worker; another worker cannot use them.

The host delivers `onEvent({name:"scene.request",data:{detectorId},...},ajn)`.
Read, analyze and submit inside that callback. Events run serially per worker.
Cache settings on `start` and `settings.changed`; avoid network/storage work
in this callback. Do not busy-poll or queue expired requests.

```typescript
interface ScenePair {
  requestId: string; epoch: string;
  previousPtsSeconds: number; currentPtsSeconds: number;
  width: number; height: number;
  sourceWidth: number; sourceHeight: number;
  format: "gray8"; stage: "beforeInterpolation"; range: "full";
  remainingMs: number;
  previous: Uint8Array; current: Uint8Array;
}
```

Each array has `width*height` row-major bytes without row padding: full-range,
encoded SDR luma, bilinearly resized from RIFE's input pair. These are not
linear-light or final-display pixels. `sourceWidth/sourceHeight` describe the
processing stage, which may already be resized/upscaled. Later subtitles/OSD
are absent. Analysis runs once per input pair, independent of interpolation
factor. The preceding reduced sample is cached when its identity matches.

`cut` invokes RIFE's existing left-frame substitution. `continuous` interpolates
even when the built-in threshold would classify a cut. `default` delegates this
pair to AJN. AJN retains control of output timestamps and frame cadence.

Keep IDs as opaque strings. Reset cached history on epoch changes, seeks,
geometry changes or timestamp discontinuities. `accepted:false` means a late,
duplicate, revoked or stale submission; it never affects a later pair. An
accepted submission acknowledges delivery, not proof the native thread consumed
it before the deadline. `acceptedPairs` counts native consumption.

## Deadlines, fallback and cost

The native deadline begins **after sample production**. `remainingMs` is the
budget left when the host copied the pair and may be stale by guest delivery.
Missing/invalid decisions use AJN's detector. Three consecutive missed deadlines
suspend the attachment until detach/attach. An independent three-second host
lease expires if the player's lifecycle connection stops renewing it.

Without a detector there is no scene sampling/wait. With one, the implementation
downloads each new hardware frame, reduces it on the CPU, copies a pair to Wasm
and waits for its answer. This is not zero-copy; the deadline does **not** bound
GPU download/resize time. Benchmark the intended GPU, source FPS, resolution
and RIFE order. Reduce sample size/work before increasing deadlines, which can
cause stutter. Small samples may not preserve every cue a full-resolution
MVTools algorithm uses; detector accuracy requires separate evaluation.

Existing worker limits apply: 500 broker calls/second, 128/event, 16 MiB/second
of binary frame transport, a two-second outer event deadline, and Wasm memory
and CPU limits. A pair is at most 115,200 bytes. Multiple players share worker
resources. A slow unrelated callback can cause scene deadlines to expire.

| Status | Meaning/action |
| --- | --- |
| `waitingForPlayer` | No pair yet; check the active profile has RIFE enabled. |
| `pending` | Player is waiting for a decision. |
| `active` | A decision was consumed; this does not assess its quality. |
| `fallback` | A deadline was missed; built-in detection was used. |
| `suspended` | Three consecutive misses; fix workload, detach, reattach. |
| `sampleUnavailable` | Unsupported/sample-failure path, including HDR/interlaced/stereo. |
| `backendUnavailable` | Loaded inference DLLs lack the optional scene ABI. |
| `unavailable` | Unknown native state. |

Player closure releases its detectors; later calls return
`scene_detector_not_found`. Enumerate again. Missing grants give
`permission_denied`; missing support gives `feature_unavailable` or startup
`missing_capability`. Invalid options give `invalid_request`.

## Raw RPC for other Wasm languages

Methods are `sceneDetection.list`, `.attach`, `.read`, `.submit`, `.status`,
`.detach`, with parameters matching the SDK table. Raw `attach` must supply
all three numeric options. A read response is `{pair,byteLength}` followed
immediately after its JSON newline by exactly `byteLength` bytes, previous
plane first. Metadata contains every field above except the arrays. No data is
`{pair:null,byteLength:0}`. Other replies are JSON only. Consume the entire tail
before the next JSON message; use the standard hello/event/error protocol.

## Creator qualification

Test increasing `acceptedPairs` and forced cut/continuous behavior on permitted
SDR footage with working RIFE. Compare detector quality on cuts, pans, flashes
and fades. Test seek, pause/resume, profile/RIFE-order changes, player restart,
disable, competing detectors, deadline suspension/recovery and HDR fallback.
Measure overhead and missed deadlines with the intended number of players.
Protocol tests and compilation alone do not establish smooth playback or
MVTools parity. The complete example source is supplied in `examples/scene-detector`.
