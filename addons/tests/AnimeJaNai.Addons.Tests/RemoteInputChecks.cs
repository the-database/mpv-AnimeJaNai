using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;
using AnimeJaNai.Addons.TestSupport;

internal static partial class Checks
{
    private static readonly string[] InputPermissions = ["sessions.manage", "network.connect", "media.input", "credentials.use"];
    private static RemoteInputPlan InputPlan(int port, string? header = null, string? credential = null) =>
        new(new($"http://input.example.invalid:{port}", "http", "input.example.invalid", port, ["127.0.0.1"]), "/media", header, credential);
    private static int RangeStart(HttpInput request) => int.Parse(request.Headers["Range"][6..^1], CultureInfo.InvariantCulture);
    private static HttpReply RangeReply(HttpInput request, byte[] data, string validator = "ETag: \"sample-1\"\r\n", int segment = int.MaxValue)
    {
        int start = RangeStart(request), last = Math.Min(data.Length - 1, start + Math.Min(segment, data.Length) - 1);
        return new(206, data[start..(last + 1)], $"Content-Range: bytes {start}-{last}/{data.Length}\r\n" + validator);
    }
    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var output = new MemoryStream(); await stream.CopyToAsync(output); return output.ToArray();
    }

    private static async Task RemoteInputChecks()
    {
        await Test("File-backed remote fixture preserves range offsets and streamed bytes", async () =>
        {
            byte[] expected = Enumerable.Range(0, 300000).Select(i => (byte)(i % 251)).ToArray();
            string path = Path.Combine(Area(), "source.bin"); await File.WriteAllBytesAsync(path, expected);
            await using var server = new HttpFixture((request, _) =>
            {
                int start = RangeStart(request);
                return Task.FromResult(new HttpReply(206, [], $"Content-Range: bytes {start}-{expected.Length - 1}/{expected.Length}\r\nETag: \"file-fixture\"\r\n", FileBody: path, FileOffset: start));
            });
            using var stream = await RemoteMediaStream.OpenAsync(InputPlan(server.Port), default);
            byte[] prefix = new byte[1024]; stream.ReadExactly(prefix); True(prefix.SequenceEqual(expected[..1024]));
            stream.Seek(150000, SeekOrigin.Begin);
            True((await ReadAll(stream)).SequenceEqual(expected[150000..]));
        });
        await Test("Remote input API requires its capability and independent grants before opening a session", async () =>
        {
            string area = Area(); var package = Package(permissions: [.. InputPermissions, "media.output"]);
            var provider = new RemoteProvider(); var registry = new SessionRegistry(provider, totalLimit: 2, perOwnerLimit: 1);
            JsonObject Parameters() => new() { ["source"] = new JsonObject { ["type"] = "http", ["destinationId"] = "approved",
                ["path"] = "/media", ["useCredential"] = false }, ["profileId"] = "profile" };
            await using var broker = new Broker(package, new(package, package.Manifest.Permissions), area, sessions: registry);
            True(broker.Info()["capabilities"]?["remoteSources"] is not null);
            foreach (string permission in new[] { "media.input", "sessions.manage", "network.connect" })
            {
                await using var denied = new Broker(package, new(package, package.Manifest.Permissions.Where(p => p != permission).ToArray()), Area(), sessions: registry);
                await Error("permission_denied", () => denied.InvokeAsync("sessions.openRemote", Parameters(), default));
                await Error("permission_denied", () => denied.InvokeAsync("outputs.openRemote", Parameters(), default));
            }
            await using var noOutput = new Broker(package, new(package, InputPermissions), Area(), sessions: registry);
            await Error("permission_denied", () => noOutput.InvokeAsync("outputs.openRemote", Parameters(), default));
            True(provider.Opened == 0);
            await using var noNative = new Broker(package, new(package, InputPermissions), Area());
            True(noNative.Info()["capabilities"]?["remoteSources"] is null);
            await Error("feature_unavailable", () => noNative.InvokeAsync("sessions.openRemote", Parameters(), default));
            var opened = (JsonObject)(await broker.InvokeAsync("sessions.openRemote", Parameters(), default))!;
            await Error("capacity_exceeded", () => broker.InvokeAsync("sessions.openRemote", Parameters(), default));
            await Error("session_not_found", () => noOutput.InvokeAsync("sessions.status", opened, default));
            await broker.InvokeAsync("sessions.close", opened, default);
            True(provider.Opened == 1 && provider.Closed == 1);
            var output = OutputParameters("receiver"); output.Remove("sourceId"); output["source"] = Parameters()["source"]!.DeepClone();
            opened = (JsonObject)(await broker.InvokeAsync("outputs.openRemote", output, default))!;
            await broker.InvokeAsync("sessions.close", opened, default);
            True(provider.Opened == 2 && provider.Outputs == 1 && provider.Closed == 2);
        });
        await Test("Remote input plan requires owner, service, processing and input grants", async () =>
        {
            string area = Area(); var selections = new NetworkSelections(area);
            var package = Package(permissions: InputPermissions); var grant = new PermissionGrant(package, InputPermissions);
            string id = selections.Approve(package, grant, "Media service", await NetworkDestination.InspectAsync("http://127.0.0.1:18080", default));
            var request = new RemoteInputRequest(id, "/media");
            _ = RemoteInputPlan.Prepare(package, grant, selections, request);
            foreach (string permission in new[] { "media.input", "network.connect", "sessions.manage" })
                await Error("permission_denied", () => RemoteInputPlan.Prepare(package, new(package, InputPermissions.Where(p => p != permission).ToArray()), selections, request));
            await Error("destination_not_granted", () => RemoteInputPlan.Prepare(package, grant, selections, request with { DestinationId = "other" }));
            await Error("invalid_request", () => RemoteInputPlan.Prepare(package, grant, selections, request with { Path = "//other.invalid/media" }));
            await Error("credential_not_granted", () => RemoteInputPlan.Prepare(package, grant, selections, request with { UseCredential = true }));
            selections.SetCredential(package, grant, id, "X-Media-Key", "synthetic-input-credential");
            var plan = RemoteInputPlan.Prepare(package, grant, selections, request with { UseCredential = true });
            var decoded = RemoteInputPlan.FromPrivateJson(plan.ToPrivateJson());
            True(decoded.Credential == "synthetic-input-credential" && decoded.Target.AbsolutePath == "/media");
            True(!decoded.ToString().Contains("synthetic-input-credential"));
            await Error("permission_denied", () => RemoteInputPlan.Prepare(package, new(package, InputPermissions.Where(p => p != "credentials.use").ToArray()), selections, request with { UseCredential = true }));
            await Error("invalid_credential", () => InputPlan(18080, "Range", "bytes=0-").Validate());
        });
        await Test("Remote input pins authority, reads exact bytes and inherits no browser state", async () =>
        {
            byte[] data = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i * 17)).ToArray();
            await using var server = new HttpFixture((request, _) =>
            {
                True(request.Method == "GET" && request.Path == "/media" && request.Headers["Range"] == "bytes=0-");
                True(request.Headers["Host"].StartsWith("input.example.invalid:") && request.Headers["Accept-Encoding"] == "identity");
                True(!request.Headers.ContainsKey("Cookie") && !request.Headers.ContainsKey("Authorization"));
                return Task.FromResult(new HttpReply(200, data));
            });
            using var input = await RemoteMediaStream.OpenAsync(InputPlan(server.Port));
            True(!input.CanSeek && input.Length == data.Length);
            byte[] first = new byte[128 * 1024]; int size = await input.ReadAsync(first);
            True(size > 0 && size <= RemoteMediaStream.MaximumRead);
            byte[] rest = await ReadAll(input);
            True(first.AsSpan(0, size).ToArray().Concat(rest).SequenceEqual(data));
            True(input.Position == data.Length && server.Requests == 1 && input.Status()["bytesRead"]!.GetValue<long>() == data.Length);
        });
        await Test("Remote range reads preserve validators, support seeks and avoid requests at EOF", async () =>
        {
            byte[] data = Enumerable.Range(0, 8000).Select(i => (byte)i).ToArray();
            await using var server = new HttpFixture((request, _) => Task.FromResult(RangeReply(request, data)));
            using var input = await RemoteMediaStream.OpenAsync(InputPlan(server.Port, "X-Media-Key", "synthetic-input-credential"));
            True(input.CanSeek); input.Seek(123, SeekOrigin.Begin);
            byte[] sample = new byte[32]; await input.ReadExactlyAsync(sample);
            True(sample.SequenceEqual(data.AsSpan(123, 32).ToArray()));
            True(server.Last!.Headers["If-Range"] == "\"sample-1\"" && server.Last.Headers["X-Media-Key"] == "synthetic-input-credential");
            int requests = server.Requests;
            True(input.Seek(0, SeekOrigin.End) == data.Length && await input.ReadAsync(sample) == 0 && server.Requests == requests);
            input.Seek(0, SeekOrigin.Begin); True((await ReadAll(input)).SequenceEqual(data));
            True(!input.Status().ToJsonString().Contains("synthetic-input-credential"));
        });
        await Test("Remote input assembles smaller valid ranges and accepts chunked bodies", async () =>
        {
            byte[] data = Enumerable.Range(0, 2000).Select(i => (byte)i).ToArray();
            await using var ranged = new HttpFixture((request, _) => Task.FromResult(RangeReply(request, data, segment: 500) with { Chunked = true }));
            using (var input = await RemoteMediaStream.OpenAsync(InputPlan(ranged.Port))) True((await ReadAll(input)).SequenceEqual(data) && ranged.Requests == 4);
            await using var forward = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, data, Chunked: true)));
            using (var input = await RemoteMediaStream.OpenAsync(InputPlan(forward.Port)))
            { True(!input.CanSeek); True((await ReadAll(input)).SequenceEqual(data)); True(input.Length == data.Length); }
        });
        await Test("Remote range dates require a stable interval and never replace weak entity tags", async () =>
        {
            byte[] data = [1, 2, 3, 4];
            foreach (var (headers, seekable) in new[] {
                ("Last-Modified: Wed, 01 Jan 2025 00:00:00 GMT\r\nDate: Wed, 01 Jan 2025 00:10:00 GMT\r\n", true),
                ("Last-Modified: Wed, 01 Jan 2025 00:00:00 GMT\r\nDate: Wed, 01 Jan 2025 00:00:30 GMT\r\n", false),
                ("ETag: W/\"sample-1\"\r\nLast-Modified: Wed, 01 Jan 2025 00:00:00 GMT\r\nDate: Wed, 01 Jan 2025 00:10:00 GMT\r\n", false),
                ("", false),
                ("ETag: *\r\n", false),
            })
            {
                await using var server = new HttpFixture((request, _) => Task.FromResult(RangeReply(request, data, headers)));
                using var input = await RemoteMediaStream.OpenAsync(InputPlan(server.Port)); True(input.CanSeek == seekable);
                if (seekable)
                {
                    input.Seek(2, SeekOrigin.Begin); True((await ReadAll(input)).SequenceEqual(data[2..]));
                    True(server.Last!.Headers["If-Range"] == "Wed, 01 Jan 2025 00:00:00 GMT");
                }
                else True((await ReadAll(input)).SequenceEqual(data));
            }
        });
        await Test("Remote input stops when range validators change or are ignored", async () =>
        {
            foreach (bool ignored in new[] { false, true })
            {
                int requests = 0; byte[] data = [1, 2, 3, 4];
                await using var server = new HttpFixture((request, _) =>
                {
                    bool first = ++requests == 1;
                    return Task.FromResult(!first && ignored ? new HttpReply(200, data) : RangeReply(request, data, first ? "ETag: \"first\"\r\n" : "ETag: \"changed\"\r\n"));
                });
                using var input = await RemoteMediaStream.OpenAsync(InputPlan(server.Port)); input.Seek(2, SeekOrigin.Begin);
                await Error("input_changed", async () => { await input.ReadExactlyAsync(new byte[10]); });
                True(!input.CanRead);
            }
        });
        await Test("Remote input rejects invalid ranges, oversized metadata and content encodings", async () =>
        {
            foreach (var (reply, code) in new[] {
                (new HttpReply(206, [1], "Content-Range: bytes 1-1/2\r\n"), "input_range"),
                (new HttpReply(206, [1], "Content-Range: bytes 0-0/2\r\n"), "input_not_seekable"),
                (new HttpReply(206, [1], $"Content-Range: bytes 0-0/{RemoteMediaStream.MaximumBytes + 1}\r\n"), "input_range"),
                (new HttpReply(206, [1], "Content-Range: bytes 0-1/2\r\n"), "input_range"),
                (new HttpReply(200, [1], "Content-Encoding: gzip\r\n"), "input_encoding"),
                (new HttpReply(200, [1], "X-Large: " + new string('a', 17000) + "\r\n"), "input_unavailable"),
            })
            {
                await using var server = new HttpFixture((_, _) => Task.FromResult(reply));
                await Error(code, async () => { using var input = await RemoteMediaStream.OpenAsync(InputPlan(server.Port)); });
            }
        });
        await Test("Remote input refuses redirects to another service", async () =>
        {
            await using var other = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, [1])));
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(302, [], $"Location: http://127.0.0.1:{other.Port}/\r\n")));
            await Error("input_status", async () => { using var input = await RemoteMediaStream.OpenAsync(InputPlan(server.Port)); });
            True(other.Requests == 0);
        });
        await Test("Remote input distinguishes a truncated body from ordinary EOF", async () =>
        {
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, [1, 2, 3], DeclaredLength: 8)));
            using var input = await RemoteMediaStream.OpenAsync(InputPlan(server.Port));
            await Error("input_truncated", async () => { await ReadAll(input); });
            True(input.Status()["errorCode"]!.GetValue<string>() == "input_truncated");
        });
        await Test("Remote input cancels header and body waits without blocking the native callback", async () =>
        {
            await using var headers = new HttpFixture(async (_, token) => { await Task.Delay(TimeSpan.FromMinutes(1), token); return new HttpReply(200, [1]); });
            using var stop = new CancellationTokenSource();
            var opening = RemoteMediaStream.OpenAsync(InputPlan(headers.Port), stop.Token);
            var timer = Stopwatch.StartNew();
            while (headers.Requests == 0 && timer.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
            True(headers.Requests == 1); timer.Restart(); stop.Cancel();
            await Error("input_cancelled", async () => { using var input = await opening; });
            True(timer.Elapsed < TimeSpan.FromSeconds(2));
            await using var stalled = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, [1], BodyDelay: TimeSpan.FromMinutes(1))));
            using var body = await RemoteMediaStream.OpenAsync(InputPlan(stalled.Port));
            var reading = body.ReadAsync(new byte[1]).AsTask(); await Task.Delay(50);
            timer.Restart(); body.Cancel(); True(timer.Elapsed < TimeSpan.FromMilliseconds(100));
            await Error("input_cancelled", async () => { await reading.WaitAsync(TimeSpan.FromSeconds(2)); });
        });
        await Test("Remote input applies a deadline to a stalled body", async () =>
        {
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, [1], BodyDelay: TimeSpan.FromMinutes(1))));
            using var input = await RemoteMediaStream.OpenAsync(InputPlan(server.Port));
            var timer = Stopwatch.StartNew();
            await Error("input_timeout", async () => { await input.ReadExactlyAsync(new byte[1]); });
            True(timer.Elapsed >= RemoteMediaStream.IoTimeout - TimeSpan.FromMilliseconds(250) && timer.Elapsed < TimeSpan.FromSeconds(15));
        });
        await Test("Remote lifetime expiry fails an idle reader and retains its first error after cancellation", async () =>
        {
            await using var server = new HttpFixture((_, _) => Task.FromResult(new HttpReply(200, [1])));
            using var input = await RemoteMediaStream.OpenAsync(InputPlan(server.Port), lifetimeLimit: TimeSpan.FromSeconds(1));
            await Task.Delay(1200);
            True(!input.CanRead && input.Status()["errorCode"]!.GetValue<string>() == "input_limit");
            await Error("input_limit", async () => { await input.ReadExactlyAsync(new byte[1]); });
            input.Cancel(); True(input.Status()["errorCode"]!.GetValue<string>() == "input_limit");
        });
    }

    private sealed class RemoteProvider : IProcessingSessionProvider
    {
        internal int Opened, Outputs, Closed;
        public bool SupportsRemoteSources => true;
        public bool SupportsOutputs => true;
        public Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken token) => throw new NotSupportedException();
        public Task<IProcessingSession> OpenRemoteAsync(RemoteInputRequest source, string? profileId, CancellationToken token)
        {
            True(source.DestinationId == "approved" && source.Path == "/media" && profileId == "profile"); Opened++;
            return Task.FromResult<IProcessingSession>(new RemoteFixture(this));
        }
        public Task<IProcessingSession> OpenRemoteOutputAsync(RemoteInputRequest source, string? profileId, OutputRequest output, CancellationToken token)
        { True(output.DestinationId == "receiver"); Outputs++; return OpenRemoteAsync(source, profileId, token); }
        private sealed class RemoteFixture(RemoteProvider parent) : IProcessingSession
        {
            public Task<JsonObject> GetStatusAsync(CancellationToken token) => Task.FromResult(new JsonObject { ["state"] = "running" });
            public ValueTask DisposeAsync() { parent.Closed++; return ValueTask.CompletedTask; }
        }
    }
}
