using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

namespace ZenithServer.ZenFabric;

internal static class StarterWeapons
{
    internal const uint Item = 1087;
    internal const uint Godstone = 2035;
    internal const uint Grabbable = 4001;
    internal const uint Ownable = 4003;
    internal const uint PreWarm = 4022;
    internal const uint Bow = 4031;
    internal const uint Arrow = 4032;
    internal const uint Projectile = 2030;
    internal const int MaxAmmo = 12;
    internal const int AmmoRechargeMs = 7000;
    internal const int ShoulderSlot = 18;
    internal const string BowItemId = "weapon_bow_reimagined_starter";
    internal const string ArrowPrefab = "Projectile-BoStaffArrow-Reimagined-Base";
    internal const string AirChops = "godstone_airChops";

    // Build 56557 wire defaults for the starter bow's ItemDefinition. The item instance below
    // supplies the catalog ID and uses the same generated instance ID as the equipped item.
    private static readonly byte[] BowDefinition = Convert.FromHexString(
        "0100010000000002000003000000000004000005000100000000060000000000070000000000080000000000090000000000" +
        "0a00000000000b00000000000c0001000000000d00000000000e00000000000f0000100000000000110000120000000000" +
        "1300000000001400001500001800000000001900000000001a00000000001b00000000001c00001d00000000001f000000" +
        "000020000000000021000000000022000000000023000100000000");

    // Starter ProjectileParameters preceding field 7 (the attacking entity).
    private static readonly byte[] ProjectileDefaultsBeforeOwner = Convert.FromHexString(
        "010000020036000000010012000000010000000000020000000000030000000000020012000000010000000000020000000000030000000000030000000000030000000000040000000000050000060000");
    private static readonly byte[] ProjectileDefaultsAfterOwner = Convert.FromHexString(
        "080000000000000000000900000a00000b00000c00010000803f0d00000e00000f0000");

    public static byte[] Godstones()
    {
        var w = new SchemaWriter();
        w.AddEmptyList(1); // Etherdisks
        w.AddObject(2, disk =>
        {
            disk.AddString(1, null);
            disk.AddUInt32(2, 0);
            disk.AddUInt32(3, 0);
            disk.AddEmptyList(4);
        });
        WriteCooldowns(w);
        w.AddStringList(4, [AirChops, "godstone_arrow_reimagined_base"]);
        w.AddEmptyList(5); // Skill tree progress
        return w.ToArray();
    }

    public static byte[] CooldownUpdate()
    {
        var writer = new SchemaWriter();
        WriteCooldowns(writer);
        return writer.ToArray();
    }

    private static void WriteCooldowns(SchemaWriter writer) =>
        writer.AddObjectList(3, [Cooldown(1), Cooldown(22)]);

    private static SchemaWriter Cooldown(uint activation)
    {
        var slot = new SchemaWriter();
        slot.AddUInt32(1, activation);
        slot.AddObject(2, data =>
        {
            data.AddInt64(1, 0);
            data.AddInt64(2, 0);
            data.AddInt32(3, 0);
            data.AddInt32(4, 0);
        });
        return slot;
    }

    public static ComponentData[] ShoulderBow(long workerId, long actorId, long avatarId, string characterId,
        (float X, float Y, float Z) position)
    {
        var item = new SchemaWriter();
        item.AddBytes(1, BowDefinition);
        item.AddObject(2, CharacterEntities.StarterItem(characterId, ShoulderSlot, BowItemId));
        item.AddEmptyList(3);
        item.AddBool(10, false);
        item.AddBool(11, true); // Spawned by a player
        item.AddBool(12, false);
        item.AddBool(13, false);
        return
        [
            ..Physical("Item-CN-Bow-Sky1", workerId, actorId, position, default, avatarId),
            new(Item, item.ToArray()),
            new(Bow, BowState(0, 0, MaxAmmo, 0)),
            new(4033, [1, 0, 0, 2, 0, 0]), // Beam and staff states on the stock bow prefab
            new(4034, [1, 0, 0, 0, 0, 0]),
        ];
    }

    public static byte[] BowState(float draw, float knock, int ammo, float recharge)
    {
        var w = new SchemaWriter();
        WriteDraw(w, draw, knock);
        w.AddInt32(4, ammo);
        w.AddBool(5, false);
        w.AddFloat(6, recharge);
        return w.ToArray();
    }

    public static byte[] BowDrawUpdate(float draw, float knock)
    {
        var w = new SchemaWriter();
        WriteDraw(w, draw, knock);
        return w.ToArray();
    }

    private static void WriteDraw(SchemaWriter w, float draw, float knock)
    {
        w.AddFloat(2, draw);
        w.AddFloat(3, knock);
    }

    public static byte[] BowAmmoUpdate(int ammo, float recharge)
    {
        var w = new SchemaWriter();
        w.AddInt32(4, ammo);
        w.AddFloat(6, recharge);
        return w.ToArray();
    }

