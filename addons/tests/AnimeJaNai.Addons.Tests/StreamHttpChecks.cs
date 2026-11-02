using System.Net;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task StreamHttpChecks()
    {
        await Test("Stream HTTP delivers native bytes with HEAD, ranges and conditional validators", async () =>
        {
            var package = Package(permissions: ["network.listen"]); var grant = new PermissionGrant(package, ["network.listen"]);
            var store = new ListenerSelections(Area()); int port = ListenerPort();
            string selected = store.Approve(package, grant, "stream", new("127.0.0.1", port, [], []));
            await using var access = new HttpServerAccess(package, grant, store);
            string server = access.Open(selected);
            await Until(() => access.Status(server)["state"]!.GetValue<string>() == "listening");
            await using var cache = StreamCache.Create(Area(), 0);
            byte[] bytes = Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(Path.Combine(cache.Directory, "part_00000000.ts"), bytes);
            FixtureStreamMetadata(cache.Directory, "part_00000000.ts");
            await cache.PublishAsync(new([new NativeSegment(0, "part_00000000.ts", 0, 1)], null, false), default);
            var descriptor = cache.List(null, 1)["segments"]![0]!;
            string resource = descriptor["resourceId"]!.GetValue<string>(), etag = descriptor["etag"]!.GetValue<string>();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            async Task<HttpResponseMessage> Send(HttpMethod method, params (string, string)[] headers)
            {
                using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}/segment");
                foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
                var response = client.SendAsync(request); string requestId = (await NextHttp(access)).Data["requestId"]!.GetValue<string>();
                var lease = cache.Acquire(cache.Generation, resource);
                Task work = access.ClaimNative(requestId, async (context, token) =>
                { try { await StreamHttpDelivery.SendAsync(context, lease, "mpegts", token); } finally { lease.Dispose(); } });
                await work; return await response;
            }
            using (var full = await Send(HttpMethod.Get))
            { True(full.StatusCode == HttpStatusCode.OK && full.Headers.ETag!.Tag == etag); True((await full.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes)); }
            using (var head = await Send(HttpMethod.Head))
            { True(head.Content.Headers.ContentLength == bytes.Length && (await head.Content.ReadAsByteArrayAsync()).Length == 0); }
            using (var slice = await Send(HttpMethod.Get, ("Range", "bytes=32000-65999")))
            { True(slice.StatusCode == HttpStatusCode.PartialContent && slice.Content.Headers.ContentRange!.ToString() == "bytes 32000-65999/200000"); True((await slice.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[32000..66000])); }
            using (var tail = await Send(HttpMethod.Get, ("Range", "bytes=-3")))
            { True((await tail.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[^3..])); }
            using (var invalid = await Send(HttpMethod.Get, ("Range", "bytes=200000-")))
            { True(invalid.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && invalid.Content.Headers.ContentRange!.ToString() == "bytes */200000"); }
            using (var multi = await Send(HttpMethod.Get, ("Range", "bytes=0-1,4-5"))) True(multi.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable);
            using (var unchanged = await Send(HttpMethod.Get, ("If-None-Match", "W/" + etag))) True(unchanged.StatusCode == HttpStatusCode.NotModified);
            using (var failed = await Send(HttpMethod.Get, ("If-Match", "W/" + etag))) True(failed.StatusCode == HttpStatusCode.PreconditionFailed);
            using (var changed = await Send(HttpMethod.Get, ("If-Range", "\"old\""), ("Range", "bytes=0-1")))
            { True(changed.StatusCode == HttpStatusCode.OK && (await changed.Content.ReadAsByteArrayAsync()).Length == bytes.Length); }
            using (var post = await Send(HttpMethod.Post)) True(post.StatusCode == HttpStatusCode.MethodNotAllowed);
            True(cache.Counters()["activeReaders"]!.GetValue<int>() == 0);
        });
        await Test("Stream continuous HTTP follows growing data and completes after its producer", async () =>
        {
            await using var cache = StreamCache.Create(Area(), 0);
            using var writer = new FileStream(Path.Combine(cache.Directory, "continuous.mkv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            writer.Write([1, 2]); writer.Flush(); cache.PublishContinuous("matroska");
            string resource = cache.List(null, 1)["continuousResourceId"]!.GetValue<string>();
            using var lease = cache.Acquire(cache.Generation, resource);
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext(); context.Request.Method = "GET";
            var body = new MemoryStream(); context.Response.Body = body;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task sending = StreamHttpDelivery.SendAsync(context, lease, "matroska", deadline.Token);
            await Until(() => body.Length == 2); True(!sending.IsCompleted);
            writer.Write([3, 4]); writer.Flush(); await Until(() => body.Length == 4);
            cache.ProducerCompleted = true; await sending;
            True(body.ToArray().SequenceEqual(new byte[] { 1, 2, 3, 4 }));
        });
    }
}
