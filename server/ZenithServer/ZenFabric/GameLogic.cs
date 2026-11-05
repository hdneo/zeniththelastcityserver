using Zenith.Protocol.Components;
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
    /// <summary>true = command was handled (a response must have been sent).</summary>
    bool TryHandleCommand(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp request);
    void Tick(ZenFabricRuntime rt);
}

/// <summary>
/// Minimal startup logic:
///  * creates a PlayerCreator entity (Improbable.Gdk.PlayerLifecycle, component 13000),
///  * answers PlayerCreator.CreatePlayer with a new player entity,
///  * removes player entities on disconnect,
///  * answers all other commands the server logic is authoritative for with "not implemented".
///
/// The real player entity of the original contains many Zenith components (see
/// docs/data/component_sets.txt: PlayerServerSet, PlayerCharacterServerSet, ...). Which of them the client
/// strictly needs should be captured with the sniffer on the live server (until Dec 18).
/// </summary>
public sealed class DefaultGameLogic(ServerOptions opts, ILogger<DefaultGameLogic> log) : IGameLogic
{
    private readonly Dictionary<long, long> _playerEntityByWorker = new();

    public void OnStart(ZenFabricRuntime rt)
    {
        var existing = rt.Entities.Entities.Values.FirstOrDefault(e => e.Components.ContainsKey(Ids.PlayerCreator));
        if (existing is not null)
        {
            log.LogInformation("PlayerCreator entity exists: {Id}", existing.Id);
            return;
        }
        var e = rt.CreateEntity(0,
        [
            new ComponentData(Ids.Metadata, StandardComponents.Metadata("PlayerCreator")),
            new ComponentData(Ids.Position, StandardComponents.Position(0, 0, 0)),
            new ComponentData(Ids.Persistence, StandardComponents.Empty()),
            new ComponentData(Ids.PlayerCreator, StandardComponents.Empty()),
            new ComponentData(Ids.AuthorityDelegation, StandardComponents.AuthorityDelegation(new Dictionary<uint, long>
            {
                [Ids.DefaultServerSet] = ZenFabricRuntime.InternalWorkerEntityId,
            })),
        ]);
        log.LogInformation("PlayerCreator entity created: {Id}", e.Id);
    }

    public void OnWorkerReady(ZenFabricRuntime rt, WorkerConnection worker)
    {
        log.LogInformation("Worker ready: {Worker}. Waiting for PlayerCreator.CreatePlayer or other commands …", worker.Display);
    }

    public void OnWorkerDisconnected(ZenFabricRuntime rt, WorkerConnection worker)
    {
        if (_playerEntityByWorker.Remove(worker.WorkerEntityId, out var playerEntity))
        {
            log.LogInformation("Removing player entity {Id} of {Worker}", playerEntity, worker.Display);
            rt.DeleteEntity(playerEntity, null);
        }
    }

    public bool TryHandleCommand(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req)
    {
        var e = rt.Entities.Get(req.EntityId);
        if (e is null) return false;

        if (req.ComponentId == Ids.PlayerCreator && req.CommandIndex == Ids.CreatePlayerCommandIndex)
        {
            CreatePlayer(rt, caller, req);
            return true;
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

    private void CreatePlayer(ZenFabricRuntime rt, WorkerConnection caller, CommandRequestOp req)
    {
        var args = StandardComponents.ReadCreatePlayerArguments(req.Data);
        log.LogInformation("CreatePlayer from {Worker}, arguments ({Len} bytes): {Hex}", caller.Display, args?.Length ?? 0, OpDescriber.Hex(args ?? [], 256));

        if (_playerEntityByWorker.TryGetValue(caller.WorkerEntityId, out var old))
            rt.DeleteEntity(old, null);

        var spawn = opts.ZenFabric.DefaultSpawn;
        // Minimal player. TODO: fill Zenith components (PlayerAuthComponent 1000, PlayerCharacterComponent 1011, ...)
        // with real data – derive the format from sniffer captures.
        var player = rt.CreateEntity(0,
        [
            new ComponentData(Ids.Metadata, StandardComponents.Metadata("Player")),
            new ComponentData(Ids.Position, StandardComponents.Position(spawn.ElementAtOrDefault(0), spawn.ElementAtOrDefault(1), spawn.ElementAtOrDefault(2))),
            new ComponentData(Ids.OwningWorker, StandardComponents.OwningWorker(caller.WorkerId)),
            new ComponentData(Ids.PlayerHeartbeatClient, StandardComponents.Empty()),
            new ComponentData(Ids.PlayerHeartbeatServer, StandardComponents.Empty()),
            new ComponentData(Ids.AuthorityDelegation, StandardComponents.AuthorityDelegation(new Dictionary<uint, long>
            {
                [Ids.PlayerClientSet] = caller.WorkerEntityId,
                [Ids.PlayerServerSet] = ZenFabricRuntime.InternalWorkerEntityId,
            })),
        ]);
        _playerEntityByWorker[caller.WorkerEntityId] = player.Id;
        rt.SendCommandResponse(caller, req, StandardComponents.CreatePlayerResponse(player.Id));
    }

    public void Tick(ZenFabricRuntime rt) { }
}
