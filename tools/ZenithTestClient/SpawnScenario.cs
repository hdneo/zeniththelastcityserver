using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

internal sealed class SpawnScenario(string accountId, string ticket, string characterId, string name, long playerId, long workerId,
    bool challenges, bool shop, bool revive)
{
    private const uint VfxComponent = 1013;
    private const uint PublicActorStats = 4021;
    private const uint GodstoneComponent = 2035;
    private const uint BowComponent = 4031;
    private const uint ArrowComponent = 4032;
    private const uint ProjectileComponent = 2030;
    private const uint LootComponent = 2014;
    private const uint InventoryComponent = 1015;
    private readonly Dictionary<long, string> _entities = new();
    private readonly Dictionary<long, HashSet<uint>> _authority = new();
    private int _responses;
    private long _characterId;
    private long _avatarId;
    private long _characterWithInterest;
    private bool _bodyPartsLinked;
    private bool _inventoryAccountReady;
    private bool _inventoryWorldReady;
    private bool _rejectedWrongHands;
    private bool _handsRegistered;
    private bool _mainWorld;
    private bool _worldLoaded;
    private bool _positionAccepted;
    private int _teleportsCompleted;
    private uint _lifeState = 3;
    private int _startingHealth;
    private int _health;
    private int _maxHealth;
    private float _totalHealth;
    private int _experience;
    private int _level;
    private bool _leveledUp;
    private int _startingLootCount;
    private enum LootPickup { None, TooFar, Moving, Grabbing, TakingOwnership, Releasing, Claiming, Duplicate, Done }
    private LootPickup _lootPickup;
    private long _loot;
    private (double X, double Y, double Z) _lootPosition;
    private string? _lootItem;
    private string? _pickedUpInstance;
    private long _grabLootAt;
    private bool _recoveryAcknowledged;
    private readonly ChallengeScenario? _challenges = challenges ? new(playerId, firstRoute: revive ? 1 : 0) : null;
    private readonly ShopScenario? _shop = shop ? new() : null;
    private long _shoulderBow;
    private bool _starterBowEquipped;
    private bool _starterAbilities;
    private bool _starterStatusEffects;
    private bool _bowDrawUpdated;
    private Guid _arrowCastId;
    private long _arrow;
    private bool _arrowAttached;
    private bool _arrowShot;
    private int _bowAmmo = -1;
    private int _combatAmmo = -1;
    private int _shotResponses;
    private int _cooldownUpdates;
    private int _airChopsEnded;
    private Guid _castId;
    private long _projectile;
    private bool _projectileInitialized;
    private EncounterScenario? _encounter;
    private long _enemy;
    private int _expectedEnemyHealth;
    private int _airChopHitResponses;
    private int _encounterArrowAmmo;
    private bool _testedEnemyOwnership;
    private bool _waitingForEnemyAttack;
    private bool _enemyAttacked;
    private bool _testedGunnerAttack;
    private bool _testedSniperAttack;
    private bool _receivedEnemyOrb;
    private bool _receivedEnemyOrbLink;
    private HashSet<long> _aggroEnemies = [];
    private bool _combatShotPending;
    private long _nextCombatShot;
    private enum ReviveStage { None, Opening, Casting, Outside, WaitingForDeath, Ghost, Requested }
    private ReviveStage _reviveStage = revive ? ReviveStage.Opening : ReviveStage.None;
    private int _enemyHealthBeforeHeal;
    private long _defeatedAt;
    private static readonly (double X, double Y, double Z) GhostLocation = (-246, -3.57, -26);
    public bool Completed { get; private set; }

    public CommandRequestOp Start(long requestId) => Request(requestId, characterId, "invalid-ticket");

    public ZfOp? Handle(ZfOp op, long requestId)
    {
        if (_shop is not null)
        {
            var next = _shop.Handle(op, requestId, _characterId);
            Completed = _shop.Completed;
            if (next is not null || Completed) return next;
        }
        switch (op)
        {
            case CommandResponseOp { ComponentId: Ids.CharacterSpawnerComponent, CommandIndex: 3 } response:
                _responses++;
                switch (_responses)
                {
                    case 1:
                        Require(response.Status == StatusCode.PermissionDenied, "invalid account ticket");
                        return Request(requestId, "unknown-character", ticket);
                    case 2:
                        Require(response.Status == StatusCode.NotFound, "unknown character");
                        return Request(requestId, characterId, ticket);
                    case 3:
                        Require(response.Status == StatusCode.Success && response.Data.Length == 0, "empty spawn acknowledgment");
                        break;
                    case 4:
                        Require(response.Status == StatusCode.Success && response.Data.Length == 0, "idempotent spawn retry");
                        Require(_mainWorld, "main world assigned after spawn");
                        Require(_bodyPartsLinked, "avatar linked to replicated body parts");
                        Require(_inventoryAccountReady && _inventoryWorldReady, "inventory title and season-rank readers are present");
                        return RegisterHands(requestId, Find("RightHand"), Find("LeftHand"));
                }
                break;
            case AddComponentOp { Data.ComponentId: Ids.Metadata } metadata:
                Require(_responses >= 3, "spawn acknowledgment before entity replication");
                _entities.Add(metadata.EntityId, StandardComponents.ReadMetadata(metadata.Data.Data)!);
                break;
            case RemoveEntityOp removed:
                _challenges?.CheckRemoval(removed.EntityId);
                _entities.Remove(removed.EntityId);
                _authority.Remove(removed.EntityId);
                break;
            case AddComponentOp { Data.ComponentId: InventoryComponent } inventory:
                var initialInventory = new SchemaReader(inventory.Data.Data);
                for (uint field = 1; field < 4; field++)
                {
                    Require(initialInventory.ReadFieldId() == field, "initial inventory bags");
                    initialInventory.ReadObjectList();
                }
                Require(initialInventory.ReadFieldId() == 4, "initial basic inventory field");
                _startingLootCount = initialInventory.ReadObjectList().Count;
                break;
            case AddComponentOp { Data.ComponentId: Ids.InstancedAreaTriggerPrefabInfoComponent } prefab:
                _challenges?.RecordPrefab(prefab);
                break;
            case AddComponentOp { Data.ComponentId: 1016 } equipment when challenges && _characterId == 0:
                CheckStarterEquipment(equipment.Data.Data);
                break;
            case AddComponentOp { Data.ComponentId: 1009 } effects when _characterId == 0:
                var status = new SchemaReader(effects.Data.Data);
                _starterStatusEffects = status.ReadFieldId() == 1 && status.ReadListCount() == 0 && !status.HasMore;
                break;
            case AddComponentOp { Data.ComponentId: GodstoneComponent } abilities when challenges && _characterId == 0:
                CheckStarterAbilities(abilities.Data.Data);
                break;
            case AddComponentOp { Data.ComponentId: Ids.ZSyncComponent } bow
                when challenges && _entities.GetValueOrDefault(bow.EntityId) == "Item-CN-Bow-Sky1":
                if (ReadShoulderParent(bow.Data.Data) == _avatarId) _shoulderBow = bow.EntityId;
                break;
            case AddComponentOp { Data.ComponentId: BowComponent } bow when bow.EntityId == _shoulderBow:
                var initialBow = new SchemaReader(bow.Data.Data);
                Require(initialBow.ReadFieldId() == 2, "initial bow draw field");
                initialBow.ReadFloat();
                Require(initialBow.ReadFieldId() == 3, "initial bow nock field");
                initialBow.ReadFloat();
                Require(initialBow.ReadFieldId() == 4 && initialBow.ReadInt32() == 12, "bow starts with 12 arrows");
                break;
            case AddComponentOp { Data.ComponentId: Ids.ZSyncComponent } arrow when arrow.EntityId == _arrow:
                _arrowAttached = ReadShoulderParent(arrow.Data.Data) == _shoulderBow;
                break;
            case AddComponentOp { Data.ComponentId: 4022 } cast when _castId != Guid.Empty || _arrowCastId != Guid.Empty:
                var prewarm = new SchemaReader(cast.Data.Data);
                Require(prewarm.ReadFieldId() == 1, "prewarm ID field");
                var prewarmId = prewarm.ReadOptionalString();
                if (prewarmId == _castId.ToString()) _projectile = cast.EntityId;
                if (prewarmId == _arrowCastId.ToString()) _arrow = cast.EntityId;
                break;
            case AddComponentOp { Data.ComponentId: ProjectileComponent } projectile when projectile.EntityId == _projectile:
                _projectileInitialized = projectile.Data.Data.Length == 144;
                break;
            case AddComponentOp { Data.ComponentId: Ids.PlayerAvatarComponent } avatar when _characterId == 0:
                var initial = new SchemaReader(avatar.Data.Data);
                Require(ReadEntityId(ref initial, 1) == playerId, "avatar player link");
                CheckBodyParts(ref initial, 0, 0, 0);
                break;
            case AddComponentOp { Data.ComponentId: Ids.Interest } interest
                when _entities.GetValueOrDefault(interest.EntityId) == "PlayerCharacter":
                var interests = new SchemaReader(interest.Data.Data);
                Require(interests.ReadFieldId() == 1 && interests.ReadListCount() == 0 && !interests.HasMore,
                    "valid initial character interest map");
                _characterWithInterest = interest.EntityId;
                break;
            case AddComponentOp { Data.ComponentId: Ids.ActorStatsComponent } stats
                when _entities.GetValueOrDefault(stats.EntityId) == "PlayerCharacter":
                CheckActorStats(stats.Data.Data, initialized: false);
                break;
            case AddComponentOp { Data.ComponentId: PublicActorStats } stats
                when _characterId == 0 && _entities.GetValueOrDefault(stats.EntityId) == "PlayerCharacter":
                var initialStats = new SchemaReader(stats.Data.Data);
                Require(initialStats.ReadFieldId() == 7, "starting actor level field");
                _level = initialStats.ReadInt32();
                Require(_level is >= 1 and <= 40 && initialStats.ReadFieldId() == 8, "starting actor XP field");
                _experience = initialStats.ReadInt32();
                Require(_experience >= 0 && (_experience >= 6000) == (_level >= 2), "saved XP matches starting level");
                Require(initialStats.ReadFieldId() == 9, "current health field");
                _startingHealth = _health = initialStats.ReadInt32();
                Require(_health > 0, "positive starting health");
                Require(ReadInitialMaxHealth(ref initialStats) == 0, "effective max health starts unset");
                break;
            case AddComponentOp { Data.ComponentId: Ids.PlayerAccountPersistenceComponent } persistence
                when _entities.GetValueOrDefault(persistence.EntityId) == "PlayerCharacter":
                var accountData = new SchemaReader(persistence.Data.Data);
                Require(accountData.ReadFieldId() == 1 && accountData.ReadBool(), "account inventory data initialized");
                for (uint field = 2; field <= 4; field++)
                    Require(accountData.ReadFieldId() == field && accountData.ReadListCount() == 0, "empty account persistence collections");
                _inventoryAccountReady = !accountData.HasMore;
                break;
            case AddComponentOp { Data.ComponentId: Ids.PlayerWorldPersistenceComponent } persistence
                when _entities.GetValueOrDefault(persistence.EntityId) == "PlayerCharacter":
                var worldData = new SchemaReader(persistence.Data.Data);
                for (uint field = 1; field <= 14; field++)
                    Require(worldData.ReadFieldId() == field && worldData.ReadListCount() == 0, "empty character persistence collections");
                Require(worldData.ReadFieldId() == 15 && worldData.ReadOptionalString() == "" &&
                    worldData.ReadFieldId() == 16 && worldData.ReadListCount() == 0 &&
                    worldData.ReadFieldId() == 17 && worldData.ReadListCount() == 0 &&
                    worldData.ReadFieldId() == 18, "inventory has a selected title field and progression defaults");
                worldData.ReadBytesSpan();
                Require(worldData.ReadFieldId() == 19 && worldData.ReadListCount() == 0 &&
                    worldData.ReadFieldId() == 20, "inventory has season rank data");
                var rankData = new SchemaReader(worldData.ReadBytesSpan());
                _inventoryWorldReady = rankData.ReadFieldId() == 1 && rankData.ReadInt32() == 0 && !worldData.HasMore;
                break;
            case AddComponentOp { Data.ComponentId: Ids.Position } position
                when _entities.GetValueOrDefault(position.EntityId) is { } prefab && prefab.StartsWith("EnemyBase-", StringComparison.Ordinal):
                Require(_challenges?.InChallengeArea == true, "enemies are replicated only in their challenge area");
                _encounter?.Targets.Add(position.EntityId,
                    new(prefab, StandardComponents.ReadPosition(position.Data.Data)!.Value));
                break;
            case AddComponentOp { Data.ComponentId: Ids.Position } position
                when _lootPickup == LootPickup.None && _entities.GetValueOrDefault(position.EntityId) == "generic_reimagined_loot_drop":
                _lootPosition = StandardComponents.ReadPosition(position.Data.Data)!.Value;
                break;
            case AddComponentOp { Data.ComponentId: LootComponent } drop
                when _lootPickup == LootPickup.None && _encounter is not null && !revive:
                _loot = drop.EntityId;
                var lootData = new SchemaReader(drop.Data.Data);
                Require(lootData.ReadFieldId() == 1 && lootData.ReadListCount() == 1, "enemy drop contains one item");
                var lootItem = new SchemaReader(lootData.ReadBytesSpan());
                Require(lootItem.ReadFieldId() == 1 && lootItem.ReadInt32() == 0 && lootItem.ReadFieldId() == 2,
                    "enemy drop item schema");
                _lootItem = lootItem.ReadOptionalString();
                Require(!string.IsNullOrWhiteSpace(_lootItem) &&
                    lootData.ReadFieldId() == 2 && lootData.ReadListCount() == 1 &&
                    lootData.ReadString() == characterId, "enemy drop is for the contributing character");
                _lootPickup = LootPickup.TooFar;
                return ClaimLoot(requestId);
            case ComponentUpdateOp { ComponentId: 4026 } signal when _encounter is { } encounterSignal:
                var signals = new SchemaReader(signal.Data);
                Require(signals.ReadFieldId() == 1001, "encounter custom event");
                var portalSignal = new SchemaReader(signals.ReadBytesSpan());
                Require(portalSignal.ReadFieldId() == 1, "encounter signal name");
                if (portalSignal.ReadOptionalString() is "TurnOnPortals&Spawns" or "SpawnEffectsSpawn")
                    encounterSignal.PortalSignal = true;
                break;
            case ComponentUpdateOp { ComponentId: VfxComponent } effect
                when _encounter?.Targets.ContainsKey(effect.EntityId) == true:
                var visual = new SchemaReader(effect.Data);
                Require(_encounter.PortalSignal && visual.ReadFieldId() == 1001, "enemy spawn VFX follows portal signal");
                var spawnEffect = new SchemaReader(visual.ReadBytesSpan());
                Require(spawnEffect.ReadFieldId() == 1 && spawnEffect.ReadBytesSpan().Length == 48 &&
                    spawnEffect.ReadFieldId() == 2 && spawnEffect.ReadUInt32() == 13 &&
                    spawnEffect.ReadFieldId() == 3 && spawnEffect.ReadOptionalString() == "BasicPlayerActions",
                    "enemy replicates the captured spawn effect");
                _encounter.SpawnEffect = true;
                break;
            case ComponentUpdateOp { ComponentId: Ids.Position } position
                when _encounter?.Targets.GetValueOrDefault(position.EntityId) is { } movingEnemy:
                movingEnemy.Position = StandardComponents.ReadPosition(position.Data)!.Value;
                break;
            case ComponentUpdateOp { ComponentId: 1010 } combat
                when _encounter?.Targets.GetValueOrDefault(combat.EntityId) is { } combatEnemy:
                var combatData = new SchemaReader(combat.Data);
                switch (combatData.ReadFieldId())
                {
                    case 1:
                        combatEnemy.Staggered = combatData.ReadBool();
                        Require(combatData.ReadFieldId() == 6 && combatData.ReadOptionHasValue() == combatEnemy.Staggered,
                            "enemy stagger update includes its duration");
                        if (combatEnemy.Staggered) Require(combatData.ReadFloat() == 2, "two-second stagger");
                        Require(combatData.ReadFieldId() == 7 && combatData.ReadOptionHasValue() == combatEnemy.Staggered,
                            "enemy stagger update includes its start time");
                        if (combatEnemy.Staggered) Require(combatData.ReadDouble() > 0, "server time for stagger");
                        break;
                    case 1007:
                        var action = new SchemaReader(combatData.ReadBytesSpan());
                        Require(action.ReadFieldId() == 1 && action.ReadInt32() == (combatEnemy.Sniper ? 1 : 0) &&
                            action.ReadFieldId() == 2 && action.ReadBytesSpan().Length == 18 && action.ReadFieldId() == 3,
                            "ranged enemy starts its shooting action at its position");
                        action.ReadFloat();
                        Require(action.ReadFieldId() == 4 && action.ReadOptionHasValue() &&
                            action.ReadInt64() == _characterId, "shooting animation targets the character");
                        combatEnemy.RangedActionStarted = true;
                        break;
                    case 1015:
                        var linked = new SchemaReader(combatData.ReadBytesSpan());
                        Require(combatEnemy.RangedActionStarted && linked.ReadFieldId() == 1 &&
                            _entities.GetValueOrDefault(linked.ReadInt64()) ==
                                (combatEnemy.Sniper ? "Projectile_SmallEnergyOrb_Sniper" : "Projectile_SmallEnergyOrb_Gunner"),
                            "shooting action links the matching orb after starting the animation");
                        if (combat.EntityId == _enemy) _receivedEnemyOrbLink = true;
                        break;
                }
                break;
            case ComponentUpdateOp { ComponentId: 3000 } aggro when aggro.EntityId == _characterId:
                var aggroData = new SchemaReader(aggro.Data);
                Require(aggroData.ReadFieldId() == 1, "player aggro map field");
                _aggroEnemies = [];
                foreach (var entryData in aggroData.ReadObjectList())
                {
                    var entry = new SchemaReader(entryData);
                    Require(entry.ReadFieldId() == 1, "enemy in player aggro map");
                    Require(_aggroEnemies.Add(entry.ReadInt64()) && entry.ReadFieldId() == 2,
                        "aggro map identifies each enemy once");
                    entry.ReadInt32();
                    Require(!entry.HasMore, "complete aggro entry");
                }
                Require(!aggroData.HasMore, "complete player aggro update");
                if (_reviveStage == ReviveStage.Requested && _lifeState == 3 && _health == _maxHealth &&
                    _aggroEnemies.Contains(_enemy))
                {
                    Console.WriteLine("   [OK] Self-revive restores health at the moved ghost position and resumes combat after quick confirmation");
                    Completed = true;
                }
                break;
            case ComponentUpdateOp { ComponentId: Ids.ZSyncComponent } shot
                when _entities.GetValueOrDefault(shot.EntityId) is "Projectile_SmallEnergyOrb_Gunner" or "Projectile_SmallEnergyOrb_Sniper":
                var sync = new SchemaReader(shot.Data);
                Require(sync.ReadFieldId() == 7 && sync.ReadOptionHasValue(), "enemy orb starts linear flight");
                var motion = new SchemaReader(sync.ReadBytesSpan());
                Require(motion.ReadFieldId() == 1 && motion.ReadBytesSpan().Length == 18 &&
                    motion.ReadFieldId() == 2 && motion.ReadDouble() > 0 &&
                    motion.ReadFieldId() == 3 && motion.ReadFloat() == 20, "enemy orb replicates its path and speed");
                if (_waitingForEnemyAttack && _aggroEnemies.Contains(_enemy)) _receivedEnemyOrb = true;
                break;
            case AddComponentOp { Data.ComponentId: PublicActorStats } stats
                when _encounter?.Targets.ContainsKey(stats.EntityId) == true:
                var enemy = new SchemaReader(stats.Data.Data);
                Require(enemy.ReadFieldId() == 7 && enemy.ReadInt32() == 1 &&
                    enemy.ReadFieldId() == 8 && enemy.ReadInt32() == 0 && enemy.ReadFieldId() == 9, "enemy stats at spawn");
                var enemyHealth = enemy.ReadInt32();
                var stagger = enemy;
                Require(stagger.ReadFieldId() == 18, "initial stagger meter field");
                var vulnerability = stagger.ReadInt32();
                Require(ReadInitialMaxHealth(ref enemy) == enemyHealth, "enemy starts at full health");
                Require(enemy.ReadFieldId() == 30 && enemy.ReadInt32() == vulnerability && vulnerability > 0,
                    "enemy starts with a nonzero, empty stagger meter");
                _encounter.Add(stats.EntityId, enemyHealth, vulnerability);
                break;
            case DeleteEntityResponseOp deleted when deleted.EntityId == _enemy:
                Require(deleted.Status == StatusCode.PermissionDenied, "the server owns the enemy");
                _testedEnemyOwnership = true;
                _combatShotPending = false;
                _enemy = 0;
                break;
            case ComponentSetAuthorityChangeOp { Authority: Authority.Authoritative } change:
                if (!_authority.TryGetValue(change.EntityId, out var sets))
                    _authority.Add(change.EntityId, sets = []);
                sets.Add(change.ComponentSetId);
                break;
            case ComponentUpdateOp { ComponentId: Ids.PlayerCharacterComponent } links when links.EntityId == playerId:
                CheckGraph(links.Data);
                return Request(requestId, characterId, ticket);
            case ComponentUpdateOp { ComponentId: Ids.PlayerAvatarComponent } links when links.EntityId == _avatarId:
                var parts = new SchemaReader(links.Data);
                CheckBodyParts(ref parts, Find("LeftHand"), Find("RightHand"), Find("Head"));
                _bodyPartsLinked = true;
                break;
            case ComponentUpdateOp { ComponentId: Ids.PlayerWorldComponent } world when world.EntityId == playerId:
                if (_challenges is not null)
                {
                    _challenges.RecordWorld(world.Data, initialEntry: !_mainWorld);
                    if (_mainWorld)
                    {
                        var next = _challenges.AreaChanged(requestId, _avatarId);
                        if (next is not null || !_challenges.StartEncounter) return next;
                        _encounter = new EncounterScenario(_challenges.RouteIndex, _challenges.DailyStops);
                        return ChallengeScenario.Move(_avatarId, _encounter.Current.Position);
                    }
                    _mainWorld = true;
                }
                else _mainWorld = world.Data.AsSpan().SequenceEqual(new byte[] { 1, 0, 1, 0, 0, 0 });
                break;
            case ComponentUpdateOp { ComponentId: Ids.NetworkedObjectInteractionComponent } hands when hands.EntityId == _characterId:
                var handLinks = new SchemaReader(hands.Data);
                Require(handLinks.ReadFieldId() == 2 && handLinks.ReadInt64() == Find("LeftHand"), "left interaction hand");
                Require(handLinks.ReadFieldId() == 3 && handLinks.ReadInt64() == Find("RightHand") && !handLinks.HasMore,
                    "right interaction hand");
                _handsRegistered = true;
                break;
            case CommandResponseOp { ComponentId: Ids.NetworkedObjectInteractionComponent, CommandIndex: 2 } hands:
                if (!_rejectedWrongHands)
                {
                    Require(hands.Status == StatusCode.PermissionDenied && !_handsRegistered, "swapped hands rejected");
                    _rejectedWrongHands = true;
                    return RegisterHands(requestId, Find("LeftHand"), Find("RightHand"));
                }
                Require(hands.Status == StatusCode.Success && hands.Data.Length == 0 && _handsRegistered,
                    "hand registration acknowledgment and component update");
                var ready = new SchemaWriter();
                ready.AddEmptyOption(1);
                ready.AddBool(2, false);
                return WorldCommand(requestId, _avatarId, Ids.TeleportComponent, 3, ready.ToArray());
            case CommandResponseOp { ComponentId: Ids.TeleportComponent, CommandIndex: 3 } readyResponse:
                Require(readyResponse.Status == StatusCode.Success && readyResponse.Data.Length == 0, "avatar ready response");
                return WorldCommand(requestId, playerId, Ids.PlayerWorldComponent, 2, []);
            case CommandResponseOp { ComponentId: Ids.PlayerWorldComponent, CommandIndex: 2 } loaded:
                Require(loaded.Status == StatusCode.Success && loaded.Data.Length == 0, "world loaded response");
                _worldLoaded = true;
                break;
            case CommandResponseOp { ComponentId: Ids.PlayerWorldComponent, CommandIndex: 4 } areas when _challenges is not null:
                return _challenges.EnterFirstArea(areas, _avatarId);
            case CommandResponseOp { ComponentId: Ids.PlayerWorldComponent, CommandIndex: 10 } returned when _challenges is not null:
                Require(returned.Status == StatusCode.Success && returned.Data.Length == 0, "return to Neo City acknowledged");
                break;
            case ComponentUpdateOp { ComponentId: BowComponent } draw when draw.EntityId == _shoulderBow:
                var bowUpdate = new SchemaReader(draw.Data);
                var bowField = bowUpdate.ReadFieldId();
                if (bowField == 2)
                {
                    _bowDrawUpdated = bowUpdate.ReadFloat() == 0.5f &&
                        bowUpdate.ReadFieldId() == 3 && bowUpdate.ReadFloat() == -0.25f && !bowUpdate.HasMore;
                    break;
                }
                Require(bowField == 4, "ammo count update");
                _bowAmmo = bowUpdate.ReadInt32();
                Require(bowUpdate.ReadFieldId() == 6 && bowUpdate.ReadFloat() >= 0 && !bowUpdate.HasMore &&
                    _bowAmmo == _combatAmmo, "ammo count and recharge are replicated");
                if (_bowAmmo == 12 && _airChopsEnded == 1) return StartAirChopsCooldown(requestId);
                break;
            case ComponentUpdateOp { ComponentId: 1010 } combat when combat.EntityId == _characterId && challenges:
                var ammo = new SchemaReader(combat.Data);
                Require(ammo.ReadFieldId() == 13 && ammo.ReadOptionHasValue(), "character combat ammo");
                _combatAmmo = ammo.ReadInt32();
                Require(!ammo.HasMore, "only combat ammo changed");
                break;
            case ComponentUpdateOp { ComponentId: ArrowComponent } shot when shot.EntityId == _arrow:
                var arrowUpdate = new SchemaReader(shot.Data);
                _arrowShot = arrowUpdate.ReadFieldId() == 2 && arrowUpdate.ReadBool() && !arrowUpdate.HasMore;
                break;
            case CommandResponseOp { ComponentId: BowComponent, CommandIndex: 1 } draw when draw.EntityId == _shoulderBow:
                Require(draw.Status == StatusCode.Success && _bowDrawUpdated, "starter bow draw replicated");
                return SpawnArrow(requestId);
            case CommandResponseOp { ComponentId: BowComponent, CommandIndex: 2 } nock when nock.EntityId == _shoulderBow:
                Require(nock.Status == StatusCode.Success && _arrow != 0 && _arrowAttached &&
                    _entities.GetValueOrDefault(_arrow) == "Projectile-BoStaffArrow-Reimagined-Base", "arrow attached and prewarmed");
                return WorldCommand(requestId, _arrow, ArrowComponent, 2, []);
            case CommandResponseOp { ComponentId: ArrowComponent, CommandIndex: 2 } released when released.EntityId == _arrow:
                Require(released.Status == StatusCode.Success && _arrowShot, "arrow released");
                _encounterArrowAmmo = _bowAmmo;
                return ShootArrow(requestId);
            case CommandResponseOp { ComponentId: BowComponent, CommandIndex: 4 } shoot when shoot.EntityId == _shoulderBow:
                if (_encounter is not null)
                {
                    Require(shoot.Status == StatusCode.Success && _bowAmmo == _encounterArrowAmmo - 1, "encounter arrow spent");
                    return ReportHit(requestId, _arrow, 120);
                }
                Require(shoot.Status == StatusCode.Success && _bowAmmo == 11, "one arrow spent");
                return ++_shotResponses == 1 ? ShootArrow(requestId) : StartAirChopsCooldown(requestId);
            case CommandResponseOp { ComponentId: GodstoneComponent, CommandIndex: 9 } cooldown when cooldown.EntityId == _characterId:
                Require(cooldown.Status == StatusCode.Success && cooldown.Data.Length == 0, "cooldown start acknowledged");
                break;
            case ComponentUpdateOp { ComponentId: GodstoneComponent } cooldown when cooldown.EntityId == _characterId:
                var cooldownReader = new SchemaReader(cooldown.Data);
                Require(cooldownReader.ReadFieldId() == 3 && cooldownReader.ReadObjectList().Count == 2 &&
                    !cooldownReader.HasMore, "starter cooldown state replicated");
                _cooldownUpdates++;
                return CastAirChop(requestId);
            case CommandResponseOp { ComponentId: Ids.ActorStatsComponent, CommandIndex: 15 } cast when cast.EntityId == _characterId:
                Require(cast.Status == StatusCode.Success && cast.Data.AsSpan().SequenceEqual(new byte[] { 1, 0, 1 }) &&
                    _projectile != 0 && _projectileInitialized, "Air Chops projectile matched the cast ID");
                if (_encounter is not null)
                    return ReportHit(requestId, _projectile, _airChopHitResponses == 0 ? 0 : 90, missed: _airChopHitResponses == 0);
                return WorldCommand(requestId, _projectile, ProjectileComponent, 4, []);
            case CommandResponseOp { ComponentId: ProjectileComponent, CommandIndex: 3 } hit:
                Require(hit.Status == StatusCode.Success && hit.Data.Length == 0, "projectile hit acknowledged");
                var target = _encounter!.Targets[_enemy];
                Require(target.Health == _expectedEnemyHealth, "hit, missed impact, or duplicate has the expected effect");
                if (_reviveStage == ReviveStage.Casting)
                {
                    _combatShotPending = false;
                    _enemyHealthBeforeHeal = target.Health;
                    _reviveStage = ReviveStage.Outside;
                    return ChallengeScenario.Move(_avatarId, (-207, -3.57, -16));
                }
                if (hit.EntityId == _projectile && _airChopHitResponses < 3)
                {
                    return ++_airChopHitResponses switch
                    {
                        1 => ReportHit(requestId, _projectile, 90),
                        2 => ReportHit(requestId, _projectile, 0),
                        _ => SpawnArrow(requestId),
                    };
                }
                Require(hit.EntityId == _arrow || hit.EntityId == _projectile, "hit from active test projectile");
                _combatShotPending = false;
                if (target.Health == 0) _enemy = 0;
                var waitForStagger = _encounter.GroveFight && !_encounter.ObservedStaggerRecovery && target.Staggered && target.Health > 0;
                _nextCombatShot = Environment.TickCount64 + (waitForStagger ? 2200 : 205);
                break;
            case ComponentUpdateOp { ComponentId: PublicActorStats } stats
                when _encounter?.Targets.ContainsKey(stats.EntityId) == true:
                _encounter.UpdateHealth(stats.EntityId, stats.Data);
                break;
            case ComponentUpdateOp { ComponentId: 4023 } activation
                when _encounter is { } encounter &&
                    _entities.GetValueOrDefault(activation.EntityId) == "NOScript" &&
                    activation.Data.AsSpan().SequenceEqual(new byte[] { 1, 0, 1 }):
                encounter.Clear();
                if (encounter.GroveFight)
                {
                    Require(encounter.ObservedStaggerRecovery, "enemy staggered and recovered during the fight");
                    Console.WriteLine("   [OK] Hits fill the stagger meter and the enemy recovers after the stagger");
                }
                break;
            case CommandResponseOp { ComponentId: ProjectileComponent, CommandIndex: 4 } ended when _challenges is not null:
                Require(ended.Status == StatusCode.Success && ended.EntityId == _projectile, "Air Chops ended");
                _airChopsEnded++;
                if (_airChopsEnded == 1) return _bowAmmo == 12 ? StartAirChopsCooldown(requestId) : null;
                Require(_cooldownUpdates == 2 && _bowAmmo == 12, "two Air Chops and one arrow recharge");
                Console.WriteLine("   [OK] Bow draw, nock, shot, ammo recharge and repeated Air Chops");
                return _challenges.RequestAreas(requestId);
            case ComponentUpdateOp { ComponentId: Ids.PlayerLifecycleComponent } life when life.EntityId == _characterId:
                return CheckLifeUpdate(life.Data, requestId);
            case ComponentUpdateOp { ComponentId: PublicActorStats } stats when stats.EntityId == _characterId:
                var health = new SchemaReader(stats.Data);
                var statField = health.ReadFieldId();
                if (statField is 7 or 8)
                {
                    ObserveExperience(ref health, statField);
                    break;
                }
                Require(statField == 9, "current health update");
                _health = health.ReadInt32();
                if (_maxHealth == 0)
                {
                    Require(health.ReadFieldId() == 29, "max health published with current health");
                    _maxHealth = health.ReadInt32();
                    Require(_maxHealth == _startingHealth && _maxHealth == _totalHealth, "HUD and total health agree");
                }
                if (_encounter is not null && (_health > 0 && _health < _maxHealth ||
                    _health == 0 && _reviveStage == ReviveStage.Ghost))
                {
                    Require(health.ReadFieldId() == 1001, "enemy attack sends a damage event");
                    if (_waitingForEnemyAttack && _receivedEnemyOrb && _receivedEnemyOrbLink)
                    {
                        var hitData = new SchemaReader(health.ReadBytesSpan());
                        Require(hitData.ReadFieldId() == 1 && hitData.ReadInt32() > 0 &&
                            hitData.ReadFieldId() == 2, "enemy damage event includes its source");
                        hitData.ReadBool();
                        Require(hitData.ReadFieldId() == 3, "enemy attacker field");
                        if (!long.TryParse(hitData.ReadOptionalString(), out var attacker) || attacker != _enemy) break;
                        _enemyAttacked = true;
                        var sniper = _encounter!.Targets[_enemy].Sniper;
                        Console.WriteLine(sniper
                            ? "   [OK] Sharpshooter starts Sniper Shot, links its orb, and deals damage"
                            : "   [OK] Gunner starts Rapid Shot, links its orb, and deals damage");
                    }
                    break;
                }
                Require(!health.HasMore && _health == (_lifeState == 1 ? 0 : _startingHealth), "health follows lifecycle state");
                break;
            case ComponentUpdateOp { ComponentId: Ids.ActorStatsComponent } stats when stats.EntityId == _characterId:
                CheckActorStats(stats.Data, initialized: true);
                break;
            case ComponentUpdateOp { ComponentId: InventoryComponent } items
                when items.EntityId == _characterId && _lootPickup == LootPickup.Claiming:
                var bag = new SchemaReader(items.Data);
                Require(bag.ReadFieldId() == 4 && bag.ReadListCount() == 1, "loot is added to the basic inventory bag");
                var lootEntry = new SchemaReader(bag.ReadBytesSpan());
                Require(!lootEntry.ReadBool() && lootEntry.ReadFieldId() == 1 && lootEntry.ReadInt32() == _startingLootCount &&
                    lootEntry.ReadFieldId() == 2, "loot inventory entry");
                var item = new SchemaReader(lootEntry.ReadBytesSpan());
                Require(item.ReadFieldId() == 1, "loot instance ID");
                _pickedUpInstance = item.ReadOptionalString();
                Require(_pickedUpInstance is not null && item.ReadFieldId() == 2 && item.ReadOptionalString() == _lootItem,
                    "inventory item matches the drop");
                break;
            case CommandResponseOp { ComponentId: 4001, CommandIndex: 1 } grabbed when grabbed.EntityId == _loot:
                Require(grabbed.Status == StatusCode.Success, "loot hand command acknowledged");
                if (_lootPickup == LootPickup.Grabbing)
                {
                    _lootPickup = LootPickup.TakingOwnership;
                    var own = new SchemaWriter();
                    own.AddEntityId(1, workerId, isOption: true);
                    own.AddBool(2, false);
                    own.AddString(3, characterId);
                    return WorldCommand(requestId, _loot, 2016, 1, own.ToArray());
                }
                Require(_lootPickup == LootPickup.Releasing, "loot release acknowledged");
                _lootPickup = LootPickup.Claiming;
                return ClaimLoot(requestId);
            case CommandResponseOp { ComponentId: 2016, CommandIndex: 1 } owned when owned.EntityId == _loot:
                Require(_lootPickup == LootPickup.TakingOwnership && owned.Status == StatusCode.Success &&
                    _authority.GetValueOrDefault(_loot)?.Contains(Ids.ZSyncSet) == true, "held loot transfers transform authority");
                _lootPickup = LootPickup.Releasing;
                return GrabLoot(requestId, held: false);
            case CommandResponseOp { ComponentId: LootComponent, CommandIndex: 1 } claim when claim.EntityId == _loot:
                if (_lootPickup == LootPickup.TooFar)
                {
                    Require(claim.Status == StatusCode.PermissionDenied, "loot cannot be picked up from out of reach");
                    _lootPickup = LootPickup.Moving;
                    _grabLootAt = Environment.TickCount64 + 200;
                    return ChallengeScenario.Move(_avatarId, _lootPosition);
                }
                if (_lootPickup == LootPickup.Claiming)
                {
                    var claimed = new SchemaReader(claim.Data);
                    Require(claim.Status == StatusCode.Success && claimed.ReadFieldId() == 1 && claimed.ReadListCount() == 1 &&
                        claimed.ReadString() == _pickedUpInstance && !claimed.HasMore, "loot claim returns the saved item instance");
                    _lootPickup = LootPickup.Duplicate;
                    return ClaimLoot(requestId);
                }
                Require(_lootPickup == LootPickup.Duplicate && claim.Status == StatusCode.NotFound &&
                    !_entities.ContainsKey(_loot), "claimed loot despawns and cannot be claimed twice");
                Console.WriteLine("   [OK] Enemy loot can be grabbed and saved to inventory once");
                _lootPickup = LootPickup.Done;
                break;
            case CommandResponseOp { ComponentId: Ids.PlayerLifecycleComponent, CommandIndex: 1 } response:
                Require(response.Status == StatusCode.Success && response.Data.Length == 0 && _lifeState == 1,
                    "recovery acknowledged before lifecycle changes");
                _recoveryAcknowledged = true;
                break;
            case ComponentUpdateOp { ComponentId: Ids.TeleportComponent } teleport when teleport.EntityId == _avatarId:
                var reader = new SchemaReader(teleport.Data);
                if (reader.ReadFieldId() == 1001)
                {
                    Require(_worldLoaded && teleport.Data.Length == 48, "teleport event after world loaded");
                    if (_reviveStage == ReviveStage.Requested)
                    {
                        var payload = new SchemaReader(reader.ReadBytesSpan());
                        Require(_lifeState == 2 && _health == 0 && payload.ReadFieldId() == 1, "ghost is recovering during quick teleport");
                        var destination = new SchemaReader(payload.ReadBytesSpan());
                        Require(destination.ReadFieldId() == 1 && Math.Abs(destination.ReadDouble() - GhostLocation.X) < 0.01 &&
                            destination.ReadFieldId() == 2 && Math.Abs(destination.ReadDouble() - GhostLocation.Y) < 0.01 &&
                            destination.ReadFieldId() == 3 && Math.Abs(destination.ReadDouble() - GhostLocation.Z) < 0.01 &&
                            payload.ReadFieldId() == 2 && !payload.ReadOptionHasValue() &&
                            payload.ReadFieldId() == 3 && payload.ReadBool(), "self-revive uses the tracked ghost location with quick teleport");
                        return WorldCommand(requestId, _avatarId, Ids.TeleportComponent, 1, [1, 0, 1]);
                    }
                    if (_teleportsCompleted is 1 or 2)
                    {
                        Require(_lifeState == 2 && _health == 0, "teleport while recovering");
                        var payload = new SchemaReader(reader.ReadBytesSpan());
                        Require(payload.ReadFieldId() == 1, "recovery teleport destination");
                        var destination = new SchemaReader(payload.ReadBytesSpan());
                        Require(destination.ReadFieldId() == 1, "destination X");
                        destination.ReadDouble();
                        Require(destination.ReadFieldId() == 2 && destination.ReadDouble() > -150,
                            "server chose an in-bounds destination");
                    }
                    if (_teleportsCompleted >= 3) _challenges?.CheckTeleport(reader.ReadBytesSpan(), recovering: _lifeState == 2);
                    return WorldCommand(requestId, _avatarId, Ids.TeleportComponent, 1, [1, 0, 1]);
                }
                Require(_positionAccepted && teleport.Data.Length == 24, "confirmed teleport position update");
                if (_reviveStage == ReviveStage.Requested) break; // The stock quick teleport sends no command 4.
                return WorldCommand(requestId, _avatarId, Ids.TeleportComponent, 4, [1, 0, 1]);
            case CommandResponseOp { ComponentId: Ids.TeleportComponent, CommandIndex: 1 } position:
                Require(position.Status == StatusCode.Success && position.Data.AsSpan().SequenceEqual(new byte[] { 1, 0, 1 }),
                    "position confirmation response");
                _positionAccepted = true;
                break;
            case CommandResponseOp { ComponentId: Ids.TeleportComponent, CommandIndex: 4 } finished:
                Require(finished.Status == StatusCode.Success && finished.Data.Length == 0, "teleport finished response");
                _teleportsCompleted++;
                if (_teleportsCompleted == 1)
                {
                    if (_experience > 0 || _startingLootCount > 0)
                        Console.WriteLine($"   [OK] Loaded level {_level}, {_experience} XP and {_startingLootCount} saved inventory items");
                    return _shop is not null ? _shop.Start(requestId, _characterId) : GlideEffect(requestId, 1);
                }
                Require(_lifeState == 3 && _health == _maxHealth && _maxHealth == _startingHealth,
                    "alive and fully healed after recovery");
                if (_teleportsCompleted == 2) return FallBelowWorld(useZSync: false);
                if (_challenges is not null)
                {
                    _positionAccepted = false;
                    if (_teleportsCompleted == 3) return DrawBow(requestId);
                    var next = _challenges.FinishTeleport(_avatarId);
                    Completed = next is null;
                    if (Completed) Require(_leveledUp && _lootPickup == LootPickup.Done, "challenge rewards and level-up completed");
                    return next;
                }
                Completed = true;
                break;
            case CommandResponseOp { ComponentId: VfxComponent, CommandIndex: 1 or 2 } effect:
                Require(effect.Status == StatusCode.Success && effect.Data.Length == 0, "glide effect acknowledgment");
                break;
            case ComponentUpdateOp { ComponentId: VfxComponent } effect when effect.EntityId == _characterId:
                var eventReader = new SchemaReader(effect.Data);
                var eventId = eventReader.ReadFieldId();
                Require(eventId is 1001 or 1002, "glide effect event ID");
                Require(eventReader.ReadBytesSpan().SequenceEqual(GlideEffect(requestId, eventId - 1000).Data) &&
                    !eventReader.HasMore, "glide effect event matches the command payload");
                return eventId == 1001 ? GlideEffect(requestId, 2) : FallBelowWorld(useZSync: true);
        }
        return null;
    }

    private void CheckStarterEquipment(byte[] data)
    {
        var reader = new SchemaReader(data);
        Require(reader.ReadFieldId() == 1, "equipment entries field");
        foreach (var raw in reader.ReadObjectList())
        {
            var row = new SchemaReader(raw);
            Require(row.ReadFieldId() == 1, "equipment slot field");
            if (row.ReadInt32() != 18) continue;
            Require(row.ReadFieldId() == 2, "shoulder item field");
            var item = new SchemaReader(row.ReadBytesSpan());
            Require(item.ReadFieldId() == 1 && item.ReadOptionalString() is not null, "bow instance ID");
            _starterBowEquipped = item.ReadFieldId() == 2 && item.ReadOptionalString() == "weapon_bow_reimagined_starter";
        }
    }

    private void CheckStarterAbilities(byte[] data)
    {
        var reader = new SchemaReader(data);
        Require(reader.ReadFieldId() == 1 && reader.ReadListCount() == 0, "starter etherdisks");
        Require(reader.ReadFieldId() == 2, "active etherdisk field");
        reader.ReadBytesSpan();
        Require(reader.ReadFieldId() == 3 && reader.ReadObjectList().Count == 2, "starter cooldowns");
        Require(reader.ReadFieldId() == 4 && reader.ReadListCount() == 2, "starter ability list");
        _starterAbilities = reader.ReadString() == "godstone_airChops" && reader.ReadString() == "godstone_arrow_reimagined_base";
    }

    private static long ReadShoulderParent(byte[] data)
    {
        var reader = new SchemaReader(data);
        Require(reader.ReadFieldId() == 1, "bow location field");
        reader.ReadBytesSpan();
        Require(reader.ReadFieldId() == 2, "bow rotation field");
        reader.ReadUInt32();
        Require(reader.ReadFieldId() == 3, "bow scale field");
        reader.ReadBytesSpan();
        Require(reader.ReadFieldId() == 4 && reader.ReadOptionHasValue(), "bow shoulder parent");
        var parent = new SchemaReader(reader.ReadBytesSpan());
        Require(parent.ReadFieldId() == 1, "bow avatar parent field");
        return parent.ReadInt64();
    }

    private CommandRequestOp DrawBow(long requestId)
    {
        Require(_shoulderBow != 0 && _starterBowEquipped && _starterAbilities && _starterStatusEffects && _maxHealth > 0,
            "starter bow, abilities and status effects initialized");
        var draw = new SchemaWriter();
        draw.AddFloat(1, 0.5f);
        draw.AddFloat(2, -0.25f);
        return WorldCommand(requestId, _shoulderBow, BowComponent, 1, draw.ToArray());
    }

    private CommandRequestOp CastAirChop(long requestId)
    {
        _castId = Guid.NewGuid();
        _projectile = 0;
        _projectileInitialized = false;
        var request = new SchemaWriter();
        request.AddString(1, "godstone_airChops");
        request.AddObject(2, info =>
        {
            info.AddEmptyList(1);
            info.AddFloat(2, 0.5f);
            info.AddBytes(3, []);
            info.AddString(4, null);
            info.AddString(5, null);
            info.AddBool(6, false);
            info.AddString(7, _castId.ToString());
            info.AddInt32(8, 0);
            info.AddString(0, null);
            info.AddBytes(9, []);
            info.AddObject(10, hand => hand.AddObject(1, position =>
            {
                position.AddInt32(1, 2_653_800);
                position.AddInt32(2, 799_700);
                position.AddInt32(3, 2_649_300);
            }));
            info.AddEmptyList(11);
            info.AddEmptyList(12);
        });
        return WorldCommand(requestId, _characterId, Ids.ActorStatsComponent, 15, request.ToArray());
    }

    private CommandRequestOp StartAirChopsCooldown(long requestId)
    {
        var cooldown = new SchemaWriter();
        cooldown.AddUInt32(1, 1);
        return WorldCommand(requestId, _characterId, GodstoneComponent, 9, cooldown.ToArray());
    }

    public ZfOp? PollEncounter(long requestId)
    {
        if (_encounter is not { } encounter || encounter.Added < encounter.Current.OpeningCount || _combatShotPending) return null;
        var now = Environment.TickCount64;
        if (revive) return PollRevive(encounter, requestId, now);
        if (_lootPickup == LootPickup.Moving && now >= _grabLootAt)
        {
            _lootPickup = LootPickup.Grabbing;
            return GrabLoot(requestId, held: true);
        }
        if (_lootPickup is not (LootPickup.None or LootPickup.Done)) return null;
        if (encounter.ClearedAt != 0 && now >= encounter.ClearedAt + (encounter.GroveFight ? 3500 : 0))
        {
            encounter.NextStop();
            _enemy = 0;
            if (!encounter.Completed) return ChallengeScenario.Move(_avatarId, encounter.Current.Position);
            _encounter = null;
            return _challenges!.FinishEncounter(_avatarId);
        }
        if (now < _nextCombatShot || encounter.ClearedAt != 0) return null;
        if (_enemy == 0)
        {
            var checkGunner = encounter.GroveFight && !_testedGunnerAttack;
            var checkSniper = encounter.GroveSniperFight && !_testedSniperAttack;
            var next = encounter.Targets.FirstOrDefault(entry => entry.Value.Health > 0 &&
                (!checkGunner || entry.Value.Prefab.EndsWith("-Gunner", StringComparison.Ordinal)) &&
                (!checkSniper || entry.Value.Sniper));
            if (next.Key == 0) return null;
            _enemy = next.Key;
            var (x, y, z) = next.Value.Position;
            _nextCombatShot = now + 170;
            if (encounter.GroveFight && !_testedEnemyOwnership)
            {
                _combatShotPending = true;
                return new DeleteEntityRequestOp { RequestId = requestId, EntityId = _enemy };
            }
            _waitingForEnemyAttack = checkGunner || checkSniper;
            if (_waitingForEnemyAttack) _enemyAttacked = _receivedEnemyOrb = _receivedEnemyOrbLink = false;
            return _waitingForEnemyAttack
                ? ChallengeScenario.Move(_avatarId, (x + 6, y, z))
                : ChallengeScenario.Move(_avatarId, (x, y + 13, z));
        }
        if (_waitingForEnemyAttack)
        {
            if (!_enemyAttacked) return null;
            _waitingForEnemyAttack = false;
            if (encounter.Targets[_enemy].Sniper) _testedSniperAttack = true;
            else _testedGunnerAttack = true;
            _nextCombatShot = now + 170;
            var (x, y, z) = encounter.Targets[_enemy].Position;
            return ChallengeScenario.Move(_avatarId, (x, y + 13, z));
        }
        _combatShotPending = true;
        return CastAirChop(requestId);
    }

    private CommandRequestOp ClaimLoot(long requestId)
    {
        var claim = new SchemaWriter();
        claim.AddEntityId(1, _characterId);
        claim.AddBool(2, true);
        return WorldCommand(requestId, _loot, LootComponent, 1, claim.ToArray());
    }

    private CommandRequestOp GrabLoot(long requestId, bool held)
    {
        var grab = new SchemaWriter();
        grab.AddObject(1, state =>
        {
            state.AddBool(1, held);
            if (held) state.AddEntityId(2, Find("RightHand"), isOption: true);
            else state.AddEmptyOption(2);
            state.AddString(3, workerId.ToString());
            if (held) state.AddEntityId(4, _characterId, isOption: true);
            else state.AddEmptyOption(4);
            state.AddEmptyOption(5);
        });
        return WorldCommand(requestId, _loot, 4001, 1, grab.ToArray());
    }

    private void ObserveExperience(ref SchemaReader update, uint firstField)
    {
        var previousLevel = _level;
        var levelUp = firstField == 7;
        if (levelUp)
        {
            _level = update.ReadInt32();
            Require(_level > previousLevel && update.ReadFieldId() == 8, "level rises with earned XP");
        }
        var earned = update.ReadInt32();
        Require(earned > _experience, "defeat awards positive XP");
        if (levelUp)
        {
            Require(update.ReadFieldId() == 9, "level up publishes current health");
            _health = update.ReadInt32();
        }
        Require(update.ReadFieldId() == 19 && update.ReadInt32() == earned, "role XP mirrors earned XP");
        if (levelUp)
        {
            Require(update.ReadFieldId() == 29, "level up publishes max health");
            var maximum = update.ReadInt32();
            Require(maximum >= _maxHealth && maximum == _totalHealth && _health <= maximum,
                "level up health agrees with the new base stats");
            _startingHealth = _maxHealth = maximum;
            Require(update.ReadFieldId() == 32 && update.ReadFloat() > 0 &&
                update.ReadFieldId() == 33 && update.ReadInt32() == _level, "effective level and attack stats update");
        }
        Require(update.ReadFieldId() == 1004, "XP popup event");
        var experience = new SchemaReader(update.ReadBytesSpan());
        Require(experience.ReadFieldId() == 1 && experience.ReadInt32() == earned - _experience &&
            experience.ReadFieldId() == 2 && experience.ReadInt32() == earned &&
            experience.ReadFieldId() == 3 && long.TryParse(experience.ReadOptionalString(), out var enemy) &&
            _encounter?.Targets.GetValueOrDefault(enemy)?.Health == 0,
            "XP event reports the amount and defeated enemy");
        _experience = earned;
        if (levelUp)
        {
            Require(update.ReadFieldId() == 1005, "level-up event");
            var level = new SchemaReader(update.ReadBytesSpan());
            Require(level.ReadFieldId() == 1 && level.ReadInt32() == previousLevel &&
                level.ReadFieldId() == 2 && level.ReadInt32() == _level, "level-up event reports both levels");
            _leveledUp = true;
            Console.WriteLine($"   [OK] Reached level {_level} at {_experience} XP with {_maxHealth} max health");
        }
        Require(!update.HasMore, "complete experience update");
    }

    private ZfOp? PollRevive(EncounterScenario encounter, long requestId, long now)
    {
        switch (_reviveStage)
        {
            case ReviveStage.Opening:
                _enemy = encounter.Targets.First(entry => entry.Value.Prefab.EndsWith("-Gunner", StringComparison.Ordinal)).Key;
                _airChopHitResponses = 3; // Use one normal, nonlethal hit.
                _reviveStage = ReviveStage.Casting;
                _nextCombatShot = now + 200;
                return ChallengeScenario.Move(_avatarId, (-251.3, -3.57, -19));
            case ReviveStage.Casting when now >= _nextCombatShot:
                _combatShotPending = true;
                return CastAirChop(requestId);
            case ReviveStage.Outside when encounter.Targets[_enemy].Health > _enemyHealthBeforeHeal && _aggroEnemies.Count == 0:
                Console.WriteLine("   [OK] Wounded enemy regenerates when the living player leaves the arena");
                _reviveStage = ReviveStage.WaitingForDeath;
                return ChallengeScenario.Move(_avatarId, (-251.3, -3.57, -19));
            case ReviveStage.Ghost when now >= _defeatedAt + 3000 && encounter.Targets[_enemy].Health > _enemyHealthBeforeHeal:
                Require(_lifeState == 1 && _health == 0 && _aggroEnemies.Count == 0, "ghost remains downed and ignored until self-revive");
                Console.WriteLine("   [OK] Downed player can move, remains ignored, and does not stop enemy regeneration");
                _reviveStage = ReviveStage.Requested;
                return RequestRecovery(requestId);
            default:
                return null;
        }
    }

    private CommandRequestOp ReportHit(long requestId, long projectile, int damage, bool missed = false)
    {
        var target = _encounter!.Targets[_enemy];
        var (x, y, z) = target.Position;
        _expectedEnemyHealth = Math.Max(0, target.Health - damage);
        var hit = new SchemaWriter();
        hit.AddBool(1, true);
        hit.AddEntityId(2, _enemy);
        hit.AddObject(3, impact =>
        {
            impact.AddInt32(1, (int)((x + (missed ? 20 : 0)) * 100_000));
            impact.AddInt32(2, (int)(y * 100_000));
            impact.AddInt32(3, (int)(z * 100_000));
        });
        hit.AddFloat(4, 8);
        return WorldCommand(requestId, projectile, ProjectileComponent, 3, hit.ToArray());
    }

    private CommandRequestOp SpawnArrow(long requestId)
    {
        _arrowCastId = Guid.NewGuid();
        var arrow = new SchemaWriter();
        arrow.AddString(3, _arrowCastId.ToString());
        arrow.AddBool(4, true);
        arrow.AddString(5, "Projectile-BoStaffArrow-Reimagined-Base");
        return WorldCommand(requestId, _shoulderBow, BowComponent, 2, arrow.ToArray());
    }

    private CommandRequestOp ShootArrow(long requestId)
    {
        var shot = new SchemaWriter();
        shot.AddInt32(1, 1);
        shot.AddBool(2, true);
        shot.AddBool(3, true);
        shot.AddFloat(4, 5);
        shot.AddInt32(5, 3);
        shot.AddInt32(6, 2);
        return WorldCommand(requestId, _shoulderBow, BowComponent, 4, shot.ToArray());
    }

    private ZfOp? CheckLifeUpdate(byte[] data, long requestId)
    {
        var reader = new SchemaReader(data);
        Require(reader.ReadFieldId() == 1, "lifecycle state field");
        var state = reader.ReadUInt32();
        Require(state == (_lifeState % 3) + 1, "downed, recovering, then alive");
        _lifeState = state;
        if (state == 2)
        {
            Require(!reader.HasMore && (_teleportsCompleted != 1 || _recoveryAcknowledged), "recovering state update");
            return null;
        }

        var eventField = reader.ReadFieldId();
        if (eventField == 2)
        {
            Require(reader.ReadOptionHasValue() == (state == 1), "death tether is set on combat defeat and cleared on revive");
            if (state == 1) Require(reader.ReadBytesSpan().Length == 18, "death tether records the defeat position");
            eventField = reader.ReadFieldId();
        }
        Require(eventField == (state == 1 ? 1001 : 1002), "captured lifecycle event ID");
        var evt = new SchemaReader(reader.ReadBytesSpan());
        var combatDeath = state == 1 && _reviveStage == ReviveStage.WaitingForDeath;
        Require(evt.ReadFieldId() == (state == 1 ? 1 : 2) && evt.ReadBool() == (state == 1 && !combatDeath) &&
            !evt.HasMore && !reader.HasMore, "captured lifecycle event payload");
        if (combatDeath)
        {
            _reviveStage = ReviveStage.Ghost;
            _defeatedAt = Environment.TickCount64;
            _enemyHealthBeforeHeal = _encounter!.Targets[_enemy].Health;
            return ChallengeScenario.Move(_avatarId, GhostLocation);
        }
        if (state != 1 || _teleportsCompleted != 1) return null;

        return RequestRecovery(requestId);
    }

    private CommandRequestOp RequestRecovery(long requestId)
    {
        var request = new SchemaWriter();
        request.AddObject(1, proposed => proposed.AddObject(1, destination =>
        {
            destination.AddDouble(1, 0);
            destination.AddDouble(2, -151);
            destination.AddDouble(3, 0);
        }), isOption: true);
        return WorldCommand(requestId, _characterId, Ids.PlayerLifecycleComponent, 1, request.ToArray());
    }

    private CommandRequestOp GlideEffect(long requestId, uint index)
    {
        const string leftGlide = "glide-left-hand";
        var writer = new SchemaWriter();
        if (index == 1)
        {
            writer.AddObject(1, location =>
            {
                location.AddObject(1, coordinates =>
                {
                    for (uint field = 1; field <= 3; field++) coordinates.AddInt32(field, 0);
                });
                location.AddEmptyOption(2);
                location.AddEmptyOption(3);
                location.AddEntityId(4, Find("LeftHand"), isOption: true);
                for (uint field = 5; field <= 9; field++) location.AddEmptyOption(field);
            });
            writer.AddInt32(2, 99);
            writer.AddString(3, "gliding");
            writer.AddBool(4, false);
            writer.AddString(5, leftGlide);
            writer.AddEntityId(6, _characterId);
            writer.AddFloat(7, 1);
            writer.AddInt64(8, -1);
        }
        else
        {
            writer.AddInt32(1, 99);
            writer.AddString(2, leftGlide);
            writer.AddEntityId(3, _characterId);
        }
        return WorldCommand(requestId, _characterId, VfxComponent, index, writer.ToArray());
    }

    private ComponentUpdateOp FallBelowWorld(bool useZSync)
    {
        _positionAccepted = false;
        const float height = -151;
        var zsync = new SchemaWriter();
        zsync.AddObject(1, location =>
        {
            location.AddFloat(1, 0);
            location.AddFloat(2, height);
            location.AddFloat(3, 0);
        });
        return new ComponentUpdateOp
        {
            EntityId = _avatarId,
            ComponentId = useZSync ? Ids.ZSyncComponent : Ids.Position,
            Data = useZSync ? zsync.ToArray() : StandardComponents.Position(0, height, 0),
        };
    }

    private static int ReadInitialMaxHealth(ref SchemaReader reader)
    {
        foreach (var field in new[] { 18, 19, 21, 22 })
        {
            Require(reader.ReadFieldId() == field, "initial public combat stats");
            reader.ReadInt32(); // Integers and floats both occupy four bytes.
        }
        Require(reader.ReadFieldId() == 25 && reader.ReadListCount() == 0, "initial status effects");
        Require(reader.ReadFieldId() == 26, "new-spawn flag");
        reader.ReadBool();
        Require(reader.ReadFieldId() == 28, "max stagger duration");
        reader.ReadFloat();
        Require(reader.ReadFieldId() == 29, "initial max health field");
        return reader.ReadInt32();
    }

    private void CheckActorStats(byte[] data, bool initialized)
    {
        var reader = new SchemaReader(data);
        Require(reader.ReadFieldId() == 10, "current rage");
        var rage = reader.ReadInt32();
        Require(reader.ReadFieldId() == 12, "current essence");
        reader.ReadInt32();
        var next = reader.ReadFieldId();
        var includesBase = next == 13;
        if (includesBase)
        {
            var baseCount = reader.ReadListCount();
            for (var i = 0; i < baseCount; i++) reader.ReadBytesSpan();
            next = reader.ReadFieldId();
        }
        else Require(initialized, "base stats at spawn");

        Require(next == 15, "total stats");
        var totalCount = reader.ReadListCount();
        if (!initialized)
        {
            Require(rage == 0 && totalCount == 0, "effective resources and stats start unset");
            return;
        }

        float? moveSpeed = null, stamina = null, maxRage = null;
        for (var i = 0; i < totalCount; i++)
        {
            var stat = new SchemaReader(reader.ReadBytesSpan());
            Require(stat.ReadFieldId() == 1, "stat key");
            var key = stat.ReadInt32();
            Require(stat.ReadFieldId() == 2, "stat value");
            var value = stat.ReadFloat();
            if (key == 4) _totalHealth = value;
            if (key == 5) maxRage = value;
            if (key == 9) moveSpeed = value;
            if (key == 11) stamina = value;
        }
        Require(moveSpeed == 10, "neutral movement multiplier");
        Require(stamina > 0, "positive maximum stamina");
        Require((includesBase || _totalHealth == _startingHealth) && !reader.HasMore, "effective stats published at world entry");
        Require(challenges ? rage > 0 && rage == maxRage : rage == 0, "starting rage matches the class");
    }

    private void CheckGraph(byte[] data)
    {
        Require(_entities.Count == 5 + (challenges ? ChallengeScenario.MapCount : 0), "spawned character rig and its world");
        _challenges?.CheckPreloadedMaps();
        var reader = new SchemaReader(data);
        var character = ReadEntityId(ref reader, 1);
        var avatar = ReadEntityId(ref reader, 2);
        _characterId = character;
        _avatarId = avatar;
        Require(ReadEntityId(ref reader, 3) == playerId && !reader.HasMore, "player link");
        Require(_entities.GetValueOrDefault(character) == "PlayerCharacter", "character link");
        Require(_characterWithInterest == character, "character has the Interest component before avatar setup");
        Require(_entities.GetValueOrDefault(avatar) == "PlayerAvatar", "avatar link");
        Require(_authority[character].SetEquals([Ids.PlayerCharacterClientSet]), "character authority");
        Require(_authority[avatar].SetEquals([Ids.PlayerAvatarClientSet, Ids.PositionSet, Ids.ZSyncSet]), "avatar authority");
        Require(_authority[Find("Head")].SetEquals([Ids.PositionSet, Ids.ZSyncSet]), "head authority");
        Require(_authority[Find("LeftHand")].SetEquals([Ids.PlayerHandClientSet, Ids.PositionSet, Ids.ZSyncSet]), "left hand authority");
        Require(_authority[Find("RightHand")].SetEquals([Ids.PlayerHandClientSet, Ids.PositionSet, Ids.ZSyncSet]), "right hand authority");
    }

    private static void CheckBodyParts(ref SchemaReader data, long left, long right, long head)
    {
        Require(data.ReadFieldId() == 2, "avatar body parts field");
        var parts = new SchemaReader(data.ReadBytesSpan());
        Require(parts.ReadFieldId() == 1 && parts.ReadInt64() == left, "avatar left hand");
        Require(parts.ReadFieldId() == 2 && parts.ReadInt64() == right, "avatar right hand");
        Require(parts.ReadFieldId() == 3 && parts.ReadInt64() == head && !parts.HasMore && !data.HasMore, "avatar head");
    }

    private CommandRequestOp RegisterHands(long requestId, long left, long right)
    {
        var writer = new SchemaWriter();
        writer.AddEntityId(1, left, isOption: true);
        writer.AddEntityId(2, right, isOption: true);
        return WorldCommand(requestId, _characterId, Ids.NetworkedObjectInteractionComponent, 2, writer.ToArray());
    }

    private long Find(string type) => _entities.Single(entry => entry.Value == type &&
        entry.Key >= _characterId && entry.Key < _characterId + 5).Key;

    private static CommandRequestOp WorldCommand(long requestId, long entity, uint component, uint command, byte[] data) =>
        new() { RequestId = requestId, EntityId = entity, ComponentId = component, CommandIndex = command, Data = data };

    private CommandRequestOp Request(long requestId, string selectedId, string sessionTicket)
    {
        var writer = new SchemaWriter();
        writer.AddString(1, accountId);
        writer.AddString(2, selectedId);
        writer.AddUInt32(6, challenges ? 4u : 3u);
        writer.AddString(9, "DPS");
        writer.AddString(3, name);
        writer.AddUInt32(4, 1); // Windows Steam
        writer.AddString(5, null);
        writer.AddUInt64(7, 0);
        writer.AddString(8, null);
        writer.AddString(10, null);
        writer.AddString(11, null);
        writer.AddEmptyList(12);
        writer.AddEmptyList(13);
        writer.AddString(14, sessionTicket);
        writer.AddEmptyOption(15);
        return new()
        {
            RequestId = requestId, EntityId = 4, ComponentId = Ids.CharacterSpawnerComponent,
            CommandIndex = 3, Data = writer.ToArray(),
        };
    }

    private static long ReadEntityId(ref SchemaReader reader, uint field)
    {
        Require(reader.ReadFieldId() == field, $"entity link field {field}");
        return long.Parse(reader.ReadOptionalString()!);
    }

    private static void Require(bool valid, string detail)
    {
        if (!valid) throw new InvalidDataException($"Unexpected spawn data: {detail}");
    }
}
