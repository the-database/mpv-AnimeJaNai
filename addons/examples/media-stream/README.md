# Local streaming developer example

This is an API exercise, not a media-service integration. Build `manifest.json`
and `addon.js` with the release developer tools and install the resulting
`.ajnaddon`. The host needs the matching API 1.7 native streaming runtime.

Approve a loopback listener, one media file and a DirectML profile. Open the
listener, then start a stream from the addon actions. Local clients may read that
selected media while the listener is open. The example refuses LAN/public
listeners; a production addon must authenticate and authorize every request.

`GET /status` returns stream state and generation. `GET /segments` returns up to
32 descriptors; pass the returned cursor as `?cursor=...` for another page. Fetch
`/media/{generationId}/{resourceId}` for a segment, initialization object or
continuous resource. Completed objects support HEAD, single byte ranges and
conditional ETags. Continuous output starts at the beginning and supports no
arbitrary byte ranges. Media bytes travel through the host, never Wasm JSON.

Report the client's actual source position with
`POST /demand?generation={generationId}&seconds={sourceSeconds}`. This drives the
15-second production window. While deliberately paused, send the same ownership
update periodically (for example every minute). Otherwise abandonment expires
the stream after ten minutes. Downloads alone do not advance viewing position.

To seek or change tracks, probe the source, copy the chosen track IDs into the
addon settings, set the new start position, and use **Replace playback with
current settings**. The example closes the old stream, waits for its encoder
capacity to be released, then opens the replacement. Every replacement has a new
generation. A real integration should also cancel its pending client responses
and reject late requests from the previous generation.

The example produces H.264/AAC stereo, with selectable containers and continuous
or segmented delivery. Segmented Matroska objects are independent chunks, not
universally compatible HLS. Construct client-specific manifests using the actual
durations and initialization IDs returned by the host. This example deliberately
exposes those primitives so an integration can choose its own protocol.

For an approved remote source, enable the remote option and set its relative
path. Production multiuser integrations should use request-derived credential
contexts instead of this example's optional service credential.
