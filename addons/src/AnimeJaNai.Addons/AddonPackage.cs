using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AnimeJaNai.Addons;

public sealed class AddonPackage
{
    public const int MaxModuleBytes = 16 * 1024 * 1024;
    public const int MaxArchiveBytes = MaxModuleBytes + 64 * 1024;
    private readonly byte[] archive;
    private readonly byte[] module;
    private readonly AddonManifest manifest;
    public AddonManifest Manifest => manifest with
    {
        Permissions = manifest.Permissions.ToArray(),
        RequiredCapabilities = manifest.RequiredCapabilities is null ? null : new(manifest.RequiredCapabilities, StringComparer.Ordinal),
        Activation = manifest.Activation?.ToArray(),
        Settings = manifest.Settings?.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal),
        Actions = manifest.Actions is null ? null : new(manifest.Actions, StringComparer.Ordinal),
        SensitiveRequestFields = manifest.SensitiveRequestFields?.Copy(),
        Metadata = manifest.Metadata is null ? null : new(manifest.Metadata, StringComparer.Ordinal),
    };
    public string Hash { get; }

    private AddonPackage(byte[] archive, byte[] module, AddonManifest manifest)
    {
        this.archive = archive;
        this.module = module;
        this.manifest = manifest;
        Hash = Convert.ToHexStringLower(SHA256.HashData(archive));
    }

    public static AddonPackage Load(string path) => Read(ReadBoundedFile(path, MaxArchiveBytes));

    public static byte[] ReadBoundedFile(string path, int limit)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadBounded(input, limit);
    }

    private static byte[] ReadBounded(Stream input, int limit)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int n;
        while ((n = input.Read(buffer)) > 0)
        {
            Contract.Require(output.Length + n <= limit, "package_too_large", "Package size limit exceeded.");
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }

    public static AddonPackage Read(byte[] bytes)
    {
        Contract.Require(bytes.Length <= MaxArchiveBytes, "package_too_large", "Package size limit exceeded.");
        var snapshot = bytes.ToArray();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(snapshot), ZipArchiveMode.Read);
            Contract.Require(zip.Entries.Count == 2 && zip.Entries.Count(e => e.FullName == "manifest.json") == 1 &&
                zip.Entries.Count(e => e.FullName == "module.wasm") == 1, "invalid_package", "Package must contain exactly manifest.json and module.wasm.");
            foreach (var entry in zip.Entries)
                Contract.Require(((entry.ExternalAttributes >> 16) & 0xF000) != 0xA000,
                    "invalid_package", "Package links are not supported.");
            using var manifestStream = zip.GetEntry("manifest.json")!.Open();
            var manifestBytes = ReadBounded(manifestStream, 16 * 1024);
            _ = Contract.ParseObject(manifestBytes);
            var manifest = JsonSerializer.Deserialize<AddonManifest>(manifestBytes, Contract.Json)
                ?? throw new AddonException("invalid_manifest", "Missing manifest.");
            manifest.Validate();
            using var moduleStream = zip.GetEntry("module.wasm")!.Open();
            var module = ReadBounded(moduleStream, MaxModuleBytes);
            ValidateModule(module);
            Contract.Require(Convert.ToHexStringLower(SHA256.HashData(module)) == manifest.ModuleSha256,
                "integrity_mismatch", "Module does not match its manifest hash.");
            return new(snapshot, module, manifest);
        }
        catch (InvalidDataException) { throw new AddonException("invalid_package", "Invalid addon archive."); }
        catch (JsonException) { throw new AddonException("invalid_manifest", "Invalid manifest fields."); }
    }

    public static void ValidateModule(ReadOnlySpan<byte> bytes) => Contract.Require(bytes.Length >= 8 &&
        bytes[..8].SequenceEqual(new byte[] { 0, 97, 115, 109, 1, 0, 0, 0 }),
        "invalid_module", "Only portable core WebAssembly binaries are accepted.");

    public void WriteModule(string path) => File.WriteAllBytes(path, module);
    public void Save(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(archive);
        stream.Flush(flushToDisk: true);
    }

    public static AddonPackage Create(AddonManifest manifest, byte[] module)
    {
        manifest = manifest with { ModuleSha256 = Convert.ToHexStringLower(SHA256.HashData(module)) };
        manifest.Validate();
        ValidateModule(module);
        Contract.Require(module.Length <= MaxModuleBytes, "package_too_large", "Module size limit exceeded.");
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Write("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Contract.Json));
            Write("module.wasm", module);
            void Write(string name, byte[] content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        return Read(output.ToArray());
    }
}
