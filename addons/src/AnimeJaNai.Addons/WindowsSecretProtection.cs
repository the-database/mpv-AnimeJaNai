using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AnimeJaNai.Addons;

// Windows user-bound DPAPI. Guests never receive this API or its encrypted
// storage. The context binds an encrypted value to its package and destination.
internal static class WindowsSecretProtection
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value, string context)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(Transform(bytes, context, true)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Unprotect(string value, string context)
    {
        byte[] bytes;
        try { bytes = Transform(Convert.FromBase64String(value), context, false); }
        catch (FormatException) { throw new AddonException("credential_unavailable", "The saved credential cannot be opened. Save it again in Manager."); }
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static unsafe byte[] Transform(byte[] bytes, string context, bool encrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new AddonException("feature_unavailable", "Protected credentials currently require Windows.");
        byte[] entropy = SHA256.HashData(Encoding.UTF8.GetBytes(context));
        fixed (byte* inputPointer = bytes, entropyPointer = entropy)
        {
            var input = new Blob { Length = bytes.Length, Data = (IntPtr)inputPointer };
            var extra = new Blob { Length = entropy.Length, Data = (IntPtr)entropyPointer };
            Blob output = default;
            try
            {
                bool ok = encrypt ? CryptProtectData(ref input, null, ref extra, IntPtr.Zero, IntPtr.Zero, 1, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, 1, out output);
                Contract.Require(ok && output.Length is > 0 and <= 16384 && output.Data != IntPtr.Zero,
                    "credential_unavailable", "The saved credential cannot be protected or opened for this Windows user.");
                var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                {
                    if (output.Length is > 0 and <= 16384) CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Data, output.Length));
                    LocalFree(output.Data);
                }
                CryptographicOperations.ZeroMemory(entropy);
            }
        }
    }
}
