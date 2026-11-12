using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

namespace ZenithServer.ZenFabric;

internal sealed class IslandPublicEvent
{
    public const uint Component = 2034;
    private readonly EncounterCatalog.Definition _definition;
    private readonly IslandFinales.Stage _stage;
    private readonly SchemaWriter _configuration;
    private readonly string _counter;
    private readonly string _label;
    private readonly int _total;
    private readonly double _start;

    public StoredEntity Entity { get; }
    public long RemoveAt { get; private set; }

    public IslandPublicEvent(ZenFabricRuntime rt, (string Realm, string Area) space,
        EncounterCatalog.Definition definition, IslandFinales.Stage stage, long now)
    {
        _definition = definition;
        _stage = stage;
        _counter = stage.Collect is null ? "enemies" : "wave1_collected";
        _label = stage.Collect is null ? "Enemies Defeated" : "Souls Deposited";
        _total = stage.Collect?.Orbs.Length ?? definition.Waves.Sum(wave => wave.Spawns.Length);
        _start = now / 1000d;
        _configuration = Configuration();
        var position = definition.Center;
        Entity = rt.CreateAreaEntity(space,
        [
            new(Ids.Metadata, StandardComponents.Metadata(stage.Prefab)),
            new(Ids.Position, StandardComponents.Position(position.X, position.Y, position.Z)),
            new(Component, Snapshot(0, complete: false)),
        ]);
    }

    public void Publish(ZenFabricRuntime rt, int progress, bool complete = false)
    {
        var update = new SchemaWriter();
        if (complete)
        {
            update.AddInt32(4, 2);
            RemoveAt = Environment.TickCount64 + 3000;
        }
        WriteCounter(update, progress);
        rt.ReplaceComponentSnapshot(Entity, Component, Snapshot(progress, complete), update.ToArray());
    }

    private byte[] Snapshot(int progress, bool complete)
    {
        var writer = new SchemaWriter();
        writer.AddString(1, _definition.Name);
        writer.AddFloat(2, _definition.StartRadius);
        writer.AddFloat(3, 0);
        writer.AddInt32(4, complete ? 2 : 1);
        writer.AddDouble(5, _start);
        writer.AddDouble(6, 0);
        WriteCounter(writer, progress);
        writer.AddString(9, _stage.Quest);
        writer.AddString(10, _label);
        writer.AddObject(11, _configuration, isOption: true);
        writer.AddString(12, "");
        return writer.ToArray();
    }

    private void WriteCounter(SchemaWriter writer, int progress)
    {
        var entry = new SchemaWriter();
        entry.AddString(1, _counter);
        entry.AddString(2, _label);
        entry.AddFloat(3, progress);
        entry.AddFloat(4, _total);
        entry.AddInt32(5, 0);
        entry.AddBool(6, true);
        writer.AddObjectList(7, [entry]);
    }

    private SchemaWriter Configuration()
    {
        var config = new SchemaWriter();
        var collect = _stage.Collect;
        config.AddInt32(1, collect is null ? 1 : 2);
        var positions = collect?.Orbs ?? _definition.Waves.SelectMany(wave => wave.Spawns.Select(spawn => spawn.Position)).ToArray();
        config.AddObjectList(2, positions.Select(SpawnPoint).ToArray());
        config.AddObject(3, SpawnPoint(_definition.Center), isOption: true);
        config.AddFloat(4, _definition.StartRadius);
        config.AddObjectList(5, collect is null ? [] : [SpawnPoint(collect.Collector)]);
        config.AddFloat(6, 5);
        config.AddInt32(7, 0);
        config.AddInt32(8, 0);
        config.AddBool(9, false);
        config.AddBool(10, true);
        config.AddBool(11, false);
        config.AddEmptyList(12);
        return config;
    }

    private static SchemaWriter SpawnPoint((float X, float Y, float Z) position)
    {
        var point = new SchemaWriter();
        point.AddFloat(1, 0.5f);
        point.AddObject(2, coordinates => CharacterEntities.IntAbsolute(coordinates, position.X, position.Y, position.Z));
        return point;
    }
}
