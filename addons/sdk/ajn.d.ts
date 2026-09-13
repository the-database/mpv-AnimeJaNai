/** AJN addon API 1.2 preview. Plain JavaScript, with optional editor type checking. */
interface AjnFrame {
    frameId: string;
    epoch: string;
    width: number;
    height: number;
    stride: number;
    format: "bgra8";
    stage: "processed";
    /** Processing PTS, not measured display time. */
    ptsSeconds: number | null;
    producerTimeMs: number;
    sourceWidth: number;
    sourceHeight: number;
    rotation: number;
    verticalFlip: boolean;
    pixelAspectRatio: number;
    crop: { x: number; y: number; width: number; height: number };
    color: { primaries: string; transfer: string; matrix: "rgb"; range: "full"; alpha: "opaque" };
    skippedSamples: number;
    producerDrops: number;
    /** Tightly packed bytes, top row first. Do not return pixels as event JSON. */
    pixels: Uint8Array;
}
interface AjnSampleOptions {
    stage?: "processed";
    format?: "bgra8";
    width?: number;
    height?: number;
    maxFps?: number;
}
interface AjnHostInfo {
    id: string;
    api: { major: number; minor: number };
    permissions: string[];
    features: string[];
    capabilities: Record<string, { major: number; minor: number }>;
}
interface AjnApi {
    info(): AjnHostInfo;
    log(message: string): void;
    /** Host-owned declarative settings. Only the user/host can change them. */
    settings: { get(): Record<string, boolean | number | string> };
    /** Timer events have data {timerId, elapsedMs, missedTicks}. All callbacks are
     * serialized. Missed ticks coalesce; eight timers and 60 background events/s. */
    timers: {
        set(timerId: string, intervalMs: number, repeat?: boolean): void;
        clear(timerId: string): void;
    };
    /** Requires frames.read, sessions.manage and frames capability 1.0. */
    frames: {
        subscribe(sessionId: string, options?: AjnSampleOptions): Required<AjnSampleOptions> & { subscriptionId: string };
        /** Latest unread sample, or null. Never waits for the GPU. */
        read(subscriptionId: string): AjnFrame | null;
        unsubscribe(subscriptionId: string): void;
    };
    storage: {
        /** Missing keys return null. Values must be JSON-serializable. */
        get(key: string): unknown;
        set(key: string, value: unknown): void;
    };
    sessions: {
        /** Sessions capability 1.1. Only user-approved resources, never paths. */
        selections(): {
            sources: { id: string; name: string }[];
            profiles: { id: string; name: string; slot: number; backend: string }[];
            maximumConcurrentSessions: number;
        };
        /** Requires sessions.manage and a trusted native provider in the host. */
        open(sourceId: string, profileId?: string | null): { sessionId: string };
        status(sessionId: string): Record<string, unknown>;
        /** Accepted asynchronously; observe status for the resulting state. */
        pause(sessionId: string, paused: boolean): void;
        /** Absolute media seconds. Sessions capability 1.1. */
        seek(sessionId: string, seconds: number): void;
        /** Legacy synchronous release. Use requestClose for native sessions. */
        close(sessionId: string): void;
        /** Sessions 1.1: starts cleanup immediately. Capacity remains reserved
         * until cleanup finishes. Status then returns session_not_found. */
        requestClose(sessionId: string): void;
    };
}
interface AjnEvent { type: "event"; eventId: number; name: string; data: unknown; }
/** Implement this callback in addon.js. Return a JSON value or undefined. */
declare function onEvent(event: AjnEvent, ajn: AjnApi): unknown;
