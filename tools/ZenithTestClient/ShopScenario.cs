using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

internal sealed class ShopScenario
{
    private const uint Wallet = 5001;
    private const uint Inventory = 1015;
    private const uint Equipment = 1016;
    private const int FeetCosmeticSlot = 22;
    private const string Cosmetic = "armor_feet_shin_cosmetic";
    private const int Price = 650;
    private readonly Dictionary<int, string> _items = new();
    private bool _cosmeticEquipped;
    private long _inventoryOwner;
    private long _balance;
    private long _initialBalance;
    private bool _initiallyOwned;
    private bool _reportedOwnership;
    private int _responses;
    private int _transactionStarts;
    private long _transaction;
    public bool Completed { get; private set; }

    public CommandRequestOp Start(long request, long character)
    {
        _initialBalance = _balance;
        _initiallyOwned = _items.ContainsValue(Cosmetic) || _cosmeticEquipped;
        var data = new SchemaWriter();
        data.AddInt32(1, 0); // Steam
        data.AddString(2, "local-test");
        data.AddEmptyOption(3);
        return Command(request, character, 7, data);
    }

    public ZfOp? Handle(ZfOp op, long request, long character)
    {
        switch (op)
        {
            case AddComponentOp { Data.ComponentId: Wallet } wallet:
                var initial = new SchemaReader(wallet.Data.Data);
                Require(initial.ReadFieldId() == 1);
                initial.ReadInt64();
                _balance = ReadBalance(ref initial);
                break;
            case AddComponentOp { Data.ComponentId: Inventory } inventory:
                _inventoryOwner = inventory.EntityId;
                var snapshot = new SchemaReader(inventory.Data.Data);
                for (uint field = 1; field <= 8; field++)
                {
                    Require(snapshot.ReadFieldId() == field);
                    var count = snapshot.ReadListCount();
                    for (var i = 0; i < count; i++) snapshot.ReadBytesSpan();
                }
                Require(snapshot.ReadFieldId() == 9);
                RecordItems(ref snapshot);
                break;
            case ComponentUpdateOp { ComponentId: Wallet } wallet when wallet.EntityId == character:
                var balance = new SchemaReader(wallet.Data);
                _balance = ReadBalance(ref balance);
                break;
            case ComponentUpdateOp { ComponentId: Inventory } inventory when inventory.EntityId == character:
                var changes = new SchemaReader(inventory.Data);
                while (changes.HasMore)
                {
                    switch (changes.ReadFieldId())
                    {
                        case 5: changes.ReadObjectList(); break;
                        case 9: RecordItems(ref changes); break;
                        default: throw new InvalidDataException("Unexpected inventory change");
                    }
                }
                break;
            case AddComponentOp { Data.ComponentId: Equipment } equipment when equipment.EntityId == _inventoryOwner:
                ReadEquipment(equipment.Data.Data);
                break;
            case ComponentUpdateOp { ComponentId: Equipment } equipment when equipment.EntityId == character:
                ReadEquipment(equipment.Data);
                break;
            case ComponentUpdateOp { ComponentId: Ids.CharacterCredentialsComponent } rewards
                when rewards.EntityId == character:
                var ownership = new SchemaReader(rewards.Data);
                Require(ownership.ReadFieldId() == 17 && ownership.ReadListCount() == 1);
                _reportedOwnership = ownership.ReadString() == Cosmetic && !ownership.HasMore;
                break;
            case CommandResponseOp { ComponentId: Wallet, CommandIndex: 7 } session:
                Require(session.Status == StatusCode.Success && session.Data.Length == 0);
                return StartTransaction(request, character, null, "vc_zennies01000_zenithtokens0100");
            case CommandResponseOp { ComponentId: Wallet, CommandIndex: 8 } started:
                _transactionStarts++;
                if (_transactionStarts == 1)
                {
                    Require(started.Status == StatusCode.ApplicationError);
                    _responses = 1;
                    return StartTransaction(request, character, Cosmetic);
                }
                Require(started.Status == StatusCode.Success);
                var transaction = new SchemaReader(started.Data);
                Require(transaction.ReadFieldId() == 1);
                _transaction = transaction.ReadInt64();
                Require(_transaction > 0 && transaction.ReadFieldId() == 2);
                transaction.ReadOptionalString();
                Require(transaction.ReadFieldId() == 3 && transaction.ReadInt32() == 0);
                Require(transaction.ReadFieldId() == 4 && transaction.ReadInt32() == 1);
                return Checkout(request, character, _responses == 3 ? "armor_hands_shin_cosmetic" : Cosmetic);
            case CommandResponseOp { ComponentId: Wallet, CommandIndex: 11 } purchase:
                _responses++;
                var expectedBalance = _initialBalance - (_initiallyOwned ? 0 : Price);
                var insufficient = _responses == 4;
                Require(purchase.Status == StatusCode.Success && _balance == expectedBalance &&
                    (_cosmeticEquipped ? _items.Count == 0 : _items.Count == 1 && _items.ContainsValue(Cosmetic)) &&
                    (_initiallyOwned || _reportedOwnership));
                var result = new SchemaReader(purchase.Data);
                Require(result.ReadFieldId() == 1);
                result.ReadInt64();
                Require(result.ReadFieldId() == 2);
                result.ReadOptionalString();
                Require(result.ReadFieldId() == 3 && result.ReadInt32() == 0);
                Require(result.ReadFieldId() == 4 && result.ReadInt32() == (insufficient ? -1 : 7));
                Require(result.ReadFieldId() == 5);
                result.ReadInt32();
                Require(result.ReadFieldId() == 6);
                result.ReadInt32();
                Require(result.ReadFieldId() == 7 && result.ReadInt32() == (_responses == 2 && !_initiallyOwned ? -Price : 0));
                Require(result.ReadFieldId() == 8 &&
                    result.ReadOptionalString() == (insufficient ? "armor_hands_shin_cosmetic" : Cosmetic));
                Require(result.ReadFieldId() == 9 && result.ReadInt32() == (insufficient ? 700 : Price));
                Require(result.ReadFieldId() == 10 && result.ReadOptionalString() is null);
                Require(result.ReadFieldId() == 11 && result.ReadInt32() == 0);
                for (uint field = 12; field <= 15; field++)
                    Require(result.ReadFieldId() == field && !result.ReadOptionHasValue());
                Require(result.ReadFieldId() == 16 && result.ReadBool());
                Require(result.ReadFieldId() == 17 && result.ReadBool() == !insufficient);
                Require(result.ReadFieldId() == 18 && result.ReadOptionHasValue() && result.ReadInt32() == _balance);
                Require(result.ReadFieldId() == 19 &&
                    result.ReadOptionalString() == (insufficient ? "Insufficient Zennies" : null));
                Require(result.ReadFieldId() == 20 && result.ReadOptionalString() == "StorePanel_NeoCity_MainStore" &&
                    !result.HasMore);
                if (_responses == 2) return Checkout(request, character, Cosmetic);
                if (_responses == 3 && _balance < 700)
                    return StartTransaction(request, character, "armor_hands_shin_cosmetic");

                Console.WriteLine($"   [OK] Cosmetic checkout and duplicate handling. Balance {_balance} Zennies");
                if (_cosmeticEquipped)
                {
                    Console.WriteLine("   [OK] Equipped cosmetic was restored in the initial character snapshot");
                    return Move(request, character, 3, 1, FeetCosmeticSlot, 5, 0);
                }
                return Move(request, character, 1, 5, _items.Single().Key, 1, 20);
            case CommandResponseOp { ComponentId: Inventory, CommandIndex: 3 } moved:
                var move = new SchemaReader(moved.Data);
                Require(moved.Status == StatusCode.Success && move.ReadFieldId() == 1);
                var id = move.ReadUInt32();
                Require(move.ReadFieldId() == 2 && move.ReadBool() == (id != 1) && !move.HasMore);
                switch (id)
                {
                    case 1:
                        Require(!_cosmeticEquipped && _items.ContainsValue(Cosmetic));
                        return Move(request, character, 2, 5, _items.Single().Key, 1, FeetCosmeticSlot);
                    case 2:
                        Require(_cosmeticEquipped && _items.Count == 0);
                        return Move(request, character, 3, 1, FeetCosmeticSlot, 5, 0);
                    case 3:
                        Require(!_cosmeticEquipped && _items.GetValueOrDefault(0) == Cosmetic);
                        return Move(request, character, 4, 5, 0, 1, FeetCosmeticSlot);
                    case 4:
                        Require(_cosmeticEquipped && _items.Count == 0);
                        Console.WriteLine("   [OK] Cosmetic equip and unequip replicate, and an incompatible slot is rejected");
                        Completed = true;
                        break;
                }
                break;
        }
        return null;
    }

