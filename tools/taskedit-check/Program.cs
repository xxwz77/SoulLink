using System.Text.Json.Nodes;
using SoulLink.TaskEdit;

var root = Path.Combine(Path.GetTempPath(), "sl-pick-roundtrip-" + Guid.NewGuid().ToString("N"));
try
{
    var user = Path.Combine(root, "User"); Directory.CreateDirectory(user);
    var config = Path.Combine(user, "config.json");
    File.WriteAllText(config, """{"autoPickConfig":{"enabled":true,"mode":"Whitelist","itemIconLeftOffset":77,"futureRuntimeOption":{"keep":1},"templatePick":{"obsolete":true}}}""");
    var loaded = BgiPickList.Read(root) ?? throw new Exception("read failed");
    if (!loaded.Enabled || loaded.Mode != "Whitelist") throw new Exception("native auto pick config read failed");
    if (loaded.NativeConfig["futureRuntimeOption"]?["keep"]?.GetValue<int>() != 1) throw new Exception("unknown native option lost on read");
    loaded.NativeConfig["interactionDelay"] = 123;
    BgiPickList.Write(root, loaded, Path.Combine(user, ".backup"));
    var ap = JsonNode.Parse(File.ReadAllText(config))!["autoPickConfig"]!;
    if (ap["templatePick"] != null) throw new Exception("obsolete templatePick retained");
    if (ap["futureRuntimeOption"]?["keep"]?.GetValue<int>() != 1 || ap["itemIconLeftOffset"]?.GetValue<int>() != 77 || ap["interactionDelay"]?.GetValue<int>() != 123) throw new Exception("native config options not preserved");

    var catalog = Path.Combine(root, "GameTask", "AutoPick", "Assets", "莫版模板"); Directory.CreateDirectory(catalog);
    File.WriteAllText(Path.Combine(catalog, "templates.json"), """
        [
          {"name":"Northern Prototype","itemName":"Northern Prototype","color":"purple","category":"Weapon Materials","len":8},
          {"name":"Mystic Ore","itemName":"Mystic Ore","color":"blue","category":"Ore","len":5},
          {"name":"旧模板","color":"蓝","file":"蓝/其他材料/旧模板.png"},
          {"name":"嘟嘟莲","color":"白","file":"白/嘟嘟莲.png"},
          {"name":"金鱼草","color":"白","file":"白/金鱼草.png"},
          {"name":"松茸","color":"白","file":"白/松茸.png"},
          {"name":"虹彩蔷薇","color":"白","file":"白/虹彩蔷薇.png"},
          {"name":"残毀的橫脊","color":"绿","file":"绿/残毀的橫脊.png"},
          {"name":"来自何处的待放之","color":"绿","file":"绿/来自何处的待放之.png"},
          {"name":"来自何处的待放之花","color":"绿","file":"绿/来自何处的待放之花.png"},
          {"name":"来自何处的待放之花 alternate image","itemName":"来自何处的待放之花","color":"绿"},
          {"name":"来自何处的待放之花 alternate color","itemName":"来自何处的待放之花","color":"蓝"},
          {"name":"冒险家之花","color":"灰","file":"灰/冒险家之花.png"},
          {"name":"冒险家的经验","color":"蓝","file":"蓝/冒险家的经验.png"},
          {"name":"妖精绿艾","color":"白","file":"白/妖精绿艾.png"}
        ]
        """);
    var templates = BgiTemplatePick.Read(root);
    if (templates.Templates.Count != 14) throw new Exception("template catalog deduplication changed");
    RequireTemplate("Mystic Ore", "Ore", "通属地区", "blue");
    RequireTemplate("旧模板", "其他材料", "通属地区", "蓝");
    RequireTemplate("嘟嘟莲", "地方特产", "蒙德", "白");
    RequireTemplate("金鱼草", "食材", "蒙德", "白");
    RequireTemplate("松茸", "食材", "通属地区", "白");
    RequireTemplate("虹彩蔷薇", "地方特产", "枫丹", "白");
    RequireTemplate("残毀的橫脊", "怪物掉落", "通属地区", "绿");
    RequireTemplate("冒险家之花", "圣遗物", "通属地区", "灰");
    RequireTemplate("冒险家的经验", "角色经验材料", "通属地区", "蓝");
    RequireTemplate("妖精绿艾", "地方特产", "通属地区", "白");
    const string legacyFlower = "来自何处的待放之";
    const string fullFlower = "来自何处的待放之花";
    var legacyTemplate = RequireTemplate(legacyFlower, "怪物掉落", "通属地区", "绿");
    var fullTemplate = RequireTemplate(fullFlower, "怪物掉落", "通属地区", "绿");
    RequireTemplate(fullFlower, "怪物掉落", "通属地区", "蓝");
    if (legacyTemplate.Name != legacyFlower || legacyTemplate.DisplayName != fullFlower ||
        legacyTemplate.AlternateItemName != fullFlower || fullTemplate.AlternateItemName != legacyFlower)
        throw new Exception("legacy template name or display compatibility changed");

    BgiTemplatePick.TemplateInfo RequireTemplate(string name, string category, string region, string color)
    {
        var template = templates.Templates.Single(x => x.ItemName == name && x.Color == color);
        if (template.Category != category || template.Region != region)
            throw new Exception($"template classification failed for {name}: {template.Category}, {template.Region}");
        return template;
    }
    var patch = new BgiTemplatePick.Model
    {
        BlackList = new() { "Northern Prototype" }, WhiteAllowList = new() { "Mystic Ore" },
        FuzzyBlackList = null!,
        TemplateBlackList = new() { "Global template item", legacyFlower },
    };
    patch.AutoPickConfig["mode"] = "Blacklist";
    File.WriteAllText(Path.Combine(user, BgiTemplatePick.FuzzyBlackFile), "existing fuzzy\r\n");
    BgiTemplatePick.Write(root, patch);
    var saved = BgiTemplatePick.Read(root);
    if (!saved.TemplateBlackList.SequenceEqual(new[] { "Global template item", legacyFlower }))
        throw new Exception("global template blacklist did not round-trip");
    if (saved.Mode != "Blacklist" || !saved.WhiteAllowList.SequenceEqual(new[] { "Mystic Ore" }) ||
        !saved.BlackList.SequenceEqual(new[] { "Northern Prototype" }) ||
        !saved.FuzzyBlackList.SequenceEqual(new[] { "existing fuzzy" }) ||
        saved.AutoPickConfig["futureRuntimeOption"]?["keep"]?.GetValue<int>() != 1)
        throw new Exception("BGI native settings or lists did not round-trip");
    if (File.Exists(Path.Combine(user, BgiTemplatePick.WhiteExcludeFile)))
        throw new Exception("unchanged list file should not have been created");
    var serialized = System.Text.Json.JsonSerializer.Serialize(legacyTemplate, BgiJson.Options);
    var row = JsonNode.Parse(serialized)!;
    if (row["category"]?.GetValue<string>() != "怪物掉落" ||
        row["region"]?.GetValue<string>() != "通属地区" ||
        row["color"]?.GetValue<string>() != "绿" ||
        row["displayName"]?.GetValue<string>() != fullFlower ||
        row["itemName"]?.GetValue<string>() != legacyFlower ||
        row["alternateItemName"]?.GetValue<string>() != fullFlower)
        throw new Exception("template classification metadata did not reach the HTTP JSON shape");
    File.WriteAllText(config, "{broken");
    try { patch.BlackList.Clear(); BgiTemplatePick.Write(root, patch); throw new Exception("corrupt config was accepted"); }
    catch (System.Text.Json.JsonException) { }
    if (BgiPickList.ReadLines(user, BgiTemplatePick.BlackFile).Count != 1)
        throw new Exception("list was modified before validation of config");
    Console.WriteLine("PASS native picker settings, BGI templates, categories, black/white lists and legacy-rule removal");
}
finally { try { Directory.Delete(root, true); } catch { } }

namespace SoulLink.TaskEdit
{
 internal static class BgiBackup
 {
  internal const string KindScriptGroup = "scriptgroup";
  internal const string KindOneDragon = "onedragon";
  internal const string KindPickList = "picklist";
  internal static string Save(string kind, string path, string? account = null) => path;
  internal static string DirFor(string kind) => Path.Combine(Path.GetTempPath(), "unused");
 }
}
