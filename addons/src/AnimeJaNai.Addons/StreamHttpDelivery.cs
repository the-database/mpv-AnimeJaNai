using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace AnimeJaNai.Addons;

internal static class StreamHttpDelivery
{
    internal static async Task SendAsync(HttpContext context, StreamCache.Lease lease, string container, CancellationToken requestToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestToken, lease.Stop.Token);
        var token = lifetime.Token; var request = context.Request; var response = context.Response;
        if (request.Method is not ("GET" or "HEAD")) { response.StatusCode = 405; response.Headers.Allow = "GET, HEAD"; return; }
        var resource = lease.Resource;
        response.ContentType = resource.Initialization || container == "fragmentedMp4" ? "video/mp4" : container == "mpegts" ? "video/mp2t" : "video/x-matroska";
        response.Headers.CacheControl = "private, no-cache";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        if (resource.Continuous)
        {
            response.Headers.AcceptRanges = "none";
            if (request.Headers.ContainsKey("Range")) { response.StatusCode = 416; return; }
            if (request.Method == "HEAD") return;
            await CopyAsync(response, lease, null, token); return;
        }
        string etag = '"' + resource.Hash + '"';
        response.Headers.ETag = etag; response.Headers.AcceptRanges = "bytes";
        if (request.Headers.TryGetValue("If-Match", out var match) && !TagsMatch(match.ToString(), etag, weak: false))
        { response.StatusCode = 412; return; }
        if (request.Headers.TryGetValue("If-None-Match", out var none) && TagsMatch(none.ToString(), etag, weak: true))
        { response.StatusCode = 304; return; }
        long start = 0, length = resource.Length;
        if (request.Headers.TryGetValue("Range", out var ranges) &&
            (!request.Headers.TryGetValue("If-Range", out var validator) || validator.ToString() == etag))
        {
            if (!TryRange(ranges.ToString(), length, out start, out long end))
            {
                response.StatusCode = 416; response.Headers.ContentRange = "bytes */" + length.ToString(CultureInfo.InvariantCulture); return;
            }
            response.StatusCode = 206;
            response.Headers.ContentRange = FormattableString.Invariant($"bytes {start}-{end}/{length}");
            length = end - start + 1;
        }
        response.ContentLength = length;
        if (request.Method == "HEAD") return;
        lease.File.Position = start;
        await CopyAsync(response, lease, length, token);
    }
    internal static bool TagsMatch(string value, string expected, bool weak) => value.Split(',').Any(tag =>
    {
        tag = tag.Trim(); if (weak && tag.StartsWith("W/", StringComparison.Ordinal)) tag = tag[2..]; return tag == "*" || tag == expected;
    });
    internal static bool TryRange(string value, long size, out long start, out long end)
    {
        start = 0; end = size - 1;
        if (size <= 0 || !value.StartsWith("bytes=", StringComparison.Ordinal) || value.Contains(',')) return false;
        string[] parts = value[6..].Split('-'); if (parts.Length != 2) return false;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix <= 0) return false;
            start = Math.Max(0, size - suffix); return true;
        }
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0 || start >= size) return false;
        if (parts[1].Length > 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start) return false;
            end = Math.Min(end, size - 1);
        }
        return true;
    }
    private static async Task CopyAsync(HttpResponse response, StreamCache.Lease lease, long? length, CancellationToken token)
    {
        byte[] buffer = new byte[32768]; long copied = 0;
        while (length is null || copied < length)
        {
            token.ThrowIfCancellationRequested();
            int wanted = length is null ? buffer.Length : (int)Math.Min(buffer.Length, length.Value - copied);
            int read;
            using (var io = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                io.CancelAfter(TimeSpan.FromSeconds(15)); read = await lease.File.ReadAsync(buffer.AsMemory(0, wanted), io.Token);
            }
            if (read == 0)
            {
                if (length is not null) throw new AddonException("stream_changed", "A completed media object ended early.");
                if (lease.Complete) break;
                // A demand/user pause is intentional. It is not an upload stall.
                await Task.Delay(50, token); continue;
            }
            using (var io = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                io.CancelAfter(TimeSpan.FromSeconds(15)); await response.Body.WriteAsync(buffer.AsMemory(0, read), io.Token);
            }
            lease.Served(read); copied += read;
        }
    }
}
