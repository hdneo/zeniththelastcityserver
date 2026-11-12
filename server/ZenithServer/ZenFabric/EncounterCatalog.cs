namespace ZenithServer.ZenFabric;

internal static class EncounterCatalog
{
    internal sealed record EnemyKind(string Prefab, string Name, int Health, int Damage, float AttackRange,
        float Leash, float AggroRange = 18, float VerticalReach = 3, float HitRadius = 4, int AttackIntervalMs = 2100,
        EnemyRangedAction.Attack? RangedAttack = null);

    internal sealed record SpawnPoint((float X, float Y, float Z) Position, EnemyKind Kind, uint Rotation,
        (float X, float Y, float Z)? Approach = null);
    internal sealed record Wave(SpawnPoint[] Spawns, int Replacements = 0, int DelayMs = 0, string? StartEvent = null,
        string? EndEvent = null);
    internal sealed record Definition(string Name, (float X, float Y, float Z) Center, float StartRadius,
        Wave[] Waves, EncounterSignals.Anchor? Anchor = null, (float X, float Y, float Z)? Miniboss = null,
        string? ClearEvent = null, bool CountsTowardClear = false, bool BossRushEffects = false,
        (float X, float Y, float Z)? Checkpoint = null, string? NavigationId = null, IslandFinales.Stage? Finale = null);
    internal sealed record ThresholdProgress(EncounterSignals.Anchor Anchor, int RequiredIslands);

    private static readonly EnemyKind Berserker = new("EnemyBase-Reimagined-Desert-Gnasher-Melee", "Gnasher Berserker",
        360, 60, 2.6f, 24);
    private static readonly EnemyKind Gunner = new("EnemyBase-Reimagined-Desert-Gnasher-Gunner", "Gnasher Gunner",
        300, 45, 14, 22, VerticalReach: 6, AttackIntervalMs: 5700, RangedAttack: EnemyRangedAction.Gunner);
    private static readonly EnemyKind Sniper = new("EnemyBase-Reimagined-Desert-Gnasher-Sniper", "Gnasher Sniper",
        300, 65, 22, 8, AggroRange: 27, VerticalReach: 10, AttackIntervalMs: 3400, RangedAttack: EnemyRangedAction.Sniper);
    private static readonly EnemyKind Brute = new("EnemyBase-Reimagined-Desert-Gnasher-Brute", "Gnasher Brute",
        540, 80, 3, 24);
    private static readonly EnemyKind Aqueos = new("EnemyBase-WaterElemental-Boss-Reimagined", "Ancient Aqueos",
        1440, 115, 8, 18, AggroRange: 21, VerticalReach: 5, HitRadius: 6, AttackIntervalMs: 2800);
    private static readonly EnemyKind FireTitan = new("EnemyBase-FireTitan-DungeonBoss-Reimagined", "Fire Titan",
        1800, 135, 8, 22, AggroRange: 23, VerticalReach: 6, HitRadius: 7, AttackIntervalMs: 3000);
    private static readonly EnemyKind NatureElemental = new("EnemyBase-NatureElemental-Reimagined", "Nature Elemental",
        1800, 135, 8, 22, AggroRange: 23, VerticalReach: 6, HitRadius: 7, AttackIntervalMs: 3000);
    private static readonly EnemyKind Boulger = new("EnemyBase-Boulger-Furnace-Boss-Reimagined", "Boulger",
        2520, 160, 9, 22, AggroRange: 24, VerticalReach: 6, HitRadius: 7, AttackIntervalMs: 3000);

    private static readonly EnemyKind[] IslandMinibosses =
    [
        Aqueos,
        Aqueos with { Prefab = "EnemyBase-Banshee-Boss-Reimagined", Name = "Banshee" },
        Aqueos with { Prefab = "EnemyBase-Boulger-Furnace-Miniboss-Reimagined", Name = "Boulger" },
        Aqueos with { Prefab = "EnemyBase-Gargoyle-Veteran-Reliquary-Reimagined", Name = "Gargoyle" },
        Aqueos with { Prefab = "EnemyBase-SharkPirate-Boss-Reimagined", Name = "Shark Pirate" },
    ];

