using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AnimeJaNai.Launcher;

// Shell COM interfaces, source-generated (Native AOT has no built-in COM interop). Only the
// methods used are called, but every vtable slot before them has to be declared in order.

[StructLayout(LayoutKind.Sequential)]
struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
struct PROPVARIANT
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public nint ptr;
}

[StructLayout(LayoutKind.Sequential)]
struct COMDLG_FILTERSPEC
{
    public nint pszName;
    public nint pszSpec;
}

[GeneratedComInterface, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
partial interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
    [PreserveSig] int Commit();
}

[GeneratedComInterface, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
partial interface IShellItem
{
    void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);
    void GetParent(out IShellItem parent);
    void GetDisplayName(uint sigdn, out nint name);
    void GetAttributes(uint mask, out uint attributes);
    void Compare(IShellItem other, uint hint, out int order);
}

[GeneratedComInterface, Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
partial interface IShellItemArray
{
    void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, out nint ppv);
    void GetPropertyStore(int flags, ref Guid riid, out nint ppv);
    void GetPropertyDescriptionList(nint keyType, ref Guid riid, out nint ppv);
    void GetAttributes(int flags, uint mask, out uint attributes);
    void GetCount(out uint count);
    void GetItemAt(uint index, out IShellItem item);
    void EnumItems(out nint enumerator);
}

[GeneratedComInterface, Guid("b4db1657-70d7-485e-8e3e-6fcb5a5c1802")]
partial interface IModalWindow
{
    [PreserveSig] int Show(nint owner);
}

[GeneratedComInterface, Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
partial interface IFileDialog : IModalWindow
{
    void SetFileTypes(uint count, nint specs);
    void SetFileTypeIndex(uint index);
    void GetFileTypeIndex(out uint index);
    void Advise(nint events, out uint cookie);
    void Unadvise(uint cookie);
    void SetOptions(uint options);
    void GetOptions(out uint options);
    void SetDefaultFolder(IShellItem folder);
    void SetFolder(IShellItem folder);
    void GetFolder(out IShellItem folder);
    void GetCurrentSelection(out IShellItem item);
    void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetFileName(out nint name);
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
    void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
    void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
    void GetResult(out IShellItem item);
    void AddPlace(IShellItem item, int place);
    void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
    void Close(int hr);
    void SetClientGuid(ref Guid guid);
    void ClearClientData();
    void SetFilter(nint filter);
}

[GeneratedComInterface, Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
partial interface IFileOpenDialog : IFileDialog
{
    void GetResults(out IShellItemArray items);
    void GetSelectedItems(out IShellItemArray items);
}
