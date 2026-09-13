using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

public static class DeveloperTools
{
    public const string CompilerHash = "4b298b147ba2d8656af174e266ca8f9de1a4d963795a95f4ce82f685b34d3c73";
    public static string Resource(string name)
    {
        using var stream = typeof(DeveloperTools).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static void New(string directory, string id)
    {
        Contract.Require(Contract.ValidId(id), "invalid_id", "Use a lowercase reverse-domain addon id.");
        directory = Path.GetFullPath(directory);
        Contract.Require(!Directory.Exists(directory) && !File.Exists(directory), "already_exists", "Choose a new addon directory.");
        SafeFiles.CheckParents(directory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "addon.js"), Resource("example.js"));
        File.WriteAllText(Path.Combine(directory, "ajn.d.ts"), Resource("sdk.d.ts"));
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, id, name = id, version = "0.1.0", api = new { major = 1, minMinor = 0 },
            permissions = new[] { "log.write", "storage.read", "storage.write" },
            requiredCapabilities = new { settings = new { major = 1, minMinor = 0 } },
            activation = new[] { "manual" },
            settings = new { greeting = new { type = "string", label = "Greeting", @default = "Hello", maxLength = 100 } },
            actions = new { status = new { label = "Show counter" } },
        }));
    }

    public static async Task<AddonPackage> BuildAsync(string directory, string compiler, string output, CancellationToken cancellationToken = default)
    {
        using (var stream = File.OpenRead(compiler))
            Contract.Require(Convert.ToHexStringLower(SHA256.HashData(stream)) == CompilerHash,
                "compiler_mismatch", "Expected the pinned Javy 9.1.0 compiler. Run tools/bootstrap.ps1.");
        directory = Path.GetFullPath(directory);
        var sourceManifest = Contract.ParseObject(AddonPackage.ReadBoundedFile(Path.Combine(directory, "manifest.json"), 16 * 1024));
        sourceManifest["moduleSha256"] = new string('0', 64);
        var manifest = sourceManifest.Deserialize<AddonManifest>(Contract.Json)
            ?? throw new AddonException("invalid_manifest", "Missing manifest.");
        manifest.Validate();
        string source = new UTF8Encoding(false, true).GetString(AddonPackage.ReadBoundedFile(Path.Combine(directory, "addon.js"), 256 * 1024));
        string temporary = SafeFiles.DirectoryPath(directory, "build-" + Guid.NewGuid().ToString("N"));
        string js = Path.Combine(temporary, "addon.js"), wasm = Path.Combine(temporary, "module.wasm");
        try
        {
            File.WriteAllText(js, Resource("sdk.js") + "\n" + source + "\n__ajnSdk.run(onEvent);\n", new UTF8Encoding(false));
            var info = WorkerBridge.ProcessInfo(compiler, temporary);
            foreach (string arg in new[] { "build", js, "-o", wasm }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new AddonException("compiler_start", "Could not start Javy.");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken); }
            catch { process.Kill(true); throw; }
            _ = await stdout;
            string errors = await stderr;
            Contract.Require(process.ExitCode == 0, "build_failed", "Javy compilation failed: " + errors);
            var package = AddonPackage.Create(manifest, AddonPackage.ReadBoundedFile(wasm, AddonPackage.MaxModuleBytes));
            output = Path.GetFullPath(output);
            Contract.Require(!File.Exists(output), "already_exists", "Choose a new output filename.");
            SafeFiles.CheckParents(output);
            package.Save(output);
            return package;
        }
        finally
        {
            File.Delete(js);
            File.Delete(wasm);
            if (!Directory.EnumerateFileSystemEntries(temporary).Any()) Directory.Delete(temporary);
        }
    }
}
