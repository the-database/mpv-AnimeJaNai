using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

internal sealed record NativeEncoderPlan(string Family, string Codec, string PixelFormat, string Options)
{
    internal static NativeEncoderPlan Select(NativeEncoding encoding, IReadOnlySet<int> vendors)
    {
        encoding.Validate();
        string family = encoding.Encoder == "auto" ? vendors.Contains(0x10de) ? "nvenc" : vendors.Contains(0x1002) ? "amf" : "unavailable" : encoding.Encoder;
        Contract.Require(family == "nvenc" && vendors.Contains(0x10de) || family == "amf" && vendors.Contains(0x1002),
            "encoder_unavailable", "The selected hardware encoder has no matching graphics adapter. No software or backend fallback was selected.");
        Contract.Require(family != "amf" || encoding.VideoCodec != "av1", "unsupported_format", "AMD AV1 output is not implemented by this adapter.");
        string options = family == "nvenc" ? "preset=p4,tune=ll,bf=0,rc-lookahead=0" : "usage=lowlatency,quality=balanced";
        // HEVC AMF does not expose the H.264 bf option.
        if (family == "amf" && encoding.VideoCodec == "h264") options += ",bf=0";
        options += ",b=" + (encoding.VideoKbps * 1000).ToString(CultureInfo.InvariantCulture) + ",g=" + encoding.KeyframeFrames.ToString(CultureInfo.InvariantCulture);
        if (family == "nvenc" && encoding.KeyframeSeconds > 0) options += ",forced-idr=1";
        return new(family, encoding.VideoCodec + "_" + family, encoding.BitDepth == 8 ? "nv12" : "p010", options);
    }
    internal JsonObject ToJson() => new() { ["family"] = Family, ["codec"] = Codec, ["pixelFormat"] = PixelFormat,
        ["deviceSelection"] = "encoderDefault", ["softwareFallback"] = false, ["hardwareQualification"] = "requiresStreamTest" };

    // DXGI inventory is queried in the isolated native worker. These IDs only
    // identify candidate vendors; actual codec initialization remains decisive.
    internal static HashSet<int> AdapterVendors()
    {
        var result = new HashSet<int>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Contract.Require(CreateDXGIFactory1(ref iid, out var factory) >= 0, "encoder_unavailable", "Graphics adapter discovery failed.");
        try
        {
            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumAdapter>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            for (uint i = 0; i < 32; i++)
            {
                int code = enumerate(factory, i, out var adapter);
                if (code == unchecked((int)0x887a0002)) break;
                Contract.Require(code >= 0, "encoder_unavailable", "Graphics adapter enumeration failed.");
                try
                {
                    var describe = Marshal.GetDelegateForFunctionPointer<GetDescription>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size));
                    IntPtr buffer = Marshal.AllocHGlobal(312);
                    try { if (describe(adapter, buffer) >= 0) result.Add(Marshal.ReadInt32(buffer, 256)); }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }
    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapter(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr adapter, IntPtr description);
}
