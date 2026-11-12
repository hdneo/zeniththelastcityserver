using Zenith.Protocol.Components;
using Zenith.Protocol.Schema;
using Zenith.Protocol.ZenFabric;

internal sealed class CharacterScenario(string accountId, string name, uint characterType)
{
    private string? _createdId;
    public string? CharacterId { get; private set; }
    public bool Completed => CharacterId is not null;

    public CommandRequestOp ValidateName(long requestId) =>
        MakeRequest(requestId, 2, w => w.AddString(2, name));

    public CommandRequestOp? Handle(CommandResponseOp response, long nextRequestId)
    {
        if (response.Status != StatusCode.Success)
            throw new InvalidDataException($"Character command #{response.CommandIndex} failed: {response.Message}");

        var reader = new SchemaReader(response.Data);
        switch (response.CommandIndex)
        {
            case 2:
                Require(reader.ReadFieldId() == 1);
                var available = reader.ReadBool();
                Require(!reader.HasMore);
                return available ? CreateCharacter(nextRequestId) : MakeRequest(nextRequestId, 1);
            case 4:
                _createdId = ReadString(ref reader, 1);
                Require(_createdId?.Length == 16 && ReadString(ref reader, 2) == name);
                Require(reader.ReadFieldId() == 3 && reader.ReadUInt32() == characterType);
                Require(reader.ReadFieldId() == 7 && reader.ReadListCount() == 0 && !reader.HasMore);
                return ValidateName(nextRequestId);
            case 1:
                Require(reader.ReadFieldId() == 1 && !reader.ReadBool() && reader.ReadFieldId() == 2);
                foreach (var row in reader.ReadObjectList())
                {
                    var entry = new SchemaReader(row);
                    var id = ReadString(ref entry, 1);
                    if (ReadString(ref entry, 2) != name) continue;
                    Require(id is not null && (_createdId is null || id == _createdId));
                    Require(ReadString(ref entry, 3) == (characterType == 4 ? "classless" : "cyber_ninja") && ReadString(ref entry, 4) == "DPS");
                    Require(entry.ReadFieldId() == 5);
                    var stats = entry.ReadObjectList().Select(ReadStat).ToDictionary(x => x.Key, x => x.Value);
                    Require(int.TryParse(stats["level"], out var level) && level is >= 1 and <= 40 &&
                        int.TryParse(stats["current_XP"], out var experience) && experience >= 0 &&
                        (experience >= 6000) == (level >= 2) &&
                        stats["appearance"].Contains("5A7945FF", StringComparison.Ordinal));
                    CharacterId = id;
                    break;
                }
                Require(Completed && !reader.HasMore);
                return null;
            default:
                throw new InvalidDataException($"Unexpected character command #{response.CommandIndex}");
        }
    }

    private CommandRequestOp CreateCharacter(long requestId) => MakeRequest(requestId, 4, w =>
    {
        w.AddUInt32(6, characterType);
        w.AddUInt32(3, 0); // DPS
        w.AddString(5, name);
        var appearance = new Dictionary<uint, string> { [4] = "1", [3] = "5A7945FF", [2] = "10" };
        w.AddMap(7, appearance, (entry, key) => entry.AddUInt32(1, key), (entry, value) => entry.AddString(2, value));
        w.AddUInt32(8, 1); // Male
    });

    private CommandRequestOp MakeRequest(long requestId, uint index, Action<SchemaWriter>? write = null)
    {
        var data = new SchemaWriter();
        data.AddString(1, accountId);
        write?.Invoke(data);
        return new CommandRequestOp
        {
            RequestId = requestId, EntityId = 4, ComponentId = Ids.CharacterSpawnerComponent,
            CommandIndex = index, Data = data.ToArray(),
        };
    }

    private static KeyValuePair<string, string> ReadStat(byte[] data)
    {
        var reader = new SchemaReader(data);
        return new KeyValuePair<string, string>(ReadString(ref reader, 1)!, ReadString(ref reader, 2)!);
    }

    private static string? ReadString(ref SchemaReader reader, uint field)
    {
        Require(reader.ReadFieldId() == field);
        return reader.ReadOptionalString();
    }

    private static void Require(bool valid)
    {
        if (!valid) throw new InvalidDataException("Unexpected character command payload");
    }
}
