using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace AnimeJaNai.Addons.Native;

// Host-owned admission. Each job includes its native worker and engine builder;
// reservations span all addons and are released only after process cleanup.
internal sealed record NativeResourceBudget(ulong ProcessBytes, ulong JobBytes)
{
    internal JsonObject ToJson() => new() { ["processMemoryBytes"] = ProcessBytes, ["jobMemoryBytes"] = JobBytes };
}

internal static class NativeResourcePolicy
{
    internal const ulong GiB = 1UL << 30;
    private static readonly object Gate = new();
    private static ulong reserved;
    private static ulong commitEnvelope;
    private static int tensorJobs;
    private static bool preparingEngines;
    internal static NativeResourceBudget Select(ApprovedProfile profile, int width, int height, bool preparation = false)
    {
        Contract.Require(width is > 0 and <= 8192 && height is > 0 and <= 8192 && (long)width * height <= 3840L * 2160,
            "unsupported_media", "Native streaming currently admits source video up to 3840×2160 pixels.");
        // Custom slots may contain stacked models and RIFE. Reserve their larger
        // envelope without trusting an addon-supplied model or memory estimate.
        bool complex = profile.Slot == 1001 || profile.Slot >= 1010 || profile.Slot < 1000 && !KnownSingleModel(profile);
        ulong gib = complex ? 8UL : 4UL;
        if ((long)width * height > 1920L * 1080) gib = Math.Max(gib, 8);
        if (profile.Backend == "TensorRT" && profile.Slot != 1003) gib = Math.Max(gib, 6);
        // Cached-only streams and DirectML cannot create a builder. Performance
        // preparation reserves two extra GiB for the small SPAN builder; other
        // TensorRT profiles reserve four. Job process limits also apply to the
        // builder child, which needs a larger ceiling than cached inference.
        ulong builder = preparation && profile.Backend == "TensorRT" ? profile.Slot == 1003 ? 2UL : 4UL : 0;
        return new((gib + builder) * GiB, (gib + builder) * GiB);
    }

    private static bool KnownSingleModel(ApprovedProfile profile)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool selected = false;
        foreach (string raw in profile.Configuration.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) { selected = line.Equals($"[slot_{profile.Slot}]", StringComparison.OrdinalIgnoreCase); continue; }
            int equals = line.IndexOf('=');
            if (selected && equals > 0) settings[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        if (settings.Any(p => p.Key.EndsWith("_rife", StringComparison.OrdinalIgnoreCase) && !new[] { "", "no", "false", "0" }.Contains(p.Value, StringComparer.OrdinalIgnoreCase))) return false;
        var models = settings.Where(p => System.Text.RegularExpressions.Regex.IsMatch(p.Key, @"^chain_\d+_model_\d+_name$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && p.Value.Length > 0).ToArray();
        return models.Length > 0 && models.GroupBy(p => p.Key.Split('_')[1]).All(g => g.Count() == 1) && models.All(p =>
            p.Value.StartsWith("2x_AnimeJaNai_HD_V3.1_Performance_", StringComparison.Ordinal) ||
            p.Value.StartsWith("2x_AnimeJaNai_HD_V3.1_Balanced_", StringComparison.Ordinal));
    }

    internal static IDisposable Reserve(NativeResourceBudget budget, string backend = "DirectML", bool preparation = false)
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        Contract.Require(GlobalMemoryStatusEx(ref memory), "resource_unavailable", "Could not query native memory capacity.");
        ulong total = Math.Min(32 * GiB, memory.TotalPhysical / 4 * 3);
        lock (Gate)
        {
            // Reserve against the same commit snapshot until all reservations
            // drain. Otherwise several unopened jobs could each spend the same
            // currently-free commit capacity.
            if (reserved == 0) commitEnvelope = memory.AvailableCommit > GiB / 2 ? Math.Min(total, memory.AvailableCommit - GiB / 2) : 0;
            bool tensor = backend == "TensorRT";
            Contract.Require(!tensor || (preparation ? tensorJobs == 0 : !preparingEngines), "preparation_busy", "An engine preparation and TensorRT sessions cannot share the engine cache concurrently. Close the active operation first.");
            Contract.Require(budget.JobBytes <= commitEnvelope && reserved <= commitEnvelope - budget.JobBytes &&
                memory.AvailableCommit >= budget.JobBytes + GiB / 2,
                "resource_exhausted", "Insufficient native memory budget. Close a processing session or choose a lighter approved profile.");
            reserved += budget.JobBytes;
            if (tensor) { tensorJobs++; preparingEngines |= preparation; }
        }
        return new Reservation(budget.JobBytes, backend == "TensorRT", preparation);
    }
    private sealed class Reservation(ulong bytes, bool tensor, bool preparation) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) lock (Gate) { reserved -= bytes; if (tensor) { tensorJobs--; if (preparation) preparingEngines = false; } } }
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalCommit, AvailableCommit, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
}
