# HTTP and credential APIs — 1.7 development

This document describes the current implementation in this working tree. It is
not a claim that the complete streaming framework is ready. The sealed
integration.1 archives and the standalone API 1.6 guide do not contain these APIs.
The integrated media contracts, subtitle processing, limits and stream timeline
controls are documented in [STREAMING.md](STREAMING.md). Native qualification is
tracked separately from the host networking tests.

## Approve a listener

In Manager, select the addon and use **Listening access**. Default access is
**This computer only** (`127.0.0.1`). Explicit alternatives are **Local network**
and **Internet / public clients**. LAN scope accepts loopback, private IPv4,
IPv4 link-local, IPv6 ULA and link-local peers. Public scope accepts other peers.
Neither scope authenticates a client.

Specify the bind IP and port. For an all-interface bind (`0.0.0.0` or `::`), also
specify exact hostnames/IPs that clients will use, without a scheme or port.
Incoming Host authority must match one of these names and the approved port.
A reverse proxy must supply the approved internal Host authority. Wildcard host
names are not accepted. No firewall, URL reservation or router setting changes.
A separately configured public origin is always reported as **unverified** in
this development implementation; binding is not an internet reachability test.

For HTTPS, first import a PFX/PKCS#12 bundle containing one private key from
Listening access. Then select the certificate and a hostname covered by its
subject alternative names. Review displays expiry, hostname and SHA-256
fingerprint. AJN encrypts the saved bundle for this Windows user and package.
It does not install trust roots. Clients perform normal certificate validation.
Replacing a certificate stops the addon; start it again to load the replacement.
Replacement must cover the hostnames of listeners using it. Remove associated
listener approvals before removing a certificate. Linux certificate storage is
not implemented yet.

