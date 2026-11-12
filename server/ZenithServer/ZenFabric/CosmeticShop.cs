using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using ZenithServer.Accounts;

namespace ZenithServer.ZenFabric;

internal static class CosmeticShop
{
    public const uint Wallet = 5001;
    public const uint StartSession = 7;
    public const uint StartTransaction = 8;
    public const uint Purchase = 11;
    private const int CosmeticBag = 5;
    private const int EquipmentBag = 1;
    private const int FirstCosmeticSlot = 20;
    private const int LastCosmeticSlot = 24;

    private static readonly Dictionary<string, (int Price, int Slot)> Catalog = BuildCatalog();
    private static long _transactionId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal sealed record Checkout(long Id, string? Language, int Currency, string? ItemId, string? StorePanelId);

    private static Dictionary<string, (int Price, int Slot)> BuildCatalog()
    {
        // StorePanel_NeoCity_MainStore in client build 56557.
        var items = new Dictionary<string, (int Price, int Slot)>(StringComparer.Ordinal);
        string[] sets = ["shin", "toxicalch", "berserkmecha", "pirate", "navarre", "mika", "exiled", "beachbug"];
        (string Part, int Price, int Slot)[] pieces =
            [("head", 900, 20), ("body", 900, 21), ("feet", 650, 22), ("hands", 700, 23), ("legs", 850, 24)];
        foreach (var set in sets)
            foreach (var (part, price, slot) in pieces)
                if (set != "navarre" || part != "head")
                    items.Add($"armor_{part}_{set}_cosmetic", (price, slot));
        return items;
    }

    public static bool TryGetPrice(string? itemId, out int price)
    {
        var found = Catalog.TryGetValue(itemId ?? "", out var item);
        price = item.Price;
        return found;
    }

    public static void ReadSessionRequest(byte[] data)
    {
        var reader = new SchemaReader(data);
        if (ReadInt(ref reader, 1) is < 0 or > 4) throw new SchemaFormatException("Unknown wallet platform");
        ReadString(ref reader, 2); // The authenticated worker already identifies the account.
        ReadString(ref reader, 3); // Optional platform authorization code.
        RequireEnd(ref reader);
    }

    public static Checkout ReadCheckout(byte[] data)
    {
        var reader = new SchemaReader(data);
        RequireField(ref reader, 1);
        var id = reader.ReadInt64();
        var language = ReadString(ref reader, 2);
        if (ReadInt(ref reader, 3) != 0) throw new SchemaFormatException("Only cosmetic item purchases are supported");
        ReadInt(ref reader, 4); // Transaction state
        var currency = ReadInt(ref reader, 5);
        ReadInt(ref reader, 6); // Client balance
        ReadInt(ref reader, 7); // Client balance change
        var item = ReadString(ref reader, 8);
        ReadInt(ref reader, 9); // Client price
        ReadString(ref reader, 10); // Currency package SKU
        ReadInt(ref reader, 11); // Currency package quantity
        ReadString(ref reader, 12); // Steam order
        ReadString(ref reader, 13); // Steam transaction
        RequireField(ref reader, 14);
        if (reader.ReadOptionHasValue()) reader.ReadBool();
        ReadString(ref reader, 15); // Sony transaction
        RequireField(ref reader, 16);
        reader.ReadBool(); // Completed
        RequireField(ref reader, 17);
        reader.ReadBool(); // Succeeded
        RequireField(ref reader, 18);
        if (reader.ReadOptionHasValue()) reader.ReadInt32();
        ReadString(ref reader, 19); // Message
        var storePanel = ReadString(ref reader, 20);
        RequireEnd(ref reader);
        return new(id, language, currency, item, storePanel);
    }

    public static Checkout ReadTransactionStart(byte[] data)
    {
        var reader = new SchemaReader(data);
        var panel = ReadString(ref reader, 1);
        var item = ReadString(ref reader, 2);
        var currencySku = ReadString(ref reader, 3);
        RequireEnd(ref reader);
        if (!string.IsNullOrEmpty(currencySku)) throw new SchemaFormatException("Currency top-ups are unavailable");
        return new(Interlocked.Increment(ref _transactionId), null, -1, item, panel);
    }

