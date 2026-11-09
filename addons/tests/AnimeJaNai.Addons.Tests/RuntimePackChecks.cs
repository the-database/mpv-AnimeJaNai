using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task RuntimePackChecks()
    {
        static string Inference(string root, params string[] files)
        {
            string inference = Directory.CreateDirectory(Path.Combine(root, "animejanai", "inference")).FullName;
            foreach (string file in files) File.WriteAllText(Path.Combine(inference, file), "fixture");
            return inference;
        }
        await Test("TensorRT sessions find the runtime beside libaji without the shared store", () =>
        {
            string inference = Inference(Area(), "aji_trt.dll", "nvinfer_11.dll", "trtexec.exe");
            True(NativeRuntimePacks.TrtDirectory(inference) == inference && NativeRuntimePacks.TensorRtInstalled(inference));
            File.Delete(Path.Combine(inference, "nvinfer_11.dll"));
            True(!NativeRuntimePacks.TensorRtInstalled(inference), "A missing in-tree runtime was reported as installed");
        });
        await Test("TensorRT sessions find the runtime in the shared store pack aji-runtime.json names", () =>
        {
            string root = Area();
            string inference = Inference(root, "aji_trt.dll");
            string pack = Directory.CreateDirectory(Path.Combine(root, "packs", "trt-runtime", "11.4.0.106-cuda13.4")).FullName;
            File.WriteAllText(Path.Combine(inference, "aji-runtime.json"),
                "{\"format\":1,\"trt\":" + System.Text.Json.JsonSerializer.Serialize(pack) + ",\"trt_builder\":[]}");
            True(NativeRuntimePacks.TrtDirectory(inference) == pack);
            True(!NativeRuntimePacks.TensorRtInstalled(inference), "An empty store pack was reported as installed");
            File.WriteAllText(Path.Combine(pack, "nvinfer_11.dll"), "fixture");
            File.WriteAllText(Path.Combine(pack, "trtexec.exe"), "fixture");
            True(NativeRuntimePacks.TensorRtInstalled(inference), "The store pack's runtime was not found");
            // a relative pack path resolves from the inference folder, as libaji reads it
            File.WriteAllText(Path.Combine(inference, "aji-runtime.json"), "{\"format\":1,\"trt\":\"../../packs/trt-runtime/11.4.0.106-cuda13.4\"}");
            True(NativeRuntimePacks.TrtDirectory(inference) == pack && NativeRuntimePacks.TensorRtInstalled(inference));
        });
        await Test("An unreadable aji-runtime.json falls back to the inference folder", () =>
        {
            string inference = Inference(Area(), "aji_trt.dll", "nvinfer_11.dll", "trtexec.exe");
            File.WriteAllText(Path.Combine(inference, "aji-runtime.json"), "{ not json");
            True(NativeRuntimePacks.TrtDirectory(inference) == inference && NativeRuntimePacks.TensorRtInstalled(inference));
        });
    }
}
