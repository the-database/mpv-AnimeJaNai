namespace AnimeJaNai.Addons;

internal static class WorkerFiles
{
    // Called only after obtaining the exclusive data-directory lease, before
    // starting workers. An abrupt process exit cannot run normal disposal.
    internal static void Recover(string dataRoot)
    {
        string root = Path.Combine(Path.GetFullPath(dataRoot), "workers");
        RecoverRoot(root);
        // Relocated workers use a stable directory specific to this data root.
        // The same exclusive host lease therefore protects both locations.
        if (OperatingSystem.IsWindows()) RecoverRoot(WorkerBridge.ShortWorkRoot(root));
    }

    private static void RecoverRoot(string root)
    {
        try
        {
            SafeFiles.CheckParents(root);
            if (!Directory.Exists(root)) return;
            foreach (string directory in Directory.EnumerateDirectories(root).Take(256))
            {
                string name = Path.GetFileName(directory);
                if (!name.StartsWith("worker-", StringComparison.Ordinal) || !Guid.TryParseExact(name[7..], "N", out _)) continue;
                try
                {
                    SafeFiles.CheckParents(directory);
                    // Never recurse, remove unfamiliar contents, or follow a
                    // link. Only the disposable compiled module belongs here.
                    var entries = Directory.EnumerateFileSystemEntries(directory).Take(2).ToArray();
                    string module = Path.Combine(directory, "module.wasm");
                    if (entries.Length > 1 || (entries.Length == 1 && entries[0] != module)) continue;
                    SafeFiles.CheckParents(module);
                    File.Delete(module);
                    Directory.Delete(directory);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or AddonException) { }
            }
        }
        // A locked file can be retried on the next launch. Temporary-file
        // housekeeping must not prevent the user opening their installed addons.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AddonException) { }
    }
}
