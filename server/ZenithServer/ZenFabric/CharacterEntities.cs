using System.Globalization;
using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;
using ZenithServer.Accounts;

namespace ZenithServer.ZenFabric;

internal static class CharacterEntities
{
    public const int EntityCount = 5;
    internal sealed record SpawnEntity(long Id, ComponentData[] Components);

    private const uint InitialTransformOwnership = 3016;
    internal const uint Combat = 1010;
    private const uint ActorStatusEffects = 1009;
    internal const uint Vfx = 1013;
    internal const uint Inventory = 1015;
    private const uint Equipment = 1016;
    private const uint AnimationDetector = 2015;
    private const uint ClientMovement = 2023;
    private const uint ServerMovement = 2024;
    private const uint ClientRotation = 2025;
    private const uint Qbi = 2026;
    private const uint PlayerVoice = 1040;
    private const uint ClientQuestJournal = 2041;
    private const uint PlayerAggroMap = 3000;
    internal const uint Friend = 3012;
    private const uint PlayerStatus = 3501;
    private const uint Party = 3503;
    private const uint InviteRequestHandler = 3511;
    private const uint VoiceChat = 3705;
    internal const uint PublicActorStats = 4021;
    private const uint LatencyShield = 4040;

    // The owner also gets inventory, account/session information and command-only components.
    internal static readonly IReadOnlySet<uint> PeerComponents = new HashSet<uint>
    {
        Ids.Metadata, Ids.Position, Ids.ZSyncComponent, Ids.OwningWorker, Ids.Interest,
        Ids.ActorInfoComponent, Ids.CharacterCredentialsComponent, Ids.CharacterAppearanceComponent,
        Ids.PlayerCharacterComponent, Ids.PlayerAvatarComponent, Ids.TrackedBodyPartComponent, Ids.TrackedHandComponent,
        Ids.PlayerLifecycleComponent, PublicActorStats, Vfx, InitialTransformOwnership, Combat, Equipment,
        AnimationDetector, ClientMovement, ServerMovement, ClientRotation, Qbi, ActorStatusEffects,
        StarterWeapons.Bow, StarterWeapons.Arrow, StarterWeapons.Item, StarterWeapons.PreWarm, StarterWeapons.Projectile,
        StarterWeapons.Grabbable, StarterWeapons.Ownable,
    };

    // Classless starter equipment, keyed by EquipmentSlot.
    private static readonly (int Slot, string ItemId)[] InfiniteRealmsStarterEquipment =
    [
        (2, "armor_body_reimagined_starter"),
        (3, "armor_feet_reimagined_starter"),
        (4, "armor_hands_reimagined_starter"),
        (5, "armor_legs_reimagined_starter"),
        (StarterWeapons.ShoulderSlot, StarterWeapons.BowItemId),
    ];

    // Initial snapshots that are identical for the characters in the November 4 and 8 captures.
    private static readonly byte[] EmptyClientMovement = Convert.FromHexString(
        "010027000000010012000000010000000000020000000000030000000000020000000000030000000000040000");
    private static readonly byte[] EmptyClientRotation = Convert.FromHexString(
        "010018000000010000000000020000000000030000000000040000000000");
    private static readonly byte[] IdleAnimation = Convert.FromHexString(
        "0100000200000300000400000500000600000700000800000900000A00000000000B00000000000C00000D00000000000E00000F0000100000110000120000000000130000140000150000000000");
    private static readonly byte[] InitialPlayerStatus = Convert.FromHexString(
        "01000000000000000000020012000000010000000000020000030000040000000000030000040000");
    private static readonly byte[] InitialVoiceChat = Convert.FromHexString(
        "01000100000002000003000004000100000000050000");
    private static readonly byte[] InitialLatencyShield = Convert.FromHexString(
        "010000020000000000030012000000010000000000020000000000030000000000040000000000");
    private static readonly byte[] EmptyParty = Convert.FromHexString(
        "01000000000002000000000003000000000004000005000006000000000000000000070000080000");

