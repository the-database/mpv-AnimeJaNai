using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;
using AnimeJaNai.Updates;

internal static class NativeArchiveUpdateChecks
{
    internal static async Task RunAsync(string output, string previousArchive, string currentArchive, List<JsonObject> evidence)
    {
        string previous = Path.Combine(output, "previous"), current = Path.Combine(output, "current"), target = Path.Combine(output, "installation");
        foreach (var (archive, directory) in new[] { (previousArchive, previous), (currentArchive, current), (previousArchive, target) })
            ZipFile.ExtractToDirectory(Path.GetFullPath(archive), directory);
        string data = Path.Combine(target, "animejanai", "addons");
        var oldAddon = AddonPackage.Load(Path.Combine(previous, "addon-development", "counter.ajnaddon"));
        var newAddon = AddonPackage.Load(Path.Combine(current, "addon-development", "counter.ajnaddon"));
        var listenerAddon = AddonPackage.Load(Path.Combine(current, "addon-development", "http-inspector.ajnaddon"));
        Require(oldAddon.Hash != newAddon.Hash, "Choose archives containing different counter builds to exercise renewed approval.");
        var registry = new AddonRegistry(data); registry.Install(oldAddon, oldAddon.Manifest.Permissions);
        JsonObject Id() => new() { ["id"] = oldAddon.Manifest.Id };
        Task<ManagementClient> Connect() => ManagementClient.ConnectOrStartAsync(data,
            Path.Combine(target, "addon-host", "ajn-addon.exe"), Path.Combine(target, "addon-host", "runtime", "wasmtime.exe"));
        async Task VerifyState(ManagementClient client)
        {
            await client.CallAsync("addons.start", Id());
            var settings = await client.CallAsync("addons.settings", Id());
            Require(settings?["values"]?["greeting"]?.GetValue<string>() == "Survives full archive replacement", "Archive update lost settings.");
            Require(new AddonStorage(data, oldAddon.Manifest.Id).Get("archive-marker")?.GetValue<string>() == "preserved", "Archive update lost addon storage.");
        }
        async Task Apply(string staged, string name)
        {
            var info = new ProcessStartInfo(Path.Combine(target, "AnimeJaNaiUpdater.exe")) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "--apply-staged", target, staged }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5)); }
            catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
            File.WriteAllText(Path.Combine(output, name + ".log"), await stdout + "\n" + await stderr);
            Require(process.ExitCode == 0 && !InstallationActivity.Pending(target), "Actual archive replacement failed: " + name);
            var inventory = JsonNode.Parse(File.ReadAllText(Path.Combine(staged, "addon-package.json")))!["files"]!.AsObject();
            foreach (var entry in inventory)
            {
                using var file = File.OpenRead(Path.Combine(target, entry.Key));
                Require(Convert.ToHexStringLower(SHA256.HashData(file)) == entry.Value!.GetValue<string>(), "Updated inventory differs: " + entry.Key);
            }
        }
        ManagementClient? client = null;
        try
        {
            client = await Connect();
            await client.CallAsync("addons.configure", new() { ["id"] = oldAddon.Manifest.Id,
                ["changes"] = new JsonObject { ["greeting"] = "Survives full archive replacement" } });
            new AddonStorage(data, oldAddon.Manifest.Id).Set("archive-marker", JsonValue.Create("preserved"));
            await VerifyState(client);
            await Apply(current, "upgrade"); await client.DisposeAsync(); client = await Connect();
            await VerifyState(client);
            Require(client.ServerInfo["httpServerAvailable"]?.GetValue<bool>() == true, "Upgrade did not load the new host.");
            Require(registry.Load(oldAddon.Manifest.Id).Package.Hash == oldAddon.Hash, "Application update silently replaced the installed addon.");
            string path = Path.Combine(current, "addon-development", "counter.ajnaddon");
            try
            {
                await client.CallAsync("addons.installDev", new() { ["path"] = path, ["expectedHash"] = new string('0', 64), ["permissions"] = new JsonArray() });
                throw new Exception("Unreviewed package hash was accepted.");
            }
            catch (ManagementException error) when (error.Code == "integrity_mismatch") { }
            await client.CallAsync("addons.installDev", new() { ["path"] = path, ["expectedHash"] = newAddon.Hash,
                ["permissions"] = new JsonArray("storage.read") });
            Require(registry.Load(oldAddon.Manifest.Id).Grant.Allowed.SequenceEqual(new[] { "storage.read" }), "New package inherited unapproved permissions.");
            await client.CallAsync("addons.rollback", Id());
            Require(registry.Load(oldAddon.Manifest.Id).Package.Hash == oldAddon.Hash, "Addon rollback did not restore its previously reviewed build.");
            await VerifyState(client);
            await client.CallAsync("addons.installDev", new() { ["path"] = Path.Combine(current, "addon-development", "http-inspector.ajnaddon"),
                ["expectedHash"] = listenerAddon.Hash, ["permissions"] = new JsonArray() });
            await Apply(previous, "rollback"); await client.DisposeAsync(); client = await Connect();
            await VerifyState(client);
            try
            {
                await client.CallAsync("addons.start", new() { ["id"] = listenerAddon.Manifest.Id });
                throw new Exception("Old host started an addon requiring API 1.7.");
            }
            catch (ManagementException error) when (error.Code == "incompatible_api") { }
            evidence.Add(new() { ["fullArchiveRoundTrip"] = true, ["settingsAndStoragePreserved"] = true,
                ["reviewedPackageHashRequired"] = true, ["newPackageDidNotInheritGrants"] = true,
                ["addonRollbackRestoredReviewedBuild"] = true, ["oldHostRejectedApi17"] = true,
                ["previousArchiveSha256"] = Hash(previousArchive), ["currentArchiveSha256"] = Hash(currentArchive) });
        }
        finally
        {
            await AddonUpdateTransaction.PrepareUninstallAsync(target);
            if (client is not null) await client.DisposeAsync();
            await AddonUpdateTransaction.RecoverAsync(target);
        }
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
