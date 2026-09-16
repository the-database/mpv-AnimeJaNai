/** AJN addon API 1.9 development. Plain JavaScript, with optional editor type checking. */
interface AjnScenePair {
    requestId: string; epoch: string;
    previousPtsSeconds: number; currentPtsSeconds: number;
    width: number; height: number; sourceWidth: number; sourceHeight: number;
    format: "gray8"; stage: "beforeInterpolation"; range: "full";
    /** Remaining native decision budget at the time the host copied the pair. */
    remainingMs: number;
    /** Two row-major width*height grayscale arrays. Encoded SDR, no OSD/subtitles. */
    previous: Uint8Array; current: Uint8Array;
}
interface AjnRemoteSource {
    type?: "http"; destinationId: string; path?: string; useCredential?: boolean;
    /** API 1.7: temporary request-derived context. Mutually exclusive with useCredential. */
    credentialId?: string;
}
type AjnProbeSource = { type: "local"; sourceId: string } | AjnRemoteSource;
interface AjnProbeResult {
    representationId: string; container: string | null; durationSeconds: number | null;
    startSeconds: number | null; seekable: boolean;
    tracks: AjnProbeTrack[];
    chapters: { startSeconds: number | null; endSeconds: number | null; title: string | null }[];
    attachments: { streamIndex: number; name: string | null; mimeType: string | null; byteLength: number; font: boolean }[];
}
interface AjnProbeTrack {
    trackId: string; streamIndex: number; typeOrdinal: number;
    type: "video" | "audio" | "subtitle"; codec: string | null;
    language: string | null; title: string | null; default: boolean; forced: boolean; startSeconds: number | null;
    width?: number | null; height?: number | null;
    pixelFormat?: string | null;
    /** API 1.9: observed codec profile and pixel component depth. */
    profile?: string | null; bitDepth?: number | null;
    pixelAspectRatio?: AjnRatio | null; displayAspectRatio?: AjnRatio | null;
    averageFrameRate?: AjnRatio | null; nominalFrameRate?: AjnRatio | null;
    /** null means the bounded probe cannot establish CFR or VFR. */
    variableFrameRate?: boolean | null; fieldOrder?: "progressive" | "interlaced" | null;
    colorPrimaries?: string | null; colorTransfer?: string | null; colorMatrix?: string | null; colorRange?: string | null;
    rotationDegrees?: number | null; hasMasteringDisplayMetadata?: boolean; hasContentLightMetadata?: boolean;
    channels?: number | null; sampleRate?: number | null; channelLayout?: string | null;
    subtitleKind?: "text" | "bitmap" | null;
}
interface AjnRatio { numerator: number; denominator: number; }
interface AjnRemoteFormats {
    types: string[]; protocols: string[]; containers: string[]; playlists: boolean; redirects: boolean; seek: string;
    maximumBytes: number; maximumBytesPerSecond: number; maximumReadBytes: number; maximumRequests: number;
    ioDeadlineSeconds: number; maximumWallSeconds: number; maximumConcurrentSessions: number;
}
interface AjnOutputOptions {
    encoding: {
        videoCodec: "h264" | "hevc" | "av1"; container: "matroska" | "mpegts" | "fragmentedMp4";
        videoKbps: number; audioCodec?: "none" | "aac" | "opus"; audioKbps?: number;
        keyframeFrames?: number; lengthSeconds?: number; audioChannels?: 2;
        /** API 1.9. Encoder selection never changes the approved inference backend. */
        encoder?: "auto" | "nvenc" | "amf"; bitDepth?: 8 | 10;
    };
    destination: {
        type?: "httpUpload"; destinationId: string; path?: string; method?: "POST" | "PUT";
        useCredential?: boolean;
    } | { type: "servedStream"; mode?: "segments" | "continuous"; segmentSeconds?: number };
    playback?: AjnOutputPlayback;
}
interface AjnOutputPlayback {
    startSeconds?: number;
    audioTrack?: "default" | "none" | { trackId: string };
    subtitles?: { mode: "none" } | { mode: "burn"; trackId: string } | { mode: "burn"; externalSourceId: string }
        | { mode: "burn"; externalRemoteSource: AjnRemoteSource };
}
interface AjnStreamOptions {
    encoding: AjnOutputOptions["encoding"]; playback?: AjnOutputPlayback;
    mode?: "segments" | "continuous"; segmentSeconds?: number;
}
interface AjnStreamHandle { sessionId: string; streamId: string; generationId: string; }
interface AjnStreamSegment {
    resourceId: string; generationId: string; sequence: number;
    sourceStartSeconds: number; sourceEndSeconds: number; durationSeconds: number;
    encodedTimestampOriginSeconds: number | null; byteLength: number;
    independent: boolean; initializationId: string | null; etag: string;
}
interface AjnStreamPage {
    generationId: string; segments: AjnStreamSegment[]; nextCursor: string;
    initializationId: string | null; continuousResourceId: string | null;
}
interface AjnStreamStatus extends AjnStreamHandle {
    state: "ready" | "building" | "opening" | "starting" | "probing" | "loadingSubtitles" | "loading" | "running" | "finishing" | "paused" | "bufferPaused" | "producerCompleted" | "closing" | "closed" | "failed" | "cleanupFailed";
    operation: "required" | "check" | "prepare"; ready: boolean;
    nativeCapacityReleased: boolean; requestedStartSeconds: number; demandPositionSeconds: number; userPaused: boolean;
    retainedStartSeconds: number | null; retainedEndSeconds: number | null;
    native: Record<string, unknown>; transfer: Record<string, number>;
    /** Measured from a completed encoded object; null until one is inspected (continuous: at EOF). */
    encodedMedia: { container: string | null; durationSeconds: number | null; startSeconds: number | null;
        tracks: Omit<AjnProbeTrack, "trackId" | "language" | "title">[] } | null;
    measurements: { processingMediaSecondsPerWallSecond: number | null; speedMeasurementWallSeconds: number | null;
        effectiveBitrateKbps: number | null; bitrateMeasurementMediaSeconds: number | null; bitrateBasis: string };
    error: { code: string; message: string } | null;
}
interface AjnOutputFormats {
    encoders: string[]; requires: string; videoCodecs: string[]; containers: string[]; audioCodecs: string[]; destinations: string[];
    mpegtsVideoCodecs: string[]; mpegtsAudioCodecs: string[];
    minimumVideoKbps: number; maximumVideoKbps: number; minimumAudioKbps: number; maximumAudioKbps: number;
    minimumKeyframeFrames: number; maximumKeyframeFrames: number;
    maximumWallSeconds: number; maximumBytes: number; maximumBytesPerSecond: number; maximumHostBytesPerSecond: number;
    maximumConcurrentSessions: number; softwareSubtitles: boolean;
}
interface AjnNetworkDestination {
    id: string; name: string; origin: string; protocol: "http" | "https" | "udp";
    addresses: string[]; hasCredential: boolean; credentialHeader: string | null;
}
interface AjnHttpOptions {
    method?: "GET" | "HEAD" | "POST" | "PUT" | "PATCH" | "DELETE" | "OPTIONS";
    path?: string; headers?: Record<string, string>;
    /** At most 32 KiB. Strings are encoded as UTF-8. */
    body?: Uint8Array | string;
    useCredential?: boolean;
}
interface AjnNetworkResult {
    state: "pending" | "completed" | "failed";
    status?: number; headers?: Record<string, string>;
    error?: { code: string; message: string };
    /** At most 64 KiB. Empty while pending or failed. */
    body: Uint8Array;
}
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
interface AjnProxyOptions {
    path?: string;
    requestHeaders?: { name: string; values: string[] | null }[];
    responseHeaders?: { name: string; values: string[] | null }[];
    /** Explicitly opt in to incoming sensitive headers and outgoing Set-Cookie. */
    passRequestHeaders?: string[];
    passResponseHeaders?: string[];
    useCredential?: boolean;
    credentialId?: string;
}
interface AjnListenerBinding {
    address: string;
    port: number;
    sensitiveHeaders: string[];
    sensitiveQuery: string[];
    scheme: "http" | "https";
    scope: "loopback" | "lan" | "public";
    certificateId: string | null;
    certificateHost: string | null;
    publicBaseUrl: string | null;
    allowedHosts: string[] | null;
    cors: { origins: string[]; methods: string[]; headers: string[]; exposeHeaders: string[]; allowCredentials: boolean } | null;
}
interface AjnApi {
    /** API 1.8, sceneDetection 1.0. Requires player.sceneDetection and frames.read.
     * One exclusive detector per player; callbacks remain inside the addon sandbox.
     * The host delivers scene.request events containing {detectorId}. */
    sceneDetection: {
        list(): { playerId: string; inUse: boolean; format: "gray8"; stage: "beforeInterpolation" }[];
        attach(playerId: string, options?: { width?: number; height?: number; deadlineMs?: number }): {
            detectorId: string; width: number; height: number; deadlineMs: number; format: "gray8"; stage: "beforeInterpolation";
        };
        /** Latest pending pair once, or null if consumed, expired, reset or unavailable. */
        read(detectorId: string): AjnScenePair | null;
        /** false means late, duplicate or stale. It never applies to a later pair. */
        submit(detectorId: string, requestId: string, decision: "cut" | "continuous" | "default"): { accepted: boolean };
        status(detectorId: string): {
            state: "waitingForPlayer" | "pending" | "active" | "fallback" | "sampleUnavailable" | "backendUnavailable" | "suspended" | "unavailable";
            acceptedPairs: number; timedOutPairs: number;
        };
        detach(detectorId: string): void;
    };
    info(): AjnHostInfo;
    /** API 1.7 development: explicitly approved HTTP(S) listeners. */
    httpServer: {
        selections(): { listeners: { id: string; name: string; binding: AjnListenerBinding }[] };
        formats(): { protocols: string[]; scope: string; maximumInlineBytes: number; maximumBufferedBytes: number; maximumRequests: number; maximumActiveRequests: number; maximumListeners: number; maximumConnectionsPerListener: number; decisionSeconds: number; maximumDecisionSeconds: number; requestBodyReading: boolean; webSockets: boolean; cors: string };
        open(listenerId: string): { serverId: string };
        status(serverId: string): { serverId: string; listenerId: string; state: string; publicBaseUrl: string | null; reachability: "unverified"; certificateExpires: string | null; error: { code: string; message: string } | null };
        requestClose(serverId: string): void;
        requestStatus(requestId: string): { requestId: string; state: "pending" | "claimed"; bodyState: "unread" | "reading" | "ready" | "failed"; bodyLength: number | null; bodyError: string | null; decisionRemainingSeconds: number | null };
        cancelRequest(requestId: string): void;
        /** Extend from now, capped at 120 seconds after arrival. Does not renew an expired request. */
        extend(requestId: string, seconds: number): void;
        readBody(requestId: string): void;
        bodyChunk(requestId: string, offset: number, count?: number): { body: Uint8Array; byteLength: number; totalBytes: number; offset: number; eof: boolean };
        /** Begin claims the request. Append chunks up to 32 KiB, total up to 256 KiB, then finish within the decision deadline. */
        beginResponse(requestId: string, response: { status: number; headers?: { name: string; values: string[] }[] }): void;
        appendResponse(requestId: string, body: Uint8Array | string): void;
        finishResponse(requestId: string): void;
        /** Claims the request once; up to 32 KiB body. Framing and CORS headers are host-owned. */
        respond(requestId: string, response: { status: number; headers?: { name: string; values: string[] }[]; body?: Uint8Array | string }): void;
    };
    log(message: string): void;
    /** API 1.7 development. Requires network.proxy and network.connect; forward also requires network.listen. */
    httpProxy: {
        formats(): { nativeForwarding: boolean; webSockets: boolean; maximumBufferedBytes: number; maximumChunkBytes: number; maximumOperations: number; maximumBufferedOperations: number; headerTimeoutSeconds: number; ioTimeoutSeconds: number; maximumLifetimeSeconds: number; maximumTransferBytes: number; maximumWebSocketMessageBytes: number };
        forward(requestId: string, destinationId: string, options?: AjnProxyOptions): { operationId: string };
        buffer(destinationId: string, options?: AjnProxyOptions & { method?: string; body?: Uint8Array | string; maximumBytes?: number }): { operationId: string };
        status(operationId: string): { operationId: string; state: "pending" | "completed" | "failed"; status: number | null; headers: { name: string; values: string[] }[]; bodyLength: number | null; representationLength: number | null; cleanupReady: boolean; error: { code: string; message: string } | null };
        read(operationId: string, offset: number, count?: number): { body: Uint8Array; byteLength: number; totalBytes: number; offset: number; eof: boolean };
        cancel(operationId: string): void;
        /** Wait for cleanupReady before releasing a completed/cancelled operation. */
        close(operationId: string): void;
    };
    /** API 1.7 development. Capture is not authentication. Validate the client with the upstream before serving protected content. */
    requestCredentials: {
        formats(): { maximumContexts: number; maximumLifetimeSeconds: number; maximumFields: number; maximumBytes: number; captureAuthenticatesClient: false };
        capture(requestId: string, destinationId: string, mappings: { from: "header" | "query"; name: string; to: "header" | "query"; target: string }[], seconds?: number): { credentialId: string };
        status(credentialId: string): { credentialId: string; destinationId: string; expires: string; present: true; authenticated: false; fields: { kind: string; name: string; present: true }[] };
        release(credentialId: string): void;
    };
    /** Host-owned declarative settings. Only the user/host can change them. */
    settings: { get(): Record<string, boolean | number | string> };
    /** API 1.5, remoteSources capability 1.0 and media.input permission.
     * The trusted reader streams media; bytes never enter the Wasm runtime. */
    remoteSources: { formats(): AjnRemoteFormats };
    /** API 1.7. Probes have their own capacity and do not start an encoder. */
    mediaProbe: {
        formats(): { maximumJobs: number; maximumSeconds: number; maximumResultBytes: number; maximumChunkBytes: number; startsEncoder: false };
        open(source: AjnProbeSource): { probeId: string };
        status(probeId: string): { probeId: string; state: "pending" | "completed" | "failed"; byteLength: number | null; error: { code: string; message: string } | null };
        /** Complete result, assembled through at most eight bounded native reads. Wait for completed status first. */
        result(probeId: string): AjnProbeResult;
        read(probeId: string, offset: number, count?: number): { body: Uint8Array; byteLength: number; totalBytes: number; offset: number; eof: boolean };
        cancel(probeId: string): void;
        /** Cancel if active, then wait for terminal status before closing. */
        close(probeId: string): void;
    };
    /** Requires media.input and sessions.manage; remote sources also require network.connect.
     * Source consent and request credential scope are checked independently. */
    subtitles: {
        formats(): { burn: boolean; extract: string[]; textCodecs: string[]; bitmapExtraction: boolean; preservesAssLayout: boolean;
            timestampTimeline: "source"; maximumJobs: number; maximumHostJobs: number; maximumResultBytes: number; maximumChunkBytes: number; maximumSeconds: number };
        /** Omit trackId only for a standalone approved subtitle file. Text conversion loses ASS positioning/fonts/drawing. */
        open(source: AjnProbeSource, options?: { trackId?: string; startSeconds?: number; endSeconds?: number; allowStylingLoss?: boolean }): { subtitleId: string };
        status(subtitleId: string): { subtitleId: string; state: "pending" | "completed" | "failed"; byteLength: number | null;
            contentType: string; timestampTimeline: "source"; error: { code: string; message: string } | null };
        read(subtitleId: string, offset: number, count?: number): { body: Uint8Array; byteLength: number; offset: number; totalBytes: number; eof: boolean };
        /** Authorize each client request first; the host serves WebVTT bytes directly. */
        serve(requestId: string, subtitleId: string): void;
        cancel(subtitleId: string): void;
        /** Cancel and wait for pending work/responses first. Retry subtitles_active until cleanup completes. */
        close(subtitleId: string): void;
    };
    mediaStreams: {
        formats(): Record<string, unknown>;
        /** API 1.9 / mediaStreams 1.1. Readiness handles produce no media; inspect status and close them. */
        check(source: AjnProbeSource, profileId: string | null, options: AjnStreamOptions): AjnStreamHandle;
        /** Same handle lifecycle. Builds missing/incompatible engines for the selected profile and source dimensions. */
        prepare(source: AjnProbeSource, profileId: string | null, options: AjnStreamOptions): AjnStreamHandle;
        open(source: AjnProbeSource, profileId: string | null, options: AjnStreamOptions): AjnStreamHandle;
        status(streamId: string): AjnStreamStatus;
        segments(streamId: string, cursor?: string | null, limit?: number): AjnStreamPage;
        /** Absolute source playback position, within the produced timeline. Refresh while paused to retain ownership. */
        setDemand(streamId: string, seconds: number): void;
        pause(streamId: string, paused: boolean): void;
        /** Authorize the client before each call. Handles confer no client authentication. */
        serve(requestId: string, streamId: string, generationId: string, resourceId: string): void;
        requestClose(streamId: string): void;
    };
    outputs: {
        open(sourceId: string, profileId: string | null, options: AjnOutputOptions & { destination: { type: "servedStream" } }): AjnStreamHandle;
        openRemote(source: AjnRemoteSource, profileId: string | null, options: AjnOutputOptions & { destination: { type: "servedStream" } }): AjnStreamHandle;
        formats(): AjnOutputFormats;
        open(sourceId: string, profileId: string | null, options: AjnOutputOptions): { sessionId: string };
        /** Also requires media.input and remoteSources capability 1.0.
         * Input and receiver must each be independently approved. */
        openRemote(source: AjnRemoteSource, profileId: string | null, options: AjnOutputOptions): { sessionId: string };
    };
    outputPlayback: { formats(): { startOffsets: boolean; audioTrackSelection: boolean; audioChannels: number[]; subtitleModes: string[];
        externalSubtitleSources: string[]; maximumExternalSubtitleBytes: number; seekStrategy: "closeAndReopen"; maximumStartSeconds: number } };
    /** Requires network.connect plus destination consent. Saved credentials
     * also require credentials.use. No redirects, cookies or OS credentials. */
    network: {
        selections(): { destinations: AjnNetworkDestination[] };
        request(destinationId: string, options?: AjnHttpOptions): { requestId: string };
        /** A completed/failed result is consumed exactly once and frees its slot. */
        result(requestId: string): AjnNetworkResult;
        /** Poll the terminal result to release the cancelled request's slot. */
        cancel(requestId: string): void;
        /** At most 16 KiB; success means sent, not acknowledged by the device. */
        sendDatagram(destinationId: string, bytes: Uint8Array | string): { bytesSent: number };
    };
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
    /** API 1.6, playerFrames capability 1.0. Requires player.observe and
     * frames.read separately from owned-session permissions. No player controls
     * or filenames are exposed. Samples precede final display composition. */
    playerFrames: {
        list(): { playerId: string; stage: "processed"; format: "bgra8" }[];
        subscribe(playerId: string, options?: AjnSampleOptions): Required<AjnSampleOptions> & { subscriptionId: string };
        /** Independent latest unread sample, or null. Slow readers skip frames. */
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
        /** API 1.5: media.input, sessions.manage, network.connect and an approved
         * HTTP service/profile. credentials.use is required when selected.
         * Check status.input.seekable before seeking; some streams are forward-only. */
        openRemote(source: AjnRemoteSource, profileId?: string | null): { sessionId: string };
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
