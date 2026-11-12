using Zenith.Protocol.Schema;

internal sealed class EncounterScenario(int route, EncounterScenario.Stop[]? dailyStops)
{
    internal sealed record Stop((double X, double Y, double Z) Position, int OpeningCount, int DefeatCount,
        HashSet<string>? EnemyPrefabs = null);

    private static readonly Stop[][] Stops =
    [
        [],
        [new((-232, -3.57, -13.8735), 3, 13), new((-259.2027, 7.72, 65.3472), 3, 13), new((-385, 34, 57), 4, 14)],
        [new((-12.4, 11.6, 444.2), 2, 5)],
    ];

    private readonly Stop[] _stops = route == 0
        ? dailyStops ?? throw new InvalidDataException("No Shifting Isles layout was preloaded")
        : Stops[route];

    internal sealed class Target(string prefab, (double X, double Y, double Z) position)
    {
        public string Prefab { get; } = prefab;
        public (double X, double Y, double Z) Position { get; set; } = position;
        public int Health { get; set; }
        public int Vulnerability { get; set; }
        public int MaxVulnerability { get; set; }
        public bool Staggered { get; set; }
        public bool RangedActionStarted { get; set; }
        public bool Sniper => Prefab.EndsWith("-Sniper", StringComparison.Ordinal);
    }

    public Dictionary<long, Target> Targets { get; } = new();
    public int StopIndex { get; private set; }
    public Stop Current => _stops[StopIndex];
    public int Added { get; private set; }
    public int Defeated { get; private set; }
    public long ClearedAt { get; private set; }
    public bool Completed => StopIndex == _stops.Length;
    public bool GroveFight => route == 1 && StopIndex == 0;
    public bool GroveSniperFight => route == 1 && StopIndex == 2;
    public bool ObservedStaggerRecovery { get; private set; }
    public bool PortalSignal { get; set; }
    public bool SpawnEffect { get; set; }

    public void NextStop()
    {
        StopIndex++;
        Targets.Clear();
        Added = Defeated = 0;
        ClearedAt = 0;
        PortalSignal = SpawnEffect = false;
    }

    public void Add(long id, int health, int vulnerability)
    {
        var target = Targets[id];
        Require(health > 0, "enemy starts alive");
        target.Health = health;
        target.Vulnerability = target.MaxVulnerability = vulnerability;
        var regular = Current.EnemyPrefabs?.Contains(target.Prefab) ?? target.Prefab.Contains("Gnasher", StringComparison.Ordinal);
        if (route != 2 && !regular)
        {
            Targets.Remove(id);
            if (GroveFight && ClearedAt != 0)
                Console.WriteLine($"   [OK] Optional miniboss spawned ({target.Prefab})");
            return;
        }
        if (ClearedAt != 0)
        {
            throw new InvalidDataException("Encounter spawned another regular enemy after the clear");
        }

        if (route == 2)
        {
            var (expected, priorKills) = Added switch
            {
                < 2 => ("EnemyBase-FireTitan-DungeonBoss-Reimagined", 0),
                < 4 => ("EnemyBase-NatureElemental-Reimagined", 2),
                _ => ("EnemyBase-Boulger-Furnace-Boss-Reimagined", 4),
            };
            Require(Added < 5 && target.Prefab == expected && Defeated >= priorKills,
                "Telos advances to the authored bosses after each wave dies");
        }
        else
        {
            Require(Added < Current.OpeningCount ||
                Added < Current.DefeatCount && Defeated >= Current.OpeningCount,
                "reinforcements start only after the opening pack dies");
        }
        Added++;
    }

    public void UpdateHealth(long id, byte[] data)
    {
        var target = Targets[id];
        var reader = new SchemaReader(data);
        var field = reader.ReadFieldId();
        if (field == 18)
        {
            Require(target.Vulnerability == 0 && !target.Staggered && reader.ReadInt32() == target.MaxVulnerability &&
                reader.ReadFieldId() == 21 && reader.ReadFloat() == 2 &&
                reader.ReadFieldId() == 22 && reader.ReadFloat() == 2 && !reader.HasMore,
                "stagger meter resets after the enemy recovers");
            target.Vulnerability = target.MaxVulnerability;
            ObservedStaggerRecovery = true;
            return;
        }
        Require(field == 9, "enemy health field");
        var health = reader.ReadInt32();
        Require(reader.ReadFieldId() == 18, "enemy health changes carry the stagger meter");
        var vulnerability = reader.ReadInt32();
        var expectedVulnerability = target.Staggered ? 0
            : Math.Clamp(target.Vulnerability + health - target.Health, 0, target.MaxVulnerability);
        Require(vulnerability == expectedVulnerability, "enemy stagger meter follows damage and regeneration");
        target.Vulnerability = vulnerability;
        if (health > target.Health)
        {
            Require(target.Health > 0 && reader.ReadFieldId() == 1003, "living enemy regeneration includes the heal event");
            var heal = new SchemaReader(reader.ReadBytesSpan());
            Require(heal.ReadFieldId() == 1 && heal.ReadInt32() == health - target.Health,
                "enemy heal matches the health increase");
            target.Health = health;
            return;
        }
        Require(health >= 0 && health < target.Health && reader.ReadFieldId() == 1001, "hit lowers enemy health");
        reader.ReadBytesSpan();
        Require(reader.ReadFieldId() == 1008, "hit replicates a damage popup");
        reader.ReadBytesSpan();
        if (health == 0)
        {
            Require(reader.ReadFieldId() == 1010, "death replicates an actor event");
            Defeated++;
        }
        target.Health = health;
    }

    public void Clear()
    {
        Require(Defeated == Current.DefeatCount && Added == Defeated, "clear signal after every required enemy dies");
        Require(PortalSignal && SpawnEffect, "portal opens before the enemy spawn effect is played");
        ClearedAt = Environment.TickCount64;
        var routeName = route == 0 ? "Shifting Isles" : "Wasteland Grove";
        Console.WriteLine(route == 2
            ? "   [OK] Telos cleared after waves of two Fire Titans, two Nature Elementals, and Boulger"
            : $"   [OK] {routeName} island {StopIndex + 1} cleared after {Defeated} defeats");
    }

    private static void Require(bool valid, string detail)
    {
        if (!valid) throw new InvalidDataException($"Encounter scenario failed: {detail}");
    }
}
