// Generic remote-media example. Startup performs no network or GPU work.
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
        return { message: "Cleanup requested. Check status before opening more sessions." };
    }
    if (event.name === "start") return { message: "Ready. Approve a media service and processing profile, then choose Process remote media." };
    if (event.name === "stop") { close(); return; }
    if (event.name !== "action") return;
    try {
        if (event.data.id === "resources") return { ...ajn.sessions.selections(), ...ajn.network.selections() };
        if (event.data.id === "formats") return ajn.remoteSources.formats();
        if (event.data.id === "status") return status();
        if (event.data.id === "close") return close();
        if (["pause", "resume", "seek"].includes(event.data.id)) {
            const current = status(), settings = ajn.settings.get();
            if (event.data.id === "seek" && current.some(s => s.input?.seekable !== true || s.output))
                return { message: "All sessions must be seekable and have no encoded output. Close and reopen outputs to restart them." };
            for (const id of sessions) {
                if (event.data.id === "seek") ajn.sessions.seek(id, settings.seekSeconds);
                else ajn.sessions.pause(id, event.data.id === "pause");
            }
            return { message: "Control requested. Check session status." };
        }
        if (event.data.id === "open" || event.data.id === "send") {
            status();
            if (sessions.length) return { message: "Close existing sessions first, including completed sessions.", sessions };
            const settings = ajn.settings.get();
            for (const key of ["sourceIndex", "profileIndex", "destinationIndex", "sessionCount"])
                if (!Number.isInteger(settings[key])) return { message: "Choose a whole number for " + key + "." };
            const profile = ajn.sessions.selections().profiles[settings.profileIndex - 1];
            const choices = ajn.network.selections().destinations;
            const source = choices[settings.sourceIndex - 1], destination = choices[settings.destinationIndex - 1];
            if (!source || !profile || !["http", "https"].includes(source.protocol))
                return { message: "Approve and select an HTTP media service and processing profile first." };
            if (event.data.id === "send" && (!destination || !["http", "https"].includes(destination.protocol)))
                return { message: "Approve and select an HTTP upload receiver first." };
            const input = { destinationId: source.id, path: settings.sourcePath, useCredential: settings.useSourceCredential };
            for (let i = 0; i < settings.sessionCount; i++) {
                const opened = event.data.id === "open" ? ajn.sessions.openRemote(input, profile.id) :
                    ajn.outputs.openRemote(input, profile.id, {
                        encoding: { videoCodec: "h264", container: "matroska", videoKbps: 4000,
                            audioCodec: "aac", lengthSeconds: settings.lengthSeconds },
                        destination: { destinationId: destination.id, path: settings.destinationPath,
                            useCredential: settings.useDestinationCredential },
                    });
                sessions.push(opened.sessionId);
            }
            return { message: "Sessions are initializing. Check status for media and processing results.", sessions };
        }
    } catch (error) { return { error: error.code || "input_failed", message: error.message, sessions }; }
}
