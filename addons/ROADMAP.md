# Remaining integration and release gates

The developer foundation is not the completed community addon product. The following work remains before advertising support for the user's Plex and lighting scenarios.

| Area | Foundation status | Required next work |
| --- | --- | --- |
| Package / consent / rollback | Implemented locally; unsigned dev installs only | Publisher/signature policy, catalog verification, update consent and revocation |
| Sandboxed workers | Real Windows runtime, resource supervision and tests | Additional containment review, aggregate service limits, supported-system testing, Linux enforcement |
| Public protocol / SDK | Versioned preview, JS SDK, typed settings, actions, replay, bounded binary responses and coalesced timers | Freeze compatibility contract after integration, other language examples, media/output events |
| Lifecycle | Persistent per-user host, private same-user management transport, consolidated player/Manager/manual triggers, opt-in Windows login, bounded stop and health checks | Windows sign-in acceptance and Linux lifecycle integration |
| Manager | Addon controls, exact-file/profile consent, service/credential and listener/certificate review, revocation and rendered tests | Community catalog flow, supported-system usability testing |
| Sessions | Native provider, approved local and HTTP sources/profiles, admission, asynchronous open/close, independent pause/seek, persistent host capacity UI, real concurrent DirectML tests | TensorRT/RIFE validation, more source types and hardware matrix |
| Frame samples | Native GPU-reduced SDR DirectML samples from owned sessions and separately approved normal-player observations; shared producer with independent readers, negotiated size/rate, PTS/epoch/color/geometry metadata, bounded binary transport, actual Wasm and concurrent-player/session tests | Final-display and HDR stages, CUDA/TensorRT producer, broader GPU/performance matrix |
| Media output | Existing encoded upload plus API 1.7 probe, start/track selection, served streams, bounded cache/demand, subtitle composition/extraction | Complete the streaming qualification in `STREAMING-TESTS.md`; broaden formats/hardware only after testing |
| Device/service I/O | Pinned HTTP/HTTPS and UDP, approved HTTP(S) listeners, native HTTP/WebSocket proxying, saved and request-derived credentials, bounded remote media reads | Discovery, OAuth, additional remote formats and live playlists; Linux transport enforcement |
| Website links | Design requirement | Signed catalog identity resolution and Manager review flow; never arbitrary link-to-execution |

Keep optional capability discovery separate from permissions. The platform supports independent concurrent sessions; an addon decides how many to request within user/host resource limits. Do not bake in one Plex session, a codec choice, a lighting vendor, or a fixed sample rate.

Use the session controller, sample inspector, HTTP bridge and media stream examples to validate the public contract. Plex/client protocol interoperability and lighting integrations remain separate addon projects. The API 1.7 native streaming additions must pass their recorded release checks before being called qualified; a source implementation or capability listing alone is insufficient.

Before a community release: review installation and update identity, exercise malformed package/protocol cases and resource failure recovery, test rollback with evolving data, verify addon failure does not disrupt actual playback, and run old SDK fixtures against the next host version. Publish the limits and supported feature matrix alongside the SDK.
