using System.IO.Compression;
using System.Text.Json;

namespace ZenithServer.ZenFabric;

internal sealed class EncounterNavigation
{
    private const int SearchLimit = 4000;
    private static readonly (int X, int Z)[] Directions =
        [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    // Build 56557 NavMeshSurface detail triangles sampled in encounter coordinates.
    // Each compressed column holds a layer count, then heights in centimetres and one-based neighbour layers.
    private sealed record Bake(string Id, float OriginX, float OriginZ, float Step, int Width, int Height, byte[] Cells);

    private readonly Bake _map;
    private readonly int[] _columns;
    private readonly int[] _columnByNode;
    private readonly short[] _heights;
    private readonly byte[][] _links;

    private EncounterNavigation(Bake map)
    {
        _map = map;
        if (map.Width is < 1 or > 512 || map.Height is < 1 or > 512 || map.Step <= 0)
            throw new InvalidDataException($"Invalid navigation grid for {map.Id}");
        _columns = new int[map.Width * map.Height + 1];
        var columnByNode = new List<int>();
        var heights = new List<short>();
        var links = new List<byte[]>();
        using var bytes = new MemoryStream(map.Cells);
        using var reader = new BinaryReader(new GZipStream(bytes, CompressionMode.Decompress));
        for (var column = 0; column < _columns.Length - 1; column++)
        {
            var count = reader.ReadByte();
            for (var layer = 0; layer < count; layer++)
            {
                columnByNode.Add(column);
                heights.Add(reader.ReadInt16());
                links.Add(reader.ReadBytes(Directions.Length));
            }
            _columns[column + 1] = heights.Count;
        }
        _columnByNode = columnByNode.ToArray();
        _heights = heights.ToArray();
        _links = links.ToArray();
    }

    public static IReadOnlyDictionary<string, EncounterNavigation> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ClientData", "encounter_navigation.json");
        return JsonSerializer.Deserialize<Bake[]>(File.ReadAllText(path))!
            .ToDictionary(map => map.Id, map => new EncounterNavigation(map), StringComparer.Ordinal);
    }

    public (float X, float Y, float Z) Point(int node)
    {
        var column = _columnByNode[node];
        return (_map.OriginX + column % _map.Width * _map.Step, _heights[node] / 100f,
            _map.OriginZ + column / _map.Width * _map.Step);
    }

    public int? Nearest((double X, double Y, double Z) position, float radius, float vertical)
    {
        var centerX = (position.X - _map.OriginX) / _map.Step;
        var centerZ = (position.Z - _map.OriginZ) / _map.Step;
        var reach = radius / _map.Step;
        var minX = Math.Max(0, (int)Math.Ceiling(centerX - reach));
        var maxX = Math.Min(_map.Width - 1, (int)Math.Floor(centerX + reach));
        var minZ = Math.Max(0, (int)Math.Ceiling(centerZ - reach));
        var maxZ = Math.Min(_map.Height - 1, (int)Math.Floor(centerZ + reach));
        var bestDistance = double.PositiveInfinity;
        int? best = null;
        for (var z = minZ; z <= maxZ; z++)
        for (var x = minX; x <= maxX; x++)
        {
            var column = z * _map.Width + x;
            var dx = position.X - (_map.OriginX + x * _map.Step);
            var dz = position.Z - (_map.OriginZ + z * _map.Step);
            var horizontal = dx * dx + dz * dz;
            if (horizontal > radius * radius) continue;
            for (var node = _columns[column]; node < _columns[column + 1]; node++)
            {
                var dy = position.Y - _heights[node] / 100f;
                var distance = horizontal + dy * dy;
                if (Math.Abs(dy) > vertical || distance >= bestDistance) continue;
                bestDistance = distance;
                best = node;
            }
        }
        return best;
    }

    public bool CanWalkDirect(int from, int to)
    {
        var start = _columnByNode[from];
        var destination = _columnByNode[to];
        var x = start % _map.Width;
        var z = start / _map.Width;
        var toX = destination % _map.Width;
        var toZ = destination / _map.Width;
        var dx = Math.Abs(toX - x);
        var dz = Math.Abs(toZ - z);
        var error = dx - dz;
        while (x != toX || z != toZ)
        {
            var nextX = x;
            var nextZ = z;
            var decision = error * 2;
            if (decision > -dz)
            {
                error -= dz;
                nextX += Math.Sign(toX - x);
            }
            if (decision < dx)
            {
                error += dx;
                nextZ += Math.Sign(toZ - z);
            }
            var direction = Array.IndexOf(Directions, (nextX - x, nextZ - z));
            if (_links[from][direction] == 0) return false;
            x = nextX;
            z = nextZ;
            from = _columns[z * _map.Width + x] + _links[from][direction] - 1;
        }
        return from == to;
    }

    public Queue<int> FindRoute(int start, int goal, Func<int, bool> canEnter)
    {
        var pending = new PriorityQueue<(int Cell, int Cost), double>();
        var visited = new Dictionary<int, (int Previous, int Cost)> { [start] = (-1, 0) };
        var closest = start;
        var closestDistance = GridDistance(start, goal);
        pending.Enqueue((start, 0), closestDistance);
        for (var searched = 0; searched < SearchLimit && pending.TryDequeue(out var current, out _);)
        {
            var (cell, cost) = current;
            if (visited[cell].Cost != cost) continue;
            searched++;
            var distance = GridDistance(cell, goal);
            if (distance < closestDistance)
            {
                closest = cell;
                closestDistance = distance;
            }
            if (cell == goal)
            {
                closest = cell;
                break;
            }

            for (var direction = 0; direction < Directions.Length; direction++)
            {
                var layer = _links[cell][direction];
                if (layer == 0) continue;
                var offset = Directions[direction];
                var nextColumn = _columnByNode[cell] + offset.X + offset.Z * _map.Width;
                var next = _columns[nextColumn] + layer - 1;
                var nextCost = cost + (direction < 4 ? 10 : 14);
                if (visited.TryGetValue(next, out var previous) && previous.Cost <= nextCost || !canEnter(next))
                    continue;
                visited[next] = (cell, nextCost);
                pending.Enqueue((next, nextCost), nextCost + GridDistance(next, goal));
            }
        }

        var route = new Stack<int>();
        for (var cell = closest; cell != start; cell = visited[cell].Previous) route.Push(cell);
        return new Queue<int>(route);
    }

    private int GridDistance(int first, int second)
    {
        var a = _columnByNode[first];
        var b = _columnByNode[second];
        var dx = Math.Abs(a % _map.Width - b % _map.Width);
        var dz = Math.Abs(a / _map.Width - b / _map.Width);
        return Math.Max(10 * Math.Max(dx, dz) + 4 * Math.Min(dx, dz), Math.Abs(_heights[first] - _heights[second]) / 5);
    }
}
