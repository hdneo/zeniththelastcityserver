using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LiteNetLib;
using Zenith.Protocol.Components;
using Zenith.Protocol.ZenFabric;
using ZenithServer.Accounts;
using ZenithServer.Storage;

namespace ZenithServer.ZenFabric;

/// <summary>
/// Re-implementation of the "ZenFabric" runtime (Ramen VR's replacement for the SpatialOS runtime).
/// Manages worker connections (LiteNetLib/UDP), entities, components, authority and commands.
///
/// Threading: everything runs on a single runtime thread (PollEvents loop).
/// Access from other threads only via <see cref="Post"/>.
/// </summary>
public sealed class ZenFabricRuntime : BackgroundService, INetEventListener
{
    /// <summary>Virtual worker entity ID of the built-in server game logic (C#).</summary>
    public const long InternalWorkerEntityId = 1;

    private readonly ServerOptions _opts;
    private readonly AccountStore _accounts;
    private readonly IGameLogic _logic;
    private readonly ILogger<ZenFabricRuntime> _log;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Dictionary<int, WorkerConnection> _workers = new();
    private readonly Dictionary<long, (WorkerConnection Caller, long OriginalRequestId, uint ComponentId, uint CommandIndex)> _pendingCommands = new();
    private readonly RSA _rsa;
    private readonly ZfCaptureWriter? _capture;
    private readonly IGameStorage _storage;
    private NetManager? _net;
    private long _nextServerRequestId = 1_000_000;

    public EntityStore Entities { get; } = new();
    public IEnumerable<WorkerConnection> Workers => _workers.Values;
    public IEnumerable<WorkerConnection> ReadyWorkers => _workers.Values.Where(w => w.State == WorkerState.Ready);

    public ZenFabricRuntime(ServerOptions opts, AccountStore accounts, IGameStorage storage, IGameLogic logic,
        ILogger<ZenFabricRuntime> log)
    {
        _opts = opts;
        _accounts = accounts;
        _storage = storage;
        _logic = logic;
        _log = log;
        _rsa = ZfCodec.LoadOrCreateRsa(Path.Combine(opts.DataDirectory, "zenfabric_rsa.pem"));
        if (opts.ZenFabric.CapturePackets)
            _capture = new ZfCaptureWriter(Path.Combine(opts.DataDirectory, "logs", $"zenfabric-{DateTime.Now:yyyyMMdd-HHmmss}.zfcap"));
    }

    /// <summary>Runs an action on the runtime thread.</summary>
    public void Post(Action a) => _queue.Enqueue(a);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Loop(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Loop(CancellationToken ct)
    {
        Entities.RestoreSnapshot(_storage.LoadWorld());
        _net = new NetManager(this)
        {
            DisconnectTimeout = _opts.ZenFabric.DisconnectTimeoutMs,
            AutoRecycle = true,
            UnconnectedMessagesEnabled = false,
        };
        var bindAddress = IPAddress.Parse(_opts.ZenFabric.BindAddress);
        if (!bindAddress.Equals(IPAddress.Any)) _net.IPv6Mode = IPv6Mode.Disabled;
        if (!_net.Start(bindAddress, IPAddress.IPv6Any, _opts.ZenFabric.Port))
        {
            _log.LogCritical("ZenFabric: UDP port {Port} could not be opened", _opts.ZenFabric.Port);
            return;
        }
        _log.LogInformation("ZenFabric runtime listening on UDP {Port} (LiteNetLib, protocol 13, window 512). Public address: {Host}", _opts.ZenFabric.Port, _opts.PublicHost);
        _logic.OnStart(this);

        var lastSave = DateTime.UtcNow;
        var lastTick = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            _net.PollEvents();
            while (_queue.TryDequeue(out var a))
            {
                try { a(); } catch (Exception ex) { _log.LogError(ex, "Error in runtime action"); }
            }
            var now = DateTime.UtcNow;
            if (now - lastTick > TimeSpan.FromMilliseconds(100))
            {
                lastTick = now;
                try { _logic.Tick(this); } catch (Exception ex) { _log.LogError(ex, "Error in GameLogic.Tick"); }
            }
            if (now - lastSave > TimeSpan.FromSeconds(30))
            {
                lastSave = now;
                SaveWorld();
            }
            Thread.Sleep(1);
        }
        SaveWorld();
        _net.Stop();
        _capture?.Dispose();
    }

