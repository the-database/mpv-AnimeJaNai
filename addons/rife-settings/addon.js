// RIFE Settings: legacy MVTools and optional enhanced cut recovery.
// RIFE must already be enabled in the playback profile.
let settings;
let detectors = [];
let players = [];
function release(ajn) {
    for (const detector of detectors) {
        try { ajn.sceneDetection.detach(detector.id); }
        catch (error) { if (error.code !== "scene_detector_not_found") throw error; }
    }
    detectors = [];
}
function attach(ajn) {
    players = ajn.sceneDetection.list();
    detectors = detectors.filter(d => players.some(p => p.playerId === d.playerId));
    if (settings.sceneMode === "default") return;
    for (const player of players) {
        const supported = settings.sceneMode === "enhanced" ? player.nativeEnhanced : player.nativeMvtools;
        if (player.inUse || !supported || detectors.some(d => d.playerId === player.playerId)) continue;
        try {
            const detector = ajn.sceneDetection.attach(player.playerId, {
                algorithm: settings.sceneMode, thscd1: Math.round(settings.thscd1), thscd2: Math.round(settings.thscd2)
            });
            detectors.push({id: detector.detectorId, playerId: player.playerId});
        } catch (error) {
            if (!["scene_busy", "scene_detector_in_use", "player_not_found"].includes(error.code)) throw error;
        }
    }
}
function status(ajn) {
    attach(ajn);
    const states = players.map(player => {
        const detector = detectors.find(d => d.playerId === player.playerId);
        if (detector) {
            try { return {playerId: player.playerId, ...ajn.sceneDetection.status(detector.id)}; }
            catch (error) { if (error.code !== "scene_detector_not_found") throw error; }
        }
        return {playerId: player.playerId, state: settings.sceneMode === "default" ? "AJN default" :
            settings.sceneMode === "enhanced" && !player.nativeEnhanced ? "Requires the enhanced scene runtime update" :
            !player.nativeMvtools ? "Requires the MVTools runtime update" :
            player.inUse ? "Another scene detector is active" : "Waiting to attach"};
    });
    return {mode: settings.sceneMode, thscd1: Math.round(settings.thscd1), thscd2: Math.round(settings.thscd2),
        message: states.length ? "RIFE must be enabled in the selected playback profile." : "Waiting for an AJN player.", players: states};
}
function onEvent(event, ajn) {
    if (event.name === "start" || event.name === "settings.changed") {
        release(ajn);
        settings = ajn.settings.get();
        if (settings.sceneMode !== "default") ajn.timers.set("players", 1000, true);
        else ajn.timers.clear("players");
        attach(ajn);
    } else if (event.name === "timer" && event.data.timerId === "players") {
        attach(ajn);
    } else if (event.name === "stop") {
        ajn.timers.clear("players"); release(ajn);
    } else if (event.name === "action") {
        if (event.data.id === "apply") { release(ajn); attach(ajn); }
        return status(ajn);
    }
}
