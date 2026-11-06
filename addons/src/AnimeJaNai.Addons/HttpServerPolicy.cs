using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AnimeJaNai.Addons;

public sealed partial class HttpServerAccess
{
    private static bool AuthorizeEndpoint(ListenerBinding binding, HttpContext context)
    {
        string host = context.Request.Host.Host.Trim('[', ']');
        var allowed = binding.AllowedHosts ?? [binding.CertificateHost ?? binding.Address];
        int port = context.Request.Host.Port ?? (binding.Scheme == "https" ? 443 : 80);
        if (port != binding.Port || !allowed.Contains(host, StringComparer.OrdinalIgnoreCase) ||
            binding.Scope == "lan" && !IsLan(context.Connection.RemoteIpAddress))
        { context.Response.StatusCode = 403; return false; }
        if (!context.Request.Headers.TryGetValue("Origin", out var origins)) return true;
        if (origins.Count != 1 || binding.Cors is not { } cors || !cors.Origins.Contains(origins[0], StringComparer.Ordinal))
        { context.Response.StatusCode = 403; return false; }
        string origin = origins[0]!;
        bool preflight = context.Request.Method == "OPTIONS" && context.Request.Headers.ContainsKey("Access-Control-Request-Method");
        string method = preflight ? context.Request.Headers["Access-Control-Request-Method"].ToString() : context.Request.Method;
        if (!cors.Methods.Contains(method, StringComparer.Ordinal)) { context.Response.StatusCode = 403; return false; }
        if (preflight)
        {
            string[] requested = context.Request.Headers["Access-Control-Request-Headers"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (requested.Length > 32 || requested.Any(h => !cors.Headers.Contains(h, StringComparer.OrdinalIgnoreCase)))
            { context.Response.StatusCode = 403; return false; }
            context.Response.Headers.AccessControlAllowMethods = new StringValues(cors.Methods);
            context.Response.Headers.AccessControlAllowHeaders = new StringValues(cors.Headers);
            context.Response.Headers.AccessControlMaxAge = "600";
            context.Response.StatusCode = 204;
        }
        // Runs after addon/proxy headers so an upstream cannot change consent.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.AccessControlAllowOrigin = origin;
            if (cors.AllowCredentials) context.Response.Headers.AccessControlAllowCredentials = "true";
            if (cors.ExposeHeaders.Length > 0) context.Response.Headers.AccessControlExposeHeaders = new StringValues(cors.ExposeHeaders);
            string[] vary = context.Response.Headers.Vary.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!vary.Contains("Origin", StringComparer.OrdinalIgnoreCase)) context.Response.Headers.Vary = string.Join(", ", vary.Append("Origin"));
            return Task.CompletedTask;
        });
        return !preflight;
    }
    private static bool IsLan(IPAddress? ip)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        byte[] bytes = ip.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254
            : (bytes[0] & 0xfe) == 0xfc || ip.IsIPv6LinkLocal;
    }
}