    // Positions and wave counts are from the active island variants in the bundled FlowCanvas graphs.
    private static readonly Definition[] Grove =
    [
        CombatIsland("Wasteland Grove — first combat island", (-258.1214f, -3.57f, -15.4524f), 38,
            [
                At(Berserker, -252.502f, -4.32f, -3.7348f),
                At(Berserker, -274.0133f, -2.83f, -32.7273f),
                At(Gunner, -257.2945f, -3.57f, -18.9869f, 0x6C27FDFF),
            ],
            [
                At(Berserker, -248.6014f, -3.57f, -13.8735f, 0x40D7FDFF, (-253.76f, -3.57f, -13.93f)),
                At(Berserker, -254.0229f, -3.57f, -23.7769f, 0xDFF529FF, (-257.41f, -3.57f, -20.43f)),
                At(Gunner, -278.9184f, -2.83f, -29.9698f),
                At(Gunner, -279.3572f, -2.83f, -30.6249f, approach: (-267.53f, -2.83f, -22.97f)),
            ], new("1527494c-00fa-4005-8594-fe3fe3fa942a", (-258.1214f, -6.57f, -15.4524f)),
            (-255.3112f, -1.94f, 0.6773f), (-251.9111f, -3.57f, -10.6249f)),
        CombatIsland("Wasteland Grove — second combat island", (-259.2027f, 7.72f, 65.3472f), 31,
            [
                At(Gunner, -263.4586f, 6.72f, 70.2724f),
                At(Berserker, -252.4348f, 8.344f, 76.086f),
                At(Berserker, -253.4959f, 6.9806f, 60.1976f),
            ],
            [
                At(Gunner, -273.8517f, 7.9001f, 56.5979f, approach: (-257.85f, 6.72f, 62.47f)),
                At(Gunner, -265.6623f, 7.8517f, 49.8791f, approach: (-264.27f, 7.27f, 53.08f)),
                At(Berserker, -272.1646f, 7.9758f, 54.8213f),
                At(Berserker, -268.6366f, 8.0252f, 51.1838f, approach: (-261.53f, 6.72f, 66.9f)),
            ], new("9c017366-76bf-4500-8b39-489e7adab550", (-259.2027f, 2.72f, 65.3472f)),
            (-252.3261f, 10.46f, 59.1158f), (-248.2996f, 8.9f, 76.3146f)),
        CombatIsland("Wasteland Grove — third combat island", (-385f, 34, 57), 39,
            [
                At(Berserker, -398.897f, 33.2231f, 41.0832f),
                At(Berserker, -372.1364f, 33.283f, 57.3756f),
                At(Gunner, -388.6298f, 33.1408f, 65.9479f),
                At(Sniper, -388.6353f, 38.1339f, 77.192f),
            ],
            [
                At(Gunner, -370.1141f, 33.1184f, 42.3853f, approach: (-376.01f, 33.26f, 42.92f)),
                At(Gunner, -383.6581f, 38.1338f, 81.9341f, approach: (-389.29f, 38.13f, 77f)),
                At(Sniper, -365.4963f, 39.8597f, 38.8866f, approach: (-368.81f, 39.86f, 39.71f)),
                At(Gunner, -388.7702f, 33.1793f, 73.1778f, approach: (-401.15f, 33.26f, 69.31f)),
                At(Berserker, -386.3342f, 33.2703f, 36.7414f, approach: (-397.12f, 33.26f, 55.42f)),
                At(Berserker, -391.5409f, 33.1821f, 73.6381f, approach: (-391.1f, 33.26f, 44.83f)),
            ], new("991fea49-9bf1-4821-a2be-5d93cc8a9a30", (-405.105f, 29.172f, 46.0241f)),
            (-395.8289f, 36.9117f, 65.9843f)),
    ];

    private static readonly Definition[] Telos =
    [
        new("Trials of Telos — Boss Rush", (-12.4f, 11.6f, 444.2f), 22,
        [
            new([At(FireTitan, -12.4f, 11.6f, 461.7f), At(FireTitan, -0.0256f, 11.6f, 456.5744f)], DelayMs: 3000),
            new([At(NatureElemental, -12.4f, 11.6f, 461.7f), At(NatureElemental, -0.0256f, 11.6f, 456.5744f)], DelayMs: 3000),
            new([At(Boulger, -12.4f, 11.6f, 461.7f)], DelayMs: 3000),
        ], new("f7690740-cf97-4553-a6d5-511f65ede2ce", (-12.4f, 21.6f, 444.2f)),
        ClearEvent: "FinishInstance", BossRushEffects: true, Checkpoint: (-12.22f, 14.19f, 423)),
    ];

