using AnimeJaNai.Addons;

// Windows GUI subsystem: Explorer's opt-in login entry never opens a console.
// All addon execution and native sessions still belong to the separate host.
string? dataRoot = null;
try
{
    if (!OperatingSystem.IsWindows()) return 2;
    if (args.Length is 1 or 2 && args[0] == "login")
    {
        string installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        dataRoot = args.Length == 2 ? args[1] : Path.Combine(installRoot, "animejanai", "addons");
        return await LoginAttachment.RunAsync(installRoot, dataRoot);
    }
    if (args.Length is 4 or 5 && args[0] == "player" && int.TryParse(args[3], out int playerId))
    { dataRoot = args[2]; return await PlayerAttachment.RunAsync(args[1], dataRoot, playerId, instance: args.Length == 5 ? args[4] : null); }
    return 2;
}
catch (Exception error)
{
    // A lifecycle failure must not surface as an unhandled .NET crash dialog.
    // Keep one bounded diagnostic in this installation's addon data directory.
    try
    {
        if (dataRoot is not null)
        {
            string root = SafeFiles.DirectoryPath(dataRoot);
            string message = $"{DateTime.UtcNow:O} {error.GetType().Name}: {error.Message}";
            SafeFiles.AtomicWrite(Path.Combine(root, "launcher-error.txt"), System.Text.Encoding.UTF8.GetBytes(message[..Math.Min(message.Length, 2048)]));
        }
    }
    catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException or ArgumentException or AddonException) { }
    return 1;
}
