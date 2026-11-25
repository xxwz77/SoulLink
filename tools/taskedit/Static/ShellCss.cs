using System;

namespace SoulLink.TaskEdit
{
    // 任务编辑页样式（原生 CSS，无框架、无 CDN）。
    internal static partial class TaskEditStatic
    {
        private const string ShellCss = """
:root{
  --bg:#0f1115;--panel:#171a21;--panel2:#1c2029;--line:#262b36;--line2:#2c3240;
  --fg:#e6e8ee;--fg2:#a8b0c0;--fg3:#7d8798;--acc:#3b82f6;--acc2:#2f6fd8;
  --ok:#4ade80;--warn:#ffb454;--err:#ff7a7a;--tag:#1f2937;--tagfg:#93c5fd;
  --mono:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;
}
*{box-sizing:border-box}
html,body{height:100%}
body{margin:0;background:var(--bg);color:var(--fg);
  font:14px/1.6 -apple-system,'Segoe UI','Microsoft YaHei',sans-serif;overflow:hidden}

/* ── 顶栏 ── */
.top{display:flex;align-items:center;gap:10px;padding:8px 14px;background:var(--panel);
  border-bottom:1px solid var(--line);flex-wrap:wrap}
.brand{font-weight:600;letter-spacing:.3px}
.tabs{display:flex;gap:2px;background:#0d0f14;padding:2px;border-radius:9px}
.tab{padding:5px 14px;border-radius:7px;cursor:pointer;color:var(--fg2);font-size:13px;
  border:0;background:transparent}
.tab.on{background:var(--acc);color:#fff}
.tab:hover:not(.on){background:#1f2532;color:var(--fg)}
.spacer{flex:1}
.ver{color:var(--fg3);font-size:12px;font-family:var(--mono)}
.pill{padding:2px 9px;border-radius:999px;background:var(--tag);color:var(--tagfg);font-size:12px}

/* ── 按钮 ── */
.btn{padding:5px 12px;border-radius:8px;border:1px solid var(--line2);background:var(--panel2);
  color:var(--fg);cursor:pointer;font-size:13px;white-space:nowrap}
.btn:hover{background:#242a36;border-color:#3a4252}
.btn:disabled{opacity:.45;cursor:not-allowed}
.btn.pri{background:var(--acc);border-color:var(--acc);color:#fff}
.btn.pri:hover{background:var(--acc2)}
.btn.danger{background:#3a1d1d;border-color:#5a2a2a;color:#ffb3b3}
.btn.danger:hover{background:#4a2424}
.btn.sm{padding:3px 9px;font-size:12px}

/* ── 主体：左树 + 右画布 ── */
.main{display:flex;height:calc(100vh - 49px);overflow:hidden}
.side{width:260px;min-width:180px;background:var(--panel);border-right:1px solid var(--line);
  display:flex;flex-direction:column;overflow:hidden;flex-shrink:0;resize:horizontal}
.side-head{padding:9px 12px;border-bottom:1px solid var(--line);font-size:12px;color:var(--fg3);
  display:flex;align-items:center;gap:8px}
.tree{flex:1;overflow:auto;padding:6px 4px}
.node{padding:5px 9px;border-radius:7px;cursor:pointer;font-size:13px;color:var(--fg2);
  display:flex;align-items:center;gap:6px;user-select:none;white-space:nowrap;
  overflow:hidden;text-overflow:ellipsis}
.node:hover{background:#1f2532;color:var(--fg)}
.node.on{background:#1e3a5f;color:#cfe4ff}
.node.root{font-weight:600;color:var(--fg);margin-top:4px}
.node .cnt{color:var(--fg3);font-size:11px;margin-left:auto}
.node .caret{width:12px;display:inline-block;text-align:center;color:var(--fg3);font-size:10px}
.node.children{display:none}
.node.children.open{display:block}
.node.child{padding-left:26px}
.hint{padding:8px 12px;color:var(--fg3);font-size:12px;line-height:1.7}

/* ── 右画布 ── */
.canvas{flex:1;overflow:auto;padding:16px 20px}
.canvas-empty{color:var(--fg3);text-align:center;padding:80px 20px;line-height:2}
.head{display:flex;align-items:center;gap:10px;margin-bottom:14px;flex-wrap:wrap}
.head h2{margin:0;font-size:17px;font-weight:600}
.head .meta{color:var(--fg3);font-size:12px;font-family:var(--mono)}

/* ── 表格 ── */
table.grid{width:100%;border-collapse:collapse;font-size:13px}
table.grid th{text-align:left;padding:7px 9px;color:var(--fg3);font-weight:500;font-size:12px;
  border-bottom:1px solid var(--line);position:sticky;top:0;background:var(--panel2);z-index:1}
table.grid td{padding:6px 9px;border-bottom:1px solid #1d2129;vertical-align:middle}
table.grid tr:hover td{background:#1a1e26}
table.grid tr.dragging{opacity:.4}
table.grid tr.drop-before td{border-top:2px solid var(--acc)}
table.grid tr.drop-after td{border-bottom:2px solid var(--acc)}
.idx{color:var(--fg3);font-family:var(--mono);font-size:12px;width:38px}
.grip{cursor:grab;color:var(--fg3);user-select:none;width:22px}
.grip:active{cursor:grabbing}
.mono{font-family:var(--mono);font-size:12px;color:var(--fg2)}
.tag{display:inline-block;padding:1px 8px;border-radius:999px;background:var(--tag);
  color:var(--tagfg);font-size:11px;font-family:var(--mono)}
.tag.js{background:#1e3a2f;color:#86efac}
.tag.pathing{background:#2a2a1e;color:#fde68a}
.tag.solo{background:#2a1e3a;color:#d8b4fe}
.tag.shell{background:#3a2a1e;color:#fdba74}
.tag.keymouse{background:#1e2a3a;color:#93c5fd}

/* ── 开关 ── */
.sw{position:relative;display:inline-block;width:36px;height:20px;flex-shrink:0}
.sw input{opacity:0;width:0;height:0}
.sw i{position:absolute;inset:0;background:#2c3240;border-radius:999px;transition:.16s;cursor:pointer}
.sw i:before{content:"";position:absolute;width:14px;height:14px;left:3px;top:3px;
  background:#8b93a3;border-radius:50%;transition:.16s}
.sw input:checked + i{background:var(--acc)}
.sw input:checked + i:before{transform:translateX(16px);background:#fff}
.sw input:disabled + i{opacity:.4;cursor:not-allowed}

/* ── 表单 ── */
.field{display:flex;align-items:flex-start;gap:10px;padding:6px 0;border-bottom:1px solid #1a1e26}
.field label{flex:0 0 200px;color:var(--fg2);font-size:13px;padding-top:4px;word-break:break-word}
.field .ctl{flex:1;min-width:0}
.field .desc{color:var(--fg3);font-size:11px;margin-top:2px}
input[type=text],input[type=number],select,textarea{
  width:100%;max-width:520px;padding:5px 9px;border-radius:7px;border:1px solid var(--line2);
  background:#0d0f14;color:var(--fg);font-size:13px;font-family:inherit}
input[type=number]{max-width:140px}
textarea{font-family:var(--mono);font-size:12px;min-height:64px;resize:vertical;max-width:640px}
input:focus,select:focus,textarea:focus{outline:0;border-color:var(--acc)}
input.bad,select.bad,textarea.bad{border-color:var(--err);background:#2a1618}
.err-tip{color:var(--err);font-size:11px;margin-top:3px}

/* ── 折叠卡片 ── */
.card{border:1px solid var(--line);border-radius:11px;margin-bottom:10px;background:var(--panel);overflow:hidden}
.card > summary{padding:9px 13px;cursor:pointer;font-weight:600;font-size:13px;
  background:var(--panel2);display:flex;align-items:center;gap:8px;user-select:none;list-style:none}
.card > summary::-webkit-details-marker{display:none}
.card > summary:before{content:"▸";color:var(--fg3);font-size:11px;transition:.15s;display:inline-block}
.card[open] > summary:before{transform:rotate(90deg)}
.card > summary:hover{background:#202634}
.card .body{padding:10px 14px}
.card .badge{margin-left:auto}

/* ── 一周表格 ── */
table.week{width:100%;border-collapse:collapse;font-size:13px}
table.week th{text-align:center;padding:6px 4px;color:var(--fg3);font-size:12px;font-weight:500;
  border-bottom:1px solid var(--line)}
table.week td{padding:3px 4px;border-bottom:1px solid #1a1e26}
table.week td.rowhead{color:var(--fg2);font-size:12px;padding-right:9px;white-space:nowrap}
table.week input[type=number]{max-width:74px;width:100%;padding:3px 6px;font-size:12px;text-align:center}
table.week select{max-width:110px;width:100%;padding:3px 5px;font-size:12px}
table.week td.on-hover{background:#1a1e26}
table.week th.weekend{color:#f0a5a5}

/* ── 右键菜单 ── */
.menu{position:fixed;z-index:9000;min-width:190px;background:#1b1f28;border:1px solid var(--line2);
  border-radius:9px;padding:4px;box-shadow:0 8px 28px rgba(0,0,0,.55);display:none}
.menu.open{display:block}
.menu .item{padding:6px 11px;border-radius:6px;font-size:13px;cursor:pointer;color:var(--fg2);
  display:flex;align-items:center;gap:8px;white-space:nowrap}
.menu .item:hover{background:#2a3242;color:var(--fg)}
.menu .item.danger:hover{background:#4a2424;color:#ffb3b3}
.menu .sep{height:1px;background:var(--line);margin:4px 2px}
.menu .hdr{padding:5px 11px 3px;font-size:11px;color:var(--fg3)}

/* ── 弹窗 ── */
.mask{position:fixed;inset:0;background:rgba(0,0,0,.55);z-index:9500;display:none;
  align-items:center;justify-content:center;padding:20px}
.mask.open{display:flex}
.modal{background:var(--panel);border:1px solid var(--line2);border-radius:13px;width:100%;
  max-width:720px;max-height:86vh;display:flex;flex-direction:column;overflow:hidden;
  box-shadow:0 16px 48px rgba(0,0,0,.6)}
.modal > .mh{padding:13px 17px;border-bottom:1px solid var(--line);font-weight:600;
  display:flex;align-items:center;gap:9px}
.modal > .mb{padding:15px 17px;overflow:auto;flex:1}
.modal > .mf{padding:11px 17px;border-top:1px solid var(--line);display:flex;gap:9px;
  justify-content:flex-end;background:var(--panel2)}
.pick{max-height:340px;overflow:auto;border:1px solid var(--line);border-radius:9px}
.pick .row{padding:6px 11px;cursor:pointer;font-size:13px;color:var(--fg2);
  display:flex;align-items:center;gap:8px;border-bottom:1px solid #1a1e26}
.pick .row:hover{background:#1f2532;color:var(--fg)}
.pick .row.sel{background:#1e3a5f;color:#cfe4ff}
.pick .empty{padding:22px;text-align:center;color:var(--fg3);font-size:13px}

/* ── 提示条 ── */
.toast{position:fixed;left:50%;bottom:28px;transform:translateX(-50%);z-index:9800;
  background:#222834;border:1px solid var(--line2);color:var(--fg);padding:9px 18px;
  border-radius:10px;font-size:13px;box-shadow:0 8px 28px rgba(0,0,0,.5);display:none}
.toast.open{display:block}
.toast.ok{border-color:#25623f;background:#16321f}
.toast.err{border-color:#5a2a2a;background:#331a1a}
.banner{padding:9px 13px;border-radius:9px;font-size:13px;margin-bottom:12px;
  border:1px solid;display:flex;align-items:center;gap:9px}
.banner.warn{background:#332a13;border-color:#5c4a1a;color:#ffd699}
.banner.err{background:#331a1a;border-color:#5a2a2a;color:#ffb3b3}
.banner.info{background:#132a3a;border-color:#1a4a5c;color:#a5dcf0}

/* ── 加载/忙碌 ── */
.busy{display:inline-block;width:13px;height:13px;border:2px solid #3a4252;
  border-top-color:var(--acc);border-radius:50%;animation:spin .7s linear infinite;vertical-align:-2px}
@keyframes spin{to{transform:rotate(360deg)}}
.loading{color:var(--fg3);padding:26px;text-align:center;font-size:13px}
.row{display:flex;gap:10px;align-items:center;flex-wrap:wrap}
.grow{flex:1}
@media(max-width:760px){
  .side{width:180px}
  .canvas{padding:12px 10px}
  .field{flex-direction:column;gap:4px}
  .field label{flex:none;padding-top:0}
}
""";
    }
}
