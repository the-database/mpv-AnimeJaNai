using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace AnimeJaNai.Addons;

public sealed partial class HttpServerAccess
{
    public const int MaximumBufferedBytes = 256 * 1024;

    // Called only under sync. Checking elapsed time closes the gap between a
    // deadline passing and the HTTP continuation removing the handle.
    private Pending FindRequest(string id)
    {
        Contract.Require(requests.TryGetValue(id, out var request) && !request.Lifetime.IsCancellationRequested &&
            (request.Response.Task.IsCompleted || (!request.Decision.IsCancellationRequested &&
                Stopwatch.GetElapsedTime(request.Created).TotalSeconds < request.DecisionSeconds)),
            "request_not_found", "Request expired or belongs to another addon instance.");
        return request;
    }
    private static void Claim(Pending request)
    {
        Contract.Require(!request.Claimed, "request_claimed", "The request already has a response owner.");
        request.Claimed = true;
    }
    private static void DisposeRequest(Pending request)
    {
        request.ResponseBuffer?.Dispose();
        request.Decision.Dispose();
        request.Lifetime.Dispose();
    }
    public JsonObject RequestStatus(string requestId)
    {
        Demand();
        lock (sync)
        {
            var request = FindRequest(requestId);
            return new() { ["requestId"] = requestId, ["state"] = request.Claimed ? "claimed" : "pending",
                ["bodyState"] = request.BodyTask is null ? "unread" : !request.BodyTask.IsCompleted ? "reading" : request.BodyError is null ? "ready" : "failed",
                ["bodyLength"] = request.Body?.Length, ["bodyError"] = request.BodyError,
                ["decisionRemainingSeconds"] = request.Response.Task.IsCompleted ? null : Math.Max(0, request.DecisionSeconds - Stopwatch.GetElapsedTime(request.Created).TotalSeconds) };
        }
    }
    public void Extend(string requestId, int seconds)
    {
        Demand();
        Contract.Require(seconds is >= 1 and <= 120, "invalid_request", "Use a decision extension of 1 to 120 seconds.");
        lock (sync)
        {
            var request = FindRequest(requestId);
            Contract.Require(!request.Response.Task.IsCompleted, "request_claimed", "The response is already being delivered.");
            double elapsed = Stopwatch.GetElapsedTime(request.Created).TotalSeconds;
            request.DecisionSeconds = Math.Min(120, Math.Max(request.DecisionSeconds, elapsed + seconds));
            request.Decision.CancelAfter(TimeSpan.FromSeconds(Math.Max(0, request.DecisionSeconds - elapsed)));
        }
    }
    public void CancelRequest(string requestId)
    {
        Demand();
        lock (sync) FindRequest(requestId).Lifetime.Cancel();
    }
    public void ReadBody(string requestId)
    {
        Demand();
        lock (sync)
        {
            var request = FindRequest(requestId);
            Contract.Require(!request.Claimed && request.BodyTask is null, "request_claimed", "Request body already has a reader or response owner.");
            Contract.Require(request.Context.Request.ContentLength is null or <= MaximumBufferedBytes, "request_too_large", "Buffered body exceeds 256 KiB.");
            request.BodyTask = Task.Run(async () =>
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request.Lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    using var buffer = new MemoryStream();
                    var chunk = new byte[MaximumInlineBytes];
                    while (true)
                    {
                        int read = await request.Context.Request.Body.ReadAsync(chunk, deadline.Token);
                        if (read == 0) break;
                        if (buffer.Length + read > MaximumBufferedBytes) throw new AddonException("request_too_large", "Buffered body exceeds 256 KiB.");
                        buffer.Write(chunk, 0, read);
                    }
                    lock (sync) request.Body = buffer.ToArray();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    lock (sync) request.BodyError = e switch
                    {
                        AddonException a => a.Code,
                        BadHttpRequestException b when b.StatusCode == 413 => "request_too_large",
                        OperationCanceledException => "request_cancelled",
                        _ => "body_read_failed"
                    };
                }
            });
        }
    }
    public BrokerResponse BodyChunk(string requestId, int offset, int count)
    {
        Demand();
        Contract.Require(offset is >= 0 and <= MaximumBufferedBytes && count is >= 1 and <= MaximumInlineBytes,
            "invalid_request", "Use a nonnegative body offset and a chunk of 1 to 32768 bytes.");
        lock (sync)
        {
            var request = FindRequest(requestId);
            Contract.Require(request.BodyTask?.IsCompleted == true, "body_not_ready", "Start reading the body and poll request status first.");
            Contract.Require(request.BodyError is null, request.BodyError ?? "body_read_failed", "Request body could not be read.");
            var body = request.Body!;
            Contract.Require(offset <= body.Length, "invalid_request", "Offset exceeds the body length.");
            int length = Math.Min(count, body.Length - offset);
            return new(new JsonObject { ["byteLength"] = length, ["offset"] = offset, ["totalBytes"] = body.Length,
                ["eof"] = offset + length == body.Length }, body.AsMemory(offset, length));
        }
    }
    public void BeginResponse(string requestId, int status, KeyValuePair<string, string[]>[] headers)
    {
        Demand(); ValidateResponse(status, headers, 0);
        lock (sync)
        {
            var request = FindRequest(requestId); Claim(request);
            request.ResponseStatus = status; request.ResponseHeaders = CopyHeaders(headers);
            request.ResponseBuffer = new();
        }
    }
    public void AppendResponse(string requestId, byte[] body)
    {
        Demand();
        Contract.Require(body.Length <= MaximumInlineBytes, "invalid_response", "Each response chunk is limited to 32 KiB.");
        lock (sync)
        {
            var request = FindRequest(requestId);
            Contract.Require(request.ResponseBuffer is not null && !request.Response.Task.IsCompleted, "request_claimed", "No buffered response is open.");
            ValidateResponse(request.ResponseStatus, request.ResponseHeaders!, checked((int)request.ResponseBuffer.Length + body.Length));
            request.ResponseBuffer.Write(body);
        }
    }
    public void FinishResponse(string requestId)
    {
        Demand();
        lock (sync)
        {
            var request = FindRequest(requestId);
            Contract.Require(request.ResponseBuffer is not null && !request.Response.Task.IsCompleted, "request_claimed", "No buffered response is open.");
            request.Response.SetResult(new(request.ResponseStatus, request.ResponseHeaders!, request.ResponseBuffer.ToArray()));
            request.ResponseBuffer.Dispose(); request.ResponseBuffer = null;
        }
    }
    internal Task ClaimNative(string requestId, Func<HttpContext, CancellationToken, Task> response)
    {
        Demand();
        lock (sync)
        {
            var request = FindRequest(requestId);
            Contract.Require(request.BodyTask is null, "body_claimed", "A native response requires an unread request body.");
            Claim(request);
            request.Response.SetResult(new(200, [], [], response));
            return request.Done.Task;
        }
    }
    internal HashSet<string> SensitiveHeaders(string requestId)
    {
        Demand();
        lock (sync)
        {
            return new(FindRequest(requestId).Server.Selection.Binding.SensitiveHeaders.Concat(
                new[] { "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie" }).Concat(package.Manifest.SensitiveRequestFields?.Headers ?? []), StringComparer.OrdinalIgnoreCase);
        }
    }
    internal string[] CaptureField(string requestId, string kind, string name)
    {
        Demand();
        lock (sync)
        {
            var request = FindRequest(requestId);
            Contract.Require(kind is "header" or "query", "invalid_credential", "Capture a declared sensitive header or query field.");
            bool hidden = kind == "header" ? SensitiveHeaders(requestId).Contains(name) :
                request.Server.Selection.Binding.SensitiveQuery.Concat(package.Manifest.SensitiveRequestFields?.Query ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);
            Contract.Require(hidden, "credential_field_not_declared", "Declare sensitive fields before request arrival.");
            var fields = kind == "header" ? request.Context.Request.Headers.AsEnumerable() : request.Context.Request.Query.AsEnumerable();
            string[] values = fields.Where(f => f.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).SelectMany(f => f.Value).Select(v => v ?? "").ToArray();
            Contract.Require(values.Length is > 0 and <= 8 && values.All(v => v.Length is > 0 and <= 4096 && !v.Any(char.IsControl)),
                "credential_field_missing", "The designated credential field is missing or invalid.");
            return values;
        }
    }
}
