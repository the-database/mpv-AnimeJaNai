using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

internal sealed record SubtitleRequest(string? TrackId, double StartSeconds = 0, double EndSeconds = 315576000, bool AllowStylingLoss = false)
{
    internal void Validate()
    {
        Contract.Require(TrackId is null || OutputPlayback.TrackId(TrackId), "invalid_track", "Select a subtitle track from this source's probe.");
        Contract.Require(double.IsFinite(StartSeconds) && double.IsFinite(EndSeconds) && StartSeconds >= 0 && EndSeconds > StartSeconds && EndSeconds <= 315576000,
            "invalid_position", "Choose a finite, positive subtitle interval.");
    }
    internal static SubtitleRequest Parse(JsonObject value)
    {
        double start = 0, end = 315576000;
        if (value["startSeconds"] is not null) Contract.Require(value["startSeconds"] is JsonValue a && a.TryGetValue<double>(out start), "invalid_position", "Invalid subtitle start.");
        if (value["endSeconds"] is not null) Contract.Require(value["endSeconds"] is JsonValue b && b.TryGetValue<double>(out end), "invalid_position", "Invalid subtitle end.");
        Contract.Require(value["allowStylingLoss"] is null || value["allowStylingLoss"] is JsonValue consent && consent.TryGetValue<bool>(out _), "invalid_subtitle", "Choose whether styling loss is acceptable.");
        var result = new SubtitleRequest(value["trackId"] is null ? null : Contract.Text(value, "trackId", 68), start, end, value["allowStylingLoss"]?.GetValue<bool>() == true); result.Validate(); return result;
    }
    internal JsonObject ToJson() => new() { ["trackId"] = TrackId, ["startSeconds"] = StartSeconds, ["endSeconds"] = EndSeconds, ["allowStylingLoss"] = AllowStylingLoss };
    internal int Resolve(JsonObject probe)
    {
        Validate(); var tracks = probe["tracks"]!.AsArray().OfType<JsonObject>().ToArray();
        JsonObject? track;
        if (TrackId is not null)
        {
            Contract.Require(probe["representationId"]?.GetValue<string>() == TrackId[..64], "stale_track", "Source changed. Probe it again before selecting subtitles.");
            track = tracks.SingleOrDefault(t => t["trackId"]?.GetValue<string>() == TrackId);
        }
        else
        {
            Contract.Require(tracks.Length == 1 && tracks[0]["type"]?.GetValue<string>() == "subtitle", "invalid_track", "Embedded subtitles require an explicit probe track. A standalone subtitle file may omit it.");
            track = tracks[0];
        }
        Contract.Require(track is not null && track["type"]?.GetValue<string>() == "subtitle", "invalid_track", "This subtitle track is unavailable.");
        Contract.Require(track["codec"]?.GetValue<string>() is "ass" or "ssa" or "subrip" or "webvtt" or "mov_text" or "text", "subtitle_format_unavailable", "This subtitle format cannot be extracted to text; choose burn-in if supported.");
        Contract.Require(track["codec"]?.GetValue<string>() is not ("ass" or "ssa") || AllowStylingLoss, "subtitle_styling_loss", "ASS to WebVTT loses positioning, fonts and drawing. Choose allowStylingLoss explicitly or use burn-in.");
        return checked((int)Contract.Number(track, "streamIndex"));
    }
}
internal interface ISubtitleProvider
{
    bool SupportsSubtitles { get; }
    Task<byte[]> ExtractSubtitlesAsync(ProbeRequest source, SubtitleRequest options, CancellationToken token);
}
