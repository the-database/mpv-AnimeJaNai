using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

// Versioned evidence emitted by the native producer, then verified by both the
// assembler and host. This is not a signature or a GPU compatibility guarantee.
internal static class NativeCapabilities
{
    internal const string FileName = "native-capabilities.json";
    internal static bool Present(string root) => File.Exists(Path.Combine(root, "addon-host", FileName));
    internal static bool Has(string root, string capability)
    {
        try
        {
            var marker = Contract.ParseObject(AddonPackage.ReadBoundedFile(Path.Combine(root, "addon-host", FileName), 64 * 1024));
            if (Contract.Number(marker, "schemaVersion") != 1 || Contract.Text(marker, "platform") != "win-x64" ||
                Contract.Number(marker, capability) != 1 || marker["files"] is not JsonObject files || files.Count is < 2 or > 64 ||
                !files.ContainsKey("libmpv-2.dll") || !files.ContainsKey("mpv.exe")) return false;
            if (capability == "privateOutputAbi")
            {
                if (Contract.Text(marker, "cRuntime") != "ucrt") return false;
                string linkage = Contract.Text(marker, "ffmpegLinkage");
                bool hasMuxer = files.Any(f => f.Key.StartsWith("avformat-", StringComparison.Ordinal) && f.Key.EndsWith(".dll", StringComparison.Ordinal));
                if (linkage is not ("static" or "shared") || hasMuxer != (linkage == "shared")) return false;
            }
            foreach (var file in files)
            {
                // Producer records have flat names only. Never hash a path
                // outside the installation based on metadata contents.
                if (file.Key.Length is < 1 or > 128 || file.Key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) ||
                    !(file.Key.EndsWith(".dll", StringComparison.Ordinal) || file.Key is "mpv.exe" or "mpv.com")) return false;
                string expected = Contract.Text(files, file.Key, 64);
                if (!Contract.ValidHash(expected)) return false;
                string path = Path.Combine(root, file.Key); SafeFiles.CheckParents(path);
                using var binary = File.OpenRead(path);
                if (Convert.ToHexStringLower(SHA256.HashData(binary)) != expected) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AddonException or JsonException) { return false; }
    }
}
