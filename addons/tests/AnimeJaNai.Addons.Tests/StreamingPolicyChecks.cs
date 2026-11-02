using System.Diagnostics;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task StreamingPolicyChecks()
    {
        await Test("Streaming policy preserves the final measured speed without treating idle retention as encoding", () =>
        {
            var metrics = new StreamMetrics(); long start = Stopwatch.GetTimestamp();
            JsonObject State(double end, bool paused = false) => new() { ["buffer"] = new JsonObject { ["producedEndSeconds"] = end, ["bufferPaused"] = paused } };
            metrics.Update(State(10), new(), 10, 10, true, false, timestamp: start);
            var active = metrics.Update(State(14), new(), 10, 14, true, false, timestamp: start + 2 * Stopwatch.Frequency);
            True(active["processingMediaSecondsPerWallSecond"]!.GetValue<double>() == 2);
            var done = metrics.Update(State(16, true), new(), 10, 16, true, false, true, start + 3 * Stopwatch.Frequency);
            True(done["processingMediaSecondsPerWallSecond"]!.GetValue<double>() == 2);
            var retained = metrics.Update(State(16), new(), 10, 16, true, false, true, start + 100 * Stopwatch.Frequency);
            Same(done, retained);
        });
        await Test("Streaming policy separates inference from vendor encoding and rejects unsupported formats", async () =>
        {
            var options = new NativeEncoding("h264", "mpegts", 1000);
            var amd = NativeEncoderPlan.Select(options, new HashSet<int> { 0x1002 });
            True(amd.Codec == "h264_amf" && amd.PixelFormat == "nv12" && !amd.Options.Contains("rc-lookahead"));
            var nvidia = NativeEncoderPlan.Select(options with { Encoder = "nvenc" }, new HashSet<int> { 0x10de });
            True(nvidia.Codec == "h264_nvenc" && nvidia.PixelFormat == "nv12");
            await Error("encoder_unavailable", () => NativeEncoderPlan.Select(options with { Encoder = "nvenc" }, new HashSet<int> { 0x1002 }));
            await Error("unsupported_format", () => (options with { BitDepth = 10 }).Validate());
            await Error("unsupported_format", () => NativeEncoderPlan.Select(options with { VideoCodec = "av1", Container = "matroska" }, new HashSet<int> { 0x1002 }));
            Same((options with { Encoder = "amf" }).ToJson(), NativeEncoding.Parse((options with { Encoder = "amf" }).ToJson()).ToJson());
        });
        await Test("Streaming policy selects bounded profile and resolution budgets and releases aggregate reservations", async () =>
        {
            var profile = new ApprovedProfile("p", "Balanced", 1002, "DirectML", "");
            var hd = NativeResourcePolicy.Select(profile, 1920, 1080);
            True(hd.ProcessBytes == 4 * NativeResourcePolicy.GiB);
            // Slot numbering comes from inference/src/aji_conf.cpp:
            // 1001 Quality, 1002 Balanced, 1003 Performance.
            True(NativeResourcePolicy.Select(profile with { Slot = 1001 }, 1920, 1080).ProcessBytes == 8 * NativeResourcePolicy.GiB);
            var performance = NativeResourcePolicy.Select(profile with { Slot = 1003, Backend = "TensorRT" }, 1920, 1080, true);
            True(performance.ProcessBytes == 6 * NativeResourcePolicy.GiB && performance.JobBytes == 6 * NativeResourcePolicy.GiB);
            True(NativeResourcePolicy.Select(profile with { Slot = 1 }, 1920, 1080).ProcessBytes > hd.ProcessBytes);
            var custom = profile with { Slot = 1, Configuration = "[slot_1]\nchain_1_model_1_name=2x_AnimeJaNai_HD_V3.1_Balanced_example\n" };
            True(NativeResourcePolicy.Select(custom, 1920, 1080).ProcessBytes == hd.ProcessBytes);
            True(NativeResourcePolicy.Select(custom with { Configuration = custom.Configuration + "chain_1_rife=yes\n" }, 1920, 1080).ProcessBytes > hd.ProcessBytes);
            True(NativeResourcePolicy.Select(profile, 3840, 2160).ProcessBytes > hd.ProcessBytes);
            await Error("unsupported_media", () => NativeResourcePolicy.Select(profile, 7680, 4320));
            var held = new List<IDisposable>(); bool rejected = false;
            try { for (int i = 0; i < 20; i++) held.Add(NativeResourcePolicy.Reserve(new(NativeResourcePolicy.GiB, 2 * NativeResourcePolicy.GiB))); }
            catch (AddonException e) when (e.Code == "resource_exhausted") { rejected = true; }
            finally { foreach (var lease in held) { lease.Dispose(); lease.Dispose(); } }
            True(rejected); using var again = NativeResourcePolicy.Reserve(new(NativeResourcePolicy.GiB, 2 * NativeResourcePolicy.GiB));
            using var builder = NativeResourcePolicy.Reserve(new(NativeResourcePolicy.GiB, NativeResourcePolicy.GiB), "TensorRT", true);
            await Error("preparation_busy", () => NativeResourcePolicy.Reserve(new(NativeResourcePolicy.GiB, NativeResourcePolicy.GiB), "TensorRT"));
        });
        await Test("Streaming policy exports bounded structured processing evidence without arbitrary log text", () =>
        {
            var state = NativeProcessingEvidence.Parse("AJN_STREAM_V1 active DirectML 1002 1920 1080 3840 2160 0 24.000000\n");
            True(state?["actualBackend"]?.GetValue<string>() == "DirectML" && state["outputWidth"]!.GetValue<int>() == 3840);
            True(NativeProcessingEvidence.Parse("AJN_STREAM_V1 active https://private 1002 1920 1080 3840 2160 0 24") is null);
            string area = Area(); File.WriteAllText(Path.Combine(area, "inference.log"), "private source /secret\n1. Applied Model: safe_model;    New Video Resolution: 3840x2160\n2. Applied Model: C:/private/model;\n");
            var models = NativeProcessingEvidence.Models(area); True(models.Count == 1 && models[0]!.GetValue<string>() == "safe_model");
        });
    }
}
