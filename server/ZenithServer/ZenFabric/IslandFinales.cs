using System.Text.Json;
using static ZenithServer.ZenFabric.DailyRotation;
using static ZenithServer.ZenFabric.EncounterCatalog;

namespace ZenithServer.ZenFabric;

internal static class IslandFinales
{
    internal sealed record Collection((float X, float Y, float Z) Collector, (float X, float Y, float Z)[] Orbs);
    internal sealed record Stage(int Order, string Prefab, string Quest, (float X, float Y, float Z) Trigger,
        float TriggerRadius, string[] StartSignals, bool Boss, Collection? Collect);
    internal sealed record Route(Definition[] Encounters, (float X, float Y, float Z) Exit)
    {
        public (float X, float Y, float Z)? CheckpointAt((double X, double Y, double Z) position)
        {
            foreach (var definition in Encounters)
            {
                if (definition.Checkpoint is not { } point) continue;
                var dx = position.X - point.X;
                var dy = position.Y - point.Y;
                var dz = position.Z - point.Z;
                if (dx * dx + dz * dz <= 225 && Math.Abs(dy) <= 9) return point;
            }
            return null;
        }
    }

    private sealed record RouteData(string Prefab, float[] Exit, StageData[] Stages);
    private sealed record StageData(string Name, string Prefab, string Quest, string AnchorId, float[] AnchorPosition,
        float[] Center, float Radius, float[] Trigger, float TriggerRadius, float[] Checkpoint, string? NavigationId,
        string[] StartSignals, string? ClearSignal, bool Boss, CollectionData? Collection, WaveData[] Waves);
    private sealed record CollectionData(float[] Collector, float[][] Orbs);
    private sealed record WaveData(int DelayMs, string? Signal, string? EndSignal, SpawnData[] Spawns);
    private sealed record SpawnData(string Prefab, float[] Position);

    private static readonly Dictionary<string, Route> Routes = Load();

    public static Route? For(string prefab) => Routes.GetValueOrDefault(prefab);

    private static Dictionary<string, Route> Load()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "ClientData", "island_finales.json"));
        var routes = JsonSerializer.Deserialize<RouteData[]>(file,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new InvalidDataException("Missing island finales");
        return routes.ToDictionary(route => route.Prefab, route => new Route(
            route.Stages.Select(Build).ToArray(), Position(route.Exit)), StringComparer.Ordinal);
    }

    private static Definition Build(StageData data, int index)
    {
        var collection = data.Collection is { } collect
            ? new Collection(Position(collect.Collector), collect.Orbs.Select(Position).ToArray()) : null;
        var stage = new Stage(index, data.Prefab, data.Quest, Position(data.Trigger), data.TriggerRadius,
            data.StartSignals, data.Boss, collection);
        var waves = data.Waves.Select(wave => new Wave(wave.Spawns.Select(spawn => new SpawnPoint(
            Position(spawn.Position), DailyEnemy(spawn.Prefab), 0xDFF7FDFF)).ToArray(),
            DelayMs: wave.DelayMs, StartEvent: wave.Signal, EndEvent: wave.EndSignal)).ToArray();
        return new(data.Name, Position(data.Center), data.Radius, waves,
            new(data.AnchorId, Position(data.AnchorPosition)), ClearEvent: data.ClearSignal,
            Checkpoint: Position(data.Checkpoint), NavigationId: data.NavigationId, Finale: stage);
    }
}
