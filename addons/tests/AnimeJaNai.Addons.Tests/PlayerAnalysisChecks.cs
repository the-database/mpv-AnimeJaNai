using System.Buffers.Binary;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

internal static partial class Checks
{
    private static async Task PlayerAnalysisChecks()
    {
        await Test("A player observation reports waiting before its first native producer attaches", () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            using var buffer = new AnimeJaNai.Addons.Native.NativeFrameBuffer(player:true, stage:"source", analysis:true);
            buffer.RenewPlayerLease();
            True(buffer.Status()["state"]!.GetValue<string>() == "waitingForFrame");
        });
        await Test("Character analysis requires explicit consent and uses paired binary framing", async () =>
        {
            using var frames = new PlayerFrameRegistry(supportsAnalysis: true);
            var source = new AnalysisSource(); using var player = frames.Register(source, out var playerId);
            var package = Package(permissions: ["player.observe", "frames.read", "player.analysis"]);
            var request = new JsonObject { ["playerId"] = playerId, ["model"] = FrameRequest.Model, ["stage"] = "source",
                ["format"] = "bgra8+unorm16le", ["width"] = 2L, ["height"] = 2L, ["maxFps"] = 12L };
            await using var denied = new Broker(package, new(package, ["player.observe", "frames.read"]), Area(), playerFrames: frames);
            await Error("permission_denied", () => denied.InvokeAsync("playerAnalysis.subscribe", request, default));
            await Error("permission_denied", () => denied.InvokeTransportAsync("playerAnalysis.read", new() { ["subscriptionId"] = "unknown" }, default));
            True(source.Active == 0);
            await using var granted = new Broker(package, new(package, package.Manifest.Permissions), Area(), playerFrames: frames);
            var id = (await granted.InvokeAsync("playerAnalysis.subscribe", request, default))!["subscriptionId"]!.GetValue<string>();
            True(source.Active == 1);
            var response = await granted.InvokeTransportAsync("playerAnalysis.read", new() { ["subscriptionId"] = id }, default);
            True(response.Binary.Length == 24 && response.Result!["frame"]!["analysis"]!["format"]!.GetValue<string>() == "unorm16le");
            True(BinaryPrimitives.ReadUInt16LittleEndian(response.Binary.Span[18..]) == 21845);
            using var wire = new MemoryStream();
            var channel = new MessageChannel(wire, wire);
            byte[] pair = new byte[FrameRequest.MaxBytes * 3 / 2]; pair[^1] = 47;
            await Error("message_too_large", () => channel.WriteBinaryAsync(new(), pair, default));
            await channel.WriteBinaryAsync(new() { ["pair"] = true }, pair, default, pairedAnalysis: true);
            wire.Position = 0; True((await channel.ReadAsync(default))["pair"]!.GetValue<bool>());
            True((await channel.ReadBinaryAsync(pair.Length, default, pairedAnalysis: true))[^1] == 47);
            await granted.DisposeAsync(); True(source.Active == 0);
        });
        await Test("Character observations share only their channel and keep masks matched through resize and epoch changes", () =>
        {
            var clock = new ManualClock(); using var frames = new PlayerFrameRegistry(clock, supportsAnalysis: true);
            var source = new AnalysisSource(); using var player = frames.Register(source, out var playerId);
            var one = frames.CreateOwner(); var two = frames.CreateOwner();
            string normal = frames.Subscribe(one, playerId, new(2,2,12))["subscriptionId"]!.GetValue<string>();
            string large = frames.Subscribe(one, playerId, new(2,2,12,"source",true))["subscriptionId"]!.GetValue<string>();
            string small = frames.Subscribe(two, playerId, new(1,1,12,"source",true))["subscriptionId"]!.GetValue<string>();
            True(source.Active == 2);
            var full = frames.Read(one,large)!; var reduced = frames.Read(two,small)!;
            True(full.Mask.Length == 8 && reduced.Mask.Length == 2);
            True(BinaryPrimitives.ReadUInt16LittleEndian(reduced.Mask.Span) == 32768 && reduced.Pixels.Span[0] == 30);
            True(full.Metadata["epoch"]!.GetValue<string>() == reduced.Metadata["epoch"]!.GetValue<string>());
            frames.ReleaseOwner(two); True(source.Active == 2 && frames.Read(one,normal)!.Mask.IsEmpty);
            source.Ready = false; clock.Now = 100; True(frames.Read(one,large) is null);
            source.Epoch++; source.Ready = true; clock.Now = 200;
            True(frames.Read(one,large)!.Metadata["epoch"]!.GetValue<string>() == "2");
            frames.ReleaseOwner(one); True(source.Active == 0);
        });
    }
    private sealed class AnalysisSource : IPlayerFrameSource
    {
        internal int Active, Epoch = 1;
        internal bool Ready = true;
        public IFrameSubscription Subscribe(FrameRequest request) { Active++; return new Subscription(this,request); }
        public void Dispose() {}
        private sealed class Subscription(AnalysisSource owner, FrameRequest request) : IFrameSubscription
        {
            private bool disposed;
            public FramePacket? ReadLatest()
            {
                if (!owner.Ready) return null;
                byte[] color = new byte[request.Width*request.Height*4], mask = request.CharacterMask ? new byte[request.Width*request.Height*2] : [];
                for(int i=0;i<color.Length/4;i++) {color[i*4]=(byte)(i*20);color[i*4+3]=255; if(mask.Length>0)BinaryPrimitives.WriteUInt16LittleEndian(mask.AsSpan(i*2),(ushort)(i*21845));}
                var metadata = new JsonObject { ["frameId"]="1", ["epoch"]=owner.Epoch.ToString(), ["width"]=(long)request.Width,["height"]=(long)request.Height,
                    ["format"]="bgra8",["stage"]=request.Stage };
                if(request.CharacterMask)metadata["analysis"]=new JsonObject{["model"]=FrameRequest.Model,["format"]="unorm16le",["width"]=request.Width,["height"]=request.Height};
                return new(metadata,color,mask);
            }
            public void Dispose(){if(!disposed){disposed=true;owner.Active--;}}
        }
    }
}
