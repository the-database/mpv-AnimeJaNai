using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons;

// Per-package certificate material is encrypted for the Windows user. Only
// trusted management operations accept a file path/password; guests see IDs.
public sealed class ListenerCertificates(string root)
{
    private sealed record Saved(int Version, string Name, string ProtectedPfx);
    private readonly object sync = new();
    private string DirectoryFor(AddonPackage package, PermissionGrant grant)
    {
        grant.Demand("network.listen");
        Contract.Require(package.Hash == grant.PackageHash, "invalid_grant", "Certificate consent belongs to a different package.");
        return SafeFiles.DirectoryPath(root, "listener-certificates", package.Manifest.Id, package.Hash);
    }
    private static string Context(AddonPackage package, string id) => "AJN listener certificate/" + package.Manifest.Id + "/" + package.Hash + "/" + id;
    public string Import(AddonPackage package, PermissionGrant grant, string path, string password, string name, string? replaceId = null, string[]? requiredHosts = null)
    {
        Contract.Require(name is { Length: > 0 and <= 100 } && !name.Any(char.IsControl) && password.Length <= 4096,
            "invalid_certificate", "Use a certificate name of 1–100 characters.");
        byte[] input = AddonPackage.ReadBoundedFile(path, 1024 * 1024);
        try
        {
            using var material = LoadMaterial(input, password);
            Validate(material.Certificate, null);
            foreach (string host in requiredHosts ?? []) Validate(material.Certificate, host);
            lock (sync)
            {
                string directory = DirectoryFor(package, grant); using var held = SafeFiles.Lock(directory);
                Contract.Require(replaceId is not null || Directory.EnumerateFiles(directory, "*.json").Take(5).Count() < 4,
                    "capacity_exceeded", "Replace or remove an existing certificate. Maximum: four per addon package.");
                string id = replaceId ?? Guid.NewGuid().ToString("N");
                Contract.Require(Guid.TryParseExact(id, "N", out _), "invalid_certificate", "Invalid certificate ID.");
                if (replaceId is not null) _ = Read(directory, id);
                byte[] pfx = material.All.Export(X509ContentType.Pkcs12)!;
                try
                {
                    Contract.Require(pfx.Length <= 1024 * 1024, "invalid_certificate", "Certificate bundle exceeds 1 MiB.");
                    var saved = new Saved(1, name, Convert.ToBase64String(WindowsSecretProtection.ProtectBytes(pfx, Context(package, id))));
                    SafeFiles.AtomicWrite(Path.Combine(directory, id + ".json"), JsonSerializer.SerializeToUtf8Bytes(saved, Contract.Json));
                }
                finally { CryptographicOperations.ZeroMemory(pfx); }
                return id;
            }
        }
        finally { CryptographicOperations.ZeroMemory(input); }
    }
    public JsonArray List(AddonPackage package, PermissionGrant grant)
    {
        lock (sync)
        {
            string directory = DirectoryFor(package, grant); var result = new JsonArray();
            foreach (string path in Directory.EnumerateFiles(directory, "*.json").Order().Take(4))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                try
                {
                    using var material = Open(package, grant, id);
                    var info = Describe(id, Read(directory, id).Name, material.Certificate); result.Add(info);
                }
                catch (AddonException) { result.Add(new JsonObject { ["id"] = id, ["name"] = "Unavailable certificate", ["error"] = "certificate_unavailable" }); }
            }
            return result;
        }
    }
    internal Material Open(AddonPackage package, PermissionGrant grant, string id, bool serving = false)
    {
        lock (sync)
        {
            var saved = Read(DirectoryFor(package, grant), id);
            byte[] bytes;
            try { bytes = WindowsSecretProtection.UnprotectBytes(Convert.FromBase64String(saved.ProtectedPfx), Context(package, id)); }
            catch (Exception error) when (error is AddonException or FormatException) { throw new AddonException("certificate_unavailable", "Import the certificate again for this Windows user."); }
            try { return LoadMaterial(bytes, null, serving); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    public JsonObject InspectBinding(AddonPackage package, PermissionGrant grant, ListenerBinding binding)
    {
        binding.Validate();
        if (binding.Scheme != "https") return new();
        using var material = Open(package, grant, binding.CertificateId!);
        Validate(material.Certificate, binding.CertificateHost);
        var result = Describe(binding.CertificateId!, Read(DirectoryFor(package, grant), binding.CertificateId!).Name, material.Certificate);
        result["hostnameMatches"] = true; result["hostname"] = binding.CertificateHost;
        return result;
    }
    public void Remove(AddonPackage package, PermissionGrant grant, string id)
    {
        lock (sync)
        {
            string directory = DirectoryFor(package, grant); using var held = SafeFiles.Lock(directory);
            _ = Read(directory, id); File.Delete(Path.Combine(directory, id + ".json"));
        }
    }
    public void Clear(string addonId)
    {
        Contract.Require(Contract.ValidId(addonId), "invalid_id", "Invalid addon ID.");
        lock (sync)
        {
            string directory = SafeFiles.DirectoryPath(root, "listener-certificates", addonId);
            foreach (string version in Directory.EnumerateDirectories(directory))
            {
                if (!Contract.ValidHash(Path.GetFileName(version))) continue;
                SafeFiles.CheckParents(version);
                using var held = SafeFiles.Lock(version);
                foreach (string path in Directory.EnumerateFiles(version, "*.json"))
                {
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) continue;
                    SafeFiles.CheckParents(path); File.Delete(path);
                }
            }
        }
    }
    internal static void Validate(X509Certificate2 certificate, string? hostname)
    {
        Contract.Require(certificate.HasPrivateKey, "certificate_key_missing", "Select a PKCS#12/PFX certificate containing its private key.");
        Contract.Require(DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow < certificate.NotAfter.ToUniversalTime(),
            "certificate_expired", "The certificate is expired or not valid yet. Import its replacement.");
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        Contract.Require(eku is null || eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0"),
            "invalid_certificate", "This certificate does not permit TLS server authentication.");
        Contract.Require(hostname is null || certificate.MatchesHostname(hostname, allowWildcards: true, allowCommonName: false),
            "certificate_hostname_mismatch", "The certificate does not cover the selected hostname in its subject alternative names.");
    }
    private static JsonObject Describe(string id, string name, X509Certificate2 certificate) => new()
    {
        ["id"] = id, ["name"] = name, ["subject"] = certificate.Subject, ["fingerprint"] = certificate.GetCertHashString(HashAlgorithmName.SHA256),
        ["notBefore"] = certificate.NotBefore.ToUniversalTime().ToString("O"), ["notAfter"] = certificate.NotAfter.ToUniversalTime().ToString("O"),
        ["timeValid"] = DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow < certificate.NotAfter.ToUniversalTime(),
        ["trust"] = "client-verifies" // Importing a key does not install trust roots or prove external reachability.
    };
    private static Saved Read(string directory, string id)
    {
        Contract.Require(Guid.TryParseExact(id, "N", out _), "invalid_certificate", "Invalid certificate ID.");
        string path = Path.Combine(directory, id + ".json"); SafeFiles.CheckParents(path);
        try
        {
            var saved = Contract.ParseObject(AddonPackage.ReadBoundedFile(path, 1500 * 1024)).Deserialize<Saved>(Contract.Json);
            Contract.Require(saved is { Version: 1, Name.Length: > 0, ProtectedPfx.Length: > 0 }, "certificate_unavailable", "Invalid saved certificate.");
            return saved;
        }
        catch (Exception e) when (e is IOException or JsonException or AddonException) { throw new AddonException("certificate_unavailable", "The saved certificate is unavailable. Import it again."); }
    }
    private static Material LoadMaterial(byte[] bytes, string? password, bool serving = false)
    {
        X509Certificate2Collection? certificates = null;
        try
        {
            // Windows Schannel cannot use an ephemeral private key. A serving
            // certificate uses a temporary user key container (.NET removes it
            // when the certificate is disposed); no PersistKeySet/trust import.
            var flags = serving && OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;
            certificates = X509CertificateLoader.LoadPkcs12Collection(bytes, password, flags);
            var keys = certificates.Cast<X509Certificate2>().Where(c => c.HasPrivateKey).ToArray();
            Contract.Require(keys.Length == 1 && certificates.Count <= 16, "invalid_certificate", "Select a bundle with one private key and at most sixteen certificates.");
            return new(certificates, keys[0]);
        }
        catch (Exception error) when (error is CryptographicException or AddonException)
        {
            if (certificates is not null) foreach (var certificate in certificates) certificate.Dispose();
            throw new AddonException("invalid_certificate", "Could not open the PFX/PKCS#12 certificate. Check its password and private key.");
        }
    }
    internal sealed class Material(X509Certificate2Collection all, X509Certificate2 certificate) : IDisposable
    {
        public X509Certificate2Collection All { get; } = all;
        public X509Certificate2 Certificate { get; } = certificate;
        public void Dispose() { foreach (var c in All) c.Dispose(); }
    }
}
