using System.Globalization;
using System.Text.Json;
using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;
using ZenithServer.Accounts;

namespace ZenithServer.ZenFabric;

/// <summary>Character selection commands on entity 4, component 2010.</summary>
public sealed class CharacterCommands(AccountStore accounts, ServerOptions options, EnemyLootOptions lootOptions, TimeProvider clock,
    ILogger<CharacterCommands> log)
{
    private static readonly string[] Classes = ["", "blade_master", "essence_mage", "cyber_ninja", "classless"];
    private static readonly string[] Roles = ["DPS", "TANK", "SUPPORT", "CROSS_ROLE"];
    private readonly CharacterProgression _progression = new();
    private readonly Dictionary<long, ActiveSpawn> _activeSpawns = new();
    private readonly EnemyLootTable _loot = new(lootOptions);
    private readonly DailyRotation _daily = new(clock);
    private readonly EnemyEncounters _enemies = new(options.ZenFabric.IslandMinibossChance,
        lootOptions.RegularDropEvery, log);

    private enum LifeState : uint { Downed = 1, Recovering = 2, Alive = 3 }

    private sealed record ActiveSpawn(string CharacterId, uint CharacterType, uint Role, long PlayerId, long[] Entities,
        (float X, float Y, float Z) Position, Dictionary<WorldEntry.Area, long> AreaEntities, int StartingHealth)
    {
        public long CharacterEntityId => Entities[0];
        public long AvatarId => Entities[1];
        public long HeadId => Entities[2];
        public long LeftHandId => Entities[3];
        public long RightHandId => Entities[4];
        public long RegisteredLeftHand { get; set; }
        public long RegisteredRightHand { get; set; }
        public bool AvatarReady { get; set; }
        public bool WorldLoaded { get; set; }
        public bool SkipLoginTeleport { get; set; }
        public (float X, float Y, float Z)? TeleportDestination { get; set; }
        public bool QuickTeleport { get; set; }
        public bool PositionConfirmed { get; set; }
        public bool WorldEntered { get; set; }
        public LifeState Life { get; set; } = LifeState.Alive;
        public int Health { get; set; } = StartingHealth;
        public int MaxHealth { get; set; } = StartingHealth;
        public int Experience { get; set; }
        public int Level { get; set; }
        public int PendingExperience { get; set; }
        public long ExperienceSource { get; set; }
        public long RetryExperienceAt { get; set; }
        public Dictionary<long, EnemyLoot> LootDrops { get; } = [];
        public long CheckpointRecoveryAt { get; set; }
        public (float X, float Y, float Z)? DeathTether { get; set; }
        public WorldEntry.Area? Area { get; set; } = AreaEntities.Count == 0 ? null : WorldEntry.Hub;
        public WorldEntry.Area? DailyArea { get; set; } = AreaEntities.Keys.FirstOrDefault(area => area.Daily is not null);
        public long? ShoulderBowId { get; set; }
        public StoredLootItem? EquippedBow { get; set; }
        public long[] GrappleIds { get; set; } = [];
        public CosmeticShop.Checkout? CosmeticCheckout { get; set; }
        public float BowDraw { get; set; }
        public float BowKnock { get; set; }
        public int BowAmmo { get; set; } = StarterWeapons.MaxAmmo;
        public long NextArrowRecharge { get; set; }
        public ActiveArrow? NockedArrow { get; set; }
        public Dictionary<Guid, ActiveArrow> Arrows { get; } = new();
        public bool SpawnStatsSent { get; set; }
        public (double X, double Y, double Z) CurrentPosition { get; set; } = Position;
        public long NextAirChop { get; set; }
        public Dictionary<Guid, ActiveProjectile> AirChops { get; } = new();
        public long? AreaEntityId => Area is { } area ? AreaEntities[area] : null;
        public (float X, float Y, float Z)? Checkpoint { get; set; }
        public long[] AggroTargets { get; set; } = [];
        public (float X, float Y, float Z) RecoveryPosition => Checkpoint ?? Area?.Spawn ?? CharacterEntities.DefaultSpawnPosition(false);
    }

    private class ActiveProjectile(long entityId, long expires)
    {
        public long EntityId { get; } = entityId;
        public long Expires { get; set; } = expires;
        public (double X, double Y, double Z)? LaunchPosition { get; set; }
        public bool Hit { get; set; }
    }

    private sealed class ActiveArrow(Guid cast, long entityId, long expires) : ActiveProjectile(entityId, expires)
    {
        public Guid Cast { get; } = cast;
        public bool Shot { get; set; }
    }

    public void OnAvatarMovement(ZenFabricRuntime rt, WorkerConnection caller, ComponentUpdateOp update)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) || update.EntityId != spawn.AvatarId ||
            !spawn.WorldEntered || spawn.TeleportDestination is not null || spawn.Life == LifeState.Recovering) return;

        var location = update.ComponentId == Ids.Position
            ? StandardComponents.ReadPosition(update.Data)
            : WorldEntry.ReadZSyncPosition(update.Data);
        if (location is not { } position || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z)) return;
        spawn.CurrentPosition = position;
        if (spawn.Life == LifeState.Downed) return;
        if (position.Y >= WorldEntry.OutOfBoundsY)
        {
            if (spawn.Area is { } area)
            {
                if (WorldEntry.GateDestination(area, spawn.DailyArea, position) is { } destination)
                    EnterArea(rt, caller, spawn, destination);
                else if (_enemies.IsFinaleExit(caller, position))
                    EnterArea(rt, caller, spawn, WorldEntry.Hub, teleportToSpawn: true);
                else if (EncounterCatalog.CheckpointAt(area, position) is { } checkpoint)
                    spawn.Checkpoint = checkpoint;
            }
            return;
        }
        if (rt.Entities.Get(spawn.CharacterEntityId) is not { } character) return;

        // A fall out of the map forces checkpoint recovery. Combat deaths wait for the player's request.
        DownPlayer(rt, spawn, character, forceCheckpoint: true);
        SetHealth(rt, spawn, character, 0);
        log.LogInformation("Character {CharacterId} fell below the world at Y={Height:F1}", character.Id, position.Y);
    }

    public bool RecoverCharacter(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity character)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) || spawn.CharacterEntityId != character.Id)
        {
            rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Recovery requires the owning character");
            return true;
        }

        try
        {
            var reader = new SchemaReader(req.Data);
            RequireField(ref reader, 1);
            // The payload includes a proposed position and health. Use the last tracked avatar position.
            if (reader.ReadOptionHasValue()) reader.ReadBytesSpan();
            RequireEnd(ref reader);
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
            return true;
        }

        rt.SendCommandResponse(caller, req, []);
        BeginRecovery(rt, spawn, character);
        return true;
    }

    public bool HandleCosmeticShop(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity character)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) || spawn.CharacterEntityId != character.Id ||
            caller.Session is not { } session || accounts.FindById(session.PlayFabId) is not { } account ||
            accounts.ListCharacters(account).FirstOrDefault(c => c.Id == spawn.CharacterId) is not { } stored)
            return false;

        try
        {
            if (req.CommandIndex == CosmeticShop.StartSession)
            {
                CosmeticShop.ReadSessionRequest(req.Data);
                var balance = spawn.CharacterType == 4 ? accounts.ReadCosmeticWallet(account).Balance : 0;
                rt.SendCommandResponse(caller, req, []);
                PublishWallet(rt, character, balance);
                return true;
            }

            var checkout = req.CommandIndex == CosmeticShop.StartTransaction
                ? CosmeticShop.ReadTransactionStart(req.Data) : CosmeticShop.ReadCheckout(req.Data);
            if (!spawn.WorldEntered || spawn.Area != WorldEntry.Hub || spawn.Life != LifeState.Alive ||
                spawn.TeleportDestination is not null)
            {
                rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Cosmetics are available at the Neo City kiosk");
                return true;
            }
            if (!CosmeticShop.TryGetPrice(checkout.ItemId, out var price))
            {
                rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Unknown kiosk cosmetic");
                return true;
            }
            if (req.CommandIndex == CosmeticShop.StartTransaction)
            {
                spawn.CosmeticCheckout = checkout;
                var balance = accounts.ReadCosmeticWallet(account).Balance;
                rt.SendCommandResponse(caller, req, CosmeticShop.CheckoutResult(checkout, price, balance, balance, state: 1));
                return true;
            }
            if (spawn.CosmeticCheckout is not { } pending || pending.Id != checkout.Id || pending.ItemId != checkout.ItemId)
            {
                rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Start a kiosk transaction before checkout");
                return true;
            }

            var (before, wallet, succeeded, purchased) = accounts.BuyCosmetic(account, checkout.ItemId!, price);
            if (purchased)
            {
                PublishWallet(rt, character, wallet.Balance);
                var locations = CosmeticShop.Locations(stored, wallet.Items);
                var position = locations.Single(entry => entry.Value == wallet.Items.Length - 1).Key;
                CosmeticShop.PublishInventory(rt, character, stored, account.PlayFabId, wallet.Items, locations, [position]);
                CosmeticShop.PublishOwnership(rt, character, wallet.Items);
            }
            rt.SendCommandResponse(caller, req, CosmeticShop.CheckoutResult(checkout, price, before, wallet.Balance,
                succeeded ? 7 : -1, succeeded ? null : "Insufficient Zennies"));
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        catch (IOException ex)
        {
            log.LogError(ex, "Could not save cosmetic purchase for worker {WorkerId}", caller.WorkerEntityId);
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Could not save cosmetic purchase");
        }
        return true;
    }

    public bool MoveInventory(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity character)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) || spawn.CharacterEntityId != character.Id ||
            spawn.CharacterType != 4 || caller.Session is not { } session ||
            accounts.FindById(session.PlayFabId) is not { } account ||
            accounts.ListCharacters(account).FirstOrDefault(c => c.Id == spawn.CharacterId) is not { } stored)
            return false;

        try
        {
            var move = CharacterGear.ReadMove(req.Data);
            var wallet = accounts.ReadCosmeticWallet(account);
            var success = false;
            if (CosmeticShop.Position(move.From) is { } cosmeticFrom && CosmeticShop.Position(move.To) is { } cosmeticTo)
            {
                var locations = CosmeticShop.Locations(stored, wallet.Items);
                success = CosmeticShop.TryMove(cosmeticFrom, cosmeticTo, move.AllowSwap, locations, wallet.Items);
                if (success && cosmeticFrom != cosmeticTo)
                {
                    var updated = accounts.UpdateCharacter(account, stored.Id,
                        current => current with { CosmeticLocations = locations })!;
                    CosmeticShop.PublishInventory(rt, character, updated, account.PlayFabId, wallet.Items, locations,
                        [cosmeticFrom, cosmeticTo]);
                }
            }
            else if (CharacterGear.Position(move.From) is { } from && CharacterGear.Position(move.To) is { } to)
            {
                var gear = new CharacterGear(stored);
                success = gear.Move(from, to, move.AllowSwap);
                if (success && from != to)
                {
                    var updated = accounts.UpdateCharacter(account, stored.Id,
                        current => current with { GearLocations = gear.Locations })!;
                    gear.PublishInventory(rt, character, updated, account.PlayFabId, wallet.Items, [from, to]);
                    UpdateEquippedBow(rt, caller, spawn, gear.At(-StarterWeapons.ShoulderSlot));
                }
            }
            var response = new SchemaWriter();
            response.AddUInt32(1, move.Id);
            response.AddBool(2, success);
            rt.SendCommandResponse(caller, req, response.ToArray());
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or System.Data.Common.DbException)
        {
            log.LogError(ex, "Could not save inventory move for worker {WorkerId}", caller.WorkerEntityId);
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Could not save inventory move");
        }
        return true;
    }

    private static void PublishWallet(ZenFabricRuntime rt, StoredEntity character, int balance) =>
        rt.ReplaceComponentSnapshot(character, CosmeticShop.Wallet, CosmeticShop.WalletState(balance),
            CosmeticShop.WalletState(balance, updateOnly: true));

    public bool HandleGrapple(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity entity)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) || !spawn.GrappleIds.Contains(entity.Id))
            return false;

        try
        {
            var reader = new SchemaReader(req.Data);
            RequireField(ref reader, 1);
            (float X, float Y, float Z) target = default;
            var hit = false;
            var retract = req.CommandIndex == 2;
            var immediate = false;
            if (retract)
            {
                immediate = reader.ReadBool();
            }
            else
            {
                var location = new SchemaReader(reader.ReadBytesSpan());
                RequireField(ref location, 1);
                var x = location.ReadFloat();
                RequireField(ref location, 2);
                var y = location.ReadFloat();
                RequireField(ref location, 3);
                var z = location.ReadFloat();
                RequireEnd(ref location);
                RequireField(ref reader, 2);
                hit = reader.ReadBool();
                RequireField(ref reader, 3);
                reader.ReadBool();
                var dx = x - spawn.CurrentPosition.X;
                var dy = y - spawn.CurrentPosition.Y;
                var dz = z - spawn.CurrentPosition.Z;
                if (!CanFight(spawn) || !(dx * dx + dy * dy + dz * dz <= 100 * 100))
                    throw new SchemaFormatException("Grapple target is out of reach");
                target = (x, y, z);
            }
            RequireEnd(ref reader);
            rt.ReplaceComponentSnapshot(entity, StarterWeapons.Hookshot, StarterWeapons.GrappleState(target, hit),
                StarterWeapons.GrappleMove(target, hit, retract, immediate));
            rt.SendCommandResponse(caller, req, []);
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        return true;
    }

    public bool HandleEnemyLoot(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity entity)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) || !CanFight(spawn) ||
            !spawn.LootDrops.TryGetValue(entity.Id, out var loot) || loot.Expires <= Environment.TickCount64 ||
            !loot.CanReach(spawn.CurrentPosition))
        {
            rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Loot is unavailable or out of reach");
            return true;
        }

        try
        {
            var reader = new SchemaReader(req.Data);
            switch (req.ComponentId)
            {
                case StarterWeapons.Grabbable:
                    var (held, hand, actor) = ReadGrabRequest(ref reader);
                    if (held && (actor != spawn.CharacterEntityId || HandAttachment(spawn, hand) == 0))
                        throw new SchemaFormatException("Loot must be grabbed with an active character hand");
                    loot.Grab(rt, spawn.CharacterEntityId, caller.WorkerEntityId, held ? hand : 0);
                    if (!held) loot.SetTransformOwner(rt, 0, spawn.CurrentPosition, Environment.TickCount64);
                    rt.SendCommandResponse(caller, req, []);
                    break;

                case EnemyLoot.Ownership:
                    var (worker, release, owner) = ReadOwnershipRequest(ref reader);
                    if (owner != spawn.CharacterId || !release && (worker != caller.WorkerEntityId || loot.Hand == 0))
                        throw new SchemaFormatException("Loot transform ownership requires the active character");
                    loot.SetTransformOwner(rt, release ? 0 : worker, spawn.CurrentPosition, Environment.TickCount64);
                    rt.SendCommandResponse(caller, req, []);
                    break;

                case EnemyLoot.Component:
                    RequireField(ref reader, 1);
                    var claimant = reader.ReadInt64();
                    RequireField(ref reader, 2);
                    reader.ReadBool(); // Both pickup and quick equip first put the reward in inventory.
                    RequireEnd(ref reader);
                    if (claimant != spawn.CharacterEntityId || caller.Session is not { } session ||
                        accounts.FindById(session.PlayFabId) is not { } account ||
                        rt.Entities.Get(spawn.CharacterEntityId) is not { } character)
                    {
                        rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Loot requires the owning character");
                        break;
                    }
                    var pickupSlot = 0;
                    var updated = accounts.UpdateCharacter(account, spawn.CharacterId, current =>
                    {
                        var gear = new CharacterGear(current);
                        if (gear.FirstFreeBagSlot() is not { } available) return null;
                        pickupSlot = available;
                        gear.Locations.Add(available, loot.Item.InstanceId);
                        return current with { LootInventory = [..current.LootInventory, loot.Item], GearLocations = gear.Locations };
                    });
                    if (updated is null)
                    {
                        rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Inventory is full");
                        break;
                    }

                    spawn.LootDrops.Remove(entity.Id);
                    var wallet = accounts.ReadCosmeticWallet(account);
                    new CharacterGear(updated).PublishInventory(rt, character, updated, account.PlayFabId, wallet.Items, [pickupSlot]);
                    var response = new SchemaWriter();
                    response.AddStringList(1, [loot.Item.InstanceId]);
                    rt.SendCommandResponse(caller, req, response.ToArray());
                    rt.DeleteEntity(entity.Id, null);
                    break;
            }
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        catch (IOException ex)
        {
            log.LogError(ex, "Could not save loot pickup for worker {WorkerId}", caller.WorkerEntityId);
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Could not save loot pickup");
        }
        return true;
    }

    internal bool HandleFinaleSoul(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) ||
            GetCombatPlayer(caller) is not { } player || _enemies.FindSouls(caller, req.EntityId) is not { } souls)
        {
            rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Soul is unavailable");
            return true;
        }

        try
        {
            var reader = new SchemaReader(req.Data);
            if (req.ComponentId == StarterWeapons.Grabbable)
            {
                var (held, hand, actor) = ReadGrabRequest(ref reader);
                var attachment = HandAttachment(spawn, hand);
                if (held && (actor != spawn.CharacterEntityId || attachment == 0) ||
                    !souls.TryGrab(rt, req.EntityId, player, held ? hand : 0, spawn.AvatarId, attachment, Environment.TickCount64))
                {
                    rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Soul is already held or out of reach");
                    return true;
                }
            }
            else
            {
                var (worker, release, owner) = ReadOwnershipRequest(ref reader);
                if (owner != spawn.CharacterId || !release &&
                    (worker != caller.WorkerEntityId || !souls.IsHeldBy(req.EntityId, worker)))
                {
                    rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Soul must be held by this character");
                    return true;
                }
            }
            rt.SendCommandResponse(caller, req, []);
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        return true;
    }

    private static int HandAttachment(ActiveSpawn spawn, long hand)
    {
        if (hand == 0) return 0;
        if (hand == spawn.LeftHandId || hand == spawn.RegisteredLeftHand) return StarterWeapons.Grapples[0].Attachment;
        return hand == spawn.RightHandId || hand == spawn.RegisteredRightHand ? StarterWeapons.Grapples[1].Attachment : 0;
    }

    private static (bool Held, long Hand, long Actor) ReadGrabRequest(ref SchemaReader reader)
    {
        RequireField(ref reader, 1);
        var grab = new SchemaReader(reader.ReadBytesSpan());
        RequireField(ref grab, 1);
        var held = grab.ReadBool();
        var hand = ReadOptionalEntity(ref grab, 2);
        ReadString(ref grab, 3);
        var actor = ReadOptionalEntity(ref grab, 4);
        RequireField(ref grab, 5);
        if (grab.ReadOptionHasValue()) grab.ReadUInt32();
        RequireEnd(ref grab);
        RequireEnd(ref reader);
        return (held, hand, actor);
    }

    private static (long Worker, bool Release, string? Owner) ReadOwnershipRequest(ref SchemaReader reader)
    {
        var worker = ReadOptionalEntity(ref reader, 1);
        RequireField(ref reader, 2);
        var release = reader.ReadBool();
        var owner = ReadString(ref reader, 3);
        RequireEnd(ref reader);
        return (worker, release, owner);
    }

    public void Tick(ZenFabricRuntime rt)
    {
        var now = Environment.TickCount64;
        foreach (var spawn in _activeSpawns.Values)
        {
            foreach (var loot in spawn.LootDrops.Values.Where(loot => loot.Expires <= now).ToArray())
            {
                spawn.LootDrops.Remove(loot.Entity.Id);
                rt.DeleteEntity(loot.Entity.Id, null);
            }
            if (spawn.Life == LifeState.Downed && spawn.CheckpointRecoveryAt != 0 && now >= spawn.CheckpointRecoveryAt &&
                rt.Entities.Get(spawn.CharacterEntityId) is { } character)
                BeginRecovery(rt, spawn, character);
            foreach (var cast in spawn.AirChops.Where(entry => entry.Value.Expires <= now).ToArray())
            {
                rt.DeleteEntity(cast.Value.EntityId, null);
                spawn.AirChops.Remove(cast.Key);
            }
            if (spawn.Arrows.Count != 0)
                foreach (var arrow in spawn.Arrows.Values.Where(arrow => arrow.Expires <= now).ToArray())
                    RemoveArrow(rt, spawn, arrow);

            if (spawn.NextArrowRecharge != 0 && now >= spawn.NextArrowRecharge &&
                spawn.ShoulderBowId is { } bowId && rt.Entities.Get(bowId) is { } bow)
            {
                spawn.BowAmmo++;
                spawn.NextArrowRecharge = spawn.BowAmmo < StarterWeapons.MaxAmmo
                    ? now + StarterWeapons.AmmoRechargeMs : 0;
                PublishBowAmmo(rt, spawn, bow, now, recharged: true);
            }
        }
        var daily = _daily.Today;
        foreach (var worker in rt.ReadyWorkers)
        {
            if (!_activeSpawns.TryGetValue(worker.WorkerEntityId, out var spawn)) continue;
            if (spawn.PendingExperience > 0 && now >= spawn.RetryExperienceAt)
                SaveExperience(rt, worker, spawn, now);
            RefreshDailyTrigger(rt, worker, spawn, daily);
        }

        _enemies.Tick(rt, now, GetCombatPlayer, (player, attacker, damage) => DamagePlayer(rt, player, attacker, damage),
            (worker, targets) =>
            {
                if (_activeSpawns.TryGetValue(worker.WorkerEntityId, out var spawn))
                    PublishAggro(rt, spawn, targets);
            });
    }

    private static void RefreshDailyTrigger(ZenFabricRuntime rt, WorkerConnection worker, ActiveSpawn spawn,
        WorldEntry.Area daily)
    {
        // Keep the original geometry and encounter space for players still inside yesterday's daily.
        if (spawn.DailyArea is not { } previous || previous == daily || spawn.Area == previous) return;
        rt.DeleteEntity(spawn.AreaEntities[previous], null);
        var replacement = rt.CreateEntity(0, WorldEntry.AreaTrigger(daily), worker.WorkerEntityId);
        spawn.AreaEntities.Remove(previous);
        spawn.AreaEntities.Add(daily, replacement.Id);
        spawn.DailyArea = daily;
    }

    private static void PublishAggro(ZenFabricRuntime rt, ActiveSpawn spawn, long[] targets)
    {
        if (spawn.AggroTargets.AsSpan().SequenceEqual(targets) ||
            rt.Entities.Get(spawn.CharacterEntityId) is not { } character) return;
        spawn.AggroTargets = targets;
        var snapshot = CharacterEntities.AggroMap(targets);
        rt.ReplaceComponentSnapshot(character, CharacterEntities.PlayerAggroMap, snapshot, snapshot);
    }

    private EnemyEncounters.Player? GetCombatPlayer(WorkerConnection worker) =>
        _activeSpawns.TryGetValue(worker.WorkerEntityId, out var spawn) && CanFight(spawn)
            ? new(worker.WorkerEntityId, spawn.CharacterEntityId, spawn.CurrentPosition)
            : null;

    private static bool CanFight(ActiveSpawn spawn) =>
        spawn.WorldEntered && spawn.Life == LifeState.Alive && spawn.TeleportDestination is null;

    private void AwardDefeat(ZenFabricRuntime rt, WorkerConnection killer, EnemyEncounters.Defeat defeat, long now)
    {
        foreach (var worker in rt.ReadyWorkers)
        {
            if (worker.SharedSpace != killer.SharedSpace || !_activeSpawns.TryGetValue(worker.WorkerEntityId, out var spawn) ||
                !spawn.WorldEntered || !defeat.Contributors.Contains(spawn.CharacterEntityId)) continue;

            spawn.PendingExperience = Math.Min(_progression.MaximumExperience, spawn.PendingExperience + defeat.Experience);
            spawn.ExperienceSource = defeat.EnemyId;
            if (now >= spawn.RetryExperienceAt) SaveExperience(rt, worker, spawn, now);
            if (!defeat.DropsLoot || _loot.Roll(defeat.Boss) is not { } reward) continue;
            var loot = EnemyLoot.Spawn(rt, worker, spawn.CharacterId, spawn.CharacterEntityId, spawn.Level,
                reward, defeat.Position, now);
            spawn.LootDrops.Add(loot.Entity.Id, loot);
        }
    }

    private void SaveExperience(ZenFabricRuntime rt, WorkerConnection worker, ActiveSpawn spawn, long now)
    {
        if (worker.Session is not { } session || accounts.FindById(session.PlayFabId) is not { } account ||
            rt.Entities.Get(spawn.CharacterEntityId) is not { } character) return;

        StoredCharacter? updated;
        try
        {
            updated = accounts.UpdateCharacter(account, spawn.CharacterId, current =>
            {
                var experience = Math.Min(_progression.MaximumExperience, current.Experience + spawn.PendingExperience);
                return experience == current.Experience ? current : current with { Experience = experience };
            });
        }
        catch (IOException ex)
        {
            log.LogError(ex, "Could not save experience for character {CharacterId}", spawn.CharacterId);
            spawn.RetryExperienceAt = now + 5000;
            return;
        }
        spawn.PendingExperience = 0;
        spawn.RetryExperienceAt = 0;
        if (updated is null || updated.Experience == spawn.Experience) return;

        var gained = updated.Experience - spawn.Experience;
        spawn.Experience = updated.Experience;
        var previousLevel = spawn.Level;
        spawn.Level = _progression.LevelAt(spawn.Experience);
        var levelUp = spawn.Level > previousLevel;
        var stats = _progression.Stats(Classes[spawn.CharacterType], spawn.Level);
        var delta = new SchemaWriter();
        if (levelUp) delta.AddInt32(7, spawn.Level);
        delta.AddInt32(8, spawn.Experience);
        if (levelUp)
        {
            var maxHealth = CharacterEntities.StartingHealth(stats);
            if (spawn.Health > 0) spawn.Health = Math.Clamp(spawn.Health + maxHealth - spawn.MaxHealth, 1, maxHealth);
            spawn.MaxHealth = maxHealth;
            delta.AddInt32(9, spawn.Health);
        }
        delta.AddInt32(19, spawn.Experience);
        if (levelUp)
        {
            delta.AddInt32(29, spawn.MaxHealth);
            delta.AddFloat(32, float.Parse(stats["attack"], CultureInfo.InvariantCulture));
            delta.AddInt32(33, spawn.Level);
            rt.ReplaceComponentSnapshot(character, Ids.ActorStatsComponent,
                CharacterEntities.ActorStatsSnapshot(spawn.CharacterType, stats, initialized: true),
                CharacterEntities.InitializeActorStats(spawn.CharacterType, stats, includeBase: true));
        }
        delta.AddObject(1004, xp =>
        {
            xp.AddInt32(1, gained);
            xp.AddInt32(2, spawn.Experience);
            xp.AddString(3, spawn.ExperienceSource.ToString(CultureInfo.InvariantCulture));
        });
        if (levelUp) delta.AddObject(1005, level =>
        {
            level.AddInt32(1, previousLevel);
            level.AddInt32(2, spawn.Level);
        });
        rt.ReplaceComponentSnapshot(character, CharacterEntities.PublicActorStats,
            CharacterEntities.PublicStatsSnapshot(spawn.Role, stats, initialized: true, spawn.Experience, spawn.Health), delta.ToArray());
    }

    private void DamagePlayer(ZenFabricRuntime rt, EnemyEncounters.Player player, long attacker, int damage)
    {
        var spawn = _activeSpawns[player.WorkerId];
        if (rt.Entities.Get(player.CharacterId) is not { } character) return;
        var dealt = Math.Min(damage, spawn.Health);
        var health = spawn.Health - dealt;
        if (health == 0) DownPlayer(rt, spawn, character, forceCheckpoint: false);
        SetHealth(rt, spawn, character, health, dealt, attacker);
    }

    public bool HandleBow(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity bow)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) || spawn.ShoulderBowId != bow.Id) return false;

        try
        {
            var reader = new SchemaReader(req.Data);
            var now = Environment.TickCount64;
            switch (req.CommandIndex)
            {
                case 1:
                    RequireField(ref reader, 1);
                    var draw = reader.ReadFloat();
                    RequireField(ref reader, 2);
                    var knock = reader.ReadFloat();
                    RequireEnd(ref reader);
                    if (!float.IsFinite(draw) || !float.IsFinite(knock)) throw new SchemaFormatException("Invalid bow draw");
                    spawn.BowDraw = draw;
                    spawn.BowKnock = knock;
                    rt.ReplaceComponentSnapshot(bow, StarterWeapons.Bow, BowSnapshot(spawn, now),
                        StarterWeapons.BowDrawUpdate(draw, knock));
                    break;
                case 2:
                    if (!Guid.TryParseExact(ReadString(ref reader, 3), "D", out var cast))
                        throw new SchemaFormatException("Invalid arrow ID");
                    RequireField(ref reader, 4);
                    var attach = reader.ReadBool();
                    if (ReadString(ref reader, 5) != StarterWeapons.ArrowPrefab)
                        throw new SchemaFormatException("Unknown starter arrow");
                    RequireEnd(ref reader);
                    if (spawn.Arrows.ContainsKey(cast)) break;
                    if (!spawn.WorldEntered || spawn.Life != LifeState.Alive || spawn.TeleportDestination is not null ||
                        spawn.NockedArrow is not null || spawn.BowAmmo == 0 || spawn.Arrows.Count >= 24)
                    {
                        rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Cannot nock an arrow now");
                        return true;
                    }
                    var position = spawn.CurrentPosition;
                    if (bow.Components.TryGetValue(Ids.Position, out var bowPosition) &&
                        StandardComponents.ReadPosition(bowPosition.Data) is { } origin)
                        position = origin;
                    var arrowPosition = AirChopOrigin(((float)position.X, (float)position.Y, (float)position.Z),
                        spawn.CurrentPosition);
                    var entity = rt.CreateEntity(0, StarterWeapons.BowArrow(caller.WorkerEntityId, spawn.CharacterEntityId,
                        attach ? bow.Id : null, spawn.CharacterId, cast, arrowPosition), caller.WorkerEntityId, shareInArea: true);
                    var arrow = new ActiveArrow(cast, entity.Id, now + 60_000);
                    spawn.Arrows.Add(cast, arrow);
                    spawn.NockedArrow = arrow;
                    break;
                case 4:
                    var cost = ReadBowShot(ref reader);
                    if (spawn.NockedArrow is not { } shot) break; // The release may already have been processed.
                    if (cost > spawn.BowAmmo)
                    {
                        RemoveArrow(rt, spawn, shot);
                        rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Insufficient arrows");
                        return true;
                    }
                    if (rt.Entities.Get(shot.EntityId) is { } shotEntity) MarkArrowShot(rt, shotEntity, shot);
                    shot.LaunchPosition = spawn.CurrentPosition;
                    spawn.NockedArrow = null;
                    spawn.BowAmmo -= cost;
                    if (spawn.NextArrowRecharge == 0) spawn.NextArrowRecharge = now + StarterWeapons.AmmoRechargeMs;
                    PublishBowAmmo(rt, spawn, bow, now);
                    break;
            }
            rt.SendCommandResponse(caller, req, []);
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        return true;
    }

    private static int ReadBowShot(ref SchemaReader reader)
    {
        RequireField(ref reader, 1);
        var cost = reader.ReadInt32();
        RequireField(ref reader, 2);
        reader.ReadBool(); // Override shared bow settings
        RequireField(ref reader, 3);
        reader.ReadBool(); // Minimum hit distance enabled
        RequireField(ref reader, 4);
        var minimumDistance = reader.ReadFloat();
        RequireField(ref reader, 5);
        reader.ReadInt32(); // Headshot ammo reward
        RequireField(ref reader, 6);
        reader.ReadInt32(); // Weakpoint ammo reward
        RequireEnd(ref reader);
        if (cost is < 1 or > 2 || !float.IsFinite(minimumDistance))
            throw new SchemaFormatException("Invalid starter bow shot");
        return cost;
    }

    private static float RechargeSeconds(ActiveSpawn spawn, long now) =>
        spawn.NextArrowRecharge == 0 ? 0 : Math.Max(0, StarterWeapons.AmmoRechargeMs - (spawn.NextArrowRecharge - now)) / 1000f;

    private static byte[] BowSnapshot(ActiveSpawn spawn, long now) =>
        StarterWeapons.BowState(spawn.BowDraw, spawn.BowKnock, spawn.BowAmmo, RechargeSeconds(spawn, now));

    private static void PublishBowAmmo(ZenFabricRuntime rt, ActiveSpawn spawn, StoredEntity bow, long now,
        bool recharged = false)
    {
        if (rt.Entities.Get(spawn.CharacterEntityId) is { } character)
        {
            var snapshot = new SchemaWriter();
            CharacterEntities.WriteCombatState(snapshot, spawn.BowAmmo);
            var update = new SchemaWriter();
            CharacterEntities.WriteCombatAmmo(update, spawn.BowAmmo);
            rt.ReplaceComponentSnapshot(character, CharacterEntities.Combat, snapshot.ToArray(), update.ToArray());
        }
        rt.ReplaceComponentSnapshot(bow, StarterWeapons.Bow, BowSnapshot(spawn, now),
            StarterWeapons.BowAmmoUpdate(spawn.BowAmmo,
                recharged ? StarterWeapons.AmmoRechargeMs / 1000f : RechargeSeconds(spawn, now)));
    }

    public bool HandleArrow(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity entity)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) ||
            spawn.Arrows.Values.FirstOrDefault(arrow => arrow.EntityId == entity.Id) is not { } arrow) return false;

        try
        {
            var reader = new SchemaReader(req.Data);
            if (req.CommandIndex == 1)
            {
                RequireField(ref reader, 1);
                var stick = reader.ReadBytesSpan().ToArray();
                RequireEnd(ref reader);
                if (stick.Length != 49) throw new SchemaFormatException("Invalid arrow impact");
                if (arrow.Shot)
                    rt.ReplaceComponentSnapshot(entity, StarterWeapons.Arrow, StarterWeapons.ArrowState(shot: true, stick), req.Data);
            }
            else
            {
                RequireEnd(ref reader);
                if (req.CommandIndex == 2) MarkArrowShot(rt, entity, arrow);
                else if (!arrow.Shot) RemoveArrow(rt, spawn, arrow);
            }
            rt.SendCommandResponse(caller, req, []);
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        return true;
    }

    private static void MarkArrowShot(ZenFabricRuntime rt, StoredEntity entity, ActiveArrow arrow)
    {
        if (arrow.Shot) return;
        arrow.Shot = true;
        arrow.Expires = Environment.TickCount64 + 12_000;
        var update = new SchemaWriter();
        update.AddBool(2, true);
        rt.ReplaceComponentSnapshot(entity, StarterWeapons.Arrow, StarterWeapons.ArrowState(shot: true), update.ToArray());
    }

    private static void RemoveArrow(ZenFabricRuntime rt, ActiveSpawn spawn, ActiveArrow arrow)
    {
        if (spawn.NockedArrow == arrow) spawn.NockedArrow = null;
        spawn.Arrows.Remove(arrow.Cast);
        rt.DeleteEntity(arrow.EntityId, null);
    }

    public bool TryStarterAbility(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity character)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) || spawn.CharacterEntityId != character.Id ||
            spawn.Area is null) return false;

        try
        {
            var reader = new SchemaReader(req.Data);
            if (req.ComponentId == StarterWeapons.Godstone)
            {
                var activation = ReadUInt32(ref reader, 1);
                RequireEnd(ref reader);
                if (activation is not (1 or 22)) return false;
                rt.SendCommandResponse(caller, req, []);
                rt.Send(caller, new ComponentUpdateOp
                {
                    EntityId = character.Id, ComponentId = StarterWeapons.Godstone, Data = StarterWeapons.CooldownUpdate(),
                });
                return true;
            }

            if (ReadString(ref reader, 1) != StarterWeapons.AirChops) return false;
            RequireField(ref reader, 2);
            var info = new SchemaReader(reader.ReadBytesSpan());
            RequireEnd(ref reader);
            var (castId, handPosition) = ReadAirChop(ref info);
            var now = Environment.TickCount64;
            var alreadySpawned = spawn.AirChops.ContainsKey(castId);
            var canSpawn = spawn.WorldEntered && spawn.Life == LifeState.Alive && spawn.TeleportDestination is null &&
                now >= spawn.NextAirChop && spawn.AirChops.Count < 20 &&
                Math.Abs(spawn.CurrentPosition.X) <= 20_000 && Math.Abs(spawn.CurrentPosition.Y) <= 20_000 &&
                Math.Abs(spawn.CurrentPosition.Z) <= 20_000;
            if (canSpawn && !alreadySpawned)
            {
                var position = AirChopOrigin(handPosition, spawn.CurrentPosition);
                var projectile = rt.CreateEntity(0, StarterWeapons.AirChopProjectile(caller.WorkerEntityId, character.Id,
                    castId, position), caller.WorkerEntityId, shareInArea: true);
                spawn.AirChops.Add(castId, new(projectile.Id, now + 3500) { LaunchPosition = position });
                spawn.NextAirChop = now + 120;
            }

            var result = new SchemaWriter();
            result.AddBool(1, alreadySpawned || canSpawn);
            rt.SendCommandResponse(caller, req, result.ToArray());
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        return true;
    }

    private static (Guid Cast, (float X, float Y, float Z) Position) ReadAirChop(ref SchemaReader info)
    {
        RequireField(ref info, 1);
        var targets = info.ReadListCount();
        if (targets is < 0 or > 64) throw new SchemaFormatException("Invalid Air Chops target count");
        info.ReadRaw(targets * sizeof(long)); // Projectile impact is reported separately.
        RequireField(ref info, 2);
        if (!float.IsFinite(info.ReadFloat())) throw new SchemaFormatException("Invalid Air Chops intensity");
        RequireField(ref info, 3);
        info.ReadBytesSpan(); // EventLocation. The hand data carries the launch position.
        ReadString(ref info, 4); // Item ID
        ReadString(ref info, 5); // Item instance
        RequireField(ref info, 6);
        info.ReadBool(); // Hand
        if (!Guid.TryParseExact(ReadString(ref info, 7), "D", out var cast))
            throw new SchemaFormatException("Invalid Air Chops cast ID");
        RequireField(ref info, 8);
        info.ReadInt32(); // Hittable index
        ReadString(ref info, 0); // Tag, serialized with field ID 0 by the stock client.
        RequireField(ref info, 9);
        info.ReadBytesSpan(); // Local hand data
        RequireField(ref info, 10);
        var hand = new SchemaReader(info.ReadBytesSpan());
        return (cast, ReadAbsolutePosition(ref hand, 1));
    }

    private static (float X, float Y, float Z) ReadAbsolutePosition(ref SchemaReader reader, uint field)
    {
        RequireField(ref reader, field);
        var position = new SchemaReader(reader.ReadBytesSpan());
        RequireField(ref position, 1);
        var x = position.ReadInt32() / 100_000f;
        RequireField(ref position, 2);
        var y = position.ReadInt32() / 100_000f;
        RequireField(ref position, 3);
        var z = position.ReadInt32() / 100_000f;
        RequireEnd(ref position);
        return (x, y, z);
    }

    private static (float X, float Y, float Z) AirChopOrigin((float X, float Y, float Z) hand,
        (double X, double Y, double Z) avatar)
    {
        var dx = hand.X - avatar.X;
        var dy = hand.Y - avatar.Y;
        var dz = hand.Z - avatar.Z;
        return dx * dx + dy * dy + dz * dz <= 25 ? hand : ((float)avatar.X, (float)avatar.Y + 1.2f, (float)avatar.Z);
    }

    public bool HandleProjectileHit(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity projectile)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn)) return false;
        var isArrow = projectile.Components.ContainsKey(StarterWeapons.Arrow);
        ActiveProjectile? shot = isArrow
            ? spawn.Arrows.Values.FirstOrDefault(arrow => arrow.EntityId == projectile.Id)
            : spawn.AirChops.Values.FirstOrDefault(chop => chop.EntityId == projectile.Id);
        if (shot is null) return false;

        try
        {
            var reader = new SchemaReader(req.Data);
            RequireField(ref reader, 1);
            var successful = reader.ReadBool();
            RequireField(ref reader, 2);
            var target = reader.ReadInt64();
            var impact = ReadAbsolutePosition(ref reader, 3);
            RequireField(ref reader, 4);
            if (!float.IsFinite(reader.ReadFloat())) throw new SchemaFormatException("Invalid projectile hit");
            RequireEnd(ref reader);

            var now = Environment.TickCount64;
            if (successful && CanFight(spawn) && !shot.Hit && shot.Expires > now && shot.LaunchPosition is { } origin)
            {
                shot.Hit = _enemies.Hit(rt, caller, target, spawn.CharacterEntityId, isArrow ? 120 : 90,
                    impact, origin, isArrow ? 45 : 30, now, out var defeat);
                if (defeat is not null) AwardDefeat(rt, caller, defeat, now);
            }
            rt.SendCommandResponse(caller, req, []);
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        return true;
    }

    public bool EndStarterProjectile(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity projectile)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn)) return false;
        if (projectile.Components.ContainsKey(StarterWeapons.Arrow) &&
            spawn.Arrows.Values.FirstOrDefault(arrow => arrow.EntityId == projectile.Id) is { } arrow)
        {
            rt.SendCommandResponse(caller, req, []);
            arrow.Expires = Math.Min(arrow.Expires, Environment.TickCount64 + 2000);
            return true;
        }
        foreach (var (cast, active) in spawn.AirChops)
        {
            if (active.EntityId != projectile.Id) continue;
            rt.SendCommandResponse(caller, req, []);
            rt.DeleteEntity(projectile.Id, null);
            spawn.AirChops.Remove(cast);
            return true;
        }
        return false;
    }

    private static void BeginRecovery(ZenFabricRuntime rt, ActiveSpawn spawn, StoredEntity character)
    {
        if (spawn.Life != LifeState.Downed || spawn.TeleportDestination is not null ||
            rt.Entities.Get(spawn.AvatarId) is not { } avatar) return;

        var (x, y, z) = spawn.CurrentPosition;
        var inPlace = spawn.CheckpointRecoveryAt == 0 && y >= WorldEntry.OutOfBoundsY &&
            Math.Abs(x) <= 20_000 && Math.Abs(y) <= 20_000 && Math.Abs(z) <= 20_000;
        spawn.CheckpointRecoveryAt = 0;
        SetLife(rt, spawn, character, LifeState.Recovering);
        SendTeleport(rt, spawn, avatar, inPlace ? ((float)x, (float)y, (float)z) : spawn.RecoveryPosition, quick: inPlace);
    }

    private static void DownPlayer(ZenFabricRuntime rt, ActiveSpawn spawn, StoredEntity character, bool forceCheckpoint)
    {
        var (x, y, z) = spawn.CurrentPosition;
        spawn.DeathTether = forceCheckpoint ? null : ((float)x, (float)y, (float)z);
        spawn.CheckpointRecoveryAt = forceCheckpoint ? Environment.TickCount64 + 1000 : 0;
        ClearStarterProjectiles(rt, spawn);
        PublishAggro(rt, spawn, []);
        SetLife(rt, spawn, character, LifeState.Downed);
    }

    private static void SetLife(ZenFabricRuntime rt, ActiveSpawn spawn, StoredEntity character, LifeState state)
    {
        var update = new SchemaWriter();
        update.AddUInt32(1, (uint)state);
        if (state == LifeState.Downed)
        {
            if (spawn.DeathTether is not null) CharacterEntities.WriteDeathTether(update, spawn.DeathTether);
            update.AddObject(1001, e => e.AddBool(1, spawn.CheckpointRecoveryAt != 0));
        }
        if (state == LifeState.Alive)
        {
            if (spawn.DeathTether is not null) CharacterEntities.WriteDeathTether(update, null);
            spawn.DeathTether = null;
            update.AddObject(1002, e => e.AddString(2, null));
        }
        spawn.Life = state;
        rt.ReplaceComponentSnapshot(character, Ids.PlayerLifecycleComponent,
            CharacterEntities.PlayerLifecycle((uint)state, spawn.DeathTether), update.ToArray());
    }

    private static void SetHealth(ZenFabricRuntime rt, ActiveSpawn spawn, StoredEntity character, int health,
        int damage = 0, long attacker = 0)
    {
        var snapshot = character.Components[CharacterEntities.PublicActorStats].Data.ToArray();
        var reader = new SchemaReader(snapshot);
        RequireField(ref reader, 7); // Level
        reader.ReadInt32();
        RequireField(ref reader, 8); // XP
        reader.ReadInt32();
        var healthOffset = reader.Position;
        RequireField(ref reader, 9);
        reader.ReadInt32();

        var update = EnemyEncounters.HealthUpdate(health, damage, attacker, character.Id);
        update.WrittenSpan[..(reader.Position - healthOffset)].CopyTo(snapshot.AsSpan(healthOffset));
        spawn.Health = health;
        rt.ReplaceComponentSnapshot(character, CharacterEntities.PublicActorStats, snapshot, update.ToArray());
    }

    public bool BroadcastVfx(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity entity)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) || spawn.CharacterEntityId != entity.Id)
        {
            rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Effects require the owning character");
            return true;
        }
        if (req.Data.Length is < 6 or > 4096)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Invalid effect event size");
            return true;
        }

        // Commands 1 and 2 carry the payloads for play and stop events 1001 and 1002.
        var update = new SchemaWriter();
        update.AddBytes(1000 + req.CommandIndex, req.Data);
        rt.SendCommandResponse(caller, req, []);
        rt.Send(caller, new ComponentUpdateOp { EntityId = entity.Id, ComponentId = req.ComponentId, Data = update.ToArray() });
        return true;
    }

    public bool RegisterInteractionHands(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity entity)
    {
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) || spawn.CharacterEntityId != entity.Id)
        {
            rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Hand registration requires the owning character");
            return true;
        }

        try
        {
            var reader = new SchemaReader(req.Data);
            RequireField(ref reader, 1);
            var left = reader.ReadOptionHasValue() ? reader.ReadInt64() : spawn.RegisteredLeftHand;
            RequireField(ref reader, 2);
            var right = reader.ReadOptionHasValue() ? reader.ReadInt64() : spawn.RegisteredRightHand;
            RequireEnd(ref reader);
            if ((left != 0 && left != spawn.LeftHandId) || (right != 0 && right != spawn.RightHandId))
            {
                rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Hand IDs must belong to the active character");
                return true;
            }

            if (left != spawn.RegisteredLeftHand || right != spawn.RegisteredRightHand)
            {
                rt.ReplaceComponentSnapshot(entity, Ids.NetworkedObjectInteractionComponent,
                    CharacterEntities.InteractionHands(left, right),
                    CharacterEntities.InteractionHands(left, right, linksOnly: true));
                spawn.RegisteredLeftHand = left;
                spawn.RegisteredRightHand = right;
            }
            rt.SendCommandResponse(caller, req, []);
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        return true;
    }

    public bool TryHandle(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity? player)
    {
        try
        {
            var reader = new SchemaReader(req.Data);
            var playFabId = ReadString(ref reader, 1);
            var account = caller.Session is { } session
                && string.Equals(playFabId, session.PlayFabId, StringComparison.OrdinalIgnoreCase)
                ? accounts.FindById(session.PlayFabId)
                : null;
            if (account is null)
            {
                rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Character requests require the owning account");
                return true;
            }

            var response = new SchemaWriter();
            switch (req.CommandIndex)
            {
                case 1:
                    RequireEnd(ref reader);
                    response.AddBool(1, false); // UsePlayfab
                    response.AddObjectList(2, accounts.ListCharacters(account).Select(WriteCharacter).ToArray());
                    break;
                case 2:
                    var candidate = ReadString(ref reader, 2);
                    RequireEnd(ref reader);
                    response.AddBool(1, IsValidName(candidate) && accounts.IsCharacterNameAvailable(candidate!));
                    break;
                case 3:
                    Spawn(rt, caller, req, account, player, ref reader);
                    return true;
                case 4:
                    var type = ReadUInt32(ref reader, 6);
                    var role = ReadUInt32(ref reader, 3);
                    var name = ReadString(ref reader, 5);
                    var appearance = ReadAppearance(ref reader);
                    var gender = ReadUInt32(ref reader, 8);
                    RequireEnd(ref reader);
                    if (type == 0 || type >= Classes.Length || role >= Roles.Length || gender > 2 || !IsValidName(name))
                        throw new SchemaFormatException("Invalid character name, class, role or gender");

                    appearance[4] = gender.ToString();
                    var character = accounts.CreateCharacter(account, name!, type, role, gender, appearance);
                    if (character is null)
                    {
                        rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Character name is unavailable");
                        return true;
                    }
                    response.AddString(1, character.Id);
                    response.AddString(2, character.Name);
                    response.AddUInt32(3, character.Type);
                    response.AddEmptyList(7); // The captured create response leaves Appearance empty.
                    break;
                default:
                    return false;
            }
            rt.SendCommandResponse(caller, req, response.ToArray());
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        catch (IOException ex)
        {
            log.LogError(ex, "Could not save character for {Worker}", caller.Display);
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Could not save character");
        }
        return true;
    }

    public void RemoveSpawn(ZenFabricRuntime rt, WorkerConnection worker)
    {
        if (_activeSpawns.Remove(worker.WorkerEntityId, out var spawn))
        {
            ClearLootDrops(rt, spawn);
            rt.SetPlayerArea(worker, null);
            ClearStarterProjectiles(rt, spawn);
            if (spawn.ShoulderBowId is { } bow) rt.DeleteEntity(bow, null);
            foreach (var grapple in spawn.GrappleIds) rt.DeleteEntity(grapple, null);
            foreach (var id in spawn.Entities.Reverse()) rt.DeleteEntity(id, null);
            foreach (var areaEntityId in spawn.AreaEntities.Values) rt.DeleteEntity(areaEntityId, null);
            log.LogInformation("Removed {Count} spawn entities for worker {WorkerId}", spawn.Entities.Length, worker.WorkerEntityId);
        }
    }

    public bool TryHandleWorldEntry(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, StoredEntity entity)
    {
        var worldCommand = req.ComponentId == Ids.PlayerWorldComponent;
        if (!_activeSpawns.TryGetValue(caller.WorkerEntityId, out var spawn) ||
            entity.Id != (worldCommand ? spawn.PlayerId : spawn.AvatarId))
        {
            rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "World entry requires the owning player's active spawn");
            return true;
        }

        try
        {
            var reader = new SchemaReader(req.Data);
            if (worldCommand)
            {
                HandlePlayerWorld(rt, caller, spawn, req, ref reader);
                return true;
            }

            RequireField(ref reader, 1);
            if (req.CommandIndex == 3)
            {
                if (reader.ReadOptionHasValue()) reader.ReadBytesSpan(); // Ignore debug spawn overrides.
                RequireField(ref reader, 2);
                var skipLoginTeleport = reader.ReadBool();
                RequireEnd(ref reader);
                if (!spawn.AvatarReady)
                {
                    spawn.AvatarReady = true;
                    spawn.SkipLoginTeleport = skipLoginTeleport;
                }
                rt.SendCommandResponse(caller, req, []);
                SendLoginTeleportIfReady(rt, caller, spawn);
                return true;
            }

            var clientConfirmed = reader.ReadBool();
            RequireEnd(ref reader);
            if (req.CommandIndex == 1)
            {
                var accepted = clientConfirmed && (spawn.TeleportDestination is not null || spawn.WorldEntered);
                var response = new SchemaWriter();
                response.AddBool(1, accepted);
                rt.SendCommandResponse(caller, req, response.ToArray());
                if (accepted && !spawn.PositionConfirmed && spawn.TeleportDestination is { } destination)
                {
                    var position = WorldEntry.TeleportPosition(destination);
                    rt.ReplaceComponentSnapshot(entity, Ids.TeleportComponent, position, position);
                    spawn.PositionConfirmed = true;
                    spawn.CurrentPosition = destination;
                    if (!spawn.WorldEntered) ResetRigPosition(rt, caller, spawn, destination);
                    if (spawn.Life == LifeState.Recovering && rt.Entities.Get(spawn.CharacterEntityId) is { } character)
                    {
                        SetLife(rt, spawn, character, LifeState.Alive);
                        SetHealth(rt, spawn, character, spawn.MaxHealth);
                    }
                    // The stock client confirms a quick teleport with command 1 only.
                    if (spawn.QuickTeleport)
                    {
                        spawn.TeleportDestination = null;
                        spawn.QuickTeleport = false;
                    }
                }
                return true;
            }

            rt.SendCommandResponse(caller, req, []);
            if (clientConfirmed && spawn.PositionConfirmed && spawn.TeleportDestination is not null)
            {
                spawn.WorldEntered = true;
                spawn.TeleportDestination = null;
                PublishEnteredWorld(rt, caller, spawn);
                log.LogInformation("Teleport finished for worker {WorkerId}", caller.WorkerEntityId);
            }
        }
        catch (SchemaFormatException ex)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, ex.Message);
        }
        return true;
    }

    private void HandlePlayerWorld(ZenFabricRuntime rt, WorkerConnection caller, ActiveSpawn spawn,
        CommandRequestOp req, ref SchemaReader reader)
    {
        if (req.CommandIndex == 2)
        {
            RequireEnd(ref reader);
            spawn.WorldLoaded = true;
            rt.SendCommandResponse(caller, req, []);
            SendLoginTeleportIfReady(rt, caller, spawn);
            return;
        }

        var areaId = ReadString(ref reader, 1);
        if (req.CommandIndex == 4)
        {
            RequireField(ref reader, 2);
            if (reader.ReadOptionHasValue()) reader.ReadInt32();
            RequireField(ref reader, 3);
            if (reader.ReadOptionHasValue()) reader.ReadBool();
            RequireEnd(ref reader);
            var area = spawn.AreaEntities.Keys.FirstOrDefault(area => area.Id == areaId);
            var entityId = area is not null && spawn.AreaEntities.TryGetValue(area, out var id) ? (long?)id : null;
            rt.SendCommandResponse(caller, req, WorldEntry.AreaList(area, entityId));
            return;
        }

        RequireEnd(ref reader);
        if (spawn.Area is null || areaId != WorldEntry.Hub.Id)
        {
            rt.SendCommandFailure(caller, req, StatusCode.NotFound, "Local area unavailable");
            return;
        }
        rt.SendCommandResponse(caller, req, []);
        EnterArea(rt, caller, spawn, WorldEntry.Hub, teleportToSpawn: true);
    }

    private void EnterArea(ZenFabricRuntime rt, WorkerConnection caller, ActiveSpawn spawn, WorldEntry.Area destination,
        bool teleportToSpawn = false)
    {
        if (spawn.Area is null || spawn.Area == destination || !spawn.WorldEntered || spawn.TeleportDestination is not null ||
            spawn.Life != LifeState.Alive || rt.Entities.Get(spawn.PlayerId) is not { } player ||
            rt.Entities.Get(spawn.AvatarId) is not { } avatar) return;

        ClearStarterProjectiles(rt, spawn);
        ClearLootDrops(rt, spawn);
        PublishAggro(rt, spawn, []);
        spawn.Area = destination;
        spawn.Checkpoint = null;
        UpdatePlayerWorld(rt, spawn, player);
        if (teleportToSpawn)
        {
            rt.SetPlayerArea(caller, null);
            spawn.WorldEntered = false;
            SendTeleport(rt, spawn, avatar, destination.Spawn);
        }
        else
        {
            // Gate paths are continuous. Keep the avatar in motion and switch its replication area.
            rt.SetPlayerArea(caller, destination.SharedId);
            _enemies.Enter(rt, caller, destination);
        }
        log.LogInformation("Worker {WorkerId} entering {Area}", caller.WorkerEntityId, destination.Name);
    }

    private static void ClearStarterProjectiles(ZenFabricRuntime rt, ActiveSpawn spawn)
    {
        foreach (var active in spawn.AirChops.Values) rt.DeleteEntity(active.EntityId, null);
        spawn.AirChops.Clear();
        foreach (var arrow in spawn.Arrows.Values) rt.DeleteEntity(arrow.EntityId, null);
        spawn.Arrows.Clear();
        spawn.NockedArrow = null;
    }

    private static void ClearLootDrops(ZenFabricRuntime rt, ActiveSpawn spawn)
    {
        foreach (var loot in spawn.LootDrops.Values) rt.DeleteEntity(loot.Entity.Id, null);
        spawn.LootDrops.Clear();
    }

    private static void UpdatePlayerWorld(ZenFabricRuntime rt, ActiveSpawn spawn, StoredEntity player,
        bool initialEntry = false) =>
        rt.ReplaceComponentSnapshot(player, Ids.PlayerWorldComponent,
            WorldEntry.PlayerWorld(enterMainWorld: true, spawn.Area, spawn.AreaEntityId),
            WorldEntry.PlayerWorldUpdate(spawn.Area, spawn.AreaEntityId, initialEntry));

    private static void ResetRigPosition(ZenFabricRuntime rt, WorkerConnection caller, ActiveSpawn spawn,
        (float X, float Y, float Z) position)
    {
        var data = StandardComponents.Position(position.X, position.Y, position.Z);
        foreach (var id in spawn.Entities)
        {
            if (rt.Entities.Get(id) is not { } entity) continue;
            entity.Components[Ids.Position] = new StoredComponent { Data = data };
            var transformOwner = id == spawn.CharacterEntityId ? 0 : caller.WorkerEntityId;
            entity.Components[Ids.ZSyncComponent] = new StoredComponent { Data = CharacterEntities.ZSyncSnapshot(position, transformOwner) };
        }
        if (spawn.ShoulderBowId is { } bowId && rt.Entities.Get(bowId) is { } bow)
        {
            bow.Components[Ids.Position] = new StoredComponent { Data = data };
            bow.Components[Ids.ZSyncComponent] = new StoredComponent
            {
                Data = CharacterEntities.ZSyncSnapshot(default, caller.WorkerEntityId, spawn.AvatarId),
            };
        }
        for (var i = 0; i < spawn.GrappleIds.Length; i++)
        {
            if (rt.Entities.Get(spawn.GrappleIds[i]) is not { } grapple) continue;
            grapple.Components[Ids.Position] = new StoredComponent { Data = data };
            grapple.Components[Ids.ZSyncComponent] = new StoredComponent
            {
                Data = CharacterEntities.ZSyncSnapshot(default, caller.WorkerEntityId, spawn.AvatarId,
                    attachmentPath: StarterWeapons.Grapples[i].Attachment),
            };
            rt.ReplaceComponentSnapshot(grapple, StarterWeapons.Hookshot, StarterWeapons.GrappleState(default, false),
                StarterWeapons.GrappleMove(default, false, retract: true, immediate: true));
        }
    }

    private void PublishEnteredWorld(ZenFabricRuntime rt, WorkerConnection caller, ActiveSpawn spawn)
    {
        if (!spawn.SpawnStatsSent && rt.Entities.Get(spawn.CharacterEntityId) is { } character)
        {
            // The HUD caches its starting values and writes the labels only when they change.
            var stats = _progression.Stats(Classes[spawn.CharacterType], spawn.Level);
            rt.ReplaceComponentSnapshot(character, Ids.ActorStatsComponent,
                CharacterEntities.ActorStatsSnapshot(spawn.CharacterType, stats, initialized: true),
                CharacterEntities.InitializeActorStats(spawn.CharacterType, stats));
            var health = new SchemaWriter();
            health.AddInt32(9, spawn.MaxHealth);
            health.AddInt32(29, spawn.MaxHealth);
            rt.ReplaceComponentSnapshot(character, CharacterEntities.PublicActorStats,
                CharacterEntities.PublicStatsSnapshot(spawn.Role, stats, initialized: true, spawn.Experience), health.ToArray());
            spawn.SpawnStatsSent = true;
        }
        UpdateEquippedBow(rt, caller, spawn, spawn.EquippedBow);
        if (spawn.Area is not null && spawn.GrappleIds.Length == 0)
        {
            var (x, y, z) = spawn.CurrentPosition;
            spawn.GrappleIds = Enumerable.Range(0, StarterWeapons.Grapples.Length).Select(index =>
                rt.CreateEntity(0, StarterWeapons.HandGrapple(index, caller.WorkerEntityId, spawn.CharacterEntityId,
                    spawn.AvatarId, spawn.CharacterId, ((float)x, (float)y, (float)z)), caller.WorkerEntityId,
                    shareInArea: true).Id).ToArray();
        }
        rt.SetPlayerArea(caller, spawn.Area?.SharedId ?? "classic");
        _enemies.Enter(rt, caller, spawn.Area);
    }

    private static void UpdateEquippedBow(ZenFabricRuntime rt, WorkerConnection caller, ActiveSpawn spawn, StoredLootItem? bow)
    {
        if (spawn.EquippedBow?.InstanceId != bow?.InstanceId)
        {
            if (spawn.ShoulderBowId is { } previous) rt.DeleteEntity(previous, null);
            spawn.ShoulderBowId = null;
            foreach (var arrow in spawn.Arrows.Values.ToArray()) RemoveArrow(rt, spawn, arrow);
            spawn.BowDraw = spawn.BowKnock = 0;
            spawn.EquippedBow = bow;
        }
        if (bow is null || spawn.ShoulderBowId is not null || !spawn.WorldEntered) return;

        var (x, y, z) = spawn.CurrentPosition;
        spawn.ShoulderBowId = rt.CreateEntity(0, StarterWeapons.ShoulderBow(caller.WorkerEntityId,
            spawn.CharacterEntityId, spawn.AvatarId, bow, spawn.BowAmmo, ((float)x, (float)y, (float)z)),
            caller.WorkerEntityId, shareInArea: true).Id;
    }

    private void SendLoginTeleportIfReady(ZenFabricRuntime rt, WorkerConnection caller, ActiveSpawn spawn)
    {
        if (!spawn.AvatarReady || !spawn.WorldLoaded || spawn.WorldEntered || spawn.TeleportDestination is not null) return;
        if (spawn.SkipLoginTeleport)
        {
            spawn.WorldEntered = true;
            PublishEnteredWorld(rt, caller, spawn);
            return;
        }
        if (rt.Entities.Get(spawn.AvatarId) is { } avatar) SendTeleport(rt, spawn, avatar, spawn.Position);
    }

    private static void SendTeleport(ZenFabricRuntime rt, ActiveSpawn spawn, StoredEntity avatar,
        (float X, float Y, float Z) destination, bool quick = false)
    {
        spawn.TeleportDestination = destination;
        spawn.QuickTeleport = quick;
        spawn.PositionConfirmed = false;
        rt.ReplaceComponentSnapshot(avatar, Ids.TeleportComponent, avatar.Components[Ids.TeleportComponent].Data,
            WorldEntry.TeleportEvent(destination, quick));
    }

    private void Spawn(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req, Account account,
        StoredEntity? player, ref SchemaReader reader)
    {
        var characterId = ReadString(ref reader, 2);
        ReadUInt32(ref reader, 6); // The saved roster supplies the class, role and display name.
        ReadString(ref reader, 9);
        ReadString(ref reader, 3);
        var platform = ReadUInt32(ref reader, 4);
        ReadString(ref reader, 5); // Vivox
        RequireField(ref reader, 7);
        reader.ReadUInt64(); // PSN account
        ReadString(ref reader, 8);
        ReadString(ref reader, 10);
        ReadString(ref reader, 11);
        SkipStringList(ref reader, 12);
        SkipStringList(ref reader, 13);
        var ticket = ReadString(ref reader, 14);
        RequireField(ref reader, 15);
        if (reader.ReadOptionHasValue()) reader.ReadBytesSpan(); // The server chooses the spawn point.
        RequireEnd(ref reader);

        var ticketSession = accounts.BySessionTicket(ticket);
        if (player is null || ticketSession?.PlayFabId != account.PlayFabId)
        {
            rt.SendCommandFailure(caller, req, StatusCode.PermissionDenied, "Character spawn requires a player and its account ticket");
            return;
        }
        var character = accounts.ListCharacters(account).FirstOrDefault(c => c.Id == characterId);
        if (character is null)
        {
            rt.SendCommandFailure(caller, req, StatusCode.NotFound, "Character not found on this account");
            return;
        }
        if (_activeSpawns.TryGetValue(caller.WorkerEntityId, out var active))
        {
            if (active.CharacterId == character.Id)
                rt.SendCommandResponse(caller, req, []);
            else
                rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "A character is already active on this worker");
            return;
        }
        if (rt.ReadyWorkers.Any(w => w.Session?.PlayFabId == account.PlayFabId && _activeSpawns.ContainsKey(w.WorkerEntityId)))
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "A character is already active on this account");
            return;
        }

        var infiniteRealms = character.Type == 4;
        var areas = infiniteRealms ? WorldEntry.Areas(_daily.Today) : [];
        var firstId = rt.Entities.Reserve(CharacterEntities.EntityCount + areas.Length);
        var areaEntities = areas.Select((area, index) => (area, id: firstId + CharacterEntities.EntityCount + index))
            .ToDictionary(entry => entry.area, entry => entry.id);
        var position = CharacterEntities.SpawnPosition(options.ZenFabric.CharacterSpawn, infiniteRealms);
        var level = _progression.LevelAt(character.Experience);
        var stats = _progression.Stats(Classes[character.Type], level);
        var wallet = infiniteRealms ? accounts.ReadCosmeticWallet(account) : new CosmeticWallet(0, []);
        var gear = new CharacterGear(character);
        var graph = CharacterEntities.Build(firstId, player.Id, caller.WorkerEntityId, account.PlayFabId, character,
            platform, position, stats, wallet, gear);
        var spawn = new ActiveSpawn(character.Id, character.Type, character.Role, player.Id, graph.Select(e => e.Id).ToArray(), position,
            areaEntities, CharacterEntities.StartingHealth(stats))
        {
            Level = level,
            Experience = character.Experience,
            EquippedBow = gear.At(-StarterWeapons.ShoulderSlot),
        };
        _activeSpawns.Add(caller.WorkerEntityId, spawn);
        try
        {
            // The live realm acknowledges the command before publishing the character and its rig.
            rt.SendCommandResponse(caller, req, []);
            // Gate paths connect to the bundled maps. Give the client time to load them before travel.
            foreach (var (area, entityId) in areaEntities)
                rt.CreateEntity(entityId, WorldEntry.AreaTrigger(area), caller.WorkerEntityId);
            foreach (var entity in graph) rt.CreateEntity(entity.Id, entity.Components, caller.WorkerEntityId, shareInArea: true);
            rt.ReplaceComponentSnapshot(player, Ids.PlayerCharacterComponent,
                CharacterEntities.PlayerLinks(firstId, firstId + 1, player.Id),
                CharacterEntities.PlayerLinks(firstId, firstId + 1, player.Id, linksOnly: true));
            rt.ReplaceComponentSnapshot(rt.Entities.Get(spawn.AvatarId)!, Ids.PlayerAvatarComponent,
                CharacterEntities.AvatarLinks(player.Id, spawn.LeftHandId, spawn.RightHandId, spawn.HeadId),
                CharacterEntities.AvatarLinks(player.Id, spawn.LeftHandId, spawn.RightHandId, spawn.HeadId, partsOnly: true));
            UpdatePlayerWorld(rt, spawn, player, initialEntry: true);
        }
        catch
        {
            RemoveSpawn(rt, caller);
            throw;
        }
    }

    private static void SkipStringList(ref SchemaReader reader, uint field)
    {
        RequireField(ref reader, field);
        var count = reader.ReadListCount();
        if (count < 0 || count > reader.Remaining / sizeof(int))
            throw new SchemaFormatException("Invalid character list length");
        for (var i = 0; i < count; i++) reader.ReadBytesSpan();
    }

    private SchemaWriter WriteCharacter(StoredCharacter character)
    {
        var className = Classes[character.Type];
        var role = Roles[character.Role];
        var experience = character.Experience.ToString(CultureInfo.InvariantCulture);
        var stats = new Dictionary<string, string>(_progression.Stats(className, _progression.LevelAt(character.Experience)))
        {
            ["current_XP"] = experience,
            ["role_XP"] = JsonSerializer.Serialize(new Dictionary<string, string> { [role] = experience }),
        };
        var data = new Dictionary<string, string>(stats)
        {
            ["appearance"] = JsonSerializer.Serialize(character.Appearance.Select(entry => new { entry.Key, entry.Value })),
            [className] = JsonSerializer.Serialize(stats),
            ["current_class"] = className,
            ["current_role"] = role,
            ["current_health"] = stats["health"],
            ["current_rage"] = stats["rage"],
            ["current_stamina"] = stats["stamina"],
            ["current_essence"] = stats["essence"],
        };
        var row = new SchemaWriter();
        row.AddString(1, character.Id);
        row.AddString(2, character.Name);
        row.AddString(3, className);
        row.AddString(4, role);
        row.AddMap(5, data, (w, key) => w.AddString(1, key), (w, value) => w.AddString(2, value));
        return row;
    }

    private static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 32 && name == name.Trim() && !name.Any(char.IsControl);

    private static Dictionary<uint, string> ReadAppearance(ref SchemaReader reader)
    {
        RequireField(ref reader, 7);
        var count = reader.ReadListCount();
        if (count is < 0 or > 7) throw new SchemaFormatException("Invalid appearance count");
        var appearance = new Dictionary<uint, string>();
        for (var i = 0; i < count; i++)
        {
            var entry = new SchemaReader(reader.ReadBytesSpan());
            var key = ReadUInt32(ref entry, 1);
            var value = ReadString(ref entry, 2);
            RequireEnd(ref entry);
            if (key > 6 || value is null || value.Length > 64 || !appearance.TryAdd(key, value))
                throw new SchemaFormatException("Invalid appearance entry");
        }
        return appearance;
    }

    private static uint ReadUInt32(ref SchemaReader reader, uint field)
    {
        RequireField(ref reader, field);
        return reader.ReadUInt32();
    }

    private static string? ReadString(ref SchemaReader reader, uint field)
    {
        RequireField(ref reader, field);
        return reader.ReadOptionalString();
    }

    private static long ReadOptionalEntity(ref SchemaReader reader, uint field)
    {
        RequireField(ref reader, field);
        return reader.ReadOptionHasValue() ? reader.ReadInt64() : 0;
    }

    private static void RequireField(ref SchemaReader reader, uint field)
    {
        if (reader.ReadFieldId() != field) throw new SchemaFormatException($"Expected character field {field}");
    }

    private static void RequireEnd(ref SchemaReader reader)
    {
        if (reader.HasMore) throw new SchemaFormatException("Unexpected character fields");
    }
}
