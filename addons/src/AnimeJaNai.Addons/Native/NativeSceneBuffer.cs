using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace AnimeJaNai.Addons.Native;

// Layout is paired with mpv/video/filter/ajn_scene_shared.h. Only trusted
// processes map it. Guests receive a bounded copy and opaque request IDs.
internal sealed unsafe class NativeSceneBuffer : ISceneSource
{
    internal const int HeaderBytes = 256, Size = HeaderBytes + SceneRequest.MaximumBytes;
    private readonly object sync = new();
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly EventWaitHandle ready, reply;
    private readonly RegisteredWaitHandle registered;
    private byte* pointer;
    private Action? notify;
    private bool disposed;
    private long generation, lastRead;
    private Lease? active;
    internal string Name { get; } = "Local\\AJN.Scene." + Guid.NewGuid().ToString("N");
    internal bool Enabled { get { lock (sync) return !disposed && active is not null; } }

    internal NativeSceneBuffer()
    {
        mapping = NativeFrameBuffer.CreatePlayerMapping(Name, Size);
        try
        {
            view = mapping.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            new Span<byte>(pointer, Size).Clear();
            I(0) = 0x31434a41; I(4) = 1; I(8) = SceneRequest.MaximumBytes; I(12) = HeaderBytes;
            ready = CreateEvent(Name + ".Ready"); reply = CreateEvent(Name + ".Reply");
            registered = ThreadPool.RegisterWaitForSingleObject(ready, (_, _) =>
            {
                Action? callback; lock (sync) callback = disposed ? null : notify;
                callback?.Invoke();
            }, null, Timeout.Infinite, false);
        }
        catch
        {
            reply?.Dispose(); ready?.Dispose();
            if (pointer != null) view?.SafeMemoryMappedViewHandle.ReleasePointer();
            view?.Dispose(); mapping.Dispose(); throw;
        }
    }
    private ref int I(int offset) => ref *(int*)(pointer + offset);
    private ref long L(int offset) => ref *(long*)(pointer + offset);
    private double D(int offset) => *(double*)(pointer + offset);
    private bool Enter() => Interlocked.CompareExchange(ref I(16), 1, 0) == 0;
    private void Leave() => Interlocked.Exchange(ref I(16), 0);
    private void Alive() => Contract.Require(!disposed, "player_closed", "The scene-analysis player has closed.");
    internal void RenewLease()
    {
        lock (sync) { Alive(); Interlocked.Exchange(ref L(56), Environment.TickCount64 + 3000); }
    }
    public ISceneSubscription Subscribe(SceneRequest request, Action readyCallback)
    {
        request.Validate();
        lock (sync)
        {
            Alive(); Contract.Require(active is null, "scene_detector_in_use", "This player already has a detector.");
            Contract.Require(Enter(), "scene_busy", "The native scene buffer is being updated; retry attachment.");
            try
            {
                generation = checked(generation + 1); L(24) = generation;
                I(96) = request.Width; I(100) = request.Height; I(120) = request.DeadlineMs;
                I(108) = 0; I(112) = 0; I(116) = 0; I(124) = 0;
                L(32) = 0; L(48) = 0; lastRead = 0;
                notify = readyCallback; I(20) = 1;
                active = new(this, generation); return active;
            }
            finally { Leave(); }
        }
    }
    private void Check(Lease lease) { Alive(); Contract.Require(active == lease, "scene_detector_not_found", "This detector is no longer attached."); }
    private ScenePair? Read(Lease lease)
    {
        lock (sync)
        {
            Check(lease); if (!Enter()) return null;
            try
            {
                long id = L(32);
                if (id <= 0 || id == lastRead || L(24) != lease.Generation || I(20) == 0 ||
                    I(108) != 1 || L(64) <= Environment.TickCount64 || L(56) <= Environment.TickCount64) return null;
                int width = I(96), height = I(100);
                Contract.Require(width is >= 1 and <= 320 && height is >= 1 and <= 180, "invalid_frame", "Native scene dimensions are invalid.");
                byte[] pixels = new ReadOnlySpan<byte>(pointer + HeaderBytes, width * height * 2).ToArray();
                lastRead = id;
                return new(new JsonObject { ["requestId"] = id.ToString(CultureInfo.InvariantCulture),
                    ["epoch"] = L(128).ToString(CultureInfo.InvariantCulture), ["previousPtsSeconds"] = D(72), ["currentPtsSeconds"] = D(80),
                    ["width"] = (long)width, ["height"] = (long)height, ["sourceWidth"] = (long)I(88), ["sourceHeight"] = (long)I(92),
                    ["format"] = "gray8", ["stage"] = "beforeInterpolation", ["range"] = "full",
                    ["remainingMs"] = Math.Max(0, L(64) - Environment.TickCount64) }, pixels);
            }
            finally { Leave(); }
        }
    }
    private bool Submit(Lease lease, string requestId, int decision)
    {
        Contract.Require(long.TryParse(requestId, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0 && decision is >= -1 and <= 1,
            "invalid_request", "Invalid scene decision or request ID.");
        lock (sync)
        {
            Check(lease); if (!Enter()) return false;
            try
            {
                if (L(24) != lease.Generation || I(20) == 0 || L(32) != id || L(48) == id || I(108) != 1 ||
                    L(64) <= Environment.TickCount64 || L(56) <= Environment.TickCount64) return false;
                I(104) = decision; L(48) = id;
            }
            finally { Leave(); }
            reply.Set(); return true;
        }
    }
    private JsonObject Status(Lease lease)
    {
        lock (sync)
        {
            Check(lease);
            int status = Volatile.Read(ref I(108));
            return new() { ["state"] = status switch { 0 => "waitingForPlayer", 1 => "pending", 2 => "active", 3 => "fallback", 4 => "sampleUnavailable", 5 => "backendUnavailable", 6 => "suspended", _ => "unavailable" },
                ["acceptedPairs"] = Math.Max(0, Volatile.Read(ref I(116))), ["timedOutPairs"] = Math.Max(0, Volatile.Read(ref I(112))) };
        }
    }
    private void Detach(Lease lease)
    {
        lock (sync)
        {
            if (disposed || active != lease) return;
            // Revocation must not wait for a producer holding the header lock.
            Interlocked.Exchange(ref I(20), 0); notify = null; active = null; reply.Set();
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            if (active is not null) Detach(active);
            disposed = true; registered.Unregister(null);
            reply.Dispose(); ready.Dispose();
            view.SafeMemoryMappedViewHandle.ReleasePointer(); pointer = null;
            view.Dispose(); mapping.Dispose();
        }
    }
    private sealed class Lease(NativeSceneBuffer owner, long generation) : ISceneSubscription
    {
        internal long Generation { get; } = generation;
        public ScenePair? Read() => owner.Read(this);
        public bool Submit(string id, int decision) => owner.Submit(this, id, decision);
        public JsonObject Status() => owner.Status(this);
        public void Dispose() => owner.Detach(this);
    }
    private static EventWaitHandle CreateEvent(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent(); string sid = identity.User!.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"O:{sid}D:P(D;;GA;;;NU)(A;;GA;;;{sid})", 1, out var descriptor, out _))
            throw new IOException("Could not protect the scene event.");
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateEventW(ref attributes, false, false, name); int error = Marshal.GetLastPInvokeError();
            if (handle.IsInvalid || error == 183) { handle.Dispose(); throw new IOException("Could not create a unique scene event."); }
            var result = new EventWaitHandle(false, EventResetMode.AutoReset);
            result.SafeWaitHandle.Dispose(); result.SafeWaitHandle = handle; return result;
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateEventW(ref SecurityAttributes attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string name);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor, uint revision, out IntPtr security, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
}
