# Encoded media output — API 1.4 preview

`outputs` capability 1.0 connects an owned AJN processing session to a separately
approved receiver. The first adapter sends a chunked HTTP/1.1 POST or PUT. It
does not implement Plex, a web server, discovery or arbitrary native plugins.
Windows is implemented first; public messages contain no OS handles or paths.

## Permissions and resources

Opening an output requires **`media.output`**, **`sessions.manage`**, and
**`network.connect`**. These are independent of capability discovery. The user
must separately approve the source file, saved profile snapshot and destination
for this exact addon package. Manager explains that an output-enabled addon can
send processed video and audio to the reviewed service. Updates do not silently
inherit an old package's resource or permission grants.

Only HTTP/HTTPS destinations are accepted. The host connects to their reviewed
IP addresses and retains normal HTTPS certificate/hostname validation. It does
not follow redirects or inherit browser cookies, system proxy settings or OS
credentials. Optional saved request-header credentials require `credentials.use`
and `useCredential: true`; the value never enters the guest response. Revoking
media/network access or changing a credential stops the addon and drains its
outputs before changing the grant. See [NETWORK.md](NETWORK.md).

## SDK

```javascript
const resources = ajn.sessions.selections();
const receivers = ajn.network.selections().destinations;
const formats = ajn.outputs.formats();
const { sessionId } = ajn.outputs.open(resources.sources[0].id, resources.profiles[0].id, {
    encoding: {
        videoCodec: "h264", container: "matroska", videoKbps: 4000,
        audioCodec: "aac", audioKbps: 128, keyframeFrames: 60, lengthSeconds: 2
    },
    destination: {
        type: "httpUpload", destinationId: receivers[0].id,
        path: "/media", method: "POST", useCredential: false
    }
});
// In a later action or host timer callback:
const state = ajn.sessions.status(sessionId);
// Retain the ID until cleanup has finished; it then returns session_not_found.
ajn.sessions.requestClose(sessionId);
```

Choose actual approved selections; the example's `[0]` indexes are illustrative.
The [output inspector](examples/output-inspector/addon.js) exposes selection and
format settings, multiple sessions, status, pause/resume and close actions. It
sends nothing on startup. **Start** it before choosing **Send processed media**;
actions on a stopped addon run temporarily and release their work afterward.
Use a receiver you control that accepts streaming request bodies. Merely running
a normal file-serving HTTP server does not provide an upload endpoint.

`outputs.formats` requires `media.output` and `sessions.manage`; it describes
this adapter's options, bounds and host session limit. It is not a GPU/driver or
remote-client compatibility probe. This preview uses NVIDIA NVENC even when AJN
inference uses DirectML. Unsupported hardware or input formats produce a failed
session. Other encoders/transports can be added through capability negotiation.

| Choice | Current adapter |
| --- | --- |
| Video | H.264 (`h264`), HEVC (`hevc`), AV1 (`av1`) |
| Container | Matroska (`matroska`), MPEG-TS (`mpegts`), fragmented MP4 (`fragmentedMp4`) |
| Audio | Disabled (`none`, SDK default), AAC (`aac`), Opus (`opus`) |
| MPEG-TS combinations | H.264 or HEVC; audio disabled or AAC |
| Bitrate | Video 256–50,000 kbps; audio 32–512 kbps, default 128 |
| Keyframe interval | 1–600 frames, default 60 |
| Duration | 0 for the source's remaining duration, or up to 86,400 seconds |
| Receiver | Approved HTTP/HTTPS origin, relative path, POST or PUT |

Encoding starts at the beginning of the selected source. Seeking requires a new
output; pause/resume retains the current timeline. The receiver sees one muxed
video stream and optional selected audio track. Software subtitles/OSD are
disabled on the direct hardware path. Encoding proceeds at the producer's rate
with transport backpressure; this is not a guarantee of realtime pacing or a
particular client latency. Format/color limitations and native details are in
[NATIVE-OUTPUT.md](NATIVE-OUTPUT.md).

## Ownership, limits and completion

Each open returns promptly with an owned `sessionId`. Use the existing session
API for status, pause and asynchronous close. Output and non-output sessions
share admission; the default native limit is two, configurable by the host up
to sixteen. Addons choose how many to request within that capacity. A completed
or failed handle retains its admission slot until explicitly closed or its
owner stops. Failed cleanup retains ownership/capacity and can be retried.

Manager's **Performance limits** changes the shared native-session limit. Lowering it
preserves active outputs and prevents new admission until enough handles close.

Full-resolution frames and continuous encoded bytes stay in trusted components:
native GPU processing/encoding → private bounded pipe → HTTP transport. They do
not cross guest JSON-RPC. Optional `frames.read` sampling remains a distinct,
permission-checked, reduced-image path.

The transport uses a 64 KiB copy buffer, with native codec/muxer memory covered
by the existing process/job limits. It permits 8 MiB per one-second window per
output and 32 MiB per host window; those are throughput budgets with bursts,
not smooth constant-rate pacing. A slow receiver backpressures its own producer.
Limits are 512 GiB per output and 24 hours of wall time, including pauses. Each
network write has a 15-second deadline; after the last encoded byte, the final
HTTP response has 15 seconds. Native termination gets three seconds of grace
before its job is terminated. Cleanup waits for the pending pipe read too.

Status retains native dimensions, codec/pixel-format and processing information,
plus `output: { type, destinationId, method, encoding, bytesSent, httpStatus }`.
`bytesSent` counts bytes accepted by the host's HTTP content stream; it is not a
remote durable-storage receipt. `opening`/`running`/`finishing` precede a terminal
`completed` or `failed`. `completed` requires native completion, complete upload,
a final successful HTTP status and producer cleanup. Early success, rejection,
redirects, transport failure and native failure cannot be reported as complete.
Errors include `output_rejected`, `output_incomplete`, `output_unavailable`,
`output_cancelled`, `native_output_failed` and retryable `cleanup_pending`.

HTTP/1.1 `HttpClient` waits for content serialization before returning a final
response. The host uses `Expect: 100-continue` and a bounded, read-only
[plaintext stream observer](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.plaintextstreamfilter?view=net-10.0)
to cancel serialization when the service replies early. The standard HTTP
client still parses/validates the response and performs TLS validation; the
observer never accepts a response or grants authority.

## Runtime and validation

The native provider advertises outputs only when `addon-host/native-output.json`
matches private output ABI 1, the UCRT runtime, and the exact player and FFmpeg
muxer DLL hashes. This is a package-consistency check, not a publisher signature.
Earlier previews without that marker remain usable without output capability.

The ordinary suite tests permission/resource ownership, byte-exact uploads,
scoped credentials, rate limits, fragmented/interim/early responses, native and
cleanup failure, cancellation, and the real Wasm inspector. The Windows GPU
suite sends synthetic processed media to a loopback receiver, decodes H.264,
HEVC and AV1 with audio, checks dimensions/SDR color/short A/V timing, runs two
outputs, and revokes source/destination access while workers are owned.

```text
dotnet run --project addons/tests/AnimeJaNai.Addons.NativeTests -c Release -- <AJN-root> <new-results-directory> <dotnet.exe> <wasmtime.exe> <javy.exe> --outputs-only
```

Validated hardware is Windows 11 / RTX 5090 with DirectML. The two-second fixture
checks A/V timestamps within 150 ms; it does not certify long-stream sync,
variable-frame-rate behavior, streaming latency, all source formats or other
GPUs. CUDA/TensorRT, RIFE, HDR/final display and software subtitle composition
still need their own integration and hardware validation. Remote sources,
listeners and additional output transports remain separate capabilities.
