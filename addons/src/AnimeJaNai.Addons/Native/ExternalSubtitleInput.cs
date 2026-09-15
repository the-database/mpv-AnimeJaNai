namespace AnimeJaNai.Addons.Native;

internal static class ExternalSubtitleInput
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    internal static async Task<MemoryStream> DownloadAsync(RemoteInputPlan plan, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var source = await RemoteMediaStream.OpenAsync(plan, deadline.Token, TimeSpan.FromSeconds(30));
        try { Contract.Require(source.Length is > 0 and <= MaximumBytes, "subtitle_limit", "External subtitle resource is empty or exceeds 16 MiB."); }
        catch (NotSupportedException) { } // A bounded chunked response is also supported.
        var output = new MemoryStream(); byte[] bytes = new byte[32768];
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(bytes, deadline.Token);
                if (read == 0) break;
                Contract.Require(output.Length + read <= MaximumBytes, "subtitle_limit", "External subtitle resource exceeds 16 MiB.");
                output.Write(bytes, 0, read);
            }
            Contract.Require(output.Length > 0, "subtitle_limit", "External subtitle resource is empty.");
            output.Position = 0; return output;
        }
        catch { output.Dispose(); throw; }
    }
}
