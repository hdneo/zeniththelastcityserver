using System.Text.Json;

namespace ZenithServer.ZenFabric;

internal static class LootGearCatalog
{
    private sealed record Catalog(Item[] Items);
    internal sealed record Item(string Id, string Type, bool Hidden, int ClassRestriction, int AllowedEquipmentSlots,
        bool Equippable, int Rarity);

    private static readonly Dictionary<string, Item> Items = Load();

    private static Dictionary<string, Item> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ClientData", "item_catalog-56557.json");
        using var stream = File.OpenRead(path);
        var catalog = JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) ?? throw new InvalidDataException($"Missing item definitions in {path}");

        return catalog.Items.Where(item => item.Equippable && !item.Hidden && item.ClassRestriction is 0 or 4 &&
            ((item.Type == "armor" && (item.AllowedEquipmentSlots & 31) != 0) ||
             item.Id is StarterWeapons.BowItemId or StarterWeapons.DesertBowItemId))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    public static Item? Find(string itemId) => Items.GetValueOrDefault(itemId);
}
