// No network permissions or physical-light effects. GPU work exists only
// between explicit subscribe and unsubscribe/stop. The host never runs the
// color estimator or decides how/when a lighting product should update lights.
let subscription = null, captured = null, exportOffset = 0;
function describe(frame) {
    const metadata = Object.assign({}, frame); delete metadata.pixels; delete metadata.mask;
    let min = 65535, max = 0, sum = 0, foreground = 0;
    for (let i = 0; i < frame.mask.length; i += 2) {
        const value = frame.mask[i] | (frame.mask[i + 1] << 8);
        min = Math.min(min, value); max = Math.max(max, value); sum += value;
        if (value >= 32768) foreground++;
    }
    metadata.maskSummary = {min: min / 65535, max: max / 65535,
        mean: sum / (frame.mask.length / 2) / 65535, foregroundFraction: foreground / (frame.mask.length / 2)};
    return metadata;
}
function onEvent(event, ajn) {
    if (event.name === "stop") {
        if (subscription) ajn.playerAnalysis.unsubscribe(subscription);
        subscription = null; captured = null; return;
    }
    if (event.name !== "action") return;
    try {
        const action = event.data.id;
        if (action === "list") return ajn.playerAnalysis.list().map((p,i) => Object.assign({number:i+1},p));
        if (action === "subscribe") {
            if (subscription) ajn.playerAnalysis.unsubscribe(subscription);
            subscription = null; captured = null;
            const settings = ajn.settings.get(), player = ajn.playerAnalysis.list()[Math.floor(settings.playerNumber)-1];
            if (!player) return {error:"Open an AJN player first, then select its number."};
            const selected = ajn.playerAnalysis.subscribe(player.playerId,{width:Math.floor(settings.width),height:Math.floor(settings.height),maxFps:Math.floor(settings.fps)});
            subscription = selected.subscriptionId; return selected;
        }
        if (action === "unsubscribe") {
            if (subscription) ajn.playerAnalysis.unsubscribe(subscription);
            subscription = null; captured = null; return {observing:false};
        }
        if (action === "latest" || action === "capture") {
            if (!subscription) return {state:"idle",message:"Choose Start character analysis first."};
            const status = ajn.playerAnalysis.status(subscription), frame = ajn.playerAnalysis.read(subscription);
            if (!frame) return {status,frame:null};
            if (action === "capture") { captured = frame; exportOffset = 0; }
            return {status,frame:describe(frame)};
        }
        if (action === "nextChunk") {
            if (!captured) return {error:"Capture a pair first."};
            const total = captured.pixels.length + captured.mask.length;
            const offset = exportOffset, end = Math.min(total,offset+4096);
            let hex = "";
            // Diagnostic export only: small chunks fit the existing action UI.
            // The native-to-guest observation itself uses bounded binary data.
            for (;exportOffset<end;exportOffset++) {
                const value = exportOffset<captured.pixels.length?captured.pixels[exportOffset]:captured.mask[exportOffset-captured.pixels.length];
                hex += value.toString(16).padStart(2,"0");
            }
            return {frameId:captured.frameId,epoch:captured.epoch,offset,total,hex,done:exportOffset===total};
        }
    } catch (error) { return {error:error.code||"analysis_unavailable",message:error.message}; }
}
