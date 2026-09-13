using System.Text.Json;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

try
{
    if (args.Length == 3 && args[0] == "worker") return await WorkerBridge.RunAsync(args[1], args[2]);
    if (args.Length == 0) { Help(); return 0; }
    switch (args[0])
    {
        case "new" when args.Length == 3:
            DeveloperTools.New(args[1], args[2]);
            Console.WriteLine("Created addon source and editor types.");
            return 0;
        case "build" when args.Length == 4:
            var built = await DeveloperTools.BuildAsync(args[1], args[2], args[3]);
            Print(new { built.Manifest, sha256 = built.Hash });
            return 0;
        case "inspect" when args.Length == 2:
            var package = AddonPackage.Load(args[1]);
            Print(new { package.Manifest, sha256 = package.Hash, trust = "Unsigned local development package" });
            return 0;
        case "install-dev" when args.Length is 3 or 4:
            var installing = AddonPackage.Load(args[1]);
            new AddonRegistry(args[2]).Install(installing, args.Length == 4 ? Grants(args[3]) : []);
            Print(new { installed = installing.Manifest.Id, sha256 = installing.Hash, permissions = args.Length == 4 ? Grants(args[3]) : [] });
            return 0;
        case "rollback" when args.Length == 3:
            new AddonRegistry(args[2]).Rollback(args[1]);
            Console.WriteLine("Restored the previous package and its permission grant. Addon data is preserved.");
            return 0;
        case "disable" when args.Length == 3:
            new AddonRegistry(args[2]).Disable(args[1]);
            Console.WriteLine("Disabled addon registration. Stored data is preserved.");
            return 0;
        case "settings" when args.Length == 3:
            var settingsPackage = new AddonRegistry(args[2]).Load(args[1]).Package;
            Print(new { definitions = settingsPackage.Manifest.Settings, values = new AddonSettings(args[2], settingsPackage.Manifest).Get() });
            return 0;
        case "configure" when args.Length == 4:
            var configured = new AddonRegistry(args[2]).Load(args[1]).Package;
            var changes = Contract.ParseObject(AddonPackage.ReadBoundedFile(args[3], 64 * 1024));
            Print(new AddonSettings(args[2], configured.Manifest).Update(changes));
            return 0;
        case "action" when args.Length == 5:
            var (actionPackage, actionGrant) = new AddonRegistry(args[2]).Load(args[1]);
            await using (var activation = new AddonActivation(actionPackage, async token => await AddonWorker.StartAsync(actionPackage, actionGrant,
                args[3], Path.Combine(args[2], "workers"), args[2], WorkerCommand.Current(), cancellationToken: token)))
                Print(await activation.RunActionAsync(args[4]));
            return 0;
        case "replay" when args.Length == 5:
            var events = EventReplay.Load(args[4]);
            var (replayPackage, replayGrant) = new AddonRegistry(args[2]).Load(args[1]);
            await using (var replayWorker = await AddonWorker.StartAsync(replayPackage, replayGrant, args[3], Path.Combine(args[2], "workers"), args[2], WorkerCommand.Current()))
                Print(await EventReplay.RunAsync(replayWorker, events));
            return 0;
        case "run" when args.Length is 4 or 5:
            var (installed, grant) = new AddonRegistry(args[2]).Load(args[1]);
            await using (var worker = await AddonWorker.StartAsync(installed, grant, args[3], Path.Combine(args[2], "workers"), args[2],
                WorkerCommand.Current(), log: message => Console.Error.WriteLine(message)))
            {
                Print(await worker.SendEventAsync(args.Length == 5 ? args[4] : "start"));
            }
            return 0;
        default: Help(); return 2;
    }
}
catch (AddonException error)
{
    Print(new { error = new { code = error.Code, message = error.Message } });
    return 1;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or PlatformNotSupportedException
    or OperationCanceledException or TimeoutException or System.ComponentModel.Win32Exception)
{
    Print(new { error = new { code = "host_error", message = error.Message } });
    return 1;
}

static string[] Grants(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
static void Print(object? value) => Console.WriteLine(JsonSerializer.Serialize(value, Contract.Json));
static void Help() => Console.WriteLine("""
AJN addon developer host (API 1.0 preview)
  new <new-directory> <reverse.domain.id>
  build <source-directory> <javy.exe> <new-package.ajnaddon>
  inspect <package.ajnaddon>
  install-dev <package.ajnaddon> <data-directory> [permission,permission]
  run <addon-id> <data-directory> <wasmtime.exe> [event-name]
  settings <addon-id> <data-directory>
  configure <addon-id> <data-directory> <settings-patch.json>
  action <addon-id> <data-directory> <wasmtime.exe> <action-id>
  replay <addon-id> <data-directory> <wasmtime.exe> <events.json>
  rollback <addon-id> <data-directory>
  disable <addon-id> <data-directory>

No permissions are granted by default. install-dev explicitly opts into a local
unsigned package; the website/catalog installer is a later integration.
""");
