// AnimeJaNaiBenchmark — cross-platform playback-throughput benchmark for the
// native mpv pipeline. Replaces the Windows-only benchmark.ps1 (Win32 job
// objects + named pipes + ShowWindowAsync) with one .NET tool that runs on
// Windows and Linux: it drives the bundled mpv over JSON IPC and samples
// estimated-frame-number, exactly like the old script.
//
// Each cell runs the player uncapped (--untimed) with --vo=null (decode +
// upscale, no presentation), over the bundled clips for the built-in benchmark
// slots (1010 Balanced, 1011 Performance). TensorRT builds an engine per
// resolution on first use (cached after). Cells below the fps floor are
// recorded as -1.
//
// Usage:
//   AnimeJaNaiBenchmark [--install-root DIR] [--slots 1010,1011]
//                       [--fps-floor N] [--out results.json]
// The Manager's "Run Benchmarks" button and the benchmark launcher both call
// this same executable.
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

static string? Arg(string[] a, string name)
{
    int i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

bool isWin = OperatingSystem.IsWindows();

// ---- locate the install tree -------------------------------------------------
// Default: this exe sits at the install root (Windows) or is invoked from it.
string installRoot = Path.GetFullPath(Arg(args, "--install-root")
    ?? Environment.GetEnvironmentVariable("ANIMEJANAI_ROOT")
    ?? AppContext.BaseDirectory);
string animejanai = Path.Combine(installRoot, "animejanai");
string inference = Path.Combine(animejanai, "inference");
string benchDir = Path.Combine(animejanai, "benchmarks");
string onnxDir = Path.Combine(animejanai, "onnx");
string rifeDir = Path.Combine(animejanai, "rife");
string confPath = Path.Combine(animejanai, "animejanai.conf");

if (!Directory.Exists(benchDir))
{
    Console.Error.WriteLine($"Benchmark clips folder not found at {benchDir}");
    return 1;
}

// Platform-varying names (mirror the assembler's Platform descriptor).
string ajiLib = isWin ? "aji.dll" : "libaji.so";
string trtexec = Path.Combine(inference, isWin ? "trtexec.exe" : "trtexec");
string player = isWin
    ? Path.Combine(installRoot, "mpvnet.com")
    : Path.Combine(installRoot, "mpv", "mpv");
if (!File.Exists(player))
{
    Console.Error.WriteLine($"player not found at {player}");
    return 1;
}

string backend = ReadBackend(confPath);
int fpsFloor = int.TryParse(Arg(args, "--fps-floor"), out var ff) ? ff : 8;
int[] slots = (Arg(args, "--slots") ?? "1010,1011")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(int.Parse).ToArray();
// Bundled clips, smallest first (warms engines progressively).
var clips = Directory.GetFiles(benchDir, "*.mp4")
    .OrderBy(f => ClipPixels(Path.GetFileNameWithoutExtension(f)))
    .ToList();
if (clips.Count == 0) { Console.Error.WriteLine("No benchmark clips (*.mp4) found."); return 1; }

Console.WriteLine($"AnimeJaNai playback benchmark — backend: {backend}");
Console.WriteLine($"Runs offscreen (--vo=null). Slots: {string.Join(",", slots)}. fps floor: {fpsFloor}.");
Console.WriteLine("(TensorRT builds an engine per resolution on first run — the first sweep is slow.)\n");

// slot -> catalog row name (the built-in benchmark templates)
static string SlotName(int s) => s switch
{
    1010 => "Balanced",
    1011 => "Performance",
    1012 => "Balanced RIFE 2x (upscale then RIFE)",
    1013 => "Balanced RIFE 2x (RIFE then upscale)",
    _ => $"slot {s}",
};
static string ResLabel(string clipName)
{
    var m = System.Text.RegularExpressions.Regex.Match(clipName, @"\d+x\d+");
    return m.Success ? m.Value : clipName;
}

var resolutions = clips.Select(c => ResLabel(Path.GetFileNameWithoutExtension(c)))
    .Distinct().OrderBy(ClipPixels).ToList();
var table = new Dictionary<string, Dictionary<string, double>>();

foreach (var slot in slots)
{
    string name = SlotName(slot);
    table[name] = new Dictionary<string, double>();
    foreach (var clip in clips)
    {
        string res = ResLabel(Path.GetFileNameWithoutExtension(clip));
        Console.Write($"{name} @ {res,-9} ");
        double fps;
        try { fps = await BenchmarkCell(slot, clip); }
        catch (Exception e) { Console.WriteLine($"error: {e.Message}"); fps = -1; }
        if (fps >= 0 && fps < fpsFloor) { Console.WriteLine($"{fps:F1} fps (below floor → -)"); fps = -1; }
        else if (fps >= 0) Console.WriteLine($"{fps:F1} fps");
        else Console.WriteLine("-");
        table[name][res] = fps;
    }
}

// Write benchmark.txt at animejanai/ in the exact table format the Manager
// parses (BenchmarkSubmission.FromBenchmarkFile): rows = profile names, columns
// = resolutions, slow/failed cells rendered as "-".
var sb = new StringBuilder();
sb.AppendLine($"AnimeJaNai playback benchmark - backend: {backend}");
sb.AppendLine();
sb.AppendLine("|fps|" + string.Join("|", resolutions) + "|");
sb.AppendLine("|-|" + string.Join("|", resolutions.Select(_ => "-")) + "|");
foreach (var slot in slots)
{
    string name = SlotName(slot);
    var row = resolutions.Select(r =>
        table[name].TryGetValue(r, out var f) && f >= 0
            ? f.ToString("0.##", CultureInfo.InvariantCulture) : "-");
    sb.AppendLine($"|{name}|" + string.Join("|", row) + "|");
}
string outFile = Path.Combine(animejanai, "benchmark.txt");
File.WriteAllText(outFile, sb.ToString());
Console.WriteLine($"\n{sb}\nWritten to {outFile}");
return 0;

// ---- one cell: launch mpv, sample estimated-frame-number, kill --------------
async Task<double> BenchmarkCell(int slot, string clip)
{
    string ipc = isWin
        ? $@"\\.\pipe\aji-bench-{Environment.ProcessId}-{slot}"
        : Path.Combine(Path.GetTempPath(), $"aji-bench-{Environment.ProcessId}-{slot}.sock");
    if (!isWin) { try { File.Delete(ipc); } catch { } }

    string stats = Path.Combine(Path.GetTempPath(), $"aji-bench-{slot}.log");
    string vf = $"@aji:animejanai:lib={Path.Combine(inference, ajiLib)}:conf={confPath}" +
                $":model-dir={onnxDir}:rife-model-dir={rifeDir}:trtexec={trtexec}" +
                $":stats={stats}:slot={slot}";

    var psi = new ProcessStartInfo
    {
        FileName = player,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var a in new[]
    {
        // Loop the short seed clip so playback stays active over the sampling
        // window (--untimed = uncapped throughput).
        "--no-config", "--vo=null", "--untimed", "--no-audio", "--loop-file=inf",
        $"--input-ipc-server={ipc}", "--hwdec=nvdec", "--gpu-api=vulkan",
        $"--vf={vf}", clip,
    }) psi.ArgumentList.Add(a);
    if (!isWin)
    {
        // bundled libs on the loader path (libmpv/libplacebo + TRT/cudart/aji)
        psi.Environment["LD_LIBRARY_PATH"] =
            $"{Path.Combine(installRoot, "mpv")}:{inference}:" +
            Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
    }

    using var proc = Process.Start(psi)!;
    try
    {
        using var ipcConn = await ConnectIpc(ipc, proc, TimeSpan.FromSeconds(60));
        // Wait for the first decoded frame: this excludes process launch AND the
        // one-time TensorRT engine build (the filter pauses playback to build, so
        // the counter only starts advancing once real upscaling begins).
        double f0 = await SampleFrame(ipcConn, TimeSpan.FromSeconds(180));
        if (f0 < 0) return -1;

        // Time the remaining frames until mpv exits at --frames=FRAMES.
        // Sample over a ~3s window, accumulating across --loop-file resets
        // (estimated-frame-number restarts at 0 each loop of the short clip).
        // NOTE: at multi-thousand fps the tiny seed clips loop faster than we can
        // poll, so small-resolution numbers are approximate; the high-resolution
        // cells (the ones users care about) are the most reliable.
        double prev = f0, accumulated = 0, baseTotal = -1, lastTotal = f0;
        var sw = Stopwatch.StartNew();
        TimeSpan baseTime = TimeSpan.Zero, lastTime = TimeSpan.Zero;
        while (sw.Elapsed < TimeSpan.FromSeconds(3))
        {
            double f = await SampleFrame(ipcConn, TimeSpan.FromSeconds(3));
            if (f < 0) break;
            if (f < prev) accumulated += prev;
            prev = f;
            double total = accumulated + f;
            if (baseTotal < 0) { baseTotal = total; baseTime = sw.Elapsed; }
            lastTotal = total; lastTime = sw.Elapsed;
            await Task.Delay(100);
        }
        double dt = (lastTime - baseTime).TotalSeconds;
        if (baseTotal < 0 || dt < 0.5 || lastTotal <= baseTotal) return -1;
        return (lastTotal - baseTotal) / dt;
    }
    finally
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        if (!isWin) { try { File.Delete(ipc); } catch { } }
    }
}

