// General output API example. Starting it never sends media automatically.
// Only approved opaque resource IDs reach the SDK, never paths or raw handles.
let sessions = [];
function onEvent(event, ajn) {
    function status() {
        const results = [];
        sessions = sessions.filter(id => {
            try { results.push({ id, ...ajn.sessions.status(id) }); return true; }
            catch (error) { if (error.code === "session_not_found") return false; throw error; }
        });
        return results;
    }
    function close() {
        status();
        for (const id of sessions) ajn.sessions.requestClose(id);
        return { message: "Cleanup requested. Show output status to check; close again if cleanup needs a retry." };
    }
    if (event.name === "start") return { message: "Ready. Approve a file, profile and receiver, then choose Send processed media." };
    if (event.name === "stop") { close(); return; }
    if (event.name !== "action") return;
    try {
        if (event.data.id === "resources") return { ...ajn.sessions.selections(), ...ajn.network.selections() };
        if (event.data.id === "formats") return ajn.outputs.formats();
        if (event.data.id === "status") return status();
        if (event.data.id === "close") return close();
        if (event.data.id === "pause" || event.data.id === "resume") {
            for (const id of sessions) ajn.sessions.pause(id, event.data.id === "pause");
            return { message: "Control requested. Check output status." };
        }
        if (event.data.id === "open") {
            status();
            if (sessions.length) return { message: "Close existing outputs first, including completed outputs.", sessions };
            const settings = ajn.settings.get();
            for (const key of ["sourceIndex", "profileIndex", "destinationIndex", "sessionCount", "videoKbps"])
                if (!Number.isInteger(settings[key])) return { message: "Choose a whole number for " + key + "." };
            const selected = ajn.sessions.selections();
            const source = selected.sources[settings.sourceIndex - 1], profile = selected.profiles[settings.profileIndex - 1];
            const destination = ajn.network.selections().destinations[settings.destinationIndex - 1];
            if (!source || !profile || !destination) return { message: "Approve and select a file, profile and receiver first." };
            if (destination.protocol !== "http" && destination.protocol !== "https") return { message: "Select an HTTP or HTTPS upload receiver." };
            for (let i = 0; i < settings.sessionCount; i++) {
                const opened = ajn.outputs.open(source.id, profile.id, {
                    encoding: { videoCodec: settings.videoCodec, container: settings.container, videoKbps: settings.videoKbps,
                        audioCodec: settings.audioCodec, lengthSeconds: settings.lengthSeconds },
                    destination: { destinationId: destination.id, method: settings.method, path: settings.path, useCredential: settings.useCredential },
                });
                sessions.push(opened.sessionId);
            }
            return { message: "Outputs are initializing. Show output status for delivery and processing results.", sessions };
        }
    } catch (error) { return { error: error.code || "output_failed", message: error.message, sessions }; }
}