    private void RecordItems(ref SchemaReader reader)
    {
        foreach (var raw in reader.ReadObjectList())
        {
            var entry = new SchemaReader(raw);
            var removed = entry.ReadBool();
            Require(entry.ReadFieldId() == (removed ? 2 : 1));
            var index = entry.ReadInt32();
            if (removed)
            {
                Require(_items.Remove(index) && !entry.HasMore);
                continue;
            }
            Require(entry.ReadFieldId() == 2);
            var item = new SchemaReader(entry.ReadBytesSpan());
            _items[index] = ItemId(ref item);
        }
    }

    private void ReadEquipment(byte[] data)
    {
        var reader = new SchemaReader(data);
        Require(reader.ReadFieldId() == 1);
        var slots = new Dictionary<int, string>();
        foreach (var raw in reader.ReadObjectList())
        {
            var entry = new SchemaReader(raw);
            Require(entry.ReadFieldId() == 1);
            var slot = entry.ReadInt32();
            Require(entry.ReadFieldId() == 2);
            var item = new SchemaReader(entry.ReadBytesSpan());
            slots.Add(slot, ItemId(ref item));
        }
        _cosmeticEquipped = slots.GetValueOrDefault(FeetCosmeticSlot) == Cosmetic;
        Require(slots.GetValueOrDefault(3) == "armor_feet_reimagined_starter" && slots.ContainsKey(18));
    }

