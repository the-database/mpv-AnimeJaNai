# Samples from normal AJN playback

API1.6 adds optional `playerFrames` capability1.0. An addon can observe small
images from the user's ordinary mpv/mpv.net playback without opening its own
processing session. This is a framework capability; the player inspector is a
generic diagnostic example, not a lighting integration.

## Consent and API

Both `player.observe` and `frames.read` must be requested and explicitly granted
to the exact package hash. The Manager review describes observation as **Read
small image samples from videos played in AJN**. Owned-session grants do not
implicitly permit it. `sessions.manage` is not required and no pause/seek/open
controls are granted. Network access remains separately permissioned.

| Method | Result |
| --- | --- |
| `playerFrames.list()` | Attached players as opaque `playerId`, `stage`, `format` records; no title, filename or process identifier |
| `playerFrames.subscribe(playerId, options)` | Owned subscription ID and accepted `stage`, `format`, `width`, `height`, `maxFps` |
| `playerFrames.read(subscriptionId)` | Latest unread `AjnFrame` with bounded binary pixels, or `null` |
| `playerFrames.unsubscribe(subscriptionId)` | Releases this observer; other addons and playback continue |

Sample options and metadata match [FRAMES.md](FRAMES.md): BGRA8, processed SDR,
1..320 by1..180 pixels,1..60 Hz; source geometry, crop, rotation, pixel aspect,
color metadata, media PTS and seek/file epochs are retained. This point precedes
final display tone mapping, subtitles and OSD. `ptsSeconds` is media processing
time, not a measurement of when a monitor displayed the image.

Each player has one producer. Its negotiated width, height and rate are the
maximum of current subscriptions. The host reduces that small image to each
reader's dimensions with bilinear interpolation in the encoded SDR values.
Readers keep independent frame IDs and rate limits; an unread sample is replaced,
never queued. The host copies only these bounded small images into Wasm.

The preview admits four attached players, eight readers per player and four per
addon instance. Existing worker binary/control quotas also apply. These are
resource ceilings; an addon chooses its own usage within them. Owned processing
sessions retain their independent user-configurable capacity.

Closing an addon releases its subscriptions. Closing a player invalidates its
subscriptions with `player_closed`; unsubscribe still releases the handle.
Unsupported formats report `frame_format_unavailable`; a native readback failure
reports `frame_unavailable`. A lease with no fresh producer can return `null`.
Do not interpret it as a black frame. Use host timers for future reads.

## Try the inspector

1. Use a full preview containing the matching native observation adapter.
2. Install `player-inspector.ajnaddon` and review its two unchecked permissions.
3. Open a video in AJN. Start the inspector and choose **List attached players**.
4. Choose a player number, sample dimensions and rate in settings, then choose
   **Observe selected player** and **Show latest sample**.
5. **Stop observing** removes that subscription. Stopping the addon does the same.

The inspector sends no network traffic. Addon actions on a stopped addon are
temporary: keep the inspector running between subscribe and sample actions.
An already-open player sees its first installed addon on its next launch.

## Trusted integration

Only the shipped Lua bridge configures the private native filter. A trusted
launcher holds the original player process, connects with its fixed lifecycle
role, and exchanges narrowly typed registration/configuration messages. It writes
a bounded, atomic per-instance control file. No general mpv command forwarding,
IPC endpoint ownership or raw OS handles are exposed to an addon.

The host creates a fresh named mapping with a protected DACL granting its Windows
user access and denying network logons. Existing object names are rejected. The
filter opens its own view and closes the opening handle; no foreign-process
handle is left for the host to reclaim. Private mapping names never enter the
guest protocol. Public registry interfaces keep Windows details out of the API.

The host renews a three-second monotonic lease. Both native capture and the Lua
configuration bridge stop using stale leases. Removing the last subscriber clears
capture immediately; Lua removes its own labeled filter on the next poll. With
no installed addons, the original zero-helper startup path remains. With no
observers, no sample filter or GPU sampling work is requested.

Normal-player teardown signals retirement without joining the GPU readback thread
or waiting on its lock. The source image stays referenced until readback completes
or device removal is confirmed. Four live/retiring branches per process bound
retained driver work during reconfiguration; exhaustion leaves observation inert
and video passing through. Retired polling backs off after250ms. The native DLL
is pinned for the player process lifetime so asynchronous cleanup cannot execute
unloaded code. A driver that stops responding may retain those bounded resources
until it recovers or the process exits; this does not promise recovery of a hung
graphics driver. The existing supervised owned-session teardown remains separate.

Publication uses a writer guard and producer identity to keep old filter instances
from publishing a stale epoch or presenting an old failure as the new producer's
failure. Availability requires an exact private-ABI marker plus SHA256 matches for
both mpv.exe and libmpv-2.dll. Older installations omit this optional capability.

## Validation

Real Windows11/RTX5090 tests use moving synthetic media, a D3D11 gpu-next player
and an mpv.net player, each doing480x360→960x720 DirectML processing. Actual Wasm
inspectors verify two players, shared subscriptions with independent sizes,
nonblack samples, seek/file-change epochs, one addon stopping while another
continues, last unsubscribe, test-host termination, and complete helper cleanup.
The regular playback path continues after host termination, with manual addons
remaining stopped. Tests isolate mpv.net settings using `MPVNET_HOME`.

Library tests cover permissions, ownership, negotiation, capacity, failed
reconfiguration, mapping ACLs, lease expiry, repeated snapshots and old producer
status. A native lifetime test exercises both cleanup orders with a stalled
readback stand-in. It is not a simulated GPU-driver failure. Existing API1.0
binary compatibility remains covered. Wider GPUs, long playback, HDR,
TensorRT/CUDA and final-display samples still need separate validation/adapters;
the older owned-session performance numbers are not a normal-player benchmark.

Windows references: [file mappings](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-createfilemappingw),
[module lifetime](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-getmodulehandleexw),
[GPU query completion](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11devicecontext-getdata).
