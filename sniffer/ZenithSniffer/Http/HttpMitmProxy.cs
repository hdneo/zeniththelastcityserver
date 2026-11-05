using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Zenith.Protocol.Net;
using ZenithSniffer.Capture;
using ZenithSniffer.Net;
using ZenithSniffer.Udp;

namespace ZenithSniffer.Http;

/// <summary>
/// HTTP/HTTPS proxy for the game domains redirected via the hosts file.
/// Terminates TLS with certificates from the local CA, forwards to the REAL server
/// (IP via its own DNS), logs everything and rewrites the realm list to point at the UDP bridge.
/// </summary>
public static class HttpMitmProxy
{
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "Proxy-Authorization",
        "TE", "Trailer", "Content-Length", "Accept-Encoding",
    };

    public static WebApplication Build(SnifferOptions opts, CaptureSession capture, UpstreamDns dns, UdpBridgeManager udp, LocalCertificateAuthority ca)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 64 * 1024 * 1024;
            var ip = IPAddress.Parse(opts.BindAddress);
            if (opts.HttpPort > 0) k.Listen(ip, opts.HttpPort);
            if (opts.HttpsPort > 0)
                k.Listen(ip, opts.HttpsPort, l =>
                {
                    l.Protocols = HttpProtocols.Http1;
                    l.UseHttps(h => h.ServerCertificateSelector = (_, sni) => ca.GetServerCertificate(sni ?? "localhost"));
                });
        });

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (ctx, ct) =>
            {
                if (!opts.TryGetUpstreamOverride(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, out var target))
                    target = new IPEndPoint(await dns.ResolveAsync(ctx.DnsEndPoint.Host, ct), ctx.DnsEndPoint.Port);
                var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await s.ConnectAsync(target, ct);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            },
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };

        var app = builder.Build();
        app.Run(async ctx =>
        {
            var req = ctx.Request;
            var host = req.Host.Host;
            var port = req.Host.Port ?? (req.IsHttps ? 443 : 80);
            var scheme = req.IsHttps ? "https" : "http";
            var upstreamUrl = $"{scheme}://{host}{(port is 80 or 443 ? "" : ":" + port)}{req.PathBase}{req.Path}{req.QueryString}";

            using var bodyMs = new MemoryStream();
            await req.Body.CopyToAsync(bodyMs);
            var reqBody = bodyMs.ToArray();

            var msg = new HttpRequestMessage(new HttpMethod(req.Method), upstreamUrl);
            if (reqBody.Length > 0 || req.ContentLength is not null) msg.Content = new ByteArrayContent(reqBody);
            foreach (var h in req.Headers)
            {
                if (HopHeaders.Contains(h.Key)) continue;
                if (!msg.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray()))
                    msg.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
            }

            int status;
            byte[] respBody;
            Dictionary<string, string> respHeaders = new();
            string? note = null;
            try
            {
                using var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseContentRead, ctx.RequestAborted);
                status = (int)resp.StatusCode;
                respBody = await resp.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                ctx.Response.StatusCode = status;
                foreach (var h in resp.Headers.Concat(resp.Content.Headers))
                {
                    respHeaders[h.Key] = string.Join(", ", h.Value);
                    if (HopHeaders.Contains(h.Key) || h.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                    ctx.Response.Headers[h.Key] = h.Value.ToArray();
                }

                if (opts.RewriteRealmList && TryRewriteRealmList(respBody, udp, opts, out var rewritten, out var info))
                {
                    note = "realm list rewritten: " + info;
                    Console.WriteLine($"[HTTP] Realm list rewritten: {info}");
                    capture.LogHttp(new { t = DateTimeOffset.UtcNow, kind = "realm-list-original", url = upstreamUrl, body = Encoding.UTF8.GetString(respBody) });
                    respBody = rewritten;
                }
            }
            catch (Exception ex)
            {
                status = 502;
                respBody = Encoding.UTF8.GetBytes($"{{\"proxyError\":\"{ex.GetType().Name}: {ex.Message.Replace("\"", "'")}\"}}");
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                note = "upstream error";
                Console.WriteLine($"[HTTP] ERROR {upstreamUrl}: {ex.Message}");
            }

            ctx.Response.ContentLength = respBody.Length;
            await ctx.Response.Body.WriteAsync(respBody);

            capture.LogHttp(new
            {
                t = DateTimeOffset.UtcNow,
                method = req.Method,
                url = upstreamUrl,
                requestHeaders = req.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                requestBody = AsText(reqBody),
                status,
                responseHeaders = respHeaders,
                responseBody = AsText(respBody),
                note,
            });
            capture.LogHttpText(req.Method, upstreamUrl,
                req.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                AsText(reqBody) as string ?? $"<{reqBody.Length} bytes binary>",
                status, respHeaders,
                AsText(respBody) as string ?? $"<{respBody.Length} bytes binary>", note);
            Console.WriteLine($"[HTTP] {req.Method} {upstreamUrl} -> {status} ({respBody.Length} B)");
        });
        return app;
    }

    private static object AsText(byte[] data)
    {
        if (data.Length == 0) return "";
        try
        {
            var s = new UTF8Encoding(false, true).GetString(data);
            return s;
        }
        catch
        {
            return new { base64 = Convert.ToBase64String(data) };
        }
    }

    /// <summary>
    /// Detects { "&lt;id&gt;": { ..., "workerHost": "...", "workerPort": n }, ... } and replaces host/port
    /// with the local UDP bridge.
    /// </summary>
    private static bool TryRewriteRealmList(byte[] body, UdpBridgeManager udp, SnifferOptions opts, out byte[] result, out string info)
    {
        result = body;
        info = "";
        JsonNode? root;
        try { root = JsonNode.Parse(body); } catch { return false; }
        if (root is not JsonObject obj) return false;
        var changed = new List<string>();
        foreach (var (key, val) in obj)
        {
            if (val is not JsonObject e || e["workerHost"] is null || e["workerPort"] is null) continue;
            var host = e["workerHost"]!.ToString();
            if (!int.TryParse(e["workerPort"]!.ToString(), out var port)) continue;
            var local = udp.GetOrCreateBridge(host, port);
            e["workerHost"] = opts.AdvertiseHost;
            e["workerPort"] = local;
            changed.Add($"{key}: {host}:{port} -> {opts.AdvertiseHost}:{local}");
        }
        if (changed.Count == 0) return false;
        result = Encoding.UTF8.GetBytes(obj.ToJsonString());
        info = string.Join("; ", changed);
        return true;
    }
}
