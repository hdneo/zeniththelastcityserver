using LiteNetLib;
using Zenith.Protocol.ZenFabric;
using ZenithServer.Accounts;

namespace ZenithServer.ZenFabric;

public enum WorkerState
{
    /// <summary>Connected, ProtocolSettings sent, waiting for AuthWorker.</summary>
    AwaitingAuth,
    /// <summary>InitWorker sent, worker is active.</summary>
    Ready,
    Closed,
}

/// <summary>One ZenFabric connection (= a SpatialOS "worker", e.g. UnityClient).</summary>
public sealed class WorkerConnection
{
    public required NetPeer Peer { get; init; }
    public required string JoinToken { get; init; }
    public WorkerState State { get; set; } = WorkerState.AwaitingAuth;
    public byte[]? Key { get; set; }
    public long WorkerEntityId { get; set; }
    public string WorkerId { get; set; } = "";
    public string WorkerType { get; set; } = "UnityClient";
    public Session? Session { get; set; }
    public HashSet<long> VisibleEntities { get; } = [];
    /// <summary>Entity -> component sets this worker is currently authoritative for.</summary>
    public Dictionary<long, HashSet<uint>> Authority { get; } = new();
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    public int Id => Peer.Id;
    public string Remote => Peer.EndPoint.ToString();
    public string Display => $"#{Id} {WorkerType}/{WorkerEntityId} {Session?.PlayFabId ?? "?"} ({Remote})";
}
