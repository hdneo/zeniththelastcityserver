namespace ZenithServer.ZenFabric;

internal sealed class EnemyLootTable
{
    internal readonly record struct Reward(string ItemId, int Rarity);
    private readonly record struct Entry(Reward Reward, long CumulativeWeight);

    // Keep the original pool for configurations that predate the loot settings.
    private static readonly Dictionary<string, int> LegacyPool = new(StringComparer.Ordinal)
    {
        ["armor_feet_reimagined_desert"] = 1,
        ["armor_body_reimagined_desert"] = 1,
        ["armor_hands_reimagined_desert"] = 1,
        [StarterWeapons.DesertBowItemId] = 1,
    };

    private readonly Entry[] _regular;
    private readonly Entry[] _boss;

    public EnemyLootTable(EnemyLootOptions options)
    {
        if (options.RegularDropEvery < 0)
            throw new InvalidOperationException("Zenith:EnemyLoot:RegularDropEvery must be zero or greater.");

        _regular = Build(options.RegularItems ?? LegacyPool, nameof(options.RegularItems), 0);
        _boss = Build(options.BossItems ?? LegacyPool, nameof(options.BossItems), 1);
    }

    public Reward? Roll(bool boss)
    {
        var pool = boss ? _boss : _regular;
        if (pool.Length == 0) return null;

        var roll = Random.Shared.NextInt64(pool[^1].CumulativeWeight);
        foreach (var entry in pool)
            if (roll < entry.CumulativeWeight) return entry.Reward;

        throw new InvalidOperationException("Loot roll fell outside the configured pool.");
    }

    private static Entry[] Build(Dictionary<string, int> weights, string pool, int defaultRarity)
    {
        var entries = new List<Entry>(weights.Count);
        long total = 0;
        foreach (var (id, weight) in weights)
        {
            var key = $"Zenith:EnemyLoot:{pool}:{id}";
            if (weight < 0) throw new InvalidOperationException($"{key} must have a non-negative weight.");
            if (weight == 0) continue;

            var item = LootGearCatalog.Find(id) ?? throw new InvalidOperationException(
                $"{key} must use the exact client ID of armor wearable by a classless character or a supported bow.");
            total += weight;
            entries.Add(new(new(id, item.Rarity == 0 ? defaultRarity : item.Rarity), total));
        }
        return entries.ToArray();
    }
}
