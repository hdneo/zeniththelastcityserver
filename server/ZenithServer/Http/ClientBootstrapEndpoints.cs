using System.Text.Json.Nodes;
using ZenithServer.Accounts;
using static ZenithServer.Http.GameApiEndpoints;

namespace ZenithServer.Http;

internal static class ClientBootstrapEndpoints
{
    public static void Map(WebApplication app)
    {
        var store = app.Services.GetRequiredService<AccountStore>();
        var clientData = Path.Combine(AppContext.BaseDirectory, "ClientData");

        void MapClientData(string route, string fileName)
        {
            var json = File.ReadAllText(Path.Combine(clientData, fileName));
            app.MapGet(route, (HttpContext ctx) =>
            {
                ctx.Items["zenith.handled"] = true;
                return Results.Text(json, "application/json");
            });
        }

        MapClientData("/api/build/56557", "build-56557.json");
        MapClientData("/api/tiny-content/prod/level_stats/public", "level_stats.json");
        MapClientData("/api/tiny-content/prod/level/public", "level.json");
        app.MapGet("/api/tiny-content/prod/reimagined_city_signage/public", (HttpContext ctx) => Ok(ctx, new
        {
            motd = "Adventures are available behind each gate.",
            ironGate = new { contentName = "Wasteland Grove", contentType = "Adventure" },
            silverGate = new { contentName = "Trials of Telos", contentType = "Boss Rush" },
            pandowlGate = new { contentName = "Shifting Isles", contentType = "Daily Session" },
        }));
        app.MapGet("/builds/ptr_meta_data.json", (HttpContext ctx) => Ok(ctx, new { }));
        app.MapGet("/api/news/{environment}", (HttpContext ctx) => Ok(ctx, Array.Empty<object>()));
        app.MapGet("/api/experiment/prod/client-list", (HttpContext ctx) => Ok(ctx, new Dictionary<string, bool>()));
        app.MapGet("/api/account/prod/relations", (HttpContext ctx) =>
            Ok(ctx, new { muted = Array.Empty<string>(), mutedBy = Array.Empty<string>(), blocked = Array.Empty<string>(), reported = Array.Empty<string>() }));

        Account? GetAccount(HttpContext ctx)
        {
            var session = store.Resolve(ctx.Request.Headers.Authorization.ToString());
            return session is null ? null : store.FindById(session.PlayFabId);
        }

        app.MapGet("/api/account/prod/preferences", (HttpContext ctx) =>
        {
            var account = GetAccount(ctx);
            return Ok(ctx, account is null ? new JsonObject() : store.ReadPreferences(account));
        });

        app.MapPost("/api/account/prod/preferences", async (HttpContext ctx) =>
        {
            var account = GetAccount(ctx);
            if (account is null) return Results.Unauthorized();

            var preferences = await ReadJson(ctx.Request);
            store.Update(account, a =>
            {
                foreach (var (key, value) in preferences)
                    a.Preferences[key] = value?.DeepClone();
            });
            return Ok(ctx, new { });
        });
    }
}
