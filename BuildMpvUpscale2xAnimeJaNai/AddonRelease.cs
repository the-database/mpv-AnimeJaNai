using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Public releases use the ordinary official component pins. The locally built
// host bundle can only populate support directories, never replace those pins.
internal static class AddonRelease
{
    internal const string RuntimeSha256 = "37045d920f7abbd202f0ea8c52ae9bbc0e49bcdca47e70f37649d1dc0ae1cff5";
    internal static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static JsonObject ReadNative(string path)
    {
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if ((int?)record["schemaVersion"] != 1 || (string?)record["platform"] != "win-x64" ||
            (string?)record["cRuntime"] != "ucrt" || (string?)record["ffmpegLinkage"] != "static")
            throw new InvalidDataException("The public addon release requires the UCRT static native build.");
        foreach (string field in new[] { "privateSampleAbi", "privatePlayerSampleAbi", "privateOutputAbi", "nullHardwareFramesOptIn",
            "privateProbeAbi", "privateMuxAbi", "privateSubtitlesAbi" })
            if ((int?)record[field] != 1) throw new InvalidDataException("Missing native capability: " + field);
        var files = record["files"]!.AsObject();
        if (!files.ContainsKey("mpv.exe") || !files.ContainsKey("libmpv-2.dll"))
            throw new InvalidDataException("Native inventory is incomplete.");
        foreach (var entry in files)
            if (!Regex.IsMatch(entry.Key, @"^[A-Za-z0-9._+-]{1,128}\.(dll|exe|com)$") ||
                !Regex.IsMatch((string?)entry.Value ?? "", "^[0-9a-f]{64}$"))
                throw new InvalidDataException("Invalid native inventory entry: " + entry.Key);
        return record;
    }

    internal static List<(string Source, string Relative)> ValidateSupport(string bundle, JsonObject files)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "addon-host", ["runtime"] = "addon-host/runtime", ["licenses"] = "addon-host/licenses",
            ["source"] = "addon-development/source", ["shared"] = "addon-development/shared", ["sdk"] = "addon-development/sdk",
            ["examples"] = "addon-development/examples", ["tools"] = "addon-development/tools", ["rife-settings"] = "addon-development/rife-settings"
        };
        // Validate the complete input before copying any file. Source bundles
        // cannot smuggle a historical core manifest or arbitrary root binaries.
        var copies = new List<(string Source, string Relative)>();
        void Walk(string directory)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked support files are not allowed.");
                if (Directory.Exists(path)) { Walk(path); continue; }
                string name = Path.GetRelativePath(bundle, path).Replace('\\', '/');
                if (name == "host-build.json") continue;
                if (Hash(path) != (string?)files[name]) throw new InvalidDataException("Host inventory mismatch: " + name);
                int slash = name.IndexOf('/');
                if (slash > 0 && mapping.TryGetValue(name[..slash], out string? prefix))
                    copies.Add((path, prefix + name[slash..]));
                else if (slash < 0 && Path.GetExtension(name) is ".md" or ".html" or ".ajnaddon")
                    copies.Add((path, "addon-development/" + name));
                else throw new InvalidDataException("Unexpected file in addon support bundle: " + name);
            }
        }
        Walk(bundle);
        if (copies.Count != files.Count) throw new InvalidDataException("Host inventory contains missing files.");
        return copies;
    }

    internal static void InstallSupport(string bundle, string destination, JsonObject native,
        string managerSource, string inferenceSource)
    {
        var host = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "host-build.json")))!.AsObject();
        var copies = ValidateSupport(bundle, host["files"]!.AsObject());
        if (Hash(Path.Combine(bundle, "runtime/wasmtime.exe")) != RuntimeSha256)
            throw new InvalidDataException("The host bundle does not contain the pinned Wasmtime runtime.");
        foreach (var entry in native["files"]!.AsObject())
            if (Hash(Path.Combine(destination, entry.Key)) != (string?)entry.Value)
                throw new InvalidDataException("Official native release file does not match its pinned inventory: " + entry.Key);
        foreach (var copy in copies)
        {
            string target = Path.Combine(destination, copy.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(copy.Source, target, true);
        }
        void Write(string relative, JsonNode value)
        {
            string path = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value.ToJsonString(new() { WriteIndented = true }));
        }
        foreach (string extension in new[] { ".md", ".html" })
            File.Copy(Path.Combine(destination, "addon-development/USER-GUIDE" + extension),
                Path.Combine(destination, "addon-development/START-HERE" + extension), true);
        Write("addon-host/native-capabilities.json", native);
        Write("addon-host/native-media.json", new JsonObject { ["schemaVersion"] = 1,
            ["sessionApi"] = new JsonObject { ["major"] = 1, ["minor"] = 1 }, ["mpvRevision"] = native["sources"]!["mpv"]!.DeepClone() });
        Write("build-info/host-build.json", host);
        var sources = native["sources"]!.DeepClone().AsObject();
        sources["main"] = host["sourceCommit"]!.DeepClone();
        sources["manager"] = managerSource; sources["inference"] = inferenceSource;
        var inventory = native["files"]!.DeepClone().AsObject();
        var owned = Directory.GetFiles(Path.Combine(destination, "addon-host"), "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(destination, "animejanai/inference"), "aji*.dll"))
            .Append(Path.Combine(destination, "AnimeJaNaiManager.exe"))
            .Append(Path.Combine(destination, "portable_config/scripts/animejanai_addons.lua"));
        foreach (string path in owned) inventory[Path.GetRelativePath(destination, path).Replace('\\', '/')] = Hash(path);
        Write("addon-package.json", new JsonObject { ["schemaVersion"] = 1, ["platform"] = "win-x64", ["sources"] = sources, ["files"] = inventory });
    }
}
