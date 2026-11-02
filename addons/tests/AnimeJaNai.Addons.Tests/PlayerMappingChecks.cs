using AnimeJaNai.Addons.Native;
using System.IO.MemoryMappedFiles;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;

internal static partial class Checks
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task PlayerMappingChecks()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Test("Named samples apply owner-only ACLs and preserve views through host disposal", () =>
        {
            using var buffer = new NativeFrameBuffer(player: true);
            using var mapping = MemoryMappedFile.OpenExisting(buffer.Name!, MemoryMappedFileRights.ReadWrite | MemoryMappedFileRights.ReadPermissions);
            using var view = mapping.CreateViewAccessor();
            True(GetPlayerTestSecurityInfo(mapping.SafeMemoryMappedFileHandle.DangerousGetHandle(), 6, 4, out _, out _, out _, out _, out var descriptor) == 0);
            try
            {
                uint length = GetPlayerTestDescriptorLength(descriptor); True(length is > 0 and <= 4096);
                byte[] bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                var security = new RawSecurityDescriptor(bytes, 0);
                True(security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
                var rules = security.DiscretionaryAcl!.Cast<CommonAce>().ToArray();
                using var identity = WindowsIdentity.GetCurrent();
                True(rules.Length == 2 && rules.Any(a => a.AceQualifier == AceQualifier.AccessAllowed && a.SecurityIdentifier == identity.User) &&
                    rules.Any(a => a.AceQualifier == AceQualifier.AccessDenied && a.SecurityIdentifier.IsWellKnown(WellKnownSidType.NetworkSid)));
            }
            finally { FreeLocalPlayerTest(descriptor); }
            using var lease = buffer.Subscribe(new(1, 1, 30), repeatLatest: true);
            buffer.RenewPlayerLease(); True(view.ReadInt64(16) != 0 && view.ReadInt64(168) > Environment.TickCount64);
            buffer.Dispose(); True(view.ReadInt64(16) == 0 && view.ReadInt64(168) == 0);
        });
        await Test("Shared snapshots repeat safely but invalidate on seek, lease expiry and producer replacement", () =>
        {
            using var buffer = new NativeFrameBuffer(player: true);
            using var mapping = MemoryMappedFile.OpenExisting(buffer.Name!);
            using var view = mapping.CreateViewAccessor();
            using var lease = buffer.Subscribe(new(1, 1, 30), repeatLatest: true);
            buffer.RenewPlayerLease();
            long sequence = 0;
            void Publish(long id, long epoch, long producer)
            {
                view.Write(32, ++sequence); view.Write(24, epoch); view.Write(40, id); view.Write(48, view.ReadInt64(16)); view.Write(56, epoch);
                foreach (var (offset, value) in new[] { (80, 1), (84, 1), (88, 4), (92, 4), (96, 1), (100, 1), (104, 3), (112, 1), (116, 1), (124, 1), (128, 1), (144, 1), (148, 1) }) view.Write(offset, value);
                view.Write(160, producer); view.Write(176, (producer << 32) | 3); view.WriteArray(256, new byte[] { 20, 40, 60, 255 }, 0, 4); view.Write(32, ++sequence);
            }
            Publish(1, 1, 1);
            var first = lease.ReadLatest(); True(first is not null && ReferenceEquals(first, lease.ReadLatest()));
            buffer.InvalidateForSeek(); True(lease.ReadLatest() is null);
            Publish(2, 2, 1); True(lease.ReadLatest()!.Metadata["epoch"]!.GetValue<string>() == "2");
            view.Write(168, Environment.TickCount64 - 1); True(lease.ReadLatest() is null);
            buffer.RenewPlayerLease(); True(lease.ReadLatest() is not null);
            view.Write(160, 2L); view.Write(24, 3L); view.Write(176, (1L << 32) | 5);
            True(lease.ReadLatest() is null);
            Publish(3, 3, 2); True(lease.ReadLatest() is not null);
        });
    }
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo")] private static extern uint GetPlayerTestSecurityInfo(IntPtr handle, int type, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength")] private static extern uint GetPlayerTestDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree")] private static extern IntPtr FreeLocalPlayerTest(IntPtr value);
}
