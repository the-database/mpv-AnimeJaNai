# Remaining integration and release gates

The developer foundation is not the completed community addon product. The following work remains before advertising support for the user's Plex and lighting scenarios.

| Area | Foundation status | Required next work |
| --- | --- | --- |
| Package / consent / rollback | Implemented locally; unsigned dev installs only | Publisher/signature policy, catalog verification, update consent and revocation |
| Sandboxed workers | Real Windows runtime, resource supervision and tests | Additional containment review, aggregate service limits, supported-system testing, Linux enforcement |
| Public protocol / SDK | Versioned preview, JS SDK, typed settings, actions, replay | Freeze compatibility contract after integration, other language examples, more capable async/event scheduling |
| Lifecycle | Persistent per-user host, private same-user management transport, consolidated activation, bounded stop and health checks | Player connection and opt-in login activation |
| Manager | Addon list/install/permissions/settings/actions/logs UI; rendered UI tests and a real management-to-Wasm test | Native capability consent and source/device selection, community catalog flow |
| Sessions | Owned concurrent registry, provider interface and cancellation tests | Native provider, selected sources/profiles, admission, asynchronous startup/progress, seek/cancel, real concurrent GPU tests |
| Frame samples | Immutable bounded latest-sample queue fixture | Native hook, negotiated stage/format/rate, timestamps/color metadata, bounded binary transport, GPU overhead tests |
| Media output | Design requirement | Trusted encode/mux/output pipeline, streaming backpressure and cleanup |
| Device/service I/O | No network capability is exposed | Destination-scoped outbound APIs; separately authorized listeners/discovery; credential handling |
| Website links | Design requirement | Signed catalog identity resolution and Manager review flow; never arbitrary link-to-execution |

Keep optional capability discovery separate from permissions. The platform supports independent concurrent sessions; an addon decides how many to request within user/host resource limits. Do not bake in one Plex session, a codec choice, a lighting vendor, or a fixed sample rate.

Use generic test consumers for the next integration work: a session controller and a sample inspector. They validate the public contract without implementing the user's future Plex or lighting products. Measure them with real DirectML and TensorRT playback and more than one session before making video-performance or compatibility claims.

Before a community release: review installation and update identity, exercise malformed package/protocol cases and resource failure recovery, test rollback with evolving data, verify addon failure does not disrupt actual playback, and run old SDK fixtures against the next host version. Publish the limits and supported feature matrix alongside the SDK.
