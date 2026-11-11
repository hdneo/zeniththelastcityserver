using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

internal sealed class ShopScenario
{
    private const uint Wallet = 5001;
    private const uint Inventory = 1015;
    private const string Cosmetic = "armor_feet_shin_cosmetic";
    private const int Price = 650;
    private readonly HashSet<string> _items = new(StringComparer.Ordinal);
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
        _initiallyOwned = _items.Contains(Cosmetic);
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
                var snapshot = new SchemaReader(inventory.Data.Data);
                for (uint field = 1; field <= 8; field++)
                {
                    Require(snapshot.ReadFieldId() == field);
                    var count = snapshot.ReadListCount();
                    for (var i = 0; i < count; i++) snapshot.ReadBytesSpan();
                }
                RecordItems(ref snapshot);
                break;
            case ComponentUpdateOp { ComponentId: Wallet } wallet when wallet.EntityId == character:
                var balance = new SchemaReader(wallet.Data);
                _balance = ReadBalance(ref balance);
                break;
            case ComponentUpdateOp { ComponentId: Inventory } inventory when inventory.EntityId == character:
                var changes = new SchemaReader(inventory.Data);
                RecordItems(ref changes);
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
                    _items.Count == 1 && _items.Contains(Cosmetic) && (_initiallyOwned || _reportedOwnership));
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
                Completed = true;
                break;
        }
        return null;
    }

    private void RecordItems(ref SchemaReader reader)
    {
        Require(reader.ReadFieldId() == 9);
        foreach (var raw in reader.ReadObjectList())
        {
            var entry = new SchemaReader(raw);
            Require(entry.ReadByte() == 0 && entry.ReadFieldId() == 1);
            entry.ReadInt32();
            Require(entry.ReadFieldId() == 2);
            var item = new SchemaReader(entry.ReadBytesSpan());
            Require(item.ReadFieldId() == 1 && item.ReadOptionalString() is not null && item.ReadFieldId() == 2);
            Require(_items.Add(item.ReadOptionalString()!));
        }
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
