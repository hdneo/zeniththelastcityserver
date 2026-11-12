using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZenithServer.ZenFabric;

internal sealed class CharacterProgression
{
    private sealed record Threshold(int XP, int Level);

    private readonly Threshold[] _thresholds = JsonSerializer.Deserialize<Threshold[]>(ReadClientData("level.json"),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.OrderBy(row => row.XP).ToArray();
    private readonly Dictionary<(string Class, int Level), Dictionary<string, string>> _stats =
        JsonNode.Parse(ReadClientData("level_stats.json"))!.AsArray()
            .GroupBy(row => (row!["className"]!.GetValue<string>(), row["level"]!.GetValue<int>()))
            .ToDictionary(group => group.Key, group => group.First()!.AsObject().Where(stat => stat.Key != "className")
                .ToDictionary(stat => stat.Key == "hp" ? "health" : stat.Key, stat => stat.Value!.ToString()));

    public int MaximumExperience => _thresholds[^1].XP;

    public int LevelAt(int experience) => _thresholds.Last(row => row.XP <= experience).Level;

    public Dictionary<string, string> Stats(string className, int level) => _stats[(className, level)];

    private static string ReadClientData(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ClientData", name));
}
