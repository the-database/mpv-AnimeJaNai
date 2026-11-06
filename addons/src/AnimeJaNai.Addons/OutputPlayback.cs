using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public sealed record OutputPlayback(double StartSeconds = 0, string AudioTrack = "default",
    string SubtitleMode = "none", string? SubtitleTrack = null, string? ExternalSourceId = null, RemoteInputRequest? ExternalRemoteSource = null)
{
    internal static bool TrackId(string value) => value.Length is >= 66 and <= 68 &&
        Contract.ValidHash(value[..64]) && value[64] == ':' && int.TryParse(value[65..], out int index) && index is >= 0 and < 128;
    public void Validate()
    {
        Contract.Require(double.IsFinite(StartSeconds) && StartSeconds is >= 0 and <= 315576000,
            "invalid_position", "Start position must be a finite, nonnegative number of seconds.");
        Contract.Require(AudioTrack is "default" or "none" || TrackId(AudioTrack), "invalid_track", "Select an audio track from this source's probe result.");
        int selected = (SubtitleTrack is null ? 0 : 1) + (ExternalSourceId is null ? 0 : 1) + (ExternalRemoteSource is null ? 0 : 1);
        Contract.Require(SubtitleMode is "none" or "burn" && (SubtitleMode == "none" ? selected == 0 : selected == 1) &&
            (SubtitleTrack is null || TrackId(SubtitleTrack)) && (ExternalSourceId is null || ExternalSourceId.Length is > 0 and <= 128),
            "invalid_subtitle", "Choose no subtitles or one approved embedded/external subtitle source.");
        ExternalRemoteSource?.Validate();
    }
    public static OutputPlayback Parse(JsonObject value)
    {
        double start = 0;
        if (value["startSeconds"] is not null)
        {
            Contract.Require(value["startSeconds"] is JsonValue number && number.TryGetValue<double>(out start), "invalid_position", "Invalid start position.");
        }
        string audio = value["audioTrack"] switch
        {
            null => "default",
            JsonObject track => Contract.Text(track, "trackId", 68),
            JsonValue text when text.TryGetValue<string>(out var name) && name is "default" or "none" => name,
            _ => throw new AddonException("invalid_track", "Choose default, none, or a probe audio track.")
        };
        Contract.Require(value["subtitles"] is null or JsonObject, "invalid_subtitle", "Invalid subtitle selection.");
        var subtitles = value["subtitles"] as JsonObject;
        Contract.Require(subtitles?["externalRemoteSource"] is null or JsonObject, "invalid_subtitle", "Invalid approved remote subtitle source.");
        var remote = subtitles?["externalRemoteSource"]?.DeepClone().AsObject();
        if (remote is not null) { remote["type"] ??= "http"; remote["path"] ??= "/"; remote["useCredential"] ??= false; }
        var result = new OutputPlayback(start, audio, subtitles is null ? "none" : Contract.Text(subtitles, "mode", 16),
            subtitles?["trackId"] is null ? null : Contract.Text(subtitles, "trackId", 68),
            subtitles?["externalSourceId"] is null ? null : Contract.Text(subtitles, "externalSourceId", 128),
            remote is null ? null : RemoteInputRequest.Parse(remote));
        result.Validate(); return result;
    }
    internal JsonObject ToJson() => new()
    {
        ["startSeconds"] = StartSeconds,
        ["audioTrack"] = AudioTrack is "default" or "none" ? JsonValue.Create(AudioTrack) : new JsonObject { ["trackId"] = AudioTrack },
        ["subtitles"] = new JsonObject { ["mode"] = SubtitleMode, ["trackId"] = SubtitleTrack, ["externalSourceId"] = ExternalSourceId,
            ["externalRemoteSource"] = ExternalRemoteSource is null ? null : new JsonObject {
                ["type"] = "http", ["destinationId"] = ExternalRemoteSource.DestinationId, ["path"] = ExternalRemoteSource.Path,
                ["useCredential"] = ExternalRemoteSource.UseCredential, ["credentialId"] = ExternalRemoteSource.CredentialId } },
    };
    internal JsonObject Resolve(JsonObject probe, string audioCodec)
    {
        Validate();
        Contract.Require(StartSeconds == 0 || probe["seekable"]?.GetValue<bool>() == true, "input_not_seekable", "The source does not support seeking to a start position.");
        double? duration = probe["durationSeconds"]?.GetValue<double>();
        Contract.Require(duration is null || StartSeconds < duration, "position_out_of_range", "Start position is at or beyond the end of this source.");
        JsonObject? Track(string? id, string type)
        {
            if (id is null || id is "default" or "none") return null;
            Contract.Require(probe["representationId"]?.GetValue<string>() == id[..64], "stale_track", "Source representation changed. Probe it again before selecting tracks.");
            var found = probe["tracks"]!.AsArray().OfType<JsonObject>().SingleOrDefault(t => t["trackId"]?.GetValue<string>() == id);
            Contract.Require(found is not null && found["type"]?.GetValue<string>() == type, "invalid_track", "The requested track is not available for this source and media type.");
            return found;
        }
        var audio = Track(AudioTrack, "audio"); var subtitle = Track(SubtitleTrack, "subtitle");
        Contract.Require(audio is null || audioCodec != "none", "invalid_encoding", "Choose an audio encoder for an explicitly selected audio track.");
        return new JsonObject { ["requestedStartSeconds"] = StartSeconds, ["representationId"] = probe["representationId"]!.DeepClone(),
            ["sourceDurationSeconds"] = duration, ["sourceStartSeconds"] = probe["startSeconds"]?.DeepClone(),
            ["audioTrack"] = audio?.DeepClone(), ["subtitleTrack"] = subtitle?.DeepClone(), ["subtitleMode"] = SubtitleMode };
    }
}
