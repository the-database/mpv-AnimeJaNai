# Frame samples and scheduled callbacks

API 1.2 adds `frames` capability 1.0 and `timers` capability 1.0. These are additive:
older JSON-only clients keep their existing messages and session behavior.

See [initial performance measurements](FRAME-PERFORMANCE.md) for measured payload
rates, the test setup and the limits of the current performance evidence.

## Requesting samples

The addon needs `frames.read` and `sessions.manage`, and must own the processing
session. The user separately approves that addon's exact source file and profile.
Permissions apply to the installed package hash. `host.info().capabilities.frames`
is present only with a sample-capable native runtime. Capabilities do not grant
permissions, and the chosen session can still have an unsupported format/backend.

```javascript
const subscription = ajn.frames.subscribe(sessionId, {
    stage: "processed", format: "bgra8", width: 64, height: 36, maxFps: 30,
});
ajn.timers.set("samples", 33);
// In an event whose name is "timer" and data.timerId is "samples":
const frame = ajn.frames.read(subscription.subscriptionId);
if (frame) {
    // frame.pixels is a Uint8Array, not base64 or JSON-encoded pixel values.
    // Process it here; return only a small JSON result from the callback.
}
// When finished:
ajn.timers.clear("samples");
ajn.frames.unsubscribe(subscription.subscriptionId);
```

The initial producer accepts 1..320 by 1..180 pixels and 1..60 maximum samples/s,
with one subscription per session. An addon can use independent concurrent
sessions within host resource limits. The width/height stretches the visible crop
to the sample rectangle; it does not add display letterboxing. Slow consumers
receive the latest unread sample. There is no backlog and no promise that every
source frame, or the requested maximum rate, will be delivered.

The Windows implementation supports progressive mono SDR DirectML/D3D11 frames.
CUDA/TensorRT, HDR, stereo and interlaced samples are unavailable in this producer.
`frame_format_unavailable` or `feature_unavailable` lets an addon report that
limitation or choose another supported operation. Do not reinterpret HDR as SDR.

Each BGRA8 sample carries width, height, tightly packed stride, processing PTS,
producer-clock milliseconds, source dimensions, crop, pixel aspect, rotation,
vertical flip, primaries, transfer, full RGB range and opaque alpha. Rotation and
vertical flip describe display transforms still to apply. Primaries and transfer
are preserved; matrix/range conversion alone does not make every frame sRGB.
`frameId` and `epoch` are decimal strings, avoiding integer precision loss in JS.
The producer clock is local to that native session, not a shared wall clock.

This is the processed filter image, before final display tone mapping, color
management, subtitles and OSD. Its PTS is not a measured presentation timestamp.
Future final-display and HDR sampling require explicit negotiated representations.

Seeks invalidate samples from the old filter epoch. Native seek commands remain
asynchronous; session status reports their progress. Unsubscribe turns sampling
off. Closing a session, stopping/failing an addon or revoking its media access
releases its subscriptions. A bad subscriber never owns GPU textures or blocks
the original video image from continuing through the pipeline.

## Binary wire extension

Frame reads opt into binary framing on a successful `frames.read` response
(API 1.3 also uses this envelope for [network results](NETWORK.md)):

```json
{"jsonrpc":"2.0","id":3,"result":{"frame":{"width":64,"height":36,"format":"bgra8","stage":"processed"},"byteLength":9216}}
```

This illustrative header omits the other metadata. Its newline is followed by
exactly `byteLength` raw bytes, with no additional separator. The next JSON message
starts immediately afterward. A missing sample returns `frame: null, byteLength:
0`. Errors have the normal JSON error response and no binary tail. Every other
API 1.0/1.1 method remains JSON-only. The event gate prevents a heartbeat/timer
message from interleaving with a binary response. Buffered input must consume all
declared bytes before reading the next JSON line; binary bytes can contain any
value, including newline and invalid UTF-8.

Each binary payload is at most 230400 bytes. A worker has a 16 MiB/second binary
response budget; an excess read receives `bandwidth_exceeded`, so the addon can
reduce its requested size/rate. The normal two-second callback and CPU/memory
limits still apply. Unsolicited guest binary data is not part of this protocol.

Internally, the trusted parent duplicates an unnamed fixed-size mapping into its
supervised native worker. A private filter reduces the image on the GPU, retains
the source until GPU completion, and copies only the small staging image. A
seqlock publishes pixels plus metadata. The parent validates geometry and copies
with at most three snapshot attempts; no OS mapping handle reaches Wasm.

## Timers

`ajn.timers.set(timerId, intervalMs, repeat = true)` creates or replaces one of at
most eight timers, with intervals from 16 to 3600000 ms. `clear(timerId)` cancels
it, including a scheduled ticket that has not entered its callback. No extra
permission is needed: the same worker resource limits contain these callbacks.

Timer events carry `{ timerId, elapsedMs, missedTicks }`. Callbacks are serialized
with actions/settings events and collectively capped at 60 background callbacks
per second. Missed intervals coalesce; they are not replayed in an unbounded
queue. Timers are not hard real-time clocks. A slow callback can delay another
timer, and a failed callback stops its addon. Use the native media timeline for
media timing rather than assuming timer ticks equal displayed frames.

## Try the generic inspector

Build `examples/sample-inspector`, install it in Manager and grant its two
requested permissions. Start the addon, approve a local video and a saved
DirectML profile, then use **Start samples** and **Show sample information**.
The inspector reports dimensions, color, PTS, a center pixel and byte counts;
it neither transmits video nor controls a lighting device. The normal AJN
performance benchmark clips are black, so choose visible test content when
checking image colors. Close/reopen sample sessions to apply changed settings.
