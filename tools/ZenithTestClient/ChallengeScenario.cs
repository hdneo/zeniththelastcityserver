using System.Text.Json;
using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

internal sealed class ChallengeScenario(long playerId, int firstRoute = 0)
{
    private sealed record Area(string Id, string Prefab);
    private sealed record Route(Area Area, (double X, double Y, double Z) Gate, (double X, double Y, double Z) Exit);
    private enum PendingTeleport { None, AdventureRecovery, ReturnToCity }

    private static readonly Area Hub = new("6484fa5c-ad17-4b11-9412-f912afd75c14",
        "079955399a35be944956b3b059e0c5a7");
    private readonly Route[] _routes =
    [
        new(new("d43f0672-3e63-4fdf-984e-f369a5249ea6", ""), (150, 6.5, 0), default),
        new(new("443a50bb-b60a-4423-afa6-21cc77b18c5b", "7b887548e731a834ab66f6df5e7fad37"),
            (-152.672, -1.263, -10.824), (-125.6, 0.54, -13.8)),
        new(new("a462fbec-d8c1-4740-b2bc-50ff774e577f", "9cc8e22f410ce4b47b0053d0986da85f"),
            (-12.711, 5.594, 168.24), (-11.9, 4.3, 140.7)),
    ];

    private readonly Dictionary<long, string?> _prefabs = new();
    private Area _expected = Hub;
    private Area _current = Hub;
    private int _route = firstRoute;
    private long _hubEntity;
    private bool _walkedBackFromBossRush;
    private PendingTeleport _pendingTeleport;

    public const int MapCount = 4;
    public int RouteIndex => _route;
    public EncounterScenario.Stop[]? DailyStops { get; private set; }
    public bool StartEncounter => _expected != Hub && !_walkedBackFromBossRush;
    public bool InChallengeArea => _current != Hub;

    public void RecordPrefab(AddComponentOp prefab)
    {
        var reader = new SchemaReader(prefab.Data.Data);
        Require(reader.ReadFieldId() == 1, "prefab address field");
        Require(_prefabs.TryAdd(prefab.EntityId, reader.ReadOptionalString()), "area geometry only added once");
        Require(!reader.HasMore, "prefab component ends after address");
        if (DailyStops is not null && _prefabs.Count == MapCount) CheckPreloadedMaps();
    }

