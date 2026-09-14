# Native media sessions — API 1.1 preview

The Windows host can run independent AJN processing sessions in supervised native processes. A real Wasm addon has been tested opening two DirectML sessions, producing 960×720 frames from a 480×360 video, pausing/seeking one while the other advances, and closing them cleanly. The native null output avoids downloading GPU frames merely to discard them. API1.2 additionally offers GPU-reduced SDR samples through the separate [frames capability](FRAMES.md). API1.4 adds [owned encoded outputs](OUTPUTS.md) to approved HTTP receivers, backed by the [native encoding/audio adapter](NATIVE-OUTPUT.md).

## Select media in Manager

Frame samples currently require DirectML/D3D11 and represent processed SDR
images. CUDA/TensorRT and final-display samples are unavailable. The owned
worker opts into `vo-null-accept-hwframes`; ordinary null-output benchmarks keep
their previous default. See [release integration](RELEASE-INTEGRATION.md).

Use the integrated native-media preview. Install a local addon requesting `sessions.manage`, approve that permission, then use its **Processing access** section:

1. **Allow a media file**: choose one local video and review its exact path and the addon receiving access.
2. **Allow a saved profile**: choose a built-in or saved slot and backend. Manager copies the saved AJN configuration into a private snapshot; subsequent edits to player settings do not change existing approvals or sessions.
3. Start the addon and use its declared actions. The generic `session-controller` example processes the first approved source/profile and supports independent session controls.
4. **Remove access** stops the addon and drains its sessions before removing that selection. If cleanup fails, the approval is retained and the operation reports an error; retrying continues cleanup. Removing the addon also removes its media approvals and preserves ordinary settings/storage.

Approvals belong to the exact package hash, not merely the addon name. New package versions see no previous file/profile approvals. Up to 16 sources and eight profile snapshots can be approved per addon. Files are read-only. Supported local container families are MP4/MOV, Matroska/WebM, AVI and MPEG-TS. Playlists, network paths, external references and automatic sidecar loading are disabled. API1.5 provides separately approved [HTTP media sources](REMOTE-INPUTS.md).

## Public contract

Declare API 1.1 and required capability `sessions: { major: 1, minMinor: 1 }`, plus permission `sessions.manage`. Optional consumers can inspect `ajn.info().capabilities.sessions`. Capability availability and user permission are separate checks. API 1.5 additionally offers [approved HTTP media sources](REMOTE-INPUTS.md), with a separate input permission and capability.

```javascript
const resources = ajn.sessions.selections();
const {sessionId} = ajn.sessions.open(resources.sources[0].id, resources.profiles[0].id);
// Later events/actions:
const state = ajn.sessions.status(sessionId);
ajn.sessions.pause(sessionId, true);
ajn.sessions.seek(sessionId, 30);
ajn.sessions.requestClose(sessionId);
```

Source/profile IDs are opaque and specific to this addon package. Session IDs belong to this running addon instance. Guests cannot pass paths, native options, commands, library names or handles. They cannot use another addon's selections or sessions. Omitting a profile is allowed only when exactly one profile has been approved.

Opening returns promptly while decoding/model initialization proceeds. Cached status is refreshed about four times per second: `starting`, `opening`, `loading`, `running`, `paused`, `completed`, or `failed`. Optional fields include position/duration in seconds, seeking, input/output dimensions, pixel format, decoder, and the last native command result. Missing/unknown numeric values are null. Pause/seek are accepted asynchronously; read status on a later event to observe the result. Native errors are reported in status. A failed/completed session still owns its reservation until closed.

Use **requestClose** for native sessions. It starts cleanup without occupying the addon's two-second callback deadline. Status is `closing` during cleanup, or `cleanup_failed` if a retry is needed. After successful cleanup, the handle returns `session_not_found`. Calling requestClose again retries failed cleanup. The API 1.0 `close` method retains its original synchronous-release semantics; it is not suitable for potentially slow native shutdown. No existing method changed semantics in API 1.1.

Each addon decides how many independent sessions it needs within host limits. The default native capacity is two across all addons. Manager's **Performance limits** saves a limit from 1 to 16. Changes affect new admissions; existing and reserved sessions continue. The trusted operator can also supply an explicit read-only override when launching:

```text
ajn-addon serve <data-directory> <wasmtime.exe> <trusted-AJN-root> [maximum-media-sessions]
```

Without the AJN root argument, the host remains usable without native media and does not advertise sessions. Manager opts into the packaged runtime when `addon-host/native-media.json` is present. Capacity is admission control, not a guarantee that every GPU can run that many selected models. Engine creation is on demand; no TensorRT engines are distributed by this addon package. The current desktop evidence is DirectML; TensorRT/RIFE combinations require additional validation.

Frame-capable packages also include `addon-host/native-frames.json` with
`privateSampleAbi: 1` and `mpvSha256` equal to the packaged `libmpv-2.dll` digest.
The host advertises samples only when both match. A missing, incompatible or
stale marker leaves ordinary native sessions available without advertising a
private filter that the installed library cannot provide. Addon authors should
use public capability discovery; they do not need this private native ABI.

## Private implementation and failure ownership

The native worker uses libmpv internally; its stream callback ABI is a pinned implementation dependency, not part of the addon API. The selected file is opened once with a read-only handle and served through one private URI. Container parsing is limited, and reference/protocol opens are disabled. The UI supplies trusted profile snapshots; addons cannot alter them.

Each media process has a separate Windows Job: 2 GiB process memory, 4 GiB job memory, at most two processes, and no CPU hard cap. The extra process allows a trusted engine compiler. These limits do not bound GPU VRAM. The Wasm guest retains its separate 64 MiB linear-memory limit, 512/768 MiB process/job limits and 25% CPU cap. Native processing stays outside the Manager/service/Wasm processes; the native worker is trusted AJN code, not an additional filesystem permission sandbox.

The parent requires a status heartbeat within 15 seconds and allows three seconds for graceful shutdown before terminating the native job. Private configuration/cache folders are removed only after their process job empties. Failing cleanup keeps reservations and ownership for retry; revocation cannot report success while forgetting a resource. Independent sessions drain concurrently when an addon stops. A host crash terminates its owned jobs.

The backend, runtime and codec/driver dependencies remain maintenance responsibilities. Process separation cannot guarantee recovery from a system-wide GPU driver failure. Frame samples now use GPU reduction, explicit stage/color/timestamp semantics and a bounded binary transport; their support and performance limits are documented in [FRAMES.md](FRAMES.md).

## Validation

The normal host suite includes persisted approvals, package/owner separation, revocation retry, pending opens, asynchronous close and real Wasm cleanup. The GPU suite is opt-in and requires a matching native runtime/models and supported hardware:

```text
dotnet run --project addons/tests/AnimeJaNai.Addons.NativeTests -c Release -- <AJN-root> <new-results-directory> <dotnet.exe> <wasmtime.exe> <javy.exe>
```

It runs both the native adapter and a real compiled JavaScript/Wasm addon. GPU-less CI runs contract, worker, Manager and native C tests separately; it does not claim a successful GPU test.