    public static (float X, float Y, float Z) SpawnPosition(float[] configuredPosition, bool infiniteRealms) =>
        configuredPosition is [var x, var y, var z] && configuredPosition.All(v => float.IsFinite(v) && Math.Abs(v) <= 20_000)
            ? (x, y, z)
            : DefaultSpawnPosition(infiniteRealms);

    public static (float X, float Y, float Z) DefaultSpawnPosition(bool infiniteRealms) =>
        infiniteRealms ? WorldEntry.Hub.Spawn : (-1095.4f, 481.8f, 776.5f);

    public static SpawnEntity[] Build(long firstId, long playerId, long workerId, string accountId,
        StoredCharacter character, uint platform, (float X, float Y, float Z) position, Dictionary<string, string> stats,
        CosmeticWallet wallet)
    {
        var avatarId = firstId + 1;
        var headId = firstId + 2;
        var leftId = firstId + 3;
        var rightId = firstId + 4;
        var positionData = StandardComponents.Position(position.X, position.Y, position.Z);
        KeyValuePair<int, SchemaWriter>[] equipped = character.Type == 4
            ? InfiniteRealmsStarterEquipment.Select(piece => KeyValuePair.Create(piece.Slot,
                StarterItem(character.Id, piece.Slot, piece.ItemId))).ToArray()
            : [];

        ComponentData[] Base(string type, uint clientSet, uint serverSet, bool tracksClient)
        {
            var server = ZenFabricRuntime.InternalWorkerEntityId;
            var transformOwner = tracksClient ? workerId : server;
            var authority = new Dictionary<uint, long>
            {
                [Ids.AuthorityDelegationSet] = server,
                [serverSet] = server,
                [Ids.PositionSet] = transformOwner,
                [Ids.ZSyncSet] = transformOwner,
            };
            if (clientSet != 0) authority[clientSet] = workerId;
            return
            [
                new(Ids.AuthorityDelegation, StandardComponents.AuthorityDelegation(authority)),
                new(Ids.Metadata, StandardComponents.Metadata(type)),
                new(Ids.Position, positionData),
                new(Ids.OwningWorker, StandardComponents.OwningWorker(workerId)),
                Make(InitialTransformOwnership, w =>
                {
                    w.AddBool(1, tracksClient);
                    w.AddBool(2, tracksClient);
                }),
                new(Ids.ZSyncComponent, ZSyncSnapshot(position, tracksClient ? workerId : 0)),
            ];
        }

        SpawnEntity BodyPart(long id, string type, uint part, uint serverSet)
        {
            var isHand = part != 0;
            List<ComponentData> components =
            [
                ..Base(type, isHand ? Ids.PlayerHandClientSet : 0, serverSet, tracksClient: true),
                Make(Ids.TrackedBodyPartComponent, w =>
                {
                    w.AddString(1, IdString(avatarId));
                    w.AddUInt32(2, part, isOption: true);
                }),
            ];
            if (isHand) components.Add(Make(Ids.TrackedHandComponent, w => w.AddEmptyList(1)));
            return new(id, components.ToArray());
        }

        return
        [
            new(firstId,
            [
                ..Base("PlayerCharacter", Ids.PlayerCharacterClientSet, Ids.PlayerCharacterServerSet, tracksClient: false),
                // The stock client waits for Interest and CharacterCredentials before creating this object.
                // The local runtime already controls visibility, so the interest map starts empty.
                Make(Ids.Interest, w => w.AddEmptyList(1)),
                ActorInfo(character.Name),
                Credentials(accountId, workerId, character, platform, wallet.Items),
                Make(Ids.CharacterAppearanceComponent, w =>
                {
                    w.AddMap(1, character.Appearance, (entry, key) => entry.AddUInt32(1, key),
                        (entry, value) => entry.AddString(2, value));
                    w.AddUInt32(2, character.Gender);
                }),
                new(Ids.ActorStatsComponent, ActorStatsSnapshot(character.Type, stats, initialized: false)),
                new(PublicActorStats, PublicStatsSnapshot(character.Role, stats, initialized: false)),
                Make(ActorStatusEffects, w => w.AddEmptyList(1)),
                Make(Combat, w => WriteIdleCombat(w, character.Type == 4 ? StarterWeapons.MaxAmmo : null)),
                new(Party, EmptyParty),
                Make(Vfx, w => w.AddEmptyOption(1)),
                Make(PlayerAggroMap, w => w.AddEmptyList(1)),
                Make(Inventory, w => WriteInventory(w, equipped, accountId, wallet.Items)),
                Make(Equipment, w =>
                {
                    w.AddMap(1, equipped, (entry, slot) => entry.AddInt32(1, slot),
                        (entry, item) => entry.AddObject(2, item));
                    w.AddString(2, "Equipment");
                }),
                new(Friend, []),
                Make(InviteRequestHandler, w => w.AddEmptyList(1)),
                new(CosmeticShop.Wallet, CosmeticShop.WalletState(wallet.Balance)),
                new(Ids.PlayerLifecycleComponent, PlayerLifecycle(3, position)),
                new(Ids.PlayerCharacterComponent, PlayerLinks(firstId, avatarId, playerId)),
                new(Ids.NetworkedObjectInteractionComponent, InteractionHands(0, 0)),
                new(PlayerStatus, InitialPlayerStatus),
                new(VoiceChat, InitialVoiceChat),
                Make(PlayerVoice, w =>
                {
                    w.AddInt32(1, 0);
                    w.AddString(2, null);
                }),
                new(ClientMovement, EmptyClientMovement),
                new(Qbi, []),
                new(ClientQuestJournal, []),
                new(LatencyShield, InitialLatencyShield),
                ..(character.Type == 4 ? new[] { new ComponentData(StarterWeapons.Godstone, StarterWeapons.Godstones()) } : []),
            ]),
            new(avatarId,
            [
                ..Base("PlayerAvatar", Ids.PlayerAvatarClientSet, Ids.PlayerAvatarServerSet, tracksClient: true),
                ActorInfo("PlayerAvatar"),
                new(Ids.PlayerAvatarComponent, AvatarLinks(playerId)),
                new(AnimationDetector, IdleAnimation),
                new(Qbi, []),
                new(ClientMovement, EmptyClientMovement),
                new(ClientRotation, EmptyClientRotation),
                new(Ids.TeleportComponent, WorldEntry.TeleportPosition(default)),
                Make(ServerMovement, w => w.AddObject(1, movement =>
                {
                    movement.AddObject(1, v => IntAbsolute(v, position.X, position.Y, position.Z));
                    movement.AddInt32(2, 0);
                    movement.AddInt32(3, 0);
                    movement.AddEmptyOption(4);
                    movement.AddEmptyOption(5);
                })),
            ]),
            BodyPart(headId, "Head", 0, Ids.PlayerHeadServerSet),
            BodyPart(leftId, "LeftHand", 1, Ids.PlayerHandServerSet),
            BodyPart(rightId, "RightHand", 2, Ids.PlayerHandServerSet),
        ];
    }

