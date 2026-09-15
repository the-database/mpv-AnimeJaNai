using AnimeJaNai.Addons;
using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static void FixtureStreamMetadata(string directory, string media, double origin = 0)
    {
        var value = new System.Text.Json.Nodes.JsonObject { ["probeAbi"] = 1, ["resourceFile"] = media,
            ["tracks"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject {
                ["type"] = "video", ["codec"] = "h264", ["width"] = 640, ["height"] = 360, ["startSeconds"] = origin }) };
        File.WriteAllText(Path.Combine(directory, EncodedMediaMetadata.FileName(media)), value.ToJsonString());
    }
    private static async Task StreamCacheChecks()
    {
        await Test("Stream metadata gates publication, reports encoded timestamps and expires with its object", async () =>
        {
            await using var cache = StreamCache.Create(Area(), 12);
            const string media = "part_00000000.ts";
            File.WriteAllBytes(Path.Combine(cache.Directory, media), [1]);
            var index = new NativeSegmentIndex([new(0, media, 0, 1)], null, false);
            await cache.PublishAsync(index, default);
            True(cache.List(null, 1)["segments"]!.AsArray().Count == 0);
            await Error("stream_incomplete", () => cache.Complete(index));
            FixtureStreamMetadata(cache.Directory, media, 1.4);
            await cache.PublishAsync(index, default);
            True(cache.List(null, 1)["segments"]![0]!["encodedTimestampOriginSeconds"]!.GetValue<double>() == 1.4);
            True(cache.EncodedMetadata()!["tracks"]![0]!["codec"]!.GetValue<string>() == "h264");
            await Error("stream_incomplete", () => cache.Complete(new([new(1, "part_00000001.ts", 1, 2)], null, true)));
            cache.Complete(index); True(cache.ProducerCompleted);
            cache.Expire(45);
            True(!File.Exists(Path.Combine(cache.Directory, EncodedMediaMetadata.FileName(media))));
        });
        await Test("Joined media input preserves fragment boundaries and supports backward seeks", () =>
        {
            string area = Area(), first = Path.Combine(area, "init.mp4"), second = Path.Combine(area, "part_00000000.m4s");
            File.WriteAllBytes(first, [1, 2]); File.WriteAllBytes(second, [3, 4, 5]);
            using var stream = new JoinedMediaStream(first, second);
            byte[] bytes = new byte[5]; stream.ReadExactly(bytes); True(bytes.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }));
            stream.Seek(-2, SeekOrigin.End); True(stream.ReadByte() == 4);
            stream.Position = 1; stream.ReadExactly(bytes.AsSpan(0, 3)); True(bytes[..3].SequenceEqual(new byte[] { 2, 3, 4 }));
            return Task.CompletedTask;
        });
        await Test("Stream cache retains leased segments, isolates generations and skips expired index entries", async () =>
        {
            await using var cache = StreamCache.Create(Area(), 100);
            string firstPath = Path.Combine(cache.Directory, "part_00000000.ts");
            File.WriteAllBytes(firstPath, [1, 2, 3]);
            FixtureStreamMetadata(cache.Directory, "part_00000000.ts");
            var first = new NativeSegment(0, "part_00000000.ts", 0, 1);
            await cache.PublishAsync(new([first], null, false), default);
            var page = cache.List(null, 8); var descriptor = page["segments"]![0]!;
            string id = descriptor["resourceId"]!.GetValue<string>();
            True(descriptor["sourceStartSeconds"]!.GetValue<double>() == 100 && descriptor["sourceEndSeconds"]!.GetValue<double>() == 101);
            await Error("stale_generation", () => cache.Acquire("other", id));
            using (var lease = cache.Acquire(cache.Generation, id))
            {
                cache.Expire(132); True(File.Exists(firstPath));
                True((await ReadAll(lease.File)).SequenceEqual(new byte[] { 1, 2, 3 }));
            }
            cache.Expire(132); True(!File.Exists(firstPath));
            await Error("segment_expired", () => cache.Acquire(cache.Generation, id));
            File.WriteAllBytes(Path.Combine(cache.Directory, "part_00000001.ts"), [4, 5, 6]);
            FixtureStreamMetadata(cache.Directory, "part_00000001.ts");
            await cache.PublishAsync(new([first, new(1, "part_00000001.ts", 1, 2)], null, true), default);
            True(cache.List(null, 8)["segments"]!.AsArray().Count == 1);
            await Error("stale_generation", () => cache.List("other:1", 8));
        });
        await Test("Stream cache cleanup cancels readers and waits before releasing its storage reservation", async () =>
        {
            var cache = StreamCache.Create(Area(), 0);
            string path = cache.Directory;
            File.WriteAllBytes(Path.Combine(path, "part_00000000.ts"), [1]);
            FixtureStreamMetadata(path, "part_00000000.ts");
            await cache.PublishAsync(new([new(0, "part_00000000.ts", 0, 1)], null, false), default);
            string id = cache.List(null, 1)["segments"]![0]!["resourceId"]!.GetValue<string>();
            var lease = cache.Acquire(cache.Generation, id);
            var close = cache.DisposeAsync().AsTask();
            await Until(() => lease.Stop.IsCancellationRequested);
            True(!close.IsCompleted); lease.Dispose();
            await close.WaitAsync(TimeSpan.FromSeconds(5)); True(!Directory.Exists(path));
            await cache.DisposeAsync();
        });
        await Test("Stream cache reservations bound aggregate temporary storage and survive slot reuse", async () =>
        {
            var caches = new List<StreamCache>();
            try
            {
                for (int i = 0; i < 4; i++) caches.Add(StreamCache.Create(Area(), 0));
                await Error("capacity_exceeded", () => StreamCache.Create(Area(), 0));
                await caches[0].DisposeAsync();
                await using var replacement = StreamCache.Create(Area(), 0);
            }
            finally { foreach (var cache in caches) await cache.DisposeAsync(); }
        });
    }
}
