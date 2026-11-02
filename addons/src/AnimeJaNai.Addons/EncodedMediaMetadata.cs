using System.Text.Json.Nodes;
using AnimeJaNai.Addons.Native;

namespace AnimeJaNai.Addons;

internal static class EncodedMediaMetadata
{
    internal static string FileName(string media) => media.StartsWith("continuous.", StringComparison.Ordinal)
        ? "continuous.json" : Path.GetFileNameWithoutExtension(media) + ".json";

    internal static JsonObject? Read(string directory, string media)
    {
        string path = Path.Combine(directory, FileName(media)); SafeFiles.CheckParents(path);
        FileStream file;
        try { file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (IOException) { return null; } // The native writer has not closed this object's metadata yet.
        using (file)
        {
            Contract.Require(file.Length is > 0 and <= 32768, "invalid_stream_metadata", "Encoded metadata exceeds its size limit.");
            byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
            JsonObject value;
            try { value = Contract.ParseObject(bytes); }
            catch (Exception e) when (e is System.Text.Json.JsonException or AddonException)
            { throw new AddonException("invalid_stream_metadata", "Encoded metadata is invalid."); }
            Contract.Require(value["probeAbi"]?.GetValue<int>() == 1 && value["resourceFile"]?.GetValue<string>() == media &&
                value["tracks"] is JsonArray tracks && tracks.Count is > 0 and <= 4,
                "invalid_stream_metadata", "Encoded metadata does not match its media object.");
            var video = value["tracks"]!.AsArray().OfType<JsonObject>().Where(t => t["type"]?.GetValue<string>() == "video").ToArray();
            Contract.Require(video.Length == 1 && video[0]["width"]?.GetValue<int>() > 0 && video[0]["height"]?.GetValue<int>() > 0 &&
                video[0]["codec"] is JsonValue, "invalid_stream_metadata", "Encoded output lacks video metadata.");
            return value;
        }
    }

    internal static double? Origin(JsonObject? metadata)
    {
        var video = metadata?["tracks"]?.AsArray().FirstOrDefault(t => t?["type"]?.GetValue<string>() == "video");
        return video?["startSeconds"]?.GetValue<double>();
    }
}
