using System.Globalization;
using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;
using static ZenithServer.ZenFabric.EncounterCatalog;

namespace ZenithServer.ZenFabric;

internal sealed class EnemyEncounters(double minibossChance, int regularDropEvery, ILogger log)
{
    private const uint Animation = 1002;
    private const uint Lootable = 2011;
    private const int UpdateIntervalMs = 100;
    private const int StaggerSeconds = 2;
    private const float MaxStaggerSeconds = 4;

    private sealed class Encounter(Definition definition, EncounterNavigation? navigation)
    {
        public Definition Definition { get; } = definition;
        public EncounterNavigation? Navigation { get; } = navigation;
        public PriorityQueue<SpawnPoint, long> Replacements { get; } = new();
        public int WaveIndex { get; set; } = -1;
        public long StartWaveAt { get; set; }
        public long EnableBossPortalsAt { get; set; }
        public int Alive { get; set; }
        public int RemainingReplacements { get; set; }
        public long SpawnMinibossAt { get; set; }
        public StoredEntity? SignalEntity { get; set; }
        public IslandPublicEvent? PublicEvent { get; set; }
        public FinaleSoulCollection? Souls { get; set; }
        public int PublishedSouls { get; set; }
        public bool Alerted { get; set; }
        public long NextHealAt { get; set; }
        public int Defeated { get; set; }
    }

    private sealed class AreaRun(IReadOnlyList<Definition> definitions, IslandFinales.Route? finale,
        IReadOnlyDictionary<string, EncounterNavigation> navigation)
    {
        public Encounter[] Encounters { get; } = definitions.Concat(finale?.Encounters ?? []).Select(definition =>
            new Encounter(definition, navigation.GetValueOrDefault(definition.NavigationId ?? definition.Anchor?.Id ?? ""))).ToArray();
        public IslandFinales.Route? Finale { get; } = finale;
        public int FinaleCleared { get; set; }
        public long NextFinaleAt { get; set; }
        public Dictionary<string, StoredEntity> Signals { get; } = new(StringComparer.Ordinal);
        public List<Enemy> Enemies { get; } = new();
        public List<EnemyProjectile> Shots { get; } = new();
        public int IslandsCleared { get; set; }
        public (StoredEntity Entity, int RequiredIslands)? Threshold { get; set; }
    }

    private sealed class Enemy(SpawnPoint spawn, Encounter encounter, bool countsForWave, long now)
    {
        public SpawnPoint Spawn { get; } = spawn;
        public Encounter Encounter { get; } = encounter;
        public bool CountsForWave { get; } = countsForWave;
        public StoredEntity? Entity { get; set; }
        public (float X, float Y, float Z) Position { get; set; } = spawn.Position;
        public int? NavigationCell { get; set; }
        public int? RouteGoal { get; set; }
        public Queue<int> Route { get; set; } = [];
        public long PlanRouteAt { get; set; }
        public int Health { get; set; } = spawn.Kind.Health;
        public int MaxVulnerability { get; } = spawn.Kind.Health * 3 / 4;
        public int Vulnerability { get; set; } = spawn.Kind.Health * 3 / 4;
        public long? StaggerStartedAt { get; set; }
        public HashSet<long> Contributors { get; } = [];
        public long Target { get; set; }
        public long LastAttacker { get; set; }
        public long PreferAttackerUntil { get; set; }
        public bool Approaching { get; set; } = spawn.Approach is not null;
        public long NextAttack { get; set; }
        public long NextBurstShot { get; set; }
        public int BurstShotsRemaining { get; set; }
        public long RemoveAt { get; set; }
        public long SpawnEffectAt { get; set; }
        public long MoveAfterSpawnAt { get; set; }
        public float Speed { get; set; }
        public uint Rotation { get; set; } = spawn.Rotation;
        public double TransformTimestamp { get; set; } = now / 1000d;
    }

    internal readonly record struct Player(long WorkerId, long CharacterId, (double X, double Y, double Z) Position);
    internal sealed record Defeat(long EnemyId, (float X, float Y, float Z) Position, int Experience, bool DropsLoot,
        bool Boss, HashSet<long> Contributors);

    private readonly Dictionary<(string Realm, string Area), AreaRun> _areas = new();
    private readonly Dictionary<long, Enemy> _byId = new();
    private readonly IReadOnlyDictionary<string, EncounterNavigation> _navigation = EncounterNavigation.Load();
    private long _nextUpdate;

