// A small transport/reference example, not an MVTools port or a quality claim.
// Replace analyze() with a bounded motion-aware detector for production use.
let detectors = [];
let settings;
function analyze(pair, threshold) {
    let difference = 0;
    for (let i = 0; i < pair.previous.length; i++)
        difference += Math.abs(pair.previous[i] - pair.current[i]);
    return difference / (255 * pair.previous.length) > threshold ? "cut" : "continuous";
}
function onEvent(event, ajn) {
    if (event.name === "start" || event.name === "settings.changed") {
        settings = ajn.settings.get();
        return;
    }
    if (event.name === "scene.request") {
        const id = event.data.detectorId;
        const pair = ajn.sceneDetection.read(id);
        if (!pair) return;
        const decision = settings.decision === "analyze" ? analyze(pair, settings.threshold) : settings.decision;
        const result = ajn.sceneDetection.submit(id, pair.requestId, decision);
        const detector = detectors.find(d => d.id === id);
        if (detector) detector.lastPair = { requestId: pair.requestId, epoch: pair.epoch,
            previousPtsSeconds: pair.previousPtsSeconds, currentPtsSeconds: pair.currentPtsSeconds,
            sourceWidth: pair.sourceWidth, sourceHeight: pair.sourceHeight,
            decision, submitted: result.accepted };
        return result;
    }
    if (event.name === "stop" || (event.name === "action" && event.data.id === "detach")) {
        for (const d of detectors) {
            try { ajn.sceneDetection.detach(d.id); } catch (e) { if (e.code !== "scene_detector_not_found") throw e; }
        }
        detectors = []; return { attached: 0 };
    }
    if (event.name === "action" && event.data.id === "attach") {
        const players = ajn.sceneDetection.list();
        detectors = detectors.filter(d => players.some(p => p.playerId === d.playerId));
        for (const p of players) {
            if (p.inUse) continue;
            const d = ajn.sceneDetection.attach(p.playerId, { width: 160, height: 90, deadlineMs: 25 });
            detectors.push({ id: d.detectorId, playerId: p.playerId });
        }
        return { attached: detectors.length, message: "RIFE must be enabled in the selected AJN profile." };
    }
    if (event.name === "action" && event.data.id === "status") {
        return detectors.map(d => {
            try { return { detectorId: d.id, ...ajn.sceneDetection.status(d.id), lastPair: d.lastPair || null }; }
            catch (e) { return { detectorId: d.id, state: e.code }; }
        });
    }
}
