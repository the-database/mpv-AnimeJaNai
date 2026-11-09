# HTTP bridge diagnostic

Build this folder with the matching AJN developer tools, install the `.ajnaddon`
in Manager, approve its permissions, one loopback listener, and one test HTTP
upstream. Start it, then choose **Open approved loopback bridge**.

- `POST /control` returns the same bounded body (maximum 256 KiB).
- `/proxy` forwards natively to the upstream `/`, including WebSocket upgrades.
- `GET /metadata` buffers at most 256 KiB from upstream `/`, then responds in
  bounded chunks.
- `/authorized-proxy` demonstrates per-request credentials: send `X-Client-Key`.
  AJN keeps it hidden from the guest and supplies it as `X-Upstream-Key` to the
  approved upstream `/account`. Only HTTP 200 with JSON `{ "allowed": true }`
  authorizes forwarding to `/` using that same context. All other authorization
  responses deny access. Each request owns a separate temporary context.

The `/account` contract is deliberately a test protocol. Replace it with the
target service's real client validation and authorization rules. The other three
diagnostic routes are intentionally open to local clients and must be removed or
protected before adapting this into a production service. Listener approval and
credential capture do not themselves authenticate anyone. This example refuses
LAN/public listener selections.

Close the bridge or stop the addon to release resources. Completed proxy jobs and
their temporary credentials are released by the polling callback. TLS, listener
approval and input redaction are documented in STREAMING.md and the complete
developer guide; no raw sockets or companion process are needed.
