using System.Diagnostics;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Management;

// Focused regression: a real non-longPathAware mpv.net process builds and then
// reopens an analysis engine when even the model path exceeds MAX_PATH.
internal static class NativeAnalysisPathChecks
{
    internal static async Task RunAsync(string root, string output, string compiler, string source, List<JsonObject> evidence)
    {
        string analysis = Path.Combine(root,"animejanai","analysis");
        string model = Directory.GetFiles(analysis,"*.onnx").Single();
        if(model.Length <= 260 || Directory.GetFiles(analysis,"*.engine").Length != 0)
            throw new Exception("Use an isolated long installation with one verified model and no existing analysis engines.");
        string data = Path.Combine(output,"data"), addons = Path.Combine(data,"addons");
        var package = await DeveloperTools.BuildAsync(Path.Combine(AppContext.BaseDirectory,"character-mask"),compiler,Path.Combine(output,"character-mask.ajnaddon"));
        new AddonRegistry(addons).Install(package,package.Manifest.Permissions);
        ManagementClient? manager = null;
        async Task Until(Func<Task<bool>> ready, int seconds = 30)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            while(!await ready())await Task.Delay(100,timeout.Token);
        }
        Task<JsonNode?> Call(string method, JsonObject? request = null)
        { request ??= new(); request["id"]=package.Manifest.Id;return manager!.CallAsync(method,request); }
        Task<JsonNode?> Action(string name)=>Call("addons.action",new(){["action"]=name});
        string? cache = null; DateTime written = default; long cacheSize = 0;
        try
        {
            foreach(string pass in new[]{"create","reuse"})
            {
                await using var player = new NativeLifecycleChecks.TestPlayer(root,data,output,"path-"+pass,"mpvnet.exe",
                    ["--hwdec=nvdec","--vo=gpu-next","--gpu-api=vulkan","--gpu-context=winvk","--force-window=yes",
                     "--start=830","--audio=no","--sub=no"],source);
                await player.ConnectAsync();
                if(manager is null)await Until(async()=>{try{manager=await ManagementClient.ConnectAsync(addons);return true;}
                    catch(Exception e)when(e is IOException or TimeoutException){return false;}});
                await Call("addons.start");
                await Until(async()=> (await Action("list")) is JsonArray a && a.Count==1);
                var started=await Action("subscribe");
                if(started?["subscriptionId"] is null)throw new Exception("Subscription failed: "+started);
                var states=new HashSet<string>(); JsonObject? frame=null;
                var clock=Stopwatch.StartNew();
                await Until(async()=>{
                    var result=await Action("latest");
                    if(result?["error"] is not null)throw new Exception(result.ToJsonString());
                    string? state=result?["status"]?["state"]?.GetValue<string>();
                    if(state is not null)states.Add(state);
                    if(state is "failed" or "unsupportedFormat" or "modelMissing")throw new Exception(result!.ToJsonString());
                    frame=result?["frame"] as JsonObject;
                    return frame is not null;
                },180);
                string found=Directory.GetFiles(analysis,"*.engine").Single();
                if((found+".lock").Length<=260 || Directory.GetFiles(analysis,"*.partial").Length!=0 || Directory.GetFiles(analysis,"*.lock").Length!=0)
                    throw new Exception("Long cache creation did not finish cleanly.");
                if(pass=="create"){
                    cache=found;written=File.GetLastWriteTimeUtc(found);cacheSize=new FileInfo(found).Length;
                    if(cacheSize==0 || !states.Contains("buildingEngine"))throw new Exception("Expected a fresh engine build.");
                } else if(found!=cache || File.GetLastWriteTimeUtc(found)!=written || new FileInfo(found).Length!=cacheSize || states.Contains("buildingEngine"))
                    throw new Exception("The second process rebuilt or changed the cached engine.");
                evidence.Add(new(){["pass"]=pass,["player"]="mpvnet.exe",["modelPathLength"]=model.Length,["enginePathLength"]=found.Length,
                    ["lockPathLength"]=(found+".lock").Length,["secondsToPair"]=clock.Elapsed.TotalSeconds,["engineBytes"]=cacheSize,
                    ["states"]=new JsonArray(states.Select(s=>(JsonNode)JsonValue.Create(s)!).ToArray()),["frame"]=frame!.DeepClone()});
                await Action("unsubscribe");
                // Query the owned filter, not Process.Modules while Windows is
                // concurrently unloading the model's dependency DLLs.
                await Until(async()=>!(await player.CommandAsync("get_property","vf"))!.AsArray()
                    .Any(f=>f?["label"]?.GetValue<string>()=="ajn-player-analysis"));
                await Call("addons.stop"); await player.QuitAsync();
            }
        }
        finally
        {
            if(manager is not null){
                try{await Call("addons.stop");}catch(Exception e)when(e is IOException or TimeoutException or ManagementException){}
                await manager.DisposeAsync();
            }
        }
    }
}
