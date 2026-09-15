using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed class Broker : IAsyncDisposable
{
    private readonly AddonPackage package;
    private readonly PermissionGrant grant;
    private readonly AddonStorage storage;
    private readonly AddonSettings settings;
    private readonly SessionRegistry? sessions;
    private readonly Action<string> log;
    private readonly SessionRegistry.Owner? owner;
    private readonly NetworkAccess network;
    private readonly PlayerFrameRegistry? playerFrames;
    private readonly PlayerFrameRegistry.Owner? observer;
    private volatile bool disposed;
    internal AddonTimers Timers { get; } = new();
    internal HttpServerAccess HttpServers { get; }
    internal HttpProxyAccess HttpProxy { get; }
    internal RequestCredentials RequestCredentials { get; }
    internal MediaProbeAccess? MediaProbes { get; }
    internal MediaStreamsAccess? MediaStreams { get; }
    internal SubtitlesAccess? Subtitles { get; }

    public Broker(AddonPackage package, PermissionGrant grant, string dataRoot, Action<string>? log = null, SessionRegistry? sessions = null, NetworkSelections? networkSelections = null, PlayerFrameRegistry? playerFrames = null)
    {
        Contract.Require(package.Hash == grant.PackageHash, "invalid_grant", "Permissions belong to a different package.");
        this.package = package;
        this.grant = grant;
        this.log = log ?? (_ => { });
        this.sessions = sessions;
        this.playerFrames = playerFrames;
        network = new(package, grant, networkSelections ?? new(dataRoot));
        HttpServers = new(package, grant, new ListenerSelections(dataRoot));
        RequestCredentials = new(package, grant, networkSelections ?? new(dataRoot), HttpServers);
        owner = sessions?.CreateOwner(package, grant, RequestCredentials);
        if (owner is not null && sessions!.ProbeProvider(owner) is { } probeProvider) MediaProbes = new(grant, probeProvider);
        if (owner is not null && sessions!.StreamProvider(owner) is { } streamProvider) MediaStreams = new(grant, streamProvider, dataRoot, HttpServers, RequestCredentials);
        if (owner is not null && sessions!.SubtitleProvider(owner) is { } subtitleProvider) Subtitles = new(grant, subtitleProvider, HttpServers, RequestCredentials);
        HttpProxy = new(package, grant, networkSelections ?? new(dataRoot), HttpServers, RequestCredentials);
        foreach (var (name, requirement) in package.Manifest.RequiredCapabilities ?? [])
            Contract.Require(AvailableCapabilities().Contains(name) && requirement.Major == 1 && requirement.MinMinor <= CapabilityMinor(name),
                "missing_capability", $"Required capability is unavailable: {name}.");
        storage = new(dataRoot, package.Manifest.Id);
        settings = new(dataRoot, package.Manifest);
        _ = settings.Get();
        observer = playerFrames?.CreateOwner();
    }

    private string[] AvailableCapabilities() => ["host", "storage", "logging", "settings", "timers", "network", "httpServer", "httpProxy", "requestCredentials",
        .. OperatingSystem.IsWindows() ? new[] { "credentials" } : [],
        .. sessions is null ? [] : sessions.SupportsFrames(owner!) ? new[] { "sessions", "frames" } : ["sessions"],
        .. sessions?.SupportsOutputs(owner!) == true ? new[] { "outputs" } : [],
        .. sessions?.SupportsOutputPlayback(owner!) == true ? new[] { "outputPlayback" } : [],
        .. sessions?.SupportsRemoteSources(owner!) == true ? new[] { "remoteSources" } : [],
        .. MediaProbes is null ? [] : new[] { "mediaProbe" },
        .. MediaStreams is null ? [] : new[] { "mediaStreams" },
        .. Subtitles is null ? [] : new[] { "subtitles" },
        .. playerFrames is not null ? new[] { "playerFrames" } : []];
    private int CapabilityMinor(string name) => name == "sessions" && sessions is not null ? sessions.CapabilityMinor(owner!) : 0;

    public JsonObject Info() => new()
    {
        ["id"] = package.Manifest.Id,
        ["api"] = new JsonObject { ["major"] = Contract.Major, ["minor"] = Contract.Minor },
        ["permissions"] = new JsonArray(grant.Allowed.Order(StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        ["features"] = new JsonArray(AvailableCapabilities().Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
        ["capabilities"] = new JsonObject(AvailableCapabilities().Select(p => new KeyValuePair<string, JsonNode?>(p,
            new JsonObject { ["major"] = 1, ["minor"] = CapabilityMinor(p) }))),
    };

    public async Task<BrokerResponse> InvokeTransportAsync(string method, JsonObject parameters, CancellationToken token)
    {
        if (method == "subtitles.read")
        {
            token.ThrowIfCancellationRequested(); Contract.Require(!disposed, "owner_closed", "Addon instance has stopped.");
            long offset = Contract.Number(parameters, "offset"), count = Contract.Number(parameters, "count");
            Contract.Require(offset is >= 0 and <= 16777216 && count is >= 1 and <= 32768, "invalid_request", "Invalid subtitle chunk offset or size.");
            return TextSubtitles().Read(Contract.Text(parameters, "subtitleId", 64), (int)offset, (int)count);
        }
        if (method == "mediaProbe.result")
        {
            token.ThrowIfCancellationRequested(); Contract.Require(!disposed, "owner_closed", "Addon instance has stopped.");
            long offset = Contract.Number(parameters, "offset"), count = Contract.Number(parameters, "count");
            Contract.Require(offset is >= 0 and <= 262144 && count is >= 1 and <= 32768, "invalid_request", "Invalid probe chunk offset or size.");
            return Probes().Result(Contract.Text(parameters, "probeId", 64), (int)offset, (int)count);
        }
        if (method == "httpProxy.read")
        {
            token.ThrowIfCancellationRequested();
            Contract.Require(!disposed, "owner_closed", "Addon instance has stopped.");
            long offset = Contract.Number(parameters, "offset"), count = Contract.Number(parameters, "count");
            Contract.Require(offset is >= 0 and <= HttpProxyAccess.MaximumBufferBytes && count is >= 1 and <= HttpServerAccess.MaximumInlineBytes,
                "invalid_request", "Invalid buffer chunk offset or size.");
            return HttpProxy.Read(Contract.Text(parameters, "operationId", 64), (int)offset, (int)count);
        }
        if (method == "httpServer.bodyChunk")
        {
            token.ThrowIfCancellationRequested();
            Contract.Require(!disposed, "owner_closed", "Addon instance has stopped.");
            long offset = Contract.Number(parameters, "offset"), count = Contract.Number(parameters, "count");
            Contract.Require(offset is >= 0 and <= HttpServerAccess.MaximumBufferedBytes && count is >= 1 and <= HttpServerAccess.MaximumInlineBytes,
                "invalid_request", "Invalid body chunk offset or size.");
            return HttpServers.BodyChunk(Contract.Text(parameters, "requestId", 64), (int)offset, (int)count);
        }
        if (method == "playerFrames.read")
        {
            token.ThrowIfCancellationRequested();
            Contract.Require(!disposed, "owner_closed", "Addon instance has stopped.");
            var snapshot = Observations().Read(observer!, Contract.Text(parameters, "subscriptionId", 64));
            return new(new JsonObject { ["frame"] = snapshot?.Metadata, ["byteLength"] = snapshot?.Pixels.Length ?? 0 }, snapshot?.Pixels ?? default);
        }
        if (method == "network.result")
        {
            token.ThrowIfCancellationRequested();
            Contract.Require(!disposed, "owner_closed", "Addon instance has stopped.");
            return network.Result(Contract.Text(parameters, "requestId", 64));
        }
        if (method != "frames.read") return new(await InvokeAsync(method, parameters, token));
        token.ThrowIfCancellationRequested();
        Contract.Require(!disposed, "owner_closed", "Addon instance has stopped.");
        grant.Demand("frames.read"); grant.Demand("sessions.manage");
        var frame = await Sessions().ReadFrameAsync(owner!, Contract.Text(parameters, "subscriptionId", 64), false, token);
        return new(new JsonObject { ["frame"] = frame?.Metadata, ["byteLength"] = frame?.Pixels.Length ?? 0 }, frame?.Pixels ?? default);
    }

    public async Task<JsonNode?> InvokeAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Contract.Require(!disposed, "owner_closed", "Addon instance has stopped.");
        switch (method)
        {
            case "host.info": return Info();
            case "subtitles.formats": return TextSubtitles().Formats();
            case "subtitles.open":
                Contract.Require(parameters["source"] is JsonObject && parameters["options"] is JsonObject, "invalid_subtitle", "Expected source and subtitle options.");
                return new JsonObject { ["subtitleId"] = TextSubtitles().Open(ProbeRequest.Parse(parameters["source"]!.AsObject()), SubtitleRequest.Parse(parameters["options"]!.AsObject())) };
            case "subtitles.status": return TextSubtitles().Status(Contract.Text(parameters, "subtitleId", 64));
            case "subtitles.cancel": TextSubtitles().Cancel(Contract.Text(parameters, "subtitleId", 64)); return null;
            case "subtitles.close": TextSubtitles().Close(Contract.Text(parameters, "subtitleId", 64)); return null;
            case "subtitles.serve": TextSubtitles().Serve(Contract.Text(parameters, "requestId", 64), Contract.Text(parameters, "subtitleId", 64)); return null;
            case "subtitles.read": throw new AddonException("binary_transport_required", "Subtitle chunks require the binary response transport.");
            case "mediaStreams.formats": return Streams().Formats();
            case "mediaStreams.open":
                Contract.Require(parameters["source"] is JsonObject && parameters["options"] is JsonObject, "invalid_stream", "Expected source and stream options.");
                return Streams().Open(ProbeRequest.Parse(parameters["source"]!.AsObject()),
                    parameters["profileId"] is null ? null : Contract.Text(parameters, "profileId", 128), StreamRequest.Parse(parameters["options"]!.AsObject()));
            case "mediaStreams.status": return Streams().Status(Contract.Text(parameters, "streamId", 64));
            case "mediaStreams.segments":
                long pageLimit = parameters["limit"] is null ? 32 : Contract.Number(parameters, "limit");
                Contract.Require(pageLimit is >= 1 and <= 32, "invalid_request", "Use a page size of 1 to 32 segments.");
                return Streams().Segments(Contract.Text(parameters, "streamId", 64), parameters["cursor"] is null ? null : Contract.Text(parameters, "cursor", 128), (int)pageLimit);
            case "mediaStreams.setDemand":
                Contract.Require(parameters["seconds"] is JsonValue position && position.TryGetValue<double>(out _), "invalid_position", "Expected a playback position.");
                Streams().SetDemand(Contract.Text(parameters, "streamId", 64), parameters["seconds"]!.GetValue<double>()); return null;
            case "mediaStreams.pause":
                Contract.Require(parameters["paused"] is JsonValue streamPaused && streamPaused.TryGetValue<bool>(out _), "invalid_request", "Expected a pause state.");
                Streams().Pause(Contract.Text(parameters, "streamId", 64), parameters["paused"]!.GetValue<bool>()); return null;
            case "mediaStreams.serve":
                Streams().Serve(Contract.Text(parameters, "requestId", 64), Contract.Text(parameters, "streamId", 64),
                    Contract.Text(parameters, "generationId", 64), Contract.Text(parameters, "resourceId", 64)); return null;
            case "mediaStreams.requestClose": Streams().RequestClose(Contract.Text(parameters, "streamId", 64)); return null;
            case "mediaProbe.formats": return Probes().Formats();
            case "mediaProbe.open":
                Contract.Require(parameters["source"] is JsonObject, "invalid_source", "Expected an approved source descriptor.");
                return new JsonObject { ["probeId"] = Probes().Open(ProbeRequest.Parse(parameters["source"]!.AsObject())) };
            case "mediaProbe.status": return Probes().Status(Contract.Text(parameters, "probeId", 64));
            case "mediaProbe.cancel": Probes().Cancel(Contract.Text(parameters, "probeId", 64)); return null;
            case "mediaProbe.close": Probes().Close(Contract.Text(parameters, "probeId", 64)); return null;
            case "mediaProbe.result": throw new AddonException("binary_transport_required", "Probe results require the binary chunk transport.");
            case "requestCredentials.formats": return RequestCredentials.Formats();
            case "requestCredentials.capture":
                Contract.Require(parameters["mappings"] is JsonArray, "invalid_credential", "Expected credential mappings.");
                long credentialSeconds = parameters["seconds"] is null ? 86400L : Contract.Number(parameters, "seconds");
                Contract.Require(credentialSeconds is >= 1 and <= 86400, "invalid_credential", "Credential lifetime must be 1–86400 seconds.");
                return new JsonObject { ["credentialId"] = RequestCredentials.Capture(Contract.Text(parameters, "requestId", 64), Contract.Text(parameters, "destinationId", 64), (JsonArray)parameters["mappings"]!, (int)credentialSeconds) };
            case "requestCredentials.status": return RequestCredentials.Status(Contract.Text(parameters, "credentialId", 64));
            case "requestCredentials.release": RequestCredentials.Release(Contract.Text(parameters, "credentialId", 64)); return null;
            case "httpProxy.formats": return HttpProxy.Formats();
            case "httpProxy.forward":
                Contract.Require(parameters["options"] is JsonObject, "invalid_request", "Expected proxy options.");
                return new JsonObject { ["operationId"] = HttpProxy.Forward(Contract.Text(parameters, "requestId", 64), Contract.Text(parameters, "destinationId", 64), (JsonObject)parameters["options"]!) };
            case "httpProxy.buffer":
                Contract.Require(parameters["options"] is JsonObject, "invalid_request", "Expected buffer options.");
                return new JsonObject { ["operationId"] = HttpProxy.Buffer(Contract.Text(parameters, "destinationId", 64), (JsonObject)parameters["options"]!) };
            case "httpProxy.status": return HttpProxy.Status(Contract.Text(parameters, "operationId", 64));
            case "httpProxy.cancel": HttpProxy.Cancel(Contract.Text(parameters, "operationId", 64)); return null;
            case "httpProxy.close": HttpProxy.Close(Contract.Text(parameters, "operationId", 64)); return null;
            case "httpProxy.read": throw new AddonException("binary_transport_required", "Buffer chunks require the binary response transport.");
            case "httpServer.selections": return HttpServers.List();
            case "httpServer.formats": return HttpServers.Formats();
            case "httpServer.open": return new JsonObject { ["serverId"] = HttpServers.Open(Contract.Text(parameters, "listenerId", 64)) };
            case "httpServer.status": return HttpServers.Status(Contract.Text(parameters, "serverId", 64));
            case "httpServer.requestClose": HttpServers.RequestClose(Contract.Text(parameters, "serverId", 64)); return null;
            case "httpServer.requestStatus": return HttpServers.RequestStatus(Contract.Text(parameters, "requestId", 64));
            case "httpServer.cancelRequest": HttpServers.CancelRequest(Contract.Text(parameters, "requestId", 64)); return null;
            case "httpServer.extend":
                long extension = Contract.Number(parameters, "seconds");
                Contract.Require(extension is >= 1 and <= 120, "invalid_request", "Use an extension of 1 to 120 seconds.");
                HttpServers.Extend(Contract.Text(parameters, "requestId", 64), (int)extension); return null;
            case "httpServer.readBody": HttpServers.ReadBody(Contract.Text(parameters, "requestId", 64)); return null;
            case "httpServer.bodyChunk": throw new AddonException("binary_transport_required", "Body chunks require the binary response transport.");
            case "httpServer.finishResponse": HttpServers.FinishResponse(Contract.Text(parameters, "requestId", 64)); return null;
            case "httpServer.appendResponse":
                byte[] chunk;
                try { chunk = Convert.FromBase64String(Contract.Text(parameters, "bodyBase64", 44 * 1024)); }
                catch (FormatException) { throw new AddonException("invalid_response", "Expected base64 response bytes."); }
                HttpServers.AppendResponse(Contract.Text(parameters, "requestId", 64), chunk); return null;
            case "httpServer.beginResponse":
            case "httpServer.respond":
                long responseStatus = Contract.Number(parameters, "status");
                Contract.Require(responseStatus is >= 200 and <= 599 && parameters["headers"] is JsonArray { Count: <= 64 }, "invalid_response", "Invalid response status or headers.");
                var responseHeaders = new List<KeyValuePair<string, string[]>>();
                foreach (var node in (JsonArray)parameters["headers"]!)
                {
                    Contract.Require(node is JsonObject h && h["values"] is JsonArray { Count: > 0 and <= 32 }, "invalid_response", "Expected header name and values.");
                    var header = (JsonObject)node!;
                    var values = ((JsonArray)header["values"]!).Select(v =>
                    {
                        Contract.Require(v is JsonValue j && j.TryGetValue<string>(out _), "invalid_response", "Expected a header string.");
                        return v!.GetValue<string>();
                    }).ToArray();
                    responseHeaders.Add(new(Contract.Text(header, "name", 128), values));
                }
                if (method == "httpServer.beginResponse")
                {
                    HttpServers.BeginResponse(Contract.Text(parameters, "requestId", 64), (int)responseStatus, responseHeaders.ToArray()); return null;
                }
                byte[] responseBody;
                try { responseBody = Convert.FromBase64String(Contract.Text(parameters, "bodyBase64", 44 * 1024)); }
                catch (FormatException) { throw new AddonException("invalid_response", "Expected base64 response bytes."); }
                HttpServers.Respond(Contract.Text(parameters, "requestId", 64), (int)responseStatus, responseHeaders.ToArray(), responseBody);
                return null;
            case "playerFrames.list": return Observations().List(observer!);
            case "playerFrames.subscribe":
                var observed = Observations();
                Contract.Require(Contract.Text(parameters, "stage", 32) == "processed" && Contract.Text(parameters, "format", 16) == "bgra8",
                    "frame_format_unavailable", "Player observations offer processed BGRA8 samples.");
                long ow = Contract.Number(parameters, "width"), oh = Contract.Number(parameters, "height"), ofps = Contract.Number(parameters, "maxFps");
                Contract.Require(ow is >= 1 and <= 320 && oh is >= 1 and <= 180 && ofps is >= 1 and <= 60, "invalid_request", "Unsupported sample dimensions or rate.");
                return observed.Subscribe(observer!, Contract.Text(parameters, "playerId", 64), new((int)ow, (int)oh, (int)ofps));
            case "playerFrames.unsubscribe":
                Observations().Unsubscribe(observer!, Contract.Text(parameters, "subscriptionId", 64)); return null;
            case "playerFrames.read": throw new AddonException("binary_transport_required", "Player samples require the binary response transport.");
            case "settings.get": return settings.Get();
            case "remoteSources.formats":
                grant.Demand("media.input"); grant.Demand("sessions.manage");
                return Sessions().RemoteSourceFormats(owner!);
            case "sessions.openRemote":
            case "outputs.openRemote":
                if (method == "outputs.openRemote" && parameters["destination"] is JsonObject remoteDestination && remoteDestination["type"]?.GetValue<string>() == "servedStream")
                {
                    Contract.Require(parameters["source"] is JsonObject, "invalid_source", "Expected a remote source.");
                    return Streams().Open(new(null, RemoteInputRequest.Parse(parameters["source"]!.AsObject())),
                        parameters["profileId"] is null ? null : Contract.Text(parameters, "profileId", 128), StreamRequest.ParseOutput(parameters));
                }
                grant.Demand("media.input"); grant.Demand("sessions.manage"); grant.Demand("network.connect");
                if (method == "outputs.openRemote") grant.Demand("media.output");
                Contract.Require(parameters["source"] is JsonObject, "invalid_request", "Expected a remote source.");
                return new JsonObject { ["sessionId"] = await Sessions().OpenRemoteAsync(owner!,
                    RemoteInputRequest.Parse((JsonObject)parameters["source"]!),
                    parameters["profileId"] is null ? null : Contract.Text(parameters, "profileId", 128),
                    method == "outputs.openRemote" ? OutputRequest.Parse(parameters) : null, cancellationToken) };
            case "outputs.formats":
                grant.Demand("media.output"); grant.Demand("sessions.manage");
                return Sessions().OutputFormats(owner!);
            case "outputPlayback.formats":
                grant.Demand("media.output"); grant.Demand("sessions.manage");
                Contract.Require(Sessions().SupportsOutputPlayback(owner!), "feature_unavailable", "Output playback controls are unavailable.");
                return new JsonObject { ["startOffsets"] = true, ["audioTrackSelection"] = true, ["audioChannels"] = new JsonArray(2),
                    ["externalSubtitleSources"] = Subtitles is null ? new JsonArray() : new JsonArray("approvedLocal", "approvedHttp"), ["maximumExternalSubtitleBytes"] = 16777216,
                    ["subtitleModes"] = Subtitles is null ? new JsonArray("none") : new JsonArray("none", "burn"), ["seekStrategy"] = "closeAndReopen", ["maximumStartSeconds"] = 315576000 };
            case "outputs.open":
                if (parameters["destination"] is JsonObject localDestination && localDestination["type"]?.GetValue<string>() == "servedStream")
                    return Streams().Open(new(Contract.Text(parameters, "sourceId", 128), null),
                        parameters["profileId"] is null ? null : Contract.Text(parameters, "profileId", 128), StreamRequest.ParseOutput(parameters));
                grant.Demand("media.output"); grant.Demand("sessions.manage"); grant.Demand("network.connect");
                return new JsonObject { ["sessionId"] = await Sessions().OpenOutputAsync(owner!,
                    Contract.Text(parameters, "sourceId", 128), parameters["profileId"] is null ? null : Contract.Text(parameters, "profileId", 128),
                    OutputRequest.Parse(parameters), cancellationToken) };
            case "network.selections": return network.List();
            case "network.request": return new JsonObject { ["requestId"] = network.Request(parameters) };
            case "network.result": throw new AddonException("binary_transport_required", "Network results require the binary response transport.");
            case "network.cancel": network.Cancel(Contract.Text(parameters, "requestId", 64)); return null;
            case "network.sendDatagram": return new JsonObject { ["bytesSent"] = await network.SendDatagramAsync(parameters, cancellationToken) };
            case "timers.set":
                long interval = Contract.Number(parameters, "intervalMs");
                Contract.Require(interval is >= 16 and <= 3_600_000 && parameters["repeat"] is JsonValue repeat && repeat.TryGetValue<bool>(out _), "invalid_request", "Invalid timer interval or repeat value.");
                Timers.Set(Contract.Text(parameters, "timerId", 64), (int)interval, parameters["repeat"]!.GetValue<bool>());
                return null;
            case "timers.clear":
                Timers.Clear(Contract.Text(parameters, "timerId", 64)); return null;
            case "frames.subscribe":
                grant.Demand("frames.read"); grant.Demand("sessions.manage");
                Contract.Require(Contract.Text(parameters, "stage", 32) == "processed" && Contract.Text(parameters, "format", 16) == "bgra8", "frame_format_unavailable", "This producer offers processed BGRA8 samples.");
                long width = Contract.Number(parameters, "width"), height = Contract.Number(parameters, "height"), fps = Contract.Number(parameters, "maxFps");
                Contract.Require(width is >= 1 and <= 320 && height is >= 1 and <= 180 && fps is >= 1 and <= 60, "invalid_request", "Unsupported frame dimensions or rate.");
                var request = new FrameRequest((int)width, (int)height, (int)fps);
                string subscriptionId = await Sessions().SubscribeFramesAsync(owner!, Contract.Text(parameters, "sessionId", 64), request, cancellationToken);
                var description = request.Describe(); description["subscriptionId"] = subscriptionId;
                return description;
            case "frames.unsubscribe":
                grant.Demand("frames.read"); grant.Demand("sessions.manage");
                await Sessions().ReadFrameAsync(owner!, Contract.Text(parameters, "subscriptionId", 64), true, cancellationToken);
                return null;
            case "frames.read": throw new AddonException("binary_transport_required", "Frame reads require the binary response transport.");
            case "log.write":
                grant.Demand("log.write");
                string message = Contract.Text(parameters, "message", 4096);
                log(new string(message.Where(c => !char.IsControl(c) || c == '\t').ToArray()));
                return null;
            case "storage.get":
                grant.Demand("storage.read");
                return storage.Get(Contract.Text(parameters, "key", 64));
            case "storage.set":
                grant.Demand("storage.write");
                Contract.Require(parameters.ContainsKey("value"), "invalid_request", "Missing storage value.");
                storage.Set(Contract.Text(parameters, "key", 64), parameters["value"]);
                return null;
            case "sessions.open":
                grant.Demand("sessions.manage");
                var registry = Sessions();
                string source = Contract.Text(parameters, "sourceId", 128);
                string? profile = parameters["profileId"] is null ? null : Contract.Text(parameters, "profileId", 128);
                return new JsonObject { ["sessionId"] = await registry.OpenAsync(owner!, source, profile, cancellationToken) };
            case "sessions.selections":
                grant.Demand("sessions.manage");
                return Sessions().Selections(owner!);
            case "sessions.pause":
                grant.Demand("sessions.manage");
                Contract.Require(parameters["paused"] is JsonValue paused && paused.TryGetValue<bool>(out _), "invalid_request", "Expected a pause state.");
                if (MediaStreams?.FindSession(Contract.Text(parameters, "sessionId", 64)) is string pausedStream)
                { MediaStreams.Pause(pausedStream, parameters["paused"]!.GetValue<bool>()); return null; }
                await Sessions().ControlAsync(owner!, Contract.Text(parameters, "sessionId", 64), parameters["paused"]!.GetValue<bool>(), null, cancellationToken);
                return null;
            case "sessions.seek":
                grant.Demand("sessions.manage");
                Contract.Require(MediaStreams?.FindSession(Contract.Text(parameters, "sessionId", 64)) is null, "operation_unavailable", "Close this encoded stream and reopen at the desired offset.");
                Contract.Require(parameters["seconds"] is JsonValue seconds && seconds.TryGetValue<double>(out _), "invalid_request", "Expected a seek position.");
                await Sessions().ControlAsync(owner!, Contract.Text(parameters, "sessionId", 64), null, parameters["seconds"]!.GetValue<double>(), cancellationToken);
                return null;
            case "sessions.status":
                grant.Demand("sessions.manage");
                if (MediaStreams?.FindSession(Contract.Text(parameters, "sessionId", 64)) is string statusStream) return MediaStreams.Status(statusStream);
                return await Sessions().StatusAsync(owner!, Contract.Text(parameters, "sessionId", 64), cancellationToken);
            case "sessions.close":
                grant.Demand("sessions.manage");
                if (MediaStreams?.FindSession(Contract.Text(parameters, "sessionId", 64)) is string closeStream)
                { await MediaStreams.CloseAsync(closeStream, cancellationToken); return null; }
                await Sessions().CloseAsync(owner!, Contract.Text(parameters, "sessionId", 64), cancellationToken);
                return null;
            case "sessions.requestClose":
                grant.Demand("sessions.manage");
                if (MediaStreams?.FindSession(Contract.Text(parameters, "sessionId", 64)) is string requestCloseStream)
                { MediaStreams.RequestClose(requestCloseStream); return null; }
                var closingRegistry = Sessions();
                string closingId = Contract.Text(parameters, "sessionId", 64);
                Contract.Require(closingRegistry.CapabilityMinor(owner!) >= 1, "feature_unavailable", "Asynchronous session close is unavailable.");
                closingRegistry.RequestClose(owner!, closingId);
                return null;
            default: throw new AddonException("unknown_method", "Method is not available in this API.");
        }
    }

    private SessionRegistry Sessions() => sessions ?? throw new AddonException("feature_unavailable", "No native processing provider is connected.");
    private MediaProbeAccess Probes() => MediaProbes ?? throw new AddonException("feature_unavailable", "The matching native probe runtime is not installed.");
    private MediaStreamsAccess Streams() => MediaStreams ?? throw new AddonException("feature_unavailable", "The matching native stream runtime is not installed.");
    private SubtitlesAccess TextSubtitles() => Subtitles ?? throw new AddonException("feature_unavailable", "The matching native subtitle runtime is not installed.");
    private PlayerFrameRegistry Observations()
    {
        grant.Demand("player.observe"); grant.Demand("frames.read");
        return playerFrames ?? throw new AddonException("feature_unavailable", "This host does not include player observation.");
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        Timers.Close();
        RequestCredentials.Dispose();
        if (observer is not null) playerFrames!.ReleaseOwner(observer);
        await Task.WhenAll(HttpServers.DisposeAsync().AsTask(), HttpProxy.DisposeAsync().AsTask(), network.DisposeAsync().AsTask(),
            MediaProbes?.DisposeAsync().AsTask() ?? Task.CompletedTask, MediaStreams?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            Subtitles?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            sessions is null ? Task.CompletedTask : sessions.ReleaseOwnerAsync(owner!));
    }
}
