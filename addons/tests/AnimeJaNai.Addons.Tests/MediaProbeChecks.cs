using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private sealed class ProbeFixture(Func<CancellationToken, Task<JsonObject>> action) : IMediaProbeProvider
    {
        public bool SupportsProbes => true;
        public Task<JsonObject> ProbeAsync(ProbeRequest request, CancellationToken token) => action(token);
    }
    private static async Task ProbeFinished(MediaProbeAccess probes, string id)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (probes.Status(id)["state"]!.GetValue<string>() == "pending") await Task.Delay(5, limit.Token);
    }
    private static async Task MediaProbeChecks()
    {
        await Test("Media probes bound active and retained jobs, cancel independently and preserve chunked results", async () =>
        {
            var package = Package(permissions: ["sessions.manage", "media.input"]);
            var ready = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new ProbeFixture(token => ready.Task.WaitAsync(token));
            await using var probes = new MediaProbeAccess(new(package, package.Manifest.Permissions), provider);
            await using var other = new MediaProbeAccess(new(package, package.Manifest.Permissions), provider);
            string first = probes.Open(new("approved", null)), second = probes.Open(new("approved", null));
            await Error("capacity_exceeded", () => probes.Open(new("approved", null)));
            await Error("probe_not_found", () => other.Status(first));
            await Error("probe_not_ready", () => probes.Result(first, 0, 32768));
            await Error("probe_active", () => probes.Close(first));
            probes.Cancel(second); await ProbeFinished(probes, second);
            True(probes.Status(second)["error"]?["code"]?.GetValue<string>() == "probe_cancelled");
            probes.Close(second);
            var expected = new JsonObject { ["tracks"] = new JsonArray(), ["title"] = new string('x', 70000) };
            ready.SetResult(expected); await ProbeFinished(probes, first);
            using var bytes = new MemoryStream();
            for (int offset = 0;;)
            {
                var chunk = probes.Result(first, offset, 32768);
                bytes.Write(chunk.Binary.Span); offset += chunk.Binary.Length;
                if (chunk.Result!["eof"]!.GetValue<bool>()) break;
            }
            Same(expected, JsonNode.Parse(bytes.ToArray()));
            True(probes.Result(first, 0, 32768).Binary.Span.SequenceEqual(bytes.ToArray().AsSpan(0, 32768)));
            await Error("invalid_request", () => probes.Result(first, 0, 32769));
            await Error("invalid_request", () => probes.Result(first, 262144, 1));
            probes.Close(first); await Error("probe_not_found", () => probes.Status(first));
        });
        await Test("Media probes require input and network grants and never expose a partial oversized result", async () =>
        {
            var package = Package(permissions: ["sessions.manage", "media.input", "network.connect"]);
            var provider = new ProbeFixture(_ => Task.FromResult(new JsonObject { ["title"] = new string('x', 262144) }));
            foreach (string permission in package.Manifest.Permissions)
            {
                await using var denied = new MediaProbeAccess(new(package, package.Manifest.Permissions.Where(p => p != permission).ToArray()), provider);
                await Error("permission_denied", () => denied.Open(new(null, new("approved", "/"))));
            }
            await using var probes = new MediaProbeAccess(new(package, package.Manifest.Permissions), provider);
            string id = probes.Open(new("approved", null)); await ProbeFinished(probes, id);
            True(probes.Status(id)["error"]?["code"]?.GetValue<string>() == "probe_result_too_large");
            await Error("probe_not_ready", () => probes.Result(id, 0, 1)); probes.Close(id);
        });
        await Test("Local probe representation identity is stable and changes after source replacement", () =>
        {
            string path = Path.Combine(Area(), "selected.mkv"); File.WriteAllBytes(path, [1, 2, 3]);
            string Identity() { using var stream = File.OpenRead(path); stream.Position = 1; string id = NativeProbeWorker.LocalIdentity(path, stream); True(stream.Position == 1); return id; }
            string first = Identity(); True(first == Identity()); File.WriteAllBytes(path, [1, 2, 4]); True(first != Identity());
        });
    }
}