    internal static void WriteIdleCombat(SchemaWriter writer, int? ammo)
    {
        // The local stamina system reads the player's combat and PvP state to choose a regen rate.
        writer.AddBool(1, false); // Vulnerable
        writer.AddEmptyOption(2); // Enemy scaling
        writer.AddEmptyOption(3); // Target
        writer.AddEmptyList(4); // Weakpoints
        writer.AddBool(5, false); // Aggroed
        writer.AddEmptyOption(6); // Vulnerability duration
        writer.AddEmptyOption(7); // Vulnerability start
        writer.AddEmptyOption(8); // Highest aggro entity
        writer.AddFloat(9, 1); // Attack speed multiplier
        writer.AddBool(10, false); // PvP
        writer.AddString(11, null); // PvP team
        writer.AddEmptyOption(12); // Weakpoint animation
        WriteCombatAmmo(writer, ammo);
        writer.AddUInt32(14, 0); // Random seed
        writer.AddEmptyOption(15); // Aggro range override
        writer.AddBool(16, false); // Left-hand enchantment
        writer.AddBool(17, false); // Right-hand enchantment
    }

    internal static void WriteCombatAmmo(SchemaWriter writer, int? ammo)
    {
        if (ammo is { } count) writer.AddInt32(13, count, isOption: true);
        else writer.AddEmptyOption(13);
    }

