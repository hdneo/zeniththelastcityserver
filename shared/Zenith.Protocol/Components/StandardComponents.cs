using Zenith.Protocol.Schema;

namespace Zenith.Protocol.Components;

/// <summary>
/// Encoding of the SpatialOS standard components (improbable/standard_library.schema),
/// verified against the generated serializers in the client.
/// </summary>
public static class StandardComponents
{
    /// <summary>Improbable.Metadata (53): 1 string entity_type</summary>
    public static byte[] Metadata(string entityType)
    {
        var w = new SchemaWriter();
        w.AddString(1, entityType);
        return w.ToArray();
    }

    public static string? ReadMetadata(ReadOnlySpan<byte> data)
    {
        var r = new SchemaReader(data);
        while (r.HasMore)
        {
            var f = r.ReadFieldId();
            if (f == 1) return r.ReadOptionalString();
            return null;
        }
        return null;
    }

    /// <summary>Improbable.Position (54): 1 Coordinates coords { 1 double x, 2 double y, 3 double z }</summary>
    public static byte[] Position(double x, double y, double z)
    {
        var w = new SchemaWriter();
        w.AddObject(1, c => { c.AddDouble(1, x); c.AddDouble(2, y); c.AddDouble(3, z); });
        return w.ToArray();
    }

    public static (double X, double Y, double Z)? ReadPosition(ReadOnlySpan<byte> data)
    {
        try
        {
            var r = new SchemaReader(data);
            if (r.ReadFieldId() != 1) return null;
            var c = new SchemaReader(r.ReadBytesSpan());
            double x = 0, y = 0, z = 0;
            while (c.HasMore)
            {
                switch (c.ReadFieldId())
                {
                    case 1: x = c.ReadDouble(); break;
                    case 2: y = c.ReadDouble(); break;
                    case 3: z = c.ReadDouble(); break;
                    default: return (x, y, z);
                }
            }
            return (x, y, z);
        }
        catch { return null; }
    }

    /// <summary>Improbable.Persistence (55): leer</summary>
    public static byte[] Empty() => [];

    /// <summary>Improbable.AuthorityDelegation (65): 1 map&lt;uint32 component_set_id, int64 worker_entity_id&gt;</summary>
    public static byte[] AuthorityDelegation(IReadOnlyDictionary<uint, long> delegations)
    {
        var w = new SchemaWriter();
        w.AddMap(1, delegations, (e, k) => e.AddUInt32(1, k), (e, v) => e.AddInt64(2, v));
        return w.ToArray();
    }

    public static Dictionary<uint, long> ReadAuthorityDelegation(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<uint, long>();
        var r = new SchemaReader(data);
        if (!r.HasMore || r.ReadFieldId() != 1) return result;
        foreach (var entry in r.ReadObjectList())
        {
            var er = new SchemaReader(entry);
            uint key = 0; long val = 0;
            while (er.HasMore)
            {
                switch (er.ReadFieldId())
                {
                    case 1: key = er.ReadUInt32(); break;
                    case 2: val = er.ReadInt64(); break;
                    default: goto done;
                }
            }
            done:
            result[key] = val;
        }
        return result;
    }

    /// <summary>Improbable.Gdk.PlayerLifecycle.OwningWorker (13003): 1 EntityId worker_entity_id</summary>
    public static byte[] OwningWorker(long workerEntityId)
    {
        var w = new SchemaWriter();
        w.AddEntityId(1, workerEntityId);
        return w.ToArray();
    }

    /// <summary>Improbable.Restricted.Worker (60): worker identity and connection state.</summary>
    public static byte[] Worker(string workerId, string workerType)
    {
        var w = new SchemaWriter();
        w.AddString(1, workerId);
        w.AddString(2, workerType);
        w.AddObject(3, connection =>
        {
            connection.AddUInt64(1, 0);
            connection.AddString(2, null);
        });
        return w.ToArray();
    }

    /// <summary>PlayerCreator.CreatePlayer Request: 1 bytes serialized_arguments</summary>
    public static byte[]? ReadCreatePlayerArguments(ReadOnlySpan<byte> data)
    {
        try
        {
            var r = new SchemaReader(data);
            return r.HasMore && r.ReadFieldId() == 1 ? r.ReadBytes() : null;
        }
        catch { return null; }
    }

