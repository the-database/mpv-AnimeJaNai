# HTTP serving and media streaming — API 1.7 preview

API 1.9 extends this contract with [readiness, engine preparation and encoder selection](STREAMING-READINESS.md).

This extension lets one installed `.ajnaddon` accept approved HTTP(S) requests,
forward traffic to approved services, probe media, and serve processed audio/video.
The addon supplies service protocol and client authorization logic. AJN supplies
the HTTP transport, media worker, codecs, muxer, and temporary storage. No companion
executable is required. This is a framework API, not a Plex integration.

Use the matching runtime, SDK and developer tools. Native format qualification is
recorded separately in the release's test evidence; format enumeration does not
prove that a GPU/driver/profile can encode in real time. API 1.7 is a preview.

## Capabilities and permission review

Set `api: { major: 1, minMinor: 7 }` and declare each required capability as
`{ major: 1, minMinor: 0 }`. An older host rejects an unavailable required capability
before running the addon. Optional features can be tested through `ajn.info().capabilities`.
Never assume that a newer host contains every optional native component.

| Capability | Required access |
| --- | --- |
| `httpServer` | `network.listen` plus a reviewed listener |
| `httpProxy` | `network.proxy`, `network.connect`, reviewed upstream; forwarding also needs `network.listen` |
| `requestCredentials` | `credentials.delegate`, `credentials.use`, `network.connect`; capture uses an owned listener request |
| `mediaProbe` | `sessions.manage`, `media.input`, reviewed source; remote input also needs `network.connect` and upstream review |
| `mediaStreams` | `sessions.manage`, `media.input`, `media.output`, reviewed source/profile; remote input also needs network access |
| `outputPlayback` | `sessions.manage`, `media.output`, matching native playback runtime; source/output grants still apply |
| `subtitles` | `sessions.manage`, `media.input`, reviewed subtitle source or embedded track; remote input needs network access |

Serving stream or subtitle resources also requires `network.listen`. Saved
upstream credentials require `credentials.use`; request-derived contexts require
both credential permissions. Source, profile, listener, certificate and upstream
approvals are independent and bound to the exact reviewed package hash.

Existing HTTP uploads retain their defaults, including `audioCodec: "none"`.
Existing non-output `sessions.seek` keeps its original meaning. API 1.0–1.6 addons
do not acquire new access when AJN is updated.

## User setup in Manager

1. Install the `.ajnaddon`, review its permissions, and select it in **Addons**.
2. Approve its media source and processing profile. For remote input, approve the
   upstream service in networking access instead of supplying a local video path.
3. In **Listening access**, add a listener. The default accepts only connections
   from this PC. Access from other computers requires an explicit LAN/public
   choice, bind address, port, and accepted host names.
4. For HTTPS, use **Import HTTPS certificate** to import a PFX/PKCS#12 file with its
   private key, then select it and its covered hostname for the listener. AJN
   checks validity, server identity and expiry and protects the key for this Windows user.
5. Start the addon and use its action to open the listener. Approval alone does not
   bind the port. If binding fails, inspect the addon status for its port/certificate error.

Certificate replacement stops the addon and its listeners. Restart the addon to
use the replacement. Importing a certificate does not make clients trust it.
Certificates, DNS and routing must match the address clients actually use.

The optional public base URL is configuration only: `reachability` is `unverified`.
AJN does not open a firewall, forward router ports, supply public DNS, overcome
CGNAT, or route an existing client through an addon automatically. There is no
automatic public reachability test in this preview.

Review CORS explicitly if a browser client needs it. Use exact allowed origins;
credentialed wildcard CORS is not supported. CORS controls browser access and
does not authenticate native clients. Sensitive header/query names are reviewed
with the listener and redacted before request metadata reaches the guest.

## HTTP request lifecycle

`ajn.httpServer.selections()` returns opaque approved listener IDs and non-secret
binding metadata. `open(listenerId)` returns `{ serverId }` promptly. Poll
`status(serverId)` on a timer until `listening`, or handle `failed` and close it.

An `http.request` event contains `requestId`, `serverId`, `method`, `path`, repeated
`query` and `headers` entries, peer address, and WebSocket-upgrade information.
Repeated fields use `{ name, values: string[] }`, not a flattening dictionary.
Returning from the event does not complete its request. Store only the opaque
request ID and the small state needed for a later callback.