    internal static string StarterItemInstance(string characterId, int slot) =>
        FormattableString.Invariant($"{characterId}-starter-{slot}");

    internal static SchemaWriter StarterItem(string characterId, int slot, string itemId) =>
        Item(StarterItemInstance(characterId, slot), itemId, slot, 1, slot, slot == StarterWeapons.ShoulderSlot ? -1 : 50);

    internal static SchemaWriter Item(string instanceId, string itemId, int slot, int bag, int index, float durability)
    {
        var item = new SchemaWriter();
        item.AddString(1, instanceId);
        item.AddString(2, itemId);
        item.AddInt32(3, 1); // Quantity
        item.AddInt32(4, slot); // Equipment slot
        item.AddInt32(5, 0); // Action slot
        item.AddInt32(6, 0); // Quality
        item.AddInt32(7, bag);
        item.AddInt32(8, index);
        item.AddInt32(10, 0); // Enhancement
        item.AddEmptyOption(11); // Owning entity
        item.AddEntityId(12, 0); // Instance entity
        item.AddEmptyList(13); // Mods
        item.AddInt32(14, 1); // Level
        item.AddInt32(15, 0); // XP
        item.AddInt32(16, 1); // Crafting quality
        item.AddFloat(17, durability);
        item.AddString(18, instanceId); // Temporary instance ID
        item.AddInt32(20, 9); // Starter rarity in Infinite Realms
        item.AddInt32(21, 1); // Item level
        item.AddInt32(22, 0); // Infusion
        item.AddEmptyList(23); // Stat modifiers
        return item;
    }

    private static void WriteInventory(SchemaWriter writer, KeyValuePair<int, SchemaWriter>[] equipped, string accountId,
        IReadOnlyList<string> cosmetics)
    {
        // The client looks up the standard bags even when none contains an item.
        var bags = new SchemaWriter[7];
        for (var type = 0; type < bags.Length; type++)
        {
            var entry = bags[type] = new SchemaWriter();
            entry.WriteByte(0); // Diff dictionary entry is present.
            entry.AddInt32(1, type);
            entry.AddObject(2, bag =>
            {
                bag.AddInt32(1, type == 0 ? 240 : 0); // Capacity
                bag.AddEmptyOption(2); // Name
                bag.AddInt32(3, type);
            });
        }
        writer.AddObjectList(1, bags);
        for (uint field = 2; field <= 4; field++) writer.AddEmptyList(field);
        var equippedEntries = new SchemaWriter[equipped.Length];
        for (var i = 0; i < equipped.Length; i++)
        {
            var entry = equippedEntries[i] = new SchemaWriter();
            entry.WriteByte(0); // Diff dictionary entry is present.
            entry.AddInt32(1, equipped[i].Key);
            entry.AddObject(2, equipped[i].Value);
        }
        writer.AddObjectList(5, equippedEntries);
        for (uint field = 6; field <= 8; field++) writer.AddEmptyList(field);
        CosmeticShop.WriteInventory(writer, accountId, cosmetics);
        writer.AddEmptyList(10);
        writer.AddEmptyList(11);
    }

    public static byte[] AvatarLinks(long playerId, long left = 0, long right = 0, long head = 0, bool partsOnly = false)
    {
        var w = new SchemaWriter();
        if (!partsOnly) w.AddString(1, IdString(playerId));
        w.AddObject(2, body =>
        {
            body.AddEntityId(1, left);
            body.AddEntityId(2, right);
            body.AddEntityId(3, head);
        });
        return w.ToArray();
    }

    public static byte[] InteractionHands(long left, long right, bool linksOnly = false)
    {
        var w = new SchemaWriter();
        if (!linksOnly) w.AddEmptyList(1);
        w.AddEntityId(2, left);
        w.AddEntityId(3, right);
        return w.ToArray();
    }

