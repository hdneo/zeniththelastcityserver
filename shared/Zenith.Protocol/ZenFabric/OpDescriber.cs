using Zenith.Protocol.Components;

namespace Zenith.Protocol.ZenFabric;

/// <summary>Turns operations into readable, JSON-serializable structures (for logs/sniffer).</summary>
public static class OpDescriber
{
    public static int MaxHexBytes { get; set; } = 256;

    public static string Hex(ReadOnlySpan<byte> data, int max = -1)
    {
        if (max < 0) max = MaxHexBytes;
        var n = Math.Min(data.Length, max);
        var s = Convert.ToHexString(data[..n]);
        return n < data.Length ? s + $"...(+{data.Length - n})" : s;
    }

    private static object Comp(ComponentData c) => new
    {
        id = c.ComponentId,
        name = ComponentCatalog.NameOf(c.ComponentId),
        size = c.Data.Length,
        hex = Hex(c.Data),
    };

    public static object Describe(ZfOp op) => op switch
    {
        ProtocolSettingsOp p => new { modulusBits = p.Modulus.Length * 8, modulus = Hex(p.Modulus, 512), exponent = Hex(p.Exponent) },
        AuthWorkerOp a => new { identityToken = a.IdentityToken, keyParts = a.SymmetricKeyParts.Count, keyPartSizes = a.SymmetricKeyParts.Select(x => x.Length).ToArray() },
        InitWorkerOp i => new { workerEntityId = i.WorkerEntityId },
        FlagUpdateOp f => new { name = f.Name, value = f.Value },
        AddEntityOp a => new { entityId = a.EntityId },
        RemoveEntityOp r => new { entityId = r.EntityId },
        AddComponentOp a => new { entityId = a.EntityId, component = Comp(a.Data) },
        ComponentUpdateOp u => new { entityId = u.EntityId, componentId = u.ComponentId, name = ComponentCatalog.NameOf(u.ComponentId), size = u.Data.Length, hex = Hex(u.Data) },
        ComponentSetAuthorityChangeOp c => new
        {
            entityId = c.EntityId, componentSetId = c.ComponentSetId,
            componentSet = ComponentCatalog.Sets.TryGetValue(c.ComponentSetId, out var s) ? s.Name : null,
            authority = c.Authority.ToString(), components = c.CanonicalData.Select(Comp).ToArray(),
        },
        CreateEntityRequestOp c => new { requestId = c.RequestId, entityId = c.EntityId, components = c.Components.Select(Comp).ToArray() },
        CreateEntityResponseOp c => new { requestId = c.RequestId, status = c.Status.ToString(), message = c.Message, entityId = c.EntityId },
        DeleteEntityRequestOp d => new { requestId = d.RequestId, entityId = d.EntityId },
        DeleteEntityResponseOp d => new { requestId = d.RequestId, entityId = d.EntityId, status = d.Status.ToString(), message = d.Message },
        CommandRequestOp c => new
        {
            requestId = c.RequestId, entityId = c.EntityId, callerWorkerEntityId = c.CallerWorkerEntityId,
            componentId = c.ComponentId, name = ComponentCatalog.NameOf(c.ComponentId), commandIndex = c.CommandIndex,
            size = c.Data.Length, hex = Hex(c.Data),
        },
        CommandResponseOp c => new
        {
            requestId = c.RequestId, entityId = c.EntityId, status = c.Status.ToString(), message = c.Message,
            componentId = c.ComponentId, name = ComponentCatalog.NameOf(c.ComponentId), commandIndex = c.CommandIndex,
            size = c.Data.Length, hex = Hex(c.Data),
        },
        EntityQueryRequestOp q => new { requestId = q.RequestId, constraint = TryConstraint(q.SerializedConstraint), hex = Hex(q.SerializedConstraint) },
        EntityQueryResponseOp q => new
        {
            requestId = q.RequestId, status = q.Status.ToString(), message = q.Message,
            results = q.Results.Select(e => new { entityId = e.EntityId, components = e.Components.Select(Comp).ToArray() }).ToArray(),
        },
        SystemRequestOp s => new { requestId = s.RequestId, command = s.Command, args = s.Args },
        SystemResponseOp s => new { requestId = s.RequestId, data = s.Data },
        PingOp p => new { requestId = p.RequestId },
        PongOp p => new { requestId = p.RequestId },
        ReserveEntityIdsRequestOp r => new { requestId = r.RequestId, count = r.NumberOfEntityIds },
        ReserveEntityIdsResponseOp r => new { requestId = r.RequestId, status = r.Status.ToString(), message = r.Message, firstEntityId = r.FirstEntityId, count = r.NumberOfEntityIds },
        LogOp l => new { level = l.Level.ToString(), logger = l.LoggerName, message = l.Message, entityId = l.EntityId },
        DisconnectOp d => new { code = d.ConnectionStatusCode, reason = d.Reason },
        UnknownOp u => new { rawOpCode = u.RawCode, size = u.Body.Length, hex = Hex(u.Body) },
        _ => new { },
    };

