using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;

internal static class NativeAnalysisChecks
{
    internal static async Task RunAsync(string root, string output, string compiler, string source, List<JsonObject> evidence, bool pipelineOnly = false, bool sourceColorOnly = false)
    {
        string data = Path.Combine(output, "data"), addons = Path.Combine(data, "addons");
        string example = Path.Combine(AppContext.BaseDirectory, "character-mask");
        if(sourceColorOnly)
        {
            string manifestText = File.ReadAllText(Path.Combine(example,"manifest.json"));
            example = Directory.CreateDirectory(Path.Combine(output,"source-color")).FullName;
            var manifest=JsonNode.Parse(manifestText)!;
            manifest["permissions"]=new JsonArray("player.observe","frames.read");
            manifest["requiredCapabilities"]=new JsonObject{["playerFrames"]=new JsonObject{["major"]=1,["minMinor"]=1}};
            File.WriteAllText(Path.Combine(example,"manifest.json"),manifest.ToJsonString());
            File.WriteAllText(Path.Combine(example,"addon.js"), """
                let id;
                function onEvent(e,a) {
                  if(e.name === 'stop') { if(id)a.playerFrames.unsubscribe(id); return; }
                  if(e.name !== 'action')return;
                  if(e.data.id === 'list')return a.playerFrames.list();
                  if(e.data.id === 'subscribe'){const s=a.playerFrames.subscribe(a.playerFrames.list()[0].playerId,{stage:'source',width:64,height:36,maxFps:12});id=s.subscriptionId;return s;}
                  if(e.data.id === 'latest'){const f=a.playerFrames.read(id);if(f){f.byteLength=f.pixels.length;delete f.pixels;}return {frame:f};}
                  if(e.data.id === 'unsubscribe'){a.playerFrames.unsubscribe(id);id=null;return true;}
                }
                """);
        }
        var original = await DeveloperTools.BuildAsync(example, compiler, Path.Combine(output, "character-mask.ajnaddon"));
        string module = Path.Combine(output, "inspector.wasm"); original.WriteModule(module);
        var packages = Enumerable.Range(1,2).Select(i => AddonPackage.Create(original.Manifest with { Id = "org.animejanai.analysischeck" + i }, File.ReadAllBytes(module))).ToArray();
        var registry = new AddonRegistry(addons);
        foreach (var package in packages) registry.Install(package, package.Manifest.Permissions);
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        async Task Until(Func<Task<bool>> ready, int seconds = 60)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            while (!await ready()) await Task.Delay(100, timeout.Token);
        }
        static bool Loaded(int id)
        {
            using var process = Process.GetProcessById(id);
            return process.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("aji_analysis.dll",StringComparison.OrdinalIgnoreCase));
        }
        static async Task<JsonArray> Filters(NativeLifecycleChecks.TestPlayer p) => (await p.CommandAsync("get_property","vf"))!.AsArray();
        string[] settings = ["--hwdec=nvdec", "--vo=gpu-next", "--gpu-api=vulkan", "--gpu-context=winvk", "--force-window=yes",
            "--window-minimized=no", "--autofit=720x405", "--start=830", "--audio=no", "--sub=no"];
        ManagementClient? manager = null;
        Task<JsonNode?> Call(int i, string method, JsonObject? request = null)
        { request ??= new(); request["id"] = packages[i].Manifest.Id; return manager!.CallAsync(method, request); }
        Task<JsonNode?> Action(int i, string name) => Call(i,"addons.action",new() { ["action"] = name });
        async Task<JsonObject> Sample(int i, string action = "latest", Func<JsonObject,bool>? accept = null)
        {
            JsonObject? frame = null;
            await Until(async () => {
                var result = (await Action(i,action))!.AsObject();
                Require(result["error"] is null, result.ToJsonString());
                Require(result["status"]?["state"]?.GetValue<string>() is not ("failed" or "unsupportedFormat" or "modelMissing"), result.ToJsonString());
                frame = result["frame"] as JsonObject;
                return frame is not null && (accept?.Invoke(frame) ?? true);
            }, 180);
            return (JsonObject)frame!.DeepClone();
        }
        async Task Capture(string name, JsonObject frame)
        {
            using var bytes = new MemoryStream();
            while (true) {
                var chunk = (await Action(0,"nextChunk"))!;
                Require(chunk["frameId"]!.GetValue<string>() == frame["frameId"]!.GetValue<string>() && chunk["offset"]!.GetValue<int>() == bytes.Length, "Pair export changed frame or offset.");
                bytes.Write(Convert.FromHexString(chunk["hex"]!.GetValue<string>()));
                if(chunk["done"]!.GetValue<bool>())break;
            }
            int colorBytes = frame["width"]!.GetValue<int>() * frame["height"]!.GetValue<int>() * 4;
            Require(bytes.Length == colorBytes * 3 / 2, "Color and mask dimensions differ.");
            File.WriteAllBytes(Path.Combine(output,name+".bgra"),bytes.ToArray()[..colorBytes]);
            File.WriteAllBytes(Path.Combine(output,name+".mask-u16"),bytes.ToArray()[colorBytes..]);
            File.WriteAllText(Path.Combine(output,name+".json"),frame.ToJsonString());
        }
        async Task<JsonObject> Measure(NativeLifecycleChecks.TestPlayer player, bool observe)
        {
            using var process = Process.GetProcessById(player.Id); process.Refresh();
            var cpu = process.TotalProcessorTime; var timer = Stopwatch.StartNew();
            double before = await player.PositionAsync();
            int dropsBefore = (await player.CommandAsync("get_property","frame-drop-count"))!.GetValue<int>();
            var samples = new JsonArray();
            while(timer.Elapsed.TotalSeconds < 5) {
                if(observe) { var result=await Action(0,"latest"); if(result?["frame"] is JsonObject f)samples.Add(f.DeepClone()); }
                await Task.Delay(100);
            }
            process.Refresh();
            return new() { ["elapsedSeconds"] = timer.Elapsed.TotalSeconds, ["playbackSeconds"] = await player.PositionAsync()-before,
                ["cpuPercentOneCore"] = (process.TotalProcessorTime-cpu).TotalSeconds/timer.Elapsed.TotalSeconds*100,
                ["workingSetBytes"] = process.WorkingSet64, ["addedDrops"] = (await player.CommandAsync("get_property","frame-drop-count"))!.GetValue<int>()-dropsBefore,
                ["samples"] = samples };
        }
        try
        {
            await using(var player = new NativeLifecycleChecks.TestPlayer(root,data,output,"analysis","mpv.exe",settings,source))
            {
                await player.ConnectAsync();
                await Until(async () => { try {manager=await ManagementClient.ConnectAsync(addons);return true;} catch(Exception e) when(e is IOException or TimeoutException){return false;} });
                await Call(0,"addons.start");
                await Until(async () => (await Action(0,"list")) is JsonArray a && a.Count == 1);
                if(sourceColorOnly)
                {
                    Require(!Loaded(player.Id),"Color-only observer started before its request.");
                    await Action(0,"subscribe"); var frame=await Sample(0);
                    Require(frame["stage"]!.GetValue<string>()=="source" && frame["analysis"] is null && frame["byteLength"]!.GetValue<int>()==64*36*4,"Expected source colors without masks or analysis permission.");
                    evidence.Add(new(){["sourceColorWithoutAnalysisGrant"]=frame});
                    await Action(0,"unsubscribe"); await Until(()=>Task.FromResult(!Loaded(player.Id)));
                    await Call(0,"addons.stop"); await player.QuitAsync(); return;
                }
                if (pipelineOnly)
                {
                    string pattern = await NativeFrameChecks.CreatePatternAsync(root,output);
                    string config = Path.Combine(output,"analysis-pipeline.conf");
                    File.WriteAllText(config,"[global]\nconfig_version=3\nbackend=TensorRT\nlogging=yes\ndefault_slot=1\n" +
                        "[slot_1]\nchain_1_max_fps=61\nchain_1_model_1_name=2x_AnimeJaNai_HD_V3.1_Performance_SPANF3_b5f48_unshuffle_fp16\n" +
                        "[slot_2]\nchain_1_max_fps=61\nchain_1_model_1_name=2x_AnimeJaNai_HD_V3.1_Performance_SPANF3_b5f48_unshuffle_fp16\n" +
                        "chain_1_rife=yes\nchain_1_rife_model=414\nchain_1_rife_factor_numerator=2\nchain_1_rife_factor_denominator=1\n");
                    string Q(string value)=>"%"+Encoding.UTF8.GetByteCount(value)+"%"+value;
                    string filter="@aji:animejanai:lib="+Q(Path.Combine(root,"animejanai/inference/aji.dll"))+":conf="+Q(config)+
                        ":model-dir="+Q(Path.Combine(root,"animejanai/onnx"))+":rife-model-dir="+Q(Path.Combine(root,"animejanai/rife"))+
                        ":trtexec="+Q(Path.Combine(root,"animejanai/inference/trtexec.exe"))+":slot=";
                    await Action(0,"subscribe"); var originalFrame=await Sample(0);
                    await player.CommandAsync("set_property","start","none");
                    await player.CommandAsync("set_property","loop-file","inf");
                    await player.CommandAsync("loadfile",pattern,"replace");
                    var fileFrame=await Sample(0,accept:f=>f["sourceWidth"]!.GetValue<int>()==480 && f["epoch"]!.GetValue<string>()!=originalFrame["epoch"]!.GetValue<string>());
                    foreach(int slot in new[]{1,2})
                    {
                        await player.CommandAsync("vf","add",filter+slot);
                        var frame=await Sample(0,accept:f=>f["epoch"]!.GetValue<string>()!=fileFrame["epoch"]!.GetValue<string>());
                        var chain=await Filters(player);
                        Require(chain[0]?["label"]?.GetValue<string>()=="ajn-player-analysis","Observation moved after the processing filter.");
                        Require(frame["sourceWidth"]!.GetValue<int>()==480 && frame["sourceHeight"]!.GetValue<int>()==360,"Processing changed the observation source geometry.");
                        JsonNode? outputGeometry=null;
                        await Until(async()=>{outputGeometry=await player.CommandAsync("get_property","video-out-params");return outputGeometry?["w"]?.GetValue<int>()==960;},300);
                        evidence.Add(new(){["slot"]=slot,["rifeEnabled"]=slot==2,["sourcePair"]=frame,["outputGeometry"]=outputGeometry?.DeepClone(),["filterOrder"]=chain.DeepClone()});
                        fileFrame=frame;
                    }
                    await Action(0,"unsubscribe"); await Until(()=>Task.FromResult(!Loaded(player.Id)));
                    await Call(0,"addons.stop"); await player.QuitAsync();
                    return;
                }
                Require(!Loaded(player.Id) && (await Filters(player)).Count == 0, "Inactive addon created native analysis work.");
                evidence.Add(new() { ["inactive"] = await Measure(player,false), ["optionalModuleAbsent"] = true });
                Require((await Action(0,"subscribe"))?["subscriptionId"] is not null,"Subscribe failed.");
                var first=await Sample(0,"capture"); await Capture("frieren-first",first);
                Require(first["stage"]!.GetValue<string>()=="source" && first["sourceWidth"]!.GetValue<int>()==1920 &&
                    first["maskSummary"]!["max"]!.GetValue<double>()>.5 && first["maskSummary"]!["min"]!.GetValue<double>()<.1,"Expected a real source picture and nontrivial character mask.");
                Require(Loaded(player.Id),"Optional GPU runtime did not load.");
                evidence.Add(new() { ["upscaleOffRifeOff"] = first, ["active"] = await Measure(player,true) });
                await player.CommandAsync("seek","865","absolute+exact");
                var sought=await Sample(0,"capture",f=>f["epoch"]!.GetValue<string>()!=first["epoch"]!.GetValue<string>() && f["ptsSeconds"]!.GetValue<double>()>=865);
                await Capture("frieren-sought",sought); evidence.Add(new(){["seek"] = sought});
                await Call(1,"addons.configure",new(){["changes"]=new JsonObject{["width"]=80L,["height"]=45L,["fps"]=8L}});
                await Call(1,"addons.start"); await Action(1,"subscribe");
                var smaller=await Sample(1);
                Require(smaller["width"]!.GetValue<int>()==80 && (await Filters(player)).Count(f=>f?["label"]?.GetValue<string>()=="ajn-player-analysis")==1,"Observers did not share one producer.");
                await Action(0,"unsubscribe"); var survivor=await Sample(1);
                evidence.Add(new(){["sharedSmallerPair"]=smaller,["survivor"]=survivor});
                await Action(1,"unsubscribe");
                await Until(async()=>!Loaded(player.Id) && (await Filters(player)).Count==0);
                evidence.Add(new(){["lastObserverUnloadedGpuModule"]=true,["afterStop"]=await Measure(player,false)});
                await Call(1,"addons.stop"); await Call(0,"addons.stop"); await player.QuitAsync();
            }
            await using(var player = new NativeLifecycleChecks.TestPlayer(root,data,output,"analysis-net","mpvnet.exe",settings,source))
            {
                await player.ConnectAsync(); await Call(0,"addons.start");
                await Until(async () => (await Action(0,"list")) is JsonArray a && a.Count == 1);
                await Action(0,"subscribe"); var frame=await Sample(0); evidence.Add(new(){["mpvnetRealSourcePair"]=frame});
                await Action(0,"unsubscribe"); await Until(()=>Task.FromResult(!Loaded(player.Id)));
                await player.CommandAsync("set_property","hwdec","no"); await player.CommandAsync("seek","865","absolute+exact");
                await Action(0,"subscribe");
                JsonNode? unsupported=null;
                await Until(async()=>{unsupported=await Action(0,"latest");return unsupported?["status"]?["state"]?.GetValue<string>()=="unsupportedFormat" || unsupported?["error"]?.GetValue<string>()=="frame_format_unavailable";});
                Require(!Loaded(player.Id),"Unsupported software decode loaded GPU analysis.");
                evidence.Add(new(){["softwareDecodeExplicitlyUnsupported"]=unsupported?.DeepClone()});
                await Action(0,"unsubscribe"); await Call(0,"addons.stop"); await player.QuitAsync();
            }
        }
        finally {
            if(manager is not null) {
                foreach(var p in packages)try{await manager.CallAsync("addons.stop",new(){["id"]=p.Manifest.Id});}catch(Exception e)when(e is IOException or TimeoutException or ManagementException){}
                await manager.DisposeAsync();
            }
        }
    }
}
