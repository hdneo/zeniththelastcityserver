using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

namespace ZenithServer.ZenFabric;

/// <summary>
/// This is where the server game logic lives, which the "UnityGameLogic" worker handled in the original
/// (this worker is NOT included in the client and has to be rebuilt).
/// </summary>
public interface IGameLogic
{
    void OnStart(ZenFabricRuntime rt);
    void OnWorkerReady(ZenFabricRuntime rt, WorkerConnection worker);
    void OnWorkerDisconnected(ZenFabricRuntime rt, WorkerConnection worker);
    void OnComponentUpdate(ZenFabricRuntime rt, WorkerConnection worker, ComponentUpdateOp update);
    /// <summary>true = command was handled (a response must have been sent).</summary>
    bool TryHandleCommand(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp request);
    void Tick(ZenFabricRuntime rt);
}

/// <summary>
/// Initializes the realm's login and character-selection services and manages worker-scoped players.
/// </summary>
public sealed class DefaultGameLogic(ServerOptions opts, CharacterCommands characters, ILogger<DefaultGameLogic> log) : IGameLogic
{
    private readonly Dictionary<long, long> _playerEntityByWorker = new();

    public void OnStart(ZenFabricRuntime rt)
    {
        // These singleton IDs are the targets used by the stock client.
        CreateSystem(rt, 2, Ids.PlayerCreator, "PlayerCreator");
        CreateSystem(rt, 4, Ids.CharacterSpawnerComponent, "CharacterSpawner");
        CreateSystem(rt, 7, Ids.LoginSystemComponent, "GameSystems");
    }

    private static void CreateSystem(ZenFabricRuntime rt, long id, uint componentId, string name)
    {
        var existing = rt.Entities.Get(id);
        if (existing is not null && !existing.Components.ContainsKey(componentId))
            throw new InvalidOperationException($"Entity ID {id} is reserved for {name}.");

        foreach (var duplicate in rt.Entities.Entities.Values.Where(e => e.Id != id && e.Components.ContainsKey(componentId)).ToList())
            rt.DeleteEntity(duplicate.Id, null);

        if (existing is not null) return;

        rt.CreateEntity(id,
        [
            new ComponentData(Ids.Metadata, StandardComponents.Metadata(name)),
            new ComponentData(Ids.Position, StandardComponents.Position(0, 0, 0)),
            new ComponentData(componentId, StandardComponents.Empty()),
            new ComponentData(Ids.AuthorityDelegation, StandardComponents.AuthorityDelegation(new Dictionary<uint, long>
            {
                [Ids.DefaultServerSet] = ZenFabricRuntime.InternalWorkerEntityId,
            })),
        ]);
    }

    public void OnWorkerReady(ZenFabricRuntime rt, WorkerConnection worker)
    {
        log.LogInformation("Worker ready: {Worker}. Waiting for PlayerCreator.CreatePlayer or other commands …", worker.Display);
    }

    public void OnWorkerDisconnected(ZenFabricRuntime rt, WorkerConnection worker)
    {
        characters.RemoveSpawn(rt, worker);
        if (_playerEntityByWorker.Remove(worker.WorkerEntityId, out var playerEntity))
        {
            log.LogInformation("Removing player entity {Id} of {Worker}", playerEntity, worker.Display);
            rt.DeleteEntity(playerEntity, null);
        }
    }

    public void OnComponentUpdate(ZenFabricRuntime rt, WorkerConnection worker, ComponentUpdateOp update) =>
        characters.OnAvatarMovement(rt, worker, update);

    public bool TryHandleCommand(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req)
    {
        var e = rt.Entities.Get(req.EntityId);
        if (e is null || !e.Components.ContainsKey(req.ComponentId)) return false;

        switch (req.ComponentId, req.CommandIndex)
        {
            case (Ids.LoginSystemComponent, 1):
                CheckPlayerStatus(rt, caller, req);
                return true;
            case (Ids.PlayerCreator, Ids.CreatePlayerCommandIndex):
                CreatePlayer(rt, caller, req);
                return true;
            case (Ids.ServerTimeComponent, 1):
                SendServerTime(rt, caller, req);
                return true;
            case (Ids.CharacterSpawnerComponent, 1 or 2 or 3 or 4):
                var playerId = _playerEntityByWorker.GetValueOrDefault(caller.WorkerEntityId);
                return characters.TryHandle(rt, caller, req, rt.Entities.Get(playerId));
            case (Ids.NetworkedObjectInteractionComponent, 2) when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.RegisterInteractionHands(rt, caller, req, e);
            case (CharacterEntities.Vfx, 1 or 2) when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.BroadcastVfx(rt, caller, req, e);
            case (Ids.PlayerLifecycleComponent, 1) when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.RecoverCharacter(rt, caller, req, e);
            case (CosmeticShop.Wallet, CosmeticShop.StartSession or CosmeticShop.StartTransaction or CosmeticShop.Purchase)
                when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.HandleCosmeticShop(rt, caller, req, e);
            case (CharacterEntities.Inventory, CharacterGear.MoveCommand) when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.MoveInventory(rt, caller, req, e);
            case (EnemyLoot.Component or EnemyLoot.Ownership or StarterWeapons.Grabbable, 1)
                when e.VisibleToWorker == caller.WorkerEntityId && e.Components.ContainsKey(EnemyLoot.Component):
                return characters.HandleEnemyLoot(rt, caller, req, e);
            case (StarterWeapons.Grabbable or EnemyLoot.Ownership, 1) when e.EntityType == FinaleSoulCollection.OrbPrefab:
                return characters.HandleFinaleSoul(rt, caller, req);
            case (StarterWeapons.Bow, 1 or 2 or 4) when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.HandleBow(rt, caller, req, e);
            case (StarterWeapons.Hookshot, 1 or 2) when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.HandleGrapple(rt, caller, req, e);
            case (StarterWeapons.Arrow, 1 or 2 or 3) when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.HandleArrow(rt, caller, req, e);
            case (Ids.ActorStatsComponent, 15) when e.VisibleToWorker == caller.WorkerEntityId:
            case (StarterWeapons.Godstone, 9) when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.TryStarterAbility(rt, caller, req, e);
            case (StarterWeapons.Projectile, 3) when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.HandleProjectileHit(rt, caller, req, e);
            case (StarterWeapons.Projectile, 4) when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.EndStarterProjectile(rt, caller, req, e);
            case (CharacterEntities.Friend, 1) when e.VisibleToWorker == caller.WorkerEntityId:
                if (req.Data.Length != 0)
                    rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Invalid friend list request");
                else
                {
                    var friends = new SchemaWriter();
                    friends.AddEmptyList(1);
                    rt.SendCommandResponse(caller, req, friends.ToArray());
                }
                return true;
            case (Ids.PlayerWorldComponent, 2 or 4 or 10) when e.VisibleToWorker == caller.WorkerEntityId:
            case (Ids.TeleportComponent, 1 or 3 or 4) when e.VisibleToWorker == caller.WorkerEntityId:
                return characters.TryHandleWorldEntry(rt, caller, req, e);
        }

        if (rt.AuthoritativeWorkerFor(e, req.ComponentId) == ZenFabricRuntime.InternalWorkerEntityId)
        {
            log.LogWarning("TODO command not implemented: {Comp}({Id})#{Idx} on entity {Entity} ({Type}), {Len} bytes: {Hex}",
                ComponentCatalog.NameOf(req.ComponentId), req.ComponentId, req.CommandIndex, req.EntityId, e.EntityType,
                req.Data.Length, OpDescriber.Hex(req.Data, 128));
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Not implemented by ZenithServer");
            return true;
        }
        return false;
    }