// mpv JSON IPC: named pipe on Windows, unix socket on Linux.
async Task<IpcConn> ConnectIpc(string ipc, Process proc, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (proc.HasExited) throw new InvalidOperationException("player exited before IPC was ready");
        try
        {
            if (isWin)
            {
                var name = ipc.Replace(@"\\.\pipe\", "");
                var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(500);
                return new IpcConn(pipe);
            }
            else
            {
                var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(ipc));
                return new IpcConn(new NetworkStream(sock, ownsSocket: true));
            }
        }
        catch { await Task.Delay(300); }
    }
    throw new TimeoutException("could not connect to mpv IPC");
}

// Polls estimated-frame-number until it returns a number (engine still building
// returns null/errors), or the budget expires.
async Task<double> SampleFrame(IpcConn conn, TimeSpan budget)
{
    var deadline = DateTime.UtcNow + budget;
    while (DateTime.UtcNow < deadline)
    {
        var v = await conn.GetProperty("estimated-frame-number");
        if (v is double d && d > 0) return d;
        await Task.Delay(250);
    }
    return -1;
}

static string ReadBackend(string conf)
{
    try
    {
        foreach (var raw in File.ReadLines(conf))
        {
            var line = raw.Trim();
            if (line.StartsWith("backend=", StringComparison.OrdinalIgnoreCase))
                return line.Substring("backend=".Length).Trim();
        }
    }
    catch { }
    return "TensorRT";
}

