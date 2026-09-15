using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AnimeJaNai.Addons.TestSupport;

internal sealed record HttpInput(string Path, Dictionary<string, string> Headers, byte[] Body, string Method);
internal sealed record HttpReply(int Status, byte[] Body, string Headers = "", bool Chunked = false,
    long? DeclaredLength = null, TimeSpan? BodyDelay = null, string? FileBody = null, long FileOffset = 0);
internal sealed class HttpFixture : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly List<Task> clients = [];
    private readonly Func<HttpInput, CancellationToken, Task<HttpReply>> handler;
    private readonly int maximumBody;
    private readonly bool headersOnly;
    public HttpInput? Last;
    public int Requests;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public HttpFixture(Func<HttpInput, CancellationToken, Task<HttpReply>> handler, int maximumBody = 32768, bool headersOnly = false)
    { this.handler = handler; this.maximumBody = maximumBody; this.headersOnly = headersOnly; listener.Start(); loop = AcceptAsync(); }
    private async Task AcceptAsync()
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); clients.Add(ServeAsync(client)); } }
        catch (Exception error) when (stop.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream(); using var header = new MemoryStream(); byte[] single = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(single, stop.Token) == 0) return; header.WriteByte(single[0]);
                True(header.Length <= 32768, "Fixture request header limit");
                var buffer = header.GetBuffer(); int n = (int)header.Length;
                if (n >= 4 && buffer.AsSpan(n - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
            }
            string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1)) { int colon = line.IndexOf(':'); headers[line[..colon]] = line[(colon + 1)..].Trim(); }
            byte[] body = [];
            if (!headersOnly && headers.TryGetValue("Transfer-Encoding", out var transfer) && transfer == "chunked")
            {
                using var collected = new MemoryStream();
                async Task<string> LineAsync()
                {
                    using var line = new MemoryStream();
                    while (true)
                    {
                        await stream.ReadExactlyAsync(single, stop.Token); line.WriteByte(single[0]);
                        True(line.Length <= 64, "Fixture chunk header limit");
                        if (single[0] == '\n') return Encoding.ASCII.GetString(line.ToArray()).TrimEnd('\r', '\n');
                    }
                }
                while (true)
                {
                    int count = Convert.ToInt32(await LineAsync(), 16);
                    True(count >= 0 && collected.Length + count <= maximumBody, "Fixture chunk body limit");
                    if (count == 0) { True(await LineAsync() == ""); break; }
                    byte[] chunk = new byte[count]; await stream.ReadExactlyAsync(chunk, stop.Token); collected.Write(chunk);
                    True(await LineAsync() == "");
                }
                body = collected.ToArray();
            }
            else if (!headersOnly)
            {
                int length = headers.TryGetValue("Content-Length", out var size) ? int.Parse(size) : 0;
                True(length >= 0 && length <= maximumBody); body = new byte[length]; await stream.ReadExactlyAsync(body, stop.Token);
            }
            var input = new HttpInput(lines[0].Split(' ')[1], headers, body, lines[0].Split(' ')[0]);
            Last = input; Interlocked.Increment(ref Requests);
            var reply = await handler(input, stop.Token);
            using var fileBody = reply.FileBody is null ? null : new FileStream(reply.FileBody, FileMode.Open, FileAccess.Read, FileShare.Read, 32768, FileOptions.Asynchronous);
            if (fileBody is not null)
            {
                True(!reply.Chunked && reply.FileOffset >= 0 && reply.FileOffset <= fileBody.Length, "Invalid fixture file response.");
                fileBody.Position = reply.FileOffset;
            }
            string prefix = $"HTTP/1.1 {reply.Status} Test\r\nConnection: close\r\n" + reply.Headers +
                (reply.Chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {reply.DeclaredLength ?? (fileBody is null ? reply.Body.Length : fileBody.Length - reply.FileOffset)}\r\n") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(prefix), stop.Token);
            if (reply.BodyDelay is { } delay) await Task.Delay(delay, stop.Token);
            if (reply.Chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes(reply.Body.Length.ToString("X") + "\r\n"), stop.Token);
            if (fileBody is null) await stream.WriteAsync(reply.Body, stop.Token);
            else if (input.Method != "HEAD") await fileBody.CopyToAsync(stream, 32768, stop.Token);
            if (reply.Chunked) await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray(), stop.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException) { }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); await loop; await Task.WhenAll(clients); stop.Dispose();
    }
    private static void True(bool value, string message = "HTTP fixture assertion failed") { if (!value) throw new Exception(message); }
}