    private static string ItemId(ref SchemaReader item)
    {
        Require(item.ReadFieldId() == 1 && item.ReadOptionalString() is not null && item.ReadFieldId() == 2);
        return item.ReadOptionalString()!;
    }

    private static CommandRequestOp Move(long request, long character, uint id, int fromBag, int fromSlot, int toBag, int toSlot)
    {
        static void Slot(SchemaWriter writer, int bag, int slot)
        {
            writer.AddInt32(1, bag);
            writer.AddInt32(2, slot);
            writer.AddEmptyOption(3);
        }

        var data = new SchemaWriter();
        data.AddUInt32(1, id);
        data.AddObject(2, move =>
        {
            move.AddObject(1, from => Slot(from, fromBag, fromSlot));
            move.AddObject(2, to => Slot(to, toBag, toSlot));
        });
        data.AddBool(3, true);
        return new() { RequestId = request, EntityId = character, ComponentId = Inventory, CommandIndex = 3, Data = data.ToArray() };
    }

    private static long ReadBalance(ref SchemaReader reader)
    {
        Require(reader.ReadFieldId() == 2);
        var balance = reader.ReadInt64();
        Require(!reader.HasMore);
        return balance;
    }

    private static CommandRequestOp StartTransaction(long request, long character, string? item, string? sku = null)
    {
        var data = new SchemaWriter();
        data.AddString(1, "StorePanel_NeoCity_MainStore");
        data.AddString(2, item);
        data.AddString(3, sku);
        return Command(request, character, 8, data);
    }

    private CommandRequestOp Checkout(long request, long character, string item)
    {
        var writer = new SchemaWriter();
        writer.AddInt64(1, _transaction);
        writer.AddString(2, "en");
        writer.AddInt32(3, 0);
        writer.AddInt32(4, 0);
        writer.AddInt32(5, -1);
        writer.AddInt32(6, int.MaxValue); // The server supplies the real balance.
        writer.AddInt32(7, 0);
        writer.AddString(8, item);
        writer.AddInt32(9, 1); // The server supplies the real price.
        writer.AddString(10, null);
        writer.AddInt32(11, 0);
        for (uint field = 12; field <= 15; field++) writer.AddEmptyOption(field);
        writer.AddBool(16, false);
        writer.AddBool(17, false);
        writer.AddEmptyOption(18);
        writer.AddEmptyOption(19);
        writer.AddString(20, "StorePanel_NeoCity_MainStore");
        return Command(request, character, 11, writer);
    }

    private static CommandRequestOp Command(long request, long character, uint index, SchemaWriter data) =>
        new() { RequestId = request, EntityId = character, ComponentId = Wallet, CommandIndex = index, Data = data.ToArray() };

    private static void Require(bool valid)
    {
        if (!valid) throw new InvalidDataException("Unexpected cosmetic shop response");
    }
}