    public static byte[] PlayerLinks(long characterId, long avatarId, long playerId, bool linksOnly = false)
    {
        var writer = new SchemaWriter();
        writer.AddString(1, IdString(characterId));
        writer.AddString(2, IdString(avatarId));
        writer.AddString(3, IdString(playerId));
        if (!linksOnly)
        {
            writer.AddUInt32(4, 0); // Runtime platform
            writer.AddEmptyList(5); // Experiments
            writer.AddObject(6, activity =>
            {
                activity.AddString(1, null);
                activity.AddInt64(2, 0);
                activity.AddInt64(3, 0);
                activity.AddInt32(4, 0);
            });
        }
        return writer.ToArray();
    }

    private static ComponentData ActorInfo(string name) => Make(Ids.ActorInfoComponent, w =>
    {
        w.AddEntityId(1, 0);
        w.AddString(2, name);
        w.AddUInt32(3, 0); // Player
        w.AddString(4, null);
    });

    private static ComponentData Credentials(string accountId, long workerId, StoredCharacter character, uint platform,
        IReadOnlyList<string> cosmetics) =>
        Make(Ids.CharacterCredentialsComponent, w =>
        {
            w.AddString(1, character.Id);
            w.AddString(2, character.Name);
            w.AddString(3, accountId);
            w.AddUInt32(4, character.Type);
            w.AddEntityId(5, workerId);
            w.AddUInt32(6, platform);
            // Leave service-specific identity fields empty on the private server.
            w.AddString(7, null);
            w.AddUInt64(8, 0);
            for (uint field = 9; field <= 11; field++) w.AddString(field, null);
            w.AddEmptyList(12);
            w.AddEmptyList(13);
            w.AddBool(14, false);
            w.AddString(15, null);
            w.AddInt64(16, 0);
            w.AddStringList(17, cosmetics);
            w.AddEmptyOption(18);
            w.AddInt32(19, 0);
            w.AddEmptyOption(20);
            w.AddEmptyList(21);
            for (uint field = 22; field <= 24; field++) w.AddString(field, null);
            w.AddEmptyList(25); // Campaigns
            w.AddEmptyList(26); // Homes
            w.AddString(27, null);
            w.AddString(28, null);
            w.AddObject(29, rank =>
            {
                rank.AddInt32(1, 0);
                rank.AddInt32(2, 0);
                rank.AddInt32(3, 0);
            });
        });

    private static void WriteStatMap(SchemaWriter writer, uint field, Dictionary<string, string> stats)
    {
        string[] statNames = ["attack", "defense", "support", "speed", "health", "rage", "vulnerability",
            "attack_speed", "casting_speed", "move_speed", "crit_chance", "stamina", "armor", "damage"];
        // The client divides move_speed by 10 to calculate the locomotion multiplier.
        var values = statNames.Select((name, index) => new KeyValuePair<int, float>(index,
            ReadStat(stats, name, name == "move_speed" ? "10" : "0"))).ToArray();
        writer.AddMap(field, values, (entry, key) => entry.AddInt32(1, key), (entry, value) => entry.AddFloat(2, value));
    }

    private static void WriteCurrentResources(SchemaWriter writer, uint characterType, Dictionary<string, string> stats,
        bool initialized)
    {
        writer.AddInt32(10, initialized && characterType == 4 ? (int)ReadStat(stats, "rage") : 0);
        writer.AddInt32(12, 0); // Current essence
    }

    internal static byte[] ActorStatsSnapshot(uint characterType, Dictionary<string, string> stats, bool initialized)
    {
        var writer = new SchemaWriter();
        WriteCurrentResources(writer, characterType, stats, initialized);
        WriteStatMap(writer, 13, stats);
        if (initialized) WriteStatMap(writer, 15, stats);
        else writer.AddEmptyList(15);
        writer.AddEmptyList(16);
        writer.AddUInt32(20, characterType);
        writer.AddInt32(23, 0);
        for (uint field = 27; field <= 30; field++) writer.AddInt32(field, 0);
        writer.AddEmptyOption(31);
        writer.AddEmptyOption(32);
        return writer.ToArray();
    }