    private void CheckPlayerStatus(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req)
    {
        var playFabId = caller.Session?.PlayFabId;
        var loggedIn = playFabId is not null && rt.ReadyWorkers.Any(w =>
            w != caller && w.Session?.PlayFabId == playFabId && _playerEntityByWorker.ContainsKey(w.WorkerEntityId));
        var response = new SchemaWriter();
        response.AddBool(1, loggedIn);
        response.AddBool(2, false);
        rt.SendCommandResponse(caller, req, response.ToArray());
    }

    private static void SendServerTime(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req)
    {
        var reader = new SchemaReader(req.Data);
        if (req.Data.Length != sizeof(ushort) + sizeof(int) || reader.ReadFieldId() != 1)
        {
            rt.SendCommandFailure(caller, req, StatusCode.ApplicationError, "Invalid time request");
            return;
        }
        var response = new SchemaWriter();
        response.AddInt64(1, Environment.TickCount64);
        response.AddInt32(2, reader.ReadInt32());
        rt.SendCommandResponse(caller, req, response.ToArray());
    }

    private void CreatePlayer(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req)
    {
        if (_playerEntityByWorker.TryGetValue(caller.WorkerEntityId, out var existing) && rt.Entities.Get(existing) is not null)
        {
            rt.SendCommandResponse(caller, req, StandardComponents.CreatePlayerResponse(existing));
            return;
        }
        var spawn = opts.ZenFabric.DefaultSpawn;
        var auth = new SchemaWriter();
        auth.AddString(1, caller.Session?.PlayFabId);
        auth.AddString(2, null); // Session tickets are supplied by the client, never replicated.
        auth.AddInt32(3, 0);
        var createdObjects = new SchemaWriter();
        createdObjects.AddEmptyList(1);
        var player = rt.CreateEntity(0,
        [
            new ComponentData(Ids.Metadata, StandardComponents.Metadata("Player")),
            new ComponentData(Ids.Position, StandardComponents.Position(spawn.ElementAtOrDefault(0), spawn.ElementAtOrDefault(1), spawn.ElementAtOrDefault(2))),
            new ComponentData(Ids.OwningWorker, StandardComponents.OwningWorker(caller.WorkerEntityId)),
            new ComponentData(Ids.PlayerAuthComponent, auth.ToArray()),
            new ComponentData(Ids.GarbageCollectionComponent, createdObjects.ToArray()),
            new ComponentData(Ids.PlayerNotificationComponent, StandardComponents.Empty()),
            new ComponentData(Ids.PlayerCharacterComponent, CharacterEntities.PlayerLinks(0, 0, 0)),
            new ComponentData(Ids.PlayerWorldComponent, WorldEntry.PlayerWorld(enterMainWorld: false)),
            new ComponentData(Ids.ServerTimeComponent, StandardComponents.Empty()),
            new ComponentData(Ids.PlayerHeartbeatClient, StandardComponents.Empty()),
            new ComponentData(Ids.PlayerHeartbeatServer, StandardComponents.Empty()),
            new ComponentData(Ids.AuthorityDelegation, StandardComponents.AuthorityDelegation(new Dictionary<uint, long>
            {
                [Ids.PlayerClientSet] = caller.WorkerEntityId,
                [Ids.PlayerServerSet] = ZenFabricRuntime.InternalWorkerEntityId,
            })),
        ], visibleToWorker: caller.WorkerEntityId, shareInArea: true);
        _playerEntityByWorker[caller.WorkerEntityId] = player.Id;
        rt.SendCommandResponse(caller, req, StandardComponents.CreatePlayerResponse(player.Id));
    }

    public void Tick(ZenFabricRuntime rt) => characters.Tick(rt);
}