Authorize every protected request, then choose **one** terminal claim:

- `httpServer.respond(requestId, { status, headers, body })` for up to 32 KiB.
- `beginResponse`, `appendResponse`, `finishResponse` for a buffered response up
  to 256 KiB, appended in at most 32 KiB chunks across short callbacks.
- `httpProxy.forward` for native upstream forwarding or a WebSocket tunnel.
- `mediaStreams.serve` for processed media, or `subtitles.serve` for WebVTT.

The host owns HTTP framing and hop-by-hop headers. Do not supply `Host`,
`Content-Length`, transfer framing, CORS, or WebSocket-handshake headers yourself.
HEAD suppresses the body while preserving appropriate representation metadata.

For control request bodies, call `readBody(requestId)`, return from the callback,
then poll `requestStatus`. Once `bodyState` is `ready`, use `bodyChunk(id, offset,
count)`; each result has `body: Uint8Array`, `offset`, `totalBytes`, and `eof`.
Reading a body does not claim the eventual response. A proxy can stream larger
bodies natively; do not first consume those bodies into the guest.

Unclaimed requests expire after 15 seconds. `extend(requestId, seconds)` extends
from now but never beyond 120 seconds after arrival. Extend before model startup
only when you intend to retain that request; an expired request cannot be revived.
`cancelRequest` cancels only that request. Handle `request_not_found` and
`request_claimed` when a client disconnects or a terminal claim already won.

`http.closed` has `kind: "request"` and a `requestId` when an individual request
finishes. Listener closure has `kind: "listener"` and no `requestId`.
`http.disconnected` describes a canceled/disconnected request. These are lifecycle
hints: one disconnected segment request must not automatically kill playback.

`requestClose(serverId)` starts listener cleanup. Clear your saved server ID when
requesting close; after cleanup the handle no longer resolves. Stop callbacks
must not try to close an already-removed listener. See `examples/http-inspector`
and `examples/media-stream` for complete event-driven implementations.

HTTP limits: four listeners per addon, 16 per host, 128 connections and 16 upgraded
WebSockets per listener, 64 unclaimed and 128 active requests per addon, 64 KiB
request headers, 8 KiB request line, 256 KiB buffered request/response bodies.
The normal two-second callback and broker rate budgets still apply. Use short
timer callbacks to advance work; never busy-wait for a network or media operation.

## Native proxy and buffered metadata

`ajn.httpProxy.forward(requestId, destinationId, options)` claims the request and
returns `{ operationId }`. `options.path` is relative to the approved upstream
authority. Request/response bodies flow natively, including large media bodies,
HEAD, ranges, compressed payload bytes, and bidirectional WebSocket messages.

Options include `requestHeaders` and `responseHeaders` arrays of
`{ name, values }`; `values: null` removes a field. Repeated end-to-end headers are
preserved. Explicit `passRequestHeaders`/`passResponseHeaders` allow otherwise
excluded sensitive fields for this request. There is no shared cookie jar,
inherited account state or automatic redirect following. Authority/SNI, pinned
addresses, TLS verification, framing and forbidden headers remain host-controlled.

`buffer(destinationId, { path, method, body, maximumBytes, ...options })` performs
an asynchronous bounded upstream request without an incoming request claim.
Poll `status(operationId)`. Only after `completed` may `read` return the entire
body in chunks of at most 32 KiB. Maximum result size is 2 MiB; exceeding the cap
fails with `response_too_large` instead of exposing truncated success.
The request body is at most 32 KiB. Metadata transformations remain addon logic.

Poll the operation's structured status rather than assuming HTTP 2xx:
an upstream 401/403/redirect is an HTTP response, not successful authorization.
`cancel` requests cleanup. Once `cleanupReady` is true, `close` releases the
operation slot. Completed results remain owned until closed.

Limits: 16 retained operations per addon (at most four buffered), 64 per host;
15-second header and I/O deadlines; 24-hour absolute lifetime; 512 GiB native
transfer limit; 16 MiB WebSocket message limit with 32 KiB transport chunks.
Cancellation propagates through client disconnect, credential release and owner
shutdown. This capability is not a general TCP socket API.

