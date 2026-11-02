// A loopback diagnostic, not authentication or a media streaming application.
// Request metadata stays out of logs and action results.
let serverId = null;
function onEvent(event, ajn) {
    if (event.name === "start") return { message: "Approve a local listener, then choose Open approved listener." };
    if (event.name === "stop") { if (serverId) ajn.httpServer.requestClose(serverId); return; }
    if (event.name === "http.request") {
        const allowed = event.data.method === "GET" || event.data.method === "HEAD";
        try { ajn.httpServer.respond(event.data.requestId, {
            status: allowed ? 200 : 405,
            headers: [{ name: "Content-Type", values: ["text/plain; charset=utf-8"] }],
            body: allowed ? "Hello from an AJN addon." : "Use GET or HEAD.",
        }); } catch (error) {
            // Disconnect/expiry can race event delivery. They do not make the
            // listener unusable for the next client.
            if (error.code !== "request_not_found") throw error;
        }
        return;
    }
    if (event.name !== "action") return;
    try {
        if (event.data.id === "open") {
            if (serverId) return { message: "A listener is already open or closing." };
            const choices = ajn.httpServer.selections().listeners;
            if (!choices.length) return { message: "Approve a loopback listener in Listening access first." };
            serverId = ajn.httpServer.open(choices[0].id).serverId;
            return { message: "Opening listener. Use Show listener status." };
        }
        if (!serverId) return { message: "No listener open." };
        if (event.data.id === "close") { ajn.httpServer.requestClose(serverId); return { message: "Closing. Check status before opening again." }; }
        if (event.data.id === "status") return ajn.httpServer.status(serverId);
    } catch (error) {
        if (error.code === "server_not_found") { serverId = null; return { message: "Listener closed." }; }
        return { message: error.message, code: error.code };
    }
}