    public static ComponentData[] AirChopProjectile(long workerId, long actorId, Guid cast,
        (float X, float Y, float Z) position)
    {
        var projectile = new SchemaWriter();
        projectile.AddObject(1, parameters =>
        {
            parameters.WriteRaw(ProjectileDefaultsBeforeOwner);
            parameters.AddEntityId(7, actorId);
            parameters.WriteRaw(ProjectileDefaultsAfterOwner);
        });
        projectile.AddFloat(2, 1);
        projectile.AddInt32(3, 0);
        return
        [
            Prewarm(cast),
            ..Physical("Projectile_AirChops", workerId, actorId, position, position),
            new(Projectile, projectile.ToArray()),
            TimeScale(),
        ];
    }

    public static ComponentData[] BowArrow(long workerId, long actorId, long? bowId, string characterId, Guid cast,
        (float X, float Y, float Z) position)
    {
        var projectile = new SchemaWriter();
        projectile.AddObject(1, parameters =>
        {
            parameters.WriteRaw(ProjectileDefaultsBeforeOwner);
            parameters.AddEntityId(7, actorId);
            parameters.AddEntityId(8, actorId);
            for (uint field = 9; field <= 13; field++) parameters.AddEmptyOption(field);
            parameters.AddString(14, CharacterEntities.StarterItemInstance(characterId, ShoulderSlot));
            parameters.AddEmptyOption(15);
        });
        projectile.AddFloat(2, 0.28f);
        projectile.AddInt32(3, 0);
        return
        [
            Prewarm(cast),
            ..Physical(ArrowPrefab, workerId, actorId, position, position, bowId, grabbable: false),
            new(Arrow, ArrowState(shot: false)),
            new(Projectile, projectile.ToArray()),
            TimeScale(),
        ];
    }

    public static byte[] ArrowState(bool shot, byte[]? stick = null)
    {
        var w = new SchemaWriter();
        if (stick is null)
            w.AddObject(1, data =>
            {
                data.AddInt32(1, 0);
                data.AddEntityId(2, 0);
                data.AddBool(3, false);
                data.AddObject(4, position =>
                {
                    for (uint field = 1; field <= 3; field++) position.AddFloat(field, 0);
                });
                data.AddUInt32(5, 0);
            });
        else w.AddBytes(1, stick);
        w.AddBool(2, shot);
        w.AddInt32(3, 0);
        w.AddInt32(4, 0);
        w.AddBool(5, false);
        return w.ToArray();
    }

    private static ComponentData Prewarm(Guid cast)
    {
        var writer = new SchemaWriter();
        writer.AddString(1, cast.ToString());
        for (uint field = 2; field <= 5; field++) writer.AddEmptyOption(field);
        return new(PreWarm, writer.ToArray());
    }

    private static ComponentData TimeScale()
    {
        var writer = new SchemaWriter();
        writer.AddFloat(1, 1);
        return new(2028, writer.ToArray());
    }

    private static ComponentData[] Physical(string metadata, long workerId, long actorId,
        (float X, float Y, float Z) position, (float X, float Y, float Z) syncPosition,
        long? parent = null, bool grabbable = true)
    {
        var server = ZenFabricRuntime.InternalWorkerEntityId;
        var grabState = new SchemaWriter();
        grabState.AddObject(1, grab =>
        {
            grab.AddBool(1, false);
            grab.AddEmptyOption(2);
            grab.AddString(3, "");
            grab.AddEmptyOption(4);
            grab.AddEmptyOption(5);
        });
        grabState.AddInt64List(2, parent is not null ? [actorId] : []);
        var ownable = new SchemaWriter();
        ownable.AddInt64List(1, [actorId]);
        return
        [
            new(Ids.AuthorityDelegation, StandardComponents.AuthorityDelegation(new Dictionary<uint, long>
            {
                [Ids.AuthorityDelegationSet] = server,
                [Ids.DefaultClientSet] = workerId,
                [Ids.DefaultServerSet] = server,
                [Ids.PositionSet] = workerId,
                [Ids.ZSyncSet] = workerId,
            })),
            new(Ids.Metadata, StandardComponents.Metadata(metadata)),
            new(Ids.Position, StandardComponents.Position(position.X, position.Y, position.Z)),
            new(Ids.ZSyncComponent, CharacterEntities.ZSyncSnapshot(syncPosition, workerId, parent)),
            new(Ids.OwningWorker, StandardComponents.OwningWorker(workerId)),
            new(3016, [1, 0, 1, 2, 0, 1]), // Track the client transform
            ..(grabbable ? new ComponentData[] { new(Grabbable, grabState.ToArray()), new(2016, []) } : []),
            new(Ownable, ownable.ToArray()),
        ];
    }
}
