using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnimeJaNai.Addons.Native;

internal static class NativeProcessingEvidence
{
    internal static JsonObject? Parse(string text)
    {
        string[] fields = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 10 || fields[0] != "AJN_STREAM_V1" || fields[2] is not ("DirectML" or "TensorRT")) return null;
        if (fields[1] is not ("active" or "bypass" or "building" or "engineMissing" or "engineIncompatible" or "preparationFailed" or "failed")) return null;
        var numbers = new int[6];
        for (int i = 0; i < numbers.Length; i++) if (!int.TryParse(fields[i + 3], out numbers[i]) || numbers[i] < 0 || numbers[i] > 65536) return null;
        if (!double.TryParse(fields[9], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fps) || !double.IsFinite(fps) || fps < 0 || fps > 1000) return null;
        return new() { ["state"] = fields[1], ["actualBackend"] = fields[2], ["slot"] = numbers[0],
            ["inputWidth"] = numbers[1], ["inputHeight"] = numbers[2], ["outputWidth"] = numbers[3], ["outputHeight"] = numbers[4],
            ["rifeActive"] = numbers[5] != 0, ["outputFrameRate"] = fps };
    }
    internal static JsonArray Models(string directory)
    {
        var result = new JsonArray();
        try
        {
            string text = System.Text.Encoding.UTF8.GetString(AddonPackage.ReadBoundedFile(Path.Combine(directory, "inference.log"), 64 * 1024));
            foreach (Match match in Regex.Matches(text, @"(?:^|\n)\d+\. Applied Model: ([A-Za-z0-9_. ()+\-]{1,180});", RegexOptions.CultureInvariant))
                if (result.Count < 16) result.Add(match.Groups[1].Value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AddonException) { }
        return result;
    }
}
