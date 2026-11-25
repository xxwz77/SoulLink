using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 任务编辑页的前端资源（CSS / JS）。
    ///
    /// ── 为什么把资源编译进程序集，而不是放磁盘上的静态文件 ──────────────
    ///   ① 单文件发布（PublishSingleFile）下，磁盘静态文件需要额外的
    ///      `CopyToPublishDirectory` 配置，且**路径容易出错**（发布目录 / 工作目录不一致）；
    ///   ② 用户要求「离线可用」—— 资源必须与 exe 同生共死，不能出现「exe 在但 js 丢了」；
    ///   ③ 本页面的所有资源都受同一个鉴权闸门保护，不需要额外的文件系统暴露面。
    ///   代价是改 CSS 要改 C# 字符串；用**原始字符串字面量**（`"""`）已经足够可读。
    ///
    /// ── 不引外部 CDN / 不引前端框架 ──────────────────────────────────
    ///   硬要求：离线可用。所有逻辑都是原生 JS + 原生 CSS。
    ///
    /// ── 文件切分（每份 ≤300 行）────────────────────────────────────────
    ///   本文件只做**路由与拼装**；具体内容在 <see cref="Assets"/> 的各个 partial 文件里。
    /// </summary>
    internal static partial class TaskEditStatic
    {
        internal sealed record Asset(string ContentType, Func<string> Content);

        /// <summary>路径 → 资源。键是 <c>/taskedit/static/</c> 之后的部分。</summary>
        private static readonly Dictionary<string, Asset> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["shell.css"] = new("text/css; charset=utf-8", () => ShellCss),
            ["core.js"] = new("application/javascript; charset=utf-8", () => CoreJs),
            ["tree.js"] = new("application/javascript; charset=utf-8", () => TreeJs),
            ["group.js"] = new("application/javascript; charset=utf-8", () => GroupJs),
            ["grouptable.js"] = new("application/javascript; charset=utf-8", () => GroupTableJs),
            ["groupschema.js"] = new("application/javascript; charset=utf-8", () => GroupSchemaJs),
            ["groupsettings.js"] = new("application/javascript; charset=utf-8", () => GroupSettingsJs),
            ["groupops.js"] = new("application/javascript; charset=utf-8", () => GroupOpsJs),
            ["dragon.js"] = new("application/javascript; charset=utf-8", () => DragonJs),
            ["dragonsections.js"] = new("application/javascript; charset=utf-8", () => DragonSectionsJs),
            ["dragonweek.js"] = new("application/javascript; charset=utf-8", () => DragonWeekJs),
            ["dragonbind.js"] = new("application/javascript; charset=utf-8", () => DragonBindJs),
            ["jssettings.js"] = new("application/javascript; charset=utf-8", () => JsSettingsJs),
            ["pick.js"] = new("application/javascript; charset=utf-8", () => PickJs),
            ["app.js"] = new("application/javascript; charset=utf-8", () => AppJs),
        };

        /// <summary>页面里引用的资源清单（顺序敏感：core 必须在最前）。</summary>
        internal static readonly string[] Scripts =
            { "core.js", "tree.js", "group.js", "grouptable.js", "groupschema.js", "groupsettings.js", "groupops.js",
              "dragon.js", "dragonsections.js", "dragonweek.js", "dragonbind.js",
              "jssettings.js", "pick.js", "app.js" };

        internal static string StylesheetHref => "/taskedit/static/shell.css";

        /// <summary>是否为静态资源路径。</summary>
        internal static bool IsStaticPath(string path)
            => path.StartsWith("/taskedit/static/", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 提供静态资源。走 gzip（都是文本，且体积可压）。
        /// 返回 false = 未知资源（调用方回 404）。
        /// </summary>
        internal static bool TryServe(HttpListenerContext ctx, string path)
        {
            string name = path.Substring("/taskedit/static/".Length).Trim('/');
            if (name.Length == 0 || !Map.TryGetValue(name, out var asset)) return false;

            byte[] plain = Encoding.UTF8.GetBytes(asset.Content() ?? "");
            byte[] body = plain;
            bool gzip = false;

            string ae = ctx.Request.Headers["Accept-Encoding"] ?? "";
            if (Compression.ShouldCompress(ae, asset.ContentType, "", plain.Length, false))
            {
                body = Compression.Gzip(plain);
                gzip = true;
            }

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = asset.ContentType;
            ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
            if (gzip)
            {
                ctx.Response.Headers["Content-Encoding"] = "gzip";
                ctx.Response.Headers["Vary"] = "Accept-Encoding";
            }
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.Close();
            return true;
        }
    }
}