    public void Enter(ZenFabricRuntime rt, WorkerConnection worker, WorldEntry.Area? area)
    {
        if (worker.SharedSpace is not { } space || _areas.ContainsKey(space)) return;
        if (area is null || ForArea(area) is not { } definitions) return;
        var run = new AreaRun(definitions, IslandFinales.For(area.Prefab), _navigation);
        _areas.Add(space, run);
        if (ProgressForArea(area) is { } progress)
        {
            var threshold = EncounterSignals.Create(rt, space, progress.Anchor);
            run.Signals.Add(progress.Anchor.Id, threshold);
            run.Threshold = (threshold, progress.RequiredIslands);
            EncounterSignals.SetProgress(rt, threshold, 0, progress.RequiredIslands);
        }
        foreach (var encounter in run.Encounters)
        {
            if (encounter.Definition.Anchor is not { } anchor) continue;
            if (!run.Signals.TryGetValue(anchor.Id, out var signal))
            {
                signal = EncounterSignals.Create(rt, space, anchor);
                run.Signals.Add(anchor.Id, signal);
            }
            encounter.SignalEntity = signal;
            if (encounter.Definition.BossRushEffects)
                EncounterSignals.SetBossRushWave(rt, encounter.SignalEntity, 0,
                    encounter.Definition.Waves.Select(wave => wave.Spawns.Length).ToArray());
        }
    }

    public bool Hit(ZenFabricRuntime rt, WorkerConnection worker, long target, long attacker, int damage,
        (double X, double Y, double Z) impact, (double X, double Y, double Z) origin, double range, long now,
        out Defeat? defeat)
    {
        defeat = null;
        if (worker.SharedSpace is not { } space || !_byId.TryGetValue(target, out var enemy) ||
            enemy.Entity is not { } entity || entity.SharedSpace != space || enemy.Health <= 0 ||
            DistanceSquared(impact, enemy.Position) > enemy.Spawn.Kind.HitRadius * enemy.Spawn.Kind.HitRadius ||
            DistanceSquared(origin, enemy.Position) > range * range)
            return false;

        var dealt = Math.Min(damage, enemy.Health);
        enemy.Encounter.Alerted = true;
        enemy.LastAttacker = attacker;
        enemy.Contributors.Add(attacker);
        enemy.PreferAttackerUntil = now + 6000;
        enemy.Health -= dealt;
        if (enemy.StaggerStartedAt is null)
        {
            enemy.Vulnerability = Math.Max(0, enemy.Vulnerability - dealt);
            if (enemy.Vulnerability == 0 && enemy.Health > 0)
            {
                enemy.BurstShotsRemaining = 0;
                SetSpeed(rt, enemy, 0);
                SetStagger(rt, enemy, now);
            }
        }
        var update = HealthUpdate(enemy.Health, dealt, attacker, target, enemy.Vulnerability);
        rt.ReplaceComponentSnapshot(entity, CharacterEntities.PublicActorStats,
            Stats(enemy), update.ToArray());
        if (enemy.Health == 0)
        {
            UpdateTarget(rt, enemy, 0, now);
            SetSpeed(rt, enemy, 0);
            enemy.RemoveAt = now + 2000;
            var boss = !enemy.CountsForWave || enemy.Encounter.Definition.BossRushEffects ||
                enemy.Encounter.Definition.Finale?.Boss == true;
            var dropsLoot = boss || (regularDropEvery > 0 && enemy.Encounter.Defeated % regularDropEvery == 0);
            enemy.Encounter.Defeated++;
            enemy.Encounter.PublicEvent?.Publish(rt, enemy.Encounter.Defeated);
            defeat = new(entity.Id, enemy.Position, enemy.Spawn.Kind.Health * 2, dropsLoot, boss, enemy.Contributors);
            if (enemy.CountsForWave) EnemyDefeated(enemy.Encounter, enemy.Spawn, now);
        }
        return true;
    }

