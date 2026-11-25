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

        internal static bool TryHandle(HttpListenerContext ctx, string path, string queryBgi, string configJsonPath)
        {
            // 只处理 /taskedit/api/*
            string p = path;
            if (!p.StartsWith("/taskedit/api", StringComparison.OrdinalIgnoreCase)) return false;

            var q = ctx.Request.QueryString;
            string account = q["account"] ?? "";
            var ctx2 = ResolveRoot(queryBgi, account, configJsonPath);

            // 去掉前缀 /taskedit/api
            string rest = p.Substring("/taskedit/api".Length);
            if (rest.Length == 0) rest = "/";

            string method = ctx.Request.HttpMethod;

            try
            {
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
                        string backup = BgiStore.WriteGroup(ctx2.Root, name, node, ctx2.BackupDir);
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
                    if (method == "POST")
                    {
                        string body = ReadBody(ctx);
                        var node = JsonNode.Parse(body) ?? new JsonObject();
                        var issues = BgiValidator.ValidateOneDragon(node);
                        issues.AddRange(BgiValidator.ValidateCapabilities(node, ctx2.Identity.Channel, ctx2.Identity.BaseVersion));
                        if (issues.Any(i => i.Blocking))
                            return Json(ctx, new { ok = false, errors = issues.Select(i => new { path = i.Path, message = i.Message, blocking = i.Blocking }) }, 422);
                        string backup = BgiStore.WriteOneDragon(ctx2.Root, name, node, ctx2.BackupDir);
                        return Json(ctx, new { ok = true, name, backup });
                    }
                    return false;
                }

                // ── 拾取名单（§16.1）：读写 config.json 的 autoPickConfig + 两个 txt ──
                if (rest.Equals("/picklists", StringComparison.OrdinalIgnoreCase))
                {
                    if (method == "GET")
                    {
                        var m = BgiPickList.Read(ctx2.Root);
                        if (m == null) return Json(ctx, new { ok = false, error = "BGI 根目录未确定" }, 400);
                        return Json(ctx, new
                        {
                            ok = true,
                            root = ctx2.Root,
                            enabled = m.Enabled,
                            mode = m.Mode,
                            blacklistModePickEnabled = m.BlacklistModePickEnabled,
                            whitelistModeDoNotPickEnabled = m.WhitelistModeDoNotPickEnabled,
                            blackList = m.BlackList,
                            fuzzyBlackList = m.FuzzyBlackList,
                            files = new
                            {
                                black = "User/" + "pick_black_lists.txt",
                                fuzzy = "User/" + "pick_fuzzy_black_lists.txt",
                                config = "User/config.json"
                            }
                        });
                    }
                    if (method == "POST")
                    {
                        string body = ReadBody(ctx);
                        JsonNode? node;
                        try { node = JsonNode.Parse(body); }
                        catch (Exception) { return Json(ctx, new { ok = false, error = "请求体不是合法 JSON" }, 400); }
                        if (node is not JsonObject jo) return Json(ctx, new { ok = false, error = "请求体必须是 JSON 对象" }, 400);

                        var model = new BgiPickList.Model
                        {
                            Enabled = jo["enabled"]?.GetValue<bool>() ?? false,
                            Mode = jo["mode"]?.GetValue<string>() ?? "Blacklist",
                            BlacklistModePickEnabled = jo["blacklistModePickEnabled"]?.GetValue<bool>() ?? false,
                            WhitelistModeDoNotPickEnabled = jo["whitelistModeDoNotPickEnabled"]?.GetValue<bool>() ?? false,
                            BlackList = ToList(jo["blackList"]),
                            FuzzyBlackList = ToList(jo["fuzzyBlackList"])
                        };
                        /* 把已有的 autoPickConfig 其余字段带过去（读一次，避免丢用户设置） */
                        var current = BgiPickList.Read(ctx2.Root);
                        if (current != null) model.OtherConfig = current.OtherConfig;

                        string backup = BgiPickList.Write(ctx2.Root, model, ctx2.BackupDir);
                        return Json(ctx, new { ok = true, backup, blackCount = model.BlackList.Count,
                            fuzzyCount = model.FuzzyBlackList.Count, mode = model.Mode });
                    }
                    return false;
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