    private void SaveWorld()
    {
        try { _storage.SaveWorld(Entities.WriteSnapshot()); }
        catch (Exception ex) { _log.LogWarning(ex, "World could not be saved"); }
    }

    // =====================================================================
    //  Senden
    // =====================================================================

    public void Send(WorkerConnection w, ZfOp op)
    {
        if (w.State == WorkerState.Closed) return;
        var payload = op.Serialize();
        _capture?.Write(Direction.ServerToClient, w.Id, payload);
        if (_opts.ZenFabric.VerboseOps && op.Code is not (OpCode.Pong or OpCode.Ping))
            _log.LogInformation("  -> {Worker}: {Op}", w.Id, OpDescriber.Summary(op));
        w.Peer.Send(ZfCodec.FramePayload(payload, w.Key), DeliveryMethod.ReliableOrdered);
    }

    public void Broadcast(ZfOp op, WorkerConnection? except = null, Func<WorkerConnection, bool>? filter = null)
    {
        foreach (var w in ReadyWorkers)
            if (w != except && (filter is null || filter(w)))
                Send(w, op);
    }

    // =====================================================================
    //  LiteNetLib-Events
    // =====================================================================

    public void OnConnectionRequest(ConnectionRequest request)
    {
        // The client connects with NetManager.Connect(host, port, key: joinToken).
        string key;
        try { key = request.Data.GetString(); }
        catch { key = ""; }
        var peer = request.Accept();
        var w = new WorkerConnection { Peer = peer, JoinToken = key };
        _workers[peer.Id] = w;
        _log.LogInformation("ZenFabric: connection from {Remote} (joinToken={Token})", w.Remote, Short(key));
    }

    public void OnPeerConnected(NetPeer peer)
    {
        if (!_workers.TryGetValue(peer.Id, out var w)) return;
        // Handshake step 1: send the RSA public key unencrypted.
        Send(w, ZfCodec.MakeProtocolSettings(_rsa));
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!_workers.Remove(peer.Id, out var w)) return;
        var wasReady = w.State == WorkerState.Ready;
        w.State = WorkerState.Closed;
        _log.LogInformation("ZenFabric: {Worker} disconnected ({Reason})", w.Display, info.Reason);
        if (wasReady)
        {
            try { _logic.OnWorkerDisconnected(this, w); } catch (Exception ex) { _log.LogError(ex, "OnWorkerDisconnected"); }
            if (Entities.Get(w.WorkerEntityId) is not null) DeleteEntity(w.WorkerEntityId, null);
            foreach (var key in _pendingCommands.Where(p => p.Value.Caller == w).Select(p => p.Key).ToList())
                _pendingCommands.Remove(key);
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
        _log.LogWarning("ZenFabric network error {EndPoint}: {Error}", endPoint, socketError);

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        if (!_workers.TryGetValue(peer.Id, out var w)) return;
        byte[] payload;
        try
        {
            payload = ZfCodec.Unframe(reader.GetRemainingBytes(), w.Key);
        }
        catch (Exception ex)
        {
            _log.LogWarning("ZenFabric: invalid frame from {Worker}: {Msg}", w.Display, ex.Message);
            return;
        }
        _capture?.Write(Direction.ClientToServer, w.Id, payload);

        ZfOp op;
        try { op = ZfOps.Parse(payload); }
        catch (Exception ex)
        {
            _log.LogWarning("ZenFabric: op from {Worker} not parsable ({Msg}): {Hex}", w.Display, ex.Message, OpDescriber.Hex(payload, 64));
            return;
        }

        if (_opts.ZenFabric.VerboseOps && op.Code is not (OpCode.Ping or OpCode.Pong))
            _log.LogInformation("  <- {Worker}: {Op}", w.Id, OpDescriber.Summary(op));

        try { Dispatch(w, op); }
        catch (Exception ex) { _log.LogError(ex, "Error handling {Op} from {Worker}", op.Code, w.Display); }
    }

    // =====================================================================
    //  Op-Verarbeitung
    // =====================================================================

