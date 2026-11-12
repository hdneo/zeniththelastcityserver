using System.Text.Json;
using System.Text.Json.Nodes;
using ZenithServer.Accounts;

namespace ZenithServer.Http;

/// <summary>
/// Skywave (login) + Nexus/Noodle (realm list, join) + Miso (matchmaking) + stubs.
/// Paths/field names reconstructed from the client, see docs/03_HTTP_APIs.md.
/// </summary>
public static class GameApiEndpoints
{
    public static async Task<JsonObject> ReadJson(HttpRequest req)
    {
        try
        {
            if ((req.ContentLength ?? 0) == 0 && !req.Body.CanSeek) return new JsonObject();
            req.Body.Position = 0;
            var node = await JsonNode.ParseAsync(req.Body);
            return node as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToString();

    internal static IResult Ok(HttpContext ctx, object body)
    {
        ctx.Items["zenith.handled"] = true;
        return Results.Json(body);
    }

    public static void Map(WebApplication app)
    {
        var opts = app.Services.GetRequiredService<ServerOptions>();
        var store = app.Services.GetRequiredService<AccountStore>();
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GameApi");

        // ------------------------------------------------------------------
        // Skywave  (ReleaseConfiguration.AuthSkywaveURL = https://skywave.zenithmmo.com/api/game)
        //   Header: Authorization: Bearer be4ba5c7-4314-48c6-8b97-9991cae08c51 (hard-coded in the client)
        // ------------------------------------------------------------------
        Account? FindOrCreatePlatform(JsonObject b, bool create)
        {
            var userId = Str(b, "user_id") ?? Str(b, "code") ?? "anonymous";
            var acc = store.FindByPlatformUser(userId);
            if (acc is null && create && opts.Auth.AutoRegister)
                acc = store.Create(Str(b, "username") ?? $"Rift{userId[^Math.Min(4, userId.Length)..]}", userId, Str(b, "email"), Str(b, "password"));
            return acc;
        }

        object AuthResponse(Account a)
        {
            var s = store.CreateSession(a);
            return new
            {
                success = true,
                sessionTicket = s.SessionTicket,
                authToken = s.AuthToken,
                sessionID = s.SessionId,
                playFabId = a.PlayFabId,
                displayName = a.Username,
                username = a.Username,
                errorCode = 0,
                message = "",
            };
        }

        object Fail(string msg, int code = 1) => new { success = false, sessionTicket = "", authToken = "", sessionID = "", playFabId = "", username = "", errorCode = code, message = msg };

        var sky = app.MapGroup("/api/game");
        // Oculus version: ...rift, Steam version: ...steam (from captures: checksteam/loginsteam)
        void MapPlatform(string name, Delegate handler)
        {
            foreach (var platform in new[] { "rift", "steam" })
                sky.MapMethods($"/{name}{platform}", ["GET", "POST"], handler);
        }

        // CheckResponse { success, hasAccount, errorCode, message, email, username, isPTR }
        MapPlatform("check", async (HttpContext ctx) =>
        {
            var b = await ReadJson(ctx.Request);
            var acc = FindOrCreatePlatform(b, create: true);
            return Ok(ctx, new
            {
                success = true,
                hasAccount = acc is not null,
                errorCode = 0,
                message = "",
                email = acc?.Email ?? "",
                username = acc?.Username ?? "",
                isPTR = false,
            });
        });

        // TokenRequest { code, user_id, version, ps5 } -> AuthResponse
        MapPlatform("login", async (HttpContext ctx) =>
        {
            var b = await ReadJson(ctx.Request);
            var acc = FindOrCreatePlatform(b, create: true);
            return Ok(ctx, acc is null ? Fail("No account for this platform user") : AuthResponse(acc));
        });

        // RegisterRequest { code, user_id, email, username, password, referrer, ps5 } -> AuthResponse
        MapPlatform("register", async (HttpContext ctx) =>
        {
            var b = await ReadJson(ctx.Request);
            var userId = Str(b, "user_id") ?? AccountStore.NewToken(6);
            var acc = store.FindByPlatformUser(userId)
                      ?? store.Create(Str(b, "username") ?? "Player", userId, Str(b, "email"), Str(b, "password"));
            return Ok(ctx, AuthResponse(acc));
        });

        // LoginRequest { username, password, version } -> AuthResponse
        sky.MapMethods("/login", ["GET", "POST"], async (HttpContext ctx) =>
        {
            var b = await ReadJson(ctx.Request);
            var user = Str(b, "username") ?? "";
            var pass = Str(b, "password") ?? "";
            var acc = store.FindByUsername(user);
            if (acc is null && opts.Auth.AutoRegister) acc = store.Create(user, null, null, pass);
            if (acc is null) return Ok(ctx, Fail("Unknown user"));
            if (acc.PasswordHash is not null && !AccountStore.VerifyPassword(pass, acc.PasswordHash)) return Ok(ctx, Fail("Wrong password", 2));
            return Ok(ctx, AuthResponse(acc));
        });

        // LinkRequest { code, sessionTicket, user_id, ps5 } -> LinkResponse
        MapPlatform("link", async (HttpContext ctx) =>
        {
            var b = await ReadJson(ctx.Request);
            var s = store.Resolve(Str(b, "sessionTicket"));
            var acc = s is null ? null : store.FindById(s.PlayFabId);
            if (acc is not null) store.Update(acc, a => a.PlatformUserId = Str(b, "user_id") ?? a.PlatformUserId);
            return Ok(ctx, new { success = acc is not null, message = "", errorCode = 0, linkedToPlatform = "Oculus", linkedToPlatformID = Str(b, "user_id") ?? "" });
        });

        sky.MapMethods("/updateemail", ["GET", "POST"], async (HttpContext ctx) =>
        {
            var b = await ReadJson(ctx.Request);
            var s = store.Resolve(Str(b, "sessionTicket"));
            var acc = s is null ? null : store.FindById(s.PlayFabId);
            if (acc is not null) store.Update(acc, a => a.Email = Str(b, "email"));
            return Ok(ctx, new { success = acc is not null, message = "", errorCode = 0 });
        });

        sky.MapMethods("/joinPTR", ["GET", "POST"], (HttpContext ctx) => Ok(ctx, new { success = false, message = "PTR not available", errorCode = 1 }));

        // ------------------------------------------------------------------
        // Nexus / Noodle  (ReleaseConfiguration.AuthConfigURL = https://nexus.zenithmmo.com)
        // ------------------------------------------------------------------

        app.MapMethods("/", ["GET", "HEAD"], (HttpContext ctx) =>
        {
            ctx.Items["zenith.handled"] = true;
            return Results.Content("""
                <!doctype html>
                <html lang="en">
                <head><meta charset="utf-8"><title>Zenith Community Server</title></head>
                <body>
                  <h1>Zenith Community Server</h1>
                  <p><a href="/api/status">Service status</a> · <a href="/api/realm/list">Realms</a></p>
                </body>
                </html>
                """, "text/html; charset=utf-8");
        });

        // AuthClientSkywave.ServiceStatusData { message, loginEnabled }  (NoodleAuthConfig.serverNewsURL)
        app.MapGet("/api/status", (HttpContext ctx) => Ok(ctx, new { message = "", loginEnabled = true }));

        // Dictionary<string, DeploymentInfo | RealmListEntry>
        //   DeploymentInfo { id, name, region, status, load }  +  RealmListEntry { workerHost, workerPort }
        app.MapMethods("/api/realm/list/{shardTag?}", ["GET", "POST"], (HttpContext ctx, string? shardTag) =>
        {
            var dict = new Dictionary<string, object>();
            foreach (var r in opts.Realms)
            {
                if (shardTag is not null && !r.Tags.Contains(shardTag, StringComparer.OrdinalIgnoreCase)) continue;
                dict[r.Id] = new
                {
                    id = r.Id,
                    name = r.Name,
                    region = r.Region,
                    status = r.Status,
                    load = r.Load,
                    workerHost = r.WorkerHost ?? opts.PublicHost,
                    workerPort = r.WorkerPort ?? opts.ZenFabric.Port,
                    tags = r.Tags,
                    version = r.Version,
                };
            }
            return Ok(ctx, dict);
        });

        // NoodleAuthZeuzRequest { projectId, playFabId, deploymentId, titleId, token } -> NoodleAuthResponse
        app.MapMethods("/api/realm/join", ["POST", "GET"], async (HttpContext ctx) =>
        {
            var b = await ReadJson(ctx.Request);
            var playFabId = Str(b, "playFabId");
            var token = Str(b, "token") ?? ctx.Request.Headers.Authorization.ToString();
            var s = store.Resolve(token, opts.Auth.AllowUnknownIdentityTokens ? playFabId : null);
            if (s is not null && playFabId is not null &&
                !string.Equals(s.PlayFabId, playFabId, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "session does not match account" }, statusCode: 401);
            if (s is null)
            {
                if (!opts.Auth.AllowUnknownIdentityTokens)
                    return Results.Json(new { error = "unknown session" }, statusCode: 401);
                var acc = (playFabId is null ? null : store.FindById(playFabId)) ?? store.Create("Guest", null);
                s = store.CreateSession(acc);
            }
            var deployment = Str(b, "deploymentId") ?? opts.Realms.FirstOrDefault()?.Id ?? "zenith-local-1";
            store.IssueJoinTokens(s, deployment);
            log.LogInformation("Realm join: {PlayFabId} -> {Deployment}", s.PlayFabId, deployment);
            return Ok(ctx, new
            {
                projectId = opts.ProjectId,
                playFabId = s.PlayFabId,
                queuePosition = 0,
                spatialPlayerIdentityToken = s.IdentityToken,
                spatialLoginToken = s.JoinToken,
                joinToken = s.JoinToken,
                identityToken = s.IdentityToken,
                token = s.IdentityToken,
            });
        });

        // ------------------------------------------------------------------
        // Miso matchmaking (ReleaseConfiguration.MisoURL) – stubs only.
        // ------------------------------------------------------------------
        app.MapMethods("/api/matchmaking/{action}", ["GET", "POST"], (HttpContext ctx, string action) =>
            Ok(ctx, new { success = false, message = "matchmaking not implemented", action }));
        app.MapMethods("/api/deployment/{action}", ["GET", "POST"], (HttpContext ctx, string action) =>
            Ok(ctx, new { instances = Array.Empty<object>() }));

        // Version check (level1: https://ramenvr.sfo3.digitaloceanspaces.com/builds/client_versions.json)
        app.MapGet("/builds/client_versions.json", (HttpContext ctx) =>
        {
            var file = Path.Combine(opts.DataDirectory, "client_versions.json");
            ctx.Items["zenith.handled"] = File.Exists(file);
            return File.Exists(file) ? Results.Text(File.ReadAllText(file), "application/json") : Results.Json(new { });
        });
    }
}
