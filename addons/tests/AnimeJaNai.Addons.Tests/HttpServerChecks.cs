using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

internal static partial class Checks
{
    private static int ListenerPort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private static async Task<(string Name, JsonObject Data)> NextHttp(HttpServerAccess server, string name = "http.request")
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (server.TakeEvent() is { } item && item.Name == name) return item;
            await Task.Delay(10, deadline.Token);
        }
    }
    private static async Task HttpServerChecks()
    {
        await Test("Listener review is single-use and failed revocation retains consent for retry", async () =>
        {
            string area = Area(); var package = Package(permissions: ["network.listen"]);
            new AddonRegistry(area).Install(package, ["network.listen"]);
            var worker = new RetryAddon();
            await using var service = new AddonService(area, (_, _, _, _) => Task.FromResult<IAddonInstance>(worker));
            async Task<JsonNode?> Call(string method, JsonObject p) => await service.InvokeAsync("listener-manager", method, p, default);
            await Call("manager.hello", new() { ["major"] = 1L });
            var parameters = new JsonObject { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["address"] = "127.0.0.1", ["port"] = (long)ListenerPort(), ["sensitiveHeaders"] = new JsonArray(), ["sensitiveQuery"] = new JsonArray("token") };
            var review = await Call("listeners.inspect", parameters);
            var approve = new JsonObject { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["name"] = "fixture", ["reviewId"] = review!["reviewId"]!.DeepClone() };
            var selected = await Call("listeners.approve", approve);
            await Error("review_expired", () => Call("listeners.approve", approve));
            await Call("addons.start", new() { ["id"] = package.Manifest.Id });
            var revoke = new JsonObject { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["listenerId"] = selected!["listenerId"]!.DeepClone() };
            await Error("cleanup_failed", () => Call("listeners.revoke", revoke));
            True((await Call("listeners.selections", new() { ["id"] = package.Manifest.Id }))!["listeners"]!.AsArray().Count == 1);
            worker.Fail = false;
            await Call("listeners.revoke", revoke);
            True(worker.Closes == 2 && (await Call("listeners.selections", new() { ["id"] = package.Manifest.Id }))!["listeners"]!.AsArray().Count == 0);
        });
        await Test("Listener approvals require permission, survive reload and bind exact packages", async () =>
        {
            string area = Area(); var package = Package(permissions: ["network.listen"]);
            var grant = new PermissionGrant(package, ["network.listen"]); var store = new ListenerSelections(area);
            int port = ListenerPort(); var binding = new ListenerBinding("127.0.0.1", port, ["X-Key"], ["token"]);
            await Error("permission_denied", () => store.Approve(package, new(package, []), "test", binding));
            await Error("invalid_listener", () => store.Approve(package, grant, "test", binding with { Address = "0.0.0.0" }));
            await Error("invalid_listener", () => store.Approve(package, grant, "test", binding with { Port = 0 }));
            string id = store.Approve(package, grant, "test", binding);
            binding.SensitiveHeaders[0] = "changed";
            True(new ListenerSelections(area).Resolve(package, grant, id).Binding.SensitiveHeaders[0] == "X-Key");
            var update = Package(version: "1.0.1", permissions: ["network.listen"]);
            await Error("listener_not_granted", () => store.Resolve(update, new(update, ["network.listen"]), id));
            store.Revoke(package, grant, id); True(store.List(package, grant).Length == 0);
        });
        await Test("HTTP preserves repeated values, redacts configured fields and claims requests once", async () =>
        {
            var package = Package(permissions: ["network.listen"]); var grant = new PermissionGrant(package, ["network.listen"]);
            var store = new ListenerSelections(Area()); int port = ListenerPort();
            string selected = store.Approve(package, grant, "test", new("127.0.0.1", port, ["X-Key"], ["token"]));
            await using var access = new HttpServerAccess(package, grant, store);
            string server = access.Open(selected);
            await Until(() => access.Status(server)["state"]!.GetValue<string>() != "opening");
            True(access.Status(server)["state"]!.GetValue<string>() == "listening", access.Status(server).ToJsonString());
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var input = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/hello?a=one&a=two&token=secret-query");
            input.Headers.TryAddWithoutValidation("X-Test", new[] { "one", "two" });
            input.Headers.TryAddWithoutValidation("X-Key", "secret-header");
            input.Headers.TryAddWithoutValidation("Authorization", "Bearer secret-auth");
            var responseTask = client.SendAsync(input);
            var received = (await NextHttp(access)).Data;
            True(!received.ToJsonString().Contains("secret-"));
            var query = (JsonArray)received["query"]!;
            True(query.First(n => n!["name"]!.GetValue<string>() == "a")!["values"]!.AsArray().Count == 2);
            string request = received["requestId"]!.GetValue<string>();
            await Error("invalid_response", () => access.Respond(request, 200, [new("Content-Length", ["999"])], []));
            await using var other = new HttpServerAccess(package, grant, store);
            await Error("request_not_found", () => other.Respond(request, 200, [], []));
            access.Respond(request, 200, [new("Set-Cookie", ["a=1", "b=2"])], Encoding.UTF8.GetBytes("Hello 日本語"));
            using var response = await responseTask;
            True(await response.Content.ReadAsStringAsync() == "Hello 日本語");
            True(response.Headers.GetValues("Set-Cookie").Count() == 2);
            try { access.Respond(request, 200, [], []); throw new Exception("Duplicate response succeeded"); }
            catch (AddonException e) when (e.Code is "request_claimed" or "request_not_found") { }
            using var originRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
            originRequest.Headers.Add("Origin", "https://unapproved.example");
            using var denied = await client.SendAsync(originRequest); True(denied.StatusCode == HttpStatusCode.Forbidden);
        });
        await Test("HTTP HEAD, expiry, stop and port release have bounded lifetimes", async () =>
        {
            var package = Package(permissions: ["network.listen"]); var grant = new PermissionGrant(package, ["network.listen"]);
            var store = new ListenerSelections(Area()); int port = ListenerPort();
            string selected = store.Approve(package, grant, "test", new("127.0.0.1", port, [], []));
            await using var access = new HttpServerAccess(package, grant, store) { DecisionTimeout = TimeSpan.FromMilliseconds(500) };
            string server = access.Open(selected);
            await Until(() => access.Status(server)["state"]!.GetValue<string>() != "opening");
            True(access.Status(server)["state"]!.GetValue<string>() == "listening");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var head = new HttpRequestMessage(HttpMethod.Head, $"http://127.0.0.1:{port}/");
            var task = client.SendAsync(head);
            var request = (await NextHttp(access)).Data["requestId"]!.GetValue<string>();
            access.Respond(request, 200, [], Encoding.ASCII.GetBytes("three"));
            using var response = await task;
            True(response.Content.Headers.ContentLength == 5 && (await response.Content.ReadAsByteArrayAsync()).Length == 0);
            using var expired = await client.GetAsync($"http://127.0.0.1:{port}/ignored");
            True(expired.StatusCode == HttpStatusCode.GatewayTimeout);
            access.RequestClose(server);
            await access.DisposeAsync();
            using var boundAgain = new TcpListener(IPAddress.Loopback, port); boundAgain.Start();
        });
        await Test("HTTP request overload is rejected without unbounded queuing", async () =>
        {
            var package = Package(permissions: ["network.listen"]); var grant = new PermissionGrant(package, ["network.listen"]);
            var store = new ListenerSelections(Area()); int port = ListenerPort();
            string selected = store.Approve(package, grant, "test", new("127.0.0.1", port, [], []));
            await using var access = new HttpServerAccess(package, grant, store);
            string server = access.Open(selected);
            await Until(() => access.Status(server)["state"]!.GetValue<string>() != "opening");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var pending = new List<(Task<HttpResponseMessage> Response, string Id)>();
            for (int i = 0; i < HttpServerAccess.MaximumRequests; i++)
            {
                var sent = client.GetAsync($"http://127.0.0.1:{port}/{i}");
                pending.Add((sent, (await NextHttp(access)).Data["requestId"]!.GetValue<string>()));
            }
            using var overload = await client.GetAsync($"http://127.0.0.1:{port}/overflow");
            True(overload.StatusCode == HttpStatusCode.ServiceUnavailable);
            foreach (var item in pending) access.Respond(item.Id, 204, [], []);
            foreach (var item in pending) { using var response = await item.Response; True(response.StatusCode == HttpStatusCode.NoContent); }
        });
        await Test("Port conflict and owner stop cancel pending requests without orphan listeners", async () =>
        {
            var package = Package(permissions: ["network.listen"]); var grant = new PermissionGrant(package, ["network.listen"]);
            var store = new ListenerSelections(Area()); int port = ListenerPort();
            string selected = store.Approve(package, grant, "test", new("127.0.0.1", port, [], []));
            await using var access = new HttpServerAccess(package, grant, store);
            string id = access.Open(selected);
            await Until(() => access.Status(id)["state"]!.GetValue<string>() == "listening");
            await using (var conflict = new HttpServerAccess(package, grant, store))
            {
                string other = conflict.Open(selected);
                await Until(() => conflict.Status(other)["state"]!.GetValue<string>() != "opening");
                True(conflict.Status(other)["state"]!.GetValue<string>() == "failed");
            }
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var pending = client.GetAsync($"http://127.0.0.1:{port}/pending");
            await NextHttp(access);
            await access.DisposeAsync();
            try { using var response = await pending; True(!response.IsSuccessStatusCode); }
            catch (HttpRequestException) { }
            using var boundAgain = new TcpListener(IPAddress.Loopback, port); boundAgain.Start();
        });
    }
    private static async Task HttpServerRuntimeChecks(string runtime, string compiler, string dotnet)
    {
        await Test("Packaged Wasm addon accepts HTTP events and returns a native response", async () =>
        {
            string area = Area(), source = Path.Combine(area, "source"), data = Path.Combine(area, "data");
            DeveloperTools.New(source, "org.example.http");
            string examples = Path.Combine(AppContext.BaseDirectory, "http-inspector");
            File.Copy(Path.Combine(examples, "manifest.json"), Path.Combine(source, "manifest.json"), true);
            File.Copy(Path.Combine(examples, "addon.js"), Path.Combine(source, "addon.js"), true);
            string archive = Path.Combine(area, "http.ajnaddon");
            await DeveloperTools.BuildAsync(source, compiler, archive);
            var package = AddonPackage.Load(archive); var grant = new PermissionGrant(package, ["network.listen"]);
            var store = new ListenerSelections(data); int port = ListenerPort();
            store.Approve(package, grant, "runtime fixture", new("127.0.0.1", port, [], ["token"]));
            await using var worker = await AddonWorker.StartAsync(package, grant, runtime, Path.Combine(area, "workers"), data,
                new WorkerCommand(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]));
            await worker.SendEventAsync("start");
            await worker.SendEventAsync("action", new JsonObject { ["id"] = "open" });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var state = await worker.SendEventAsync("action", new JsonObject { ["id"] = "status" }, deadline.Token);
                if (state?["state"]?.GetValue<string>() == "listening") break;
                if (state?["state"]?.GetValue<string>() == "failed") throw new Exception(state.ToJsonString());
                await Task.Delay(25, deadline.Token);
            }
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            True(await client.GetStringAsync($"http://127.0.0.1:{port}/hello?token=not-logged") == "Hello from an AJN addon.");
            True(!worker.Diagnostics.Contains("not-logged"));
            await worker.SendEventAsync("stop");
            await worker.DisposeAsync();
            using var boundAgain = new TcpListener(IPAddress.Loopback, port); boundAgain.Start();
        });
    }
}
