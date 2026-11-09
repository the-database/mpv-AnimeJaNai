// The updater's TensorRT GPU rules (GpuSupport): which NVIDIA GPUs count, and the packs they get.

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    if (!ok)
    {
        failures++;
    }
}
void Same(IEnumerable<string> got, IEnumerable<string> want, string what) =>
    Check(got.SequenceEqual(want), $"{what}: [{string.Join(", ", got)}]");

NvidiaGpu Gpu(string name, int major, int minor) => new(name, major, minor, name.ToLowerInvariant());
var gtx1080 = Gpu("GTX 1080", 6, 1);
var v100 = Gpu("Tesla V100", 7, 0);
var gtx1660 = Gpu("GTX 1660", 7, 5);
var rtx3080 = Gpu("RTX 3080", 8, 6);
var rtx5090 = Gpu("RTX 5090", 12, 0);
var future = Gpu("Future", 13, 0);
string[] packs = ["trt-runtime", "trt-sm75", "trt-sm86", "trt-sm120", "trt-ptx", "rife"];

Check(GpuSupport.TrtMinComputeCapability == (7, 5), "the threshold is compute capability 7.5");
Check(!GpuSupport.TensorRtCapable(6, 1) && !GpuSupport.TensorRtCapable(7, 0) && !GpuSupport.TensorRtCapable(7, 2),
    "Pascal, Volta and Xavier are too old");
Check(GpuSupport.TensorRtCapable(7, 5) && GpuSupport.TensorRtCapable(8, 0) && GpuSupport.TensorRtCapable(12, 0),
    "Turing and newer are supported");

// a PC with only pre-Turing NVIDIA GPUs is a PC without an NVIDIA GPU: no TensorRT packs (DirectML)
Check(GpuSupport.Summary([gtx1080]) == (false, "", ""), "GTX 1080 alone: no NVIDIA GPU for TensorRT");
Check(GpuSupport.Summary([gtx1080, v100]) == (false, "", ""), "GTX 1080 + V100: none either");
Same(GpuSupport.RecommendedPacks(packs, [gtx1080]), [], "GTX 1080 alone: no packs, not trt-ptx");
Same(GpuSupport.RecommendedPacks(packs, [v100]), [], "V100 alone: no packs");
Same(GpuSupport.TooOld([gtx1080, rtx3080]).Select(g => g.Name), ["GTX 1080"], "the old GPU of a mixed pair");

// a mixed system ignores only the old GPU
Check(GpuSupport.Summary([gtx1080, rtx3080]) == (true, "sm86", "RTX 3080"), "GTX 1080 + RTX 3080: the RTX 3080");
Same(GpuSupport.RecommendedPacks(packs, [gtx1080, rtx3080]), ["trt-runtime", "trt-sm86"], "GTX 1080 + RTX 3080 packs");
Same(GpuSupport.Capable([gtx1080, rtx3080]).Select(g => g.Name), ["RTX 3080"], "the TensorRT GPU list");

// supported GPUs keep their packs; trt-ptx stays the fallback for generations newer than the packs
Same(GpuSupport.RecommendedPacks(packs, [gtx1660]), ["trt-runtime", "trt-sm75"], "GTX 1660 (7.5)");
Same(GpuSupport.RecommendedPacks(packs, [rtx5090, rtx3080, rtx3080]), ["trt-runtime", "trt-sm120", "trt-sm86"],
    "RTX 5090 + two RTX 3080");
Same(GpuSupport.RecommendedPacks(packs, [future]), ["trt-runtime", "trt-ptx"], "a newer GPU than the packs: trt-ptx");
Check(GpuSupport.Summary([rtx5090, rtx3080]) == (true, "sm120, sm86", "RTX 5090 + RTX 3080"), "the summary format");
Same(GpuSupport.RecommendedPacks(packs, []), [], "no NVIDIA GPU: no packs");

// NVML without a compute capability: treated as before (supported, sm00 -> trt-ptx)
var unknown = new NvidiaGpu("Unknown", 0, 0, "u", ComputeKnown: false);
Same(GpuSupport.RecommendedPacks(packs, [unknown]), ["trt-runtime", "trt-ptx"], "unknown compute capability");

Check(GpuSupport.TooOldMessage([gtx1080]) ==
      "TensorRT needs an NVIDIA GeForce RTX 20 / GTX 16 series or newer GPU (compute capability 7.5+); " +
      "GTX 1080 (compute 6.1) is too old for it", "the message names the GPU and its compute capability");
Check(GpuSupport.TooOldMessage([gtx1080, v100]).EndsWith("GTX 1080 (compute 6.1) + Tesla V100 (compute 7.0) are too old for it"),
    "the message for two GPUs");

Console.WriteLine(failures == 0 ? "all passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;
