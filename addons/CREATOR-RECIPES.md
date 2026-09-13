# API recipes and working examples

Start with the [creator tutorial](CREATOR-GUIDE.md). This page helps you choose a
tested example to extend. The snippets below explain individual patterns; the
linked example directories contain complete, buildable addons.

## Pick an example

| Example source | What it demonstrates | Full guide |
| --- | --- | --- |
| [Counter](examples/counter/addon.js) and [manifest](examples/counter/manifest.json) | Settings, private storage, lifecycle and action results | [API](API.md) |
| [Session controller](examples/session-controller/addon.js) | Local source/profile selections, independent sessions, status and controls | [Native media](NATIVE-MEDIA.md) |
| [Sample inspector](examples/sample-inspector/addon.js) | Timed binary samples from owned processing sessions | [Frames](FRAMES.md) |
| [Player inspector](examples/player-inspector/addon.js) | Samples from normal playback, multiple players, independent observation handles | [Player frames](PLAYER-FRAMES.md) |
| [Service inspector](examples/service-inspector/addon.js) | Approved HTTP requests and UDP messages, optional scoped credentials | [Networking](NETWORK.md) |
| [Output inspector](examples/output-inspector/addon.js) | Owned processed video/audio delivery to an approved HTTP receiver | [Outputs](OUTPUTS.md) |
| [Remote inspector](examples/remote-inspector/addon.js) | Remote media input, range seeking and independently approved remote output | [Remote inputs](REMOTE-INPUTS.md) |

Each example has a neighboring `manifest.json`. Review its permissions and
required capabilities before copying its code. Change the package ID for your
own addon. Device vendors, codecs, stream counts and application integrations
are addon choices within the advertised platform limits.

## Optional capability checks

Capability availability and permission approval are different. This helper checks
the version; it does not grant anything:

```javascript
function supports(ajn, name, major, minimumMinor) {
    const capability = ajn.info().capabilities[name];
    return capability && capability.major === major && capability.minor >= minimumMinor;
}
```

Use `requiredCapabilities` in the manifest if the addon cannot work without a
feature. For optional behavior, check it when needed and explain what is absent.
`ajn.info().permissions.includes("player.observe")` checks the actual grant.
Still handle permission/access errors at the operation: a capability alone
does not authorize a source, profile, destination or credential.

Minimum API minors for these feature families are 0 for settings/storage,
1 for native session controls, 2 for frames/timers, 3 for networking, 4 for
outputs, 5 for remote input and 6 for normal-player observation. Independently
versioned capability requirements remain authoritative. See [API](API.md).

## Timed work without blocking

Within `start`, call `ajn.timers.set("status", 250)` to request repeat events.
Within a `timer` event, check `event.data.timerId`, do a small bounded amount of
work, and return. Within `stop`, clear the timer and release owned resources.
Missed ticks coalesce; `missedTicks` describes missed intervals. Do not enqueue
one task for every interval that elapsed while the addon was busy.

Do not use `async function onEvent`, promises, sleeps or a polling `while` loop.
Native opens and HTTP requests return handles so work can continue outside the
callback. Use later timer events to observe their results. The
[sample inspector](examples/sample-inspector/addon.js) is a complete timer example.

## Owned videos versus normal-player observations

Use `sessions.open` when your addon owns a separate processing session. The user
approves its input and saved profile. You can pause/seek/close your own session;
other addons cannot use its ID. These sessions consume processing capacity.

Use `playerFrames` when you only need small images from videos the user is already
playing. It requires its own `player.observe` and `frames.read` grants. It exposes
no filename, raw player command channel or playback controls, and does not open
another processing session. Multiple observers share one producer per player.

Never persist session, player, subscription or request IDs across worker restarts.
Re-enumerate players and approved resources, then create new handles. A selected
player can close between listing and subscribing; handle `player_not_found` or
`player_closed` and offer a fresh selection.

## Reading image samples correctly

`frames.read` and `playerFrames.read` return `null` when there is no new sample.
The SDK returns pixels as a `Uint8Array`, separate from JSON. For a valid sample:

```javascript
function centerRgb(frame) {
    const x = Math.floor(frame.width / 2);
    const y = Math.floor(frame.height / 2);
    const offset = y * frame.stride + x * 4;
    return [frame.pixels[offset + 2], frame.pixels[offset + 1], frame.pixels[offset]];
}
```

Pixels are BGRA8 with opaque alpha, not RGBA. Respect `color`, `crop`,
`pixelAspectRatio`, `rotation` and `verticalFlip` when interpreting geometry.
The preview provides processed SDR samples before subtitles, OSD and final
display tone mapping. Some sources/backends cannot produce samples. Report
`frame_format_unavailable` instead of treating failure as a black image.

Use `epoch` to reset temporal smoothing/history after a seek or file change.
Frame IDs and epochs are strings; do not convert them to JavaScript numbers and
lose integer precision. Slow consumers drop samples. PTS is media time, not
measured monitor display time. Start at a small size/rate and measure before
increasing them. The maximum sample is 320×180 at 60 Hz.

## HTTP and UDP

Get `network.selections()` and let the user choose an approved destination. An
empty array is a setup state, not a reason to hardcode a new address. For HTTP,
`network.request` returns a request ID; poll `network.result` on a later timer.
Reading a terminal result consumes it and frees its slot. After cancellation,
read the terminal result as well. HTTP success status and transport completion
are separate: examine the returned status code.

Service credentials are attached by trusted code only when requested and granted.
Your addon should not ask users to paste credentials into ordinary string settings
or logs. The destination review establishes an origin and pinned addresses;
changing the request path cannot broaden that authority. Redirects, arbitrary
cookies and OS credentials are not provided.

UDP send success means the datagram was sent, not acknowledged by a device.
There is no general inbound listener, TCP/TLS socket or WebSocket API in this
preview. See the precise bounds in [Networking](NETWORK.md).

## Media input and output

Remote media uses `sessions.openRemote`, not repeated HTTP reads in Wasm. The
trusted reader supplies bytes to the native pipeline and supports seeking only
when the source provides the required stable range metadata. Check session
status before seeking. Playlists and redirects are currently unsupported.

Use `outputs.formats()` before choosing codec/container/audio combinations.
Owned output sessions flow through the trusted encoder and transport without
putting continuous media bytes into JSON or Wasm. `sessions.pause`, `status` and
`requestClose` manage these handles; encoding output is not generally seekable.
Input and output services have independent approvals and credentials.

An accepted open is not successful playback or delivery. Poll loading/running/
completed/failed status, communicate failures, and close terminal sessions to
release capacity. The current native output adapter needs NVIDIA NVENC; the
supported combination matrix and resource budgets are in [Outputs](OUTPUTS.md).

## Build and share responsibly

Use a small, generic diagnostic addon to establish a capability before adding a
large application integration. Test unavailable hardware and missing permissions
alongside the success path. Keep secrets out of action results and logs. Prefer
short messages for users and compact structured diagnostics for developers.

For debugging, `replay` sends recorded events to one worker; the
[counter replay file](examples/counter/events.json) shows its format. Replay is a
developer facility, not an authority to open resources. The broker still enforces
grants and ownership. Read [Troubleshooting](TROUBLESHOOTING.md) when a callback,
permission, resource selection or native operation fails.
