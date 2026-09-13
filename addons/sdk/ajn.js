// AJN API 1.0 transport. This SDK is a convenience layer; the host independently
// validates every message and permission even when an addon replaces this code.
const __ajnSdk = (() => {
    const maximum = 128 * 1024;
    const input = new Uint8Array(4096);
    const line = new Uint8Array(maximum);
    let at = 0, end = 0, nextId = 1;
    const encoder = new TextEncoder();
    const decoder = new TextDecoder();
    function read() {
        let length = 0;
        for (;;) {
            if (at === end) {
                end = Javy.IO.readSync(0, input);
                at = 0;
                if (end === 0) throw new Error("AJN host disconnected");
            }
            const byte = input[at++];
            if (byte === 10) return JSON.parse(decoder.decode(line.subarray(0, length)));
            if (length === maximum) throw new Error("AJN message size exceeded");
            line[length++] = byte;
        }
    }
    function write(message) {
        const bytes = encoder.encode(JSON.stringify(message) + "\n");
        if (bytes.length > maximum + 1) throw new Error("AJN message size exceeded");
        let sent = 0;
        while (sent < bytes.length) {
            const count = Javy.IO.writeSync(1, bytes.subarray(sent));
            if (count <= 0) throw new Error("AJN host disconnected");
            sent += count;
        }
    }
    function request(method, params = {}) {
        const id = nextId++;
        if (nextId >= Number.MAX_SAFE_INTEGER) nextId = 1;
        write({ jsonrpc: "2.0", id, method, params });
        const response = read();
        if (response.jsonrpc !== "2.0" || response.id !== id) throw new Error("Mismatched AJN response");
        if (response.error) {
            const error = new Error(response.error.message);
            error.code = response.error.data && response.error.data.code;
            throw error;
        }
        return response.result;
    }
    const api = Object.freeze({
        info: () => request("host.info"),
        log: message => request("log.write", { message: String(message) }),
        settings: Object.freeze({ get: () => request("settings.get") }),
        storage: Object.freeze({
            get: key => request("storage.get", { key }),
            set: (key, value) => request("storage.set", { key, value }),
        }),
        sessions: Object.freeze({
            selections: () => request("sessions.selections"),
            open: (sourceId, profileId = null) => request("sessions.open", { sourceId, profileId }),
            status: sessionId => request("sessions.status", { sessionId }),
            pause: (sessionId, paused) => request("sessions.pause", { sessionId, paused }),
            seek: (sessionId, seconds) => request("sessions.seek", { sessionId, seconds }),
            close: sessionId => request("sessions.close", { sessionId }),
            requestClose: sessionId => request("sessions.requestClose", { sessionId }),
        }),
    });
    return {
        run(handler) {
            write({ jsonrpc: "2.0", id: "hello", method: "addon.hello", params: { major: 1, minMinor: 0 } });
            const welcome = read();
            if (welcome.jsonrpc !== "2.0" || welcome.id !== "hello" || !welcome.result) throw new Error("Missing AJN welcome");
            for (;;) {
                const message = read();
                if (message.jsonrpc !== "2.0" || message.method !== "addon.event") throw new Error("Expected AJN event");
                const event = message.params;
                try {
                    const value = event.name === "host.ping" ? null : handler(event, api);
                    if (value && typeof value.then === "function") throw new Error("API 1.0 callbacks must finish synchronously");
                    write({ jsonrpc: "2.0", id: message.id, result: value === undefined ? null : value });
                } catch (error) {
                    write({ jsonrpc: "2.0", id: message.id, error: { code: -32000, message: String(error.message || error).slice(0, 4096) } });
                    return;
                }
            }
        },
    };
})();
