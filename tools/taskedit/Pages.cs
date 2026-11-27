using System;
using System.Net;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 本服务自处理的几个页面（P0 只有登录页与占位页）。
    ///
    /// 单独成文件的理由：HTML/CSS 是**内容**不是逻辑，混在请求处理里会让
    /// <see cref="TaskEditServer"/> 超过单文件行数预算，也让改样式必须读路由代码。
    /// </summary>
    internal static class Pages
    {
        private const string Shell =
            "body{margin:0;font:15px/1.7 -apple-system,'Segoe UI','Microsoft YaHei',sans-serif;" +
            "background:#0f1115;color:#e6e8ee;display:flex;min-height:100vh;align-items:center;justify-content:center}";

        /// <summary>登录页（<paramref name="error"/> 非空时显示错误提示）。</summary>
        internal static string Login(string error)
            => "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
               "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
               "<title>SoulLink · 访问口令</title>" + Services.WebFavicon.Link + "<style>" + Shell +
               ".card{width:340px;padding:28px 30px;background:#171a21;border:1px solid #262b36;border-radius:14px}" +
               "h1{margin:0 0 16px;font-size:18px}input{width:100%;box-sizing:border-box;padding:10px 12px;" +
               "border-radius:9px;border:1px solid #2c3240;background:#0d0f14;color:#e6e8ee;font-size:14px}" +
               "button{margin-top:14px;width:100%;padding:10px;border-radius:9px;border:0;background:#3b82f6;" +
               "color:#fff;font-size:14px;cursor:pointer}button:hover{background:#2f6fd8}" +
               ".err{margin:0 0 12px;color:#ff8080;font-size:13px}" +
               ".hint{margin:12px 0 0;color:#7d8798;font-size:12px}" +
               "</style></head><body><form class=\"card\" method=\"POST\" action=\"/taskedit/login\">" +
               "<h1>SoulLink 远程访问</h1>" +
               (string.IsNullOrEmpty(error) ? "" : "<p class=\"err\">" + WebUtility.HtmlEncode(error) + "</p>") +
               "<input type=\"password\" name=\"pw\" placeholder=\"请输入访问口令\" autofocus " +
               "autocomplete=\"current-password\"><button type=\"submit\">进入</button>" +
               "<p class=\"hint\">口令即主程序设置里的「网页口令」。本机（127.0.0.1）访问无需口令。</p>" +
               "</form></body></html>";

        /// <summary>/taskedit/ 占位页（P1 才做真界面）。</summary>
        internal static string Placeholder()
            => "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
               "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
               "<title>SoulLink · 任务编辑</title>" + Services.WebFavicon.Link + "<style>" + Shell +
               ".card{max-width:600px;padding:30px 34px;background:#171a21;border:1px solid #262b36;border-radius:14px}" +
               "h1{margin:0 0 14px;font-size:19px}p{margin:9px 0;color:#a8b0c0}" +
               "code{background:#0d0f14;padding:2px 6px;border-radius:5px;color:#8ad1ff;font-size:13px}" +
               "ul{margin:10px 0 0 18px;color:#a8b0c0}li{margin:4px 0}" +
               ".tag{display:inline-block;padding:1px 8px;border-radius:999px;background:#1f2937;" +
               "color:#93c5fd;font-size:12px;margin-left:8px}" +
               "a{color:#8ad1ff;text-decoration:none}a:hover{text-decoration:underline}" +
               "</style></head><body><div class=\"card\">" +
               "<h1>任务编辑 <span class=\"tag\">P0 骨架</span></h1>" +
               "<p>这个入口已由独立服务 <code>SoulLink.TaskEdit</code> 接管：</p>" +
               "<ul><li><b>一个端口</b>访问整个网页（数据中心 / 米游社 / 任务编辑都在这里）</li>" +
               "<li>其余页面与接口<b>反向代理</b>到主程序回环端口</li>" +
               "<li>非本机访问<b>强制口令</b>；文本响应已启用 <b>gzip</b></li></ul>" +
               "<p>编辑界面与本地配置读写属于 P1，本版本只提供通道与鉴权。</p>" +
               "<p>返回现有页面：<a href=\"/\">首页</a>　" +
               "健康检查：<a href=\"/taskedit/health\">/taskedit/health</a></p>" +
               "</div></body></html>";

        /// <summary>「主程序未运行」页（反代降级）。</summary>
        internal static string BackendDown(string detail)
            => "<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">" +
               "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
               "<title>SoulLink · 主程序未运行</title>" + Services.WebFavicon.Link + "<style>" + Shell +
               ".card{max-width:560px;padding:28px 32px;background:#171a21;border:1px solid #262b36;border-radius:14px}" +
               "h1{margin:0 0 12px;font-size:19px;color:#ffb454}p{margin:8px 0;color:#a8b0c0}" +
               "code{background:#0d0f14;padding:2px 6px;border-radius:5px;color:#8ad1ff;font-size:13px}" +
               "ul{margin:10px 0 0 18px;padding:0;color:#a8b0c0}li{margin:4px 0}" +
               "</style></head><body><div class=\"card\">" +
               "<h1>主程序未运行</h1>" +
               "<p>网页入口（本服务）在正常工作，但后面的 <b>SoulLink 主程序</b>没有响应。</p>" +
               "<p>说明：" + WebUtility.HtmlEncode(detail) + "</p>" +
               "<ul><li>请确认主程序 <code>SoulLink.exe</code> 已启动且未退出</li>" +
               "<li>如果主程序刚启动，稍等几秒刷新本页</li>" +
               "<li>如果反复出现，请查看主程序的 error.log</li></ul>" +
               "</div></body></html>";
    }
}
