using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

namespace ZenithServer.ZenFabric;

internal static class EncounterSignals
{
    internal const uint Activated = 4023;
    private const uint CustomEvent = 4026;
    private const uint Blackboard = 7014;

    internal sealed record Anchor(string Id, (float X, float Y, float Z) Position);

    public static StoredEntity Create(ZenFabricRuntime rt, (string Realm, string Area) space, Anchor anchor)
    {
        var prewarm = new SchemaWriter();
        prewarm.AddString(1, anchor.Id);
        prewarm.AddEmptyOption(2);
        prewarm.AddString(3, Guid.NewGuid().ToString("D"));
        prewarm.AddBool(4, true);
        prewarm.AddBool(5, true);

        var ownership = new SchemaWriter();
        ownership.AddBool(1, false);
        ownership.AddBool(2, false);
        var board = new SchemaWriter();
        board.AddEmptyList(1);
        var number = new SchemaWriter();
        number.AddInt32(1, 0);
        var server = ZenFabricRuntime.InternalWorkerEntityId;
        return rt.CreateAreaEntity(space,
        [
            new(StarterWeapons.PreWarm, prewarm.ToArray()),
            new(Ids.AuthorityDelegation, StandardComponents.AuthorityDelegation(new Dictionary<uint, long>
            {
                [Ids.AuthorityDelegationSet] = server,
                [Ids.DefaultServerSet] = server,
                [Ids.PositionSet] = server,
                [Ids.ZSyncSet] = server,
            })),
            new(Ids.Metadata, StandardComponents.Metadata("NOScript")),
            new(Ids.Position, StandardComponents.Position(anchor.Position.X, anchor.Position.Y, anchor.Position.Z)),
            new(Ids.ZSyncComponent, CharacterEntities.ZSyncSnapshot(anchor.Position, 0)),
            new(3016, ownership.ToArray()),
            new(Blackboard, board.ToArray()),
            new(Activated, ActiveState(false)),
            new(4024, number.ToArray()),
            new(4025, number.ToArray()),
            new(CustomEvent, []),
            new(4042, new byte[] { 1, 0, 0 }), // Empty NOS string
        ]);
    }

    public static void SetActive(ZenFabricRuntime rt, StoredEntity entity)
    {
        var state = ActiveState(true);
        rt.ReplaceComponentSnapshot(entity, Activated, state, state);
    }

    public static void SendEvent(ZenFabricRuntime rt, StoredEntity entity, string name)
    {
        var update = new SchemaWriter();
        update.AddObject(1001, payload =>
        {
            payload.AddString(1, name);
            payload.AddEntityId(2, 0);
            payload.AddBool(3, false);
            payload.AddInt32(4, 0);
            payload.AddFloat(5, 0);
        });
        var data = update.ToArray();
        var history = entity.Components[CustomEvent].UpdateLog.ToArray();
        rt.ReplaceComponentSnapshot(entity, CustomEvent, [], data);
        entity.Components[CustomEvent].UpdateLog.AddRange(history);
        entity.Components[CustomEvent].UpdateLog.Add(data);
    }

    public static void PlaySpawnEffect(ZenFabricRuntime rt, StoredEntity enemy, (float X, float Y, float Z) position)
    {
        var update = new SchemaWriter();
        update.AddObject(1001, effect =>
        {
            effect.AddObject(1, location =>
            {
                location.AddObject(1, absolute => CharacterEntities.IntAbsolute(absolute, position.X, position.Y, position.Z));
                for (uint field = 2; field <= 9; field++) location.AddEmptyOption(field);
            });
            effect.AddUInt32(2, 13); // BasicPlayerActions effect used at the stock enemy spawn.
            effect.AddString(3, "BasicPlayerActions");
            effect.AddBool(4, true);
            effect.AddString(5, null);
            effect.AddEntityId(6, 0);
            effect.AddFloat(7, 1);
            effect.AddInt64(8, -1);
        });
        rt.ReplaceComponentSnapshot(enemy, CharacterEntities.Vfx, enemy.Components[CharacterEntities.Vfx].Data, update.ToArray());
    }

    public static void SetProgress(ZenFabricRuntime rt, StoredEntity entity, int cleared, int required)
    {
        PublishCounters(rt, entity, [Counter("islandsCleared", cleared), Counter("requiredNumIslands", required)]);
    }

    public static void SetBossRushWave(ZenFabricRuntime rt, StoredEntity entity, int waveIndex, IReadOnlyList<int> counts)
    {
        var counters = new List<SchemaWriter>
        {
            Counter("numberOfWaves", counts.Count),
            Counter("currentWave", waveIndex + 1),
            Counter("spawnCount", counts[waveIndex]),
        };
        for (var i = 0; i < counts.Count; i++) counters.Add(Counter($"boss{i + 1}SpawnCount", counts[i]));
        PublishCounters(rt, entity, counters);
    }

    private static void PublishCounters(ZenFabricRuntime rt, StoredEntity entity, IReadOnlyList<SchemaWriter> counters)
    {
        var writer = new SchemaWriter();
        writer.AddObjectList(1, counters);
        var data = writer.ToArray();
        rt.ReplaceComponentSnapshot(entity, Blackboard, data, data);
    }

    private static SchemaWriter Counter(string name, int value)
    {
        var counter = new SchemaWriter();
        counter.WriteBool(false); // DiffDictionary entry is an upsert.
        counter.AddString(1, name);
        var bytes = new byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        counter.AddBytes(2, bytes);
        return counter;
    }

    private static byte[] ActiveState(bool active)
    {
        var w = new SchemaWriter();
        w.AddBool(1, active);
        return w.ToArray();
    }
}
