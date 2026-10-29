using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AnimeJaNai.Addons.Native;

// A fixed-size mapping shared only with trusted native code. Owned sessions use
// an unnamed handle; normal players use an explicitly protected named object.
// Addons receive a validated copy of one reduced sample over the broker pipe.
internal sealed unsafe class NativeFrameBuffer : IDisposable
{
    internal const int HeaderBytes = 256, Capacity = FrameRequest.MaxBytes, Size = HeaderBytes + Capacity;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly object sync = new();
    private byte* pointer;
    private int generation;
    private Lease? active;
    private bool disposed;
    private long minimumEpoch;
    private readonly string stage;
    private readonly bool analysis;
    private readonly int size;
    internal string? Name { get; }
    internal bool HasSubscription { get { lock (sync) { return !disposed && active is not null; } } }

    public NativeFrameBuffer(bool player = false, string stage = "processed", bool analysis = false)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) throw new PlatformNotSupportedException();
        this.stage = stage; this.analysis = analysis;
        size = Size + (analysis ? Capacity / 2 : 0);
        Name = player ? "Local\\AJN.PlayerFrames." + Guid.NewGuid().ToString("N") : null;
        mapping = Name is null ? MemoryMappedFile.CreateNew(null, size, MemoryMappedFileAccess.ReadWrite) : CreatePlayerMapping(Name, size);
        try
        {
            view = mapping.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            new Span<byte>(pointer, size).Clear();
            Write32(0, 0x31464a41); Write32(4, analysis ? 2 : 1); Write32(8, size - HeaderBytes); Write32(12, HeaderBytes); Write32(104, 1);
            Write32(184, analysis ? 1 : 0); Write32(188, stage == "source" ? 1 : 0);
        }
        catch { view?.Dispose(); mapping.Dispose(); throw; }
    }

    // Used only by the supervisor or same-process native tests, never by guests.
    internal long Handle { get { lock (sync) { Alive(); return mapping.SafeMemoryMappedFileHandle.DangerousGetHandle().ToInt64(); } } }
    internal long DuplicateTo(Process process)
    {
        lock (sync)
        {
            Alive();
            if (!DuplicateHandle(new IntPtr(-1), mapping.SafeMemoryMappedFileHandle.DangerousGetHandle(), process.Handle,
                    out var target, 0, false, 2)) throw new IOException("Could not share the private media sample buffer.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            return target.ToInt64();
        }
    }
    public IFrameSubscription Subscribe(FrameRequest request, bool repeatLatest = false)
    {
        request.Validate();
        Contract.Require(request.Stage == stage && request.CharacterMask == analysis, "invalid_request", "Observation channel does not match its producer.");
        lock (sync)
        {
            Alive();
            Contract.Require(active is null, "capacity_exceeded", "This session already has a sample subscription.");
            Contract.Require(generation < int.MaxValue, "capacity_exceeded", "Reopen the session to renew its sample generation.");
            long config = ((long)++generation << 32) | ((long)request.MaxFps << 24) | ((long)request.Height << 12) | (uint)request.Width;
            active = new(this, config, repeatLatest);
            Exchange64(16, config);
            return active;
        }
    }

    internal void RenewPlayerLease()
    {
        lock (sync) { Alive(); if (Name is not null) Exchange64(168, Environment.TickCount64 + 3000); }
    }

    public void InvalidateForSeek()
    {
        lock (sync)
        {
            if (!disposed) minimumEpoch = checked(Math.Max(1, Read64(24)) + 1);
        }
    }

    private FramePacket? Read(Lease lease)
    {
        lock (sync)
        {
            Alive();
            Contract.Require(active == lease, "subscription_not_found", "Frame subscription is closed.");
            int state = Volatile.Read(ref *(int*)(pointer + 104));
            if (Name is not null)
            {
                if (Read64(168) <= Environment.TickCount64) return null;
                long status = Read64(176), producer = Read64(160);
                state = status >> 32 == producer ? (int)status : 1;
                if (state == 6) return null;
            }
            Contract.Require(state != 4, "frame_format_unavailable", "This observation requires progressive mono SDR GPU frames (character masks: NVIDIA CUDA only).");
            Contract.Require(state != 5, "frame_unavailable", "The native GPU sample branch failed. Video may continue without samples.");
            if (state is 7 or 8 or 9) return null;
            Span<byte> header = stackalloc byte[HeaderBytes];
            // A busy or torn snapshot is a dropped sample, never an unbounded
            // retry loop and never a wait for the native producer.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                long sequence = Read64(32);
                if (sequence == 0 || (sequence & 1) != 0) continue;
                new ReadOnlySpan<byte>(pointer, HeaderBytes).CopyTo(header);
                long config = BinaryPrimitives.ReadInt64LittleEndian(header[48..]);
                long epoch = BinaryPrimitives.ReadInt64LittleEndian(header[56..]);
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(header[40..]);
                if (config != lease.Config || epoch != Read64(24) || epoch < minimumEpoch) return null;
                if (id == lease.LastId)
                {
                    if (!lease.RepeatLatest) return null;
                    Thread.MemoryBarrier();
                    if (sequence != Read64(32) || config != Read64(16) || epoch != Read64(24)) continue;
                    return lease.Cached;
                }
                int width = BinaryPrimitives.ReadInt32LittleEndian(header[80..]);
                int height = BinaryPrimitives.ReadInt32LittleEndian(header[84..]);
                int stride = BinaryPrimitives.ReadInt32LittleEndian(header[88..]);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header[92..]);
                // Check before allocation/copy even though the producer is trusted.
                if (width is < 1 or > 320 || height is < 1 or > 180 || stride != width * 4 || length != stride * height || length > Capacity)
                {
                    if (Read64(32) != sequence) continue;
                    throw new AddonException("invalid_frame", "Native sample bounds are invalid.");
                }
                byte[] pixels = new ReadOnlySpan<byte>(pointer + HeaderBytes, length).ToArray();
                byte[] mask = analysis ? new ReadOnlySpan<byte>(pointer + HeaderBytes + Capacity, width * height * 2).ToArray() : [];
                Thread.MemoryBarrier();
                if (sequence != Read64(32) || config != Read64(16) || epoch != Read64(24)) continue;
                var packet = Decode(header, pixels, lease.LastId, mask);
                lease.LastId = id;
                if (lease.RepeatLatest) lease.Cached = packet;
                return packet;
            }
            return null;
        }
    }

    private FramePacket Decode(ReadOnlySpan<byte> header, byte[] pixels, ulong previous, byte[] mask)
    {
        // Local functions cannot capture spans. Copy only the small header for
        // straightforward bounded decoding; pixels remain binary throughout.
        byte[] h = header.ToArray();
        int I(int offset) => BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(offset));
        long L(int offset) => BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(offset));
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(40));
        int sourceWidth = I(96), sourceHeight = I(100), x = I(136), y = I(140), cropWidth = I(144), cropHeight = I(148);
        Contract.Require(sourceWidth is >= 1 and <= 65536 && sourceHeight is >= 1 and <= 65536 && x >= 0 && y >= 0 && cropWidth > 0 && cropHeight > 0 &&
            x <= sourceWidth - cropWidth && y <= sourceHeight - cropHeight && I(120) is >= 0 and <= 359 && I(124) >= 0 && I(128) >= 0 && I(152) is 0 or 1,
            "invalid_frame", "Invalid native sample geometry.");
        string primaries = I(112) switch { 1 => "bt.709", 2 => "bt.601-525", 3 => "bt.601-625", _ => throw new AddonException("invalid_frame", "Invalid sample primaries.") };
        string transfer = I(116) switch { 1 => "bt.1886", 2 => "srgb", 3 => "gamma2.2", 4 => "gamma2.4", _ => throw new AddonException("invalid_frame", "Invalid sample transfer.") };
        long pts = L(64);
        return new(new JsonObject {
            ["frameId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["epoch"] = L(56).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["width"] = (long)I(80), ["height"] = (long)I(84), ["stride"] = I(88), ["format"] = "bgra8", ["stage"] = stage,
            ["ptsSeconds"] = pts == long.MinValue ? null : pts / 1_000_000.0, ["producerTimeMs"] = L(72) / 1_000_000.0,
            ["sourceWidth"] = sourceWidth, ["sourceHeight"] = sourceHeight, ["rotation"] = I(120), ["verticalFlip"] = I(152) != 0,
            ["pixelAspectRatio"] = I(124) > 0 && I(128) > 0 ? I(124) / (double)I(128) : 1,
            ["crop"] = new JsonObject { ["x"] = x, ["y"] = y, ["width"] = cropWidth, ["height"] = cropHeight },
            ["color"] = new JsonObject { ["primaries"] = primaries, ["transfer"] = transfer, ["matrix"] = "rgb", ["range"] = "full", ["alpha"] = "opaque" },
            ["skippedSamples"] = previous > 0 && id > previous ? Math.Min(id - previous - 1, int.MaxValue) : 0,
            ["producerDrops"] = (uint)I(108),
            ["analysis"] = analysis ? new JsonObject { ["model"] = FrameRequest.Model, ["operation"] = "animeCharacterMask",
                ["format"] = "unorm16le", ["foreground"] = 1, ["width"] = I(80), ["height"] = I(84), ["stride"] = I(80) * 2,
                ["backend"] = "TensorRT", ["inputWidth"] = 1024, ["inputHeight"] = 1024,
                ["gpuTimeMs"] = L(192) / 1000.0, ["sampleAgeMs"] = L(200) / 1000.0,
                ["modelSha256"] = NativeAnalysisModel.Sha256 } : null,
        }, pixels, mask);
    }

    internal JsonObject Status()
    {
        lock (sync)
        {
            Alive();
            long packed = Read64(176);
            int status = Name is null ? Volatile.Read(ref *(int*)(pointer + 104)) :
                Read64(168) <= Environment.TickCount64 ? 6 : packed >> 32 == Read64(160) ? (int)packed : 1;
            string state = status switch { 0 or 1 => "waitingForFrame", 2 => "processing", 3 => "ready", 4 => "unsupportedFormat", 5 => "failed",
                6 => "closed", 7 => "loading", 8 => "modelMissing", 9 => "buildingEngine", _ => "failed" };
            return new() { ["state"] = state, ["model"] = analysis ? FrameRequest.Model : null,
                ["errorCode"] = Volatile.Read(ref *(int*)(pointer + 132)), ["producerDrops"] = (uint)Volatile.Read(ref *(int*)(pointer + 108)),
                ["message"] = status switch { 4 => "Use progressive mono SDR CUDA playback for character masks; HDR and software frames are unsupported.",
                    5 => "Native analysis failed. Check the player log and installed TensorRT runtime; restart this subscription after correcting it.",
                    8 => "The approved anime segmentation model is missing. Re-enable the addon to provision it.", _ => null } };
        }
    }

    private void Unsubscribe(Lease lease)
    {
        lock (sync)
        {
            if (disposed || active != lease) return;
            Exchange64(16, (long)generation << 32); active = null;
        }
    }
    private void Alive() => Contract.Require(!disposed, "subscription_not_found", "Native sample source is closed.");
    private long Read64(int offset) => Interlocked.CompareExchange(ref *(long*)(pointer + offset), 0, 0);
    private void Exchange64(int offset, long value) => Interlocked.Exchange(ref *(long*)(pointer + offset), value);
    private void Write32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(pointer + offset, 4), value);
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            Exchange64(16, 0); if (Name is not null) Exchange64(168, 0); active = null; disposed = true;
            view.SafeMemoryMappedViewHandle.ReleasePointer(); pointer = null;
            view.Dispose(); mapping.Dispose();
        }
    }
    private sealed class Lease(NativeFrameBuffer owner, long config, bool repeatLatest) : IFrameSubscription
    {
        internal long Config { get; } = config;
        internal ulong LastId;
        internal bool RepeatLatest { get; } = repeatLatest;
        internal FramePacket? Cached;
        public FramePacket? ReadLatest() => owner.Read(this);
        public JsonObject Status() => owner.Status();
        public void Dispose() => owner.Unsubscribe(this);
    }

    internal static MemoryMappedFile CreatePlayerMapping(string name, int size = Size)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User!.Value;
        // Apply the complete owner-only DACL atomically. CreateNew semantics:
        // reject a pre-existing object, retain our handle while opening the view.
        string descriptor = $"O:{sid}D:P(D;;GA;;;NU)(A;;GA;;;{sid})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(descriptor, 1, out var security, out _))
            throw new IOException("Could not protect the player sample buffer.");
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = security };
            using var handle = CreateFileMappingW(new IntPtr(-1), ref attributes, 4, 0, checked((uint)size), name);
            int error = Marshal.GetLastPInvokeError();
            if (handle.IsInvalid || error == 183) throw new IOException("Could not create a unique player sample buffer.");
            return MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        }
        finally { LocalFree(security); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileMappingW(IntPtr file, ref SecurityAttributes attributes, uint protection, uint high, uint low, string name);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor, uint revision, out IntPtr security, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
