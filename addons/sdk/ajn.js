// AJN API 1.7 development transport, compatible with the 1.0 JSON-only methods. The host independently
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
    function readBytes(length) {
        const bytes = new Uint8Array(length);
        let count = 0;
        while (count < length) {
            if (at === end) {
                end = Javy.IO.readSync(0, input); at = 0;
                if (end === 0) throw new Error("AJN binary response ended early");
            }
            const take = Math.min(length - count, end - at);
            bytes.set(input.subarray(at, at + take), count);
            count += take; at += take;
        }
        return bytes;
    }
    function request(method, params = {}, frameResponse = false) {
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
        const result = response.result;
        if (!frameResponse) return result;
        if (!result || !Number.isInteger(result.byteLength) || result.byteLength < 0 || result.byteLength > 320 * 180 * 4)
            throw new Error("Invalid AJN binary response length");
        if (frameResponse === "network") {
            if (result.byteLength > 65536 || !["pending", "completed", "failed"].includes(result.state) ||
                (result.state !== "completed" && result.byteLength !== 0)) throw new Error("Invalid AJN network result");
            return Object.assign({}, result, { body: readBytes(result.byteLength) });
        }
        if (frameResponse === "chunk") {
            if (result.byteLength > 32768) throw new Error("Invalid AJN chunk size");
            return Object.assign({}, result, { body: readBytes(result.byteLength) });
        }
        if (result.frame === null && result.byteLength === 0) return null;
        const frame = result.frame;
        if (!frame || !Number.isInteger(frame.width) || !Number.isInteger(frame.height) || frame.width < 1 || frame.width > 320 ||
            frame.height < 1 || frame.height > 180 || frame.format !== "bgra8" || frame.stage !== "processed" || result.byteLength !== frame.width * frame.height * 4)
            throw new Error("Invalid AJN sample metadata");
        return Object.assign({}, frame, { pixels: readBytes(result.byteLength) });
    }
    function base64(bytes) {
        if (typeof bytes === "string") bytes = encoder.encode(bytes);
        if (!(bytes instanceof Uint8Array) || bytes.length > 32768) throw new Error("Expected at most 32768 network bytes");
        const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        let text = "";
        for (let i = 0; i < bytes.length; i += 3) {
            const a = bytes[i], b = bytes[i + 1] || 0, c = bytes[i + 2] || 0;
            text += alphabet[a >> 2] + alphabet[((a & 3) << 4) | (b >> 4)] +
                (i + 1 < bytes.length ? alphabet[((b & 15) << 2) | (c >> 6)] : "=") +
                (i + 2 < bytes.length ? alphabet[c & 63] : "=");
        }
        return text;
    }
    const api = Object.freeze({
        info: () => request("host.info"),
        httpServer: Object.freeze({
            selections: () => request("httpServer.selections"),
            formats: () => request("httpServer.formats"),
            open: listenerId => request("httpServer.open", { listenerId }),
            status: serverId => request("httpServer.status", { serverId }),
            requestClose: serverId => request("httpServer.requestClose", { serverId }),
            requestStatus: requestId => request("httpServer.requestStatus", { requestId }),
            cancelRequest: requestId => request("httpServer.cancelRequest", { requestId }),
            extend: (requestId, seconds) => request("httpServer.extend", { requestId, seconds }),
            readBody: requestId => request("httpServer.readBody", { requestId }),
            bodyChunk: (requestId, offset, count = 32768) => request("httpServer.bodyChunk", { requestId, offset, count }, "chunk"),
            beginResponse: (requestId, response) => request("httpServer.beginResponse", { requestId, status: response.status, headers: response.headers || [] }),
            appendResponse: (requestId, body) => request("httpServer.appendResponse", { requestId, bodyBase64: base64(body) }),
            finishResponse: requestId => request("httpServer.finishResponse", { requestId }),
            respond: (requestId, response) => request("httpServer.respond", {
                requestId, status: response.status, headers: response.headers || [],
                bodyBase64: base64(response.body || new Uint8Array(0)),
            }),
        }),
        log: message => request("log.write", { message: String(message) }),
        httpProxy: Object.freeze({
            formats: () => request("httpProxy.formats"),
            forward: (requestId, destinationId, options = {}) => request("httpProxy.forward", { requestId, destinationId, options: Object.assign({ path: "/" }, options) }),
            buffer: (destinationId, options = {}) => {
                const wire = Object.assign({ path: "/" }, options, { bodyBase64: base64(options.body || new Uint8Array(0)) });
                delete wire.body;
                return request("httpProxy.buffer", { destinationId, options: wire });
            },
            status: operationId => request("httpProxy.status", { operationId }),
            read: (operationId, offset, count = 32768) => request("httpProxy.read", { operationId, offset, count }, "chunk"),
            cancel: operationId => request("httpProxy.cancel", { operationId }),
            close: operationId => request("httpProxy.close", { operationId }),
        }),
        requestCredentials: Object.freeze({
            formats: () => request("requestCredentials.formats"),
            capture: (requestId, destinationId, mappings, seconds = 86400) => request("requestCredentials.capture", { requestId, destinationId, mappings, seconds }),
            status: credentialId => request("requestCredentials.status", { credentialId }),
            release: credentialId => request("requestCredentials.release", { credentialId }),
        }),
        settings: Object.freeze({ get: () => request("settings.get") }),
        remoteSources: Object.freeze({ formats: () => request("remoteSources.formats") }),
        mediaProbe: Object.freeze({
            formats: () => request("mediaProbe.formats"),
            open: source => request("mediaProbe.open", { source: source.type === "local" ? source : Object.assign({ type: "http", path: "/", useCredential: false }, source) }),
            status: probeId => request("mediaProbe.status", { probeId }),
            read: (probeId, offset, count = 32768) => request("mediaProbe.result", { probeId, offset, count }, "chunk"),
            result: probeId => {
                const first = request("mediaProbe.result", { probeId, offset: 0, count: 32768 }, "chunk");
                if (!Number.isInteger(first.totalBytes) || first.totalBytes < 1 || first.totalBytes > 262144) throw new Error("Invalid probe result length");
                const bytes = new Uint8Array(first.totalBytes);
                let chunk = first, offset = 0;
                for (;;) {
                    if (chunk.offset !== offset || chunk.totalBytes !== bytes.length || chunk.byteLength < 1 || offset + chunk.byteLength > bytes.length)
                        throw new Error("Invalid probe chunk sequence");
                    bytes.set(chunk.body, offset); offset += chunk.byteLength;
                    if (offset === bytes.length) break;
                    chunk = request("mediaProbe.result", { probeId, offset, count: 32768 }, "chunk");
                }
                return JSON.parse(decoder.decode(bytes));
            },
            cancel: probeId => request("mediaProbe.cancel", { probeId }),
            close: probeId => request("mediaProbe.close", { probeId }),
        }),
        subtitles: Object.freeze({
            formats: () => request("subtitles.formats"),
            open: (source, options = {}) => request("subtitles.open", {
                source: source.type === "local" ? source : Object.assign({ type: "http", path: "/", useCredential: false }, source), options,
            }),
            status: subtitleId => request("subtitles.status", { subtitleId }),
            read: (subtitleId, offset, count = 32768) => request("subtitles.read", { subtitleId, offset, count }, "chunk"),
            serve: (requestId, subtitleId) => request("subtitles.serve", { requestId, subtitleId }),
            cancel: subtitleId => request("subtitles.cancel", { subtitleId }),
            close: subtitleId => request("subtitles.close", { subtitleId }),
        }),
        mediaStreams: Object.freeze({
            formats: () => request("mediaStreams.formats"),
            open: (source, profileId, options) => request("mediaStreams.open", {
                source: source.type === "local" ? source : Object.assign({ type: "http", path: "/", useCredential: false }, source),
                profileId: profileId || null,
                options: Object.assign({}, options, { encoding: Object.assign({ audioCodec: "none", audioKbps: 128, keyframeFrames: 60, lengthSeconds: 0 }, options.encoding) }),
            }),
            status: streamId => request("mediaStreams.status", { streamId }),
            segments: (streamId, cursor = null, limit = 32) => request("mediaStreams.segments", { streamId, cursor, limit }),
            setDemand: (streamId, seconds) => request("mediaStreams.setDemand", { streamId, seconds }),
            pause: (streamId, paused) => request("mediaStreams.pause", { streamId, paused }),
            serve: (requestId, streamId, generationId, resourceId) => request("mediaStreams.serve", { requestId, streamId, generationId, resourceId }),
            requestClose: streamId => request("mediaStreams.requestClose", { streamId }),
        }),
        outputs: Object.freeze({
            formats: () => request("outputs.formats"),
            openRemote: (source, profileId, options) => request("outputs.openRemote", {
                source: Object.assign({type: "http", path: "/", useCredential: false}, source), profileId: profileId || null,
                encoding: Object.assign({audioCodec: "none", audioKbps: 128, keyframeFrames: 60, lengthSeconds: 0}, options.encoding),
                destination: Object.assign({type: "httpUpload", method: "POST", path: "/", useCredential: false}, options.destination),
                playback: options.playback,
            }),
            open: (sourceId, profileId, options) => request("outputs.open", {
                sourceId, profileId: profileId || null,
                encoding: Object.assign({audioCodec: "none", audioKbps: 128, keyframeFrames: 60, lengthSeconds: 0}, options.encoding),
                destination: Object.assign({type: "httpUpload", method: "POST", path: "/", useCredential: false}, options.destination),
                playback: options.playback,
            }),
        }),
        outputPlayback: Object.freeze({ formats: () => request("outputPlayback.formats") }),
        network: Object.freeze({
            selections: () => request("network.selections"),
            request: (destinationId, options = {}) => request("network.request", {
                destinationId, method: options.method || "GET", path: options.path || "/", headers: options.headers || {},
                bodyBase64: base64(options.body || new Uint8Array(0)), useCredential: options.useCredential === true,
            }),
            result: requestId => request("network.result", { requestId }, "network"),
            cancel: requestId => request("network.cancel", { requestId }),
            sendDatagram: (destinationId, bytes) => request("network.sendDatagram", { destinationId, bodyBase64: base64(bytes) }),
        }),
        timers: Object.freeze({
            set: (timerId, intervalMs, repeat = true) => request("timers.set", { timerId, intervalMs, repeat }),
            clear: timerId => request("timers.clear", { timerId }),
        }),
        frames: Object.freeze({
            subscribe: (sessionId, options = {}) => request("frames.subscribe", Object.assign({
                sessionId, stage: "processed", format: "bgra8", width: 64, height: 36, maxFps: 30,
            }, options, { sessionId })),
            read: subscriptionId => request("frames.read", { subscriptionId }, true),
            unsubscribe: subscriptionId => request("frames.unsubscribe", { subscriptionId }),
        }),
        playerFrames: Object.freeze({
            list: () => request("playerFrames.list"),
            subscribe: (playerId, options = {}) => request("playerFrames.subscribe", Object.assign({
                playerId, stage: "processed", format: "bgra8", width: 64, height: 36, maxFps: 30,
            }, options, { playerId })),
            read: subscriptionId => request("playerFrames.read", { subscriptionId }, true),
            unsubscribe: subscriptionId => request("playerFrames.unsubscribe", { subscriptionId }),
        }),
        storage: Object.freeze({
            get: key => request("storage.get", { key }),
            set: (key, value) => request("storage.set", { key, value }),
        }),
        sessions: Object.freeze({
            selections: () => request("sessions.selections"),
            open: (sourceId, profileId = null) => request("sessions.open", { sourceId, profileId }),
            openRemote: (source, profileId = null) => request("sessions.openRemote", {
                source: Object.assign({type: "http", path: "/", useCredential: false}, source), profileId,
            }),
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
                    if (value && typeof value.then === "function") throw new Error("AJN callbacks must finish synchronously; use host timers for future events");
                    write({ jsonrpc: "2.0", id: message.id, result: value === undefined ? null : value });
                } catch (error) {
                    write({ jsonrpc: "2.0", id: message.id, error: { code: -32000, message: String(error.message || error).slice(0, 4096) } });
                    return;
                }
            }
        },
    };
})();
