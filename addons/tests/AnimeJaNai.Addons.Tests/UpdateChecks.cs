using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;
using AnimeJaNai.Updates;
using System.Text.Json.Nodes;

internal static partial class Checks
{
    private static async Task UpdateChecks()
    {
        await Test("Normal updates remove addon support, retain user data, and can roll back an interruption", async () =>
        {
            string source = Area(), target = Area();
            Directory.CreateDirectory(Path.Combine(target, "addon-host/runtime"));
            Directory.CreateDirectory(Path.Combine(target, "animejanai/addons"));
            string data = Path.Combine(target, "animejanai/addons/saved.json");
            File.WriteAllText(data, "keep");
            File.WriteAllText(Path.Combine(target, "addon-host/runtime/wasmtime.exe"), "runtime");
            File.WriteAllText(Path.Combine(target, "extra-native.dll"), "addon native");
            string nativeHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(target, "extra-native.dll"))));
            File.WriteAllText(Path.Combine(target, "addon-package.json"), new JsonObject { ["files"] = new JsonObject { ["extra-native.dll"] = nativeHash } }.ToJsonString());
            foreach (string folder in new[] { "addon-development", "build-info", "portable_config/scripts" })
                Directory.CreateDirectory(Path.Combine(target, folder));
            File.WriteAllText(Path.Combine(target, "addon-development/guide.html"), "old guide");
            File.WriteAllText(Path.Combine(target, "build-info/old.json"), "old metadata");
            File.WriteAllText(Path.Combine(target, "portable_config/scripts/animejanai_addons.lua"), "old bridge");
            Directory.CreateDirectory(Path.Combine(source, "portable_config/scripts"));
            File.WriteAllText(Path.Combine(source, "portable_config/scripts/animejanai_addons.lua"), "normal release bridge");
            File.WriteAllText(Path.Combine(target, "player.bin"), "old");
            File.WriteAllText(Path.Combine(source, "player.bin"), "new");
            File.WriteAllText(Path.Combine(target, "unchanged.bin"), "same");
            File.WriteAllText(Path.Combine(source, "unchanged.bin"), "same");
            File.SetLastWriteTimeUtc(Path.Combine(target, "unchanged.bin"), new DateTime(2020, 1, 1));
            try { await AddonUpdateTransaction.ApplyAsync(source, target, new HashSet<string>(), afterPublish: n =>
                { if (n == 2) throw new IOException("Interrupted downgrade"); }); }
            catch (IOException error) when (error.Message == "Interrupted downgrade") { }
            True(InstallationActivity.Pending(target));
            await AddonUpdateTransaction.RecoverAsync(target);
            True(File.Exists(Path.Combine(target, "addon-host/runtime/wasmtime.exe")) && File.ReadAllText(Path.Combine(target, "player.bin")) == "old");
            await AddonUpdateTransaction.ApplyAsync(source, target, new HashSet<string>());
            True(!Directory.Exists(Path.Combine(target, "addon-host")) && !File.Exists(Path.Combine(target, "addon-package.json")));
            True(File.ReadAllText(data) == "keep" && File.ReadAllText(Path.Combine(target, "player.bin")) == "new");
            True(File.GetLastWriteTimeUtc(Path.Combine(target, "unchanged.bin")).Year == 2020);
            True(File.ReadAllText(Path.Combine(target, "portable_config/scripts/animejanai_addons.lua")) == "normal release bridge");
            True(!Directory.Exists(Path.Combine(target, "addon-development")) && !Directory.Exists(Path.Combine(target, "build-info")) && !File.Exists(Path.Combine(target, "extra-native.dll")));
        });
        await Test("Normal downgrade preserves an unchanged bridge supplied by the incoming release", async () =>
        {
            string source = Area(), target = Area();
            File.WriteAllText(Path.Combine(target, "addon-package.json"), "{\"files\":{}}");
            foreach (string root in new[] { source, target })
            {
                Directory.CreateDirectory(Path.Combine(root, "portable_config/scripts"));
                File.WriteAllText(Path.Combine(root, "portable_config/scripts/animejanai_addons.lua"), "same bridge");
            }
            await AddonUpdateTransaction.ApplyAsync(source, target, new HashSet<string>());
            True(File.ReadAllText(Path.Combine(target, "portable_config/scripts/animejanai_addons.lua")) == "same bridge");
        });
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
