using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;

namespace AnimeJaNai.Addons;

// Declarative data only. The host UI never loads addon HTML, scripts, or controls.
public sealed record SettingDefinition
{
    public required string Type { get; init; }
    public required string Label { get; init; }
    public required JsonNode Default { get; init; }
    public string? Description { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public int? MaxLength { get; init; }
    public string[]? Choices { get; init; }

    public void Validate()
    {
        Contract.Require(Type is "boolean" or "number" or "string" or "choice", "invalid_manifest", "Unknown setting type.");
        LabelText(Label, 100);
        if (Description is not null) LabelText(Description, 500);
        Contract.Require((Minimum is null || double.IsFinite(Minimum.Value)) && (Maximum is null || double.IsFinite(Maximum.Value)) &&
            (Minimum is null || Maximum is null || Minimum <= Maximum), "invalid_manifest", "Invalid setting bounds.");
        Contract.Require(Type == "number" || (Minimum is null && Maximum is null), "invalid_manifest", "Only numbers accept numeric bounds.");
        Contract.Require(MaxLength is null || (Type == "string" && MaxLength is >= 1 and <= 4096), "invalid_manifest", "Invalid string limit.");
        Contract.Require(Type == "choice" ? Choices is { Length: > 0 and <= 32 } &&
            Choices.All(c => c is { Length: > 0 and <= 100 } && !c.Any(char.IsControl)) && Choices.Distinct(StringComparer.Ordinal).Count() == Choices.Length
            : Choices is null, "invalid_manifest", "Invalid setting choices.");
        Contract.Require(Accepts(Default), "invalid_manifest", "Invalid setting default.");
    }

    public bool Accepts(JsonNode? value)
    {
        if (value is not JsonValue v) return false;
        return Type switch
        {
            "boolean" => v.TryGetValue<bool>(out _),
            "number" => Number(v, out var number) && double.IsFinite(number) &&
                (Minimum is null || number >= Minimum) && (Maximum is null || number <= Maximum),
            "string" => v.TryGetValue<string>(out var text) && text.Length <= (MaxLength ?? 4096),
            "choice" => v.TryGetValue<string>(out var choice) && Choices is not null && Choices.Contains(choice, StringComparer.Ordinal),
            _ => false,
        };
    }

    private static bool Number(JsonValue value, out double number)
    {
        number = 0;
        return value.GetValueKind() == JsonValueKind.Number && (value.TryGetValue<double>(out number) ||
            double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number));
    }

    internal static void LabelText(string text, int maximum) => Contract.Require(text is { Length: > 0 } && text.Length <= maximum &&
        !text.Any(char.IsControl), "invalid_manifest", "Invalid display text.");
    public SettingDefinition Copy() => this with { Default = Default.DeepClone(), Choices = Choices?.ToArray() };
}

public sealed record ActionDefinition
{
    public required string Label { get; init; }
    public string? Description { get; init; }
    public void Validate()
    {
        SettingDefinition.LabelText(Label, 100);
        if (Description is not null) SettingDefinition.LabelText(Description, 500);
    }
}

public sealed class AddonSettings
{
    private readonly AddonManifest manifest;
    private readonly string directory;
    private string SettingsPath => Path.Combine(directory, "settings.json");
    public AddonSettings(string root, AddonManifest manifest)
    {
        manifest.Validate();
        this.manifest = manifest with { Settings = manifest.Settings?.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal) };
        directory = SafeFiles.DirectoryPath(root, "configuration", manifest.Id);
    }

    public JsonObject Get()
    {
        using var held = SafeFiles.Lock(directory);
        return Effective(Read());
    }

    // Trusted editors need a repair path when a new schema rejects an old value.
    // Showing a default does not change the saved value until the user saves.
    public (JsonObject Values, string[] Invalid) GetForEditing()
    {
        using var held = SafeFiles.Lock(directory);
        var stored = Read(); var values = new JsonObject(); List<string> invalid = [];
        foreach (var (key, definition) in manifest.Settings ?? [])
        {
            if (stored.TryGetPropertyValue(key, out var value))
            {
                if (definition.Accepts(value)) { values[key] = value!.DeepClone(); continue; }
                invalid.Add(key);
            }
            values[key] = definition.Default.DeepClone();
        }
        return (values, invalid.ToArray());
    }

    // This method is available to trusted host UI/CLI only, never to a worker.
    public JsonObject Update(JsonObject changes)
    {
        Contract.Require(JsonSerializer.SerializeToUtf8Bytes(changes, Contract.Json).Length <= 64 * 1024,
            "invalid_settings", "Settings patch exceeds 64 KiB.");
        foreach (var (key, value) in changes)
            Contract.Require(manifest.Settings is not null && manifest.Settings.TryGetValue(key, out var definition) && definition.Accepts(value),
                "invalid_settings", $"Invalid setting: {key}.");
        using var held = SafeFiles.Lock(directory);
        var stored = Read();
        foreach (var (key, value) in changes) stored[key] = value?.DeepClone();
        var effective = Effective(stored);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stored, Contract.Json);
        Contract.Require(bytes.Length <= 64 * 1024, "invalid_settings", "Saved settings exceed 64 KiB.");
        SafeFiles.AtomicWrite(SettingsPath, bytes);
        return effective;
    }

    private JsonObject Effective(JsonObject stored)
    {
        var result = new JsonObject();
        foreach (var (key, definition) in manifest.Settings ?? [])
        {
            if (stored.TryGetPropertyValue(key, out var value))
            {
                Contract.Require(definition.Accepts(value), "settings_incompatible", $"Saved setting {key} is incompatible with this addon version. Update that setting before activation.");
                result[key] = value?.DeepClone();
            }
            else result[key] = definition.Default.DeepClone();
        }
        // Unknown saved keys stay on disk for rollback but are not exposed to the addon.
        return result;
    }

    private JsonObject Read()
    {
        SafeFiles.CheckParents(SettingsPath);
        if (!File.Exists(SettingsPath)) return [];
        try { return Contract.ParseObject(AddonPackage.ReadBoundedFile(SettingsPath, 64 * 1024)); }
        catch (AddonException) { throw new AddonException("invalid_settings", "Settings file is invalid and has been preserved for recovery."); }
    }
}
