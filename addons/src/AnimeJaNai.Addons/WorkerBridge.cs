using System.Diagnostics;
using System.Security.Cryptography;

namespace AnimeJaNai.Addons;

public sealed record WorkerCommand(string Executable, string[] PrefixArguments)
{
    public static WorkerCommand Current()
    {
        string executable = Environment.ProcessPath ?? throw new AddonException("host_path", "Cannot locate addon host.");
        return new(executable, Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [typeof(WorkerBridge).Assembly.Location] : []);
    }
}

public static class WorkerBridge
{
    public const string RuntimeHash = "37045d920f7abbd202f0ea8c52ae9bbc0e49bcdca47e70f37649d1dc0ae1cff5";
    public static void VerifyRuntime(string path)
    {
        using var stream = File.OpenRead(path);
        Contract.Require(Convert.ToHexStringLower(SHA256.HashData(stream)) == RuntimeHash,
            "runtime_mismatch", "Expected the pinned Windows x64 Wasmtime 48.0.2 runtime. Run tools/bootstrap.ps1.");
    }

    internal static ProcessStartInfo ProcessInfo(string executable, string directory)
    {
        var info = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        info.Environment.Clear();
        info.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        info.Environment["TEMP"] = directory;
        info.Environment["TMP"] = directory;
        return info;
    }

    // This trusted bridge cannot start Wasmtime until the parent has attached
    // it to the resource job and sent the gate byte. Its child inherits the job.
    public static async Task<int> RunAsync(string runtime, string module)
    {
        var input = Console.OpenStandardInput();
        byte[] gate = new byte[1];
        if (await input.ReadAsync(gate) != 1 || gate[0] != 1) return 2;
        VerifyRuntime(runtime);
        string directory = Path.GetDirectoryName(Path.GetFullPath(module))!;
        var info = ProcessInfo(runtime, directory);
        foreach (string arg in new[]
        {
            "run", "-C", "cache=n,parallel-compilation=n", "-O", "opt-level=1",
            "-W", "max-memory-size=67108864,max-table-elements=100000,max-memories=1,max-instances=1,max-tables=1,threads=n,shared-memory=n,memory64=n,component-model=n,unknown-imports-trap=n,unknown-imports-default=n",
            "-S", "cli=y,inherit-env=n,inherit-network=n,allow-ip-name-lookup=n,tcp=n,udp=n,http=n,nn=n,threads=n,listenfd=n,config=n,keyvalue=n,tls=n,p3=n,max-resources=64,max-random-size=65536",
            "--argv0", "addon", Path.GetFullPath(module)
        }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new AddonException("worker_start", "Could not start Wasmtime.");
        var stdin = ForwardInputAsync(input, process);
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        var stderr = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        // The supervisor owns shutdown. Do not wait for stdin after the runtime exits.
        _ = stdin.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return process.ExitCode;
    }

    private static async Task ForwardInputAsync(Stream input, Process process)
    {
        try { await input.CopyToAsync(process.StandardInput.BaseStream); }
        finally { process.StandardInput.Close(); }
    }
}
