/** AJN addon API 1.0 preview. Plain JavaScript, with optional editor type checking. */
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
    storage: {
        /** Missing keys return null. Values must be JSON-serializable. */
        get(key: string): unknown;
        set(key: string, value: unknown): void;
    };
    sessions: {
        /** Requires sessions.manage and a trusted native provider in the host. */
        open(sourceId: string, profileId?: string | null): { sessionId: string };
        status(sessionId: string): Record<string, unknown>;
        close(sessionId: string): void;
    };
}
interface AjnEvent { type: "event"; eventId: number; name: string; data: unknown; }
/** Implement this callback in addon.js. Return a JSON value or undefined. */
declare function onEvent(event: AjnEvent, ajn: AjnApi): unknown;