    internal static byte[] InitializeActorStats(uint characterType, Dictionary<string, string> stats)
    {
        var writer = new SchemaWriter();
        WriteCurrentResources(writer, characterType, stats, initialized: true);
        WriteStatMap(writer, 15, stats);
        return writer.ToArray();
    }

    internal static byte[] PublicStatsSnapshot(uint role, Dictionary<string, string> stats, bool initialized)
    {
        var w = new SchemaWriter();
        var health = StartingHealth(stats);
        w.AddInt32(7, 1); // Level
        w.AddInt32(8, 0); // XP
        w.AddInt32(9, health);
        w.AddInt32(18, 0);
        w.AddInt32(19, 0);
        w.AddFloat(21, 0);
        w.AddFloat(22, 0);
        w.AddEmptyList(25); // Status effects
        w.AddBool(26, false);
        w.AddFloat(28, 0);
        w.AddInt32(29, initialized ? health : 0);
        w.AddInt32(30, 0);
        w.AddFloat(31, ReadStat(stats, "damage"));
        w.AddFloat(32, ReadStat(stats, "attack"));
        w.AddInt32(33, 1); // Effective level
        w.AddEmptyList(34);
        w.AddBool(35, false);
        w.AddUInt32(36, role);
        w.AddFloat(37, 0);
        for (uint field = 38; field <= 42; field++) w.AddBool(field, false);
        w.AddObject(43, scaling =>
        {
            scaling.AddBool(1, false);
            for (uint field = 2; field <= 5; field++) scaling.AddEmptyOption(field);
        });
        return w.ToArray();
    }

    internal static int StartingHealth(Dictionary<string, string> stats) => (int)ReadStat(stats, "health");

    internal static byte[] ZSyncSnapshot((float X, float Y, float Z) position, long transformOwner, long? attachment = null)
    {
        var w = new SchemaWriter();
        w.AddObject(1, v => Vector(v, position.X, position.Y, position.Z));
        w.AddUInt32(2, 0xDFF7FDFF); // Compressed identity rotation.
        w.AddObject(3, v => Vector(v, 1, 1, 1));
        if (attachment is { } parentId)
            w.AddObject(4, parent =>
            {
                parent.AddEntityId(1, parentId);
                parent.AddString(2, null);
                parent.AddInt32(3, 3); // Captured attachment path for the bow and arrow.
            }, isOption: true);
        else w.AddEmptyOption(4);
        w.AddDouble(5, 0); // Remote timestamp
        w.AddEntityId(6, transformOwner);
        w.AddEmptyOption(7); // Linear motion
        return w.ToArray();
    }

    internal static byte[] PlayerLifecycle(uint state, (float X, float Y, float Z) position)
    {
        var writer = new SchemaWriter();
        writer.AddUInt32(1, state);
        writer.AddObject(2, v => Vector(v, position.X, position.Y, position.Z), isOption: true);
        writer.AddEmptyList(3); // Recoverers
        return writer.ToArray();
    }

    private static float ReadStat(Dictionary<string, string> stats, string name, string fallback = "0") =>
        float.Parse(stats.GetValueOrDefault(name, fallback), CultureInfo.InvariantCulture);

    private static string? IdString(long id) => id == 0 ? null : id.ToString(CultureInfo.InvariantCulture);

    private static void Vector(SchemaWriter writer, float x, float y, float z)
    {
        writer.AddFloat(1, x);
        writer.AddFloat(2, y);
        writer.AddFloat(3, z);
    }

    internal static void IntAbsolute(SchemaWriter writer, float x, float y, float z)
    {
        writer.AddInt32(1, (int)Math.Round(x * 100_000d));
        writer.AddInt32(2, (int)Math.Round(y * 100_000d));
        writer.AddInt32(3, (int)Math.Round(z * 100_000d));
    }

    private static ComponentData Make(uint id, Action<SchemaWriter> write)
    {
        var writer = new SchemaWriter();
        write(writer);
        return new(id, writer.ToArray());
    }
}
