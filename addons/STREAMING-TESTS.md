# Streaming qualification and reproduction

API 1.7 implementation and qualification are separate. Read the matching release's
`build-info` provenance and test evidence before relying on a hardware/format
claim. An entry in `formats()` means the implementation is available, not that
your GPU, source or profile has passed these checks. The original integration.1
runtime has no streaming ABI and cannot run the new native tests.

Run GPU tests sequentially against a fresh copy of the exact candidate runtime.
Do not overwrite the previous release. Tests create their own fixtures, addon
data, loopback endpoints and temporary certificates; they do not need a Plex
account. FFmpeg below is an independent **test client/fixture tool**, not an addon
runtime dependency.

## Build the test executable

Use the .NET 10 SDK and build `tests/AnimeJaNai.Addons.NativeTests` in Release.
The common invocation is:

```powershell
dotnet build ./tests/AnimeJaNai.Addons.NativeTests -c Release
$tests = './tests/AnimeJaNai.Addons.NativeTests/bin/Release/net10.0/AnimeJaNai.Addons.NativeTests.dll'
$root = 'C:/AJN-candidate'
$dotnet = (Get-Command dotnet).Source
$ffmpeg = 'C:/TestTools/ffmpeg.exe'
$wasmtime = 'C:/TestTools/wasmtime.exe'
$javy = 'C:/TestTools/javy.exe'
```

Every output directory must be new. These are Windows x64 checks and require the
candidate's DirectML inference/models and a supported NVIDIA encoder. They do
not substitute software encoding if the hardware path fails.

| Invocation after `dotnet $tests` | Checks |
| --- | --- |
| `$root ./results/modes $dotnet --streams-only` | Native metadata, Unicode text extraction, all six container/mode combinations, independent audio/video decoding, 2x processing and frame counts |
| `$root ./results/timing $dotnet $ffmpeg --stream-timing-only` | CFR 23.976/24/25/29.97/30/50/60, VFR, actual numbered image content at nonzero offsets, segment boundaries and A/V timing |
| `$root ./results/subtitles $dotnet $ffmpeg --subtitles-only` | Embedded text, ASS styling/position/font, PGS bitmap, local and separately credentialed remote external text, none, cue timing and a selected second audio track |
| `$root ./results/runtime $dotnet $wasmtime $javy --stream-runtime-only` | Installed public Wasm example, approved remote input, HTTPS delivery, independent decoding, Manager disconnect, capacity-one replacement and resource revocation |
| `$root ./results/endurance $dotnet $ffmpeg --stream-endurance-only` | Five-minute pause/resume and 22-minute processing, independently decoded segments, counts, A/V timing and measured speed |
| `$root ./results/episode $dotnet $wasmtime $javy $ffmpeg --stream-episode-only` | Full 22-minute public Wasm remote-input-to-HTTPS delivery, received-byte decoding, startup/speed/throughput/cache evidence |

Results are JSON, with supporting media and logs in the output directory. A
failed test returns nonzero; inspect its error before retrying. Preserve both
failures and subsequent results so a passing retry cannot hide an unresolved
intermittent fault.

## What the fixtures prove

The timing fixture encodes each source frame number into eight broad luminance
cells. Reading those cells from independently decoded output detects wrong
preroll, missing frames and duplicates without trusting AJN's reported position.
The VFR fixture preserves nonuniform source timestamps. A/V checks compare the
first and last decoded presentation boundaries per segment, with a 100 ms limit.
They measure the defined fixture, not perceptual synchronization on every source.

At 24fps, Balanced slot 1002 selects AJN's SD model for the 480x360 fixture.
Balanced chains stop at 31fps; the 50/60fps checks select forced HD slot 1010.
The checks require actual 960x720 output. Record the installed model hashes, GPU
and driver with the result; do not extrapolate small-fixture throughput to 1080p
sources, stacked models, RIFE or a different graphics card.

The subtitle fixtures are original synthetic media. A tiny embedded font maps a
private-use character to a solid rectangle: a missing-font fallback cannot pass
the same color, position and fill measurements. ASS additionally draws a green
shape at a known position. PGS contains a known white bitmap. All cues run from
0.5 to 2.5 seconds; encoding from 1.0 seconds checks both visible preroll and
clearing. The font/PGS generation source is under the native test `fixtures`
directory; regenerating it uses fonttools 4.59.0. No font installation is needed.

## Other release gates

Run the complete host test suite with the pinned Wasmtime/Javy tools, including
API 1.0–1.6 examples and upload compatibility. Run Manager's addon UI checks and
the actual packaged updater/rollback tests from `RELEASE-INTEGRATION.md`. Extract
the standalone guide's tutorial/examples and build them with the distributed
tools. Validate the full and developer archives and their exact source/binary
inventories.

Keep a release evidence index listing each test output, binary hashes, hardware,
profile, source and subtitle mode. Any outstanding native, packaging or rollback
check remains an explicit release gate. Passing these framework checks does not
claim interoperability with official Plex clients; that belongs to the addon
project using the public APIs.