## Request-derived credentials

For a client-authenticated integration, capture only the declared sensitive
fields from its owned pending request:

Declare custom credential fields in the manifest, for example
`"sensitiveRequestFields": { "headers": ["X-Client-Key"], "query": [] }`.
Built-in sensitive headers such as Authorization are already hidden. Declaration
does not grant capture or upstream access; the credential permissions still apply.

```javascript
const context = ajn.requestCredentials.capture(requestId, destinationId, [
    { from: "header", name: "Authorization", to: "header", target: "Authorization" }
], 3600);
const check = ajn.httpProxy.buffer(destinationId, {
    path: "/account", credentialId: context.credentialId
});
```

These variables belong to your event-driven session state. Return, poll `check`
on a later timer, and validate the upstream status/body before serving protected
content. Capture does **not** authenticate the client. AJN always reports
`authenticated: false`; the addon owns authorization and session policy.

Attach the context as `credentialId` on proxy options or a remote source descriptor
used for probing, playback, or external subtitles. It belongs to one addon
instance and one approved destination. Another destination requires its own
explicitly captured context. `useCredential: true` and a context cannot be chosen
together. A header/query field must have one credential source.

`status` exposes presence, placement and expiry, never values. `release` revokes
the context and cancels its active native operations and retained media delivery.
Do not release while a playback cache is intentionally still available. Up to 64
contexts, eight fields and 16 KiB captured values per context, at most 24 hours.
Keep separate context/session/generation mappings for different clients.

## Probe and track selection

A source is `{ type: "local", sourceId }` or
`{ type: "http", destinationId, path, useCredential?, credentialId? }`.
Call `ajn.mediaProbe.open(source)`, retain its `probeId`, poll `status`, and call
`result` only after completion. The SDK assembles the result from at most eight
32 KiB chunks. `read` provides the same chunks for custom assembly. Cancel active
work, wait for terminal status, then `close` to free the retained slot.

The probe supplies representation identity, container, duration/start,
seekability, chapters, video geometry/aspect/rotation/color/HDR/interlacing,
audio/subtitle tracks and attachment metadata. Track IDs belong to that source
representation. Reprobe after replacement; stale track IDs fail with `stale_track`.
Playback probes again before resolving explicit track choices.

Unknown values are null. Container average/nominal frame rates do not establish
CFR or VFR; `variableFrameRate: null` is deliberate. Local representation identity
uses file metadata plus bounded first/last fingerprints. Remote identity uses
the approved resource and stable HTTP validators when available. A validator-free
remote resource cannot promise stable explicit track IDs across separate opens.

Probe limits: two retained jobs per addon, eight active host jobs, 30 seconds,
64 MiB native probe-read budget, and 256 KiB result. Probing does not allocate an
encoder or upscale frames. Remote sources retain the existing validated-range,
pinned-address and no-redirect behavior.

## Open a served output

```javascript
const output = ajn.mediaStreams.open(source, profileId, {
    encoding: {
        videoCodec: "h264", container: "fragmentedMp4", videoKbps: 4000,
        audioCodec: "aac", audioKbps: 128, audioChannels: 2
    },
    playback: { startSeconds: 120, audioTrack: "default", subtitles: { mode: "none" } },
    mode: "segments", segmentSeconds: 1
});
// output: { sessionId, streamId, generationId }; no filename or public URL.
```

`outputs.open`/`openRemote` also accept a destination
`{ type: "servedStream", mode: "segments", segmentSeconds: 1 }` and return the
same handle. The legacy `httpUpload` destination remains supported.

Served output accepts approved DirectML or TensorRT profiles. TensorRT engines must be prepared before opening. All output requires the matching native
runtime. The initial baseline is progressive SDR with a supported NVIDIA encoder.
Known HDR/interlaced input is rejected rather than silently misrepresented.
`audioChannels: 2` explicitly requests stereo downmix. Audio codec omission still
means no encoded audio; `audioTrack: "none"` disables it even if a codec is chosen.

`startSeconds` is nonnegative, finite and absolute in the source timeline. Nonzero
offsets require seekable input; offsets at/beyond known duration fail.
`encoding.lengthSeconds` is duration after the chosen offset, or zero for EOF.
Audio selection is `"default"`, `"none"`, or `{ trackId }` from the current probe.

