using System.Text.Json;

namespace AnimeJaNai.Addons.Native;

// The shared pack store's aji-runtime.json, which the updater has aji-runtime write beside libaji
// (animejanai/inference): the folder libaji loads TensorRT and runs trtexec from. Without it (an
// install from before the store) TensorRT is in the inference folder. Same reading as libaji, the
// Manager's RuntimePacks.TrtDirectory and animejanai_backend.lua.
internal static class NativeRuntimePacks
{
    // The TensorRT runtime's folder: the store pack aji-runtime.json names, else the inference
    // folder. Relative paths in the file are relative to it, as libaji reads them.
    internal static string TrtDirectory(string inferenceDirectory)
    {
        try
        {
            string path = Path.Combine(inferenceDirectory, "aji-runtime.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("trt", out var trt) && trt.ValueKind == JsonValueKind.String &&
                    trt.GetString() is { Length: > 0 } directory)
                    return Path.GetFullPath(Path.IsPathRooted(directory) ? directory : Path.Combine(inferenceDirectory, directory));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // unreadable: as without the file
        }
        return inferenceDirectory;
    }

    // Whether a TensorRT session can start: the backend beside libaji, and the runtime and trtexec
    // in the store pack or the inference folder.
    internal static bool TensorRtInstalled(string inferenceDirectory)
    {
        string trt = TrtDirectory(inferenceDirectory);
        return File.Exists(Path.Combine(inferenceDirectory, "aji_trt.dll")) &&
            File.Exists(Path.Combine(trt, "nvinfer_11.dll")) && File.Exists(Path.Combine(trt, "trtexec.exe"));
    }
}
