using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Collections.Frozen;

namespace AnimeJaNai.Addons;

public sealed class AddonException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class Contract
{
    public const int Major = 1;
    public const int Minor = 5;
    public const int MaxMessageBytes = 128 * 1024;
    public static readonly FrozenSet<string> PermissionNames = new[] {
        "log.write", "storage.read", "storage.write", "sessions.manage", "frames.read", "network.connect", "credentials.use", "media.output", "media.input"
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24,
    };

    public static void Require([DoesNotReturnIf(false)] bool condition, string code, string message)
    {
        if (!condition) throw new AddonException(code, message);
    }

    public static bool ValidId(string value) => value.Length <= 100 &&
        Regex.IsMatch(value, @"\A[a-z][a-z0-9]*(?:\.[a-z][a-z0-9-]*)+\z", RegexOptions.CultureInvariant);

    public static bool ValidKey(string value) => value.Length <= 64 &&
        Regex.IsMatch(value, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]*\z", RegexOptions.CultureInvariant);

    public static bool ValidHash(string value) => Regex.IsMatch(value, @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant);

    public static JsonObject ParseObject(ReadOnlySpan<byte> data)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(data);
            using var document = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
            CheckUnique(document.RootElement);
            return JsonNode.Parse(data, documentOptions: new JsonDocumentOptions { MaxDepth = 24 }) as JsonObject
                ?? throw new AddonException("invalid_message", "Expected a JSON object.");
        }
        catch (JsonException) { throw new AddonException("invalid_message", "Invalid JSON message."); }
        catch (DecoderFallbackException) { throw new AddonException("invalid_message", "Message must contain valid UTF-8."); }
    }

    private static void CheckUnique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(names.Add(property.Name), "invalid_message", "Duplicate JSON field.");
                CheckUnique(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckUnique(child);
    }

    public static string Text(JsonObject obj, string key, int maxLength = 256)
    {
        Require(obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length <= maxLength,
            "invalid_request", $"Invalid {key}.");
        return obj[key]!.GetValue<string>();
    }

    public static long Number(JsonObject obj, string key)
    {
        Require(obj[key] is JsonValue v && v.TryGetValue<long>(out _), "invalid_request", $"Invalid {key}.");
        return obj[key]!.GetValue<long>();
    }
}

public sealed record ApiRequirement
{
    public required int Major { get; init; }
    public required int MinMinor { get; init; }
}

public sealed record AddonManifest
{
    public required int SchemaVersion { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required ApiRequirement Api { get; init; }
    public required string ModuleSha256 { get; init; }
    public required string[] Permissions { get; init; }
    public Dictionary<string, ApiRequirement>? RequiredCapabilities { get; init; }
    public string[]? Activation { get; init; }
    public Dictionary<string, SettingDefinition>? Settings { get; init; }
    public Dictionary<string, ActionDefinition>? Actions { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    public void Validate()
    {
        Contract.Require(SchemaVersion == 1, "unsupported_manifest", "Manifest schema must be 1.");
        Contract.Require(Id is not null && Contract.ValidId(Id), "invalid_manifest", "Use a lowercase reverse-domain addon id.");
        Contract.Require(Name is { Length: > 0 and <= 100 } && !Name.Any(char.IsControl), "invalid_manifest", "Invalid addon name.");
        Contract.Require(Version is { Length: > 0 and <= 80 } && Regex.IsMatch(Version,
            @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?\z"),
            "invalid_manifest", "Use a major.minor.patch version, optionally with a prerelease suffix.");
        Contract.Require(Api is not null && Api.Major == Contract.Major && Api.MinMinor >= 0 && Api.MinMinor <= Contract.Minor,
            "incompatible_api", "Addon requires an unsupported API version.");
        Contract.Require(ModuleSha256 is not null && Contract.ValidHash(ModuleSha256), "invalid_manifest", "Invalid module SHA256.");
        Contract.Require(Permissions is not null && Permissions.Length <= Contract.PermissionNames.Count &&
            Permissions.All(p => p is not null && Contract.PermissionNames.Contains(p)) &&
            Permissions.Distinct(StringComparer.Ordinal).Count() == Permissions.Length,
            "invalid_manifest", "Unknown or duplicate permission.");
        Contract.Require(RequiredCapabilities is null || (RequiredCapabilities.Count <= 16 && RequiredCapabilities.All(p =>
            Contract.ValidKey(p.Key) && p.Value is not null && p.Value.Major > 0 && p.Value.MinMinor >= 0)),
            "invalid_manifest", "Invalid required capabilities.");
        Contract.Require(Activation is null || (Activation.Length is > 0 and <= 4 && Activation.Distinct(StringComparer.Ordinal).Count() == Activation.Length &&
            Activation.All(a => a is "manual" or "on_manager" or "on_player" or "on_login")), "invalid_manifest", "Invalid activation triggers.");
        Contract.Require(Settings is null || (Settings.Count <= 32 && Settings.All(p => Contract.ValidKey(p.Key) && p.Value is not null)),
            "invalid_manifest", "Invalid settings definitions.");
        foreach (var (_, setting) in Settings ?? []) setting.Validate();
        Contract.Require(Actions is null || (Actions.Count <= 16 && Actions.All(p => Contract.ValidKey(p.Key) && p.Value is not null)),
            "invalid_manifest", "Invalid action definitions.");
        foreach (var (_, action) in Actions ?? []) action.Validate();
    }
}

public sealed class PermissionGrant
{
    private readonly HashSet<string> allowed;
    public string PackageHash { get; }
    public IReadOnlyCollection<string> Allowed => allowed.ToArray();
    public PermissionGrant(AddonPackage package, IEnumerable<string> permissions)
    {
        PackageHash = package.Hash;
        allowed = new(permissions, StringComparer.Ordinal);
        Contract.Require(allowed.IsSubsetOf(package.Manifest.Permissions), "invalid_grant", "Grant contains permissions the addon did not request.");
    }
    public void Demand(string permission) => Contract.Require(allowed.Contains(permission), "permission_denied", $"Permission not granted: {permission}.");
}
