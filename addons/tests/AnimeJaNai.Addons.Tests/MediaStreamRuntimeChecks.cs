using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

internal static partial class Checks
{
    private static async Task MediaStreamRuntimeChecks(string runtime, string compiler, string dotnet)
    {
        await Test("Packaged Wasm media example opens streams, serves owned bytes and replaces capacity-one playback", async () =>
        {
            string area = Area(), source = Path.Combine(area, "source"), data = Path.Combine(area, "data");
            DeveloperTools.New(source, "org.example.stream");
            foreach (string file in new[] { "manifest.json", "addon.js" }) File.Copy(Path.Combine(AppContext.BaseDirectory, "media-stream", file), Path.Combine(source, file), true);
            string archive = Path.Combine(area, "stream.ajnaddon"); await DeveloperTools.BuildAsync(source, compiler, archive);
            var package = AddonPackage.Load(archive); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            new AddonRegistry(data).Install(package, package.Manifest.Permissions);
            new AddonSettings(data, package.Manifest).Update(new JsonObject { ["container"] = "matroska" });
            int port = ListenerPort(); new ListenerSelections(data).Approve(package, grant, "loopback", new("127.0.0.1", port, [], []));
            var provider = new StreamFixtureProvider(); var registry = new SessionRegistry(provider, 1, 1);
            await using var worker = await AddonWorker.StartAsync(package, grant, runtime, Path.Combine(area, "workers"), data,
                new WorkerCommand(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]), sessions: registry);
            async Task<JsonNode?> Action(string id) => await worker.SendEventAsync("action", new JsonObject { ["id"] = id });
            await worker.SendEventAsync("start"); await Action("open");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while ((await Action("status"))?["listener"]?["state"]?.GetValue<string>() != "listening") await Task.Delay(25, deadline.Token);
            var started = await Action("play"); True(started?["stream"] is JsonObject, started?.ToJsonString() ?? "No stream response");
            await Until(() => provider.Last is not null);
            provider.Last!.Complete = true;
            while ((await Action("status"))?["stream"]?["state"]?.GetValue<string>() != "producerCompleted") await Task.Delay(25, deadline.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var page = JsonNode.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/segments"))!;
            string generation = page["generationId"]!.GetValue<string>(), resource = page["segments"]![0]!["resourceId"]!.GetValue<string>();
            byte[] media = await client.GetByteArrayAsync($"http://127.0.0.1:{port}/media/{generation}/{resource}");
            True(media.SequenceEqual(new byte[] { 1, 2 }), worker.Diagnostics);
            var replacing = await Action("replace"); True(replacing?["replacing"]?.GetValue<bool>() == true);
            string? next;
            do { await Task.Delay(25, deadline.Token); next = (await Action("status"))?["stream"]?["generationId"]?.GetValue<string>(); }
            while (next is null || next == generation);
            True(provider.Active == 1);
            using var old = await client.GetAsync($"http://127.0.0.1:{port}/media/{generation}/{resource}");
            True(old.StatusCode == System.Net.HttpStatusCode.Conflict, worker.Diagnostics);
            await Action("close"); await worker.SendEventAsync("stop");
        });
    }
}
