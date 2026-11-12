using System.Globalization;
using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;
using ZenithServer.Accounts;

namespace ZenithServer.ZenFabric;

internal sealed class EnemyLoot(StoredEntity entity, StoredLootItem item, (float X, float Y, float Z) position, long expires)
{
    public const uint Component = 2014;
    public const uint Ownership = 2016;
    public const string Prefab = "generic_reimagined_loot_drop";
    public StoredEntity Entity { get; } = entity;
    public StoredLootItem Item { get; } = item;
    public (float X, float Y, float Z) Position { get; private set; } = position;
    public long Expires { get; } = expires;
    public long Hand { get; private set; }
    public long TransformOwner { get; private set; }

    public static EnemyLoot Spawn(ZenFabricRuntime rt, WorkerConnection worker, string characterId, long actor,
        int level, EnemyLootTable.Reward reward, (float X, float Y, float Z) ground, long now)
    {
        var item = new StoredLootItem(Guid.NewGuid().ToString("D"), reward.ItemId, level, reward.Rarity);
        var position = (ground.X, ground.Y + 0.35f, ground.Z);
        var snapshot = new SchemaWriter();
        snapshot.AddObjectList(1, [LootResult(item, characterId)]);
        snapshot.AddStringList(2, [characterId]);
        snapshot.AddBool(3, false);
        snapshot.AddEmptyOption(4);
        var safePoint = new SchemaWriter();
        safePoint.AddFloat(1, ground.X);
        safePoint.AddFloat(2, ground.Y);
        safePoint.AddFloat(3, ground.Z);
        snapshot.AddObjectList(5, [safePoint]);
        var entity = rt.CreateEntity(0,
        [
            new(Ids.Metadata, StandardComponents.Metadata(Prefab)),
            new(Ids.Position, StandardComponents.Position(position.Item1, position.Item2, position.Item3)),
            new(Ids.AuthorityDelegation, TransformAuthority(0)),
            new(3016, [1, 0, 0, 2, 0, 0]), // Server owns the transform until grabbed.
            new(Ids.ZSyncComponent, CharacterEntities.ZSyncSnapshot(position, 0)),
            new(StarterWeapons.Grabbable, GrabState(actor)),
            new(Ownership, []),
            new(Component, snapshot.ToArray()),
        ], worker.WorkerEntityId);
        return new(entity, item, position, now + 120_000);
    }

    public bool CanReach((double X, double Y, double Z) position)
    {
        var dx = position.X - Position.X;
        var dy = position.Y - Position.Y;
        var dz = position.Z - Position.Z;
        return Hand != 0 || dx * dx + dy * dy + dz * dz <= 36;
    }

    public void Grab(ZenFabricRuntime rt, long actor, long worker, long hand)
    {
        Hand = hand;
        rt.ReplaceComponentSnapshot(Entity, StarterWeapons.Grabbable, GrabState(actor, worker, hand),
            GrabState(actor, worker, hand, updateOnly: true));
    }

    public void SetTransformOwner(ZenFabricRuntime rt, long worker, (double X, double Y, double Z) playerPosition, long now)
    {
        if (worker == TransformOwner) return;
        TransformOwner = worker;
        if (worker == 0)
        {
            Position = ((float)playerPosition.X, (float)playerPosition.Y + 0.35f, (float)playerPosition.Z);
            var position = StandardComponents.Position(Position.X, Position.Y, Position.Z);
            rt.ReplaceComponentSnapshot(Entity, Ids.Position, position, position);
        }
        var sync = CharacterEntities.ZSyncSnapshot(Position, worker, timestamp: now / 1000d);
        rt.ReplaceComponentSnapshot(Entity, Ids.ZSyncComponent, sync, sync);
        var authority = TransformAuthority(worker);
        rt.ReplaceComponentSnapshot(Entity, Ids.AuthorityDelegation, authority, authority);
    }

    internal static byte[] TransformAuthority(long worker)
    {
        var server = ZenFabricRuntime.InternalWorkerEntityId;
        var owner = worker == 0 ? server : worker;
        return StandardComponents.AuthorityDelegation(new Dictionary<uint, long>
        {
            [Ids.AuthorityDelegationSet] = server,
            [Ids.DefaultServerSet] = server,
            [Ids.PositionSet] = owner,
            [Ids.ZSyncSet] = owner,
        });
    }

    internal static byte[] GrabState(long actor, long worker = 0, long hand = 0, bool updateOnly = false)
    {
        var writer = new SchemaWriter();
        writer.AddObject(1, grab =>
        {
            grab.AddBool(1, hand != 0);
            CharacterEntities.WriteCombatTarget(grab, 2, hand);
            grab.AddString(3, hand == 0 ? null : worker.ToString(CultureInfo.InvariantCulture));
            CharacterEntities.WriteCombatTarget(grab, 4, hand == 0 ? 0 : actor);
            grab.AddEmptyOption(5);
        });
        if (!updateOnly) writer.AddInt64List(2, actor == 0 ? [] : [actor]);
        return writer.ToArray();
    }

    private static SchemaWriter LootResult(StoredLootItem item, string owner)
    {
        var result = new SchemaWriter();
        result.AddInt32(1, 0); // Quality
        result.AddString(2, item.ItemId);
        result.AddInt32(3, 1);
        result.AddStringList(4, [owner]);
        result.AddEmptyList(5); // Item modifiers
        result.AddInt32(6, item.Level);
        result.AddInt32(7, item.Rarity);
        result.AddInt32(8, item.Level);
        result.AddInt32(9, 0);
        result.AddInt32(10, 0);
        result.AddEmptyList(11); // Stat modifiers
        return result;
    }

}
