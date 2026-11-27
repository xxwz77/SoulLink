using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// P1 数据面 HTTP API（设计文档 §E，挂在 /taskedit/api/*）。
    /// 鉴权已由 P0 AuthGate 在 TaskEditServer 统一拦截，这里不再重复。
    /// </summary>
    internal static class TaskEditApi
    {
        /// <summary>BGI 根目录 + 版本信息（由 query bgi 指定；空则取 config.json bgiPaths 或回环探测，见 TaskEditServer）。</summary>
        internal sealed class Ctx
        {
            internal string Root = "";
            internal BgiIdentity.Info Identity = new();
            internal string BackupDir = "";
        }

        /// <summary>解析 BGI 根目录：query 参数 bgi（优先）→ account → config.json。</summary>
        internal static Ctx ResolveRoot(string rootOverride, string account, string configJsonPath)
        {
            var ctx = new Ctx();
            string root = rootOverride;
            if (string.IsNullOrWhiteSpace(root)) root = BgiIdentity.ResolveRoot(account, configJsonPath);
            ctx.Root = root;
            if (root.Length > 0)
            {
                ctx.Identity = BgiIdentity.Detect(root);
                ctx.BackupDir = Path.Combine(root, "User", ".taskedit-backup");
            }
            return ctx;
        }

        private static readonly object PickWriteLock = new();

        internal static bool TryHandle(HttpListenerContext ctx, string path, string queryBgi, string configJsonPath, AuthGate auth, Func<string, string, string> editorState)
        {
            // 只处理 /taskedit/api/*
            string p = path;
            if (!p.StartsWith("/taskedit/api", StringComparison.OrdinalIgnoreCase)) return false;

            var q = ctx.Request.QueryString;
            string account = q["account"] ?? "";
            const string dragonRoute = "/taskedit/api/onedragons";
            bool dragonWrite = ctx.Request.HttpMethod == "POST" && (path.Equals(dragonRoute, StringComparison.OrdinalIgnoreCase) || path.StartsWith(dragonRoute + "/", StringComparison.OrdinalIgnoreCase));
            bool templateListWrite = ctx.Request.HttpMethod == "POST" && path.Equals("/taskedit/api/templatelists", StringComparison.OrdinalIgnoreCase);
            bool pickListWrite = ctx.Request.HttpMethod == "POST" && path.Equals("/taskedit/api/picklists", StringComparison.OrdinalIgnoreCase);
            if (dragonWrite || templateListWrite || pickListWrite)
            {
                if (!auth.MatchesAnyCookie(ctx)) return Json(ctx, new { ok = false, error = "请先登录后再修改配置" }, 401);
                if (!SameOrigin(ctx.Request))
                    return Json(ctx, new { ok = false, error = "需要同源请求" }, 403);
                if (!string.Equals(ctx.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                    return Json(ctx, new { ok = false, error = "需要 JSON 请求体" }, 415);
                if (q.GetValues("account")?.Length != 1 || string.IsNullOrWhiteSpace(account) || q.GetValues("bgi") != null)
                    return Json(ctx, new { ok = false, error = "必须指定唯一账号，不能覆盖安装路径" }, 400);
            }
            var ctx2 = ResolveRoot(string.IsNullOrWhiteSpace(account) ? queryBgi : "", account, configJsonPath);
            if (ctx2.Root.Length == 0)
                return Json(ctx, new { ok = false, error = "当前账号未配置有效的 BGI 安装路径" }, 404);

            // 去掉前缀 /taskedit/api
            string rest = p.Substring("/taskedit/api".Length);
            if (rest.Length == 0) rest = "/";

            string method = ctx.Request.HttpMethod;

            try
            {
                if (dragonWrite)
                {
                    var body = JsonNode.Parse(ReadBody(ctx)) as JsonObject ?? throw new ArgumentException("请求体必须是 JSON 对象");
                    return Json(ctx, DragonFiles.Mutate(ctx2.Root, account, configJsonPath, path.Substring(dragonRoute.Length), body, editorState));
                }
                if (rest.Equals("/groups", StringComparison.OrdinalIgnoreCase) && method == "GET")
                    return Json(ctx, BgiStore.ListGroups(ctx2.Root).Select(n => (object)new { name = n }));

                if (rest.StartsWith("/groups/", StringComparison.OrdinalIgnoreCase))
                {
                    string name = Uri.UnescapeDataString(rest.Substring("/groups/".Length)).TrimEnd('/');
                    if (name.Length == 0) return false;
                    if (method == "GET")
                    {
                        var node = BgiStore.ReadGroup(ctx2.Root, name);
                        return Json(ctx, node);
                    }
                    if (method == "POST")
                    {
                        string body = ReadBody(ctx);
                        var node = JsonNode.Parse(body) ?? new JsonObject();
                        // 校验
                        var issues = BgiValidator.ValidateGroup(node);
                        issues.AddRange(BgiValidator.ValidateCapabilities(node, ctx2.Identity.Channel, ctx2.Identity.BaseVersion));
                        if (issues.Any(i => i.Blocking))
                            return Json(ctx, new { ok = false, errors = issues.Select(i => new { path = i.Path, message = i.Message, blocking = i.Blocking }) }, 422);
                        string backup;
                        lock (PickWriteLock) backup = BgiStore.WriteGroup(ctx2.Root, name, node, ctx2.BackupDir);
                        return Json(ctx, new { ok = true, name, backup });
                    }
                    return false;
                }

                if (rest.Equals("/onedragons", StringComparison.OrdinalIgnoreCase) && method == "GET")
                    return Json(ctx, BgiStore.ListOneDragons(ctx2.Root).Select(n => (object)new { name = n }));

                if (rest.StartsWith("/onedragons/", StringComparison.OrdinalIgnoreCase))
                {
                    string name = Uri.UnescapeDataString(rest.Substring("/onedragons/".Length)).TrimEnd('/');
                    if (name.Length == 0) return false;
                    if (method == "GET")
                        return Json(ctx, BgiStore.ReadOneDragon(ctx2.Root, name));
                    return false;
                }

                // Both routes expose the same native picker; keep the original catalog route for existing clients.
                if (rest.Equals("/picklists", StringComparison.OrdinalIgnoreCase) ||
                    rest.Equals("/templatelists", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(q["group"]))
                        return HandleGroupTemplateLists(ctx, ctx2, q["group"]);
                    if (method == "GET")
                    {
                        var native = BgiTemplatePick.Read(ctx2.Root);
                        return Json(ctx, new
                        {
                            ok = true, root = ctx2.Root,
                            enabled = native.AutoPickConfig["enabled"]?.GetValue<bool>() ?? false,
                            mode = native.Mode,
                            blacklistModePickEnabled = native.AutoPickConfig["blacklistModePickEnabled"]?.GetValue<bool>() ?? false,
                            whitelistModeDoNotPickEnabled = native.AutoPickConfig["whitelistModeDoNotPickEnabled"]?.GetValue<bool>() ?? false,
                            autoPickConfig = native.AutoPickConfig,
                            blackList = native.BlackList,
                            fuzzyBlackList = native.FuzzyBlackList,
                            blackModeAllowList = native.BlackAllowList,
                            whiteModeAllowList = native.WhiteAllowList,
                            whiteModeExcludeList = native.WhiteExcludeList,
                            whiteModeFuzzyExcludeList = native.WhiteFuzzyExcludeList,
                            defaultBlackList = native.DefaultBlackList,
                            defaultBlackFuzzy = native.DefaultBlackFuzzy,
                            defaultWhiteList = native.DefaultWhiteList,
                            defaultWhiteFuzzy = native.DefaultWhiteFuzzy,
                            templateBlackList = native.TemplateBlackList,
                            templateNames = native.Templates.Select(x => x.Name).Distinct(),
                            templates = native.Templates, warning = native.Warning,
                            files = new { config = "User/config.json", black = BgiTemplatePick.BlackFile, fuzzy = BgiTemplatePick.FuzzyBlackFile, blackAllow = BgiTemplatePick.BlackAllowFile, whiteAllow = BgiTemplatePick.WhiteAllowFile, whiteExclude = BgiTemplatePick.WhiteExcludeFile, whiteFuzzyExclude = BgiTemplatePick.WhiteFuzzyFile }
                        });
                    }
                    if (method == "POST")
                    {
                        string body = ReadBody(ctx);
                        JsonNode? node;
                        try { node = JsonNode.Parse(body); }
                        catch (Exception) { return Json(ctx, new { ok = false, error = "请求体不是合法 JSON" }, 400); }
                        if (node is not JsonObject jo) return Json(ctx, new { ok = false, error = "请求体必须是 JSON 对象" }, 400);

                        return SavePickLists(ctx, ctx2.Root, jo);
                    }
                    return false;
                }

                if (rest.Equals("/jssettings", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    try { return Json(ctx, ReadJsSettings(ctx2.Root, q["folder"] ?? "")); }
                    catch (FileNotFoundException ex) { return Json(ctx, new { ok = false, state = ex.Message.Contains("manifest.json") ? "missing-script" : (ex.Message.Contains("settings_ui") ? "missing-settings-file" : "missing"), error = ex.Message }, 404); }
                    catch (JsonException ex) { return Json(ctx, new { ok = false, state = "invalid", error = ex.Message }, 422); }
                    catch (IOException ex) { return Json(ctx, new { ok = false, state = "request-failed", error = ex.Message }, 500); }
                }

                if (rest.Equals("/pickable/js", StringComparison.OrdinalIgnoreCase) && method == "GET")
                    return Json(ctx, BgiStore.ListPickableJs(ctx2.Root));

                if (rest.Equals("/pickable/pathing", StringComparison.OrdinalIgnoreCase) && method == "GET")
                    return Json(ctx, BgiStore.ListPickablePathing(ctx2.Root));

                if (rest.Equals("/pickable/solotasks", StringComparison.OrdinalIgnoreCase) && method == "GET")
                    return Json(ctx, BgiStore.ListPickableSoloTasks(ctx2.Root));

                if (rest.Equals("/version", StringComparison.OrdinalIgnoreCase) && method == "GET")
                    return Json(ctx, new
                    {
                        root = ctx2.Root,
                        channel = ctx2.Identity.Channel,
                        baseVersion = ctx2.Identity.BaseVersion,
                        productVersion = ctx2.Identity.ProductVersion,
                        fileVersion = ctx2.Identity.FileVersion,
                        company = ctx2.Identity.Company,
                    });

                if (rest.Equals("/capabilities", StringComparison.OrdinalIgnoreCase) && method == "GET")
                    return Json(ctx, new
                    {
                        channel = ctx2.Identity.Channel,
                        baseVersion = ctx2.Identity.BaseVersion,
                        projectTypes = BgiValidator.ProjectTypes,
                        projectStatuses = BgiValidator.ProjectStatuses,
                        schedules = BgiValidator.Schedules,
                        teabagOnlyFields = BgiValidator.TeabagOnlyFields,
                    });

                return false;
            }
            catch (DragonFiles.Refusal ex)
            {
                return Json(ctx, new { ok = false, error = ex.Message, ex.Code, ex.State, ex.References }, ex.Status);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
            {
                return Json(ctx, new { ok = false, error = "配置或请求格式无效：" + ex.Message }, 400);
            }
            catch (FileNotFoundException ex)
            {
                Json(ctx, new { ok = false, error = ex.Message }, 404);
                return true;
            }
            catch (Exception ex)
            {
                Json(ctx, new { ok = false, error = ex.GetType().Name + ": " + ex.Message }, 500);
                return true;
            }
        }

        private static object ReadJsSettings(string root, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || folder.Contains("..") || folder.Contains("/") || folder.Contains("\\")) throw new ArgumentException("脚本目录无效");
            string dir = Path.GetFullPath(Path.Combine(BgiStore.JsScriptDir(root), folder));
            string baseDir = Path.GetFullPath(BgiStore.JsScriptDir(root)) + Path.DirectorySeparatorChar;
            if (!dir.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("脚本目录越界");
            string manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("脚本 manifest.json 不存在");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject ?? throw new JsonException("manifest 必须是 JSON 对象");
            JsonNode declaration = manifest["settings_ui"] ?? manifest["SettingsUi"];
            if (declaration == null) return new { ok = true, declared = false };
            JsonNode schema;
            if (declaration is JsonValue)
            {
                string relative;
                try { relative = declaration.GetValue<string>(); } catch { throw new JsonException("settings_ui 必须是文件路径或内联 schema"); }
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains("..")) throw new ArgumentException("settings_ui 路径无效");
                string file = Path.GetFullPath(Path.Combine(dir, relative));
                if (!file.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("settings_ui 路径越界");
                if (!File.Exists(file)) throw new FileNotFoundException("manifest 已声明 settings_ui，但文件不存在");
                schema = JsonNode.Parse(File.ReadAllText(file));
            }
            else schema = declaration.DeepClone();
            if (schema is not JsonArray && schema is not JsonObject) throw new JsonException("SettingsUi 必须是数组或对象");
            return new { ok = true, declared = true, settingsUi = schema };
        }
        private static bool HandleGroupTemplateLists(HttpListenerContext ctx, Ctx installation, string name)
        {
            var method = ctx.Request.HttpMethod;
            if (method != "GET" && method != "POST") return false;
            var patch = method == "POST"
                ? JsonNode.Parse(ReadBody(ctx)) as JsonObject ?? throw new ArgumentException("请求体必须是 JSON 对象")
                : null;
            lock (PickWriteLock)
            {
                return Json(ctx, GroupTemplateLists(installation.Root, name, installation.BackupDir, patch));
            }
        }

        internal static object GroupTemplateLists(string root, string name, string backupDir, JsonObject patch)
        {
            var group = BgiStore.ReadGroup(root, name) as JsonObject
                ?? throw new InvalidOperationException("配置组格式无效");
            var settings = FindObject(group, "config", patch != null);
            var pick = settings == null ? null : FindObject(settings, "autoPickConfig", patch != null);
            if (patch == null)
                return new
                {
                    ok = true,
                    whiteList = ToList(pick == null ? null : pick[KeyFor(pick, "whiteList")]),
                    blackList = ToList(pick == null ? null : pick[KeyFor(pick, "blackList")]),
                };

            foreach (var key in new[] { "whiteList", "blackList" })
            {
                if (!patch.ContainsKey(key)) continue;
                var items = ReadPickList(patch, key);
                pick[KeyFor(pick, key)] = new JsonArray(items.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
            }
            return new { ok = true, backup = BgiStore.WriteGroup(root, name, group, backupDir) };

            static string KeyFor(JsonObject node, string key) =>
                node.Select(entry => entry.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;

            static JsonObject FindObject(JsonObject parent, string name, bool create)
            {
                var key = KeyFor(parent, name);
                if (parent[key] is JsonObject value) return value;
                if (parent[key] != null) throw new InvalidOperationException(name + " 格式无效");
                if (!create) return null;
                value = new JsonObject();
                parent[key] = value;
                return value;
            }
        }

        private static List<string> ReadPickList(JsonObject body, string key)
        {
            if (!body.ContainsKey(key)) return null;
            if (body[key] is not JsonArray entries ||
                entries.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _)))
                throw new ArgumentException(key + " 必须是字符串数组");
            return ToList(entries);
        }

        private static bool SavePickLists(HttpListenerContext ctx, string root, JsonObject body)
        {
            lock (PickWriteLock)
            {
                var model = new BgiTemplatePick.Model
                {
                    BlackList = ReadList("blackList"), FuzzyBlackList = ReadList("fuzzyBlackList"),
                    BlackAllowList = ReadList("blackModeAllowList"), WhiteAllowList = ReadList("whiteModeAllowList"),
                    WhiteExcludeList = ReadList("whiteModeExcludeList"), WhiteFuzzyExcludeList = ReadList("whiteModeFuzzyExcludeList"),
                    TemplateBlackList = ReadList("templateBlackList"),
                };
                var nested = body["autoPickConfig"] as JsonObject;
                foreach (var key in new[] { "enabled", "mode", "itemIconLeftOffset", "itemTextLeftOffset", "itemTextRightOffset", "ocrEngine", "fastModeEnabled", "pickKey", "mojangMatchEnabled", "testModeEnabled", "autoScreenshotStreak", "mojangMatchThreshold", "pickLogLevel", "interactionDelay", "repeatFInterval", "confirmInterval", "scrollDistance", "scrollInterval", "scrollAfterInterval", "blacklistModePickEnabled", "whitelistModeDoNotPickEnabled" })
                {
                    if (body.ContainsKey(key)) model.AutoPickConfig[key] = body[key]?.DeepClone();
                    else if (nested?.ContainsKey(key) == true) model.AutoPickConfig[key] = nested[key]?.DeepClone();
                }
                var backup = BgiTemplatePick.Write(root, model);
                return Json(ctx, new { ok = true, backup, blackCount = model.BlackList?.Count, fuzzyCount = model.FuzzyBlackList?.Count, mode = model.Mode });
            }

            List<string> ReadList(string key) => ReadPickList(body, key);
        }

        /// <summary>把 JSON 数组转成字符串列表（去空、去重前的原始值）。</summary>
        private static System.Collections.Generic.List<string> ToList(JsonNode? node)
        {
            var list = new System.Collections.Generic.List<string>();
            if (node is not JsonArray arr) return list;
            foreach (var item in arr)
            {
                if (item == null) continue;
                string s = item is JsonValue v && v.TryGetValue<string>(out var sv) ? sv : item.ToJsonString();
                list.Add(s);
            }
            return list;
        }

        internal static bool SameOrigin(HttpListenerRequest req) => req.Url != null &&
            string.Equals(req.Headers["Origin"], req.Url.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

        private static string ReadBody(HttpListenerContext ctx)
        {
            using var reader = new StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8, false, 1024 * 1024, true);
            return reader.ReadToEnd();
        }

        private static bool Json(HttpListenerContext ctx, object value, int status = 200)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, BgiJson.Options));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
            return true;
        }
    }
}
