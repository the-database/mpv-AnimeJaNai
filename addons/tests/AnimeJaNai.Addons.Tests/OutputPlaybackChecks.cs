using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task OutputPlaybackChecks()
    {
        await Test("Remote subtitle selections round trip and require exactly one explicit source", async () =>
        {
            var remote = new OutputPlayback(SubtitleMode: "burn", ExternalRemoteSource: new("approved", "/subtitles", CredentialId: "client"));
            True(OutputPlayback.Parse(remote.ToJson()) == remote);
            await Error("invalid_subtitle", () => (remote with { ExternalSourceId = "local" }).Validate());
            await Error("invalid_subtitle", () => (remote with { SubtitleMode = "none" }).Validate());
            await Error("invalid_input", () => (remote with { ExternalRemoteSource = remote.ExternalRemoteSource! with { UseCredential = true } }).Validate());
        });
        await Test("Remote subtitle download preserves exact text, scopes its request and rejects oversized input", async () =>
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\n日本語 test\n");
            await using var source = new AnimeJaNai.Addons.TestSupport.HttpFixture((request, _) =>
            {
                True(request.Headers["X-Subtitle-Key"] == "subtitle-fixture" && !request.Headers.ContainsKey("Cookie"));
                return Task.FromResult(new AnimeJaNai.Addons.TestSupport.HttpReply(200, bytes));
            });
            using var downloaded = await ExternalSubtitleInput.DownloadAsync(InputPlan(source.Port, "X-Subtitle-Key", "subtitle-fixture"), default);
            True(downloaded.ToArray().SequenceEqual(bytes));
            await using var large = new AnimeJaNai.Addons.TestSupport.HttpFixture((_, _) => Task.FromResult(
                new AnimeJaNai.Addons.TestSupport.HttpReply(200, new byte[ExternalSubtitleInput.MaximumBytes + 1])));
            await Error("subtitle_limit", async () => { using var rejected = await ExternalSubtitleInput.DownloadAsync(InputPlan(large.Port), default); });
        });
        await Test("Served output rejects known HDR and interlaced sources before starting its encoder", async () =>
        {
            var track = new JsonObject { ["type"] = "video", ["fieldOrder"] = "progressive" };
            var probe = new JsonObject { ["tracks"] = new JsonArray(track) };
            StreamRequest.ValidateSource(probe);
            track["fieldOrder"] = "interlaced";
            await Error("stream_format_unavailable", () => StreamRequest.ValidateSource(probe));
            track["fieldOrder"] = "progressive"; track["colorTransfer"] = "smpte2084";
            await Error("stream_format_unavailable", () => StreamRequest.ValidateSource(probe));
        });
        await Test("Output playback validates positions and track references against the current source", async () =>
        {
            string identity = new('a', 64);
            var probe = new JsonObject { ["representationId"] = identity, ["durationSeconds"] = 60.0, ["startSeconds"] = 0.0, ["seekable"] = true,
                ["tracks"] = new JsonArray(new JsonObject { ["trackId"] = identity + ":1", ["type"] = "audio", ["typeOrdinal"] = 1 },
                    new JsonObject { ["trackId"] = identity + ":2", ["type"] = "subtitle", ["typeOrdinal"] = 1 }) };
            var options = new OutputPlayback(12.5, identity + ":1");
            True(OutputPlayback.Parse(options.ToJson()) == options);
            var resolved = options.Resolve(probe, "aac");
            True(resolved["requestedStartSeconds"]!.GetValue<double>() == 12.5 && resolved["audioTrack"]?["typeOrdinal"]!.GetValue<int>() == 1);
            await Error("invalid_position", () => (options with { StartSeconds = double.NaN }).Validate());
            await Error("invalid_position", () => (options with { StartSeconds = -1 }).Validate());
            await Error("position_out_of_range", () => (options with { StartSeconds = 60 }).Resolve(probe, "aac"));
            await Error("stale_track", () => (options with { AudioTrack = new string('b', 64) + ":1" }).Resolve(probe, "aac"));
            await Error("invalid_track", () => (options with { AudioTrack = identity + ":2" }).Resolve(probe, "aac"));
            await Error("invalid_encoding", () => options.Resolve(probe, "none"));
            probe["seekable"] = false;
            await Error("input_not_seekable", () => options.Resolve(probe, "aac"));
            _ = (options with { StartSeconds = 0 }).Resolve(probe, "aac");
            await Error("invalid_subtitle", () => new OutputPlayback(SubtitleMode: "burn").Validate());
        });
        await Test("Output playback preserves absent defaults and explicitly validates stereo encoding", async () =>
        {
            var old = OutputRequest.Parse(OutputParameters("receiver"));
            True(old.Playback is null && old.AudioChannels is null);
            var parameters = OutputParameters("receiver");
            parameters["playback"] = new JsonObject { ["startSeconds"] = 7.25, ["audioTrack"] = "none" };
            var selected = OutputRequest.Parse(parameters);
            True(selected.Playback?.StartSeconds == 7.25 && selected.Playback.AudioTrack == "none");
            var encoding = new NativeEncoding("h264", "matroska", 1000, "aac", AudioChannels: 2);
            True(NativeEncoding.Parse(encoding.ToJson()) == encoding);
            await Error("invalid_encoding", () => (encoding with { AudioChannels = 6 }).Validate());
            await Error("invalid_encoding", () => (encoding with { AudioCodec = "none" }).Validate());
        });
        await Test("Forward-only probing replays the exact inspected prefix before continuing playback", () =>
        {
            byte[] bytes = Enumerable.Range(0, 200000).Select(i => (byte)i).ToArray();
            using var source = new MemoryStream(bytes); using var replay = new ProbeReplayStream(source);
            byte[] prefix = new byte[70000]; replay.ReadExactly(prefix); True(prefix.SequenceEqual(bytes[..70000]));
            replay.BeginReplay(); True(!replay.CanSeek && replay.Position == 0);
            using var result = new MemoryStream(); replay.CopyTo(result); True(bytes.SequenceEqual(result.ToArray()));
        });
    }
}
