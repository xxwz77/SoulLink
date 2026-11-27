using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SoulLink.TaskEdit;

internal static class DragonFiles
{
    internal sealed class Refusal(int status, string message, string code = "", string state = "", string[] references = null) : Exception(message)
    {
        internal int Status => status;
        internal string Code => code;
        internal string State => state;
        internal string[] References => references ?? Array.Empty<string>();
    }

    internal static string FilePath(string dir, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name != name.Trim() || name.StartsWith('.') || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|CONIN[$]|CONOUT[$]|COM[1-9¹²³]|LPT[1-9¹²³]) *([.]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("配置名含路径、保留名称或非法字符");
        return CheckPath(Path.Combine(dir, name + ".json"));
    }

    private static string[] FindReferences(string configPath, string account, string name)
    {
        var cfg = JsonNode.Parse(File.ReadAllText(configPath), new JsonNodeOptions { PropertyNameCaseInsensitive = true });
        if (cfg is not JsonObject) throw new Refusal(503, "无法读取自动化配置");
        bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var ids = (cfg["Accounts"]?.AsArray() ?? new JsonArray()).Where(a => Same((string)a["UserName"], account)).Select(a => (string)a["Id"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool HasArgs(string args) => Regex.IsMatch(args ?? "", "(?:^|\\s)--startOneDragon\\s+(?:\"" + Regex.Escape(name) + "\"|" + Regex.Escape(name) + ")(?:\\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var refs = new HashSet<string>();
        foreach (var flow in cfg["Automations"]?.AsArray() ?? new JsonArray())
        {
            foreach (var node in flow["Nodes"]?.AsArray() ?? new JsonArray())
            {
                var args = node["Params"];
                string user = ((string)args?["user"])?.Trim().Split('\\').Last();
                if (!(string.IsNullOrEmpty(user) ? ids.Contains((string)flow["AccountId"]) : Same(user, account))) continue;
                string type = node["Type"]?.ToString();
                if ((type is "25" or "ActStartOneDragon" && Same(((string)args?["name"])?.Trim(), name)) ||
                    (type is "22" or "32" or "ActStartBgi" or "ActLaunchProcess" && HasArgs((string)args?["args"])))
                    refs.Add("自动化：" + ((string)flow["Name"] ?? (string)flow["Id"]));
            }
        }
        foreach (var action in cfg["Actions"]?.AsArray() ?? new JsonArray())
            if (ids.Contains((string)action["AccountId"]) && HasArgs((string)action["Arguments"])) refs.Add("快捷动作：" + (string)action["Name"]);
        return refs.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string CheckPath(string path)
    {
        string full = Path.GetFullPath(path);
        for (string part = full; part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("配置路径含链接或重解析点");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    private static FileStream Lock(string root)
    {
        string path = CheckPath(Path.Combine(root, "User", ".soullink-editor.lock"));
        try { return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new Refusal(409, "账号正在执行其他操作，或锁文件不可用"); }
    }

    internal static object Mutate(string root, string account, string configPath, string suffix, JsonObject body, Func<string,string,string> stateOf)
    {
        void RequireStopped()
        {
            string state = stateOf(account, root);
            if (state == "running") throw new Refusal(409, "当前账号的 BGI 正在运行，请停止后重试", state: state);
            if (state != "stopped") throw new Refusal(503, "无法确认当前账号绑定的 BGI 已停止", state: "unknown");
        }
        // 主站启停遵守账号锁，自行启动的 BGI 不遵守。Windows 装载 EXE
        // 必须打开其映像；独占句柄覆盖最后一次状态采样到目标提交的整个窗口。
        FileStream GuardCommit()
        {
            string exe = CheckPath(Path.Combine(root, "BetterGI.exe"));
            FileStream image;
            try { image = File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.None); }
            catch (IOException) { throw new Refusal(409, "BGI 程序文件正在使用，未提交配置", state: "unknown"); }
            try { RequireStopped(); return image; }
            catch { image.Dispose(); throw; }
        }
        string[] parts = suffix.Split('/');
        string op = parts.Length switch { 1 => "create", 2 => "save", 3 when parts[2] is "rename" or "delete" => parts[2], _ => throw new ArgumentException("无效操作") };
        string oldName = op == "create" ? null : Uri.UnescapeDataString(parts[1]);
        string name = op is "create" or "rename" ? (string)body["name"] : oldName;
        if (op is "create" or "rename" && name != null && (name.Length > 128 || name.StartsWith('-') || name.Any(char.IsControl)))
            throw new ArgumentException("配置名无法用于 BGI 启动命令");
        string dir = BgiStore.OneDragonDir(root);
        string dest = FilePath(dir, name);
        string source = oldName == null ? null : FilePath(dir, oldName);
        using var guard = Lock(root);
        RequireStopped();
        if (source != null && !File.Exists(source)) throw new FileNotFoundException("一条龙不存在：" + oldName);
        if (op is "rename" or "create" && (File.Exists(dest) || Directory.Exists(dest))) throw new Refusal(409, "已有同名配置：" + name);
        if (op is "rename" or "delete")
        {
            var refs = FindReferences(configPath, account, oldName);
            if (refs.Length > 0 && (bool?)body["confirmReferences"] != true)
                throw new Refusal(409, "以下自动化引用了该配置，继续后需手动更新引用", "references", references: refs);
        }

        JsonObject node = op switch
        {
            "create" when (string)body["template"] is string template => BgiJson.Read(FilePath(dir, template)).AsObject(),
            "create" => new JsonObject { ["Name"] = name, ["TaskOrder"] = new JsonArray(), ["TaskDefinitions"] = new JsonObject(), ["TaskEnabledList"] = new JsonObject(), ["NextTaskId"] = "" },
            "save" => body,
            "rename" => BgiJson.Read(source).AsObject(),
            _ => null
        };
        string json = null;
        if (node != null)
        {
            string key = node.ContainsKey("name") && !node.ContainsKey("Name") ? "name" : "Name";
            node[key] = name;
            if (op == "create" && (node["TaskOrder"] ?? node["taskOrder"]) is not JsonArray && (node["TaskEnabledList"] ?? node["taskEnabledList"]) is not (JsonObject or JsonArray))
                throw new Refusal(422, "模板缺少一条龙任务结构");
            var errors = BgiValidator.ValidateOneDragon(node).Where(i => i.Blocking).ToList();
            if (errors.Count > 0) throw new Refusal(422, string.Join("；", errors.Select(i => i.Message)));
            json = BgiJson.Serialize(node);
        }
        string backup = source == null ? "" : BgiBackup.Save(BgiBackup.KindOneDragon, source, account);
        if (source != null && backup.Length == 0) throw new Refusal(500, "备份失败，操作已中止");
        if (op == "delete")
        {
            using var image = GuardCommit();
            File.Delete(source);
        }
        else
        {
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(json);
                using var image = GuardCommit();
                if (op == "save") File.Replace(tmp, source, null);
                else
                {
                    File.Move(tmp, dest);
                    if (op == "rename")
                    {
                        try { File.Delete(source); }
                        catch { File.Delete(dest); throw; }
                    }
                }
            }
            finally { File.Delete(tmp); }
        }
        return new { ok = true, name, backup };
    }
}