Delivery choices are continuous or segmented Matroska, MPEG-TS, or fragmented
MP4. Segment target duration is 0.5–6 seconds, default one second; actual durations
follow encoded keyframes and must be read from descriptors. Fragmented MP4 uses
an initialization object. Matroska chunks are not universally compatible HLS.
AJN exposes media objects; your addon creates manifests/routes for its client.

## Serve resources and manage production

Poll `mediaStreams.status(streamId)` and `segments(streamId, cursor, limit)`.
Pages contain up to 32 immutable segment descriptors, an optional initialization
ID, a continuous-resource ID when applicable, and a generation-bound next cursor.
An empty page means no newly published object yet, not necessarily EOF.

Each segment reports its sequence, source start/end, actual duration, encoded
timestamp origin, byte length, ETag, independence, and initialization reference.
Use `encodedTimestampOriginSeconds` when mapping encoded timestamps to the source
timeline. It can be null if the container cannot establish it. Never infer a
fixed frame rate or regenerate segment timing from a guessed FPS.

After authenticating and authorizing a request for this playback session:

```javascript
ajn.mediaStreams.serve(requestId, output.streamId, output.generationId, resourceId);
```

This claims the request and writes bytes natively. Resource/generation IDs isolate
ownership but are not client authentication. Initialization objects use the same
serve operation. One client must not receive another client's stream merely by
knowing its route. Reject unknown logical sessions before calling AJN.

Completed objects support GET/HEAD, ETag conditions and a single byte range
(closed, open-ended or suffix). Unsatisfiable/multipart ranges receive 416.
Growing continuous responses support sequential GET/HEAD, not ranges or seeking.
Late continuous readers begin at the retained beginning, not an arbitrary current
byte position. Use a replacement stream to begin at a new time.

Call `setDemand(streamId, absoluteSourceSeconds)` from actual client playback
progress. Downloading a segment is not proof that the client viewed it. Production
pauses when the next packet would exceed 15 seconds ahead and resumes when
updated demand lets that packet fit within the window. `pause(id,
true)` is a separate user pause; `pause(id, false)` resumes the same generation.
Periodic demand/pause ownership updates are needed during a long intentional pause.

Limits: four retained caches per addon and host, 2 GiB each (8 GiB reserved total),
16 readers per stream, two-minute segment-reader lifetime, 15-second I/O deadline,
24-hour absolute lifetime. Segments more than 30 seconds behind demand expire
only when no reader holds them. Continuous output retains its beginning and can
reach the 2 GiB cap. Storage pressure pauses writes for at most 15 seconds while
eligible objects are reclaimed, then fails with `buffer_limit_reached`.

No requests/demand updates for ten minutes expires the stream. A deliberately
paused stream can last two hours with ownership updates. The absolute 24-hour
limit still applies. Client disconnect cancels its response, not every response
or the whole logical playback. Your addon decides when playback is abandoned.

`producerCompleted` means encoding finished and native capacity was released;
cached objects remain available. `requestClose` is asynchronous and idempotent for
the retained stream handle. Wait for `closed`/`failed` and
`nativeCapacityReleased` before replacing it. `cleanupFailed` retains ownership
and capacity that could not be cleaned; retry close instead of starting an
unbounded replacement loop.

Status separates requested encoding (`native.encoding`) from observed output.
`encodedMedia` is measured from a completed encoded object, null before one is
inspected; continuous output supplies it at EOF. It includes actual codecs,
dimensions, pixel format, aspects and audio layout. The native status also gives
live pipeline observations. `measurements` reports measured media/wall speed and
effective bitrate with their intervals; unavailable values remain null.

`transfer.bytesProduced` counts published media objects (or growing continuous
bytes); `bytesServed` counts bytes actually read for responses, including repeat
reads. `cachedBytes` counts retained published media plus growing continuous
bytes. It excludes unfinished segments and metadata/index overhead, so it is not
a measurement of the directory's total disk allocation. The native quota still
covers all output files. `activeReaders` and `maximumCacheBytes` expose the current
reader count and per-stream storage limit.

## Seek, resume and track replacement