    /// <summary>PlayerCreator.CreatePlayer Response: 1 EntityId created_entity_id</summary>
    public static byte[] CreatePlayerResponse(long createdEntityId)
    {
        var w = new SchemaWriter();
        w.AddEntityId(1, createdEntityId);
        return w.ToArray();
    }
}

/// <summary>
/// Improbable.QueryConstraint – content of EntityQueryRequestOp.SerializedConstraint.
///   1..6  option&lt;Sphere/Cylinder/Box/RelativeSphere/RelativeCylinder/RelativeBox&gt; (object)
///   7     option&lt;EntityId&gt;   entity_id_constraint
///   8     option&lt;uint32&gt;     component_constraint
///   9     list&lt;QueryConstraint&gt; and_constraint
///   10    list&lt;QueryConstraint&gt; or_constraint
///   11    option&lt;SelfConstraint&gt;
///   12    option&lt;uint32&gt;     component_set_constraint
/// </summary>
public sealed class QueryConstraint
{
    public long? EntityId { get; set; }
    public uint? ComponentId { get; set; }
    public uint? ComponentSetId { get; set; }
    public bool Self { get; set; }
    public List<QueryConstraint> And { get; } = [];
    public List<QueryConstraint> Or { get; } = [];
    /// <summary>Spatial constraints (1..6) are only detected, not evaluated.</summary>
    public List<uint> SpatialConstraintFields { get; } = [];

    public static QueryConstraint Parse(ReadOnlySpan<byte> data)
    {
        var q = new QueryConstraint();
        var r = new SchemaReader(data);
        while (r.HasMore)
        {
            var f = r.ReadFieldId();
            switch (f)
            {
                case >= 1 and <= 6:
                case 11:
                    if (r.ReadOptionHasValue())
                    {
                        r.ReadBytesSpan();
                        if (f == 11) q.Self = true; else q.SpatialConstraintFields.Add(f);
                    }
                    break;
                case 7:
                    if (r.ReadOptionHasValue()) q.EntityId = r.ReadInt64();
                    break;
                case 8:
                    if (r.ReadOptionHasValue()) q.ComponentId = r.ReadUInt32();
                    break;
                case 12:
                    if (r.ReadOptionHasValue()) q.ComponentSetId = r.ReadUInt32();
                    break;
                case 9:
                    foreach (var o in r.ReadObjectList()) q.And.Add(Parse(o));
                    break;
                case 10:
                    foreach (var o in r.ReadObjectList()) q.Or.Add(Parse(o));
                    break;
                default:
                    throw new SchemaFormatException($"Unknown QueryConstraint field {f}");
            }
        }
        return q;
    }

    /// <summary>Evaluates the constraint against an entity (spatial constraints count as satisfied).</summary>
    public bool Matches(long entityId, ICollection<uint> componentIds, Func<uint, IEnumerable<uint>>? setMembers = null)
    {
        if (EntityId is { } id && id != entityId) return false;
        if (ComponentId is { } c && !componentIds.Contains(c)) return false;
        if (ComponentSetId is { } s && setMembers is not null && !setMembers(s).All(componentIds.Contains)) return false;
        if (And.Count > 0 && !And.All(a => a.Matches(entityId, componentIds, setMembers))) return false;
        if (Or.Count > 0 && !Or.Any(a => a.Matches(entityId, componentIds, setMembers))) return false;
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (EntityId is { } e) parts.Add($"entity={e}");
        if (ComponentId is { } c) parts.Add($"component={c}({ComponentCatalog.ShortNameOf(c)})");
        if (ComponentSetId is { } s) parts.Add($"componentSet={s}");
        if (Self) parts.Add("self");
        if (SpatialConstraintFields.Count > 0) parts.Add($"spatial[{string.Join(',', SpatialConstraintFields)}]");
        if (And.Count > 0) parts.Add("AND(" + string.Join(", ", And) + ")");
        if (Or.Count > 0) parts.Add("OR(" + string.Join(", ", Or) + ")");
        return parts.Count == 0 ? "<all>" : string.Join(" ", parts);
    }
}
