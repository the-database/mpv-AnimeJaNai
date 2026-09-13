# Addon API 1.4 preview

The addon API is versioned separately from AJN, mpv, inference DLLs, and the package's own version. Windows implements this preview. Public messages use no Windows handles or filesystem paths.

## Package and manifest

An `.ajnaddon` is a ZIP containing exactly `manifest.json` and `module.wasm`. No setup script is executed. Only portable core Wasm is accepted, with a maximum module size of 16 MiB and a 16 KiB manifest. Links and extra or nested entries are rejected. The manifest records the module SHA256; registration binds approval to the entire archive SHA256.

See [the example manifest](examples/counter/manifest.json). `build` adds `moduleSha256` to the source manifest. Required fields are `schemaVersion: 1`, lowercase reverse-domain `id`, `name`, `version`, `api: { major: 1, minMinor: 0 }`, `permissions`, and the generated digest. Optional fields:

- `requiredCapabilities`: independently versioned required host features, for example `settings: { major: 1, minMinor: 0 }`. An unavailable requirement prevents startup.
- `activation`: any of `manual`, `on_manager`, `on_player`, `on_login`; defaults to manual. These describe host activation sources, not authority to register startup tasks. The integrated Windows preview supplies the player/Manager events and explicit login opt-in; see [LIFECYCLE.md](LIFECYCLE.md). Other embeddings must supply their actual lifecycle sources.
- `settings`: up to 32 definitions of boolean, finite number, bounded string, or choice values. Each has a label and default. Optional descriptions, numeric minimum/maximum, string maximum length, and choice lists are validated by the host.
- `actions`: up to 16 named actions with labels and optional descriptions.

Unknown top-level metadata is retained for additive evolution. Identity, versions, permissions, typed definitions, duplicate JSON fields, and required capabilities are validated. Unknown permissions never grant access. A future required behavior must use capability negotiation or a schema/API version change rather than relying on an unknown optional field.

## Transport profile

The worker uses a bounded newline-delimited **JSON-RPC 2.0** profile over its private stdin/stdout. Messages must be UTF-8 JSON objects, at most 128 KiB before the newline, nesting at most 24. Duplicate object fields are rejected. No batch messages or notifications are accepted in this preview. IDs are nonempty strings up to 80 characters or positive integers no larger than JavaScript's safe integer maximum. All requests receive a matching result or error. This is a deliberately restricted application profile, not a general-purpose JSON-RPC server.

The guest starts by sending:

```json
{"jsonrpc":"2.0","id":"hello","method":"addon.hello","params":{"major":1,"minMinor":0}}
```

The host replies with `result` containing the bound addon ID, API version, approved permissions, features, and a version map named `capabilities`. Optional fields can be added; clients must ignore fields they do not use. A missing optional capability means unavailable. Advertising a capability does not grant its permission.

The host sends an event request:

```json
{"jsonrpc":"2.0","id":"host:1","method":"addon.event","params":{"type":"event","eventId":1,"name":"start","data":{"reason":"manual"}}}
```

While processing it, the guest may issue broker requests and wait for their replies. It then replies to `host:1`. One event runs at a time per worker. Other workers have independent channels. The preview has a two-second event deadline, including broker calls, and at most 128 broker requests per event and 500 per second. A long native operation must return an accepted session/job promptly and run asynchronously behind its handle; it must not block an addon callback for an engine build or video stream's lifetime.

The SDK handles `host.ping` internally. User handlers receive `start`, `stop`, `action` with `{ id }`, `settings.changed`, requested `timer` events, and explicit developer replay events. Additive event-data fields must be ignored unless used. Callback failures stop that worker. A host heartbeat every five seconds detects a guest that stops servicing messages after an event.

API 1.2 adds a binary tail for successful `frames.read` responses; API 1.3 adds one for `network.result`. The JSON declares `byteLength`; exactly that many bytes follow the newline. Other messages remain JSON-only. See [FRAMES.md](FRAMES.md) and [NETWORK.md](NETWORK.md).

Errors use standard integer JSON-RPC error codes and an AJN-specific string at `error.data.code`, such as `permission_denied`, `storage_quota`, `capacity_exceeded`, or `feature_unavailable`. Invalid transport/protocol messages stop the worker; valid broker requests that are denied receive a structured error and may be handled by the addon. The JavaScript SDK exposes the string as `error.code`.

## Broker methods

