// Loopback diagnostic. Any local client can read the selected media while open.
// Production integrations must authenticate/authorize EACH request before serve.
// All encoding, files and media bytes stay in AJN. Callbacks never wait for media.
let server = null, stream = null, probe = null, replacement = null, listener = null;
function onEvent(event, ajn) {
    function selection() {
        const s = ajn.settings.get(), choices = ajn.sessions.selections();
        if (!Number.isInteger(s.sourceIndex) || !Number.isInteger(s.profileIndex)) throw new Error("Choose whole resource numbers.");
        const profile = choices.profiles[s.profileIndex - 1];
        if (!profile) throw new Error("Approve and select a profile first.");
        let source;
        if (s.remote) {
            const upstream = ajn.network.selections().destinations.find(d => d.protocol === "http" || d.protocol === "https");
            if (!upstream) throw new Error("Approve an HTTP media service first.");
            source = { type: "http", destinationId: upstream.id, path: s.path, useCredential: s.useCredential };
        } else {
            const selected = choices.sources[s.sourceIndex - 1];
            if (!selected) throw new Error("Approve and select a source first.");
            source = { type: "local", sourceId: selected.id };
        }
        return { source, profileId: profile.id, options: {
            encoding: { videoCodec: "h264", container: s.container, videoKbps: 4000, audioCodec: "aac", audioChannels: 2, lengthSeconds: s.lengthSeconds },
            mode: s.mode, segmentSeconds: s.segmentSeconds,
            playback: { startSeconds: s.startSeconds, audioTrack: s.audioTrackId ? { trackId: s.audioTrackId } : "default",
                subtitles: s.subtitleTrackId ? { mode: "burn", trackId: s.subtitleTrackId } : { mode: "none" } },
        } };
    }
    function begin(plan) {
        if (!server || ajn.httpServer.status(server).state !== "listening") throw new Error("Open the loopback listener first.");
        stream = Object.assign(ajn.mediaStreams.open(plan.source, plan.profileId, plan.options), { container: plan.options.encoding.container });
    }
    function status() {
        return { listener: server ? ajn.httpServer.status(server) : null,
            stream: stream ? ajn.mediaStreams.status(stream.streamId) : null, replacing: replacement !== null };
    }
    function reply(id, code, value, type = "application/json") {
        ajn.httpServer.respond(id, { status: code, headers: [{ name: "Content-Type", values: [type] }], body: typeof value === "string" ? value : JSON.stringify(value) });
    }
    if (event.name === "start") return { message: "Approve a loopback listener, media source and DirectML profile. Open listener, then start a stream." };
    if (event.name === "stop") {
        replacement = null; ajn.timers.clear("poll");
        if (stream) ajn.mediaStreams.requestClose(stream.streamId);
        if (server) { const closing = server; server = null; ajn.httpServer.requestClose(closing); }
        if (probe) ajn.mediaProbe.cancel(probe);
        return;
    }
    if (event.name === "http.closed" && !event.data.requestId && event.data.serverId === server) { server = null; return; }
    if (event.name === "action") {
        try {
            switch (event.data.id) {
                case "resources": return { ...ajn.sessions.selections(), ...ajn.httpServer.selections() };
                case "open":
                    if (!server) {
                        listener = ajn.httpServer.selections().listeners.find(l => l.binding.scope === "loopback");
                        if (!listener) return { message: "Approve a loopback listener first. This example refuses LAN/public bindings." };
                        server = ajn.httpServer.open(listener.id).serverId; ajn.timers.set("poll", 100);
                    }
                    return status();
                case "probe":
                    if (probe) { ajn.mediaProbe.close(probe); probe = null; }
                    probe = ajn.mediaProbe.open(selection().source).probeId; return { probeId: probe };
                case "probeStatus":
                    if (!probe) return { message: "Probe the selected source first." };
                    const p = ajn.mediaProbe.status(probe); return p.state === "completed" ? ajn.mediaProbe.result(probe) : p;
                case "play":
                    if (stream) return { message: "Close or replace the current stream first." };
                    begin(selection()); return status();
                case "replace":
                    replacement = selection();
                    if (stream) ajn.mediaStreams.requestClose(stream.streamId);
                    else { begin(replacement); replacement = null; }
                    return status();
                case "pause": case "resume":
                    if (stream) ajn.mediaStreams.pause(stream.streamId, event.data.id === "pause"); return status();
                case "close":
                    replacement = null;
                    if (stream) ajn.mediaStreams.requestClose(stream.streamId);
                    if (server) { const closing = server; server = null; ajn.httpServer.requestClose(closing); }
                    return status();
                default: return status();
            }
        } catch (error) { return { error: error.code || "example_setup", message: error.message }; }
    }
    if (event.name === "timer" && event.data.timerId === "poll") {
        if (!stream) return;
        const state = ajn.mediaStreams.status(stream.streamId);
        if ((state.state === "closed" || state.state === "failed") && state.nativeCapacityReleased) {
            stream = null;
            if (replacement) { const plan = replacement; replacement = null; begin(plan); }
        }
        // A client supplies actual playback demand; downloading segments is not viewing.
        return;
    }
    if (event.name !== "http.request") return;
    const r = event.data;
    try {
        // Deliberate local-only example authorization, checked for every route.
        if (r.serverId !== server || !listener || listener.binding.scope !== "loopback") { reply(r.requestId, 403, "Denied"); return; }
        if (r.path === "/status") { reply(r.requestId, 200, status()); return; }
        if (!stream) { reply(r.requestId, 404, "No stream"); return; }
        const id = stream.streamId;
        if (r.path === "/segments") {
            const cursor = r.query.find(q => q.name === "cursor");
            reply(r.requestId, 200, ajn.mediaStreams.segments(id, cursor ? cursor.values[0] : null)); return;
        }
        if (r.path === "/demand" && r.method === "POST") {
            const position = r.query.find(q => q.name === "seconds"), generation = r.query.find(q => q.name === "generation");
            if (!generation || generation.values[0] !== stream.generationId || !position || position.values.length !== 1) { reply(r.requestId, 409, "Expected current generation and position"); return; }
            const seconds = Number(position.values[0]);
            if (!Number.isFinite(seconds)) { reply(r.requestId, 400, "Invalid position"); return; }
            ajn.mediaStreams.setDemand(id, seconds); reply(r.requestId, 200, { accepted: true }); return;
        }
        const media = /^\/media\/([a-f0-9]{32})\/([a-f0-9]{32})$/.exec(r.path);
        if (media) { ajn.mediaStreams.serve(r.requestId, id, media[1], media[2]); return; }
        reply(r.requestId, 404, "Use /status, /segments, POST /demand or a generation-scoped /media route.");
    } catch (error) {
        try { reply(r.requestId, ["stale_generation", "segment_expired", "stream_replacement_required"].includes(error.code) ? 409 : 503, { error: error.code || "stream_unavailable" }); }
        catch (closed) { if (!["request_not_found", "request_claimed"].includes(closed.code)) throw closed; }
    }
}
