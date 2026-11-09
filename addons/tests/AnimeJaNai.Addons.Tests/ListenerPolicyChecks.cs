using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using AnimeJaNai.Addons;

internal static partial class Checks
{
    private static async Task ListenerPolicyChecks()
    {
        await Test("Reviewed CORS answers preflight and protects actual responses while denying other origins", async () =>
        {
            var package = Package(permissions: ["network.listen"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            var selections = new ListenerSelections(Area()); int port = ListenerPort();
            var binding = new ListenerBinding("127.0.0.1", port, [], [], Cors: new(["https://client.example"], ["GET"], ["X-Client"], ["ETag"], true));
            string selected = selections.Approve(package, grant, "test", binding);
            await using var access = new HttpServerAccess(package, grant, selections); string server = access.Open(selected);
            await Until(() => access.Status(server)["state"]!.GetValue<string>() == "listening");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var preflight = new HttpRequestMessage(HttpMethod.Options, $"http://127.0.0.1:{port}/");
            preflight.Headers.Add("Origin", "https://client.example"); preflight.Headers.Add("Access-Control-Request-Method", "GET"); preflight.Headers.Add("Access-Control-Request-Headers", "X-Client");
            using var allowed = await client.SendAsync(preflight);
            True(allowed.StatusCode == HttpStatusCode.NoContent && allowed.Headers.GetValues("Access-Control-Allow-Origin").Single() == "https://client.example");
            True(allowed.Headers.GetValues("Access-Control-Allow-Credentials").Single() == "true");
            using var actual = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/"); actual.Headers.Add("Origin", "https://client.example");
            var pending = client.SendAsync(actual); string request = (await NextHttp(access)).Data["requestId"]!.GetValue<string>();
            access.Respond(request, 200, [new("Vary", ["Accept-Encoding"])], [1, 2]);
            using var response = await pending;
            True(response.Headers.GetValues("Vary").Any(v => v.Contains("Origin")) && response.Headers.Contains("Access-Control-Allow-Origin"));
            using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/"); deniedRequest.Headers.Add("Origin", "https://other.example");
            using var denied = await client.SendAsync(deniedRequest); True(denied.StatusCode == HttpStatusCode.Forbidden);
            await Error("invalid_listener", () => (binding with { Cors = binding.Cors! with { Origins = ["*"] } }).Validate());
            await Error("invalid_listener", () => (binding with { Scope = "loopback", Address = "0.0.0.0" }).Validate());
            (binding with { Scope = "public", Address = "0.0.0.0", AllowedHosts = ["bridge.example"] }).Validate();
        });
        if (OperatingSystem.IsWindows()) await Test("HTTPS certificate import protects private material, validates names and serves TLS without changing client trust", async () =>
        {
            string area = Area(); var package = Package(permissions: ["network.listen"]); var grant = new PermissionGrant(package, package.Manifest.Permissions);
            var selections = new ListenerSelections(area); using var rsa = RSA.Create(2048);
            var csr = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); csr.CertificateExtensions.Add(names.Build());
            csr.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var certificate = csr.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            byte[] pfx = certificate.Export(X509ContentType.Pkcs12, "fixture-password"); string path = Path.Combine(area, "fixture.pfx"); File.WriteAllBytes(path, pfx);
            await Error("invalid_certificate", () => selections.Certificates.Import(package, grant, path, "wrong", "fixture"));
            string certId = selections.Certificates.Import(package, grant, path, "fixture-password", "fixture");
            int port = ListenerPort(); var binding = new ListenerBinding("127.0.0.1", port, [], [], Scheme: "https", CertificateId: certId, CertificateHost: "localhost", AllowedHosts: ["localhost", "127.0.0.1"]);
            True(selections.Certificates.InspectBinding(package, grant, binding)["hostnameMatches"]!.GetValue<bool>());
            await Error("certificate_hostname_mismatch", () => selections.Certificates.InspectBinding(package, grant, binding with { CertificateHost = "other.example" }));
            var other = Package(version: "1.0.1", permissions: ["network.listen"]);
            await Error("certificate_unavailable", () => selections.Certificates.InspectBinding(other, new(other, other.Manifest.Permissions), binding));
            string selected = selections.Approve(package, grant, "TLS fixture", binding);
            await using var access = new HttpServerAccess(package, grant, selections); string server = access.Open(selected);
            await Until(() => access.Status(server)["state"]!.GetValue<string>() != "opening");
            True(access.Status(server)["state"]!.GetValue<string>() == "listening", access.Status(server).ToJsonString());
            using (var normalClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                try { using var untrusted = await normalClient.GetAsync($"https://127.0.0.1:{port}/"); throw new Exception("Fixture unexpectedly became trusted"); }
                catch (HttpRequestException) { }
            }
            // Test-only exact-certificate pin; production handlers use normal trust.
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var pending = client.GetAsync($"https://127.0.0.1:{port}/");
            (string Name, JsonObject Data)? received = null;
            await Until(() => (received = access.TakeEvent()) is not null || pending.IsCompleted);
            if (received is null)
            {
                using var early = await pending;
                throw new Exception("TLS request ended before an event: " + (int)early.StatusCode);
            }
            string request = received.Value.Data["requestId"]!.GetValue<string>(); access.Respond(request, 200, [], [42]);
            using var response = await pending; True((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 42 }));
            True(access.Status(server)["reachability"]!.GetValue<string>() == "unverified");
        });
    }
}
