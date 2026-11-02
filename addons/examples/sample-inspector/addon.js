// Generic API example. No network destinations or device-specific behavior.
let sessions = [];

function closeSamples(ajn) {
    ajn.timers.clear("samples");
    for (const item of sessions) {
        if (item.subscription) {
            try { ajn.frames.unsubscribe(item.subscription); } catch (_) {}
        }
        try { ajn.sessions.requestClose(item.id); } catch (_) {}
    }
    sessions = [];
}

function onEvent(event, ajn) {
    if (event.name === "start") return { message: "Ready. Approve a file and DirectML profile, then choose Start samples." };
    if (event.name === "stop") { closeSamples(ajn); return; }
    if (event.name === "timer" && event.data.timerId === "samples") {
        for (const item of sessions) {
            if (!item.subscription) continue;
            try {
                const frame = ajn.frames.read(item.subscription);
                if (!frame) continue;
                item.received++;
                const center = (Math.floor(frame.height / 2) * frame.width + Math.floor(frame.width / 2)) * 4;
                item.last = {
                    frameId: frame.frameId, epoch: frame.epoch, ptsSeconds: frame.ptsSeconds,
                    sample: [frame.width, frame.height], source: [frame.sourceWidth, frame.sourceHeight],
                    color: frame.color, rotation: frame.rotation, verticalFlip: frame.verticalFlip,
                    skippedSamples: frame.skippedSamples, producerDrops: frame.producerDrops,
                    binaryBytes: frame.pixels.length, centerBGRA: Array.from(frame.pixels.subarray(center, center + 4)),
                };
            } catch (error) {
                item.error = error.code || String(error.message);
                try { ajn.frames.unsubscribe(item.subscription); } catch (_) {}
                item.subscription = null;
                try { ajn.sessions.requestClose(item.id); } catch (_) {}
            }
        }
        if (!sessions.some(item => item.subscription)) ajn.timers.clear("samples");
    }
    if (event.name !== "action") return;
    if (event.data.id === "close") { closeSamples(ajn); return { message: "Session cleanup requested." }; }
    if (event.data.id === "open") {
        if (sessions.length) return { message: "Close existing sample sessions before reopening them." };
        const selected = ajn.sessions.selections(), settings = ajn.settings.get();
        if (!selected.sources.length || !selected.profiles.length) return { message: "Approve a local file and a DirectML profile first." };
        let error = null;
        for (let i = 0; i < Math.floor(settings.sessionCount); i++) {
            let id = null;
            try {
                id = ajn.sessions.open(selected.sources[0].id, selected.profiles[0].id).sessionId;
                const options = { width: Math.floor(settings.width), height: Math.floor(settings.height), maxFps: Math.floor(settings.maxFps) };
                const subscription = ajn.frames.subscribe(id, options).subscriptionId;
                sessions.push({ id, subscription, received: 0, last: null, error: null });
            } catch (failure) {
                if (id) { try { ajn.sessions.requestClose(id); } catch (_) {} }
                error = failure.code || String(failure.message); break;
            }
        }
        if (sessions.length) ajn.timers.set("samples", Math.max(16, Math.round(1000 / settings.maxFps)));
        return { opened: sessions.length, error };
    }
    if (event.data.id === "status") return sessions.map(item => ({ sessionId: item.id, received: item.received, last: item.last, error: item.error }));
}
