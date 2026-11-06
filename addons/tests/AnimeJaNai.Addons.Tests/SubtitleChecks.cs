using System.Text;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

internal static partial class Checks
{
    private static async Task SubtitleChecks()
    {
        await Test("Subtitle selection requires fresh typed tracks and explicit ASS styling loss", async () =>
        {
            string representation = new('a', 64), id = representation + ":2";
            var track = new JsonObject { ["trackId"] = id, ["type"] = "subtitle", ["codec"] = "ass", ["streamIndex"] = 2L };
            var probe = new JsonObject { ["representationId"] = representation, ["tracks"] = new JsonArray(track) };
            await Error("subtitle_styling_loss", () => new SubtitleRequest(id).Resolve(probe));
            True(new SubtitleRequest(id, AllowStylingLoss: true).Resolve(probe) == 2);
            True(SubtitleRequest.Parse(new SubtitleRequest(id, 1, 5, true).ToJson()).AllowStylingLoss);
            await Error("stale_track", () => new SubtitleRequest(new string('b', 64) + ":2", AllowStylingLoss: true).Resolve(probe));
            track["codec"] = "hdmv_pgs_subtitle";
            await Error("subtitle_format_unavailable", () => new SubtitleRequest(id, AllowStylingLoss: true).Resolve(probe));
            track["codec"] = "subrip"; True(new SubtitleRequest(null).Resolve(probe) == 2);
            track["type"] = "audio"; await Error("invalid_track", () => new SubtitleRequest(id).Resolve(probe));
        });
        await Test("Subtitle resources use bounded chunks, retain quota and revoke reads on cancellation", async () =>
        {
            var package = Package(permissions: ["sessions.manage", "media.input"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            var provider = new SubtitleFixture();
            await using var broker = new Broker(package, grant, Area(), sessions: new SessionRegistry(provider));
            var subtitles = broker.Subtitles!;
            string id = subtitles.Open(new("fixture", null), new(null));
            await Until(() => subtitles.Status(id)["state"]!.GetValue<string>() == "completed");
            var first = subtitles.Read(id, 0, 32768); True(first.Binary.Length == 32768);
            var end = subtitles.Read(id, 32768, 32768); True(end.Binary.Length == provider.Bytes.Length - 32768);
            await Error("invalid_request", () => subtitles.Read(id, -1, 32768));
            string second = subtitles.Open(new("fixture", null), new(null));
            await Error("capacity_exceeded", () => subtitles.Open(new("fixture", null), new(null)));
            subtitles.Cancel(id); await Error("subtitles_not_ready", () => subtitles.Read(id, 0, 8)); subtitles.Close(id);
            await Until(() => subtitles.Status(second)["state"]!.GetValue<string>() == "completed"); subtitles.Close(second);
            string replacement = subtitles.Open(new("fixture", null), new(null));
            await Until(() => subtitles.Status(replacement)["state"]!.GetValue<string>() == "completed"); subtitles.Close(replacement);
        });
    }
    private sealed class SubtitleFixture : IProcessingSessionProvider, ISubtitleProvider
    {
        internal readonly byte[] Bytes = Encoding.UTF8.GetBytes("WEBVTT\n\n" + new string('x', 40000));
        public bool SupportsSubtitles => true;
        public Task<byte[]> ExtractSubtitlesAsync(ProbeRequest source, SubtitleRequest options, CancellationToken token) => Task.FromResult(Bytes);
        public Task<IProcessingSession> OpenAsync(string sourceId, string? profileId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
