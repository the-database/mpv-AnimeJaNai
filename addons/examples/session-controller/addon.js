// Generic API example, not a Plex or lighting implementation.
let sessions = [];
function onEvent(event, ajn) {
    function closeAll() {
        for (const id of sessions) {
            try { ajn.sessions.requestClose(id); }
            catch (error) { if (error.code !== "session_not_found") throw error; }
        }
        sessions = [];
    }
    if (event.name === "stop") { closeAll(); return; }
    if (event.name !== "action") return;
    const settings = ajn.settings.get();
    try {
        switch (event.data.id) {
            case "resources": return ajn.sessions.selections();
            case "open": {
                if (sessions.length) return { message: "Close existing sessions first.", sessions };
                const selected = ajn.sessions.selections();
                if (!selected.sources.length || !selected.profiles.length)
                    return { message: "Approve a media file and a saved profile in Manager first." };
                if (!Number.isInteger(settings.sessionCount)) return { message: "Choose a whole number of sessions." };
                for (let i = 0; i < settings.sessionCount; i++)
                    sessions.push(ajn.sessions.open(selected.sources[0].id, selected.profiles[0].id).sessionId);
                return { message: "Sessions are initializing. Use Show session status.", sessions };
            }
            case "status": return sessions.map(id => ({ id, ...ajn.sessions.status(id) }));
            case "close": closeAll(); return { message: "Session cleanup requested." };
        }
        const index = settings.controlledSession - 1;
        if (!Number.isInteger(index) || !sessions[index]) return { message: "Choose an existing session number." };
        if (event.data.id === "pause") ajn.sessions.pause(sessions[index], true);
        if (event.data.id === "resume") ajn.sessions.pause(sessions[index], false);
        if (event.data.id === "seek") ajn.sessions.seek(sessions[index], 0);
        return { message: "Control accepted. Use Show session status to see the result." };
    } catch (error) {
        return { error: error.code || "operation_failed", message: error.message, sessions };
    }
}
