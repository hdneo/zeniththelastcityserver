using System.Text.Json;
using System.Text.Json.Nodes;
using ZenithServer.Accounts;

namespace ZenithServer.Http;

/// <summary>
/// Minimal emulation of the PlayFab REST API (https://{TitleId}.playfabapi.com/{Api}/{Call}).
/// Response format: { "code":200, "status":"OK", "data":{...} }.
/// Everything not explicitly implemented returns "data":{} and is logged as a stub.
/// Best capture the real responses with the sniffer before the shutdown and rebuild them here.
/// </summary>
public static class PlayFabEndpoints
{
    private delegate object? Handler(HttpContext ctx, JsonObject body, Session? session, Account? account);

    public static void Map(WebApplication app)
    {
        var opts = app.Services.GetRequiredService<ServerOptions>();
        var store = app.Services.GetRequiredService<AccountStore>();
        var titleDataPath = Path.Combine(opts.DataDirectory, "titledata.json");

        Dictionary<string, string> TitleData()
        {
            if (!File.Exists(titleDataPath)) return new();
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(titleDataPath)) ?? new(); }
            catch { return new(); }
        }

        static string[]? Keys(JsonObject b) => b["Keys"] is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToArray() : null;

        object LoginResult(Account a, Session s, bool created) => new
        {
            SessionTicket = s.SessionTicket,
            PlayFabId = a.PlayFabId,
            NewlyCreated = created,
            LastLoginTime = a.LastLogin,
            EntityToken = new
            {
                EntityToken = s.AuthToken,
                TokenExpiration = DateTime.UtcNow.AddDays(1),
                Entity = new { Id = a.PlayFabId, Type = "title_player_account", TypeString = "title_player_account" },
            },
            InfoResultPayload = new { AccountInfo = AccountInfo(a) },
        };

        static object AccountInfo(Account a) => new
        {
            PlayFabId = a.PlayFabId,
            Created = a.Created,
            Username = a.Username,
            TitleInfo = new { DisplayName = a.Username, Created = a.Created, FirstLogin = a.Created, LastLogin = a.LastLogin, isBanned = false },
            PrivateInfo = new { Email = a.Email },
        };

        static object DataRecords(Dictionary<string, string> src, string[]? keys) => new
        {
            Data = src.Where(kv => keys is null || keys.Length == 0 || keys.Contains(kv.Key))
                      .ToDictionary(kv => kv.Key, kv => new { Value = kv.Value, LastUpdated = DateTime.UtcNow, Permission = "Private" }),
            DataVersion = 0,
        };

        var handlers = new Dictionary<string, Handler>(StringComparer.OrdinalIgnoreCase)
        {
            ["Client/GetTitleData"] = (_, b, _, _) =>
            {
                var td = TitleData(); var k = Keys(b);
                return new { Data = td.Where(kv => k is null || k.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value) };
            },
            ["Client/GetUserData"] = (_, b, _, a) => DataRecords(a?.UserData ?? new(), Keys(b)),
            ["Client/GetUserReadOnlyData"] = (_, b, _, a) => DataRecords(a?.ReadOnlyData ?? new(), Keys(b)),
            ["Client/UpdateUserData"] = (_, b, _, a) =>
            {
                if (a is not null)
                    store.Update(a, acc =>
                    {
                        if (b["Data"] is JsonObject d)
                            foreach (var kv in d) acc.UserData[kv.Key] = kv.Value?.ToString() ?? "";
                        if (b["KeysToRemove"] is JsonArray rm)
                            foreach (var k in rm) acc.UserData.Remove(k?.ToString() ?? "");
                    });
                return new { DataVersion = 1 };
            },
            ["Client/GetAccountInfo"] = (_, _, _, a) => a is null ? null : new { AccountInfo = AccountInfo(a) },
            ["Client/GetPlayerProfile"] = (_, _, _, a) => new { PlayerProfile = new { PlayerId = a?.PlayFabId, TitleId = opts.PlayFabTitleId, DisplayName = a?.Username } },
            ["Client/GetPlayerCombinedInfo"] = (_, _, _, a) => new
            {
                PlayFabId = a?.PlayFabId,
                InfoResultPayload = new
                {
                    AccountInfo = a is null ? null : AccountInfo(a),
                    UserData = a?.UserData.ToDictionary(kv => kv.Key, kv => new { Value = kv.Value }),
                    UserReadOnlyData = a?.ReadOnlyData.ToDictionary(kv => kv.Key, kv => new { Value = kv.Value }),
                    TitleData = TitleData(),
                    UserInventory = Array.Empty<object>(),
                    UserVirtualCurrency = new Dictionary<string, int>(),
                    PlayerStatistics = a?.Statistics.Select(kv => new { StatisticName = kv.Key, Value = kv.Value, Version = 0 }).ToArray(),
                },
            },
            ["Client/GetPlayerStatistics"] = (_, _, _, a) => new { Statistics = (a?.Statistics ?? new()).Select(kv => new { StatisticName = kv.Key, Value = kv.Value, Version = 0 }).ToArray() },
            ["Client/UpdatePlayerStatistics"] = (_, b, _, a) =>
            {
                if (a is not null && b["Statistics"] is JsonArray st)
                    store.Update(a, acc =>
                    {
                        foreach (var s in st.OfType<JsonObject>())
                            acc.Statistics[s["StatisticName"]?.ToString() ?? "?"] = s["Value"]?.GetValue<int>() ?? 0;
                    });
                return new { };
            },
            ["Client/GetUserInventory"] = (_, _, _, _) => new { Inventory = Array.Empty<object>(), VirtualCurrency = new Dictionary<string, int>(), VirtualCurrencyRechargeTimes = new Dictionary<string, object>() },
            ["Client/GetCatalogItems"] = (_, _, _, _) => new { Catalog = Array.Empty<object>() },
            ["Client/GetStoreItems"] = (_, b, _, _) => new { Store = Array.Empty<object>(), StoreId = b["StoreId"]?.ToString(), CatalogVersion = b["CatalogVersion"]?.ToString() },
            ["Client/GetTitleNews"] = (_, _, _, _) => new { News = Array.Empty<object>() },
            ["Client/GetFriendsList"] = (_, _, _, _) => new { Friends = Array.Empty<object>() },
            ["Client/GetAllUsersCharacters"] = (_, _, _, _) => new { Characters = Array.Empty<object>() },
            ["Client/GetTime"] = (_, _, _, _) => new { Time = DateTime.UtcNow },
            ["Client/WritePlayerEvent"] = (_, _, _, _) => new { EventId = Guid.NewGuid().ToString("N") },
            ["Client/WriteTitleEvent"] = (_, _, _, _) => new { EventId = Guid.NewGuid().ToString("N") },
            ["Client/ExecuteCloudScript"] = (_, b, _, _) => new
            {
                FunctionName = b["FunctionName"]?.ToString(), Revision = 0, FunctionResult = (object?)null, Logs = Array.Empty<object>(),
                ExecutionTimeSeconds = 0.0, ProcessorTimeSeconds = 0.0, MemoryConsumedBytes = 0, APIRequestsIssued = 0, HttpRequestsIssued = 0,
            },
            ["Event/WriteEvents"] = (_, b, _, _) => new { AssignedEventIds = (b["Events"] as JsonArray)?.Select(_ => Guid.NewGuid().ToString("N")).ToArray() ?? [] },
            ["Event/WriteTelemetryEvents"] = (_, b, _, _) => new { AssignedEventIds = (b["Events"] as JsonArray)?.Select(_ => Guid.NewGuid().ToString("N")).ToArray() ?? [] },
            ["Authentication/GetEntityToken"] = (_, _, s, a) => new
            {
                EntityToken = s?.AuthToken ?? AccountStore.NewToken(),
                TokenExpiration = DateTime.UtcNow.AddDays(1),
                Entity = new { Id = a?.PlayFabId, Type = "title_player_account" },
            },
        };

        // All LoginWith* variants: find or create the account by the respective ID.
        foreach (var login in new[] { "LoginWithCustomID", "LoginWithOculus", "LoginWithSteam", "LoginWithPlayFab", "LoginWithEmailAddress", "LoginWithPSN", "LoginWithOpenIdConnect", "LoginWithAndroidDeviceID" })
        {
            handlers["Client/" + login] = (_, b, _, _) =>
            {
                var id = b["CustomId"]?.ToString() ?? b["OculusId"]?.ToString() ?? b["Username"]?.ToString() ?? b["Email"]?.ToString() ?? b["SteamTicket"]?.ToString() ?? "anon";
                var acc = store.FindByPlatformUser(id) ?? store.FindByUsername(id);
                var created = false;
                if (acc is null) { acc = store.Create(b["Username"]?.ToString() ?? "Player", id); created = true; }
                return LoginResult(acc, store.CreateSession(acc), created);
            };
        }

        async Task<IResult> Handle(HttpContext ctx, string api, string call)
        {
            var body = await GameApiEndpoints.ReadJson(ctx.Request);
            var auth = ctx.Request.Headers["X-Authorization"].FirstOrDefault()
                       ?? ctx.Request.Headers["X-EntityToken"].FirstOrDefault();
            var session = store.Resolve(auth, body["PlayFabId"]?.ToString());
            var account = session is null ? null : store.FindById(session.PlayFabId);
            object? data = new { };
            if (handlers.TryGetValue($"{api}/{call}", out var h))
            {
                ctx.Items["zenith.handled"] = true;
                data = h(ctx, body, session, account);
                if (data is null)
                    return Results.Json(new { code = 401, status = "Unauthorized", error = "NotAuthenticated", errorCode = 1074, errorMessage = "Session not found" }, statusCode: 401);
            }
            return Results.Json(new { code = 200, status = "OK", data });
        }

        foreach (var api in new[] { "Client", "Server", "Authentication", "Event", "CloudScript", "Profile", "Group", "Data", "Economy", "Inventory", "Catalog", "Multiplayer", "Experimentation", "Insights", "Progression" })
        {
            var a = api;
            app.MapPost($"/{a}/{{call}}", (HttpContext ctx, string call) => Handle(ctx, a, call));
        }
    }
}
