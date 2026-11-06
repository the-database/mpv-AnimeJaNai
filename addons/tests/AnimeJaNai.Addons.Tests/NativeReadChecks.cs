using System.Runtime.InteropServices;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task NativeReadChecks()
    {
        await Test("Native input returns bounded partial reads with untouched destination guards", () =>
        {
            foreach (int requested in new[] { 1, 32768, 32769, 10110966, int.MaxValue })
            foreach (int shortRead in new[] { 7, 32768 })
            {
                using var source = new ReadFixture(shortRead);
                var metrics = new NativeReadMetrics();
                var reader = new NativeReadBuffer(source, default, metrics: metrics);
                GuardedRead(target => reader.Read(target, requested), Math.Min(requested, shortRead));
                True(source.LargestRead == Math.Min(requested, 32768));
                True(reader.BytesRead == Math.Min(requested, shortRead));
                True(metrics.LargestRequest == requested && metrics.BytesRead == reader.BytesRead);
            }
        });
        await Test("Native input retains EOF cancellation source-error and total-budget semantics", async () =>
        {
            using var source = new ReadFixture(32768);
            var reader = new NativeReadBuffer(source, default, 5, "subtitle_read_limit");
            GuardedRead(target => reader.Read(target, 10110966), 5);
            True(source.LargestRead == 5 && reader.BytesRead == 5);
            await Error("subtitle_read_limit", () => reader.Read(IntPtr.Zero, 1));
            True(source.Calls == 1);
            foreach (int invalid in new[] { 0, -1, int.MinValue })
                await Error("native_read_invalid", () => new NativeReadBuffer(source, default).Read(IntPtr.Zero, invalid));
            using var eof = new ReadFixture(0);
            GuardedRead(target => new NativeReadBuffer(eof, default).Read(target, 10110966), NativeReadBuffer.EndOfFile);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { new NativeReadBuffer(source, cancelled.Token).Read(IntPtr.Zero, 1); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            True(source.Calls == 1);
            using var broken = new ReadFixture(0) { Fail = true };
            try { new NativeReadBuffer(broken, default).Read(IntPtr.Zero, 10110966); throw new Exception("Expected source error"); }
            catch (IOException) { }
        });
        await Test("Native input mux callback preserves partial reads EOF and translated failures", () =>
        {
            foreach (int size in new[] { 1, 32768, 32769, 10110966 })
            {
                using var source = new ReadFixture(7);
                using var mux = new NativeMuxer(Area(), source, default);
                GuardedRead(target => mux.Read(IntPtr.Zero, target, size), Math.Min(size, 7));
                True(mux.Read(IntPtr.Zero, IntPtr.Zero, 0) == -22);
            }
            using var eof = new NativeMuxer(Area(), Stream.Null, default);
            GuardedRead(target => eof.Read(IntPtr.Zero, target, 10110966), NativeReadBuffer.EndOfFile);
            using var broken = new ReadFixture(0) { Fail = true };
            using var failed = new NativeMuxer(Area(), broken, default);
            GuardedRead(target => failed.Read(IntPtr.Zero, target, 10110966), -5);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            using var stopped = new NativeMuxer(Area(), Stream.Null, cancelled.Token);
            GuardedRead(target => stopped.Read(IntPtr.Zero, target, 1), -5);
        });
    }

    private static void GuardedRead(Func<IntPtr, int> read, int expected)
    {
        byte[] guard = Enumerable.Repeat((byte)0xcd, 32770).ToArray();
        IntPtr memory = Marshal.AllocHGlobal(guard.Length);
        try
        {
            Marshal.Copy(guard, 0, memory, guard.Length);
            True(read(IntPtr.Add(memory, 1)) == expected);
            Marshal.Copy(memory, guard, 0, guard.Length);
            int count = Math.Max(0, expected);
            True(guard[0] == 0xcd && guard.Skip(1 + count).All(b => b == 0xcd));
            True(guard.Skip(1).Take(count).All(b => b == 0x5a));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private sealed class ReadFixture(int limit) : MemoryStream
    {
        internal int LargestRead, Calls;
        internal bool Fail;
        public override int Read(byte[] buffer, int offset, int count)
        {
            Calls++; LargestRead = Math.Max(LargestRead, count);
            if (Fail) throw new IOException("Synthetic read failure");
            int actual = Math.Min(count, limit); Array.Fill(buffer, (byte)0x5a, offset, actual); return actual;
        }
    }
}