| Method | Parameters | Permission | Result |
| --- | --- | --- | --- |
| `host.info` | `{}` | None | Bound identity, API, permissions, capabilities |
| `settings.get` | `{}` | None | Only this addon's declared effective settings |
| `log.write` | `{ message }` | `log.write` | `null`; control characters stripped |
| `storage.get` | `{ key }` | `storage.read` | JSON value, or `null` for missing key |
| `storage.set` | `{ key, value }` | `storage.write` | `null` after atomic save |
| `sessions.open` | `{ sourceId, profileId? }` | `sessions.manage` | `{ sessionId }`; only if a trusted provider is connected |
| `sessions.status` | `{ sessionId }` | `sessions.manage` | Provider's public JSON status |
| `sessions.close` | `{ sessionId }` | `sessions.manage` | `null` after release |
| `sessions.selections` | `{}` | `sessions.manage` | Approved source/profile IDs and labels; sessions capability 1.1 |
| `sessions.pause` | `{ sessionId, paused }` | `sessions.manage` | `null` after accepting the control; sessions 1.1 |
| `sessions.seek` | `{ sessionId, seconds }` | `sessions.manage` | `null` after accepting an absolute seek; sessions 1.1 |
| `sessions.requestClose` | `{ sessionId }` | `sessions.manage` | `null` after scheduling cleanup; sessions 1.1 |
| `frames.subscribe` | `{ sessionId, stage, format, width, height, maxFps }` | `frames.read` + `sessions.manage` | Subscription ID and accepted sample options; frames 1.0 |
| `frames.read` | `{ subscriptionId }` | `frames.read` + `sessions.manage` | `{ frame, byteLength }` followed by bounded binary bytes; or no new sample |
| `frames.unsubscribe` | `{ subscriptionId }` | `frames.read` + `sessions.manage` | `null`; releases the sample subscription |
| `timers.set` | `{ timerId, intervalMs, repeat }` | None | `null`; creates/replaces a bounded timer; timers 1.0 |
| `timers.clear` | `{ timerId }` | None | `null`; cancels the timer |
| `network.selections` | `{}` | `network.connect` | Approved destination IDs and metadata; network 1.0 |
| `network.request` | `{ destinationId, method, path, headers, bodyBase64, useCredential }` | `network.connect`; `credentials.use` when requested | `{ requestId }`; bounded asynchronous HTTP |
| `network.result` | `{ requestId }` | `network.connect` | Pending/completed/failed status and `byteLength`, then binary body |
| `network.cancel` | `{ requestId }` | `network.connect` | `null`; read terminal result to free the slot |
| `network.sendDatagram` | `{ destinationId, bodyBase64 }` | `network.connect` | `{ bytesSent }`; approved UDP destination only |
| `outputs.formats` | `{}` | `media.output` + `sessions.manage` | Adapter options and resource bounds; outputs 1.0 |
| `outputs.open` | `{ sourceId, profileId, encoding, destination }` | `media.output` + `sessions.manage` + `network.connect`; `credentials.use` when requested | Owned `{ sessionId }`; reviewed source/profile and HTTP upload receiver |

API 1.4 adds typed encoded output controls without changing the binary transport.
Encoded bytes remain in the trusted host; `sessions.status/pause/requestClose`
manage the owned output. See [OUTPUTS.md](OUTPUTS.md) for exact choices,
destination consent, completion semantics and limits.

Private storage is separated by addon ID: maximum 256 keys, 32 KiB per value, 1 MiB total. A stored null and a missing key both read as null in this preview. Saving one key is atomic; a read-modify-write sequence is not a transaction across distinct worker instances. The embedding host should create one activation controller per addon ID.

Settings are distinct from private storage. Only trusted UI/CLI code can change them. Unknown saved settings survive removal from a new schema for rollback, but are hidden from the active addon. Invalid/corrupt settings and storage are preserved for recovery. Persistent version-to-version data migrations are intentionally not implicit.

`sourceId` and `profileId` are opaque references supplied by a trusted integration, never an instruction to open an arbitrary path or execute commands. Each worker has an unforgeable host-owned session owner. Session IDs cannot be used by another worker. Closing or failing an addon cancels pending opens and releases its sessions. The library defaults are 16 sessions per registry and four per worker; the native service applies a separate operator-configurable 1–16 session limit (default two). No native capability is advertised unless a trusted AJN runtime is configured. [NATIVE-MEDIA.md](NATIVE-MEDIA.md) specifies approvals, status, and failure semantics. Native consumers should use the new `requestClose` method; legacy `close` retains its synchronous behavior.

## Limits and failure semantics

Workers have no preopened directories, inherited environment, or network grants. They cannot access another addon's files through the broker. Runtime parameters are fixed by the trusted host and verified runtime; addons cannot supply runtime flags or native precompiled code.

Each Windows worker job contains at most two processes (the trusted launcher and Wasmtime), with 512 MiB per process, 768 MiB combined, and a 25% CPU hard cap. Wasm linear memory is additionally capped at 64 MiB. At most eight workers may be active per host process. These are prototype limits to measure and tune, not a promise about future GPU throughput. Stderr is capped at 64 KiB total, retaining up to 8 KiB of diagnostics. Temporary worker files are removed after job termination.

Job objects enforce resource and lifetime limits. Wasmtime enforces the guest filesystem/network capability boundary. AppContainer is additional future defense, not something this implementation claims to use. A trusted native provider must honor cancellation, bound its work, and reliably release its own resources; it cannot be sandboxed by a guest wrapper.

## Compatibility discipline

Before freezing a public major version: ship a versioned SDK/specification, retain old-client contract fixtures, measure real pipeline behavior, and publish a deprecation window. Minor additions must preserve existing field semantics. Changes to timing, color representation, ownership, lifecycle, or permissions need explicit compatibility treatment. Do not expose mpv's raw command channel, private view models, native pointers, or configuration-file layout as the community API.
