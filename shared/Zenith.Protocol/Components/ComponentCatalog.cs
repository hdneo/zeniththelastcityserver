using System.Reflection;
using System.Text.RegularExpressions;

namespace Zenith.Protocol.Components;

/// <summary>
/// Component IDs and component sets, extracted from the client (docs/data/*.txt|tsv).
/// </summary>
public static partial class ComponentCatalog
{
    private static readonly Lazy<Dictionary<uint, string>> _names = new(LoadNames);
    private static readonly Lazy<Dictionary<uint, ComponentSetInfo>> _sets = new(LoadSets);

    public static IReadOnlyDictionary<uint, string> Names => _names.Value;
    public static IReadOnlyDictionary<uint, ComponentSetInfo> Sets => _sets.Value;

    public static string NameOf(uint componentId) =>
        _names.Value.TryGetValue(componentId, out var n) ? n : $"Component#{componentId}";

    public static string ShortNameOf(uint componentId)
    {
        var n = NameOf(componentId);
        var i = n.LastIndexOf('.');
        return i >= 0 ? n[(i + 1)..] : n;
    }

    /// <summary>All sets that contain a component.</summary>
    public static IEnumerable<ComponentSetInfo> SetsContaining(uint componentId) =>
        _sets.Value.Values.Where(s => s.ComponentIds.Contains(componentId));

    private static string ReadResource(string name)
    {
        var asm = typeof(ComponentCatalog).Assembly;
        using var s = asm.GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"Resource {name} missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static Dictionary<uint, string> LoadNames()
    {
        var d = new Dictionary<uint, string>();
        foreach (var line in ReadResource("component_ids.tsv").Split('\n'))
        {
            var p = line.Trim('\r').Split('\t');
            if (p.Length == 3 && p[0] == "ComponentId" && uint.TryParse(p[1], out var id))
                d[id] = p[2];
        }
        return d;
    }

    [GeneratedRegex(@"^set (\S+) = (\d+)")]
    private static partial Regex SetLine();

    [GeneratedRegex(@"^\s+(\d+)\s+(\S+)")]
    private static partial Regex MemberLine();

    private static Dictionary<uint, ComponentSetInfo> LoadSets()
    {
        var d = new Dictionary<uint, ComponentSetInfo>();
        ComponentSetInfo? cur = null;
        foreach (var raw in ReadResource("component_sets.txt").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = SetLine().Match(line);
            if (m.Success)
            {
                cur = new ComponentSetInfo(uint.Parse(m.Groups[2].Value), m.Groups[1].Value, []);
                d[cur.Id] = cur;
                continue;
            }
            m = MemberLine().Match(line);
            if (m.Success && cur is not null) cur.ComponentIds.Add(uint.Parse(m.Groups[1].Value));
        }
        return d;
    }
}

public sealed record ComponentSetInfo(uint Id, string Name, HashSet<uint> ComponentIds);

/// <summary>Known IDs (selection) as constants.</summary>
public static class Ids
{
    public const uint Metadata = 53;
    public const uint Position = 54;
    public const uint Persistence = 55;
    public const uint Interest = 58;
    public const uint SpatialSystem = 59;
    public const uint Worker = 60;
    public const uint PlayerClient = 61;
    public const uint AuthorityDelegation = 65;
    public const uint Partition = 66;
    public const uint PlayerCreator = 13000;
    public const uint PlayerHeartbeatClient = 13001;
    public const uint PlayerHeartbeatServer = 13002;
    public const uint OwningWorker = 13003;
    public const uint TransformInternal = 11000;
    public const uint PlayerAuthComponent = 1000;
    public const uint LoginSystemComponent = 2018;

    // Component-Set-IDs (Improbable.Generated.ComponentSets)
    public const uint WellKnownComponentSet = 50;
    public const uint DefaultClientSet = 60000;
    public const uint DefaultServerSet = 60001;
    public const uint PlayerClientSet = 70001;
    public const uint PlayerServerSet = 70002;
    public const uint AuthorityDelegationSet = 70000;
    public const uint SystemSet = 40000;
    public const uint WorkerSet = 40001;

    /// <summary>Command index of PlayerCreator.CreatePlayer (SpatialOS counts commands from 1).</summary>
    public const uint CreatePlayerCommandIndex = 1;
}
