using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;
using AnimeJaNai.Addons.TestSupport;

internal static class NativeStreamRuntimeChecks
{
    internal static async Task RunAsync(string root, string output, WorkerCommand command, string runtime, string compiler, List<JsonObject> evidence, string? episodeFfmpeg = null)
    {
        string mediaPath = await NativeEncodingChecks.FixtureAsync(root, output);
        if (episodeFfmpeg is not null)
        {
            string episode = Path.Combine(output, "episode-22-minutes.mkv");
            await NativeStreamTimingChecks.Ffmpeg(episodeFfmpeg, output, "episode-fixture", ["-stream_loop", "164", "-i", mediaPath, "-t", "1320", "-c", "copy", episode]);
            mediaPath = episode;
        }
        byte[] mediaBytes = await File.ReadAllBytesAsync(mediaPath);
        var requests = new ConcurrentQueue<HttpInput>();
        await using var upstream = new HttpFixture((request, _) =>
        {
            requests.Enqueue(request);
            Require(request.Headers.GetValueOrDefault("X-Source-Test") == "stream-source-fixture", "Wrong source credential.");
            int start = int.Parse(request.Headers["Range"][6..^1], CultureInfo.InvariantCulture);
            return Task.FromResult(new HttpReply(206, mediaBytes[start..], $"Content-Range: bytes {start}-{mediaBytes.Length - 1}/{mediaBytes.Length}\r\nETag: \"stream-fixture-1\"\r\n"));
        });
        string area = Path.Combine(output, "managed"); Directory.CreateDirectory(area);
        var package = await DeveloperTools.BuildAsync(Path.Combine(AppContext.BaseDirectory, "media-stream"), compiler, Path.Combine(output, "media-stream.ajnaddon"));
        var grant = new PermissionGrant(package, package.Manifest.Permissions); new AddonRegistry(area).Install(package, grant.Allowed);
        var media = new MediaSelections(area); var network = new NetworkSelections(area); var listeners = new ListenerSelections(area);
        media.ApproveProfile(package, grant, "Test 2x DirectML", 1002, "DirectML", "[global]\nconfig_version=3\nbackend=DirectML\nlogging=yes\n");
        string source = network.Approve(package, grant, "Selected test source", await NetworkDestination.InspectAsync($"http://127.0.0.1:{upstream.Port}", default));
        network.SetCredential(package, grant, source, "X-Source-Test", "stream-source-fixture");
        using var rsa = RSA.Create(2048);
        var csr = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        csr.CertificateExtensions.Add(names.Build());
        csr.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = csr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        string pfx = Path.Combine(output, "test-listener.pfx"); File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pkcs12, "fixture"));
        string certId = listeners.Certificates.Import(package, grant, pfx, "fixture", "Test-only localhost certificate");
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        listeners.Approve(package, grant, "HTTPS fixture", new("127.0.0.1", port, [], [], Scheme: "https", CertificateId: certId, CertificateHost: "localhost", AllowedHosts: ["127.0.0.1", "localhost"]));
        var sessions = new SessionRegistry(new NativeSessionProvider(root, area, media, command, maximumSessions: 1, networkSelections: network), totalLimit: 1, perOwnerLimit: 1);
        await using var service = new AddonService(area, async (p, g, log, token) => await AddonWorker.StartAsync(p, g, runtime,
            Path.Combine(area, "workers"), area, command, log, sessions, cancellationToken: token, networkSelections: network), media, network);
        Task<JsonNode?> Call(string method, JsonObject? parameters = null) => service.InvokeAsync("stream-test", method, parameters ?? new() { ["id"] = package.Manifest.Id }, default);
        Task<JsonNode?> Action(string action) => Call("addons.action", new() { ["id"] = package.Manifest.Id, ["action"] = action });
        Task<JsonNode?> Configure(JsonObject changes) => Call("addons.configure", new() { ["id"] = package.Manifest.Id, ["changes"] = changes });
        async Task<JsonObject> Until(Func<JsonObject, bool> done)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                var status = (JsonObject)(await Action("status"))!;
                Require(status["error"] is null && status["stream"]?["error"] is null && status["listener"]?["state"]?.GetValue<string>() != "failed", status.ToJsonString());
                if (done(status)) return status;
                await Task.Delay(50, timeout.Token);
            }
        }
        await Call("manager.hello", new() { ["major"] = 1L }); await Call("addons.start");
        await Configure(new() { ["remote"] = true, ["useCredential"] = true, ["container"] = "fragmentedMp4", ["startSeconds"] = 1, ["lengthSeconds"] = 2 });
        await Action("open"); await Until(s => s["listener"]?["state"]?.GetValue<string>() == "listening");
        await Action("probe");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
        {
            while (true)
            {
                var probed = await Action("probeStatus");
                Require(probed?["error"] is null, "Public probe failed: " + probed);
                if (probed?["tracks"] is JsonArray tracks)
                { Require(tracks.Any(t => t?["type"]?.GetValue<string>() == "audio"), "Public probe lost audio."); evidence.Add(new() { ["publicProbe"] = probed.DeepClone() }); break; }
                await Task.Delay(50, timeout.Token);
            }
        }
        if (episodeFfmpeg is not null) await Configure(new() { ["startSeconds"] = 0, ["lengthSeconds"] = 1320, ["segmentSeconds"] = 6 });
        await Action("play");
        if (episodeFfmpeg is not null)
        {
            await ReceiveEpisode(output, certificate, port, episodeFfmpeg, evidence);
            await Call("network.revoke", new() { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["destinationId"] = source });
            Require(!service.HasRunningWorkers, "Episode revocation retained its worker.");
            return;
        }
        var ready = await Until(s => s["stream"]?["state"]?.GetValue<string>() == "producerCompleted");
        Require(ready["stream"]?["encodedMedia"]?["tracks"]?.AsArray().Any(t => t?["width"]?.GetValue<int>() == 960) == true, "Encoded stream is not 2x.");
        await service.DisconnectAsync("stream-test"); // Manual activation must survive Manager closing.
        Require(service.HasRunningWorkers, "Manager closure stopped manually activated streaming.");
        // Exact test-certificate pin only in this client. No system trust or host TLS policy changes.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        string origin = $"https://127.0.0.1:{port}";
        var page = JsonNode.Parse(await client.GetStringAsync(origin + "/segments"))!;
        string generation = page["generationId"]!.GetValue<string>();
        byte[] init = await client.GetByteArrayAsync(origin + "/media/" + generation + "/" + page["initializationId"]!.GetValue<string>());
        int frames = 0;
        foreach (var segment in page["segments"]!.AsArray())
        {
            string resource = segment!["resourceId"]!.GetValue<string>();
            byte[] bytes = await client.GetByteArrayAsync(origin + "/media/" + generation + "/" + resource);
            string file = Path.Combine(output, "received-" + segment["sequence"] + ".mp4");
            await File.WriteAllBytesAsync(file, [.. init, .. bytes]);
            var decoded = await NativeStreamingChecks.DecodeAsync(root, output, "received-" + segment["sequence"], file);
            Require(decoded.Video > 0 && decoded.Audio > 0, "HTTPS object is not independently decodable with audio."); frames += decoded.Video;
        }
        Require(frames == 48, "HTTPS delivery lost or duplicated video frames.");
        evidence.Add(new() { ["httpsWasmStream"] = ready, ["segments"] = page, ["decodedFrames"] = frames, ["managerDisconnectSurvived"] = true });
        await Call("manager.hello", new() { ["major"] = 1L });
        await Configure(new() { ["startSeconds"] = 4 }); await Action("replace");
        var replacement = await Until(s => s["stream"]?["generationId"]?.GetValue<string>() is string next && next != generation && s["stream"]?["state"]?.GetValue<string>() == "producerCompleted");
        using var stale = await client.GetAsync(origin + "/media/" + generation + "/" + page["segments"]![0]!["resourceId"]!.GetValue<string>());
        Require(stale.StatusCode == HttpStatusCode.Conflict, "Old-generation request was not rejected.");
        evidence.Add(new() { ["capacityOneReplacement"] = replacement, ["sourceRequests"] = requests.Count });
        await Call("network.revoke", new() { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["destinationId"] = source });
        Require(!service.HasRunningWorkers, "Revocation left the addon active.");
        string cacheRoot = Path.Combine(area, "stream-cache");
        Require(!Directory.Exists(cacheRoot) || !Directory.EnumerateDirectories(cacheRoot).Any(), "Revocation retained stream caches.");
        Require(!Directory.EnumerateDirectories(Path.Combine(area, "media-workers")).Any(), "Revocation retained native workers.");
        Require(!ready.ToJsonString().Contains("stream-source-fixture"), "Status exposed a source credential.");
        Console.WriteLine("PASS packaged Wasm remote probe, HTTPS stream delivery, decoded A/V, Manager disconnect, capacity-one replacement and revocation.");
    }
    private static async Task ReceiveEpisode(string output, X509Certificate2 certificate, int port, string ffmpeg, List<JsonObject> evidence)
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        string origin = $"https://127.0.0.1:{port}";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var timer = System.Diagnostics.Stopwatch.StartNew(); double? startup = null; long bytesReceived = 0, maximumCache = 0;
        string? cursor = null, generation = null; byte[]? init = null; double position = 0; long sequence = 0;
        var files = new List<string>(); JsonNode? final = null;
        while (true)
        {
            var status = JsonNode.Parse(await client.GetStringAsync(origin + "/status", deadline.Token))!;
            var stream = status["stream"]!; Require(stream["error"] is null, "Episode stream failed: " + stream);
            maximumCache = Math.Max(maximumCache, stream["transfer"]?["cachedBytes"]?.GetValue<long>() ?? 0);
            var page = JsonNode.Parse(await client.GetStringAsync(origin + "/segments" + (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)), deadline.Token))!;
            generation ??= page["generationId"]!.GetValue<string>(); Require(generation == page["generationId"]!.GetValue<string>(), "Episode generation changed.");
            if (init is null && page["initializationId"] is JsonValue initId)
                init = await client.GetByteArrayAsync(origin + "/media/" + generation + "/" + initId.GetValue<string>(), deadline.Token);
            foreach (var segment in page["segments"]!.AsArray())
            {
                Require(segment!["sequence"]!.GetValue<long>() == sequence++, "Episode skipped a published segment.");
                byte[] bytes = await client.GetByteArrayAsync(origin + "/media/" + generation + "/" + segment["resourceId"]!.GetValue<string>(), deadline.Token);
                if (init is null) throw new Exception("Episode fragment lacks initialization.");
                string file = Path.Combine(output, "episode-received-" + segment["sequence"] + ".mp4");
                await File.WriteAllBytesAsync(file, [.. init, .. bytes], deadline.Token); files.Add(file);
                bytesReceived += bytes.Length; startup ??= timer.Elapsed.TotalSeconds; position = segment["sourceEndSeconds"]!.GetValue<double>();
            }
            cursor = page["nextCursor"]!.GetValue<string>();
            if (stream["state"]?.GetValue<string>() == "producerCompleted" && page["segments"]!.AsArray().Count == 0) { final = status; break; }
            double produced = stream["native"]?["buffer"]?["producedEndSeconds"]?.GetValue<double>() ?? 0;
            if (position > 0 && produced > 0)
            {
                double demand = Math.Min(position, produced);
                using var response = await client.PostAsync(origin + "/demand?generation=" + generation + "&seconds=" + demand.ToString("R", CultureInfo.InvariantCulture), null, deadline.Token);
                Require(response.IsSuccessStatusCode, "Episode demand update was rejected: " + await response.Content.ReadAsStringAsync(deadline.Token));
            }
            await Task.Delay(50, deadline.Token);
        }
        double elapsed = timer.Elapsed.TotalSeconds;
        Require(elapsed < 1320, "Public remote-to-HTTPS pipeline did not sustain 1x.");
        int frames = 0; double maximumSync = 0;
        foreach (string file in files)
        {
            var decoded = await NativeStreamTimingChecks.Decode(ffmpeg, output, Path.GetFileNameWithoutExtension(file), file);
            var video = decoded.Where(f => f.Type == "video").ToArray(); var audio = decoded.Where(f => f.Type == "audio").ToArray();
            Require(video.Length > 0 && audio.Length > 0, "Episode HTTPS segment lost video/audio.");
            frames += video.Length;
            maximumSync = Math.Max(maximumSync, Math.Max(Math.Abs(video[0].Pts - audio[0].Pts), Math.Abs(video[^1].End - audio[^1].End)));
        }
        Require(frames == 31680 && maximumSync <= .1, "Episode HTTPS content count or A/V synchronization failed.");
        evidence.Add(new() { ["publicWasmHttpsEpisode"] = true, ["mediaSeconds"] = 1320, ["elapsedSeconds"] = elapsed, ["processingAndDeliverySpeed"] = 1320 / elapsed,
            ["startupSeconds"] = startup, ["decodedVideoFrames"] = frames, ["maximumAvOffsetSeconds"] = maximumSync, ["bytesReceived"] = bytesReceived,
            ["averageTransferBytesPerSecond"] = bytesReceived / elapsed, ["maximumObservedCacheBytes"] = maximumCache, ["finalStatus"] = final });
        Console.WriteLine("PASS full 22-minute packaged Wasm remote-to-HTTPS stream, independently decoded video/audio and bounded cache.");
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
