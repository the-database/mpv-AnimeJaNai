using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

internal static partial class Checks
{
    private static async Task HostSettingsChecks()
    {
        await Test("Host session capacity survives restart and preserves additive settings", async () =>
        {
            string area = Area(), path = Path.Combine(area, "host-settings.json");
            var defaults = new HostSettings(area); True(defaults.MaximumConcurrentSessions == 2 && !File.Exists(path));
            File.WriteAllText(path, """{"schemaVersion":1,"maximumConcurrentSessions":4,"futureOption":{"label":"日本語"}}""");
            var settings = new HostSettings(area); True(settings.MaximumConcurrentSessions == 4);
            settings.Update(6); var loaded = new HostSettings(area); True(loaded.MaximumConcurrentSessions == 6);
            True(JsonNode.Parse(File.ReadAllText(path))!["futureOption"]!["label"]!.GetValue<string>() == "日本語");
            string before = File.ReadAllText(path);
            foreach (long bad in new long[] { -1, 0, 17, long.MaxValue }) await Error("invalid_host_settings", () => settings.Update(bad));
            True(File.ReadAllText(path) == before && settings.MaximumConcurrentSessions == 6);
        });
        await Test("Explicit host capacity overrides are visible, read-only and leave saved policy unchanged", async () =>
        {
            string area = Area(); new HostSettings(area).Update(3);
            var settings = new HostSettings(area, 5);
            True(settings.MaximumConcurrentSessions == 5 && !settings.Describe()["editable"]!.GetValue<bool>());
            await Error("host_settings_locked", () => settings.Update(2));
            True(new HostSettings(area).MaximumConcurrentSessions == 3);
        });
        await Test("Invalid host settings fail without silently resetting the resource policy", async () =>
        {
            string area = Area(), path = Path.Combine(area, "host-settings.json");
            foreach (var (text, code) in new[] { ("{broken", "invalid_message"), ("{\"schemaVersion\":2,\"maximumConcurrentSessions\":1}", "unsupported_host_settings"),
                ("{\"schemaVersion\":1,\"maximumConcurrentSessions\":0}", "invalid_host_settings") })
            {
                File.WriteAllText(path, text); await Error(code, () => new HostSettings(area)); True(File.ReadAllText(path) == text);
            }
        });
        await Test("Host capacity is managed through the trusted connection and unavailable to guests", async () =>
        {
            string area = Area(); var package = Package(); var settings = new HostSettings(area);
            await using var service = new AddonService(area, (_, _, _, _) => throw new NotSupportedException(), hostSettings: settings);
            await Error("handshake_required", () => service.InvokeAsync("manager", "host.configure", new() { ["maximumConcurrentSessions"] = 8L }, default));
            var hello = await service.InvokeAsync("manager", "manager.hello", new() { ["major"] = 1L }, default);
            True(hello!["hostSettingsAvailable"]!.GetValue<bool>());
            await service.InvokeAsync("manager", "host.configure", new() { ["maximumConcurrentSessions"] = 3L }, default);
            True(settings.MaximumConcurrentSessions == 3 && new HostSettings(area).MaximumConcurrentSessions == 3);
            await using var broker = new Broker(package, new(package, []), area);
            await Error("unknown_method", () => broker.InvokeAsync("host.configure", new() { ["maximumConcurrentSessions"] = 8L }, default));
            True(settings.MaximumConcurrentSessions == 3);
        });
        if (OperatingSystem.IsWindows()) await Test("A failed host policy save leaves both disk and live admission limit unchanged", () =>
        {
            string area = Area(); var settings = new HostSettings(area); settings.Update(2);
            string path = Path.Combine(area, "host-settings.json");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { settings.Update(4); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
                True(failed && settings.MaximumConcurrentSessions == 2 && new HostSettings(area).MaximumConcurrentSessions == 2);
            }
            settings.Update(4); True(new HostSettings(area).MaximumConcurrentSessions == 4);
            True(!Directory.EnumerateFiles(area, "*.tmp").Any());
        });
    }
}
