using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

internal sealed class ChallengeScenario(long playerId)
{
    private sealed record Area(string Id, string Prefab);
    private sealed record Route(Area Area, (double X, double Y, double Z) Gate, (double X, double Y, double Z) Exit);
    private enum PendingTeleport { None, AdventureRecovery, ReturnToCity }

    private static readonly Area Hub = new("6484fa5c-ad17-4b11-9412-f912afd75c14",
        "079955399a35be944956b3b059e0c5a7");
    private static readonly Route[] Routes =
    [
        new(new("d43f0672-3e63-4fdf-984e-f369a5249ea6", "09564d7cc0469a14d975d89a8cf48ab3"),
            (150, 6.5, 0), (126.714, 4.243, 0.297)),
        new(new("443a50bb-b60a-4423-afa6-21cc77b18c5b", "7b887548e731a834ab66f6df5e7fad37"),
            (-152.672, -1.263, -10.824), (-125.6, 0.54, -13.8)),
        new(new("a462fbec-d8c1-4740-b2bc-50ff774e577f", "9cc8e22f410ce4b47b0053d0986da85f"),
            (-12.711, 5.594, 168.24), (-11.9, 4.3, 140.7)),
    ];

    private readonly Dictionary<long, string?> _prefabs = new();
    private Area _expected = Hub;
    private int _route;
    private long _hubEntity;
    private bool _walkedBackFromBossRush;
    private PendingTeleport _pendingTeleport;

    public const int MapCount = 4;

    public void RecordPrefab(AddComponentOp prefab)
    {
        var reader = new SchemaReader(prefab.Data.Data);
        Require(reader.ReadFieldId() == 1, "prefab address field");
        Require(_prefabs.TryAdd(prefab.EntityId, reader.ReadOptionalString()), "area geometry only added once");
        Require(!reader.HasMore, "prefab component ends after address");
    }

    public void CheckPreloadedMaps()
    {
        Require(_prefabs.Count == MapCount && _prefabs.ContainsValue(Hub.Prefab) &&
            Routes.All(route => _prefabs.ContainsValue(route.Area.Prefab)), "all gate maps published before world entry");
    }

    public void CheckRemoval(long entityId) =>
        Require(!_prefabs.ContainsKey(entityId), "area geometry stays visible during travel");

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
        _expected = Routes[0].Area;
        return Move(avatarId, Routes[0].Gate);
    }

    public void CheckTeleport(ReadOnlySpan<byte> data, bool recovering)
    {
        Require(_pendingTeleport == (recovering ? PendingTeleport.AdventureRecovery : PendingTeleport.ReturnToCity),
            "gate crossing does not start a teleport");
        var (x, y, z) = recovering ? (-169.770, 0.5, -10.09) : (26.538, 6.797, 26.493);
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
            if (_route == Routes.Length - 1) _walkedBackFromBossRush = true;
            else _route++;
            _expected = Routes[_route].Area;
            return Move(avatarId, Routes[_route].Gate);
        }

        Console.WriteLine($"   [OK] Challenge area {_expected.Id}");
        if (_route == 1)
        {
            _pendingTeleport = PendingTeleport.AdventureRecovery;
            return Move(avatarId, (-169.770, -151, -10.09));
        }
        if (!_walkedBackFromBossRush) return WalkBack(avatarId);

        _expected = Hub;
        _pendingTeleport = PendingTeleport.ReturnToCity;
        var w = new SchemaWriter();
        w.AddString(1, Hub.Id);
        return WorldCommand(requestId, 10, w);
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
        return Move(avatarId, Routes[_route].Exit);
    }

    private CommandRequestOp WorldCommand(long id, uint command, SchemaWriter w) => new()
    {
        RequestId = id, EntityId = playerId, ComponentId = Ids.PlayerWorldComponent, CommandIndex = command, Data = w.ToArray(),
    };

    private static ComponentUpdateOp Move(long avatarId, (double X, double Y, double Z) position)
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
