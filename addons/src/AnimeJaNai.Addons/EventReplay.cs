using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed record ReplayEvent(string Name, JsonNode? Data);

public static class EventReplay
{
    public static IReadOnlyList<ReplayEvent> Load(string path)
    {
        var document = Contract.ParseObject(AddonPackage.ReadBoundedFile(path, 1024 * 1024));
        Contract.Require(Contract.Number(document, "schemaVersion") == 1 && document["events"] is JsonArray { Count: <= 256 },
            "invalid_replay", "Expected replay schema 1 and at most 256 events.");
        List<ReplayEvent> events = [];
        foreach (var item in (JsonArray)document["events"]!)
        {
            Contract.Require(item is JsonObject, "invalid_replay", "Invalid replay event.");
            var obj = (JsonObject)item;
            string name = Contract.Text(obj, "name", 64);
            Contract.Require(Contract.ValidKey(name) && name != "host.ping", "invalid_replay", "Invalid replay event name.");
            Contract.Require(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(obj).Length < Contract.MaxMessageBytes - 512,
                "invalid_replay", "Replay event exceeds the transport limit.");
            events.Add(new(name, obj["data"]?.DeepClone()));
        }
        return events;
    }

    // Explicit developer input only. No automatic capture of media, settings,
    // credentials, URLs, or frames from a user's playback session.
    public static async Task<JsonArray> RunAsync(IAddonInstance instance, IReadOnlyList<ReplayEvent> events, CancellationToken cancellationToken = default)
    {
        Contract.Require(events.Count <= 256, "invalid_replay", "Too many replay events.");
        var results = new JsonArray();
        foreach (var item in events)
        {
            var timer = Stopwatch.StartNew();
            var value = await instance.SendEventAsync(item.Name, item.Data, cancellationToken);
            results.Add(new JsonObject { ["name"] = item.Name, ["result"] = value?.DeepClone(), ["elapsedMilliseconds"] = timer.Elapsed.TotalMilliseconds });
        }
        return results;
    }
}
