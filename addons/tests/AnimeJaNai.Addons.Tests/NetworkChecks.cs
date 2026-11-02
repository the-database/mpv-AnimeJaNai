using AnimeJaNai.Addons.TestSupport;
using AnimeJaNai.Addons;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static partial class Checks
{
    private static JsonObject HttpRequest(string id, string path = "/", string method = "GET", byte[]? body = null, bool credential = false) => new()
    {
        ["destinationId"] = id, ["path"] = path, ["method"] = method, ["headers"] = new JsonObject(),
        ["bodyBase64"] = Convert.ToBase64String(body ?? []), ["useCredential"] = credential,
    };
    private static async Task<BrokerResponse> NetworkResult(NetworkAccess access, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var result = access.Result(id);
            if (result.Result?["state"]?.GetValue<string>() != "pending") return result;
            await Task.Delay(10, timeout.Token);
        }
    }
    private static NetworkDestination LoopbackDestination(int port, string scheme = "http", string host = "device.invalid") =>
        new($"{scheme}://{host}:{port}", scheme, host, port, ["127.0.0.1"]);

    private static async Task NetworkChecks()
    {
        await Test("Destination inspection normalizes explicit origins and rejects broader address forms", async () =>
        {
            var destination = await NetworkDestination.InspectAsync("http://127.0.0.1:18745", default);
            True(destination.Origin == "http://127.0.0.1:18745" && destination.Addresses.SequenceEqual(["127.0.0.1"]));
            True(NetworkDestination.Parse("https://example.com").Port == 443);
            True(NetworkDestination.Parse("http://[::1]:8080").Origin == "http://[::1]:8080");
            foreach (string invalid in new[] { "http://host/path", "http://user:secret@host", "file:///C:/test", "http://0.0.0.0", "udp://255.255.255.255:9", "udp://224.0.0.1:9", "udp://[ff02::1]:9", "http://*.example.com", "http://host/?x=1", "http://host/#fragment", "udp://host", "http://host\\path" })
                await Error("invalid_destination", () => NetworkDestination.Parse(invalid));
        });
        await Test("Destination grants survive reload and stay bound to the package and addon", async () =>
        {
            string area = Area(); var package = Package(permissions: ["network.connect"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            var selections = new NetworkSelections(area);
            await Error("permission_denied", () => selections.Approve(package, new(package, []), "local", LoopbackDestination(12345)));
            string id = selections.Approve(package, grant, "local", LoopbackDestination(12345));
            True(new NetworkSelections(area).Resolve(package, grant, id).Destination.Addresses[0] == "127.0.0.1");
            var other = Package("org.example.other", permissions: ["network.connect"]);
            await Error("destination_not_granted", () => selections.Resolve(other, new(other, other.Manifest.Permissions), id));
            var changed = Package(version: "1.0.1", permissions: ["network.connect"]);
            await Error("destination_not_granted", () => selections.Resolve(changed, new(changed, changed.Manifest.Permissions), id));
            var snapshot = selections.Resolve(package, grant, id); snapshot.Destination.Addresses[0] = "127.0.0.2";
            True(selections.Resolve(package, grant, id).Destination.Addresses[0] == "127.0.0.1");
            selections.Revoke(package, grant, id);
            await Error("destination_not_granted", () => new NetworkSelections(area).Resolve(package, grant, id));
            string file = Path.Combine(area, "network-approvals", package.Manifest.Id, "approvals.json");
            File.WriteAllText(file, "broken");
            await Error("invalid_approvals", () => new NetworkSelections(area).List(package, grant));
            True(File.ReadAllText(file) == "broken");
        });
        await Test("HTTP uses reviewed IPs and returns exact binary bytes without inherited credentials", async () =>
        {
            byte[] payload = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, payload)));
            var package = Package(permissions: ["network.connect"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            var selections = new NetworkSelections(Area()); string id = selections.Approve(package, grant, "test", LoopbackDestination(server.Port));
            await using var access = new NetworkAccess(package, grant, selections);
            var request = HttpRequest(id, "/one?x=2", "POST", [0, 10, 255]);
            string operation = access.Request(request); var response = await NetworkResult(access, operation);
            True(response.Result?["state"]?.GetValue<string>() == "completed", response.Result!.ToJsonString());
            True(response.Result?["status"]?.GetValue<int>() == 200 && response.Binary.Span.SequenceEqual(payload));
            True(server.Last!.Headers["Host"] == $"device.invalid:{server.Port}" && server.Last.Body.SequenceEqual(new byte[] { 0, 10, 255 }));
            True(!server.Last.Headers.ContainsKey("Authorization") && !server.Last.Headers.ContainsKey("Cookie"));
            await Error("request_not_found", () => access.Result(operation));
            foreach (string path in new[] { "http://other/", "//other/", "/\\other", "/bad\r\n" })
                await Error("invalid_request", () => access.Request(HttpRequest(id, path)));
            foreach (string header in new[] { "Host", "Connection", "Content-Length", "Transfer-Encoding", "Proxy-Authorization" })
            {
                var invalid = HttpRequest(id); invalid["headers"]![header] = "other";
                await Error("invalid_request", () => access.Request(invalid));
            }
        });
        await Test("HTTP redirects remain responses and oversized bodies fail with bounded results", async () =>
        {
            await using var other = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, [])));
            await using var server = new HttpFixture((request, _) => Task.FromResult(request.Path == "/redirect"
                ? new HttpReply(302, [], $"Location: http://127.0.0.1:{other.Port}/\r\n") : new HttpReply(200, new byte[65537], Chunked: true)));
            var package = Package(permissions: ["network.connect"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            var selections = new NetworkSelections(Area()); string id = selections.Approve(package, grant, "test", LoopbackDestination(server.Port));
            await using var access = new NetworkAccess(package, grant, selections);
            var redirect = await NetworkResult(access, access.Request(HttpRequest(id, "/redirect")));
            True(redirect.Result?["status"]?.GetValue<int>() == 302 && other.Requests == 0);
            var oversize = await NetworkResult(access, access.Request(HttpRequest(id, "/large")));
            True(oversize.Result?["error"]?["code"]?.GetValue<string>() == "response_too_large" && oversize.Binary.Length == 0);
        });
        await Test("Pending HTTP requests have ownership, capacity and cancellation without blocking callbacks", async () =>
        {
            await using var server = new HttpFixture(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(200, []); });
            var package = Package(permissions: ["network.connect"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            var selections = new NetworkSelections(Area()); string id = selections.Approve(package, grant, "test", LoopbackDestination(server.Port));
            await using var access = new NetworkAccess(package, grant, selections);
            await using var other = new NetworkAccess(package, grant, selections);
            var timer = Stopwatch.StartNew(); string[] requests = Enumerable.Range(0, 4).Select(_ => access.Request(HttpRequest(id))).ToArray();
            True(timer.Elapsed < TimeSpan.FromSeconds(1));
            True(access.Result(requests[0]).Result?["state"]?.GetValue<string>() == "pending");
            await Error("request_not_found", () => other.Cancel(requests[0]));
            await Error("capacity_exceeded", () => access.Request(HttpRequest(id)));
            access.Cancel(requests[0]);
            var cancelled = await NetworkResult(access, requests[0]);
            True(cancelled.Result?["error"]?["code"]?.GetValue<string>() == "network_cancelled");
            string replacement = access.Request(HttpRequest(id)); access.Cancel(replacement);
            await NetworkResult(access, replacement);
            await access.DisposeAsync();
            await Error("owner_closed", () => access.Request(HttpRequest(id)));
        });
        await Test("UDP transmits exact binary data only to the approved unicast destination", async () =>
        {
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
            var package = Package(permissions: ["network.connect"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            var selections = new NetworkSelections(Area()); string id = selections.Approve(package, grant, "device", LoopbackDestination(port, "udp"));
            await using var access = new NetworkAccess(package, grant, selections);
            byte[] bytes = Enumerable.Range(0, 16384).Select(i => (byte)i).ToArray();
            var send = new JsonObject { ["destinationId"] = id, ["bodyBase64"] = Convert.ToBase64String(bytes) };
            True(await access.SendDatagramAsync(send, default) == bytes.Length);
            var received = await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3)); True(received.Buffer.SequenceEqual(bytes));
            send["bodyBase64"] = Convert.ToBase64String(new byte[16385]);
            await Error("invalid_request", () => access.SendDatagramAsync(send, default));
            send["bodyBase64"] = "AA=="; selections.Revoke(package, grant, id);
            await Error("destination_not_granted", () => access.SendDatagramAsync(send, default));
        });
        if (OperatingSystem.IsWindows()) await Test("Saved HTTP credentials are protected, package-bound and applied only on request", async () =>
        {
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, [1])));
            var package = Package(permissions: ["network.connect", "credentials.use"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            string area = Area(); var selections = new NetworkSelections(area);
            string id = selections.Approve(package, grant, "test", LoopbackDestination(server.Port));
            const string secret = "synthetic-test-value-only";
            selections.SetCredential(package, grant, id, "X-Test-Credential", secret);
            string saved = File.ReadAllText(Path.Combine(area, "network-approvals", package.Manifest.Id, "approvals.json"));
            True(!saved.Contains(secret));
            True(!selections.List(package, grant).ToJsonString().Contains(secret));
            True(new NetworkSelections(area).Credential(package, grant, selections.Resolve(package, grant, id)) == secret);
            string encrypted = selections.Resolve(package, grant, id).ProtectedCredential!;
            await Error("credential_unavailable", () => WindowsSecretProtection.Unprotect(encrypted, "other-context"));
            await using var access = new NetworkAccess(package, grant, selections);
            await NetworkResult(access, access.Request(HttpRequest(id))); True(!server.Last!.Headers.ContainsKey("X-Test-Credential"));
            await NetworkResult(access, access.Request(HttpRequest(id, credential: true))); True(server.Last!.Headers["X-Test-Credential"] == secret);
            await using var denied = new NetworkAccess(package, new(package, ["network.connect"]), selections);
            await Error("permission_denied", () => denied.Request(HttpRequest(id, credential: true)));
            var duplicate = HttpRequest(id, credential: true); duplicate["headers"]!["x-test-credential"] = "other";
            await Error("invalid_request", () => access.Request(duplicate));
            selections.Revoke(package, grant, id, true);
            await Error("credential_not_granted", () => access.Request(HttpRequest(id, credential: true)));
        });
        await Test("HTTPS keeps certificate verification enabled on pinned connections", async () =>
        {
            using var key = RSA.Create(2048);
            var csr = new CertificateRequest("CN=device.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = csr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var serve = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                using var ssl = new SslStream(client.GetStream());
                try { await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token); }
                catch (Exception error) when (error is System.Security.Authentication.AuthenticationException or IOException) { }
            });
            var package = Package(permissions: ["network.connect"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            var selections = new NetworkSelections(Area()); string id = selections.Approve(package, grant, "TLS test", LoopbackDestination(port, "https"));
            await using var access = new NetworkAccess(package, grant, selections);
            var result = await NetworkResult(access, access.Request(HttpRequest(id)));
            True(result.Result?["state"]?.GetValue<string>() == "failed");
            True(result.Result?["error"]?["code"]?.GetValue<string>() == "network_unavailable");
            await serve;
        });
        await Test("Manager destination review is single-use, hash-bound and revocation drains before removing access", async () =>
        {
            string area = Area(); var package = Package(permissions: ["network.connect"]);
            new AddonRegistry(area).Install(package, package.Manifest.Permissions);
            var selections = new NetworkSelections(area); var worker = new RetryAddon();
            await using var service = new AddonService(area, (_, _, _, _) => Task.FromResult<IAddonInstance>(worker), networkSelections: selections);
            Task<JsonNode?> Call(string method, JsonObject parameters) => service.InvokeAsync("network-manager", method, parameters, default);
            await Call("manager.hello", new() { ["major"] = 1L });
            var parameters = new JsonObject { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["origin"] = "http://127.0.0.1:18974" };
            var inspected = await Call("network.inspectDestination", parameters);
            True(((JsonArray)(await Call("network.selections", new() { ["id"] = package.Manifest.Id }))!["destinations"]!).Count == 0);
            var approval = new JsonObject { ["id"] = package.Manifest.Id, ["expectedHash"] = new string('0', 64), ["reviewId"] = inspected!["reviewId"]!.DeepClone(), ["name"] = "local test" };
            await Error("integrity_mismatch", () => Call("network.approveDestination", approval));
            approval["expectedHash"] = package.Hash;
            var approved = await Call("network.approveDestination", approval);
            await Error("review_expired", () => Call("network.approveDestination", approval));
            await Call("addons.start", new() { ["id"] = package.Manifest.Id });
            var revoke = new JsonObject { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["destinationId"] = approved!["destinationId"]!.DeepClone() };
            await Error("cleanup_failed", () => Call("network.revoke", revoke));
            True(((JsonArray)(await Call("network.selections", new() { ["id"] = package.Manifest.Id }))!["destinations"]!).Count == 1);
            worker.Fail = false; await Call("network.revoke", revoke);
            True(((JsonArray)(await Call("network.selections", new() { ["id"] = package.Manifest.Id }))!["destinations"]!).Count == 0 && worker.Closes == 2);
        });
    }

    private static async Task NetworkRuntimeChecks(string runtime, string compiler, string dotnet)
    {
        await Test("Actual Wasm polls HTTP binary results, sends UDP bytes and continues timer callbacks", async () =>
        {
            byte[] response = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
            await using var server = new HttpFixture(async (_, token) => { await Task.Delay(300, token); return new(200, response); });
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            string area = Area(), source = Path.Combine(area, "source"); Directory.CreateDirectory(source);
            var manifest = Manifest(permissions: ["network.connect"]) with { Api = new() { Major = 1, MinMinor = 3 } };
            File.WriteAllText(Path.Combine(source, "manifest.json"), JsonSerializer.Serialize(manifest, Contract.Json));
            File.WriteAllText(Path.Combine(source, "addon.js"), """
                let request, ticks = 0, result = null;
                function onEvent(event, ajn) {
                    if (event.name === "start") {
                        const destinations = ajn.network.selections().destinations;
                        const web = destinations.find(d => d.protocol === "http");
                        const device = destinations.find(d => d.protocol === "udp");
                        const body = new Uint8Array(32768); for (let i = 0; i < body.length; ++i) body[i] = i & 255;
                        request = ajn.network.request(web.id, { method: "POST", path: "/test", body }).requestId;
                        const sent = ajn.network.sendDatagram(device.id, new Uint8Array([0, 10, 255, 12, 9]));
                        if (sent.bytesSent !== 5) throw Error("bad send length");
                        ajn.timers.set("poll", 30);
                        return ajn.info().api.minor;
                    }
                    if (event.name === "timer") {
                        ticks++;
                        const current = ajn.network.result(request);
                        if (current.state === "failed") throw Error(current.error.code);
                        if (current.state === "completed") {
                            if (current.body.length !== 65536) throw Error("bad response length");
                            for (let i = 0; i < current.body.length; ++i) if (current.body[i] !== (i & 255)) throw Error("bad response byte");
                            result = { ticks, status: current.status, bytes: current.body.length };
                            ajn.timers.clear("poll");
                        }
                    }
                    if (event.name === "status") return result;
                }
                """);
            var package = await DeveloperTools.BuildAsync(source, compiler, Path.Combine(area, "network.ajnaddon"));
            var grant = new PermissionGrant(package, package.Manifest.Permissions); var selections = new NetworkSelections(area);
            selections.Approve(package, grant, "web", LoopbackDestination(server.Port));
            selections.Approve(package, grant, "device", LoopbackDestination(((IPEndPoint)udp.Client.LocalEndPoint!).Port, "udp"));
            await using var worker = await AddonWorker.StartAsync(package, grant, runtime, Path.Combine(area, "workers"), area,
                new(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]), networkSelections: selections);
            Same(JsonValue.Create(Contract.Minor), await worker.SendEventAsync("start"));
            var packet = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3)); True(packet.Buffer.SequenceEqual(new byte[] { 0, 10, 255, 12, 9 }));
            JsonNode? result = null; var timer = Stopwatch.StartNew();
            while (result is null) { True(timer.Elapsed < TimeSpan.FromSeconds(5)); result = await worker.SendEventAsync("status"); await Task.Delay(30); }
            True(result["ticks"]!.GetValue<int>() >= 2 && result["status"]!.GetValue<int>() == 200 && result["bytes"]!.GetValue<int>() == 65536);
            True(server.Last!.Body.Length == 32768 && server.Last.Body.Select((b, i) => b == (byte)i).All(b => b));
            True(await worker.SendEventAsync("status") is not null, "JSON framing after binary response");
        });
    }

    private static async Task ServiceInspectorChecks(string runtime, string compiler, string dotnet)
    {
        await Test("Shipped service inspector applies saved settings, scoped credentials and UDP actions", async () =>
        {
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, [1, 2, 3], "Content-Type: application/octet-stream\r\n")));
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            string area = Area();
            var package = await DeveloperTools.BuildAsync(Path.Combine(AppContext.BaseDirectory, "service-inspector"), compiler, Path.Combine(area, "inspector.ajnaddon"));
            var grant = new PermissionGrant(package, package.Manifest.Permissions); var selections = new NetworkSelections(area);
            string web = selections.Approve(package, grant, "test", LoopbackDestination(server.Port));
            selections.SetCredential(package, grant, web, "X-Inspector", "synthetic-inspector-value");
            selections.Approve(package, grant, "device", LoopbackDestination(((IPEndPoint)udp.Client.LocalEndPoint!).Port, "udp"));
            await using var worker = await AddonWorker.StartAsync(package, grant, runtime, Path.Combine(area, "workers"), area,
                new(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]), networkSelections: selections);
            await worker.SendEventAsync("start"); True(server.Requests == 0);
            Task<JsonNode?> Action(string id) => worker.SendEventAsync("action", new JsonObject { ["id"] = id });
            True(((JsonArray)(await Action("list"))!["destinations"]!).Count == 2);
            async Task Completed()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while ((await Action("status"))?["state"]?.GetValue<string>() == "pending") await Task.Delay(30, timeout.Token);
                var result = await Action("status"); True(result?["status"]?.GetValue<int>() == 200 && result?["bytes"]?.GetValue<int>() == 3, result!.ToJsonString());
            }
            await Action("request"); await Completed(); True(!server.Last!.Headers.ContainsKey("X-Inspector"));
            var settings = new AddonSettings(area, package.Manifest);
            settings.Update(new() { ["useCredential"] = true, ["path"] = "/changed" });
            await worker.SendEventAsync("settings.changed"); await Action("request"); await Completed();
            True(server.Last!.Path == "/changed" && server.Last.Headers["X-Inspector"] == "synthetic-inspector-value");
            settings.Update(new() { ["destinationIndex"] = 2L, ["datagram"] = "Hello 日本語" });
            await Action("request");
            var sent = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3)); True(Encoding.UTF8.GetString(sent.Buffer) == "Hello 日本語");
            True((await Action("status"))?["bytesSent"]?.GetValue<int>() == sent.Buffer.Length);
            await worker.SendEventAsync("stop");
        });
    }

}
