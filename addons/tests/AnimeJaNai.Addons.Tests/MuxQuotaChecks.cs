using System.Runtime.InteropServices;
using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task MuxQuotaChecks()
    {
        await Test("Native mux storage pressure waits for eligible media to expire without exceeding quota", async () =>
        {
            string area = Area(); var gate = new MuxDemandGate(0);
            using var mux = new NativeMuxer(area, Stream.Null, default, maximumBytes: 8, pressureTimeoutMs: 1000) { Demand = gate };
            IntPtr bytes = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.Copy(new byte[8], 0, bytes, 8);
                long first = mux.Open(IntPtr.Zero, "part_00000000.ts"); True(mux.Write(IntPtr.Zero, first, bytes, 8) == 8); mux.Close(IntPtr.Zero, first);
                long next = mux.Open(IntPtr.Zero, "part_00000001.ts");
                var write = Task.Run(() => mux.Write(IntPtr.Zero, next, bytes, 8));
                await Until(() => gate.Status()["storagePaused"]!.GetValue<bool>());
                True(!write.IsCompleted && new FileInfo(Path.Combine(area, "part_00000001.ts")).Length == 0);
                File.Delete(Path.Combine(area, "part_00000000.ts"));
                True(await write == 8 && !gate.Status()["storagePaused"]!.GetValue<bool>()); mux.Close(IntPtr.Zero, next);
            }
            finally { Marshal.FreeHGlobal(bytes); }
        });
        await Test("Native mux writes enforce the shared file quota before touching payload bytes", () =>
        {
            string area = Area(); using var mux = new NativeMuxer(area, Stream.Null, default, maximumBytes: 8, pressureTimeoutMs: 25);
            IntPtr bytes = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.Copy(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 0, bytes, 8);
                long first = mux.Open(IntPtr.Zero, "part_00000000.ts"); True(first > 0);
                True(mux.Write(IntPtr.Zero, first, bytes, 6) == 6); True(mux.Close(IntPtr.Zero, first) == 0);
                long second = mux.Open(IntPtr.Zero, "part_00000001.ts"); True(second > 0);
                True(mux.Write(IntPtr.Zero, second, bytes, 3) == -28); mux.Close(IntPtr.Zero, second);
                True(new FileInfo(Path.Combine(area, "part_00000000.ts")).Length == 6);
                True(new FileInfo(Path.Combine(area, "part_00000001.ts")).Length == 0);
            }
            finally { Marshal.FreeHGlobal(bytes); }
        });
        await Test("Native mux quota credits expired closed objects and denies alternate output paths", () =>
        {
            string area = Area(); using var mux = new NativeMuxer(area, Stream.Null, default, maximumBytes: 8);
            IntPtr bytes = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.Copy(new byte[8], 0, bytes, 8);
                long first = mux.Open(IntPtr.Zero, "part_00000000.ts"); True(mux.Write(IntPtr.Zero, first, bytes, 8) == 8); mux.Close(IntPtr.Zero, first);
                File.Delete(Path.Combine(area, "part_00000000.ts"));
                long second = mux.Open(IntPtr.Zero, "part_00000001.ts"); True(mux.Write(IntPtr.Zero, second, bytes, 8) == 8); mux.Close(IntPtr.Zero, second);
                True(mux.Open(IntPtr.Zero, "../other.ts") < 0);
                True(!File.Exists(Path.Combine(Path.GetDirectoryName(area)!, "other.ts")));
            }
            finally { Marshal.FreeHGlobal(bytes); }
        });
    }
}