    public void Tick(ZenFabricRuntime rt, long now, Func<WorkerConnection, Player?> getPlayer,
        Action<Player, long, int> damagePlayer, Action<WorkerConnection, long[]> publishAggro)
    {
        if (_areas.Count == 0 || now < _nextUpdate) return;
        _nextUpdate = now + UpdateIntervalMs;
        var workersByArea = rt.ReadyWorkers.Where(w => w.SharedSpace is not null).ToLookup(w => w.SharedSpace!.Value);
        foreach (var (space, run) in _areas.ToArray())
        {
            var workers = workersByArea[space];
            if (!workers.Any())
            {
                foreach (var enemy in run.Enemies) Remove(rt, enemy);
                foreach (var shot in run.Shots) rt.DeleteEntity(shot.Entity.Id, null);
                foreach (var signal in run.Signals.Values) rt.DeleteEntity(signal.Id, null);
                foreach (var encounter in run.Encounters)
                {
                    if (encounter.PublicEvent is { } active) rt.DeleteEntity(active.Entity.Id, null);
                    encounter.Souls?.Remove(rt);
                }
                _areas.Remove(space);
                continue;
            }

            foreach (var encounter in run.Encounters)
                AdvanceEncounter(rt, space, run, encounter, workers, getPlayer, now);

            for (var i = run.Shots.Count - 1; i >= 0; i--)
            {
                var shot = run.Shots[i];
                if (!shot.Advance(now, workers, getPlayer, out var hit)) continue;
                if (hit is { } victim) damagePlayer(victim, shot.Shooter, shot.Damage);
                rt.DeleteEntity(shot.Entity.Id, null);
                run.Shots.RemoveAt(i);
            }

            for (var i = run.Enemies.Count - 1; i >= 0; i--)
            {
                var enemy = run.Enemies[i];
                if (enemy.Health == 0)
                {
                    if (now >= enemy.RemoveAt)
                    {
                        Remove(rt, enemy);
                        run.Enemies.RemoveAt(i);
                    }
                    continue;
                }
                // Re-read eligible players between enemies because an attack can down a player.
                var target = ChooseTarget(enemy, workers, getPlayer, now);
                TickEnemy(rt, space, run, enemy, target, now, damagePlayer);
            }

            foreach (var encounter in run.Encounters)
                RegenerateEnemies(rt, run, encounter, workers, getPlayer, now);

            var targeting = run.Enemies.Where(enemy => enemy.Health > 0 && enemy.Target != 0 && enemy.Entity is not null)
                .ToLookup(enemy => enemy.Target, enemy => enemy.Entity!.Id);
            foreach (var worker in workers)
            {
                var aggro = getPlayer(worker) is { } player ? targeting[player.CharacterId].Order().ToArray() : [];
                publishAggro(worker, aggro);
            }
        }
    }

    public bool IsFinaleExit(WorkerConnection worker, (double X, double Y, double Z) position) =>
        worker.SharedSpace is { } space && _areas.TryGetValue(space, out var run) && run.Finale is { } route &&
        run.FinaleCleared == route.Encounters.Length && DistanceSquared(position, route.Exit) <= 25;

    public FinaleSoulCollection? FindSouls(WorkerConnection worker, long id) =>
        worker.SharedSpace is { } space && _areas.TryGetValue(space, out var run)
            ? run.Encounters.Select(encounter => encounter.Souls).FirstOrDefault(souls => souls?.Contains(id) == true)
            : null;

