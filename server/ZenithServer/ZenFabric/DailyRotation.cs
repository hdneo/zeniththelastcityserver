using System.Text.Json;
using static ZenithServer.ZenFabric.EncounterCatalog;

namespace ZenithServer.ZenFabric;

internal sealed class DailyRotation(TimeProvider clock)
{
    internal sealed record Layout(string Prefab, (float X, float Y, float Z) Spawn,
        (float X, float Y, float Z) Exit, Definition[] Encounters, ThresholdProgress Threshold);

    private sealed record LayoutData(int Number, string Prefab, float[] Spawn, float[] Exit, AnchorData Threshold,
        IslandData[] Islands);
    private sealed record IslandData(float[] Center, float Radius, AnchorData Anchor, float[] Checkpoint,
        float[]? Miniboss, string? NavigationId, WaveData[] Waves, string ClearEvent);
    private sealed record AnchorData(string Id, float[] Position);
    private sealed record WaveData(SpawnData[] Spawns, int Replacements = 0, string? StartEvent = null);
    private sealed record SpawnData(string Prefab, float[] Position, float[]? Approach, uint Rotation = 0xDFF7FDFF);

    private static readonly Layout[] Layouts = Load();
    private WorldEntry.Area? _current;

    public WorldEntry.Area Today
    {
        get
        {
            var date = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            if (_current?.Date == date) return _current;

            var layout = Select(date);
            _current = new(WorldEntry.DailyId, "Shifting Isles", layout.Prefab, (618, 32.6f, -307),
                (973.2f, 786.8f, 947.8f), layout.Spawn, 4, layout, date);
            return _current;
        }
    }

    private static Layout Select(DateOnly date)
    {
        var (cycle, index) = Math.DivRem(date.DayNumber, Layouts.Length);
        var order = Shuffle(Layouts.Length > 2 ? cycle : 0);
        // Swapping the first two leaves the previous cycle's last pick independently reproducible.
        if (Layouts.Length > 2 && cycle > 0 && index < 2 && order[0] == Shuffle(cycle - 1)[^1])
            (order[0], order[1]) = (order[1], order[0]);
        return order[index];
    }

    private static Layout[] Shuffle(int seed)
    {
        var order = (Layout[])Layouts.Clone();
        new Random(seed).Shuffle(order);
        return order;
    }

    private static Layout[] Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ClientData", "daily_layouts.json");
        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<LayoutData[]>(stream,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (data is not { Length: > 0 }) throw new InvalidDataException("Missing Shifting Isles layouts");

        return data.OrderBy(row => row.Number).Select(row =>
        {
            var encounters = row.Islands.Select((island, index) => new Definition(
                $"Shifting Isles {row.Number} — combat island {index + 1}", Position(island.Center), island.Radius,
                island.Waves.Select(wave => new Wave(wave.Spawns.Select(spawn => new SpawnPoint(Position(spawn.Position),
                    DailyEnemy(spawn.Prefab), spawn.Rotation, spawn.Approach is { } approach ? Position(approach) : null)).ToArray(),
                    wave.Replacements, StartEvent: wave.StartEvent)).ToArray(),
                Anchor(island.Anchor), island.Miniboss is { } miniboss ? Position(miniboss) : null,
                island.ClearEvent, CountsTowardClear: true, Checkpoint: Position(island.Checkpoint),
                NavigationId: island.NavigationId)).ToArray();
            return new Layout(row.Prefab, Position(row.Spawn), Position(row.Exit), encounters,
                new(Anchor(row.Threshold), encounters.Length));
        }).ToArray();
    }

    private static EncounterSignals.Anchor Anchor(AnchorData data) => new(data.Id, Position(data.Position));

    internal static (float X, float Y, float Z) Position(float[] values) => values is [var x, var y, var z]
        ? (x, y, z)
        : throw new InvalidDataException("Shifting Isles positions must contain three coordinates");
}