    public void CheckPreloadedMaps()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "daily_layouts.json"));
        using var document = JsonDocument.Parse(file);
        var daily = document.RootElement.EnumerateArray().Single(layout => _prefabs.ContainsValue(layout.GetProperty("prefab").GetString()));
        var route = _routes[0];
        _routes[0] = route with
        {
            Area = route.Area with { Prefab = daily.GetProperty("prefab").GetString()! },
            Exit = ReadPosition(daily.GetProperty("exit")),
        };
        if (_expected == route.Area) _expected = _routes[0].Area;
        DailyStops = daily.GetProperty("islands").EnumerateArray().Select(island =>
        {
            var waves = island.GetProperty("waves").EnumerateArray().ToArray();
            var prefabs = waves.SelectMany(wave => wave.GetProperty("spawns").EnumerateArray())
                .Select(spawn => spawn.GetProperty("prefab").GetString()!).ToHashSet(StringComparer.Ordinal);
            var total = waves.Sum(wave => wave.GetProperty("spawns").GetArrayLength() +
                (wave.TryGetProperty("replacements", out var replacements) ? replacements.GetInt32() : 0));
            return new EncounterScenario.Stop(ReadPosition(island.GetProperty("center")),
                waves[0].GetProperty("spawns").GetArrayLength(), total, prefabs);
        }).ToArray();
        Require(_prefabs.Count == MapCount && _prefabs.ContainsValue(Hub.Prefab) &&
            _routes.All(gate => _prefabs.ContainsValue(gate.Area.Prefab)), "all gate maps published before world entry");
    }

    private static (double X, double Y, double Z) ReadPosition(JsonElement value) =>
        (value[0].GetDouble(), value[1].GetDouble(), value[2].GetDouble());

    public void CheckRemoval(long entityId)
    {
        if (!_prefabs.TryGetValue(entityId, out var prefab)) return;
        Require(prefab == _routes[0].Area.Prefab && _current != _routes[0].Area,
            "only the inactive daily layout can be replaced");
        _prefabs.Remove(entityId);
    }

    public void RecordWorld(byte[] data, bool initialEntry)
    {
        var reader = new SchemaReader(data);
        if (initialEntry) Require(reader.ReadFieldId() == 1 && reader.ReadInt32() == 1, "in main world");
        Require(reader.ReadFieldId() == 4 && reader.ReadOptionHasValue(), "current area published without reloading the world");
        var area = new SchemaReader(reader.ReadBytesSpan());
        Require(area.ReadFieldId() == 1 && area.ReadOptionalString() == _expected.Id, "expected area ID");
        Require(area.ReadFieldId() == 2 && area.ReadOptionalString() is null, "local area template");
        Require(area.ReadFieldId() == 3, "area entity field");
        var entityId = area.ReadInt64();
        Require(_prefabs.GetValueOrDefault(entityId) == _expected.Prefab, "current area references its bundled geometry");
        _current = _expected;
        if (_expected == Hub)
        {
            if (_hubEntity == 0) _hubEntity = entityId;
            Require(entityId == _hubEntity, "stable Neo City trigger across transitions");
        }
        Require(!reader.HasMore, "world update ends after current area");
    }

    public CommandRequestOp RequestAreas(long requestId)
    {
        var w = new SchemaWriter();
        w.AddString(1, Hub.Id);
        w.AddInt32(2, 0, isOption: true);
        w.AddBool(3, true, isOption: true);
        return WorldCommand(requestId, 4, w);
    }

    public ComponentUpdateOp EnterFirstArea(CommandResponseOp response, long avatarId)
    {
        Require(response.Status == StatusCode.Success, "list local areas");
        var reader = new SchemaReader(response.Data);
        Require(reader.ReadFieldId() == 1 && reader.ReadListCount() == 1, "one local Neo City area");
        var hub = new SchemaReader(reader.ReadBytesSpan());
        Require(hub.ReadFieldId() == 1 && hub.ReadOptionalString() == Hub.Id && !reader.HasMore, "Neo City list entry");
        _expected = _routes[_route].Area;
        return Move(avatarId, _routes[_route].Gate);
    }

    public void CheckTeleport(ReadOnlySpan<byte> data, bool recovering)
    {
        Require(_pendingTeleport == (recovering ? PendingTeleport.AdventureRecovery : PendingTeleport.ReturnToCity),
            "gate crossing does not start a teleport");
        var (x, y, z) = recovering ? (-395.8289, 36.9117, 65.9843) : (26.538, 6.797, 26.493);
        var teleport = new SchemaReader(data);
        Require(teleport.ReadFieldId() == 1, "arrival coordinates");
        var location = new SchemaReader(teleport.ReadBytesSpan());
        Require(location.ReadFieldId() == 1 && Math.Abs(location.ReadDouble() - x) < 0.05 &&
            location.ReadFieldId() == 2 && Math.Abs(location.ReadDouble() - y) < 0.05 &&
            location.ReadFieldId() == 3 && Math.Abs(location.ReadDouble() - z) < 0.05, "expected checkpoint");
    }

    public ZfOp? AreaChanged(long requestId, long avatarId)
    {
        if (_expected == Hub)
        {
            if (_pendingTeleport == PendingTeleport.ReturnToCity) return null;
            if (_route == _routes.Length - 1) _walkedBackFromBossRush = true;
            else _route++;
            _expected = _routes[_route].Area;
            return Move(avatarId, _routes[_route].Gate);
        }

        Console.WriteLine($"   [OK] Challenge area {_expected.Id}");
        if (StartEncounter) return null;

        _expected = Hub;
        _pendingTeleport = PendingTeleport.ReturnToCity;
        var w = new SchemaWriter();
        w.AddString(1, Hub.Id);
        return WorldCommand(requestId, 10, w);
    }

    public ComponentUpdateOp FinishEncounter(long avatarId)
    {
        if (_route != 1) return WalkBack(avatarId);
        _pendingTeleport = PendingTeleport.AdventureRecovery;
        return Move(avatarId, (-169.770, -151, -10.09));
    }

    public ZfOp? FinishTeleport(long avatarId)
    {
        Require(_pendingTeleport != PendingTeleport.None, "expected a recovery or an explicit return");
        var recovering = _pendingTeleport == PendingTeleport.AdventureRecovery;
        _pendingTeleport = PendingTeleport.None;
        if (!recovering) return null;
        Console.WriteLine("   [OK] Adventure recovery checkpoint");
        return WalkBack(avatarId);
    }

    private ComponentUpdateOp WalkBack(long avatarId)
    {
        _expected = Hub;
        return Move(avatarId, _routes[_route].Exit);
    }

    private CommandRequestOp WorldCommand(long id, uint command, SchemaWriter w) => new()
    {
        RequestId = id, EntityId = playerId, ComponentId = Ids.PlayerWorldComponent, CommandIndex = command, Data = w.ToArray(),
    };

    public static ComponentUpdateOp Move(long avatarId, (double X, double Y, double Z) position)
    {
        var sync = new SchemaWriter();
        sync.AddObject(1, coords =>
        {
            coords.AddFloat(1, (float)position.X);
            coords.AddFloat(2, (float)position.Y);
            coords.AddFloat(3, (float)position.Z);
        });
        sync.AddUInt32(2, 0xDFF7FDFF);
        return new() { EntityId = avatarId, ComponentId = Ids.ZSyncComponent, Data = sync.ToArray() };
    }

    private static void Require(bool valid, string detail)
    {
        if (!valid) throw new InvalidDataException($"Challenge scenario failed: {detail}");
    }
}