    private void Dispatch(WorkerConnection w, ZfOp op)
    {
        if (w.State == WorkerState.AwaitingAuth)
        {
            if (op is AuthWorkerOp auth) HandleAuth(w, auth);
            else _log.LogWarning("ZenFabric: {Op} before AuthWorker from {Worker} ignored", op.Code, w.Display);
            return;
        }

        switch (op)
        {
            case PingOp p: Send(w, new PongOp { RequestId = p.RequestId }); break;
            case LogOp l: _log.LogInformation("[Worker {Id} {Level}] {Logger}: {Msg}", w.Id, l.Level, l.LoggerName, l.Message); break;
            case ReserveEntityIdsRequestOp r:
                Send(w, new ReserveEntityIdsResponseOp { RequestId = r.RequestId, FirstEntityId = Entities.Reserve((int)r.NumberOfEntityIds), NumberOfEntityIds = (int)r.NumberOfEntityIds });
                break;
            case CreateEntityRequestOp c: HandleCreateEntity(w, c); break;
            case DeleteEntityRequestOp d: DeleteEntity(d.EntityId, (w, d.RequestId)); break;
            case EntityQueryRequestOp q: HandleEntityQuery(w, q); break;
            case AddComponentOp a: HandleAddComponent(w, a); break;
            case ComponentUpdateOp u: HandleComponentUpdate(w, u); break;
            case CommandRequestOp c: HandleCommandRequest(w, c); break;
            case CommandResponseOp r: HandleCommandResponse(w, r); break;
            case SystemResponseOp s: _log.LogInformation("SystemResponse from {Worker}: {Data}", w.Display, s.Data); break;
            case DisconnectOp d:
                _log.LogInformation("{Worker} reports disconnect: {Reason}", w.Display, d.Reason);
                w.Peer.Disconnect();
                break;
            default:
                _log.LogWarning("Unhandled operation {Op} from {Worker}", op.Code, w.Display);
                break;
        }
    }

    private void HandleAuth(WorkerConnection w, AuthWorkerOp auth)
    {
        try
        {
            w.Key = ZfCodec.DecryptSymmetricKey(auth.SymmetricKeyParts, _rsa);
        }
        catch (CryptographicException ex)
        {
            _log.LogWarning("AuthWorker from {Remote}: key could not be decrypted ({Msg})", w.Remote, ex.Message);
            w.Peer.Disconnect();
            return;
        }

        if (!string.IsNullOrEmpty(_opts.ZenFabric.GameLogicWorkerToken) && auth.IdentityToken == _opts.ZenFabric.GameLogicWorkerToken)
        {
            w.WorkerType = "UnityGameLogic";
        }
        else
        {
            w.Session = _accounts.ByIdentityToken(auth.IdentityToken);
            if (w.Session is null && !_opts.Auth.AllowUnknownIdentityTokens)
            {
                _log.LogWarning("AuthWorker: unknown identity token from {Remote}", w.Remote);
                Send(w, new DisconnectOp { ConnectionStatusCode = 1, Reason = "Invalid identity token" });
                w.Peer.Disconnect();
                return;
            }
        }

        w.Realm = w.Session?.DeploymentId ?? _opts.Realms.FirstOrDefault()?.Id ?? "";
        w.WorkerEntityId = Entities.Allocate();
        w.WorkerId = w.Session?.PlayFabId ?? $"{w.WorkerType}{w.WorkerEntityId}";
        w.State = WorkerState.Ready;
        _log.LogInformation("ZenFabric: worker authenticated: {Worker} (key {Len} bytes)", w.Display, w.Key.Length);

        Send(w, new InitWorkerOp { WorkerEntityId = w.WorkerEntityId });
        var workerEntity = Entities.Create(w.WorkerEntityId,
        [
            new ComponentData(Ids.Worker, StandardComponents.Worker(w.WorkerId, w.WorkerType)),
        ]);
        MakeVisible(w, workerEntity);

        // Populate the worker's view before signaling that it is synchronized.
        foreach (var e in Entities.Entities.Values.ToList())
            if (e.Id != w.WorkerEntityId && IsVisibleTo(e, w)) MakeVisible(w, e);

        foreach (var (k, v) in _opts.ZenFabric.WorkerFlags)
            Send(w, new FlagUpdateOp { Name = k, Value = v });

        try { _logic.OnWorkerReady(this, w); } catch (Exception ex) { _log.LogError(ex, "OnWorkerReady"); }
    }

