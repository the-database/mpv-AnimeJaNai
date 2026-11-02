using System.Net;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

internal static partial class Checks
{
    private static async Task HttpRequestChecks()
    {
        await Test("HTTP buffered bodies and responses cross broker in bounded exact chunks", async () =>
        {
            string area = Area(); var package = Package(permissions: ["network.listen"]);
            var grant = new PermissionGrant(package, package.Manifest.Permissions);
            int port = ListenerPort(); var selections = new ListenerSelections(area);
            string selected = selections.Approve(package, grant, "test", new("127.0.0.1", port, [], []));
            await using var broker = new Broker(package, grant, area);
            var access = broker.HttpServers; string server = access.Open(selected);
            await Until(() => access.Status(server)["state"]!.GetValue<string>() == "listening");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            byte[] body = Enumerable.Range(0, HttpServerAccess.MaximumBufferedBytes).Select(i => (byte)(i * 17)).ToArray();
            var sent = client.PostAsync($"http://127.0.0.1:{port}/", new ByteArrayContent(body));
            string id = (await NextHttp(access)).Data["requestId"]!.GetValue<string>();
            var p = new JsonObject { ["requestId"] = id };
            await broker.InvokeAsync("httpServer.readBody", p, default);
            await Until(() => access.RequestStatus(id)["bodyState"]!.GetValue<string>() != "reading");
            await Error("invalid_request", () => access.BodyChunk(id, 0, 32769));
            using var received = new MemoryStream();
            for (int offset = 0; offset < body.Length; offset += 32768)
            {
                var chunk = await broker.InvokeTransportAsync("httpServer.bodyChunk", new() { ["requestId"] = id, ["offset"] = (long)offset, ["count"] = 32768L }, default);
                received.Write(chunk.Binary.Span);
            }
            True(received.ToArray().SequenceEqual(body));
            access.BeginResponse(id, 200, []);
            await Error("request_claimed", () => access.Respond(id, 204, [], []));
            for (int offset = 0; offset < body.Length; offset += 32768) access.AppendResponse(id, body[offset..(offset + 32768)]);
            await Error("invalid_response", () => access.AppendResponse(id, [1]));
            access.FinishResponse(id);
            using var response = await sent;
            True((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(body));
        });
        await Test("HTTP deadline extensions preserve pending ownership and cancellation closes a request", async () =>
        {
            var package = Package(permissions: ["network.listen"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            int port = ListenerPort(); var selections = new ListenerSelections(Area());
            string selected = selections.Approve(package, grant, "test", new("127.0.0.1", port, [], []));
            await using var access = new HttpServerAccess(package, grant, selections) { DecisionTimeout = TimeSpan.FromMilliseconds(300) };
            string server = access.Open(selected);
            await Until(() => access.Status(server)["state"]!.GetValue<string>() == "listening");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var sent = client.GetAsync($"http://127.0.0.1:{port}/");
            string id = (await NextHttp(access)).Data["requestId"]!.GetValue<string>();
            access.Extend(id, 2);
            await Task.Delay(450);
            True(access.RequestStatus(id)["state"]!.GetValue<string>() == "pending");
            await Error("invalid_request", () => access.Extend(id, 121));
            access.Respond(id, 204, [], []); using var response = await sent;
            True(response.StatusCode == HttpStatusCode.NoContent);
            var cancelled = client.GetAsync($"http://127.0.0.1:{port}/cancel");
            string cancelId = (await NextHttp(access)).Data["requestId"]!.GetValue<string>();
            access.CancelRequest(cancelId);
            await Error("request_not_found", () => access.Extend(cancelId, 120));
            try { using var result = await cancelled; True(!result.IsSuccessStatusCode); } catch (HttpRequestException) { }
        });
    }
}
