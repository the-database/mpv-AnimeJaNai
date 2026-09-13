# Remaining integration and release gates

The developer foundation is not the completed community addon product. The following work remains before advertising support for the user's Plex and lighting scenarios.

| Area | Foundation status | Required next work |
| --- | --- | --- |
| Package / consent / rollback | Implemented locally; unsigned dev installs only | Publisher/signature policy, catalog verification, update consent and revocation |
| Sandboxed workers | Real Windows runtime, resource supervision and tests | Additional containment review, aggregate service limits, supported-system testing, Linux enforcement |
| Public protocol / SDK | Versioned preview, JS SDK, typed settings, actions, replay, bounded binary responses and coalesced timers | Freeze compatibility contract after integration, other language examples, media/output events |
| Lifecycle | Persistent per-user host, private same-user management transport, consolidated activation, bounded stop and health checks | Player connection and opt-in login activation |
| Manager | Addon controls, exact-file/profile consent, service/credential review, revocation and rendered tests | Other transport consent, community catalog flow |
| Sessions | Native provider, approved local sources/profiles, admission, asynchronous open/close, independent pause/seek, real concurrent DirectML tests | TensorRT/RIFE validation, more source types, host capacity UI and hardware matrix |
| Frame samples | Native GPU-reduced SDR DirectML samples, negotiated size/rate, PTS/epoch/color/geometry metadata, bounded binary transport, actual Wasm/concurrent-session tests | Final-display and HDR stages, CUDA/TensorRT producer, shared player observation, broader GPU/performance matrix |
| Media output | Private native GPU encoding, audio/muxing and bounded pipe tested; no guest output capability yet | Negotiated output API, owned destination consent, streaming delivery and longer A/V timing / format matrix |
| Device/service I/O | Pinned approved HTTP/HTTPS and UDP, async requests, protected scoped header credentials | TCP/TLS/WebSockets; authorized listeners/discovery; OAuth and remote media |
| Website links | Design requirement | Signed catalog identity resolution and Manager review flow; never arbitrary link-to-execution |

Keep optional capability discovery separate from permissions. The platform supports independent concurrent sessions; an addon decides how many to request within user/host resource limits. Do not bake in one Plex session, a codec choice, a lighting vendor, or a fixed sample rate.

Use generic test consumers for the next integration work: a session controller and a sample inspector. They validate the public contract without implementing the user's future Plex or lighting products. Measure them with real DirectML and TensorRT playback and more than one session before making video-performance or compatibility claims.

Before a community release: review installation and update identity, exercise malformed package/protocol cases and resource failure recovery, test rollback with evolving data, verify addon failure does not disrupt actual playback, and run old SDK fixtures against the next host version. Publish the limits and supported feature matrix alongside the SDK.