    private static bool HasOwnerAccess(StoredEntity entity, WorkerConnection worker) =>
        (!entity.Components.ContainsKey(Ids.Worker) || entity.Id == worker.WorkerEntityId) &&
        (entity.VisibleToWorker is null || entity.VisibleToWorker == worker.WorkerEntityId);

    private static bool IsVisibleTo(StoredEntity entity, WorkerConnection worker) =>
        HasOwnerAccess(entity, worker) || entity.SharedSpace is not null && entity.SharedSpace == worker.SharedSpace;

    private static bool CanSeeComponent(StoredEntity entity, WorkerConnection worker, uint componentId) =>
        HasOwnerAccess(entity, worker) || CharacterEntities.PeerComponents.Contains(componentId);

    public void SetPlayerArea(WorkerConnection worker, string? area)
    {
        (string, string)? space = area is null ? null : (worker.Realm, area);
        if (worker.SharedSpace == space) return;
        worker.SharedSpace = space;
        foreach (var entity in Entities.Entities.Values.Where(e => e.ShareInArea))
        {
            if (entity.VisibleToWorker == worker.WorkerEntityId)
            {
                entity.SharedSpace = space;
                foreach (var peer in ReadyWorkers) RefreshVisibility(peer, entity);
            }
            else
            {
                RefreshVisibility(worker, entity);
            }
        }
    }

    private void RefreshVisibility(WorkerConnection worker, StoredEntity entity)
    {
        if (IsVisibleTo(entity, worker)) MakeVisible(worker, entity);
        else HideEntity(worker, entity.Id);
    }

    private void HideEntity(WorkerConnection worker, long entityId)
    {
        worker.Authority.Remove(entityId);
        if (worker.VisibleEntities.Remove(entityId)) Send(worker, new RemoveEntityOp { EntityId = entityId });
    }

    private void MakeVisible(WorkerConnection w, StoredEntity e)
    {
        if (!w.VisibleEntities.Add(e.Id)) return;
        Send(w, new AddEntityOp { EntityId = e.Id });
        foreach (var (cid, c) in e.Components)
        {
            if (!CanSeeComponent(e, w, cid)) continue;
            Send(w, new AddComponentOp { EntityId = e.Id, Data = new ComponentData(cid, c.Data) });
            foreach (var u in c.UpdateLog)
                Send(w, new ComponentUpdateOp { EntityId = e.Id, ComponentId = cid, Data = u });
        }
        UpdateAuthority(w, e);
    }

    /// <summary>Computes authority from AuthorityDelegation and sends changes.</summary>
    private void UpdateAuthority(WorkerConnection w, StoredEntity e)
    {
        var delegated = e.Delegations.Where(kv => kv.Value == w.WorkerEntityId).Select(kv => kv.Key).ToHashSet();
        w.Authority.TryGetValue(e.Id, out var current);
        current ??= [];
        foreach (var setId in delegated.Except(current))
        {
            var members = ComponentCatalog.Sets.TryGetValue(setId, out var s) ? s.ComponentIds : [];
            Send(w, new ComponentSetAuthorityChangeOp
            {
                EntityId = e.Id,
                ComponentSetId = setId,
                Authority = Authority.Authoritative,
                CanonicalData = e.Components.Where(kv => members.Contains(kv.Key)).Select(kv => new ComponentData(kv.Key, kv.Value.Data)).ToList(),
            });
        }
        foreach (var setId in current.Except(delegated))
            Send(w, new ComponentSetAuthorityChangeOp { EntityId = e.Id, ComponentSetId = setId, Authority = Authority.NotAuthoritative });
        if (delegated.Count > 0) w.Authority[e.Id] = delegated; else w.Authority.Remove(e.Id);
    }

    /// <summary>Which worker entity is authoritative for a component? (0 = nobody)</summary>
    public long AuthoritativeWorkerFor(StoredEntity e, uint componentId)
    {
        foreach (var (setId, workerEntityId) in e.Delegations)
            if (ComponentCatalog.Sets.TryGetValue(setId, out var s) && s.ComponentIds.Contains(componentId))
                return workerEntityId;
        return 0;
    }

