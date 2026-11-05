using System.Text.Json;
using Zenith.Protocol.Components;
using Zenith.Protocol.ZenFabric;

namespace ZenithServer.ZenFabric;

public sealed class StoredComponent
{
    public byte[] Data { get; set; } = [];
    /// <summary>
    /// Updates that could not be merged into Data (schema unknown → no generic merging possible).
    /// Replayed to workers that join later, after AddComponent.
    /// </summary>
    public List<byte[]> UpdateLog { get; set; } = [];
}

public sealed class StoredEntity
{
    public long Id { get; init; }
    public Dictionary<uint, StoredComponent> Components { get; } = new();

    public Dictionary<uint, long> Delegations =>
        Components.TryGetValue(Ids.AuthorityDelegation, out var c) ? StandardComponents.ReadAuthorityDelegation(c.Data) : new();

    public string? EntityType =>
        Components.TryGetValue(Ids.Metadata, out var c) ? StandardComponents.ReadMetadata(c.Data) : null;

    public EntityData ToEntityData() => new()
    {
        EntityId = Id,
        Components = Components.Select(kv => new ComponentData(kv.Key, kv.Value.Data)).ToList(),
    };
}

/// <summary>In-memory entity database with JSON snapshot (only entities with the Persistence component).</summary>
public sealed class EntityStore
{
    /// <summary>Components with exactly one field: an update contains the complete new state.</summary>
    private static readonly HashSet<uint> SingleFieldComponents = [Ids.Metadata, Ids.Position, Ids.AuthorityDelegation, Ids.OwningWorker];

    private readonly Dictionary<long, StoredEntity> _entities = new();
    private long _nextId = 1000;

    public IReadOnlyDictionary<long, StoredEntity> Entities => _entities;

    public long Allocate() => _nextId++;

    public long Reserve(int count)
    {
        var first = _nextId;
        _nextId += Math.Max(1, count);
        return first;
    }

    public StoredEntity? Get(long id) => _entities.GetValueOrDefault(id);

    public StoredEntity Create(long id, IEnumerable<ComponentData> components)
    {
        var e = new StoredEntity { Id = id };
        foreach (var c in components) e.Components[c.ComponentId] = new StoredComponent { Data = c.Data };
        _entities[id] = e;
        if (id >= _nextId) _nextId = id + 1;
        return e;
    }

    public bool Delete(long id) => _entities.Remove(id);

    /// <summary>Applies an update. true = Data was replaced, false = only written to the UpdateLog.</summary>
    public bool ApplyUpdate(StoredEntity e, uint componentId, byte[] update, int maxLog)
    {
        if (!e.Components.TryGetValue(componentId, out var c))
        {
            e.Components[componentId] = c = new StoredComponent();
        }
        if (SingleFieldComponents.Contains(componentId) && update.Length > 2)
        {
            c.Data = update;
            c.UpdateLog.Clear();
            return true;
        }
        c.UpdateLog.Add(update);
        if (c.UpdateLog.Count > maxLog) c.UpdateLog.RemoveAt(0);
        return false;
    }

    private sealed record SnapshotEntity(long Id, Dictionary<uint, string> Components, Dictionary<uint, List<string>>? Updates);

    public void Save(string path)
    {
        var list = _entities.Values
            .Where(e => e.Components.ContainsKey(Ids.Persistence))
            .Select(e => new SnapshotEntity(e.Id,
                e.Components.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value.Data)),
                e.Components.Where(kv => kv.Value.UpdateLog.Count > 0)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.UpdateLog.Select(Convert.ToBase64String).ToList())))
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new { nextId = _nextId, entities = list }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
    }

    public void Load(string path)
    {
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        _nextId = doc.RootElement.GetProperty("nextId").GetInt64();
        foreach (var el in doc.RootElement.GetProperty("entities").EnumerateArray())
        {
            var s = el.Deserialize<SnapshotEntity>()!;
            var e = Create(s.Id, s.Components.Select(kv => new ComponentData(kv.Key, Convert.FromBase64String(kv.Value))));
            if (s.Updates is not null)
                foreach (var (cid, ups) in s.Updates)
                    e.Components[cid].UpdateLog.AddRange(ups.Select(Convert.FromBase64String));
        }
    }
}
