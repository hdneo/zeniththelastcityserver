using System.Text;
using System.Text.Json;

namespace ZenithServer.Http;

/// <summary>Writes every HTTP request incl. body and response to data/logs/http.jsonl.</summary>
public sealed class HttpLogging
{
    private readonly RequestDelegate _next;
    private readonly ILogger<HttpLogging> _log;
    private readonly StreamWriter _file;
    private readonly bool _bodies;
    private static readonly object FileLock = new();

    public HttpLogging(RequestDelegate next, ILogger<HttpLogging> log, ServerOptions opts)
    {
        _next = next;
        _log = log;
        _bodies = opts.Http.LogBodies;
        var dir = Path.Combine(opts.DataDirectory, "logs");
        Directory.CreateDirectory(dir);
        _file = new StreamWriter(new FileStream(Path.Combine(dir, "http.jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        req.EnableBuffering();
        var logRequestBody = _bodies &&
            !string.Equals(req.Path.Value?.TrimEnd('/'), "/Client/ReportPlayer", StringComparison.OrdinalIgnoreCase);
        string reqBody = "";
        if (logRequestBody && (req.ContentLength ?? 0) > 0)
        {
            using var sr = new StreamReader(req.Body, Encoding.UTF8, leaveOpen: true);
            reqBody = await sr.ReadToEndAsync();
            req.Body.Position = 0;
        }

        var originalBody = ctx.Response.Body;
        await using var capture = new MemoryStream();
        ctx.Response.Body = capture;
        try
        {
            await _next(ctx);
        }
        finally
        {
            capture.Position = 0;
            var respBytes = capture.ToArray();
            ctx.Response.Body = originalBody;
            if (respBytes.Length > 0) await originalBody.WriteAsync(respBytes);

            var handled = ctx.Items.ContainsKey("zenith.handled");
            var entry = new
            {
                time = DateTimeOffset.UtcNow,
                remote = ctx.Connection.RemoteIpAddress?.ToString(),
                method = req.Method,
                scheme = req.Scheme,
                host = req.Host.Value,
                path = req.Path.Value,
                query = req.QueryString.Value,
                handled,
                requestHeaders = req.Headers.Where(h => h.Key is not ("Cookie")).ToDictionary(h => h.Key, h => h.Value.ToString()),
                requestBody = logRequestBody ? Truncate(reqBody) : null,
                status = ctx.Response.StatusCode,
                responseBody = _bodies ? Truncate(Encoding.UTF8.GetString(respBytes)) : null,
            };
            lock (FileLock) _file.WriteLine(JsonSerializer.Serialize(entry));
            var tag = handled ? "" : "  <-- NOT IMPLEMENTED (stub)";
            _log.LogInformation("HTTP {Method} {Host}{Path} -> {Status}{Tag}", req.Method, req.Host.Value, req.Path.Value, ctx.Response.StatusCode, tag);
        }
    }

    private static string Truncate(string s) => s.Length > 65536 ? s[..65536] + "…" : s;
}
