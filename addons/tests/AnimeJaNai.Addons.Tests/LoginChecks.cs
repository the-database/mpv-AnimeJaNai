using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

internal static partial class Checks
{
    private static LoginSettings TestLoginSettings(string root, MemoryLoginStore store)
    {
        string host = Directory.CreateDirectory(Path.Combine(root, "addon-host")).FullName;
        File.WriteAllBytes(Path.Combine(host, "ajn-addon-launcher.exe"), []);
        return new(root, Path.Combine(root, "animejanai", "addons"), store);
    }
    private sealed class MemoryLoginStore : ILoginStartupStore
    {
        public readonly Dictionary<string, string> Values = [];
        public bool FailWrite;
        public int Writes;
        public string? Read(string name) => Values.GetValueOrDefault(name);
        public void Write(string name, string command) { if (FailWrite) throw new IOException("Simulated registration error"); Values[name] = command; Writes++; }
        public void Delete(string name) { if (FailWrite) throw new IOException("Simulated registration error"); Values.Remove(name); Writes++; }
    }
    private static async Task LoginChecks()
    {
        await Test("Login startup stays off by default, survives reopen and changes only the installation's own entry", () =>
        {
            var store = new MemoryLoginStore(); store.Values["Other application"] = "untouched";
            string area = Area(); var login = TestLoginSettings(area, store);
            True(!login.IsEnabled && login.IsAvailable && store.Writes == 0);
            login.Update(true); True(login.IsEnabled && TestLoginSettings(area, store).IsEnabled);
            var second = TestLoginSettings(Area(), store); True(!second.IsEnabled);
            second.Update(true); login.Update(false); True(!login.IsEnabled && second.IsEnabled);
            second.Update(false); True(store.Values.Count == 1 && store.Values["Other application"] == "untouched");
        });
        await Test("Login registration preserves the previous choice on write failure and shows a moved installation", () =>
        {
            var store = new MemoryLoginStore(); string area = Area(); var login = TestLoginSettings(area, store);
            login.Update(true); string prior = store.Values.Values.Single(); store.FailWrite = true;
            bool failed = false; try { login.Update(false); } catch (IOException) { failed = true; }
            True(failed && login.IsEnabled && store.Values.Values.Single() == prior);
            store.FailWrite = false; store.Values[store.Values.Keys.Single()] = "Previous installation";
            True(!login.IsEnabled && login.Describe()["registeredElsewhere"]!.GetValue<bool>());
            login.Update(false); True(store.Values.Values.Single() == "Previous installation");
            login.Update(true); True(login.IsEnabled);
        });
        await Test("Login command arguments preserve Windows paths and missing launchers cannot be enabled", async () =>
        {
            True(LoginSettings.Quote("C:\\") == "\"C:\\\\\"");
            True(LoginSettings.Quote("a b\\c") == "\"a b\\c\"");
            True(LoginSettings.Quote("a\\\"b") == "\"a\\\\\\\"b\"");
            string area = Area(); var store = new MemoryLoginStore(); var login = new LoginSettings(area, Path.Combine(area, "data"), store);
            await Error("feature_unavailable", () => login.Update(true)); True(store.Writes == 0 && !login.IsEnabled);
            _ = TestLoginSettings(area, store);
            var tooLong = new LoginSettings(area, Path.Combine(area, new string('x', 190)), store);
            await Error("startup_path_too_long", () => tooLong.Update(true)); True(store.Writes == 0);
        });
        await Test("Only an explicit trusted login preference admits login activation, and disabling it preserves player work", async () =>
        {
            string area = Area(); var store = new MemoryLoginStore(); var login = TestLoginSettings(area, store);
            var package = AddonPackage.Create(Manifest() with { Activation = ["on_login", "on_player"] }, EmptyModule);
            new AddonRegistry(area).Install(package, []);
            await using var service = new AddonService(area, (_, _, _, _) => Task.FromResult<IAddonInstance>(new FakeAddon()), loginSettings: login);
            async Task Hello(string client, string kind) => _ = await service.InvokeAsync(client, "lifecycle.hello", new() { ["major"] = 1L, ["kind"] = kind }, default);
            await Error("login_disabled", () => Hello("login", "on_login")); True(!service.HasRunningWorkers);
            await service.InvokeAsync("manager", "manager.hello", new() { ["major"] = 1L }, default);
            await service.InvokeAsync("manager", "host.configureLogin", new() { ["enabled"] = true }, default);
            await Hello("login", "on_login"); await Hello("player", "on_player"); True(service.HasRunningWorkers);
            await Error("management_denied", () => service.InvokeAsync("player", "host.configureLogin", new() { ["enabled"] = false }, default));
            await service.InvokeAsync("manager", "host.configureLogin", new() { ["enabled"] = false }, default);
            True(!login.IsEnabled && service.HasRunningWorkers);
            await Error("handshake_required", () => service.InvokeAsync("login", "lifecycle.ping", new(), default));
            await service.DisconnectAsync("player"); True(!service.HasRunningWorkers);
            await using var broker = new Broker(package, new(package, []), area);
            await Error("unknown_method", () => broker.InvokeAsync("host.configureLogin", new() { ["enabled"] = true }, default));
        });
        await Test("Removing startup outside Manager releases the login connection on its next heartbeat", async () =>
        {
            string area = Area(); var store = new MemoryLoginStore(); var login = TestLoginSettings(area, store); login.Update(true);
            var package = AddonPackage.Create(Manifest() with { Activation = ["on_login"] }, EmptyModule); new AddonRegistry(area).Install(package, []);
            await using var service = new AddonService(area, (_, _, _, _) => Task.FromResult<IAddonInstance>(new FakeAddon()), loginSettings: login);
            await service.InvokeAsync("login", "lifecycle.hello", new() { ["major"] = 1L, ["kind"] = "on_login" }, default);
            True(service.HasRunningWorkers); store.Values.Clear();
            True(!(await service.InvokeAsync("login", "lifecycle.ping", new(), default))!["connected"]!.GetValue<bool>());
            True(!service.HasRunningWorkers);
        });
    }
}
