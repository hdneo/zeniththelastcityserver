using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

namespace ZenithServer.ZenFabric;

internal static class WorldEntry
{
    public const float OutOfBoundsY = -150; // Stock InstakillOutOfBounds threshold.
    public const string DailyId = "d43f0672-3e63-4fdf-984e-f369a5249ea6";

    internal sealed record Area(string Id, string Name, string Prefab,
        (float X, float Y, float Z) Origin, (float X, float Y, float Z) Bounds,
        (float X, float Y, float Z) Spawn, int Capacity, DailyRotation.Layout? Daily = null, DateOnly? Date = null)
    {
        public string SharedId => Date is { } date ? $"{Id}:{date.DayNumber}" : Id;
    }

    // Template IDs, positions and bounds are from the November 10 capture. Prefab addresses and
    // the starting checkpoints are from the installed client. Preserve the existing hub origin.
    public static readonly Area Hub = new("6484fa5c-ad17-4b11-9412-f912afd75c14", "Neo City",
        "079955399a35be944956b3b059e0c5a7", (0, 0, 0), (300, 270, 330), (26.538f, 6.797f, 26.493f), 20);

    public static readonly Area Adventure = new("443a50bb-b60a-4423-afa6-21cc77b18c5b", "Wasteland Grove", "7b887548e731a834ab66f6df5e7fad37",
        // The path is at Y=-2.47 here. The full teleport checks for ground within five metres.
        (-685, 97.9f, -83.4f), (1074, 800, 1074), (-169.770f, 0.5f, -10.09f), 4);

    public static readonly Area BossRush = new("a462fbec-d8c1-4740-b2bc-50ff774e577f", "Trials of Telos", "9cc8e22f410ce4b47b0053d0986da85f",
        (-6, -9.5f, 345), (200, 200, 360), (-12.94f, 3.49f, 188.54f), 4);

    private sealed record Gate(Area Destination, (float X, float Y, float Z) CityEntry, (float X, float Y, float Z) Exit);

    private static readonly Gate[] Gates =
    [
        new(Adventure, (-150, 1, -11), (-125.6f, 0.54f, -13.8f)),
        new(BossRush, (-12.7f, 6, 165), (-11.9f, 4.3f, 140.7f)),
    ];

    public static Area[] Areas(Area daily) => [Hub, daily, Adventure, BossRush];

    public static Area? GateDestination(Area current, Area? daily, (double X, double Y, double Z) position)
    {
        if (current == Hub && IsAtGate(position, (150, 6.5f, 0))) return daily;
        if (current.Daily is { } layout && IsAtGate(position, layout.Exit)) return Hub;
        foreach (var gate in Gates)
        {
            if (current == Hub && IsAtGate(position, gate.CityEntry)) return gate.Destination;
            if (current == gate.Destination && IsAtGate(position, gate.Exit)) return Hub;
        }
        return null;
    }

    private static bool IsAtGate((double X, double Y, double Z) position, (float X, float Y, float Z) gate)
    {
        var dx = position.X - gate.X;
        var dz = position.Z - gate.Z;
        return dx * dx + dz * dz <= 64 && Math.Abs(position.Y - gate.Y) <= 10;
    }

    public static ComponentData[] AreaTrigger(Area area)
    {
        var trigger = new SchemaWriter();
        trigger.AddString(1, area.Id);
        trigger.AddObject(2, bounds =>
        {
            bounds.AddFloat(1, area.Bounds.X);
            bounds.AddFloat(2, area.Bounds.Y);
            bounds.AddFloat(3, area.Bounds.Z);
        });
        trigger.AddBool(3, true); // Runtime-only area
        trigger.AddInt32(4, area.Capacity);
        trigger.AddBool(5, false); // Local areas are available continuously.
        trigger.AddBool(6, false);
        trigger.AddInt32(7, 0);

        var prefab = new SchemaWriter();
        prefab.AddString(1, area.Prefab);
        return
        [
            new(Ids.Metadata, StandardComponents.Metadata("InstancedAreaTrigger")),
            new(Ids.Position, StandardComponents.Position(area.Origin.X, area.Origin.Y, area.Origin.Z)),
            new(Ids.InstancedAreaTriggerComponent, trigger.ToArray()),
            new(Ids.InstancedAreaTriggerPrefabInfoComponent, prefab.ToArray()),
        ];
    }