static long ClipPixels(string name)
{
    var m = System.Text.RegularExpressions.Regex.Match(name, @"(\d+)x(\d+)");
    return m.Success ? long.Parse(m.Groups[1].Value) * long.Parse(m.Groups[2].Value) : 0;
}

// Minimal line-based JSON IPC client.
sealed class IpcConn : IDisposable
{
    readonly Stream _s;
    readonly StreamReader _r;
    readonly StreamWriter _w;
    public IpcConn(Stream s) { _s = s; _r = new StreamReader(s); _w = new StreamWriter(s) { AutoFlush = true }; }

    public async Task<object?> GetProperty(string prop)
    {
        await _w.WriteLineAsync($"{{\"command\":[\"get_property\",\"{prop}\"]}}");
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < until)
        {
            var line = await _r.ReadLineAsync();
            if (line == null) return null;
            if (!line.Contains("\"error\"")) continue; // skip async events
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.GetString() == "success"
                && root.TryGetProperty("data", out var data))
            {
                return data.ValueKind == JsonValueKind.Number ? data.GetDouble()
                     : data.ValueKind == JsonValueKind.String &&
                       double.TryParse(data.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv) ? dv
                     : null;
            }
            return null; // a non-success response to our command
        }
        return null;
    }

    public void Dispose() { try { _r.Dispose(); _w.Dispose(); _s.Dispose(); } catch { } }
}
