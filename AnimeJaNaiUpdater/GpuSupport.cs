// Which NVIDIA GPUs TensorRT can use, and the TensorRT packs they need. Pure logic on a GPU list
// (NVML's, in Program.cs), so tests/UpdaterGpu can check it with any GPUs.

// One NVIDIA GPU as NVML reports it. ComputeKnown is false when NVML couldn't give the compute
// capability; such a GPU is treated as one TensorRT supports, as before.
public sealed record NvidiaGpu(string Name, int Major, int Minor, string Uuid = "", bool ComputeKnown = true)
{
    // "sm86", the name the builder packs use
    public string Sm => $"sm{Major}{Minor}";
    public string Compute => $"{Major}.{Minor}";
    public bool TensorRtCapable => !ComputeKnown || GpuSupport.TensorRtCapable(Major, Minor);
}

public static class GpuSupport
{
    // TensorRT and TensorRT-RTX need compute capability 7.5+ (Turing: GeForce RTX 20 / GTX 16 series
    // and newer): the shipped TensorRT 11 on CUDA 13 supports nothing older, and libaji's CUDA kernels
    // start at sm75. The trt-ptx pack can't help an older GPU (PTX only JIT-compiles forward), so such
    // a GPU counts as no NVIDIA GPU for TensorRT: DirectML on Windows. animejanai_backend.lua
    // (TRT_MIN_COMPUTE) and installer/linux/AppRun (trt_min_compute) apply the same rule.
    public static readonly (int Major, int Minor) TrtMinComputeCapability = (7, 5);

    public static bool TensorRtCapable(int major, int minor) =>
        major > TrtMinComputeCapability.Major ||
        (major == TrtMinComputeCapability.Major && minor >= TrtMinComputeCapability.Minor);

    public static List<NvidiaGpu> Capable(IEnumerable<NvidiaGpu> gpus) => gpus.Where(g => g.TensorRtCapable).ToList();

    public static List<NvidiaGpu> TooOld(IEnumerable<NvidiaGpu> gpus) => gpus.Where(g => !g.TensorRtCapable).ToList();

    // The TensorRT GPUs as the updater has always reported them: whether there is one, their
    // generations ("sm89, sm120") and names joined with " + ". GPUs too old for TensorRT are left
    // out, so a PC with only those reports no NVIDIA GPU (and gets DirectML).
    public static (bool HasNvidia, string Sm, string GpuName) Summary(IEnumerable<NvidiaGpu> gpus)
    {
        var capable = Capable(gpus);
        var sms = capable.Select(g => g.Sm).Distinct().ToList();
        return sms.Count == 0
            ? (false, "", "")
            : (true, string.Join(", ", sms), string.Join(" + ", capable.Select(g => g.Name)));
    }

    // The TensorRT packs for these GPUs: the runtime, and per GPU generation its builder pack if
    // published, else the PTX fallback (which JIT-compiles for GPUs newer than this TensorRT knows).
    // None when no GPU can run TensorRT.
    public static List<string> RecommendedPacks(IEnumerable<string> packNames, IEnumerable<NvidiaGpu> gpus)
    {
        var names = packNames.ToHashSet();
        var rec = new List<string>();
        var (hasNvidia, sm, _) = Summary(gpus);
        if (!hasNvidia)
        {
            return rec;
        }
        rec.Add("trt-runtime");
        foreach (var s in sm.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var pack = names.Contains($"trt-{s}") ? $"trt-{s}" : "trt-ptx";
            if (!rec.Contains(pack))
            {
                rec.Add(pack);
            }
        }
        return rec;
    }

    // "NVIDIA GeForce GTX 1080 (compute 6.1)", joined with " + "
    public static string Describe(IEnumerable<NvidiaGpu> gpus) =>
        string.Join(" + ", gpus.Select(g => $"{g.Name} (compute {g.Compute})"));

    // Why these GPUs get no TensorRT, for the console and the Manager.
    public static string TooOldMessage(IReadOnlyCollection<NvidiaGpu> old) =>
        $"TensorRT needs an NVIDIA GeForce RTX 20 / GTX 16 series or newer GPU (compute capability " +
        $"{TrtMinComputeCapability.Major}.{TrtMinComputeCapability.Minor}+); {Describe(old)} " +
        $"{(old.Count == 1 ? "is" : "are")} too old for it";
}