    public static byte[] PlayerWorld(bool enterMainWorld, Area? area = null, long? areaEntityId = null)
    {
        var w = new SchemaWriter();
        w.AddInt32(1, enterMainWorld ? 1 : 0);
        w.AddObject(2, instance =>
        {
            instance.AddString(1, null);
            instance.AddObject(2, coordinates => CharacterEntities.IntAbsolute(coordinates, 0, 0, 0));
            instance.AddString(3, null);
            instance.AddInt32(4, 0);
        });
        w.AddObject(3, offset =>
        {
            offset.AddFloat(1, 0);
            offset.AddFloat(2, 0);
            offset.AddFloat(3, 0);
        });
        WriteCurrentArea(w, area, areaEntityId);
        w.AddInt64(5, 0);
        w.AddString(6, null);
        return w.ToArray();
    }

    public static byte[] PlayerWorldUpdate(Area? area, long? areaEntityId, bool initialEntry)
    {
        var writer = new SchemaWriter();
        if (initialEntry) writer.AddInt32(1, 1);
        if (area is not null) WriteCurrentArea(writer, area, areaEntityId);
        return writer.ToArray();
    }

    private static void WriteCurrentArea(SchemaWriter writer, Area? area, long? areaEntityId)
    {
        if (area is not null && areaEntityId is { } id) writer.AddObject(4, AreaInfo(area, id), isOption: true);
        else writer.AddEmptyOption(4);
    }

    public static byte[] AreaList(Area? area, long? entityId)
    {
        var writer = new SchemaWriter();
        if (area is not null && entityId is { } id) writer.AddObjectList(1, [AreaInfo(area, id)]);
        else writer.AddEmptyList(1);
        return writer.ToArray();
    }

    private static SchemaWriter AreaInfo(Area area, long entityId)
    {
        var current = new SchemaWriter();
        current.AddString(1, area.Id);
        current.AddString(2, null); // The local area is the template itself.
        current.AddEntityId(3, entityId);
        current.AddString(4, area.Name);
        current.AddInt32(5, 1);
        current.AddEmptyOption(7); // Last entry time
        current.AddEmptyOption(8); // Remote deployment
        return current;
    }

    public static byte[] TeleportEvent((float X, float Y, float Z) position, bool quick = false)
    {
        var w = new SchemaWriter();
        w.AddObject(1001, teleport =>
        {
            teleport.AddObject(1, location =>
            {
                location.AddDouble(1, position.X);
                location.AddDouble(2, position.Y);
                location.AddDouble(3, position.Z);
            });
            teleport.AddEmptyOption(2); // Keep the client's orientation.
            teleport.AddBool(3, quick);
        });
        return w.ToArray();
    }

    public static byte[] TeleportPosition((float X, float Y, float Z) position)
    {
        var w = new SchemaWriter();
        w.AddObject(2, coordinates => CharacterEntities.IntAbsolute(coordinates, position.X, position.Y, position.Z));
        return w.ToArray();
    }

    public static (double X, double Y, double Z)? ReadZSyncPosition(ReadOnlySpan<byte> update)
    {
        try
        {
            var reader = new SchemaReader(update);
            if (!reader.HasMore || reader.ReadFieldId() != 1) return null;
            var location = new SchemaReader(reader.ReadBytesSpan());
            if (location.Remaining != 18 || location.ReadFieldId() != 1) return null;
            var x = location.ReadFloat();
            if (location.ReadFieldId() != 2) return null;
            var y = location.ReadFloat();
            return location.ReadFieldId() == 3 ? (x, y, location.ReadFloat()) : null;
        }
        catch (SchemaFormatException)
        {
            return null;
        }
    }
}