On Windows, the live TLS certificate uses a temporary user key container because
[Schannel cannot use ephemeral keys](https://learn.microsoft.com/en-us/dotnet/core/extensions/sslstream-troubleshooting).
The certificate is disposed after the listener drains. Saved key material stays
encrypted; no private key or certificate password is returned to Wasm.

Browser access is denied unless explicit CORS origins, methods and headers are
reviewed. Credentialed CORS requires exact origins; wildcards are unsupported.
The host handles valid preflight OPTIONS requests and owns CORS response headers.
CORS consent does not replace application authentication.

## HTTP request lifecycle

Request `network.listen`, API minor 7 and `httpServer` capability 1.0.
Permission alone does not approve an endpoint. Use the matching SDK.

| Method | Behavior |
| --- | --- |
| `httpServer.selections()` | Approved listener IDs, names and bindings |
| `httpServer.formats()` | Current protocols and limits |
| `httpServer.open(listenerId)` | Return `{serverId}`; binding happens asynchronously |
| `httpServer.status(serverId)` | State, public address/unverified reachability, expiry and redacted error |
| `httpServer.requestClose(serverId)` | Begin asynchronous stop/drain |
| `httpServer.requestStatus(requestId)` | Pending/claimed state, body progress and remaining decision time |
| `httpServer.extend(requestId, seconds)` | Extend from now; maximum deadline is 120 seconds after arrival |
| `httpServer.cancelRequest(requestId)` | Cancel the owned request and native response |
| `httpServer.readBody(requestId)` | Start a bounded asynchronous body read; return immediately |
| `httpServer.bodyChunk(requestId, offset, count=32768)` | Read a completed buffered body chunk |
| `httpServer.respond(requestId, response)` | Claim once and return an inline response up to 32 KiB |
| `httpServer.beginResponse(requestId, response)` | Claim once and begin assembling a response up to 256 KiB |
| `httpServer.appendResponse(requestId, bytes)` | Append at most 32 KiB |
| `httpServer.finishResponse(requestId)` | Submit the complete buffered response for native transmission |

`response` contains `status`, optional `headers: [{name, values: string[]}]` and,
for `respond`, optional `body: string | Uint8Array`. Final statuses are 200–599;
204/304 have no body. HEAD sends representation length without body bytes.
Framing and CORS headers are host-owned. Header values are printable ASCII and
total response headers are limited to 16 KiB. `beginResponse` remains subject to
the decision deadline until `finishResponse`; unfinished responses are not sent.

Body reads are limited to 256 KiB and a 15-second read lifetime. Poll until
`bodyState` is `ready` or `failed`; only a complete successful body can be read.
Chunks return `{body, byteLength, offset, totalBytes, eof}`. Chunk reads do not
consume data, so retrying an offset is safe. Native forwarding requires an unread
body; it cannot take over after `readBody` starts.

Events are `http.request`, `http.closed` and `http.disconnected`. Request data has
`requestId`, `serverId`, `method`, `path`, `headers`, `query`, `peerAddress` and
`webSocketRequested`. Headers/query preserve repeated values in arrays:
`{name, values: string[] | null, redacted: boolean}`. Built-in sensitive headers
include Authorization, Proxy-Authorization, Cookie and Set-Cookie. Additional
fields declared by the package or reviewed listener are hidden before delivery.

```json
"sensitiveRequestFields": {
  "headers": ["X-Client-Key"],
  "query": ["client_key"]
}
```

This manifest property requires API 1.7. It supplements user-reviewed redaction;
neither source can remove the other's hidden fields. Do not echo request data or
secrets to logs. Kestrel's request logging providers are disabled.

Callbacks remain synchronous and use the existing two-second/broker budgets.
Returning from `onEvent` does not complete an HTTP request. Respond, buffered
response assembly and native forwarding are mutually exclusive response claims.
Expired/foreign handles fail. Completion/disconnect events are bounded hints;
catch `request_not_found` when expiry or disconnect races a callback.

Initial decision deadline is 15 seconds; expiry returns 504. Limits: 64 unclaimed
requests and 128 total requests per addon, four listeners per addon, sixteen
listeners per host, 128 connections and sixteen upgraded connections per listener.
The event queue holds 128 records. Admission overload returns 503. Both raw
headers and serialized request metadata are limited to 64 KiB. Metadata too large
for the event returns 431. Inline writes have a 15-second deadline.

## Native HTTP forwarding and buffered metadata

Request `network.proxy` and `network.connect`, plus `network.listen` for incoming
requests. Require `httpProxy` capability 1.0. Every destination needs the existing
package-bound service approval. Pinned addresses, normal TLS validation and
no automatic redirects remain in force. Each operation has an independent HTTP
handler, with no cookie jar, system proxy or inherited Windows credential.

| Method | Behavior |
| --- | --- |
| `httpProxy.formats()` | Formats, operation/byte/time limits |
| `httpProxy.forward(requestId, destinationId, options)` | Claim the incoming request; return `{operationId}` |
| `httpProxy.buffer(destinationId, options)` | Fetch metadata asynchronously; return `{operationId}` |
| `httpProxy.status(operationId)` | State, HTTP status, headers, body length, representation length, cleanup readiness and error |
| `httpProxy.read(operationId, offset, count=32768)` | Read a complete metadata body chunk |
| `httpProxy.cancel(operationId)` | Cancel upstream I/O |
| `httpProxy.close(operationId)` | Release a terminal handle after `cleanupReady` becomes true |

Common options:

```typescript
{
  path?: string; // default "/"; approved-origin-relative path and query
  requestHeaders?: {name: string; values: string[] | null}[];
  responseHeaders?: {name: string; values: string[] | null}[];
  passRequestHeaders?: string[];
  passResponseHeaders?: string[];
  useCredential?: boolean;
  credentialId?: string;
}
```

A null header change removes that header. Host, framing, hop-by-hop, CORS and
WebSocket handshake headers cannot be overridden. Connection-nominated hop headers
are removed too. Sensitive incoming headers are withheld unless explicitly named
in `passRequestHeaders`. Set-Cookie is withheld unless explicitly included in
`passResponseHeaders`. These choices are per operation. Incoming proxy forwarding
headers are stripped; AJN does not manufacture a trusted client identity.

Forwarding streams both bodies natively with bounded buffers and downstream
backpressure. Payload bytes are not decompressed or rewritten. Range, HEAD,
conditional statuses and repeated end-to-end headers survive forwarding. An
unmodified route does not move media bytes through Wasm. Ordinary HTTP methods
are GET, HEAD, POST, PUT, PATCH, DELETE and OPTIONS.

A WebSocket incoming request uses the same `forward` call after addon
authorization. The host connects to the approved upstream, negotiates a common
subprotocol and pumps fragmented text/binary messages in both directions. No
arbitrary guest socket API is exposed. Compression extensions are not negotiated.
Message limit is 16 MiB. Ping/pong and bounded close handling detect dead peers.

`buffer` additionally accepts `method`, `body: string | Uint8Array` up to 32 KiB,
and `maximumBytes` from 1 to 2097152 (default 2 MiB). Read chunks only after state
is `completed`. Oversize data fails with `response_too_large`; partial data is not
presented as complete. Metadata reads are repeatable until `close`.
This does not change the older `network.request/result` 64 KiB contract.

There are at most sixteen owned proxy operations, four buffered operations per
addon, and sixty-four active proxy operations across the host. Close terminal
handles to recover addon quota. Connect timeout is five seconds; header waits
and individual native reads/writes are bounded. Active uploads use I/O deadlines
rather than a fifteen-second total upload cutoff. Native transfers are limited
to 24 hours and 512 GiB; buffered requests have a thirty-second total deadline.
Owner shutdown and downstream disconnect cancel upstream work.

## Temporary client credential contexts

Request `credentials.delegate`, `credentials.use` and `network.connect`; capture
also needs an owned incoming request/listener. Require `requestCredentials` 1.0.

```javascript
const {credentialId} = ajn.requestCredentials.capture(
  requestId, destinationId,
  [{from: "query", name: "client_key", to: "header", target: "X-Upstream-Key"}],
  3600
);
const {operationId} = ajn.httpProxy.buffer(destinationId, {
  path: "/validate-client", credentialId
});
```

Only fields already declared sensitive before arrival can be captured. A mapping
uses `from`/`to` (`header` or `query`), incoming `name`, and upstream `target`.
Values stay native. Capture does not authenticate the client. The addon must
inspect the validation response and enforce authorization before returning
protected data or claiming output resources.

`requestCredentials.status(credentialId)` reports presence, destination, placement
names and expiry without plaintext. `requestCredentials.release(credentialId)`
cancels active operations and prevents new uses. Contexts belong to one running
addon instance and one approved upstream. Limits: 64 contexts, eight mapped
fields, 16 KiB captured bytes, lifetime up to 24 hours. Expiry, release or owner
shutdown cancels consumers. Retiring contexts retain quota while consumers drain.
Conflicting saved/delegated/header/query values fail rather than silently
replacing a different credential source.

Consumers include `httpProxy.buffer`, native HTTP/WebSocket forwarding, remote
probing, media input, served streams and external subtitle input. Pass the
context's `credentialId` explicitly for its approved destination; see
[STREAMING.md](STREAMING.md) for media ownership and cancellation.
Probe and remote-media consumers are pending and must be added before calling
the streaming framework complete.

## Run the generic examples

- [http-inspector](examples/http-inspector/addon.js) returns a greeting. Install,
  approve a loopback listener, start, then use **Open approved listener**. Run
  `curl.exe http://127.0.0.1:7888/hello` with your selected port.
- [http-bridge](examples/http-bridge/addon.js) requires a loopback listener and an
  approved HTTP service. Open it from its action. `POST /control` echoes a
  buffered control body; `/proxy` forwards to the selected service's `/` path;
  `GET /metadata` fetches up to 256 KiB and returns it through response chunks.
  Its polling queues rotate with bounded work per callback. It logs no requests.
  It is a diagnostic, not a client authentication or Plex implementation.

Build examples with the matching host/Javy toolchain. API 1.6 releases correctly
reject packages requiring minor 7. The test suite executes the packaged Wasm
examples through the real broker, not just private helper methods.
