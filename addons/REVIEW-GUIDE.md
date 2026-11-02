# Addon API 1.12: maintainer review guide

This is the entry point for reviewing the complete Windows addon implementation across repositories. The API remains a development preview. The developer package is based on AJN 3.9.0; adding these sources does not make the public 3.9.0 binaries addon-capable.

## Repository map and review order

The companion pull requests and their source branches are linked from the main PR description. Review the complete set before publishing an addon-enabled package. The mpv submission uses linear history and includes current upstream changes. Review follow-ups change runtime behavior; use the source-specific validation linked from the PRs.

| Repository | Responsibility | Start reading |
| --- | --- | --- |
| [mpv-AnimeJaNai #227](https://github.com/the-database/mpv-AnimeJaNai/pull/227) | Wasm host, SDK, grants, lifecycle, media services, updater/installer integration and documentation | This guide, [architecture](ARCHITECTURE.md), `addons/src/AnimeJaNai.Addons/Broker.cs`, `shared/AddonUpdateTransaction.cs` |
| [AnimeJaNaiManager #20](https://github.com/the-database/AnimeJaNaiManager/pull/20) | Addons UI, installation review, typed settings/actions, access selection, host startup and shutdown | `ADDONS.md`, `ADDON-API-REVIEW.md`, `AnimeJaNaiConfEditor/Views/AddonsView*`, `addon-sdk-source.json` |
| [animejanai-inference #40](https://github.com/the-database/animejanai-inference/pull/40) | Optional scene override, cached-engine streaming policy and separate CUDA analysis module | `docs/ADDON-API.md`, `include/aji.h`, `include/aji_analysis.h`, `src/aji_analysis.cpp` |
| [mpv #69](https://github.com/the-database/mpv/pull/69) | Trusted sampling/scene hooks and isolated media probe, encode, mux and subtitle plumbing | `DOCS/ajn-addon-integration.md`, `DOCS/ajn-sample-worker.md`, `etc/ajn-native-capabilities.json` |
| [mpv-winbuild #8](https://github.com/the-database/mpv-winbuild/pull/8) | Native feature checks, optional addon artifact metadata, dependencies and reproducible source records | `test-build/README.md`, `test-build/ADDON-API-REVIEW.md`, native producer/staging tests |

There is no new libass change in this set. The existing AJN libass features remain build requirements. Camera-pan smoothing, custom presentation synchronization, the separate refresh-scan fix and unrelated inference optimization PRs are not part of these addon diffs.

Read the native contracts before changing the host adapters. For integration, build inference and the addon mpv revision, produce native metadata with winbuild, build Manager and the host, then assemble the main package. Main's build/installer support is opt-in; merging source is not a replacement for producing and validating the matching native artifacts.

## Documentation for addon authors

**Give an author or another coding assistant [DEVELOPER-GUIDE.md](DEVELOPER-GUIDE.md).** It is the self-contained release document: environment setup, complete tutorial, manifest and permission rules, API declarations, error handling, examples, deployment and troubleshooting. The author still supplies the desired addon behavior and any external device/service protocol.

Shorter references:

- [Creator tutorial](CREATOR-GUIDE.md), [recipes](CREATOR-RECIPES.md), [SDK types](sdk/ajn.d.ts) and [SDK implementation](sdk/ajn.js).
- [User installation and operation](USER-GUIDE.md), [troubleshooting](TROUBLESHOOTING.md), [management transport](MANAGEMENT.md) and [lifecycle](LIFECYCLE.md).
- [Streaming](STREAMING.md), [preparation/readiness](STREAMING-READINESS.md), [HTTP services/listeners/proxies](HTTP-SERVER.md), [network selections](NETWORK.md) and [remote media](REMOTE-INPUTS.md).
- [Player observations](PLAYER-FRAMES.md), [source color and character masks](PLAYER-ANALYSIS.md), [scene decisions](SCENE-DETECTION.md) and [RIFE Settings](rife-settings/README.md).
- [Release assembly and recovery](RELEASE-INTEGRATION.md), [qualification](PLAYER-ANALYSIS-VALIDATION.md) and [remaining work](ROADMAP.md).

All of these documents, the SDK, source and examples are tracked in this PR. `addons/tools/package.ps1` produces offline HTML guides and a standalone developer bundle. `tools/assemble-addon-preview.py` includes them in the integrated player package, together with source archives and build provenance.

## Runtime and ownership

Manager and the developer CLI are trusted management clients. A persistent local service owns the package registry, approvals, settings, credentials and resource accounting. An addon runs as WebAssembly in a supervised Wasmtime worker; JavaScript is compiled with Javy. This is neither Node.js nor a browser. The guest has no general process-launch, native DLL, filesystem or network API.

The broker binds identity and grants to the worker. It exposes versioned operations with opaque handles; passing a different addon ID cannot transfer ownership. Declared settings and actions become Manager controls. Activation keys from players, Manager, manual start and opt-in Windows login share one addon controller. Timers coalesce missed work rather than generating an unbounded callback backlog. Workers and native media helpers have bounded lifetimes and resource limits.

Independent media sessions and observing an existing player are separate operations. An addon chooses its own session count within user/host limits. Plex discovery/authentication/client compatibility and lighting estimation, calibration, smoothing and device protocols belong to the addons; the framework does not implement those products.

## Permission and trust boundaries

Review `AddonPackage.cs`, `AddonService.cs`, `Broker.cs`, the management transport and each selection provider together. Package validation, resource ownership and approved destinations must remain enforced at their actual boundaries.

- A manifest permission is not blanket access to every file or device. Media, saved profile snapshots, destinations, credentials and listeners also have reviewed selections bound to the addon/package. Revocation closes affected work.
- Addon RPC cannot use the trusted Manager protocol. The management pipe is restricted to its Windows owner; this does not isolate AJN from arbitrary native code already running as that same user.
- The native player, inference libraries, host, Wasmtime runtime and installed helper DLLs are trusted. Wasm isolation does not prove those components or a GPU driver free of defects. AppContainer enforcement and Linux containment are not claimed.
- Package hashes provide integrity and grant binding, not publisher identity. Signed catalogs, one-click website installation and automatic community-addon updates remain future work.
- The optional shipped MVTools helper is trusted AJN code with a fixed interface and provenance. Addons cannot supply or load arbitrary DLLs through it. Its enabled detector uses CPU analysis and frame readback; it is not the GPU-only character-mask path.

## Inactive cost and image semantics

The native sampling filter, optional analysis DLL, model setup, sample buffers and analysis worker are activated only for a granted subscription. Last unsubscribe, lease expiry, revocation or worker/player exit releases the producer. An idle installation still has the existing lightweight discovery/lifecycle machinery; "no optional frame work" is not a claim that every management instruction costs zero.

When enabled, source character analysis shares one producer per player. Decode and model inference remain on the GPU; only reduced color/mask data is read back. Busy producers skip samples instead of accumulating full frames. Per-reader size/rate limits and epochs protect against stale or mismatched samples. Source observations are before upscaling/RIFE, display transforms, subtitles and OSD. BGRA8 does not imply sRGB, and processing timestamps do not measure physical display latency.

The current mask producer supports progressive mono SDR CUDA input and the fixed, hash-verified IS-Net model. The host requires matching TensorRT/CUDA runtime components; permission to download the ONNX does not install those components. HDR, software input and D3D11 character segmentation are reported unsupported. D3D11 color sampling is a separate supported path. Exact limits and failure states are in [PLAYER-ANALYSIS.md](PLAYER-ANALYSIS.md).

## Compatibility and updates

The public addon API, application release, per-capability version and private native ABI are different version spaces. The 3.9.0 port retained API 1.12 and byte-identical SDK files. Addons request the minimum API/capability versions they need and inspect availability; they must not infer a native capability from an AJN version string alone.

Keep `addons/tests/fixtures/counter-api-1.0.ajnaddon` unchanged when testing old binaries. Native DLL additions are additive to AJI ABI 9 and discovered separately. Private native maps/callbacks are implementation contracts, not addon-facing pointer or DLL access.

Installations retain current/previous package and grant state independently from saved settings/data. Permission expansion requires review. The updater validates a coherent addon inventory, drains the installation's owned work, and journals replacement/recovery. A normal non-addon release is allowed: it preserves `animejanai/addons`, removes addon host/bridge files, and leaves addons inactive until an addon-enabled build is installed again. Only changed files are staged/backed up, after a free-space check. Preview component downloads are pinned to the corresponding public component release; replaced native/Manager/inference components record their actual source revisions.

## What the evidence establishes

Use the validation record linked from the coordinated PR descriptions. Each record
identifies its exact source commits, runtime, hardware and limitations; a previous
preview pass is not a test of a newer PR head. Dated GPU-machine results are kept
as review/build artifacts rather than maintained source files.

The Windows addon host is the first implementation. Linux keeps ordinary AJN
playback and builds without the optional observation module. Manager hides the
Addons tab there. A future Linux host must implement equivalent isolation before
enabling these capabilities.

## Reproducing the relevant checks

Use Windows x64, PowerShell 7 and .NET SDK 10. Native builds need the toolchains documented by their repos. Use fresh output/data directories, an isolated installation and owned test processes; do not overwrite a daily-use install.

From the main repo: `addons/tools/bootstrap.ps1`, then `addons/tools/test.ps1`, and `addons/tools/check-docs.ps1`. The host test script accepts `-Dotnet` and `-OutputDirectory`. The existing native test project is `addons/tests/AnimeJaNai.Addons.NativeTests`; its focused modes include `--analysis-only`, `--analysis-long-path-only`, `--scene-only`, `--streams-only` and `--streaming-policy-only`. Consult their argument handling and the corresponding checks for media/runtime prerequisites. New paths are mandatory for reproducible evidence; caches should be retained or cleared only as required by that particular test.

From Manager: run `tools/check-addon-client.ps1` with the canonical management client, `tests/ProfileRoundTrip`, `tests/Diagnostics`, and `tests/AddonUi`. The latter accepts a fresh result directory and optionally an integrated preview root. Its fixture-only run cannot establish live player behavior.

For native inference, use `tools/build-windows-test.ps1` and inspect its exact recorded source revision. For mpv/winbuild, use the producer's source pins and native tests described in their review documents. Reuse successful unchanged checks; rerun the affected paths when code, ABI, dependencies or permission handling changes. Compilation alone is not proof of GPU behavior.

## Remaining release decisions

API freeze, publisher identity/catalog distribution, Linux enforcement, additional GPUs/formats, physical light output and official Plex-client interoperability still require separate decisions or qualification. No PR in this set claims those complete. Review source ownership, cancellation, cache/build policy and the disabled paths before enabling the framework in a public release.
