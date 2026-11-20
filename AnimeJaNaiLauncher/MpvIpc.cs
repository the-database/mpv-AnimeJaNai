using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Launcher;

/// A client of mpv's JSON IPC (--input-ipc-server): a named pipe on Windows, a Unix socket elsewhere.
sealed class MpvIpc : IDisposable
{
    readonly Stream stream;
    readonly StreamReader reader;
    int nextId = 1;

    MpvIpc(Stream stream)
    {
        this.stream = stream;
        reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    }

    /// Connects to a running player, or returns null when none answers within the timeout.
    public static MpvIpc? TryConnect(string name, int timeoutMs)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(timeoutMs);
                return new MpvIpc(pipe);
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
                pipe.Dispose();
                return null;
            }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(Install.IpcServer(name)));
            return new MpvIpc(new NetworkStream(socket, ownsSocket: true));
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    /// Waits until a player answers on `name`, up to the timeout. False if `process` exits first.
    public static bool WaitForServer(string name, int timeoutMs, Func<bool> processAlive)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            using var ipc = TryConnect(name, 50);
            if (ipc is not null)
            {
                return true;
            }
            if (!processAlive())
            {
                return false;
            }
            Thread.Sleep(25);
        }
        return false;
    }

    /// Runs one command and returns its reply's "data" (null for commands without data).
    /// Throws when mpv reports an error or does not answer within the timeout.
    public JsonNode? Command(params object[] args) => Command(args, 2000);

    public JsonNode? Command(object[] args, int timeoutMs)
    {
        int id = nextId++;
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("command");
            foreach (var a in args)
            {
                switch (a)
                {
                    case string s: w.WriteStringValue(s); break;
                    case bool b: w.WriteBooleanValue(b); break;
                    case int i: w.WriteNumberValue(i); break;
                    case long l: w.WriteNumberValue(l); break;
                    case double d: w.WriteNumberValue(d); break;
                    default: throw new ArgumentException($"unsupported IPC argument {a.GetType().Name}");
                }
            }
            w.WriteEndArray();
            w.WriteNumber("request_id", id);
            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
        stream.Flush();

        using var cts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            string? line;
            try
            {
                line = reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"mpv did not answer {args[0]}");
            }
            if (line is null)
            {
                throw new IOException("mpv closed the IPC connection");
            }
            JsonNode? reply;
            try
            {
                reply = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }
            if (reply?["request_id"]?.GetValue<int>() != id)
            {
                continue; // an event, or another client's reply
            }
            string error = reply["error"]?.GetValue<string>() ?? "success";
            if (error != "success")
            {
                throw new InvalidOperationException($"mpv: {args[0]}: {error}");
            }
            return reply["data"];
        }
    }

    /// The player window's native handle (window-id), or null when it has no window yet.
    public long? WindowId()
    {
        try
        {
            return Command("get_property", "window-id")?.GetValue<long>();
        }
        catch (InvalidOperationException)
        {
            return null; // "property unavailable" until the window exists
        }
    }

    public void Dispose()
    {
        reader.Dispose();
        stream.Dispose();
    }
}
