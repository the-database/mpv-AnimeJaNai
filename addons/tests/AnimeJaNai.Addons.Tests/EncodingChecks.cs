using System.Diagnostics;
using System.Runtime.InteropServices;
using AnimeJaNai.Addons.Native;
using Microsoft.Win32.SafeHandles;

internal static partial class Checks
{
    private static async Task EncodingChecks()
    {
        await Test("Native encoding validates codec/container/rate options before allocating resources", async () =>
        {
            var options = new NativeEncoding("hevc", "fragmentedMp4", 8000, "aac", 192, 120, 15);
            True(NativeEncoding.Parse(options.ToJson()) == options);
            foreach (var invalid in new[] { options with { VideoCodec = "other" }, options with { Container = "file" },
                options with { VideoKbps = 0 }, options with { VideoKbps = 50001 }, options with { AudioCodec = "other" },
                options with { KeyframeFrames = 0 }, options with { AudioKbps = 1000 }, options with { LengthSeconds = double.NaN },
                options with { LengthSeconds = -1 }, options with { Container = "mpegts", VideoCodec = "av1" },
                options with { Container = "mpegts", AudioCodec = "opus" } })
                await Error("invalid_encoding", invalid.Validate);
        });
        if (!OperatingSystem.IsWindows()) return;
        await Test("Private encoded pipe transfers arbitrary bytes through the UCRT descriptor and closes at EOF", async () =>
        {
            using var pipe = new NativeEncodedPipe();
            using var current = Process.GetCurrentProcess();
            using var handle = new SafeFileHandle(new IntPtr(pipe.DuplicateTo(current)), ownsHandle: true);
            byte[] expected = Enumerable.Range(0, 16384).Select(i => (byte)i).ToArray();
            using var result = new MemoryStream();
            var read = Task.Run(() => pipe.Reader.CopyToAsync(result));
            using (var writer = new NativeEncodedWriter(handle))
                True(WriteDescriptor(writer.Descriptor, expected, (uint)expected.Length) == expected.Length);
            await read.WaitAsync(TimeSpan.FromSeconds(5));
            True(result.ToArray().SequenceEqual(expected));
            True(handle.IsClosed, "UCRT must own the write handle after a successful transfer.");
        });
    }
    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "_write")]
    private static extern int WriteDescriptor(int descriptor, byte[] buffer, uint length);
}
