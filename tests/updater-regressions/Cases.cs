using System.Net;
using System.Text.Json;

try
{
    Environment.SetEnvironmentVariable("ANIMEJANAI_PACKS_DIR", null);
    string root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    int passed = 0;
    void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    void Pass(string name) { passed++; Console.WriteLine("PASS " + name); }
    foreach (string mode in new[] { "normal", "replace-fails", "retire-fails", "crlf", "empty-legacy", "no-legacy" })
    {
        string dir = Path.Combine(root, mode), pc = Path.Combine(dir, "portable_config");
        Directory.CreateDirectory(Path.Combine(pc, "scripts"));
        string target = Path.Combine(pc, "input.conf"), legacy = Path.Combine(pc, "input-user.conf");
        string loader = Path.Combine(pc, "scripts", "animejanai_userinput.lua");
        string nl = mode == "crlf" ? "\r\n" : "\n";
        string original = string.Join(nl, new[] { "# header", "#@ANIMEJANAI-MANAGED-BEGIN",
            "SPACE cycle pause", "#@ANIMEJANAI-MANAGED-END", "q quit", "" });
        File.WriteAllText(target, original);
        if (mode != "no-legacy")
            File.WriteAllText(legacy, mode == "empty-legacy" ? "# no bindings\n" : "F11 cycle fullscreen\n");
        File.WriteAllText(loader, "-- legacy fixture");
        File.WriteAllText(Path.Combine(pc, "input-animejanai.conf"),
            "SPACE cycle pause\n" + (mode == "normal" ? "F10 cycle mute\n" : ""));
        var app = new Production(dir, new FixtureHttp());
        string? locked = mode == "replace-fails" ? target : mode == "retire-fails" ? legacy : null;
        if (locked != null)
        {
            using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);
            app.Sync();
        }
        else app.Sync();
        if (mode == "replace-fails")
        {
            Check(File.ReadAllText(target) == original, "Failed replacement changed original");
            Check(File.Exists(legacy) && File.Exists(loader), "Failed replacement retired legacy files");
            Check(!Directory.EnumerateFiles(pc, "*.tmp").Any(), "Failed replacement left temporary output");
        }
        if (mode == "retire-fails") Check(File.Exists(legacy), "Fixture failed to hold legacy file");
        app.Sync();
        string saved = File.ReadAllText(target);
        int count = saved.Split("F11 cycle fullscreen").Length - 1;
        Check(count == (mode is "empty-legacy" or "no-legacy" ? 0 : 1), "Lost or duplicated migrated binding");
        Check(saved.Contains("q quit") && saved.Contains("SPACE cycle pause"), "Lost existing bindings");
        if (mode == "normal") Check(saved.Contains("F10 cycle mute"), "Did not refresh managed bindings");
        if (mode == "no-legacy") Check(saved == original, "Changed already current input.conf");
        Check(!File.Exists(legacy) && !File.Exists(loader), "Successful retry did not retire legacy files");
        if (mode == "crlf") Check(!saved.Replace("\r\n", "").Contains('\n'), "Changed line endings");
        app.Sync(); Check(File.ReadAllText(target) == saved, "Migration is not idempotent");
        Pass("migration-" + mode);
    }

    foreach (string version in new[] { "3.5.0", "3.6.0" })
    {
        var http = new FixtureHttp();
        string dir = Path.Combine(root, version); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "version.txt"), version + "\n");
        var index = await new Production(dir, http).Packs();
        Check(index.PackageVersion == version, "Selected another release's packs");
        Check(http.Urls[0].EndsWith("/releases/tags/" + version), "Did not request exact installed tag");
        Check(!http.Urls.Any(u => u.EndsWith("/latest")), "Component lookup requested latest");
        Pass("installed-tag-" + version);
    }
    {
        var http = new FixtureHttp();
        var release = await new Production(root, http).Latest();
        Check(release.Tag == "3.5.0" && http.Urls.Single().EndsWith("/latest"), "Changed application-update selection");
        Pass("application-latest-control");
    }
    {
        var http = new FixtureHttp();
        bool failed = false;
        try { await new Production(root, http).Packs(); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed && http.Urls.Count == 0, "Missing installed version selected network packs");
        Pass("missing-version");
    }
    {
        var http = new FixtureHttp();
        string dir = Path.Combine(root, "unpublished"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "version.txt"), "3.7.0");
        bool failed = false;
        try { await new Production(dir, http).Packs(); }
        catch (HttpRequestException) { failed = true; }
        Check(failed && http.Urls.Count == 1, "Missing tag silently fell back to another release");
        Pass("missing-release");
    }
    {
        var http = new FixtureHttp();
        var index = await new Production(Path.Combine(root, "3.6.0"), http, false).Packs();
        Check(index.PackageVersion == "3.6.0" && http.Urls[1].EndsWith("packs-linux-x64.json"), "Wrong platform index");
        Pass("linux-index-control");
    }
    {
        var http = new FixtureHttp(); string packs = Path.Combine(root, "local-packs"); Directory.CreateDirectory(packs);
        File.WriteAllText(Path.Combine(packs, "packs.json"), FixtureHttp.Index("3.6.0"));
        Environment.SetEnvironmentVariable("ANIMEJANAI_PACKS_DIR", packs);
        var index = await new Production(root, http).Packs();
        Environment.SetEnvironmentVariable("ANIMEJANAI_PACKS_DIR", null);
        Check(index.PackageVersion == "3.6.0" && http.Urls.Count == 0, "Local pack override used network");
        Pass("local-pack-control");
    }
    {
        var http = new FixtureHttp();
        string dir = Path.Combine(root, "pinned-components"); Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "version.txt"), "3.6.1-test.1");
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            "{\"package_version\":\"3.6.1-test.1\",\"component_package_version\":\"3.6.0\"}");
        var app = new Production(dir, http);
        var index = await app.Packs();
        Check(index.PackageVersion == "3.6.0" && http.Urls[0].EndsWith("/tags/3.6.0"),
            "Test package did not select its explicit component release");
        Check(app.Mismatch(index) == null, "Rejected explicitly selected matching packs");
        Check(app.Mismatch(index with { PackageVersion = "3.5.0" }) != null,
            "Explicit pin allowed a different component release");
        Pass("explicit-component-release");
    }
    {
        var app = new Production(Path.Combine(root, "3.6.0"), new FixtureHttp());
        var index = await app.Packs();
        Check(app.Mismatch(index) == null, "Rejected normal matching release");
        Check(app.Mismatch(index with { PackageVersion = "3.5.0" }) != null,
            "Normal package accepted a different release");
        Pass("normal-version-validation");
    }
    Console.WriteLine($"Completed {passed} updater regression cases.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

class FixtureHttp : HttpMessageHandler
{
    public List<string> Urls = new();
    public static string Index(string version) => JsonSerializer.Serialize(new { package_version = version,
        packs = new[] { new { name = "rife", asset = "component-rife.7z", bytes = 1, files = new[] { "fixture" } } } });
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var url = request.RequestUri!; Urls.Add(url.ToString());
        string path = url.AbsolutePath, version = path.Contains("3.6.0") ? "3.6.0" : "3.5.0";
        if (path.EndsWith("3.7.0")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        string body = path.EndsWith(".json") ? Index(version) : JsonSerializer.Serialize(new {
            tag_name = version, assets = new[] {
                new { name = "packs.json", browser_download_url = $"https://fixture.invalid/{version}/packs.json" },
                new { name = "packs-linux-x64.json", browser_download_url = $"https://fixture.invalid/{version}/packs-linux-x64.json" }
            } });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
