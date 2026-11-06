using AnimeJaNai.Addons.Native;

internal static partial class Checks
{
    private static async Task SegmentIndexChecks()
    {
        await Test("Native segment index preserves timeline through rolling lists and rejects changed objects", async () =>
        {
            var index = new SegmentIndex("fragmentedMp4");
            string Header(int sequence) => "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:" + sequence + "\n#EXT-X-MAP:URI=\"init.mp4\"\n";
            var first = index.Parse(Header(0) + "#EXTINF:1.001,\npart_00000000.m4s\n#EXTINF:0.999,\npart_00000001.m4s\n");
            True(first.Initialization == "init.mp4" && first.Segments.Length == 2 && first.Segments[1].End == 2);
            var next = index.Parse(Header(1) + "#EXTINF:0.999,\npart_00000001.m4s\n#EXTINF:0.5,\npart_00000002.m4s\n#EXT-X-ENDLIST\n");
            True(next.Complete && next.Segments[0].Start == 1.001 && next.Segments[1].End == 2.5);
            await Error("invalid_segment_index", () => index.Parse(Header(1) + "#EXTINF:2.0,\npart_00000001.m4s\n"));
            await Error("segment_history_lost", () => new SegmentIndex("mpegts").Parse("#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:8\n#EXTINF:1,\npart_00000008.ts\n"));
        });
        await Test("Native segment index restricts resource names and validates Matroska timing", async () =>
        {
            var mkv = new SegmentIndex("matroska");
            var parsed = mkv.Parse("part_00000000.mkv,0.000000,1.001000\npart_00000001.mkv,1.001000,1.750000\n");
            True(parsed.Segments.Length == 2 && parsed.Segments[1].End == 1.75);
            foreach (string csv in new[] { "../part_00000000.mkv,0,1", "part_00000000.mkv,NaN,1", "part_00000000.mkv,2,1" })
                await Error("invalid_segment_index", () => mkv.Parse(csv));
            await Error("invalid_segment_index", () => new SegmentIndex("fragmentedMp4").Parse("#EXT-X-MAP:URI=\"../private.mp4\"\n"));
        });
    }
}
