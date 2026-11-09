using System.Net;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.TestSupport;
using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task RequestCredentialChecks()
    {
        await Test("Request-derived credentials are redacted before delivery, scoped and revoked with active upstream work", async () =>
        {
            string area = Area(); var manifest = Manifest(permissions: ["network.listen", "network.connect", "network.proxy", "credentials.use", "credentials.delegate", "media.input", "sessions.manage"]) with
            { Api = new() { Major = 1, MinMinor = 7 }, SensitiveRequestFields = new(["X-Client-Key"], ["client_key"]) };
            var package = AddonPackage.Create(manifest, EmptyModule); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            var listeners = new ListenerSelections(area); int port = ListenerPort();
            string listener = listeners.Approve(package, grant, "test", new("127.0.0.1", port, [], []));
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var upstream = new HttpFixture(async (input, token) =>
            {
                if (input.Path.StartsWith("/slow")) { waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
                return new HttpReply(200, [7]);
            });
            var destinations = new NetworkSelections(area);
            string destination = destinations.Approve(package, grant, "first", LoopbackDestination(upstream.Port));
            string otherDestination = destinations.Approve(package, grant, "second", LoopbackDestination(upstream.Port));
            await using var broker = new Broker(package, grant, area, networkSelections: destinations);
            string server = broker.HttpServers.Open(listener);
            await Until(() => broker.HttpServers.Status(server)["state"]!.GetValue<string>() == "listening");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var input = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/?client_key=private-query");
            input.Headers.Add("X-Client-Key", "private-header");
            var pending = client.SendAsync(input); var received = (await NextHttp(broker.HttpServers)).Data;
            True(!received.ToJsonString().Contains("private-"));
            string request = received["requestId"]!.GetValue<string>();
            JsonArray mappings = new(new JsonObject { ["from"] = "header", ["name"] = "X-Client-Key", ["to"] = "header", ["target"] = "X-Upstream-Key" },
                new JsonObject { ["from"] = "query", ["name"] = "client_key", ["to"] = "query", ["target"] = "upstream_key" });
            var captured = await broker.InvokeAsync("requestCredentials.capture", new() { ["requestId"] = request, ["destinationId"] = destination, ["mappings"] = mappings.DeepClone(), ["seconds"] = 60L }, default);
            string credential = captured!["credentialId"]!.GetValue<string>();
            True(!broker.RequestCredentials.Status(credential).ToJsonString().Contains("private-"));
            using var mediaCredential = broker.RequestCredentials.Acquire(credential, destination);
            var mediaRequest = new RemoteInputRequest(destination, "/media", CredentialId: credential);
            var mediaPlan = RemoteInputPlan.Prepare(package, grant, destinations, mediaRequest, mediaCredential);
            var decodedPlan = RemoteInputPlan.FromPrivateJson(mediaPlan.ToPrivateJson());
            True(decodedPlan.Target.PathAndQuery == "/media?upstream_key=private-query" && decodedPlan.DelegatedHeaders["X-Upstream-Key"][0] == "private-header");
            True(!decodedPlan.IdentityKey.Contains("private-") && !decodedPlan.ToString().Contains("private-"));
            using (var source = await RemoteMediaStream.OpenAsync(decodedPlan))
            {
                True((await ReadAll(source)).SequenceEqual(new byte[] { 7 }));
                True(upstream.Last!.Headers["X-Upstream-Key"] == "private-header" && upstream.Last.Path == "/media?upstream_key=private-query");
                True(source.RepresentationId == source.RepresentationId);
            }
            await Error("invalid_input", () => (mediaRequest with { UseCredential = true }).Validate());
            await Error("credential_field_not_declared", () => broker.RequestCredentials.Capture(request, destination,
                new(new JsonObject { ["from"] = "header", ["name"] = "Host", ["to"] = "header", ["target"] = "X-Test" }), 10));
            broker.HttpServers.Respond(request, 204, [], []); using var accepted = await pending;
            await Error("credential_destination_mismatch", () => broker.HttpProxy.Buffer(otherDestination, new() { ["path"] = "/", ["credentialId"] = credential }));
            string operation = broker.HttpProxy.Buffer(destination, new() { ["path"] = "/validate", ["credentialId"] = credential });
            await Until(() => broker.HttpProxy.Status(operation)["cleanupReady"]!.GetValue<bool>());
            True(broker.HttpProxy.Status(operation)["state"]!.GetValue<string>() == "completed");
            True(upstream.Last!.Headers["X-Upstream-Key"] == "private-header" && upstream.Last.Path == "/validate?upstream_key=private-query");
            broker.HttpProxy.Close(operation);
            string conflict = broker.HttpProxy.Buffer(destination, new() { ["path"] = "/?upstream_key=other", ["credentialId"] = credential });
            await Until(() => broker.HttpProxy.Status(conflict)["cleanupReady"]!.GetValue<bool>());
            True(broker.HttpProxy.Status(conflict)["error"]!["code"]!.GetValue<string>() == "credential_conflict"); broker.HttpProxy.Close(conflict);
            string slow = broker.HttpProxy.Buffer(destination, new() { ["path"] = "/slow", ["credentialId"] = credential });
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            broker.RequestCredentials.Release(credential);
            True(mediaCredential.Token.IsCancellationRequested);
            await Until(() => broker.HttpProxy.Status(slow)["cleanupReady"]!.GetValue<bool>());
            True(broker.HttpProxy.Status(slow)["error"]!["code"]!.GetValue<string>() == "proxy_cancelled"); broker.HttpProxy.Close(slow);
            await Error("credential_not_found", () => broker.RequestCredentials.Status(credential));
        });
    }
}