Retain your own logical playback ID. Cancel its pending old response requests,
request stream close, and poll until cleanup/capacity release finishes. Then call
open with the desired offset/tracks and replace the saved handle. This works with
one native session slot. A new stream always receives a new generation ID.

Inside-cache replay can serve retained objects, but `setDemand` cannot recreate
expired or unproduced media. An outside-range position produces
`stream_replacement_required`. For an actual seek, close/reopen works consistently
inside or outside the cache. `sessions.seek` rejects encoded streams. Ordinary
pause/resume keeps the generation. Reject requests carrying the old generation.

## Subtitles

Burn-in is selected in `playback.subtitles`:

```javascript
{ mode: "none" }
{ mode: "burn", trackId: selectedSubtitleTrackId }
{ mode: "burn", externalSourceId: approvedSubtitleSourceId }
{ mode: "burn", externalRemoteSource: { destinationId, path: "/subtitle", credentialId } }
```

Choose exactly one source. Embedded text/ASS/SSA and supported bitmap subtitles
are composed after processing and before encoding. Software composition requires
GPU download/upload and has a separate performance cost. Embedded font resources
remain native. Limits are 64 attachments, 16 MiB per attachment, 32 MiB combined.
External local/remote SRT, ASS/SSA and WebVTT require their own approval; remote
downloads have a 30-second/16 MiB bound and a separate credential scope. AJN does
not automatically search the filesystem or arbitrary subtitle URLs.

For text delivery, call `ajn.subtitles.open(source, { trackId, startSeconds,
endSeconds, allowStylingLoss })`, poll status, and serve the resulting WebVTT with
`subtitles.serve(requestId, subtitleId)` after request authorization. Track ID can
be omitted only for a standalone source containing one subtitle track and no
video/audio. Supported text codecs are ASS/SSA, SubRip, WebVTT, MOV text and text.
ASS/SSA conversion requires `allowStylingLoss: true`; positioning, fonts/drawings
are not preserved by plain WebVTT conversion. Use burn-in when those matter.
Bitmap extraction/OCR is unavailable.

Cues are clipped to requested bounds but retain absolute source timestamps; your
manifest/client must map that timeline deliberately. `read` returns bounded text
bytes if needed. Two retained extraction jobs per addon, four per host, five-minute
job deadline, 64 GiB scan budget, 16 MiB result, and 32 KiB read chunks. `cancel`
invalidates serving and stops active work. Poll terminal status and retry `close`
on `subtitles_active` until readers/work release. Context revocation also stops
serving already-extracted protected text.

## Common recoverable errors

| Code/state | What the addon should do |
| --- | --- |
| `permission_denied`, resource not granted | Explain the missing Manager permission/resource; do not retry continuously |
| `feature_unavailable`, `backend_unavailable` | Check capability and supported profile/runtime before showing the action |
| `listener_unavailable` | Close the failed listener; let the user resolve the port/certificate configuration |
| `request_not_found`, `request_claimed` | Drop that completed/disconnected request from pending state |
| `credential_not_found`, destination mismatch | End only the affected logical session; acquire/validate a new correctly scoped context |
| Upstream HTTP 401/403 | Deny client access; do not substitute another client's or owner's token |
| `input_not_seekable`, `position_out_of_range` | Offer a valid position/source instead of decoding an unlimited prefix |
| `stale_track` | Reprobe the selected representation before selecting tracks |
| `stale_generation`, `segment_expired` | Reject stale client work; use current manifests or replace the stream |
| `capacity_exceeded` | Close unused resources or wait for an existing owner to release them |
| `buffer_limit_reached` | Close and explain stalled playback/storage pressure; choose suitable client demand policy |
| `cleanupFailed` | Retry owned close; do not claim capacity or files were released |
| `subtitle_styling_loss` | Deliberately opt into text conversion or choose burn-in |

Use the generic `http-inspector`, `http-bridge`, and `media-stream` examples as
starting points. The media example deliberately accepts local clients only; a
public service addon must add client authentication, authorization, session policy,
protocol-specific manifests and deployment instructions. Those are addon decisions.
Never put tokens, full media paths, signed URLs or incoming headers in diagnostic
messages. The host's default diagnostics avoid those values.
