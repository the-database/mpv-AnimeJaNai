// Diagnostic only. This example deliberately accepts only a loopback listener.
// Real integrations must authenticate clients before proxying or serving media.
let serverId = null, destinationId = null;
const jobs = [];
const operations = [];
const credentials = new Map();
function onEvent(event, ajn) {
    if (event.name === "start") return { message: "Approve a loopback listener and HTTP service, then open the bridge." };
    if (event.name === "stop") {
        if (serverId) { const closing = serverId; serverId = null; ajn.httpServer.requestClose(closing); }
        ajn.timers.clear("poll"); return;
    }
    if (event.name === "action") {
        if (event.data.id === "open") {
            if (serverId) return { message: "Bridge already open or closing." };
            const listener = ajn.httpServer.selections().listeners.find(l => l.binding.scope === "loopback");
            const destination = ajn.network.selections().destinations.find(d => d.protocol === "http" || d.protocol === "https");
            if (!listener || !destination) return { message: "Approve a loopback listener and one HTTP service first." };
            destinationId = destination.id;
            serverId = ajn.httpServer.open(listener.id).serverId;
            ajn.timers.set("poll", 100);
        }
        if (event.data.id === "close" && serverId) { const closing = serverId; serverId = null; ajn.httpServer.requestClose(closing); }
        if (!serverId) return { state: "closed" };
        try { return ajn.httpServer.status(serverId); }
        catch (error) { if (error.code !== "server_not_found") throw error; serverId = null; return { state: "closed" }; }
    }
    if (event.name === "http.request") {
        const r = event.data;
        try {
            if (r.path === "/authorized-proxy") {
                // Example upstream contract: /account returns HTTP 200 and {allowed:true}.
                // Replace that validation with the target service's actual authorization policy.
                const context = ajn.requestCredentials.capture(r.requestId, destinationId, [
                    { from: "header", name: "X-Client-Key", to: "header", target: "X-Upstream-Key" }
                ], 120);
                try {
                    ajn.httpServer.extend(r.requestId, 40);
                    const operationId = ajn.httpProxy.buffer(destinationId, { path: "/account", credentialId: context.credentialId, maximumBytes: 32768 }).operationId;
                    operations.push(operationId); jobs.push({ requestId: r.requestId, operationId, authorize: true, credentialId: context.credentialId });
                } catch (error) { ajn.requestCredentials.release(context.credentialId); throw error; }
            } else if (r.path === "/control" && r.method === "POST") {
                ajn.httpServer.readBody(r.requestId);
                jobs.push({ requestId: r.requestId });
            } else if (r.path === "/proxy") {
                operations.push(ajn.httpProxy.forward(r.requestId, destinationId, { path: "/" }).operationId);
            } else if (r.path === "/metadata" && r.method === "GET") {
                ajn.httpServer.extend(r.requestId, 40);
                const operationId = ajn.httpProxy.buffer(destinationId, { path: "/", maximumBytes: 262144 }).operationId;
                operations.push(operationId);
                jobs.push({ requestId: r.requestId, operationId });
            } else ajn.httpServer.respond(r.requestId, { status: 404, body: "Use POST /control, /proxy or GET /metadata." });
        } catch (error) {
            if (error.code === "request_not_found") return;
            try { ajn.httpServer.respond(r.requestId, { status: error.code === "credential_field_missing" ? 401 : 503, body: "Operation unavailable or client credential missing." }); }
            catch (closing) { if (!["request_not_found", "request_claimed"].includes(closing.code)) throw closing; }
        }
        return;
    }
    if (event.name !== "timer" || event.data.timerId !== "poll") return;
    // Limit work per callback; clients waiting for I/O never block the Wasm gate.
    for (let budget = Math.min(2, jobs.length); budget > 0; budget--) {
        const job = jobs.shift();
        try {
            const status = job.operationId ? ajn.httpProxy.status(job.operationId) : ajn.httpServer.requestStatus(job.requestId);
            if (job.operationId ? status.state === "pending" : status.bodyState === "reading") { jobs.push(job); continue; }
            const failed = job.operationId ? status.state === "failed" : status.bodyState === "failed";
            if (failed) ajn.httpServer.respond(job.requestId, { status: 502, body: "Body could not be read." });
            else if (job.authorize) {
                let allowed = false;
                try {
                    if (status.status === 200) allowed = JSON.parse(new TextDecoder().decode(ajn.httpProxy.read(job.operationId, 0).body)).allowed === true;
                } catch (_) { /* Invalid upstream authorization data denies access. */ }
                if (!allowed) ajn.httpServer.respond(job.requestId, { status: 403, body: "Client is not authorized." });
                else {
                    const forwarded = ajn.httpProxy.forward(job.requestId, destinationId, { path: "/", credentialId: job.credentialId }).operationId;
                    operations.push(forwarded); credentials.set(forwarded, job.credentialId); job.credentialId = null;
                }
            } else {
                ajn.httpServer.beginResponse(job.requestId, { status: job.operationId ? status.status : 200 });
                for (let offset = 0; offset < status.bodyLength; offset += 32768) {
                    const chunk = job.operationId ? ajn.httpProxy.read(job.operationId, offset) : ajn.httpServer.bodyChunk(job.requestId, offset);
                    ajn.httpServer.appendResponse(job.requestId, chunk.body);
                }
                ajn.httpServer.finishResponse(job.requestId);
            }
        } catch (error) {
            if (job.operationId) { try { ajn.httpProxy.cancel(job.operationId); } catch (_) {} }
            if (!["request_not_found", "operation_not_found"].includes(error.code)) throw error;
        } finally {
            // Pending validation retains its context; transfer completion releases it later.
            if (job.credentialId && !jobs.includes(job)) ajn.requestCredentials.release(job.credentialId);
        }
    }
    for (let budget = Math.min(4, operations.length); budget > 0; budget--) {
        const id = operations.shift();
        if (!jobs.some(j => j.operationId === id) && ajn.httpProxy.status(id).cleanupReady) {
            ajn.httpProxy.close(id);
            if (credentials.has(id)) { ajn.requestCredentials.release(credentials.get(id)); credentials.delete(id); }
        } else operations.push(id);
    }
}
