# Native encoded output adapter

This trusted integration component backs the optional [outputs capability](OUTPUTS.md)
in API 1.4. The guest chooses validated codec/container settings and approved
resource IDs. It cannot pass raw encoder flags, output paths, descriptors or
pipe handles. Each output belongs to one addon session and an approved receiver.

## Implemented path

The supervised media worker can decode an approved local source, apply an AJN
profile, encode the processed video with the selected NVENC or AMF encoder, optionally encode audio, and
mux an output stream. A private unnamed pipe carries only encoded bytes to a
trusted consumer in the host. Full video frames stay out of Wasm and its RPC
channel. The normal sample subscription remains a separate small-image path.

The current Windows adapter maps typed options to H.264, HEVC or AV1 with
Matroska, MPEG-TS or fragmented MP4. Audio can be disabled or encoded as AAC or
Opus. The MPEG-TS adapter accepts H.264/HEVC with optional AAC. These are tested
adapter choices, not a promise that every client supports every combination or
a permanent limitation on the public platform. Later adapters can add codecs
and containers through capability negotiation.

The private option bounds are 256–50,000 kbit/s video, 32–512 kbit/s audio,
1–600 frames between keyframes, and an optional duration up to 24 hours. NVENC
uses its low-latency tuning, with B frames and lookahead disabled. This has not
yet been certified for an end-to-end streaming latency target. The public API
distinguishes adapter options from actual hardware support and host
resource admission. Inference and encoding are independent selections; see [supported combinations](STREAMING-READINESS.md).

## GPU and color handling

The API 1.9 host converts processed frames to NV12 (8-bit) or P010 (explicit
10-bit HEVC) in the native worker. Software subtitles are composed after AI
processing. Full frames stay outside Wasm. CPU staging must be included in
performance measurements. Served sources currently require progressive SDR.

## Ownership and backpressure

The host creates a pipe with a 64 KiB requested OS buffer and duplicates only
the write end into its already-supervised native worker, before releasing the
execution gate. The host releases its local copy of that write handle. The
worker transfers ownership into the shared Windows UCRT and FFmpeg duplicates
the descriptor for its pipe protocol. Descriptors close after encoder/muxer
flush and native destruction. This private adapter requires the packaged UCRT
FFmpeg build; it is not a portable public file-descriptor ABI. Producer metadata
supports static FFmpeg inside libmpv as well as the older shared-FFmpeg previews.
The static layout needs no separate `avformat` DLL. See
[release integration](RELEASE-INTEGRATION.md) for producer validation and the
required hardware test gates.

The DLL graph remains loaded for the native process's lifetime. Individual mpv
contexts are destroyed normally, but the adapter does not unload and reload
libraries with process-wide callbacks. This avoids a reproduced ggml terminate-
handler initialization crash. Each production session still has its own
process; process exit releases the library and its dependencies.

There is no unbounded managed output queue. A slow reader fills the pipe and
backpressures that producer. Native encoder/muxer buffering remains subject to
the existing process/job limits; the pipe capacity alone is not a bound on all
codec memory or GPU VRAM. The original frame samples use a separate drop-when-
busy policy and must not share this blocking transport.

Closing an output allows three seconds for native shutdown, then terminates
that session's job if needed. Other sessions keep their own jobs and pipes.
Loss of a consumer is an output failure. Completion is reported after native
flush/destruction and pipe EOF. Seeking a muxed stream requires a new output
session; the existing stream cannot silently reset its timeline. The
public owner retains capacity until both producer and destination cleanup
complete, including retryable failures.

Ownership follows [Microsoft's `_open_osfhandle` contract](https://learn.microsoft.com/en-us/cpp/c-runtime-library/reference/open-osfhandle?view=msvc-170)
and [FFmpeg's pipe protocol](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/file.c).

## Validation and next integration

On Windows 11 with an RTX 5090, the generated moving fixture was processed
from 480×360 to 960×720 with DirectML. H.264, HEVC and AV1 encoded without mpv's
CPU download step; decoding all 48 frames matched the previous CPU-download
path pixel for pixel. The native CI also encodes and reopens a YUV fixture to
check preserved BT.2020/PQ/matrix metadata, independently of GPU availability.

The managed native suite exercises private-pipe output, complete video/audio
decoding, preserved SDR color/dimensions, two concurrent encoders, a broken
consumer, and an unread output alongside independent playback. The latter
remained bounded and closed in about 3.1 seconds on the test machine. The short
audio fixture checks timestamps within 150 ms; it is not a long-running A/V
synchronization, seek, packet-loss or streaming latency certification. The public
integration also exercises real Wasm controls, HTTP delivery followed by full
decoding, two owned uploads, and active source/destination revocation.

Run the GPU adapter checks with a matching native build and installed models:

```text
dotnet run --project addons/tests/AnimeJaNai.Addons.NativeTests -c Release -- <AJN-root> <new-results-directory> <dotnet.exe> --encoding-only
```

Next: additional delivery adapters and listener responses; remote media
sources; deeper error/format reporting; longer A/V timing checks and the hardware
matrix. No Plex bridge or lighting addon is implemented by this component.
