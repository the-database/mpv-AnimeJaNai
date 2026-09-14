using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

internal static partial class Checks
{
    private static async Task StartupPathChecks()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Test("Deep data uses disposable short work paths and reports unsupported launch paths", async () =>
        {
            string data = Area();
            while (data.Length < 300) data = Path.Combine(data, new string('d', 60));
            Directory.CreateDirectory(data);
            string executable = Environment.ProcessPath!;
            string work = WorkerBridge.CreateWorkDirectory(data, "worker");
            try
            {
                True(work.Length < 200 && Path.GetDirectoryName(work) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
                var info = WorkerBridge.ProcessInfo(executable, work);
                True(info.WorkingDirectory == work);
                True(info.Environment["TEMP"] == work && info.Environment["TMP"] == work);
                True(!info.UseShellExecute && info.CreateNoWindow);
            }
            finally { Directory.Delete(work); }
            string shortDirectory = Environment.SystemDirectory;
            True(WorkerBridge.ProcessInfo(executable, shortDirectory).WorkingDirectory == shortDirectory);
            await Error("startup_path_too_long", () => WorkerBridge.ProcessInfo(Path.Combine(data, "host.exe"), data));
            await Error("startup_path_too_long", () => WorkerBridge.ProcessInfo(executable, data));
        });
    }

    private static async Task RuntimePathChecks(string runtime, string compiler, string dotnet)
    {
        await Test("Deep source and data paths compile and run actual Wasm with durable state", async () =>
        {
            string data = Area();
            while (data.Length < 300) data = Path.Combine(data, new string('p', 60));
            string source = Path.Combine(data, "source");
            DeveloperTools.New(source, "org.example.longpath");
            var package = await DeveloperTools.BuildAsync(source, compiler, Path.Combine(data, "compiled.ajnaddon"));
            var grant = new PermissionGrant(package, ["log.write", "storage.read", "storage.write"]);
            var command = new WorkerCommand(Path.GetFullPath(dotnet), [typeof(AddonWorker).Assembly.Location]);
            for (int count = 1; count <= 2; count++)
            {
                await using var worker = await AddonWorker.StartAsync(package, grant, runtime,
                    Path.Combine(data, "workers"), data, command);
                var result = await worker.SendEventAsync("start");
                Same(JsonValue.Create(count), result!["starts"]);
            }
            Same(JsonValue.Create(2), new AddonStorage(data, package.Manifest.Id).Get("starts"));
        });
    }
}
