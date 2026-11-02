# Remaining integration and release gates

The developer foundation is not the completed community addon product. The following work remains before advertising support for the user's Plex and lighting scenarios.

| Area | Foundation status | Required next work |
| --- | --- | --- |
| Package / consent / rollback | Implemented locally; unsigned dev installs only | Publisher/signature policy, catalog verification, update consent and revocation |
| Sandboxed workers | Real Windows runtime, resource supervision and tests | Additional containment review, aggregate service limits, supported-system testing, Linux enforcement |
| Public protocol / SDK | Versioned preview, JS SDK, typed settings, actions, replay, bounded binary responses and coalesced timers | Freeze compatibility contract after integration, other language examples, media/output events |
| Lifecycle | Persistent per-user host, private same-user management transport, consolidated player/Manager/manual triggers, opt-in Windows login, bounded stop and health checks | Windows sign-in acceptance and Linux lifecycle integration |
| Manager | Addon controls, exact-file/profile consent, service/credential and listener/certificate review, revocation and rendered tests | Community catalog flow, supported-system usability testing |
| Sessions | Native provider, approved local and HTTP sources/profiles, admission, asynchronous open/close, independent pause/seek, capacity UI, real DirectML and prepared classic TensorRT checks | More source types and hardware qualification; TensorRT-RTX addon streaming is not qualified |
| Frame samples | GPU-reduced SDR DirectML processed samples; API 1.12 CUDA source colors and fixed-model character masks; shared producers, independent readers, PTS/epoch/color/geometry metadata and bounded binary transport | Final-display/HDR stages, D3D11 segmentation and broader GPU/performance qualification |
| Media output | Encoded upload plus probe, start/track selection, served streams, bounded cache/demand, subtitle composition/extraction and recorded native qualification | Preserve the checks in `STREAMING-TESTS.md`; broaden formats/hardware and real-client interoperability |
| Device/service I/O | Pinned HTTP/HTTPS and UDP, approved HTTP(S) listeners, native HTTP/WebSocket proxying, saved and request-derived credentials, bounded remote media reads | Discovery, OAuth, additional remote formats and live playlists; Linux transport enforcement |
| Website links | Design requirement | Signed catalog identity resolution and Manager review flow; never arbitrary link-to-execution |

Keep optional capability discovery separate from permissions. The platform supports independent concurrent sessions; an addon decides how many to request within user/host resource limits. Do not bake in one Plex session, a codec choice, a lighting vendor, or a fixed sample rate.

Use the session controller, sample inspector, HTTP bridge and media stream examples to validate the public contract. Plex/client protocol interoperability and lighting integrations remain separate addon projects. See [REVIEW-GUIDE.md](REVIEW-GUIDE.md) for the frozen 3.9.0 qualification and its limits. Each new runtime must pass the applicable recorded release checks; source implementation or a capability listing alone is insufficient.

Before a community release: review installation and update identity, exercise malformed package/protocol cases and resource failure recovery, test rollback with evolving data, verify addon failure does not disrupt actual playback, and run old SDK fixtures against the next host version. Publish the limits and supported feature matrix alongside the SDK.
