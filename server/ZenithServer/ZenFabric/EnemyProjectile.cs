using System.Numerics;
using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

namespace ZenithServer.ZenFabric;

internal sealed class EnemyProjectile
{
    internal const string GunnerPrefab = "Projectile_SmallEnergyOrb_Gunner";
    internal const string SniperPrefab = "Projectile_SmallEnergyOrb_Sniper";
    private const float Speed = 20;
    private const float HitRadius = 0.85f;
    private const float PlayerCenter = 1.05f;

    public StoredEntity Entity { get; }
    public long Shooter { get; }
    public int Damage { get; }
    private readonly Vector3 _origin;
    private readonly Vector3 _destination;
    private readonly long _firedAt;
    private readonly double _durationMs;
    private Vector3 _previousPosition;

    public EnemyProjectile(ZenFabricRuntime rt, (string Realm, string Area) space, string prefab, long shooter, int damage,
        (float X, float Y, float Z) source, (double X, double Y, double Z) aim, float range, uint rotation, long now)
    {
        Shooter = shooter;
        Damage = damage;
        _firedAt = now;
        var muzzle = new Vector3(source.X, source.Y + 1.25f, source.Z);
        var direction = PlayerPosition(aim) - muzzle;
        direction = direction.LengthSquared() < 0.001f ? Vector3.UnitZ : Vector3.Normalize(direction);
        _origin = _previousPosition = muzzle + direction * 0.4f;
        _destination = _origin + direction * range;
        _durationMs = range / Speed * 1000;
        var start = (_origin.X, _origin.Y, _origin.Z);
        var end = (_destination.X, _destination.Y, _destination.Z);
        var timestamp = now / 1000d;

        Entity = rt.CreateAreaEntity(space,
        [
            new(Ids.Metadata, StandardComponents.Metadata(prefab)),
            new(Ids.Position, StandardComponents.Position(start.X, start.Y, start.Z)),
            new(3016, [1, 0, 0, 2, 0, 0]), // Both transforms are server-owned.
            new(StarterWeapons.Ownable, EmptyOwners()),
            new(StarterWeapons.Projectile, Parameters(shooter, _origin)),
            new(2028, [1, 0, 0, 0, 128, 63]), // Time scale 1.
            new(Ids.ZSyncComponent, CharacterEntities.ZSyncSnapshot(start, 0, rotation: rotation,
                timestamp: timestamp, motion: (end, Speed))),
        ]);
        // The remote transform also subscribes to the linear-motion update.
        var update = new SchemaWriter();
        CharacterEntities.WriteLinearMotion(update, end, timestamp, Speed);
        rt.ReplaceComponentSnapshot(Entity, Ids.ZSyncComponent, Entity.Components[Ids.ZSyncComponent].Data, update.ToArray());
    }

    public bool Advance(long now, IEnumerable<WorkerConnection> workers, Func<WorkerConnection, EnemyEncounters.Player?> getPlayer,
        out EnemyEncounters.Player? hit)
    {
        var travel = Math.Clamp((now - _firedAt) / _durationMs, 0, 1);
        var current = Vector3.Lerp(_origin, _destination, (float)travel);
        var step = current - _previousPosition;
        var stepLength = step.LengthSquared();
        hit = null;
        var first = float.PositiveInfinity;
        if (stepLength > 0)
        {
            foreach (var worker in workers)
            {
                if (getPlayer(worker) is not { } player) continue;
                var center = PlayerPosition(player.Position);
                if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z)) continue;
                var fraction = Math.Clamp(Vector3.Dot(center - _previousPosition, step) / stepLength, 0, 1);
                if (fraction >= first ||
                    Vector3.DistanceSquared(center, _previousPosition + step * fraction) > HitRadius * HitRadius) continue;
                first = fraction;
                hit = player;
            }
        }
        _previousPosition = current;
        return hit is not null || travel >= 1;
    }

    private static Vector3 PlayerPosition((double X, double Y, double Z) position) =>
        new((float)position.X, (float)position.Y + PlayerCenter, (float)position.Z);

    private static byte[] EmptyOwners()
    {
        var w = new SchemaWriter();
        w.AddEmptyList(1);
        return w.ToArray();
    }

    private static byte[] Parameters(long shooter, Vector3 origin)
    {
        var w = new SchemaWriter();
        w.AddObject(1, parameters =>
        {
            parameters.AddString(1, null);
            parameters.AddObject(2, motion =>
            {
                motion.AddObject(1, location => CharacterEntities.IntAbsolute(location, origin.X, origin.Y, origin.Z));
                motion.AddObject(2, offset =>
                {
                    for (uint field = 1; field <= 3; field++) offset.AddFloat(field, 0);
                });
                motion.AddFloat(3, -1);
            });
            parameters.AddEmptyList(3);
            parameters.AddInt32(4, 0);
            parameters.AddBool(5, false);
            parameters.AddString(6, null);
            parameters.AddEntityId(7, shooter);
            parameters.AddEntityId(8, 0);
            parameters.AddBool(9, false);
            parameters.AddBool(10, false);
            parameters.AddString(11, null);
            parameters.AddFloat(12, 1, isOption: true);
            parameters.AddBool(13, true);
            parameters.AddString(14, null);
            parameters.AddBool(15, false);
        });
        w.AddFloat(2, 0);
        w.AddInt32(3, 0);
        return w.ToArray();
    }
}
