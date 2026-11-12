using Zenith.Protocol.Schema;
using ZenithServer.Accounts;

namespace ZenithServer.ZenFabric;

internal sealed class CharacterGear
{
    public const int Capacity = 240;
    public const int BasicBag = 0;
    public const int EquipmentBag = 1;
    public const uint MoveCommand = 3;

    internal readonly record struct Slot(int Bag, int Index);
    internal readonly record struct MoveRequest(uint Id, Slot From, Slot To, bool AllowSwap);

    private static readonly (int Slot, string ItemId)[] StarterEquipment =
    [
        (2, "armor_body_reimagined_starter"),
        (3, "armor_feet_reimagined_starter"),
        (4, "armor_hands_reimagined_starter"),
        (5, "armor_legs_reimagined_starter"),
        (StarterWeapons.ShoulderSlot, StarterWeapons.BowItemId),
    ];

    private readonly Dictionary<string, StoredLootItem> _items;
    // A negative position is the corresponding equipment slot.
    public Dictionary<int, string> Locations { get; }

    public CharacterGear(StoredCharacter character)
    {
        var starter = character.Type == 4 ? StarterEquipment.Select(piece =>
            new StoredLootItem(CharacterEntities.StarterItemInstance(character.Id, piece.Slot), piece.ItemId, 1, 9)) : [];
        _items = starter.Concat(character.LootInventory).ToDictionary(item => item.InstanceId, StringComparer.Ordinal);
        Locations = new(character.GearLocations);
        var assigned = Locations.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var item in _items.Values)
        {
            if (assigned.Contains(item.InstanceId)) continue;
            var starterSlot = StarterEquipment.FirstOrDefault(piece => piece.ItemId == item.ItemId).Slot;
            if (starterSlot != 0 && Locations.TryAdd(-starterSlot, item.InstanceId)) continue;
            if (FirstFreeBagSlot() is { } slot) Locations.Add(slot, item.InstanceId);
        }
    }

    public int? FirstFreeBagSlot()
    {
        for (var slot = 0; slot < Capacity; slot++)
            if (!Locations.ContainsKey(slot)) return slot;
        return null;
    }

    public static int? Position(Slot slot) => slot.Bag switch
    {
        BasicBag when slot.Index is >= 0 and < Capacity => slot.Index,
        EquipmentBag when slot.Index is >= 1 and <= 5 or StarterWeapons.ShoulderSlot => -slot.Index,
        _ => null,
    };

    public static MoveRequest ReadMove(byte[] data)
    {
        static void Expect(ref SchemaReader reader, uint field)
        {
            if (reader.ReadFieldId() != field) throw new SchemaFormatException($"Expected inventory move field {field}");
        }

        static Slot ReadSlot(ref SchemaReader move, uint field)
        {
            Expect(ref move, field);
            var reader = new SchemaReader(move.ReadBytesSpan());
            Expect(ref reader, 1);
            var bag = reader.ReadInt32();
            Expect(ref reader, 2);
            var index = reader.ReadInt32();
            Expect(ref reader, 3);
            reader.ReadOptionalString();
            if (reader.HasMore) throw new SchemaFormatException("Unexpected inventory slot fields");
            return new(bag, index);
        }

        var reader = new SchemaReader(data);
        Expect(ref reader, 1);
        var id = reader.ReadUInt32();
        Expect(ref reader, 2);
        var move = new SchemaReader(reader.ReadBytesSpan());
        var from = ReadSlot(ref move, 1);
        var to = ReadSlot(ref move, 2);
        Expect(ref reader, 3);
        var allowSwap = reader.ReadBool();
        if (move.HasMore || reader.HasMore) throw new SchemaFormatException("Unexpected inventory move fields");
        return new(id, from, to, allowSwap);
    }

    public bool Move(int from, int to, bool allowSwap)
    {
        if (!Locations.TryGetValue(from, out var item) || !Fits(item, to)) return false;
        if (from == to) return true;
        if (Locations.TryGetValue(to, out var displaced))
        {
            if (!allowSwap || !Fits(displaced, from)) return false;
            Locations[from] = displaced;
        }
        else Locations.Remove(from);
        Locations[to] = item;
        return true;
    }

    private bool Fits(string instance, int position)
    {
        if (position >= 0) return true;
        var id = _items[instance].ItemId;
        if (id is StarterWeapons.BowItemId or StarterWeapons.DesertBowItemId)
            return position == -StarterWeapons.ShoulderSlot;

        // The client uses a bitmask for item eligibility and a separate inventory slot index.
        var mask = -position switch
        {
            1 => 1,  // Head
            2 => 2,  // Body
            3 => 8,  // Feet
            4 => 16, // Hands
            5 => 4,  // Legs
            _ => 0,
        };
        return LootGearCatalog.Find(id) is { Type: "armor" } item && (item.AllowedEquipmentSlots & mask) != 0;
    }

    public StoredLootItem? At(int position) =>
        Locations.TryGetValue(position, out var id) ? _items[id] : null;

    public static SchemaWriter WriteItem(StoredLootItem item, int position)
    {
        var slot = Math.Max(0, -position);
        var bow = item.ItemId is StarterWeapons.BowItemId or StarterWeapons.DesertBowItemId;
        return CharacterEntities.Item(item.InstanceId, item.ItemId, slot, slot == 0 ? BasicBag : EquipmentBag,
            Math.Abs(position), bow ? -1 : 50, item.Level, item.Rarity);
    }

    public IEnumerable<KeyValuePair<int, SchemaWriter>> Equipment() =>
        Locations.Where(entry => entry.Key < 0).OrderByDescending(entry => entry.Key)
            .Select(entry => KeyValuePair.Create(-entry.Key, WriteItem(_items[entry.Value], entry.Key)));

    public void WriteBasicInventory(SchemaWriter writer) =>
        writer.AddObjectList(4, Locations.Where(entry => entry.Key >= 0).OrderBy(entry => entry.Key)
            .Select(entry => CharacterEntities.InventoryEntry(entry.Key, WriteItem(_items[entry.Value], entry.Key))).ToArray());

    public void PublishInventory(ZenFabricRuntime rt, StoredEntity entity, StoredCharacter character, string accountId,
        IReadOnlyList<string> cosmetics, IReadOnlyList<int> changed)
    {
        var data = entity.Components[CharacterEntities.Inventory].Data;
        var reader = new SchemaReader(data);
        static void SkipList(ref SchemaReader reader, uint field)
        {
            if (reader.ReadFieldId() != field) throw new SchemaFormatException($"Expected inventory field {field}");
            var count = reader.ReadListCount();
            for (var i = 0; i < count; i++) reader.ReadBytesSpan();
        }
        for (uint field = 1; field <= 3; field++) SkipList(ref reader, field);
        var start = reader.Position;
        SkipList(ref reader, 4);
        SkipList(ref reader, 5);

        var equipped = CharacterEntities.EquipmentItems(character, this, accountId, cosmetics,
            CosmeticShop.Locations(character, cosmetics));
        var snapshot = new SchemaWriter();
        snapshot.WriteRaw(data.AsSpan(0, start));
        WriteBasicInventory(snapshot);
        CharacterEntities.WriteEquippedInventory(snapshot, equipped);
        snapshot.WriteRaw(reader.RemainingSpan);

        SchemaWriter Entry(int position) => CharacterEntities.InventoryEntry(Math.Abs(position),
            At(position) is { } item ? WriteItem(item, position) : null);
        var update = new SchemaWriter();
        var basic = changed.Where(position => position >= 0).Select(Entry).ToArray();
        var equipment = changed.Where(position => position < 0).Select(Entry).ToArray();
        if (basic.Length != 0) update.AddObjectList(4, basic);
        if (equipment.Length != 0) update.AddObjectList(5, equipment);
        rt.ReplaceComponentSnapshot(entity, CharacterEntities.Inventory, snapshot.ToArray(), update.ToArray());
        if (equipment.Length != 0)
            rt.ReplaceComponentSnapshot(entity, CharacterEntities.Equipment, CharacterEntities.EquipmentSnapshot(equipped),
                CharacterEntities.EquipmentSnapshot(equipped, updateOnly: true));
    }
}
