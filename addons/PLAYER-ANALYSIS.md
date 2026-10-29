# Source observations and character masks — API 1.12

This optional Windows capability observes the video already playing in AJN. It opens no second decoder and does not need upscaling or RIFE enabled. The addon chooses when to subscribe, its sample dimensions and analysis rate. No observation filter, GPU model, download or analysis work is started without that request. The optional `aji_analysis.dll` is not imported by the normal inference backend.

## First-use setup

A clean core installation needs the matching **TensorRT/CUDA runtime and the engine-builder component for its NVIDIA GPU**, as well as `aji_analysis.dll`. In Manager, open **Components**, select the TensorRT components recommended for the PC, and choose **Apply changes**. This preview uses TensorRT **11.4.0.106 / CUDA 13**. Use the components for this build rather than copying unrelated DLL versions. Select the TensorRT backend so normal playback supplies CUDA source frames; upscaling and RIFE may remain off.

The `player.analysis` grant provisions the approved ONNX model; it does **not** install TensorRT or CUDA dependencies. Windows error **126** loading the optional runtime means a DLL or one of its dependencies could not be loaded. Check the Components installation first. Addon setup should present this prerequisite before enabling character analysis and display the host's setup status instead of treating an unavailable mask as background.

## Grants and discovery

Declare API `{ "major": 1, "minMinor": 12 }`, required capability `playerAnalysis` `{ "major": 1, "minMinor": 0 }`, and permissions `player.observe`, `frames.read`, `player.analysis`. Manager asks the user to approve GPU analysis and the first-use model download. Network destinations for light controllers still require their own existing grants; this capability grants no network access to the guest.

`ajn.playerAnalysis.list()` returns attached players with opaque `playerId` values. No filenames, native handles, model paths or player commands are exposed. Select a player using your addon's settings/actions.

```js
const selected = ajn.playerAnalysis.subscribe(playerId, {
    stage: "source", format: "bgra8+unorm16le",
    model: "skytnt.anime-seg.isnetis.v1",
    width: 160, height: 90, maxFps: 12
});
const status = ajn.playerAnalysis.status(selected.subscriptionId);
const pair = ajn.playerAnalysis.read(selected.subscriptionId);
if (pair) {
    // Color: tightly packed BGRA8. Mask: paired soft foreground, uint16 LE.
    const mask = new DataView(pair.mask.buffer, pair.mask.byteOffset, pair.mask.byteLength);
    const firstPixelForeground = mask.getUint16(0, true) / 65535;
    // Your estimator/smoothing/device policy goes here.
}
// On disable or when observation is no longer needed:
ajn.playerAnalysis.unsubscribe(selected.subscriptionId);
```

Use existing addon timers to read samples. Limits: 1–320 by 1–180 pixels, 1–30 analyses/s, default 160×90 at 12/s. Readers of the same player's analysis share one native producer at the largest requested dimensions/rate. Each reader has independent sample identity and rate limiting; removing one reader does not stop another. Overload skips work instead of queuing unbounded frames or blocking playback. `producerDrops` and `skippedSamples` expose omissions.

Color-only source observations use `playerFrames.subscribe(playerId, {stage:"source", ...})` and require playerFrames 1.1. Existing `stage:"processed"` remains unchanged. Color-only frames remain limited to 230400 binary bytes and 60 Hz; paired analysis allows 345600 bytes under the existing overall 16 MiB/s guest budget.

## Readiness and failures

Subscribe returns promptly. Poll status: `provisioningModel`, `loading`, `buildingEngine`, `waitingForFrame`, `processing`, `ready`, `unsupportedFormat`, `modelMissing`, `failed` or `closed`. First use downloads the approved 176 MB ONNX and builds a local TensorRT engine. This can take minutes. It is not a Wasm callback and holds no decoder image while setting up. Failed setup does not become an all-background mask. `read()` returns null for no new completed observation; unsupported input/native failure raises an explicit error. The inspection example returns that error in its action result.

The host verifies SHA256 `f15622d853e8260172812b657053460e20806f04b9e05147d49af7bed31a6e99` for [SkyTNT's isnetis.onnx](https://huggingface.co/skytnt/anime-seg/blob/main/isnetis.onnx). It is provisioned under the installation's `animejanai/analysis` directory. The engine cache includes the model hash, TensorRT version, GPU UUID and driver version. Do not redistribute engine files between machines. Setup errors are exposed through status; native engine errors are also reported in the player log. Correct the missing runtime/model problem and unsubscribe/re-subscribe to retry.

## Identity, preprocessing and color

The native worker takes a source CUDA image before user upscaling/interpolation, subtitles and OSD. It preserves aspect ratio, bilinearly resizes native source pixels into a black-padded 1024 square, and passes float RGB `[0,1]` NCHW `img [1,3,1024,1024]` to IS-Net. `mask [1,1,1024,1024]` is cropped to remove padding and resized to the color sample. It never enlarges the small addon color sample as model input. Soft masks retain 16-bit precision; 0 is background and 1 is character confidence.

One seqlock publication contains the color and mask for one source frame, `frameId`, `epoch`, player/subscription identities, `ptsSeconds`, `producerTimeMs`, geometry, crop, pixel aspect ratio, rotation and vertical flip. Source geometry/transforms remain metadata; the samples are not final screen capture. Seeking/rebuilding the filter invalidates the old epoch. Slow readers get the newest completed pair, never a mask borrowed from a different frame. RIFE-generated output frames do not require separate segmentation.

Color is full-range encoded RGB with declared source SDR transfer and primaries. BGRA8 does **not** imply sRGB. Apply the reported transfer/primaries when computing linear-light or OKLab values. The native worker does not apply display tone mapping or claim projector color matching. Media PTS and producer timing are processing observations, not measured display timestamps. `analysis.gpuTimeMs` measures the optional GPU work and readback; `sampleAgeMs` measures time from submission to publication, not display latency.

Character masks currently require progressive, mono, SDR CUDA frames and the matching NVIDIA/TensorRT component. D3D11 color sampling remains supported; D3D11 character segmentation, software source frames, HDR, stereoscopic and interlaced analysis are unsupported and reported explicitly. No lights, device discovery, arbitrary DLL/model loading, image estimator or smoothing policy are built into this capability.

## Inspection example

Install `character-mask.ajnaddon`, approve its three grants, start it in Manager, open visible anime footage, then choose **List players** and **Start character analysis**. **Show status and latest pair** reports identity, mask range/coverage and timing. **Capture pair for inspection** retains one matched pair in the guest. **Export next inspection chunk** returns successive 4096-byte hex chunks for diagnostics; concatenate in offset order, split at `width*height*4`, and interpret the remainder as little-endian mask values. This bounded action export is optional; live observations use binary transport. The example sends nothing to physical lights.

An addon should hold its last calculated light color when no new sample is ready, handle unsupported input explicitly, and unsubscribe when it no longer needs analysis. The host independently cleans up on worker failure, permission revocation, player exit or lease expiry.

See [qualification results and measured cost](PLAYER-ANALYSIS-VALIDATION.md) for what has actually been tested and the remaining limits.