    public static IReadOnlyList<Definition>? ForArea(WorldEntry.Area area)
    {
        if (area == WorldEntry.Adventure) return Grove;
        if (area == WorldEntry.BossRush) return Telos;
        return area.Daily?.Encounters;
    }

    public static ThresholdProgress? ProgressForArea(WorldEntry.Area area)
    {
        const string threshold = "0ed3779e-0ec4-432b-8bf9-c95849409f43";
        if (area == WorldEntry.Adventure) return new(new(threshold, (-479.6981f, 68.9021f, 11.2769f)), 3);
        return area.Daily?.Threshold;
    }

    public static EnemyKind DailyEnemy(string prefab) => prefab switch
    {
        "EnemyBase-Reimagined-Desert-Gnasher-Melee" => Berserker,
        "EnemyBase-Reimagined-Desert-Gnasher-Gunner" => Gunner,
        "EnemyBase-Reimagined-Desert-Gnasher-Sniper" => Sniper,
        "EnemyBase-Reimagined-Desert-Gnasher-Brute" => Brute,
        "EnemyBase-MeleeExiled-A-Reimagined" => Berserker with { Prefab = prefab, Name = "Exiled Fighter" },
        "EnemyBase-RangedExiled-Reimagined" => Gunner with { Prefab = prefab, Name = "Exiled Gunner" },
        "EnemyBase-ExiledGrenadier-Reimagined" => Gunner with { Prefab = prefab, Name = "Exiled Grenadier" },
        "EnemyBase-Ent-Forest-Reimagined" => Brute with { Prefab = prefab, Name = "Ent" },
        "EnemyBase-Reimagined-SylphidMelee" => Berserker with { Prefab = prefab, Name = "Sylphid Fighter" },
        "EnemyBase-Reimagined-SylphidGunner" => Gunner with { Prefab = prefab, Name = "Sylphid Gunner" },
        "EnemyBase-EssenceWolf-Reimagined" => Berserker with { Prefab = prefab, Name = "Essence Wolf" },
        "EnemyBase-PirateBandit-Reimagined-Melee" => Berserker with { Prefab = prefab, Name = "Pirate Bandit" },
        "EnemyBase-PirateBandit-Reimagined-Gunner" => Gunner with { Prefab = prefab, Name = "Pirate Gunner" },
        "EnemyBase_AvatarOfTelos_Desert_A" => Boulger with { Prefab = prefab, Name = "Avatar of Telos" },
        _ => throw new InvalidDataException($"Unknown Shifting Isles enemy prefab {prefab}"),
    };

    private static Definition CombatIsland(string name, (float X, float Y, float Z) center, float radius,
        SpawnPoint[] prespawns, SpawnPoint[] reinforcements, EncounterSignals.Anchor anchor,
        (float X, float Y, float Z) checkpoint,
        (float X, float Y, float Z)? miniboss = null) =>
        new(name, center, radius, [new(prespawns), new(reinforcements, 10 - reinforcements.Length,
            StartEvent: "TurnOnPortals&Spawns")], anchor, miniboss, ClearEvent: "LootChest_Minor",
            CountsTowardClear: true, Checkpoint: checkpoint);

    public static SpawnPoint PickMiniboss((float X, float Y, float Z) position) =>
        new(position, IslandMinibosses[Random.Shared.Next(IslandMinibosses.Length)], 0xDFF7FDFF);

    public static (float X, float Y, float Z)? CheckpointAt(WorldEntry.Area area, (double X, double Y, double Z) position)
    {
        if (ForArea(area) is not { } definitions) return null;
        foreach (var definition in definitions)
        {
            var dx = position.X - definition.Center.X;
            var dz = position.Z - definition.Center.Z;
            if (dx * dx + dz * dz <= definition.StartRadius * definition.StartRadius &&
                Math.Abs(position.Y - definition.Center.Y) <= 8)
                return definition.Checkpoint;
        }
        return IslandFinales.For(area.Prefab)?.CheckpointAt(position);
    }

    private static SpawnPoint At(EnemyKind kind, float x, float y, float z, uint rotation = 0xDFF7FDFF,
        (float X, float Y, float Z)? approach = null) =>
        new((x, y, z), kind, rotation, approach);
}
