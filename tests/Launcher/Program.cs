// Scripted checks for the mpv-animejanai launcher, on Windows (WindowsTests.cs) and Linux
// (LinuxTests.cs).
//
//   dotnet run --project tests/Launcher -- <release tree> <launcher dir> [--only registry]
//
// <release tree>: an extracted mpv-upscale-2x_animejanai package (a scratch copy: the launcher is
// copied into it). <launcher dir>: the published launcher (Windows: mpv-animejanai.exe and
// mpv-animejanai.com; Linux: mpv-animejanai).
// Players run against a scratch config dir (paused, muted, small window), on lavfi test sources,
// so nothing of the user's install or config is used.
// --only registry runs just the checks that need no player and no desktop (CI): the registry ones
// on Windows; on Linux the .desktop / mimeapps.list ones and the dialog through stub programs.
//
// Windows: player windows open on the desktop while it runs.
// Linux: headless is fine (the scratch config has vo=null, ao=null). The tree needs only mpv/mpv:
// an extracted package, or a folder with a copy of (or a symlink to) any mpv build as mpv/mpv;
// when that build needs libraries from elsewhere, name their folder in ANIMEJANAI_EXTRA_LIBS (the
// launcher puts it on the player's LD_LIBRARY_PATH; the checks also verify it gets there).
// desktop-file-validate is used when installed.
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run --project tests/Launcher -- <release tree> <launcher dir> [--only registry]");
    return 2;
}
string tree = Path.GetFullPath(args[0]);
string launcherDir = Path.GetFullPath(args[1]);
bool onlyRegistry = args.Length > 3 && args[2] == "--only" && args[3] == "registry";
if (OperatingSystem.IsWindows())
{
    return new WindowsTests(tree, launcherDir).Run(onlyRegistry);
}
if (OperatingSystem.IsLinux())
{
    return new LinuxTests(tree, launcherDir).Run(onlyRegistry);
}
Console.Error.WriteLine("Windows and Linux only.");
return 2;

/// Check bookkeeping and the scratch config both platforms use.
abstract class TestBase
{
    protected readonly string config = Path.Combine(Path.GetTempPath(), "ajn-launcher-test-" + Guid.NewGuid().ToString("N")[..8], "portable_config");
    int failed, passed;

    /// The launcher's instance name for the scratch config (Install.InstanceName).
    protected string InstanceName => "mpv-animejanai-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(config.ToLowerInvariant())))[..8];

    protected void Section(string name, Action body)
    {
        Console.WriteLine($"\n== {name}");
        try
        {
            body();
        }
        catch (Exception e)
        {
            Check(false, $"threw: {e.GetType().Name}: {e.Message}");
        }
    }

    protected void Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
        if (ok) passed++; else failed++;
    }

    protected int Summary()
    {
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    protected static int PlaylistCount(Ipc ipc) => ipc.Get("playlist-count")?.GetValue<int>() ?? -1;
}

/// Minimal mpv JSON IPC client for the checks: the instance pipe on Windows, its Unix socket on
/// Linux ($XDG_RUNTIME_DIR/<name>.sock).
sealed class Ipc : IDisposable
{
    readonly Stream stream;
    readonly StreamReader reader;
    int id;

    Ipc(Stream s)
    {
        stream = s;
        reader = new StreamReader(s, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    }

    public static Ipc? Connect(string name, int timeoutMs, string? runtimeDir = null)
    {
        if (OperatingSystem.IsWindows())
        {
            var p = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            try
            {
                p.Connect(timeoutMs);
                return new Ipc(p);
            }
            catch (Exception)
            {
                p.Dispose();
                return null;
            }
        }
        string path = Path.Combine(runtimeDir ?? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp", name + ".sock");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Connect(new UnixDomainSocketEndPoint(path));
                return new Ipc(new NetworkStream(socket, ownsSocket: true));
            }
            catch (SocketException)
            {
                socket.Dispose();
                if (sw.ElapsedMilliseconds >= timeoutMs)
                {
                    return null;
                }
                Thread.Sleep(20);
            }
        }
    }

    public JsonNode? Get(string property)
    {
        int rid = ++id;
        var line = Encoding.UTF8.GetBytes($"{{\"command\":[\"get_property\",\"{property}\"],\"request_id\":{rid}}}\n");
        stream.Write(line);
        stream.Flush();
        while (reader.ReadLine() is { } reply)
        {
            var node = JsonNode.Parse(reply);
            if (node?["request_id"]?.GetValue<int>() != rid)
            {
                continue;
            }
            if (node["error"]?.GetValue<string>() != "success")
            {
                throw new InvalidOperationException($"{property}: {node["error"]}");
            }
            return node["data"];
        }
        return null;
    }

    public void Dispose()
    {
        reader.Dispose();
        stream.Dispose();
    }
}