    /// <summary>Creates an entity (from workers or the built-in game logic).</summary>
    public StoredEntity CreateEntity(long entityId, IEnumerable<ComponentData> components, long? visibleToWorker = null, bool shareInArea = false)
    {
        var id = entityId != 0 ? entityId : Entities.Allocate();
        var e = Entities.Create(id, components, visibleToWorker, shareInArea);
        if (shareInArea)
            e.SharedSpace = ReadyWorkers.FirstOrDefault(w => w.WorkerEntityId == visibleToWorker)?.SharedSpace;
        foreach (var w in ReadyWorkers.ToList())
            if (IsVisibleTo(e, w)) MakeVisible(w, e);
        return e;
    }

    public void ReplaceComponentSnapshot(StoredEntity entity, uint componentId, byte[] snapshot, byte[] update)
    {
        entity.Components[componentId] = new StoredComponent { Data = snapshot };
        Broadcast(new ComponentUpdateOp { EntityId = entity.Id, ComponentId = componentId, Data = update },
            filter: worker => worker.VisibleEntities.Contains(entity.Id) && CanSeeComponent(entity, worker, componentId));
    }

    private void HandleCreateEntity(WorkerConnection w, CreateEntityRequestOp c)
    {
        if (c.EntityId != 0 && Entities.Get(c.EntityId) is not null)
        {
            Send(w, new CreateEntityResponseOp { RequestId = c.RequestId, Status = StatusCode.ApplicationError, Message = $"Entity {c.EntityId} already exists", EntityId = c.EntityId });
            return;
        }
        var e = CreateEntity(c.EntityId, c.Components);
        Send(w, new CreateEntityResponseOp { RequestId = c.RequestId, Status = StatusCode.Success, EntityId = e.Id });
    }

    public void DeleteEntity(long entityId, (WorkerConnection Worker, long RequestId)? requester)
    {
        var entity = Entities.Get(entityId);
        if (requester is { } client && entity?.VisibleToWorker is not null && IsVisibleTo(entity, client.Worker))
        {
            Send(client.Worker, new DeleteEntityResponseOp
            {
                RequestId = client.RequestId, EntityId = entityId, Status = StatusCode.PermissionDenied,
                Message = "The server manages this entity",
            });
            return;
        }
        var ok = entity is not null && (requester is null || IsVisibleTo(entity, requester.Value.Worker))
            && Entities.Delete(entityId);
        if (!ok)
        {
            if (requester is { } missing)
                Send(missing.Worker, new DeleteEntityResponseOp { RequestId = missing.RequestId, EntityId = entityId, Status = StatusCode.NotFound, Message = "Entity not found" });
            return;
        }
        foreach (var w in ReadyWorkers) HideEntity(w, entityId);
        if (requester is { } r)
            Send(r.Worker, new DeleteEntityResponseOp { RequestId = r.RequestId, EntityId = entityId, Status = StatusCode.Success });
    }

    private void HandleEntityQuery(WorkerConnection w, EntityQueryRequestOp q)
    {
        QueryConstraint constraint;
        try { constraint = QueryConstraint.Parse(q.SerializedConstraint); }
        catch (Exception ex)
        {
            _log.LogWarning("EntityQuery not parsable ({Msg}) – returning all entities", ex.Message);
            constraint = new QueryConstraint();
        }
        var results = Entities.Entities.Values
            .Where(e => IsVisibleTo(e, w) && !e.Components.ContainsKey(Ids.Worker))
            .Where(e => constraint.Matches(e.Id, e.Components.Keys.Where(id => CanSeeComponent(e, w, id)).ToArray(),
                setId => ComponentCatalog.Sets.TryGetValue(setId, out var s) ? s.ComponentIds : []))
            .Select(e => e.ToEntityData(id => CanSeeComponent(e, w, id)))
            .ToList();
        Send(w, new EntityQueryResponseOp { RequestId = q.RequestId, Status = StatusCode.Success, Results = results });
    }

    private void HandleAddComponent(WorkerConnection w, AddComponentOp a)
    {
        var e = Entities.Get(a.EntityId);
        if (e is null || !HasOwnerAccess(e, w)) { _log.LogWarning("AddComponent for unknown entity {Id}", a.EntityId); return; }
        if (e.VisibleToWorker is not null && AuthoritativeWorkerFor(e, a.Data.ComponentId) != w.WorkerEntityId) return;
        e.Components[a.Data.ComponentId] = new StoredComponent { Data = a.Data.Data };
        Broadcast(a, except: w, filter: x => x.VisibleEntities.Contains(e.Id) && CanSeeComponent(e, x, a.Data.ComponentId));
        if (a.Data.ComponentId == Ids.AuthorityDelegation)
            foreach (var x in ReadyWorkers) UpdateAuthority(x, e);
    }

