using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SoulLink.TaskEdit;

internal static class BgiPickTemplateCategories
{
    internal const string CommonRegion = "通属地区";
    internal const string ArtifactCategory = "圣遗物";
    private const string Specialty = "地方特产";
    private const string FullFlowerName = "来自何处的待放之花";
    private const string LegacyFlowerName = "来自何处的待放之";
    private const string ResourceName = "SoulLink.TaskEdit.PickTemplateCategories.json";

    internal sealed record Classification(string Category, string Region);

    private static readonly (Dictionary<string, Classification> Items, string[] ArtifactSets) Catalog = Load();

    internal static Classification? Find(string itemName)
    {
        var canonical = itemName switch
        {
            LegacyFlowerName => FullFlowerName,
            "残毀的橫脊" => "残毁的横脊",
            "虹彩薔薇" => "虹彩蔷薇",
            _ => itemName,
        };
        return Catalog.Items.GetValueOrDefault(canonical);
    }

    // The list names sets, while the installed templates name individual pieces.
    internal static bool IsArtifactPiece(string itemName) =>
        itemName != "冒险家的经验" &&
        Catalog.ArtifactSets.Any(set => itemName.StartsWith(set, StringComparison.Ordinal));

    internal static string DisplayName(string itemName) =>
        itemName == LegacyFlowerName ? FullFlowerName : itemName;

    internal static string? AlternateItemName(string itemName) => itemName switch
    {
        LegacyFlowerName => FullFlowerName,
        FullFlowerName => LegacyFlowerName,
        _ => null,
    };

    private static (Dictionary<string, Classification>, string[]) Load()
    {
        using var stream = typeof(BgiPickTemplateCategories).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"缺少模板分类资源 {ResourceName}");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        var items = new Dictionary<string, Classification>(StringComparer.Ordinal);
        foreach (var region in root.GetProperty("regions").EnumerateObject())
            foreach (var item in region.Value.EnumerateArray())
                items.Add(ItemName(item), new Classification(Specialty, region.Name));

        var categories = root.GetProperty("categories");
        foreach (var category in categories.EnumerateObject())
        {
            foreach (var item in category.Value.EnumerateArray())
            {
                var name = ItemName(item);
                var region = items.TryGetValue(name, out var existing) ? existing.Region : CommonRegion;
                items[name] = new Classification(category.Name, region);
            }
        }

        return (items, categories.GetProperty(ArtifactCategory).EnumerateArray().Select(ItemName).ToArray());
    }

    private static string ItemName(JsonElement item) =>
        item.GetString() ?? throw new InvalidDataException("模板分类包含空名称");
}
