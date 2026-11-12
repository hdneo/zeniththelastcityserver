using Zenith.Protocol.Schema;

namespace ZenithServer.ZenFabric;

internal static class EnemyRangedAction
{
    internal sealed record Attack(int Index, string ProjectilePrefab, float FirstStageSeconds, float SecondStageSeconds,
        int WindupMs, int ShotCount, int ShotIntervalMs, byte[] InterruptionInfo);

    // Rapid Shot's two stages and trigger configuration from the Gunner action graph.
    private static readonly byte[] RapidShotInterruptionInfo = Convert.FromHexString(
        "010002000000250000000100000200010000000300000400010000000500010000000900000000003333B33E" +
        "01000018000000010000020001000000030000040001000000050000000000" +
        "0200000000000300000400010A00000052617069642053686F74050000");

    public static readonly Attack Gunner = new(0, EnemyProjectile.GunnerPrefab, 2.3333335f, 3.3333335f,
        1200, 3, 150, RapidShotInterruptionInfo);

    // The bundled Sharpshooter graph puts its non-cover shot second, with separate aim and fire clips.
    public static readonly Attack Sniper = new(1, EnemyProjectile.SniperPrefab, 1, 2.3333335f,
        1350, 1, 0, SniperInterruptionInfo());

    public static byte[] Start(Attack attack, (float X, float Y, float Z) position,
        (double X, double Y, double Z) aim, long target, long now)
    {
        var yaw = Math.Atan2(aim.X - position.X, aim.Z - position.Z) * 180 / Math.PI;
        var update = new SchemaWriter();
        update.AddObject(1007, action =>
        {
            action.AddInt32(1, attack.Index);
            action.AddObject(2, location =>
            {
                location.AddFloat(1, position.X);
                location.AddFloat(2, position.Y);
                location.AddFloat(3, position.Z);
            });
            action.AddFloat(3, (float)((yaw + 360) % 360));
            action.AddEntityId(4, target, isOption: true);
            action.WriteFieldId(5);
            action.WriteInt32(2);
            action.WriteFloat(attack.FirstStageSeconds);
            action.WriteFloat(attack.SecondStageSeconds);
            action.AddBytes(6, attack.InterruptionInfo);
            action.AddBool(7, false);
            action.AddInt64(8, now);
        });
        return update.ToArray();
    }

    public static byte[] ProjectileSpawned(long projectile)
    {
        var update = new SchemaWriter();
        update.AddObject(1015, spawned =>
        {
            spawned.AddEntityId(1, projectile);
            spawned.AddInt32(2, 0);
            spawned.AddInt32(3, 0);
        });
        return update.ToArray();
    }

    private static byte[] SniperInterruptionInfo()
    {
        var stage = new SchemaWriter();
        stage.AddBool(1, false);
        stage.AddInt32(2, 0); // No casting bar override.
        stage.AddBool(3, false);
        stage.AddInt32(4, 1);
        stage.AddEmptyList(5);
        var info = new SchemaWriter();
        info.AddObjectList(1, [stage, stage]);
        info.AddEmptyList(2);
        info.AddBool(3, false);
        info.AddString(4, "Sniper Shot Non-Cover");
        info.AddBool(5, false);
        return info.ToArray();
    }
}