    private void HandleComponentUpdate(WorkerConnection w, ComponentUpdateOp u)
    {
        var e = Entities.Get(u.EntityId);
        if (e is null || !HasOwnerAccess(e, w)) return;
        var auth = AuthoritativeWorkerFor(e, u.ComponentId);
        if (e.VisibleToWorker is not null && auth != w.WorkerEntityId) return;
        if (auth != 0 && auth != w.WorkerEntityId)
            _log.LogDebug("Update {Comp} on {Entity} from non-authoritative worker {Worker}", u.ComponentId, e.Id, w.WorkerEntityId);
        Entities.ApplyUpdate(e, u.ComponentId, u.Data, _opts.ZenFabric.MaxUpdateLogPerComponent);
        if (auth == w.WorkerEntityId && u.ComponentId is Ids.Position or Ids.ZSyncComponent)
            _logic.OnComponentUpdate(this, w, u);
        Broadcast(u, except: w, filter: x => x.VisibleEntities.Contains(e.Id) && CanSeeComponent(e, x, u.ComponentId));
        if (u.ComponentId == Ids.AuthorityDelegation)
            foreach (var x in ReadyWorkers) UpdateAuthority(x, e);
    }

    // ---- Commands ----

    private void HandleCommandRequest(WorkerConnection caller, CommandRequestOp req)
    {
        req.CallerWorkerEntityId = caller.WorkerEntityId;
        var e = Entities.Get(req.EntityId);
        if (e is null || !HasOwnerAccess(e, caller))
        {
            SendCommandFailure(caller, req, StatusCode.NotFound, $"Entity {req.EntityId} not found");
            return;
        }

        // 1) Built-in game logic
        if (_logic.TryHandleCommand(this, caller, req)) return;

        // 2) Forward to the authoritative worker
        var authId = AuthoritativeWorkerFor(e, req.ComponentId);
        var target = ReadyWorkers.FirstOrDefault(x => x.WorkerEntityId == authId);
        if (target is null)
        {
            _log.LogWarning("Command {Comp}#{Idx} on {Entity}: no handler/authoritative worker", ComponentCatalog.ShortNameOf(req.ComponentId), req.CommandIndex, req.EntityId);
            SendCommandFailure(caller, req, StatusCode.ApplicationError, "Not implemented by ZenithServer");
            return;
        }
        var serverReqId = Interlocked.Increment(ref _nextServerRequestId);
        _pendingCommands[serverReqId] = (caller, req.RequestId, req.ComponentId, req.CommandIndex);
        Send(target, new CommandRequestOp
        {
            RequestId = serverReqId, EntityId = req.EntityId, CallerWorkerEntityId = caller.WorkerEntityId,
            ComponentId = req.ComponentId, CommandIndex = req.CommandIndex, Data = req.Data,
        });
    }

    private void HandleCommandResponse(WorkerConnection from, CommandResponseOp resp)
    {
        if (!_pendingCommands.Remove(resp.RequestId, out var p))
        {
            _log.LogWarning("CommandResponse with unknown RequestId {Id} from {Worker}", resp.RequestId, from.Display);
            return;
        }
        resp.RequestId = p.OriginalRequestId;
        Send(p.Caller, resp);
    }

    public void SendCommandResponse(WorkerConnection caller, CommandRequestOp req, byte[] data) =>
        Send(caller, new CommandResponseOp
        {
            RequestId = req.RequestId, EntityId = req.EntityId, Status = StatusCode.Success,
            ComponentId = req.ComponentId, CommandIndex = req.CommandIndex, Data = data,
        });

    public void SendCommandFailure(WorkerConnection caller, CommandRequestOp req, StatusCode status, string message) =>
        Send(caller, new CommandResponseOp
        {
            RequestId = req.RequestId, EntityId = req.EntityId, Status = status, Message = message,
            ComponentId = req.ComponentId, CommandIndex = req.CommandIndex,
        });

    private static string Short(string s) => s.Length <= 10 ? s : s[..10] + "…";
}
