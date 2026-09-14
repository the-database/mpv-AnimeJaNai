using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;
using AnimeJaNai.Updates;
using System.Text.Json.Nodes;

internal static partial class Checks
{
    private static async Task UpdateChecks()
    {
        await Test("Component removal recovers its files and installed-state record together", async () =>
        {
            string source = Area(), target = Area();
            Directory.CreateDirectory(Path.Combine(target, "animejanai/inference"));
            string runtime = Path.Combine(target, "animejanai/inference/component.bin");
            File.WriteAllText(runtime, "component");
            File.WriteAllText(Path.Combine(target, "components.json"), "old state");
            File.WriteAllText(Path.Combine(source, "components.json"), "new state");
            bool failed = false;
            try { await AddonUpdateTransaction.ApplyAsync(source, target, new HashSet<string>(), afterPublish: n =>
                { if (n == 2) throw new IOException("Interrupted component change"); }, component: true, removals: ["animejanai/inference/component.bin"]); }
            catch (IOException error) when (error.Message == "Interrupted component change") { failed = true; }
            True(failed && !File.Exists(runtime));
            await AddonUpdateTransaction.RecoverAsync(target);
            True(File.ReadAllText(runtime) == "component" && File.ReadAllText(Path.Combine(target, "components.json")) == "old state");
            await AddonUpdateTransaction.ApplyAsync(source, target, new HashSet<string>(), component: true, removals: ["animejanai/inference/component.bin"]);
            True(!File.Exists(runtime) && File.ReadAllText(Path.Combine(target, "components.json")) == "new state");
        });
        await Test("Interrupted replacement blocks activation and recovery restores the complete prior file set", async () =>
        {
            string source = Area(), target = Area();
            File.WriteAllText(Path.Combine(target, "old.txt"), "old");
            File.WriteAllText(Path.Combine(source, "old.txt"), "new");
            File.WriteAllText(Path.Combine(source, "new.txt"), "new file");
            string data = Directory.CreateDirectory(Path.Combine(target, "animejanai/addons/data")).FullName;
            File.WriteAllText(Path.Combine(data, "saved.json"), "saved");
            File.WriteAllText(Path.Combine(source, "user.conf"), "default");
            File.WriteAllText(Path.Combine(target, "user.conf"), "custom");
            bool interrupted = false;
            try { await AddonUpdateTransaction.ApplyAsync(source, target, new HashSet<string> { "user.conf" }, afterPublish: _ => throw new IOException("Interruption")); }
            catch (IOException error) when (error.Message == "Interruption") { interrupted = true; }
            True(interrupted && InstallationActivity.Pending(target));
            try { using var forbidden = InstallationActivity.Acquire(target); throw new Exception("Activation was allowed during interrupted update"); }
            catch (ManagementException error) when (error.Code == "update_in_progress") { }
            await AddonUpdateTransaction.RecoverAsync(target);
            True(!InstallationActivity.Pending(target) && File.ReadAllText(Path.Combine(target, "old.txt")) == "old");
            True(!File.Exists(Path.Combine(target, "new.txt")) && File.ReadAllText(Path.Combine(target, "user.conf")) == "custom");
            True(File.ReadAllText(Path.Combine(data, "saved.json")) == "saved");
            await AddonUpdateTransaction.ApplyAsync(source, target, new HashSet<string> { "user.conf" });
            True(File.ReadAllText(Path.Combine(target, "old.txt")) == "new" && File.Exists(Path.Combine(target, "new.txt")));
            True(!InstallationActivity.Pending(target) && File.ReadAllText(Path.Combine(target, "user.conf")) == "custom");
            await AddonUpdateTransaction.RecoverAsync(target); // Idempotent after successful commit.
        });
        await Test("Update intent drains a running addon host and excludes concurrent activation across replacement", async () =>
        {
            string source = Area(), target = Area(), data = Directory.CreateDirectory(Path.Combine(target, "animejanai/addons")).FullName;
            File.WriteAllText(Path.Combine(source, "managed.txt"), "new");
            var package = AddonPackage.Create(Manifest() with { Activation = ["on_manager"] }, EmptyModule);
            new AddonRegistry(data).Install(package, []);
            await using var service = new AddonService(data, (_, _, _, _) => Task.FromResult<IAddonInstance>(new FakeAddon()));
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var run = new ManagementServer(data, service, target).RunAsync(cancel.Token, exitWhenIdle: false);
            await using var client = await ManagementClient.ConnectAsync(data, cancel.Token);
            True(service.HasRunningWorkers);
            bool excluded = false;
            await AddonUpdateTransaction.ApplyAsync(source, target, new HashSet<string>(), cancel.Token, afterPublish: _ =>
            {
                try { using var contender = InstallationActivity.Acquire(target); }
                catch (ManagementException error) when (error.Code == "update_in_progress") { excluded = true; }
            });
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            True(excluded && !service.HasRunningWorkers && !InstallationActivity.Pending(target));
            using var restarted = InstallationActivity.Acquire(target);
            True(File.ReadAllText(Path.Combine(target, "managed.txt")) == "new");
        });
        await Test("Incomplete addon inventory is rejected before installed files change", async () =>
        {
            string source = Area(), target = Area();
            File.WriteAllText(Path.Combine(target, "old.txt"), "old");
            File.WriteAllText(Path.Combine(source, "addon-package.json"), new JsonObject {
                ["schemaVersion"] = 1, ["platform"] = "win-x64", ["files"] = new JsonObject() }.ToJsonString());
            bool rejected = false;
            try { await AddonUpdateTransaction.ApplyAsync(source, target, new HashSet<string>()); }
            catch (IOException) { rejected = true; }
            True(rejected && !InstallationActivity.Pending(target) && File.ReadAllText(Path.Combine(target, "old.txt")) == "old");
        });
    }
}