    public static byte[] CheckoutResult(Checkout checkout, int price, int before, int after, int state, string? error = null)
    {
        var completed = state is 7 or -1;
        var writer = new SchemaWriter();
        writer.AddInt64(1, checkout.Id);
        writer.AddString(2, checkout.Language);
        writer.AddInt32(3, 0); // PurchaseItem
        writer.AddInt32(4, state); // Persisted, Completed, or Error
        writer.AddInt32(5, checkout.Currency);
        writer.AddInt32(6, before);
        writer.AddInt32(7, after - before);
        writer.AddString(8, checkout.ItemId);
        writer.AddInt32(9, price);
        writer.AddString(10, null);
        writer.AddInt32(11, 0);
        for (uint field = 12; field <= 15; field++) writer.AddEmptyOption(field);
        writer.AddBool(16, completed);
        writer.AddBool(17, state == 7);
        if (completed) writer.AddInt32(18, after, isOption: true);
        else writer.AddEmptyOption(18);
        writer.AddString(19, error);
        writer.AddString(20, checkout.StorePanelId);
        return writer.ToArray();
    }

    public static byte[] WalletState(int balance, bool updateOnly = false)
    {
        var writer = new SchemaWriter();
        if (!updateOnly) writer.AddInt64(1, 0); // Zen
        writer.AddInt64(2, balance);
        return writer.ToArray();
    }

    public static int? Position(CharacterGear.Slot slot) => slot.Bag switch
    {
        CosmeticBag when slot.Index is >= 0 and < 999 => slot.Index,
        EquipmentBag when slot.Index is >= FirstCosmeticSlot and <= LastCosmeticSlot => -slot.Index,
        _ => null,
    };

    public static Dictionary<int, int> Locations(StoredCharacter character, IReadOnlyList<string> items)
    {
        var locations = new Dictionary<int, int>(character.CosmeticLocations);
        var assigned = locations.Values.ToHashSet();
        for (var item = 0; item < items.Count; item++)
        {
            if (assigned.Contains(item)) continue;
            var position = item;
            while (locations.ContainsKey(position)) position++;
            locations.Add(position, item);
        }
        return locations;
    }

    public static bool TryMove(int from, int to, bool allowSwap, Dictionary<int, int> locations, IReadOnlyList<string> items)
    {
        if (!locations.TryGetValue(from, out var item) || !Fits(items[item], to)) return false;
        if (from == to) return true;
        if (locations.TryGetValue(to, out var displaced))
        {
            if (!allowSwap || !Fits(items[displaced], from)) return false;
            locations[from] = displaced;
        }
        else
        {
            locations.Remove(from);
        }
        locations[to] = item;
        return true;
    }

    private static bool Fits(string item, int position) =>
        position >= 0 || Catalog.TryGetValue(item, out var definition) && -position == definition.Slot;

    public static void WriteInventory(SchemaWriter writer, string accountId, IReadOnlyList<string> items,
        IReadOnlyDictionary<int, int> locations) =>
        writer.AddObjectList(9, locations.Where(entry => entry.Key >= 0).OrderBy(entry => entry.Key)
            .Select(entry => CharacterEntities.InventoryEntry(entry.Key, Item(accountId, items, entry.Key, entry.Value))).ToArray());

    public static SchemaWriter Item(string accountId, IReadOnlyList<string> items, int position, int ownedIndex)
    {
        var equipped = position < 0;
        var instanceId = FormattableString.Invariant($"{accountId}-cosmetic-{ownedIndex}");
        return CharacterEntities.Item(instanceId, items[ownedIndex], equipped ? -position : 0,
            equipped ? EquipmentBag : CosmeticBag, Math.Abs(position), -1);
    }

