using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

namespace ZenithServer.ZenFabric;

internal sealed class FinaleSoulCollection
{
    public const string OrbPrefab = "collectevent_orb-Reimagined-01";
    private readonly Dictionary<long, Orb> _orbs = [];
    private readonly (float X, float Y, float Z) _collector;

    private sealed record Orb(StoredEntity Entity, (float X, float Y, float Z) Spawn)
    {
        public long Holder { get; set; }
        public long Hand { get; set; }
    }

    public int Collected { get; private set; }
    public bool Complete => _orbs.Count == 0;

    public FinaleSoulCollection(ZenFabricRuntime rt, (string Realm, string Area) space,
        IslandFinales.Collection definition, long now)
    {
        _collector = definition.Collector;
        foreach (var position in definition.Orbs)
        {
            var ownable = new SchemaWriter();
            ownable.AddEmptyList(1);
            var item = CharacterEntities.Item("", "", 0, 0, 0, 0, level: 0, rarity: 0);
            var entity = rt.CreateAreaEntity(space,
            [
                new(Ids.Metadata, StandardComponents.Metadata(OrbPrefab)),
                new(Ids.Position, StandardComponents.Position(position.X, position.Y, position.Z)),
                new(Ids.AuthorityDelegation, EnemyLoot.TransformAuthority(0)),
                new(3016, [1, 0, 0, 2, 0, 0]),
                new(Ids.ZSyncComponent, CharacterEntities.ZSyncSnapshot(position, 0, timestamp: now / 1000d)),
                new(StarterWeapons.Grabbable, EnemyLoot.GrabState(0)),
                new(StarterWeapons.Item, StarterWeapons.ItemSnapshot(item, spawnedByPlayer: false, cannotBeClaimed: true)),
                new(StarterWeapons.Ownable, ownable.ToArray()),
                new(EnemyLoot.Ownership, []),
            ]);
            _orbs.Add(entity.Id, new(entity, position));
        }
    }

    public bool Contains(long id) => _orbs.ContainsKey(id);

    public bool IsHeldBy(long id, long worker) => _orbs.TryGetValue(id, out var orb) && orb.Holder == worker;

    public bool TryGrab(ZenFabricRuntime rt, long id, EnemyEncounters.Player player, long hand, long avatar,
        int attachmentPath, long now)
    {
        if (!_orbs.TryGetValue(id, out var orb)) return false;
        if (hand == 0)
        {
            if (orb.Holder != player.WorkerId) return false;
            if (Near(player.Position, _collector, 5)) Deposit(rt, orb);
            else Release(rt, orb, now);
            return true;
        }

        if (orb.Holder == player.WorkerId && orb.Hand == hand) return true;
        if (orb.Holder != 0 || !Near(player.Position, orb.Spawn, 6) ||
            _orbs.Values.Any(other => other.Holder == player.WorkerId && other.Hand == hand)) return false;

        orb.Holder = player.WorkerId;
        orb.Hand = hand;
        var grab = EnemyLoot.GrabState(player.CharacterId, player.WorkerId, hand, updateOnly: true);
        var snapshot = new SchemaWriter();
        snapshot.WriteRaw(grab);
        snapshot.AddEmptyList(2);
        rt.ReplaceComponentSnapshot(orb.Entity, StarterWeapons.Grabbable, snapshot.ToArray(), grab);
        var sync = CharacterEntities.ZSyncSnapshot(default, 0, avatar, timestamp: now / 1000d, attachmentPath: attachmentPath);
        rt.ReplaceComponentSnapshot(orb.Entity, Ids.ZSyncComponent, sync, sync);
        return true;
    }

    public void Tick(ZenFabricRuntime rt, IReadOnlyDictionary<long, EnemyEncounters.Player> players, long now)
    {
        foreach (var orb in _orbs.Values.Where(orb => orb.Holder != 0).ToArray())
        {
            if (!players.TryGetValue(orb.Holder, out var player)) Release(rt, orb, now);
            else if (Near(player.Position, _collector, 3)) Deposit(rt, orb);
        }
    }

    private void Deposit(ZenFabricRuntime rt, Orb orb)
    {
        _orbs.Remove(orb.Entity.Id);
        Collected++;
        rt.DeleteEntity(orb.Entity.Id, null);
    }

    private static void Release(ZenFabricRuntime rt, Orb orb, long now)
    {
        orb.Holder = orb.Hand = 0;
        rt.ReplaceComponentSnapshot(orb.Entity, StarterWeapons.Grabbable, EnemyLoot.GrabState(0),
            EnemyLoot.GrabState(0, updateOnly: true));
        var sync = CharacterEntities.ZSyncSnapshot(orb.Spawn, 0, timestamp: now / 1000d);
        rt.ReplaceComponentSnapshot(orb.Entity, Ids.ZSyncComponent, sync, sync);
    }

    public void Remove(ZenFabricRuntime rt)
    {
        foreach (var id in _orbs.Keys) rt.DeleteEntity(id, null);
        _orbs.Clear();
    }

    private static bool Near((double X, double Y, double Z) player, (float X, float Y, float Z) point, double radius)
    {
        var dx = player.X - point.X;
        var dy = player.Y - point.Y;
        var dz = player.Z - point.Z;
        return dx * dx + dy * dy + dz * dz <= radius * radius;
    }
}
