using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace AnimeJaNai.Addons;

public interface ILoginStartupStore
{
    string? Read(string name);
    void Write(string name, string command);
    void Delete(string name);
}

// The current user's one Run value is the durable opt-in. There is no second
// settings file which could disagree with registration after a partial save.
public sealed class LoginSettings
{
    private readonly ILoginStartupStore store;
    private readonly string launcher, command, name;
    public LoginSettings(string installRoot, string dataRoot, ILoginStartupStore? store = null)
    {
        if (store is null && !OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Login startup currently supports Windows.");
        this.store = store ?? new WindowsLoginStartupStore();
        installRoot = Path.GetFullPath(installRoot); dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        launcher = Path.Combine(installRoot, "addon-host", "ajn-addon-launcher.exe");
        bool standardData = string.Equals(dataRoot, Path.Combine(installRoot, "animejanai", "addons"), StringComparison.OrdinalIgnoreCase);
        // Derive the installation from the launcher's location and omit the
        // standard data path to stay within Windows Run's 260-character limit.
        command = string.Join(" ", (standardData ? new[] { launcher, "login" } : [launcher, "login", dataRoot]).Select(Quote));
        // Isolate portable installations while following an explicitly shared
        // data directory across a move/update. Never edit a generic user entry.
        name = ManagementServer.PipeName(dataRoot).Replace("AJN.Addons.v1.", "AnimeJaNai.Addons.", StringComparison.Ordinal);
    }
    public bool IsAvailable => File.Exists(launcher);
    public bool IsEnabled => string.Equals(store.Read(name), command, StringComparison.Ordinal);
    public JsonObject Describe()
    {
        string? current = store.Read(name);
        return new() { ["enabled"] = current == command, ["available"] = IsAvailable,
            ["registeredElsewhere"] = current is not null && current != command };
    }
    public JsonObject Update(bool enabled)
    {
        if (enabled)
        {
            Contract.Require(IsAvailable, "feature_unavailable", "This build does not include the Windows login launcher.");
            Contract.Require(command.Length <= 260, "startup_path_too_long", "Windows login startup needs a shorter installation or addon data path (the command exceeds 260 characters).");
            store.Write(name, command);
        }
        else store.Delete(name);
        return Describe();
    }
    internal static string Quote(string value)
    {
        // Windows argv rules, not shell syntax. Double trailing backslashes
        // before the closing quote so a root directory remains one argument.
        var result = new System.Text.StringBuilder("\""); int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes); result.Append(ch); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsLoginStartupStore : ILoginStartupStore
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
    public void Write(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        key.SetValue(name, command, RegistryValueKind.String);
    }
    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
