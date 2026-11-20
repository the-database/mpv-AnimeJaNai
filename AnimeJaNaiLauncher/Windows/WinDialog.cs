using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Text;

namespace AnimeJaNai.Launcher;

/// --open-dialog: the Windows file picker, owned by the player's window (modal over it; the player
/// keeps running). Prints each chosen path as UTF-8 on stdout; exit 1 when cancelled.
[SupportedOSPlatform("windows")]
static partial class WinDialog
{
    const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_ALLOWMULTISELECT = 0x200,
               FOS_PATHMUSTEXIST = 0x800, FOS_FILEMUSTEXIST = 0x1000;
    const uint SIGDN_FILESYSPATH = 0x80058000;
    static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    public static int Run(string[] args)
    {
        nint owner = 0;
        bool multi = false, folder = false;
        string? title = null, filter = null, initial = null;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--owner": owner = (nint)long.Parse(Next()); break;
                case "--multi": multi = true; break;
                case "--folder": folder = true; break;
                case "--title": title = Next(); break;
                case "--filter": filter = Next(); break;
                case "--initial": initial = Next(); break;
                default: throw new ArgumentException($"unknown --open-dialog option {args[i]}");
            }
        }

        CoInitializeEx(0, 2 /* COINIT_APARTMENTTHREADED */);
        var clsid = CLSID_FileOpenDialog;
        var iid = typeof(IFileOpenDialog).GUID;
        int hr = CoCreateInstance(ref clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out nint ptr);
        if (hr != 0)
        {
            throw new IOException($"the file dialog could not be created (0x{hr:X8})");
        }
        var cw = new StrategyBasedComWrappers();
        var dialog = (IFileOpenDialog)cw.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
        Marshal.Release(ptr);

        uint options = FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST;
        options |= folder ? FOS_PICKFOLDERS : FOS_FILEMUSTEXIST;
        if (multi)
        {
            options |= FOS_ALLOWMULTISELECT;
        }
        dialog.SetOptions(options);
        if (title is not null)
        {
            dialog.SetTitle(title);
        }
        var allocated = new List<nint>();
        try
        {
            if (!folder && filter is not null)
            {
                // "Video files|*.mkv;*.mp4|All files|*.*": name/pattern pairs.
                var parts = filter.Split('|');
                int n = parts.Length / 2;
                var specs = new COMDLG_FILTERSPEC[n];
                for (int k = 0; k < n; k++)
                {
                    specs[k].pszName = Marshal.StringToCoTaskMemUni(parts[2 * k]);
                    specs[k].pszSpec = Marshal.StringToCoTaskMemUni(parts[2 * k + 1]);
                    allocated.Add(specs[k].pszName);
                    allocated.Add(specs[k].pszSpec);
                }
                if (n > 0)
                {
                    unsafe
                    {
                        fixed (COMDLG_FILTERSPEC* p = specs)
                        {
                            dialog.SetFileTypes((uint)n, (nint)p);
                        }
                    }
                }
            }
            if (initial is not null && Directory.Exists(initial))
            {
                var itemIid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(Path.GetFullPath(initial), 0, ref itemIid, out nint itemPtr) == 0)
                {
                    var item = (IShellItem)cw.GetOrCreateObjectForComInstance(itemPtr, CreateObjectFlags.None);
                    Marshal.Release(itemPtr);
                    dialog.SetFolder(item);
                }
            }

            Log.Write("dialog: showing");
            hr = dialog.Show(owner);
            Log.Write($"dialog: closed 0x{hr:X8}");
            if (hr != 0)
            {
                return 1; // cancelled (HRESULT_FROM_WIN32(ERROR_CANCELLED)) or failed
            }

            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n" };
            dialog.GetResults(out IShellItemArray items);
            items.GetCount(out uint count);
            for (uint k = 0; k < count; k++)
            {
                items.GetItemAt(k, out IShellItem item);
                item.GetDisplayName(SIGDN_FILESYSPATH, out nint name);
                stdout.WriteLine(Marshal.PtrToStringUni(name));
                Marshal.FreeCoTaskMem(name);
            }
            stdout.Flush();
            return 0;
        }
        finally
        {
            foreach (var p in allocated)
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint ppv);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, out nint ppv);
}
