// Generic broker example. All destinations come from explicit host consent.
let requestId = null, last = { message: "No request sent." };
function onEvent(event, ajn) {
    if (event.name === "start") return { message: "Ready. Approve a service, start this addon, then choose Send request." };
    if (event.name === "stop") { if (requestId) ajn.network.cancel(requestId); return; }
    if (event.name === "timer" && event.data.timerId === "request") {
        try {
            const response = ajn.network.result(requestId);
            if (response.state === "pending") return;
            last = response.state === "failed" ? response.error : {
                state: response.state, status: response.status, bytes: response.body.length,
                contentType: response.headers["content-type"] || null,
            };
            requestId = null; ajn.timers.clear("request");
        } catch (error) {
            if (error.code === "bandwidth_exceeded") return;
            last = { error: error.code || "request_failed", message: error.message };
            if (requestId) { try { ajn.network.cancel(requestId); } catch (_) {} }
            requestId = null; ajn.timers.clear("request");
        }
        return;
    }
    if (event.name !== "action") return;
    try {
        if (event.data.id === "list") return ajn.network.selections();
        if (event.data.id === "status") return last;
        if (event.data.id === "cancel") {
            if (requestId) { ajn.network.cancel(requestId); last = { state: "cancelling" }; }
            return last;
        }
        if (event.data.id === "request") {
            if (requestId) return { message: "A request is already active. Wait for it or cancel it." };
            const settings = ajn.settings.get();
            const destinations = ajn.network.selections().destinations;
            const destination = destinations[Math.floor(settings.destinationIndex) - 1];
            if (!destination) return { message: "Approve and select a service first." };
            if (destination.protocol === "udp") return last = ajn.network.sendDatagram(destination.id, settings.datagram);
            requestId = ajn.network.request(destination.id, { path: settings.path, useCredential: settings.useCredential }).requestId;
            ajn.timers.set("request", 100); return last = { state: "pending" };
        }
    } catch (error) { return last = { error: error.code || "request_failed", message: error.message }; }
}
