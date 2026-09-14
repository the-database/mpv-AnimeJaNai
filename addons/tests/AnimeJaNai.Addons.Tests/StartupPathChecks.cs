using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

internal static partial class Checks
{
    private static async Task StartupPathChecks()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Test("Restart recovers relocated worker modules without touching another data root", () =>
        {
            string deep = Area();
            while (deep.Length < 300) deep = Path.Combine(deep, new string('r', 60));
            string firstData = Path.Combine(deep, "first"), secondData = Path.Combine(deep, "second");
            var created = new List<string>();
            string Worker(string data, string file)
            {
                string directory = WorkerBridge.CreateWorkDirectory(Path.Combine(data, "workers"), "worker");
                created.Add(directory); File.WriteAllText(Path.Combine(directory, file), "test-owned disposable content");
                return directory;
            }
            try
            {
                string first = Worker(firstData, "module.wasm"), other = Worker(secondData, "module.wasm"), unfamiliar = Worker(firstData, "keep.txt");
                using (var lease = new HostLease(firstData))
                {
                    True(!Directory.Exists(first), "Relocated stale worker survived restart cleanup");
                    True(Directory.Exists(other) && File.Exists(Path.Combine(unfamiliar, "keep.txt")), "Cleanup crossed data ownership or removed unfamiliar content");
                }
                using var second = new HostLease(secondData);
                True(!Directory.Exists(other));
            }
            finally
            {
                foreach (string directory in created)
                    if (Directory.Exists(directory))
                    {
                        File.Delete(Path.Combine(directory, "module.wasm")); File.Delete(Path.Combine(directory, "keep.txt"));
                        Directory.Delete(directory); // Only these new, non-recursive fixture directories.
                    }
            }
        });
        await Test("Deep data uses disposable short work paths and reports unsupported launch paths", async () =>
        {
            string data = Area();
            while (data.Length < 300) data = Path.Combine(data, new string('d', 60));
            Directory.CreateDirectory(data);
            string executable = Environment.ProcessPath!;
            string work = WorkerBridge.CreateWorkDirectory(data, "worker");
            try
            {
                True(work.Length < 200 && Path.GetDirectoryName(work) == WorkerBridge.ShortWorkRoot(data));
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