    private static Player? ChooseTarget(Enemy enemy, IEnumerable<WorkerConnection> workers,
        Func<WorkerConnection, Player?> getPlayer, long now)
    {
        var kind = enemy.Spawn.Kind;
        var encounter = enemy.Encounter;
        Player? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var worker in workers)
        {
            if (getPlayer(worker) is not { } player || !IsInArena(encounter.Definition, player.Position) ||
                Math.Abs(player.Position.Y - enemy.Position.Y) > Math.Max(kind.VerticalReach, 7)) continue;
            var distance = DistanceSquared(player.Position, enemy.Position);
            var nearEnemy = distance <= kind.AggroRange * kind.AggroRange;
            if (!nearEnemy && !encounter.Alerted) continue;

            var score = player.CharacterId == enemy.Target ? distance * 0.8 : distance;
            if (player.CharacterId == enemy.LastAttacker && now < enemy.PreferAttackerUntil) score = -1;
            if (score >= bestScore) continue;
            best = player;
            bestScore = score;
        }
        return best;
    }

    private static bool IsInArena(Definition arena, (double X, double Y, double Z) position)
    {
        var radius = arena.StartRadius + 5;
        return Math.Abs(position.Y - arena.Center.Y) <= 24 &&
            HorizontalDistanceSquared(position, arena.Center) <= radius * radius;
    }

    private static void RegenerateEnemies(ZenFabricRuntime rt, AreaRun run, Encounter encounter,
        IEnumerable<WorkerConnection> workers, Func<WorkerConnection, Player?> getPlayer, long now)
    {
        if (!encounter.Alerted || workers.Any(worker => getPlayer(worker) is { } player && IsInArena(encounter.Definition, player.Position)))
        {
            encounter.NextHealAt = 0;
            return;
        }

        if (encounter.NextHealAt == 0) encounter.NextHealAt = now + 1500;
        if (now < encounter.NextHealAt) return;
        encounter.NextHealAt = now + 1000;
        foreach (var enemy in run.Enemies)
        {
            var maximum = enemy.Spawn.Kind.Health;
            if (enemy.Encounter != encounter || enemy.Entity is not { } entity || enemy.Health <= 0 || enemy.Health >= maximum)
                continue;

            var healed = Math.Min(maximum - enemy.Health, (maximum + 19) / 20);
            enemy.Health += healed;
            if (enemy.Health == maximum) enemy.Contributors.Clear();
            if (enemy.StaggerStartedAt is null)
                enemy.Vulnerability = Math.Min(enemy.MaxVulnerability, enemy.Vulnerability + healed);
            var update = HealthUpdate(enemy.Health, vulnerability: enemy.Vulnerability);
            update.AddObject(1003, heal =>
            {
                var id = entity.Id.ToString(CultureInfo.InvariantCulture);
                heal.AddInt32(1, healed);
                heal.AddBool(2, false);
                heal.AddString(3, id);
                heal.AddString(4, id);
                heal.AddBool(5, false);
            });
            rt.ReplaceComponentSnapshot(entity, CharacterEntities.PublicActorStats, Stats(enemy), update.ToArray());
        }
    }

    private static void TickEnemy(ZenFabricRuntime rt, (string Realm, string Area) space, AreaRun run, Enemy enemy,
        Player? target, long now, Action<Player, long, int> damagePlayer)
    {
        var kind = enemy.Spawn.Kind;
        if (enemy.SpawnEffectAt != 0 && now >= enemy.SpawnEffectAt)
        {
            EncounterSignals.PlaySpawnEffect(rt, enemy.Entity!, enemy.Spawn.Position);
            enemy.SpawnEffectAt = 0;
        }
        if (now < enemy.MoveAfterSpawnAt) return;
        if (target is not null) enemy.Encounter.Alerted = true;
        UpdateTarget(rt, enemy, target?.CharacterId ?? 0, now);
        if (enemy.StaggerStartedAt is { } stagger)
        {
            if (now - stagger < StaggerSeconds * 1000) return;
            SetStagger(rt, enemy, null);
            enemy.Vulnerability = enemy.MaxVulnerability;
            var update = new SchemaWriter();
            update.AddInt32(18, enemy.Vulnerability);
            update.AddFloat(21, StaggerSeconds);
            update.AddFloat(22, StaggerSeconds);
            rt.ReplaceComponentSnapshot(enemy.Entity!, CharacterEntities.PublicActorStats, Stats(enemy), update.ToArray());
            enemy.NextAttack = Math.Max(enemy.NextAttack, now + 900);
        }
        if (enemy.Approaching && enemy.Spawn.Approach is { } approach)
        {
            enemy.Approaching = !Chase(rt, enemy, approach, now, followApproach: true);
            return;
        }

        if (target is { } victim && Math.Abs(victim.Position.Y - enemy.Position.Y) <= kind.VerticalReach &&
            DistanceSquared(victim.Position, enemy.Position) <= kind.AttackRange * kind.AttackRange &&
            (kind.RangedAttack is not null || CanReachForMelee(enemy, victim.Position)))
        {
            Move(rt, enemy, enemy.Position, victim.Position, 0);
            if (kind.RangedAttack is { } attack)
            {
                TickRangedAttack(rt, space, run, enemy, attack, victim, now);
                return;
            }
            if (now < enemy.NextAttack) return;
            enemy.NextAttack = now + kind.AttackIntervalMs;
            damagePlayer(victim, enemy.Entity!.Id, kind.Damage);
            return;
        }
        enemy.BurstShotsRemaining = 0;

        var home = enemy.Spawn.Approach ?? enemy.Spawn.Position;
        var destination = target is { } tracked
            ? tracked.Position
            : ((double)home.X, home.Y, (double)home.Z);
        Chase(rt, enemy, destination, now, followApproach: target is null);
    }

    private static void TickRangedAttack(ZenFabricRuntime rt, (string Realm, string Area) space, AreaRun run,
        Enemy enemy, EnemyRangedAction.Attack attack, Player victim, long now)
    {
        var kind = enemy.Spawn.Kind;
        var entity = enemy.Entity!;
        if (enemy.BurstShotsRemaining == 0)
        {
            if (now < enemy.NextAttack) return;
            enemy.NextAttack = now + kind.AttackIntervalMs;
            enemy.NextBurstShot = now + attack.WindupMs;
            enemy.BurstShotsRemaining = attack.ShotCount;
            var update = EnemyRangedAction.Start(attack, enemy.Position, victim.Position, victim.CharacterId, now);
            rt.ReplaceComponentSnapshot(entity, CharacterEntities.Combat, entity.Components[CharacterEntities.Combat].Data, update);
            return;
        }
        if (now < enemy.NextBurstShot) return;

        var shot = new EnemyProjectile(rt, space, attack.ProjectilePrefab, entity.Id, kind.Damage / attack.ShotCount,
            enemy.Position, victim.Position, kind.AttackRange + 4, enemy.Rotation, now);
        run.Shots.Add(shot);
        enemy.BurstShotsRemaining--;
        enemy.NextBurstShot += attack.ShotIntervalMs;
        rt.ReplaceComponentSnapshot(entity, CharacterEntities.Combat, entity.Components[CharacterEntities.Combat].Data,
            EnemyRangedAction.ProjectileSpawned(shot.Entity.Id));
    }

    private void AdvanceEncounter(ZenFabricRuntime rt, (string Realm, string Area) space, AreaRun run,
        Encounter encounter, IEnumerable<WorkerConnection> workers, Func<WorkerConnection, Player?> getPlayer, long now)
    {
        var definition = encounter.Definition;
        if (encounter.WaveIndex < 0)
        {
            var finale = definition.Finale;
            if (finale is not null && (run.FinaleCleared != finale.Order || now < run.NextFinaleAt ||
                run.Threshold is { } threshold && run.IslandsCleared < threshold.RequiredIslands)) return;
            var trigger = finale?.Trigger ?? definition.Center;
            var radius = finale?.TriggerRadius ?? definition.StartRadius;
            if (!workers.Any(worker => getPlayer(worker) is { } player &&
                DistanceSquared(player.Position, trigger) <= radius * radius)) return;
            encounter.WaveIndex = 0;
            if (finale is not null)
            {
                encounter.PublicEvent = new IslandPublicEvent(rt, space, definition, finale, now);
                if (encounter.SignalEntity is { } signal)
                    foreach (var message in finale.StartSignals) EncounterSignals.SendEvent(rt, signal, message);
                if (finale.Collect is { } collection)
                    encounter.Souls = new FinaleSoulCollection(rt, space, collection, now);
            }
            if (encounter.Souls is null) ScheduleWave(rt, encounter, now);
            log.LogInformation("Encounter started in {Realm}: {Encounter}", space.Realm, definition.Name);
        }

        if (encounter.Souls is { } souls)
        {
            souls.Tick(rt, workers.Select(getPlayer).OfType<Player>().ToDictionary(player => player.WorkerId), now);
            if (souls.Complete)
            {
                encounter.Souls = null;
                CompleteEncounter(rt, space, run, encounter, now, souls.Collected);
            }
            else if (souls.Collected != encounter.PublishedSouls)
            {
                encounter.PublishedSouls = souls.Collected;
                encounter.PublicEvent?.Publish(rt, souls.Collected);
            }
            return;
        }

        if (encounter.WaveIndex == definition.Waves.Length)
        {
            if (encounter.PublicEvent is { RemoveAt: > 0 } completed && now >= completed.RemoveAt)
            {
                rt.DeleteEntity(completed.Entity.Id, null);
                encounter.PublicEvent = null;
            }
            if (encounter.SpawnMinibossAt != 0 && now >= encounter.SpawnMinibossAt && definition.Miniboss is { } miniboss)
            {
                encounter.SpawnMinibossAt = 0;
                Spawn(rt, space, run, PickMiniboss(miniboss), encounter, now, portal: true, countsForWave: false);
            }
            return;
        }

        var wave = definition.Waves[encounter.WaveIndex];
        if (encounter.StartWaveAt != 0)
        {
            if (encounter.EnableBossPortalsAt != 0 && now >= encounter.EnableBossPortalsAt)
            {
                encounter.EnableBossPortalsAt = 0;
                if (encounter.SignalEntity is { } signal) EncounterSignals.SendEvent(rt, signal, "SpawnEffectsOnOff");
            }
            if (now < encounter.StartWaveAt) return;
            encounter.StartWaveAt = 0;
            encounter.Alive = wave.Spawns.Length;
            encounter.RemainingReplacements = wave.Replacements;
            if (definition.BossRushEffects && encounter.SignalEntity is { } bossRush)
                EncounterSignals.SendEvent(rt, bossRush, "SpawnEffectsSpawn");
            foreach (var spawn in wave.Spawns)
                Spawn(rt, space, run, spawn, encounter, now,
                    portal: wave.StartEvent is not null || definition.BossRushEffects || definition.Finale is not null);
            return;
        }

        while (encounter.Replacements.TryPeek(out var replacement, out var readyAt) && now >= readyAt)
        {
            encounter.Replacements.Dequeue();
            encounter.Alive++;
            Spawn(rt, space, run, replacement, encounter, now, portal: true);
        }

        if (encounter.Alive != 0 || encounter.Replacements.Count != 0) return;
        if (wave.EndEvent is { } endEvent && encounter.SignalEntity is { } waveSignal)
            EncounterSignals.SendEvent(rt, waveSignal, endEvent);
        encounter.WaveIndex++;
        if (encounter.WaveIndex < definition.Waves.Length)
        {
            ScheduleWave(rt, encounter, now);
            return;
        }

        CompleteEncounter(rt, space, run, encounter, now, encounter.Defeated);
    }

    private void CompleteEncounter(ZenFabricRuntime rt, (string Realm, string Area) space, AreaRun run,
        Encounter encounter, long now, int progress)
    {
        var definition = encounter.Definition;
        log.LogInformation("Encounter cleared in {Realm}: {Encounter}", space.Realm, definition.Name);
        encounter.PublicEvent?.Publish(rt, progress, complete: true);
        if (encounter.SignalEntity is { } cleared)
        {
            if (definition.ClearEvent is { } message) EncounterSignals.SendEvent(rt, cleared, message);
            if (definition.Finale is null || definition.Finale.Boss) EncounterSignals.SetActive(rt, cleared);
        }
        if (definition.Finale is not null)
        {
            run.FinaleCleared++;
            run.NextFinaleAt = encounter.PublicEvent?.RemoveAt ?? now;
        }
        if (definition.CountsTowardClear && run.Threshold is { } threshold)
        {
            run.IslandsCleared++;
            EncounterSignals.SetProgress(rt, threshold.Entity, run.IslandsCleared, threshold.RequiredIslands);
        }
        if (definition.Miniboss is not null && Random.Shared.NextDouble() < minibossChance)
            encounter.SpawnMinibossAt = now + 3000;
    }

    private static void ScheduleWave(ZenFabricRuntime rt, Encounter encounter, long now)
    {
        var definition = encounter.Definition;
        var wave = definition.Waves[encounter.WaveIndex];
        encounter.StartWaveAt = now + wave.DelayMs;
        if (wave.StartEvent is { } message && encounter.SignalEntity is { } portal)
        {
            EncounterSignals.SendEvent(rt, portal, message);
            encounter.StartWaveAt += 500;
        }
        if (!definition.BossRushEffects || encounter.SignalEntity is not { } signal) return;
        encounter.EnableBossPortalsAt = encounter.StartWaveAt;
        encounter.StartWaveAt += 2000;
        EncounterSignals.SetBossRushWave(rt, signal, encounter.WaveIndex, definition.Waves.Select(wave => wave.Spawns.Length).ToArray());
        if (encounter.WaveIndex == 0) EncounterSignals.SendEvent(rt, signal, "StartInstance");
        EncounterSignals.SendEvent(rt, signal, "SpawnEffectsPre");
    }

    private static void EnemyDefeated(Encounter encounter, SpawnPoint spawn, long now)
    {
        encounter.Alive--;
        if (encounter.RemainingReplacements == 0) return;
        encounter.RemainingReplacements--;
        encounter.Replacements.Enqueue(spawn, now + 2000);
    }

    private void Spawn(ZenFabricRuntime rt, (string Realm, string Area) space, AreaRun run, SpawnPoint spawn,
        Encounter encounter, long now, bool portal, bool countsForWave = true)
    {
        var enemy = new Enemy(spawn, encounter, countsForWave, now);
        if (encounter.Navigation is { } navigation &&
            navigation.Nearest(spawn.Position, radius: 3, vertical: 3) is { } cell)
        {
            enemy.NavigationCell = cell;
            enemy.Position = navigation.Point(cell);
        }
        else log.LogWarning("Enemy spawn at {Position} is outside the navigation mesh for {Encounter}",
            spawn.Position, encounter.Definition.Name);
        if (portal)
        {
            enemy.SpawnEffectAt = now + 500;
            enemy.MoveAfterSpawnAt = now + 800;
        }
        var entity = rt.CreateAreaEntity(space, Components(enemy));
        enemy.Entity = entity;
        run.Enemies.Add(enemy);
        _byId.Add(entity.Id, enemy);
    }

    private void Remove(ZenFabricRuntime rt, Enemy enemy)
    {
        if (enemy.Entity is not { } entity) return;
        rt.DeleteEntity(entity.Id, null);
        _byId.Remove(entity.Id);
        enemy.Entity = null;
    }

    private static void UpdateTarget(ZenFabricRuntime rt, Enemy enemy, long target, long now)
    {
        if (enemy.Target == target) return;
        enemy.Target = target;
        enemy.BurstShotsRemaining = 0;
        enemy.NextAttack = now + 900;
        var update = new SchemaWriter();
        CharacterEntities.WriteCombatTarget(update, 3, target);
        update.AddBool(5, target != 0);
        CharacterEntities.WriteCombatTarget(update, 8, target);
        rt.ReplaceComponentSnapshot(enemy.Entity!, CharacterEntities.Combat, CombatState(enemy), update.ToArray());
    }

    private static void SetStagger(ZenFabricRuntime rt, Enemy enemy, long? now)
    {
        enemy.StaggerStartedAt = now;
        var update = new SchemaWriter();
        update.AddBool(1, now.HasValue);
        CharacterEntities.WriteCombatVulnerabilityTime(update, CombatVulnerability(enemy));
        rt.ReplaceComponentSnapshot(enemy.Entity!, CharacterEntities.Combat, CombatState(enemy), update.ToArray());
    }

    private static (float Duration, double Start)? CombatVulnerability(Enemy enemy) =>
        enemy.StaggerStartedAt is { } start ? (StaggerSeconds, start / 1000d) : null;

    private static bool CanReachForMelee(Enemy enemy, (double X, double Y, double Z) target) =>
        enemy.Encounter.Navigation is { } navigation && enemy.NavigationCell is { } from &&
        navigation.Nearest(target, radius: 1, vertical: 3) is { } to && navigation.CanWalkDirect(from, to);

    private static bool Chase(ZenFabricRuntime rt, Enemy enemy, (double X, double Y, double Z) target, long now,
        bool followApproach)
    {
        if (enemy.Encounter.Navigation is not { } navigation || enemy.NavigationCell is not { } cell ||
            navigation.Nearest(target, radius: 3, vertical: 8) is not { } goal)
        {
            SetSpeed(rt, enemy, 0);
            return true;
        }

        var origin = navigation.Point(cell);
        if (enemy.Position == origin && enemy.RouteGoal != goal && now >= enemy.PlanRouteAt)
        {
            var home = enemy.Spawn.Approach ?? enemy.Spawn.Position;
            var arena = enemy.Encounter.Definition;
            bool CanEnter(int candidate)
            {
                if (followApproach) return true;
                var next = navigation.Point(candidate);
                return WithinOrReturning(next, origin, home, enemy.Spawn.Kind.Leash) &&
                    WithinOrReturning(next, origin, arena.Center, arena.StartRadius + 5);
            }
            enemy.Route = navigation.FindRoute(cell, goal, CanEnter);
            enemy.RouteGoal = goal;
            enemy.PlanRouteAt = now + 600;
        }
        if (!enemy.Route.TryPeek(out var nextCell))
        {
            SetSpeed(rt, enemy, 0);
            return enemy.RouteGoal == goal;
        }

        const float speed = 3.6f;
        const float step = speed * UpdateIntervalMs / 1000;
        var waypoint = navigation.Point(nextCell);
        var distance = Math.Sqrt(DistanceSquared(waypoint, enemy.Position));
        var nextPosition = waypoint;
        if (distance > step)
        {
            var ratio = (float)(step / distance);
            nextPosition = (enemy.Position.X + (waypoint.X - enemy.Position.X) * ratio,
                enemy.Position.Y + (waypoint.Y - enemy.Position.Y) * ratio,
                enemy.Position.Z + (waypoint.Z - enemy.Position.Z) * ratio);
        }
        else
        {
            enemy.NavigationCell = nextCell;
            enemy.Route.Dequeue();
        }
        Move(rt, enemy, nextPosition, waypoint, speed);
        return false;
    }

    private static bool WithinOrReturning((float X, float Y, float Z) next, (float X, float Y, float Z) current,
        (float X, float Y, float Z) center, float radius)
    {
        var distance = HorizontalDistanceSquared(next, center);
        return distance <= radius * radius || distance < HorizontalDistanceSquared(current, center);
    }

    private static void Move(ZenFabricRuntime rt, Enemy enemy, (float X, float Y, float Z) next,
        (double X, double Y, double Z) facing, float speed)
    {
        var dx = facing.X - enemy.Position.X;
        var dz = facing.Z - enemy.Position.Z;
        var rotation = dx * dx + dz * dz > 0.0004 ? FacingRotation(Math.Atan2(dx, dz)) : enemy.Rotation;
        var moved = enemy.Position != next;
        if (moved)
        {
            var position = StandardComponents.Position(next.X, next.Y, next.Z);
            rt.ReplaceComponentSnapshot(enemy.Entity!, Ids.Position, position, position);
        }
        if (moved || enemy.Rotation != rotation)
        {
            enemy.Position = next;
            enemy.Rotation = rotation;
            enemy.TransformTimestamp = Environment.TickCount64 / 1000d;
            var update = new SchemaWriter();
            CharacterEntities.WriteZSyncPose(update, next, rotation);
            update.AddDouble(5, enemy.TransformTimestamp);
            rt.ReplaceComponentSnapshot(enemy.Entity!, Ids.ZSyncComponent, Transform(enemy), update.ToArray());
        }
        SetSpeed(rt, enemy, speed);
    }

    private static uint FacingRotation(double yaw)
    {
        // ZSync stores the three smallest quaternion components and the omitted component's index.
        const uint zero = 511;
        var y = Math.Sin(yaw / 2);
        var w = Math.Cos(yaw / 2);
        static uint Pack(double component) => (uint)Math.Clamp((component * Math.Sqrt(2) + 1) * 511.5, 0, 1023);
        return Math.Abs(y) > w
            ? (1u << 30) | (Pack(w * Math.Sign(y)) << 20) | (zero << 10) | zero
            : (3u << 30) | (zero << 20) | (Pack(y) << 10) | zero;
    }

    private static void SetSpeed(ZenFabricRuntime rt, Enemy enemy, float speed)
    {
        if (enemy.Speed == speed) return;
        enemy.Speed = speed;
        var update = new SchemaWriter();
        update.AddFloat(1, speed);
        rt.ReplaceComponentSnapshot(enemy.Entity!, Animation, AnimationState(speed), update.ToArray());
    }

    private static ComponentData[] Components(Enemy enemy) =>
    [
        new(Ids.Metadata, StandardComponents.Metadata(enemy.Spawn.Kind.Prefab)),
        new(Ids.Position, StandardComponents.Position(enemy.Position.X, enemy.Position.Y, enemy.Position.Z)),
        new(Ids.ZSyncComponent, Transform(enemy)),
        Make(Ids.ActorInfoComponent, w =>
        {
            w.AddEntityId(1, 0);
            w.AddString(2, enemy.Spawn.Kind.Name);
            w.AddUInt32(3, 1); // Enemy
            w.AddString(4, "");
        }),
        Make(1009, w => w.AddEmptyList(1)), // Actor status effects
        new(Animation, AnimationState(0)),
        new(CharacterEntities.Combat, CombatState(enemy)),
        Make(1016, w => { w.AddEmptyList(1); w.AddString(2, ""); }), // Equipment
        new(3028, []), // Indicator
        Make(3016, w => { w.AddBool(1, false); w.AddBool(2, false); }), // Transform ownership
        Make(Lootable, w =>
        {
            w.AddString(1, "");
            w.AddBool(2, false);
            w.AddInt32(3, 1);
            w.AddEmptyList(4);
            w.AddInt32(5, 1);
            w.AddEmptyList(6);
        }),
        new(CharacterEntities.PublicActorStats, Stats(enemy)),
        new(2029, []), // Ranged combat
        new(1014, []), // SFX
        Make(2028, w => w.AddFloat(1, 1)), // Time scale
        Make(CharacterEntities.Vfx, w => w.AddEmptyOption(1)),
    ];

    private static byte[] Transform(Enemy enemy) => CharacterEntities.ZSyncSnapshot(enemy.Position, 0,
        rotation: enemy.Rotation, timestamp: enemy.TransformTimestamp);

    private static byte[] AnimationState(float speed)
    {
        var w = new SchemaWriter();
        w.AddFloat(1, speed);
        w.AddFloat(2, 0);
        w.AddEmptyList(5);
        w.AddBool(6, false);
        return w.ToArray();
    }

    private static byte[] CombatState(Enemy enemy)
    {
        var w = new SchemaWriter();
        CharacterEntities.WriteCombatState(w, null, enemy.Target, CombatVulnerability(enemy));
        return w.ToArray();
    }

    private static byte[] Stats(Enemy enemy) =>
        CharacterEntities.PublicStatsSnapshot(role: 0, enemy.Health, enemy.Spawn.Kind.Health,
            enemy.Spawn.Kind.Damage, enemy.Spawn.Kind.Damage, enemy.Vulnerability, enemy.MaxVulnerability,
            StaggerSeconds, MaxStaggerSeconds);

    internal static SchemaWriter HealthUpdate(int health, int damage = 0, long attacker = 0, long victim = 0,
        int? vulnerability = null)
    {
        var w = new SchemaWriter();
        w.AddInt32(9, health);
        if (vulnerability is { } remaining) w.AddInt32(18, remaining);
        if (damage == 0) return w;
        w.AddObject(1001, hit =>
        {
            hit.AddInt32(1, damage);
            hit.AddBool(2, health == 0);
            hit.AddString(3, attacker.ToString(CultureInfo.InvariantCulture));
            hit.AddString(4, victim.ToString(CultureInfo.InvariantCulture));
            hit.AddUInt32(5, 0); // Normal damage
            hit.AddBool(6, true); // Text popup is sent separately.
            hit.AddInt32(7, damage);
        });
        w.AddObject(1008, popup =>
        {
            popup.AddEmptyOption(1);
            popup.AddInt32(2, damage, isOption: true);
            popup.AddInt32(3, damage, isOption: true);
        });
        if (health == 0) w.AddObject(1010, death => death.AddEntityId(1, attacker));
        return w;
    }

    private static double DistanceSquared((double X, double Y, double Z) first, (double X, double Y, double Z) second)
    {
        var dy = first.Y - second.Y;
        return HorizontalDistanceSquared(first, second) + dy * dy;
    }

    private static double HorizontalDistanceSquared((double X, double Y, double Z) first, (double X, double Y, double Z) second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return dx * dx + dz * dz;
    }

    private static ComponentData Make(uint id, Action<SchemaWriter> write)
    {
        var w = new SchemaWriter();
        write(w);
        return new(id, w.ToArray());
    }
}
