// Generic framework inspector: no network traffic, playback controls or device
// integrations. Keep the addon running between subscribe and latest actions.
let subscription = null;
function onEvent(event, ajn) {
    if (event.name === "stop") {
        if (subscription) {
            try { ajn.playerFrames.unsubscribe(subscription); } catch (_) {}
            subscription = null;
        }
        return;
    }
    if (event.name !== "action") return;
    const action = event.data.id;
    try {
        if (action === "list") return ajn.playerFrames.list().map((player, index) => Object.assign({number: index + 1}, player));
        if (action === "unsubscribe") {
            if (subscription) ajn.playerFrames.unsubscribe(subscription);
            subscription = null;
            return {observing: false};
        }
        if (action === "subscribe") {
            if (subscription) ajn.playerFrames.unsubscribe(subscription);
            subscription = null;
            const settings = ajn.settings.get();
            const player = ajn.playerFrames.list()[Math.floor(settings.playerNumber) - 1];
            if (!player) return {error: "The selected player is not attached."};
            const selected = ajn.playerFrames.subscribe(player.playerId, {width: Math.floor(settings.width), height: Math.floor(settings.height), maxFps: Math.floor(settings.fps)});
            subscription = selected.subscriptionId;
            return selected;
        }
        if (action === "latest") {
            if (!subscription) return {error: "Choose Observe selected player first."};
            const frame = ajn.playerFrames.read(subscription);
            if (!frame) return {frame: null};
            let checksum = 0;
            for (const byte of frame.pixels) checksum = (checksum + byte) >>> 0;
            return {frameId: frame.frameId, epoch: frame.epoch, ptsSeconds: frame.ptsSeconds,
                width: frame.width, height: frame.height, sourceWidth: frame.sourceWidth, sourceHeight: frame.sourceHeight,
                color: frame.color, stage: frame.stage, bytes: frame.pixels.length, checksum};
        }
    } catch (error) {
        return {error: error.code || "sample_unavailable", message: error.message};
    }
}
