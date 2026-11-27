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
    File.WriteAllText(Path.Combine(catalog, "templates.json"), """[{"name":"Northern Prototype","itemName":"Northern Prototype","color":"purple","category":"Weapon Materials","len":8},{"name":"Mystic Ore","itemName":"Mystic Ore","color":"blue","category":"Ore","len":5}]""");
    var templates = BgiTemplatePick.Read(root);
    if (templates.Templates.Count != 2 || !templates.Templates.Any(x => x.ItemName == "Mystic Ore" && x.Category == "Ore")) throw new Exception("template category metadata read failed");
    var patch = new BgiTemplatePick.Model
    {
        BlackList = new() { "Northern Prototype" }, WhiteAllowList = new() { "Mystic Ore" },
        FuzzyBlackList = null!,
        TemplateBlackList = new() { "Global template item" },
    };
    patch.AutoPickConfig["mode"] = "Blacklist";
    File.WriteAllText(Path.Combine(user, BgiTemplatePick.FuzzyBlackFile), "existing fuzzy\r\n");
    BgiTemplatePick.Write(root, patch);
    var saved = BgiTemplatePick.Read(root);
    if (!saved.TemplateBlackList.SequenceEqual(new[] { "Global template item" }))
        throw new Exception("global template blacklist did not round-trip");
    if (saved.Mode != "Blacklist" || !saved.WhiteAllowList.SequenceEqual(new[] { "Mystic Ore" }) ||
        !saved.BlackList.SequenceEqual(new[] { "Northern Prototype" }) ||
        !saved.FuzzyBlackList.SequenceEqual(new[] { "existing fuzzy" }) ||
        saved.AutoPickConfig["futureRuntimeOption"]?["keep"]?.GetValue<int>() != 1)
        throw new Exception("BGI native settings or lists did not round-trip");
    if (File.Exists(Path.Combine(user, BgiTemplatePick.WhiteExcludeFile)))
        throw new Exception("unchanged list file should not have been created");
    var serialized = System.Text.Json.JsonSerializer.Serialize(saved.Templates, BgiJson.Options);
    if (JsonNode.Parse(serialized)?[0]?["category"]?.GetValue<string>() != "Ore")
        throw new Exception("template metadata did not reach the HTTP JSON shape");
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