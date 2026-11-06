using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

internal static partial class Checks
{
    private static async Task MediaSelectionChecks()
    {
        await Test("Media approvals survive reload and remain scoped to the exact addon package", async () =>
        {
            string area = Area(), source = Path.Combine(area, "selected.mp4");
            File.WriteAllText(source, "fixture bytes");
            var package = Package(permissions: ["sessions.manage"]);
            var grant = new PermissionGrant(package, ["sessions.manage"]);
            var selections = new MediaSelections(area);
            string sourceId = selections.ApproveSource(package, grant, source);
            True(selections.ApproveSource(package, grant, source) == sourceId);
            string profileId = selections.ApproveProfile(package, grant, "Balanced", 1002, "DirectML", "[global]\nbackend=TensorRT\n");
            var loaded = new MediaSelections(area);
            var resolved = loaded.Resolve(package, grant, sourceId, profileId);
            True(resolved.Source.Path == source && resolved.Profile.Configuration.EndsWith("backend=DirectML\nlogging=no\n"));
            Same(selections.List(package, grant), loaded.List(package, grant));
            True(!loaded.List(package, grant).ToJsonString().Contains(area.Replace('\\', '/')));
            True(loaded.List(package, grant)["sources"]![0]!["path"] is null);
            var updated = Package(version: "2.0.0", permissions: ["sessions.manage"]);
            await Error("source_not_granted", () => loaded.Resolve(updated, new(updated, ["sessions.manage"]), sourceId, profileId));
            var other = Package("org.example.other", permissions: ["sessions.manage"]);
            await Error("source_not_granted", () => loaded.Resolve(other, new(other, ["sessions.manage"]), sourceId, profileId));
            await Error("permission_denied", () => loaded.List(package, new(package, [])));
            await Error("invalid_grant", () => loaded.List(updated, grant));
        });
        await Test("Media profile snapshots are explicit and revocation preserves unrelated selections", async () =>
        {
            string area = Area(), source = Path.Combine(area, "selected.mkv"); File.WriteAllText(source, "fixture");
            var package = Package(permissions: ["sessions.manage"]); var grant = new PermissionGrant(package, ["sessions.manage"]);
            var selections = new MediaSelections(area);
            string s = selections.ApproveSource(package, grant, source);
            string first = selections.ApproveProfile(package, grant, "One", 1001, "DirectML", "[global]\n");
            True(selections.Resolve(package, grant, s, null).Profile.Id == first);
            string second = selections.ApproveProfile(package, grant, "Two", 1002, "DirectML", "[slot_1]\nname=saved\n");
            await Error("profile_not_granted", () => selections.Resolve(package, grant, s, null));
            selections.Revoke(package, grant, "profile", first);
            await Error("profile_not_granted", () => selections.Resolve(package, grant, s, first));
            True(selections.Resolve(package, grant, s, null).Profile.Id == second);
            selections.Revoke(package, grant, "source", s);
            await Error("source_not_granted", () => selections.Resolve(package, grant, s, second));
            selections.Clear(package.Manifest.Id);
            True(((JsonArray)selections.List(package, grant)["profiles"]!).Count == 0);
        });
        await Test("Media approvals reject unsupported sources and preserve corrupt state", async () =>
        {
            string area = Area(); var selections = new MediaSelections(area);
            var package = Package(permissions: ["sessions.manage"]); var grant = new PermissionGrant(package, ["sessions.manage"]);
            await Error("invalid_source", () => selections.ApproveSource(package, grant, "https://example.invalid/media.mp4"));
            await Error("invalid_source", () => selections.ApproveSource(package, grant, Path.Combine(area, "playlist.m3u")));
            await Error("invalid_profile", () => selections.ApproveProfile(package, grant, "Invalid", 42, "DirectML", ""));
            await Error("invalid_profile", () => selections.ApproveProfile(package, grant, "Invalid", 1002, "DirectML", new string('a', 50000)));
            selections.ApproveProfile(package, grant, "Valid", 1002, "DirectML", "[global]\n");
            string state = Path.Combine(area, "media-approvals", package.Manifest.Id, "approvals.json");
            File.WriteAllText(state, "{corrupted");
            await Error("invalid_approvals", () => selections.List(package, grant));
            True(File.ReadAllText(state) == "{corrupted");
        });
        await Test("Session controls validate ownership and leave unrelated sessions responsive", async () =>
        {
            var provider = new ControlledProvider(); var registry = new SessionRegistry(provider);
            var owner = registry.CreateOwner(); var other = registry.CreateOwner();
            string id = await registry.OpenAsync(owner, "selected", null, default);
            await registry.ControlAsync(owner, id, true, null, default);
            await registry.ControlAsync(owner, id, null, 3.5, default);
            True(provider.Session.Paused && provider.Session.Position == 3.5);
            await Error("session_not_found", () => registry.ControlAsync(other, id, false, null, default));
            await Error("invalid_request", () => registry.ControlAsync(owner, id, null, double.NaN, default));
            await registry.ReleaseOwnerAsync(owner);
            True(provider.Session.Closed);
        });
        await Test("Failed session startup retains ownership until cleanup can succeed", async () =>
        {
            var provider = new FailedStartProvider(); var registry = new SessionRegistry(provider, 1, 1);
            var owner = registry.CreateOwner(); var other = registry.CreateOwner();
            await Error("cleanup_failed", () => registry.OpenAsync(owner, "selected", null, default));
            await Error("capacity_exceeded", () => registry.OpenAsync(other, "selected", null, default));
            provider.Session.FailClose = false;
            await registry.ReleaseOwnerAsync(owner);
            await Error("session_start", () => registry.OpenAsync(other, "selected", null, default));
            await registry.ReleaseOwnerAsync(other);
        });
        await Test("Asynchronous session close keeps capacity reserved without blocking addon callbacks", async () =>
        {
            var provider = new SlowCloseProvider(); var registry = new SessionRegistry(provider, 1, 1);
            var owner = registry.CreateOwner(); var other = registry.CreateOwner();
            string id = await registry.OpenAsync(owner, "selected", null, default);
            registry.RequestClose(owner, id);
            await provider.Session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            True((await registry.StatusAsync(owner, id, default))["state"]!.GetValue<string>() == "closing");
            await Error("capacity_exceeded", () => registry.OpenAsync(other, "selected", null, default));
            provider.Session.Release.SetResult();
            await registry.ReleaseOwnerAsync(owner);
            _ = await registry.OpenAsync(other, "selected", null, default);
            await registry.ReleaseOwnerAsync(other);
        });
        await Test("Management media consent is hash-bound and failed revocation is retryable", async () =>
        {
            string area = Area(), file = Path.Combine(area, "selected.mp4"); File.WriteAllText(file, "fixture");
            var package = Package(permissions: ["sessions.manage"]);
            new AddonRegistry(area).Install(package, ["sessions.manage"]);
            var media = new MediaSelections(area); var worker = new RetryAddon();
            await using var service = new AddonService(area, (_, _, _, _) => Task.FromResult<IAddonInstance>(worker), media);
            async Task<JsonNode?> Call(string method, JsonObject parameters) => await service.InvokeAsync("manager-test", method, parameters, default);
            _ = await Call("manager.hello", new() { ["major"] = 1L });
            await Error("integrity_mismatch", () => Call("media.approveSource", new() { ["id"] = package.Manifest.Id, ["expectedHash"] = new string('0', 64), ["path"] = file }));
            var source = await Call("media.approveSource", new() { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["path"] = file });
            _ = await Call("addons.start", new() { ["id"] = package.Manifest.Id });
            var revoke = new JsonObject { ["id"] = package.Manifest.Id, ["expectedHash"] = package.Hash, ["kind"] = "source", ["selectionId"] = source!["sourceId"]!.DeepClone() };
            await Error("cleanup_failed", () => Call("media.revoke", revoke));
            True(((JsonArray)(await Call("media.selections", new() { ["id"] = package.Manifest.Id }))!["sources"]!).Count == 1);
            worker.Fail = false;
            _ = await Call("media.revoke", revoke);
            True(worker.Closes == 2 && ((JsonArray)(await Call("media.selections", new() { ["id"] = package.Manifest.Id }))!["sources"]!).Count == 0);
        });
    }
    private sealed class FailedStartProvider : IProcessingSessionProvider
    {
        public FailingCloseSession Session { get; } = new();
        public Task<IProcessingSession> OpenAsync(string source, string? profile, CancellationToken token) =>
            throw new ProcessingSessionStartException(Session, new IOException("Fixture start failure"));
    }
    private sealed class SlowCloseProvider : IProcessingSessionProvider
    {
        public SlowCloseSession Session { get; } = new();
        public Task<IProcessingSession> OpenAsync(string source, string? profile, CancellationToken token) => Task.FromResult<IProcessingSession>(Session);
    }
    private sealed class SlowCloseSession : IProcessingSession
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<JsonObject> GetStatusAsync(CancellationToken token) => Task.FromResult(new JsonObject());
        public async ValueTask DisposeAsync() { Entered.TrySetResult(); await Release.Task; }
    }
    private sealed class RetryAddon : IAddonInstance
    {
        public bool Fail = true; public int Closes;
        public bool IsStopped { get; private set; }
        public Task<JsonNode?> SendEventAsync(string name, JsonNode? data = null, CancellationToken token = default) => Task.FromResult<JsonNode?>(null);
        public ValueTask DisposeAsync()
        {
            Closes++; IsStopped = true;
            if (Fail) throw new AddonException("cleanup_failed", "Fixture cleanup failure");
            return ValueTask.CompletedTask;
        }
    }
    private sealed class ControlledProvider : IProcessingSessionProvider
    {
        public ControlledSession Session { get; } = new();
        public Task<IProcessingSession> OpenAsync(string source, string? profile, CancellationToken token) => Task.FromResult<IProcessingSession>(Session);
    }
    private sealed class ControlledSession : IControllableProcessingSession
    {
        public bool Paused, Closed; public double Position;
        public Task<JsonObject> GetStatusAsync(CancellationToken token) => Task.FromResult(new JsonObject());
        public Task PauseAsync(bool paused, CancellationToken token) { Paused = paused; return Task.CompletedTask; }
        public Task SeekAsync(double seconds, CancellationToken token) { Position = seconds; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Closed = true; return ValueTask.CompletedTask; }
    }
}