    public static void PublishInventory(ZenFabricRuntime runtime, StoredEntity entity, StoredCharacter character,
        string accountId, IReadOnlyList<string> items, Dictionary<int, int> locations, IReadOnlyList<int> changed)
    {
        var data = entity.Components[CharacterEntities.Inventory].Data;
        var reader = new SchemaReader(data);
        static void SkipFields(ref SchemaReader cursor, uint first, uint last)
        {
            for (var field = first; field <= last; field++)
            {
                RequireField(ref cursor, field);
                SkipObjects(ref cursor);
            }
        }
        SkipFields(ref reader, 1, 4);
        var equipmentStart = reader.Position;
        SkipFields(ref reader, 5, 5);
        var equipmentEnd = reader.Position;
        SkipFields(ref reader, 6, 8);
        var start = reader.Position;
        SkipFields(ref reader, 9, 9);

        var equipped = CharacterEntities.EquipmentItems(character, new(character), accountId, items, locations);
        var snapshot = new SchemaWriter();
        snapshot.WriteRaw(data.AsSpan(0, equipmentStart));
        CharacterEntities.WriteEquippedInventory(snapshot, equipped);
        snapshot.WriteRaw(data.AsSpan(equipmentEnd, start - equipmentEnd));
        WriteInventory(snapshot, accountId, items, locations);
        snapshot.WriteRaw(reader.RemainingSpan);

        var delta = new SchemaWriter();
        SchemaWriter Entry(int position) => CharacterEntities.InventoryEntry(Math.Abs(position),
            locations.TryGetValue(position, out var item) ? Item(accountId, items, position, item) : null);
        var equippedChanges = changed.Where(position => position < 0).Select(Entry).ToArray();
        var bagChanges = changed.Where(position => position >= 0).Select(Entry).ToArray();
        if (equippedChanges.Length != 0) delta.AddObjectList(5, equippedChanges);
        if (bagChanges.Length != 0) delta.AddObjectList(9, bagChanges);
        runtime.ReplaceComponentSnapshot(entity, CharacterEntities.Inventory, snapshot.ToArray(), delta.ToArray());
        if (equippedChanges.Length != 0)
            runtime.ReplaceComponentSnapshot(entity, CharacterEntities.Equipment, CharacterEntities.EquipmentSnapshot(equipped),
                CharacterEntities.EquipmentSnapshot(equipped, updateOnly: true));
    }

    public static void PublishOwnership(ZenFabricRuntime runtime, StoredEntity character, IReadOnlyList<string> items)
    {
        var data = character.Components[Ids.CharacterCredentialsComponent].Data;
        var reader = new SchemaReader(data);
        for (uint field = 1; field <= 3; field++) ReadString(ref reader, field);
        ReadInt(ref reader, 4);
        RequireField(ref reader, 5);
        reader.ReadInt64();
        ReadInt(ref reader, 6);
        ReadString(ref reader, 7);
        RequireField(ref reader, 8);
        reader.ReadUInt64();
        for (uint field = 9; field <= 11; field++) ReadString(ref reader, field);
        for (uint field = 12; field <= 13; field++) SkipStrings(ref reader, field);
        RequireField(ref reader, 14);
        reader.ReadBool();
        ReadString(ref reader, 15);
        RequireField(ref reader, 16);
        reader.ReadInt64();
        var start = reader.Position;
        SkipStrings(ref reader, 17);
        var snapshot = new SchemaWriter();
        snapshot.WriteRaw(data.AsSpan(0, start));
        var delta = new SchemaWriter();
        delta.AddStringList(17, items);
        snapshot.WriteRaw(delta.WrittenSpan);
        snapshot.WriteRaw(reader.RemainingSpan);
        runtime.ReplaceComponentSnapshot(character, Ids.CharacterCredentialsComponent, snapshot.ToArray(), delta.ToArray());
    }

    private static void SkipObjects(ref SchemaReader reader)
    {
        var count = reader.ReadListCount();
        for (var i = 0; i < count; i++) reader.ReadBytesSpan();
    }

    private static void SkipStrings(ref SchemaReader reader, uint field)
    {
        RequireField(ref reader, field);
        var count = reader.ReadListCount();
        for (var i = 0; i < count; i++) reader.ReadString();
    }

    private static string? ReadString(ref SchemaReader reader, uint field)
    {
        RequireField(ref reader, field);
        return reader.ReadOptionalString();
    }

    private static int ReadInt(ref SchemaReader reader, uint field)
    {
        RequireField(ref reader, field);
        return reader.ReadInt32();
    }

    private static void RequireField(ref SchemaReader reader, uint field)
    {
        if (reader.ReadFieldId() != field) throw new SchemaFormatException($"Expected wallet field {field}");
    }

    private static void RequireEnd(ref SchemaReader reader)
    {
        if (reader.HasMore) throw new SchemaFormatException("Unexpected wallet fields");
    }
}
