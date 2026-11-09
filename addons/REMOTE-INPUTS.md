# Remote media input — API 1.5 preview

A Wasm addon can process an HTTP/HTTPS media resource from a service the user
approved in Manager. It can also connect that input to an approved encoded-output
receiver. The API supports independent concurrent sessions within the configured
host capacity. It does not implement a Plex addon or impose a Plex session policy.

Declare `remoteSources: { major: 1, minMinor: 0 }` in `requiredCapabilities`, or
discover it through `ajn.info().capabilities`. Declare `media.input`,
`sessions.manage` and `network.connect`; the user grants each independently.
Saved source headers additionally require `credentials.use`. Manager explains
that source approval covers media at all paths on that approved service.

Approve a service and processing profile first; no local-file grant is needed.
Obtain IDs from `ajn.network.selections()` and `ajn.sessions.selections()`.

```js
const source = {
    type: "http",
    destinationId: approvedSourceService.id,
    path: "/media/example.mkv",
    useCredential: false,
};
const { sessionId } = ajn.sessions.openRemote(source, approvedProfile.id);
// Observe initialization using sessions.status. No network body enters Wasm.
```

`ajn.outputs.openRemote(source, profileId, outputOptions)` uses the same encoding
and destination options as [outputs.open](OUTPUTS.md), with a separate
`media.output` grant. Input and output credentials are selected independently;
the host never sends one service's credential to the other. Short output duration
ends the input intentionally. Seeking an encoded output requires a new session.

The [remote inspector](examples/remote-inspector/addon.js) demonstrates processing,
pause/seek/close, capacity, service selection, and optional H.264/AAC upload. Its
startup is idle. The user explicitly invokes a processing or upload action. Its
session count is an example setting, bounded by host admission.

## Reading and seeking

Only a single Matroska/WebM, MOV/MP4, AVI or MPEG-TS resource is supported. No
HLS/DASH/playlist traversal, redirects, automatic refresh, or reconnect/resume is
implemented. The same approved IPs and origin apply to every GET. Normal HTTPS
certificate checks remain active. Browser cookies, proxy settings and operating
system credentials are not inherited. Responses must use identity encoding.

The reader initially requests `Range: bytes=0-`. A full `200` response works as
a forward-only stream, including chunked responses with no known length. `206`
responses must have consistent, bounded Content-Range and Content-Length values.
Seekable streams need a strong ETag, or no ETag and an eligible Last-Modified
date. The date must be at least 60 seconds older than the response Date; this is
the preview's conservative eligibility policy. Weak ETags never use the date
fallback. Subsequent GETs send If-Range; changed validators, changed lengths or a
server ignoring the condition fail the session instead of joining different
versions. See the [HTTP conditional-range specification](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.5).

`sessions.status` adds `input: { type, seekable, bytesRead, requests, errorCode }`.
Check `input.seekable` before seeking. `bytesRead` counts received bytes, including
rereads after seeking, rather than a download percentage. EOF completes normally;
incomplete bodies fail with `input_truncated`. Other codes distinguish response,
range/representation changes, unsupported encoding, deadlines and limits. Public
status never includes a source credential or its media path.

Native parsers only receive the existing private `ajnselected://media` stream
callback. Raw service URLs do not enter FFmpeg, and the native demuxer retains
its existing container/protocol restrictions. HTTP reading runs in the owned
native worker, using the trusted host's resolved private plan. Cancellation can
interrupt a blocked read without acquiring the callback's I/O lock. Source or
credential revocation stops the owning addon and its sessions before removing
the grant. Native codec and driver code remain trusted dependencies.

## Bounds and validation

`ajn.remoteSources.formats()` advertises the supported formats and these limits:

| Bound | Preview value |
| --- | --- |
| Transfer and declared media size | 512 GiB per input |
| Read buffer | 64 KiB |
| Transfer rate | 8 MiB/s per input, plus one read-buffer burst |
| HTTP response headers / connection | 16 KiB / 5-second connect deadline |
| Header or body I/O deadline | 10 seconds |
| Range requests | At most 32/s and 65,536 per input |
| Lifetime | 24 hours per input |
| Concurrent native sessions | User-configured 1–16, default two, shared with local/output sessions |

There is no separate global input bandwidth limiter yet; aggregate throughput is
bounded by the configured session capacity. Small control HTTP requests retain
their separate network API limits. File metadata can require more than one
range request during probing; the reader does not promise one request per video.

Tests cover exact reads, bounded headers, validators, chunked/ranged/forward-only
responses, changes, truncation, cancellation, deadlines and permission ownership.
Real Windows 11/RTX 5090 checks exercise the shipped JS example compiled to Wasm:
two independent 480×360→960×720 DirectML sessions, pause/seek/resume, forward-only
input, two HTTP-source→NVENC H.264/AAC uploads decoded and inspected at the test
receiver, and revocation of two blocked readers. The source and receiver are
synthetic loopback services. Long Internet streams, TLS service interoperability,
TensorRT/RIFE, other GPUs and the Linux adapter remain unvalidated.
