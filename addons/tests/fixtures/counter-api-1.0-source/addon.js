/// <reference path="./ajn.d.ts" />
// @ts-check

/** @param {AjnEvent} event @param {AjnApi} ajn */
function onEvent(event, ajn) {
    if (event.name === "start") {
        const previous = ajn.storage.get("starts");
        const starts = (typeof previous === "number" ? previous : 0) + 1;
        ajn.storage.set("starts", starts);
        ajn.log(String(ajn.settings.get().greeting || "Hello") + ": addon started " + starts + " time(s).");
        return { starts, host: ajn.info() };
    }
    if (event.name === "echo") return event.data;
    if (event.name === "action") return { starts: ajn.storage.get("starts") };
    if (event.name === "settings.changed") return { settings: ajn.settings.get() };
    return null;
}
