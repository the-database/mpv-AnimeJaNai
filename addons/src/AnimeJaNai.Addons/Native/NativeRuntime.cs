using System.Runtime.InteropServices;

namespace AnimeJaNai.Addons.Native;

// Native dependencies can install process-wide callbacks which outlive an
// individual mpv context. Keep one runtime loaded until process exit; unloading
// and reloading its DLL graph can leave those callbacks pointing into freed
// code (the packaged ggml C++ terminate handler is one such dependency).
// Production uses one supervised process per session, so exiting that process
// releases the runtime and lets the host replace versions without in-process
// hot swapping or accumulating differently-versioned runtime graphs.
internal static class NativeRuntime
{
    private static readonly object Sync = new();
    private static string? selectedPath;
    private static IntPtr library;
    internal static IntPtr Load(string installRoot)
    {
        string path = Path.GetFullPath(Path.Combine(installRoot, "libmpv-2.dll"));
        lock (Sync)
        {
            if (library != IntPtr.Zero)
            {
                Contract.Require(path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase),
                    "native_runtime_busy", "A native worker cannot switch runtime installations. Start a new worker.");
                return library;
            }
            var loaded = NativeLibrary.Load(path, typeof(NativeRuntime).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            selectedPath = path; library = loaded;
            return library;
        }
    }
}
