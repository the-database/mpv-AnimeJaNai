# Approved service and device access

API 1.3 adds `network` capability 1.0 and Windows `credentials` capability 1.0.

API 1.4 can also send encoded media to an approved HTTP/HTTPS service through
the separate `outputs` capability and `media.output` permission. Manager
discloses media delivery when that permission is granted. These outputs use
their own streaming limits; ordinary HTTP/UDP calls below retain their existing
bounds. See [OUTPUTS.md](OUTPUTS.md).
Wasm still has no socket, DNS or HTTP imports. `network.connect` alone approves
no destination.

## Consent

Manager accepts an HTTP/HTTPS origin or UDP hostname/IP and port. The host
resolves it during review and shows its canonical origin, protocol and IPs.
A three-minute, single-use review reference and package hash bind final approval
to what was shown. Cancelling grants nothing. Maximum: eight destinations per
package, each with at most eight unicast IPs.

Approval covers all paths at the HTTP origin, or datagrams to the UDP port.
Guests supply opaque IDs, never replacement hostnames, schemes or ports.
Connections use only the pinned IPs, without another DNS lookup. If a service
changes addresses, remove its old approval and review again; this also applies
to CDN and local-device changes. Loopback/private services need the same review.
Wildcards, broadcast, multicast and unspecified addresses are rejected.

Approvals belong to an exact package hash. Removing access stops the addon and
drains its work before committing removal. Failed cleanup retains approval for
retry. Removing an addon clears its destinations and credentials; ordinary
settings/data remain separate.

## HTTP

```javascript
const service = ajn.network.selections().destinations[0];
const request = ajn.network.request(service.id, {
    method: "GET", path: "/status", useCredential: false,
});
// Poll on a timer or later action, without spinning inside a callback.
const response = ajn.network.result(request.requestId);
// response.state: pending, completed, or failed.
// Completed: status, headers, body (Uint8Array). Failed: structured error.
```

Supported methods: GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS. Paths start with
a single slash. Host/connection/framing/proxy headers are host-controlled.
Bodies are at most 32 KiB, empty for GET/HEAD. The SDK accepts Uint8Array or UTF-8
strings, encoded as wire `bodyBase64`.

The adapter uses HTTP/1.1 and pinned TCP connections, with normal HTTPS hostname
and certificate verification. It does not inherit proxies, OS credentials or
browser cookies. Automatic redirects, cookie storage and decompression are off.
Redirects return a 3xx response and cannot broaden access or carry a saved header
to another service. Requests are never automatically retried. Cancellation
cannot undo work already received by the server.

Four retained request slots belong to each instance; sixteen requests can be
active across the host. Each has a fifteen-second overall deadline and five-
second connection deadline. Slow requests run outside the addon event gate.
Starting requests is limited to 32 calls and 1 MiB of bodies/s per addon.

A completed result includes HTTP status (including HTTP error statuses), bounded
headers and at most 64 KiB of body. Transport failures have a structured error.
Terminal results are consumed once and free their local slots; later reads get
`request_not_found`. `cancel(requestId)` requests cancellation; consume its
terminal result to free that slot. Owner shutdown cancels/drains all requests.

Only `network.result` adds binary framing: its JSON declares `byteLength`, then
exactly that many bytes follow the newline. Pending/failed results declare zero.
The SDK exposes the tail as `body`. Existing frame framing and API 1.0/1.1 calls
are unchanged. AJN reserves room before consuming an HTTP result. A full worker
binary budget returns `bandwidth_exceeded`, leaving it readable later. Reduce
competing sample traffic before retrying; the shared budget is 16 MiB/s.

## UDP

`ajn.network.sendDatagram(destinationId, bytes)` sends one nonempty datagram,
at most 16 KiB, to the first pinned address for that UDP destination. Success
reports OS bytes sent, not receipt/application by the device. Select a direct
IP when ordering matters. Use the device protocol's sizes and sequencing.
There is no receive, retransmission, reliable delivery or reassembly API.

Limits: 240 datagrams and 1 MiB/s per addon; 960 datagrams and 4 MiB/s across the
host. At most four sends per owner can be pending, each with a 250 ms deadline.
Rate-limit errors are recoverable after reducing traffic.

## Saved headers

An addon also needs `credentials.use`. The user selects an approved HTTP service
and saves a header name plus complete value in Manager's masked field. The value
is never returned by lists or a guest credential-read API. Windows user DPAPI
encrypts it with context bound to the addon ID, package hash and destination ID.
Replacing/removing a credential stops the addon; the destination can remain.

`useCredential: true` attaches the saved header only to its approved service.
The guest cannot override that same header. Without the flag, it is absent.
The trusted host necessarily holds plaintext while sending it. The service
receives it and could echo it back: scoped use does not guarantee a cooperating
server cannot reveal a value. Manager explains unencrypted HTTP transmission.
No unrelated Windows/browser credential store is exposed. OAuth, browser login,
token refresh and Linux credential providers remain work.

## Example and remaining work

The generic `service-inspector` lists approved destinations, sends one HTTP GET
or configured UDP message on action, polls with a timer and reports status/byte
counts. It sends nothing on startup. Start it before ongoing actions.

Loopback tests cover pinning, redirects, binary bodies, oversized chunked
responses, cancellation/capacity/ownership, TLS rejection, DPAPI reload, real
Wasm/timers and rendered Manager consent. Public messages are portable; enforced
workers and credentials currently require Windows.

Continuous media does not belong in these command buffers. API 1.7 supplies
separately approved HTTP(S) listeners, native HTTP/WebSocket forwarding, scoped
client credentials and native media serving; see [STREAMING.md](STREAMING.md).
The existing `network.request/result` size and ownership contract stays unchanged.
No Plex or lighting addon is implemented.

References: [custom HTTP connections](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0),
[redirect behavior](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.allowautoredirect?view=net-10.0),
[Windows user data protection](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata).
