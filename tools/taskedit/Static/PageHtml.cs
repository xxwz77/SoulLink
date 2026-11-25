using System;
using System.Text;

namespace SoulLink.TaskEdit
{
    // 任务编辑页 HTML 外壳（按 §3.1 左树 + 右画布布局）。
    internal static partial class TaskEditStatic
    {
        internal static string Page()
        {
            var sb = new StringBuilder(4096);
            sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>SoulLink · 任务编辑</title>");
            sb.Append("<link rel=\"stylesheet\" href=\"").Append(StylesheetHref).Append("\">");
            sb.Append("</head><body>");

            /* ── 顶栏 ── */
            sb.Append("<div class=\"top\">");
            sb.Append("<span class=\"brand\">SoulLink</span>");
            sb.Append("<span class=\"pill\">任务编辑</span>");
            sb.Append("<div class=\"tabs\">");
            sb.Append("<button class=\"tab on\" data-tab=\"edit\">配置组 / 一条龙</button>");
            sb.Append("<button class=\"tab\" data-tab=\"pick\">拾取名单</button>");
            sb.Append("</div>");
            sb.Append("<span class=\"spacer\"></span>");
            sb.Append("<span class=\"ver\" id=\"verBar\">…</span>");
            sb.Append("<button class=\"btn\" id=\"reloadBtn\">重新加载</button>");
            sb.Append("<button class=\"btn\" id=\"saveBtn\" disabled>保存</button>");
            sb.Append("</div>");

            /* ── 主体 ── */
            sb.Append("<div class=\"main\">");
            sb.Append("<div class=\"side\" id=\"side\">");
            sb.Append("<div class=\"side-head\">对象<span class=\"spacer\" style=\"flex:1\"></span>");
            sb.Append("<button class=\"btn sm\" id=\"sideRefresh\" title=\"刷新列表\">⟳</button></div>");
            sb.Append("<div class=\"tree\" id=\"tree\"><div class=\"hint\">加载中…</div></div>");
            sb.Append("</div>");
            sb.Append("<div class=\"canvas\" id=\"canvas\">");
            sb.Append("<div class=\"canvas-empty\">加载中…</div>");
            sb.Append("</div>");
            sb.Append("</div>");

            /* ── 能力集告警条（默认隐藏，由 app.js 按渠道填充）── */
            sb.Append("<div id=\"capBanner\" style=\"display:none;position:fixed;bottom:14px;right:14px;");
            sb.Append("max-width:420px;z-index:8000\"></div>");

            /* ── 右键菜单 / 弹窗 / toast ── */
            sb.Append("<div class=\"menu\" id=\"ctxMenu\"></div>");
            sb.Append("<div class=\"mask\" id=\"modalMask\"><div class=\"modal\" id=\"modalBox\">");
            sb.Append("<div class=\"mh\" id=\"modalTitle\"></div>");
            sb.Append("<div class=\"mb\" id=\"modalBody\"></div>");
            sb.Append("<div class=\"mf\"><button class=\"btn\" id=\"modalCancel\">取消</button>");
            sb.Append("<button class=\"btn pri\" id=\"modalOk\">确定</button></div>");
            sb.Append("</div></div>");
            sb.Append("<div class=\"toast\" id=\"toast\"></div>");

            /* ── 脚本（顺序敏感：core 最先，app 最后）── */
            foreach (string js in Scripts)
                sb.Append("<script src=\"/taskedit/static/").Append(js).Append("\"></script>");

            /* 侧栏刷新按钮（内联，避免为一行代码再开一个文件） */
            sb.Append("<script>document.addEventListener('DOMContentLoaded',function(){");
            sb.Append("var b=document.getElementById('sideRefresh');");
            sb.Append("if(b)b.onclick=function(){TE.tree.load(true);};});</script>");

            sb.Append("</body></html>");
            return sb.ToString();
        }
    }
}