    private static string TryConstraint(byte[] data)
    {
        try { return QueryConstraint.Parse(data).ToString(); }
        catch (Exception ex) { return "<not parsable: " + ex.Message + ">"; }
    }

    /// <summary>One-line summary for the console.</summary>
    public static string Summary(ZfOp op) => op switch
    {
        ProtocolSettingsOp p => $"ProtocolSettings RSA-{p.Modulus.Length * 8}",
        AuthWorkerOp a => $"AuthWorker token={Trunc(a.IdentityToken, 24)} parts={a.SymmetricKeyParts.Count}",
        InitWorkerOp i => $"InitWorker workerEntity={i.WorkerEntityId}",
        FlagUpdateOp f => $"FlagUpdate {f.Name}={f.Value}",
        AddEntityOp a => $"AddEntity {a.EntityId}",
        RemoveEntityOp r => $"RemoveEntity {r.EntityId}",
        AddComponentOp a => $"AddComponent e={a.EntityId} {ComponentCatalog.ShortNameOf(a.Data.ComponentId)}({a.Data.ComponentId}) {a.Data.Data.Length}B",
        ComponentUpdateOp u => $"ComponentUpdate e={u.EntityId} {ComponentCatalog.ShortNameOf(u.ComponentId)}({u.ComponentId}) {u.Data.Length}B",
        ComponentSetAuthorityChangeOp c => $"AuthorityChange e={c.EntityId} set={c.ComponentSetId} {c.Authority} ({c.CanonicalData.Count} comps)",
        CreateEntityRequestOp c => $"CreateEntityRequest req={c.RequestId} e={c.EntityId} [{string.Join(",", c.Components.Select(x => ComponentCatalog.ShortNameOf(x.ComponentId)))}]",
        CreateEntityResponseOp c => $"CreateEntityResponse req={c.RequestId} {c.Status} e={c.EntityId} {c.Message}",
        DeleteEntityRequestOp d => $"DeleteEntityRequest req={d.RequestId} e={d.EntityId}",
        DeleteEntityResponseOp d => $"DeleteEntityResponse req={d.RequestId} e={d.EntityId} {d.Status}",
        CommandRequestOp c => $"CommandRequest req={c.RequestId} e={c.EntityId} {ComponentCatalog.ShortNameOf(c.ComponentId)}({c.ComponentId})#{c.CommandIndex} {c.Data.Length}B",
        CommandResponseOp c => $"CommandResponse req={c.RequestId} e={c.EntityId} {c.Status} {ComponentCatalog.ShortNameOf(c.ComponentId)}#{c.CommandIndex} {c.Data.Length}B {c.Message}",
        EntityQueryRequestOp q => $"EntityQueryRequest req={q.RequestId} {TryConstraint(q.SerializedConstraint)}",
        EntityQueryResponseOp q => $"EntityQueryResponse req={q.RequestId} {q.Status} results={q.Results.Count}",
        SystemRequestOp s => $"SystemRequest req={s.RequestId} {s.Command} {Trunc(s.Args, 60)}",
        SystemResponseOp s => $"SystemResponse req={s.RequestId} {Trunc(s.Data, 60)}",
        PingOp p => $"Ping {p.RequestId}",
        PongOp p => $"Pong {p.RequestId}",
        ReserveEntityIdsRequestOp r => $"ReserveEntityIds req={r.RequestId} n={r.NumberOfEntityIds}",
        ReserveEntityIdsResponseOp r => $"ReserveEntityIdsResponse req={r.RequestId} first={r.FirstEntityId} n={r.NumberOfEntityIds}",
        LogOp l => $"Log [{l.Level}] {l.LoggerName}: {Trunc(l.Message, 120)}",
        DisconnectOp d => $"Disconnect code={d.ConnectionStatusCode} {d.Reason}",
        UnknownOp u => $"Unknown op {u.RawCode} {u.Body.Length}B",
        _ => op.Code.ToString(),
    };

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
