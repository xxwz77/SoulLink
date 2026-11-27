using System;

namespace SoulLink.TaskEdit
{
    // 任务编辑页样式（原生 CSS，无框架、无 CDN）。
    internal static partial class TaskEditStatic
    {
        private const string ShellCss = """
:root{
  color-scheme:dark;--bg:#05060c;--panel:#121a2e;--panel2:#0e1526;--line:#25314b;--line2:#3b4c6e;
  --fg:#f2f5ff;--fg2:rgba(194,232,255,.78);--fg3:#a5abcd;--acc:#4fe3ff;--acc2:#8b6bff;--hover:#182236;
  --ok:#41f0c0;--warn:#ffd699;--err:#ff6b8b;--tag:#1a3050;--tagfg:var(--acc);--okbg:#163328;--errbg:#341f28;--warnbg:#332a13;
  --mono:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;
}
:root[data-theme="light"]{
  color-scheme:light;--bg:#faf2fc;--panel:#fff;--panel2:#fdf9ff;--line:#e2d6f0;--line2:#c9b4e4;
  --fg:#30254c;--fg2:#392b56;--fg3:#5c4c78;--acc:#0087a7;--acc2:#7649c4;--hover:#f7f0ff;
  --ok:#008869;--warn:#77510d;--err:#c02753;--tag:#e0f4fa;--okbg:#dff7ef;--errbg:#fbe4eb;--warnbg:#fff2cd;
}
*{box-sizing:border-box}
html,body{height:100%}
body{margin:0;background:var(--bg);color:var(--fg);
  font:14px/1.6 -apple-system,'Segoe UI','Microsoft YaHei',sans-serif;overflow:hidden}

/* ── 顶栏 ── */
.top{display:flex;align-items:center;gap:10px;padding:8px 14px;background:var(--panel);
  border-bottom:1px solid var(--line);flex-wrap:wrap}
.brand{font-weight:600;letter-spacing:.3px}
.tabs{display:flex;gap:2px;background:var(--panel2);padding:2px;border-radius:9px}
.tab{padding:5px 14px;border-radius:7px;cursor:pointer;color:var(--fg2);font-size:13px;
  border:0;background:transparent}
.tab.on{background:var(--acc);color:var(--bg)}
.tab:hover:not(.on){background:var(--hover);color:var(--fg)}
.spacer{flex:1}
.ver{color:var(--fg3);font-size:12px;font-family:var(--mono)}
.pill{padding:2px 9px;border-radius:999px;background:var(--tag);color:var(--tagfg);font-size:12px}

/* ── 按钮 ── */
.btn{padding:5px 12px;border-radius:8px;border:1px solid var(--line2);background:var(--panel2);
  color:var(--fg);cursor:pointer;font-size:13px;white-space:nowrap}
.btn:hover{background:var(--hover);border-color:var(--line2)}
.btn:disabled{opacity:.45;cursor:not-allowed}
.btn.pri,.btn[data-template-mode][aria-pressed="true"]{background:var(--acc);border-color:var(--acc);color:var(--bg)}
.btn.pri:hover{background:var(--acc2)}
.btn.danger{background:var(--errbg);border-color:var(--err);color:var(--err)}
.btn.danger:hover{background:var(--errbg)}
.btn.sm{padding:3px 9px;font-size:12px}

/* ── 主体：左树 + 右画布 ── */
.main{display:flex;flex-direction:column;flex:1;min-height:0;width:100%;max-width:1600px;margin:auto;overflow:hidden}
body{height:100dvh;display:flex;flex-direction:column;background-image:radial-gradient(ellipse at 8% 0%,color-mix(in srgb,var(--acc2) 23%,transparent),transparent 55%),radial-gradient(ellipse at 100% 100%,color-mix(in srgb,var(--acc) 15%,transparent),transparent 58%)}
#objectPicker{margin:12px 20px 0;padding:7px 12px;background:var(--panel);border:1px solid var(--line);border-radius:10px;max-height:38vh;overflow:auto}
#objectPicker summary{cursor:pointer;color:var(--fg2);font-weight:600}
.object-grid,.group-fields{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,225px),1fr));gap:6px 12px}
.object-grid{padding:9px 0 3px}.object-option{border:1px solid var(--line);border-radius:7px;background:var(--panel2);color:var(--fg);text-align:left;padding:7px 10px;cursor:pointer;overflow-wrap:anywhere}
.object-option:hover,.object-option.on,.object-option[aria-current=true]{background:var(--tag);color:var(--tagfg);border-color:var(--acc)}
.native-tools{display:flex;align-items:center;gap:8px;flex-wrap:wrap;margin-bottom:9px;min-height:32px}.native-tools h2{font-size:16px;margin:0 auto 0 0}.config-choice{overflow:hidden;text-overflow:ellipsis;max-width:100%}
.project-scroll,.task-list{border:1px solid var(--line);background:var(--panel);border-radius:10px;margin-bottom:10px}.task-list td{overflow-wrap:anywhere}.group-fields .field{display:grid;grid-template-columns:minmax(5em,1fr) minmax(0,1fr);align-items:center}.group-fields .field>label{padding:0}
#accountSelect{width:auto;max-width:12em}.top{flex-shrink:0}.dragon-layout>section{min-width:0}
:focus-visible{outline:2px solid var(--acc);outline-offset:2px}.sw input:focus-visible+i{outline:2px solid var(--acc);outline-offset:2px}
[hidden]{display:none!important}
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
table.grid td{padding:6px 9px;border-bottom:1px solid var(--line);vertical-align:middle}
table.grid tr:hover td{background:var(--hover)}
table.grid tr.dragging{opacity:.4}
table.grid tr.drop-before td{border-top:2px solid var(--acc)}
table.grid tr.drop-after td{border-bottom:2px solid var(--acc)}
.idx{color:var(--fg3);font-family:var(--mono);font-size:12px;width:38px}
.grip{cursor:grab;color:var(--fg3);user-select:none;width:22px}
.grip:active{cursor:grabbing}
.mono{font-family:var(--mono);font-size:12px;color:var(--fg2)}
.tag{display:inline-block;padding:1px 8px;border-radius:999px;background:var(--tag);
  color:var(--tagfg);font-size:11px;font-family:var(--mono)}
.tag.js{background:var(--okbg);color:var(--ok)}
.tag.pathing{background:var(--warnbg);color:var(--warn)}
.tag.solo{background:var(--panel);color:var(--acc2)}
.tag.shell{background:var(--warnbg);color:var(--warn)}
.tag.keymouse{background:var(--tag);color:var(--tagfg)}

/* ── 开关 ── */
.sw{position:relative;display:inline-block;width:36px;height:20px;flex-shrink:0}
.sw input{opacity:0;width:0;height:0}
.sw i{position:absolute;inset:0;background:var(--line2);border-radius:999px;transition:.16s;cursor:pointer}
.sw i:before{content:"";position:absolute;width:14px;height:14px;left:3px;top:3px;
  background:var(--fg3);border-radius:50%;transition:.16s}
.sw input:checked + i{background:var(--acc)}
.sw input:checked + i:before{transform:translateX(16px);background:var(--bg)}
.sw input:disabled + i{opacity:.4;cursor:not-allowed}

/* ── 表单 ── */
.field{display:flex;align-items:flex-start;gap:10px;padding:6px 0;border-bottom:1px solid var(--hover)}
.field label{flex:0 0 200px;color:var(--fg2);font-size:13px;padding-top:4px;word-break:break-word}
.field .ctl{flex:1;min-width:0}
.field .desc{color:var(--fg3);font-size:11px;margin-top:2px}
input[type=text],input[type=number],select,textarea{
  width:100%;max-width:520px;padding:5px 9px;border-radius:7px;border:1px solid var(--line2);
  background:var(--panel2);color:var(--fg);font-size:13px;font-family:inherit}
input[type=number]{max-width:140px}
textarea{font-family:var(--mono);font-size:12px;min-height:64px;resize:vertical;max-width:640px}
input:focus,select:focus,textarea:focus{outline:0;border-color:var(--acc)}
input.bad,select.bad,textarea.bad{border-color:var(--err);background:var(--errbg)}
.err-tip{color:var(--err);font-size:11px;margin-top:3px}

/* ── 折叠卡片 ── */
.card{border:1px solid var(--line);border-radius:11px;margin-bottom:10px;background:var(--panel);overflow:hidden}
.card > summary{padding:9px 13px;cursor:pointer;font-weight:600;font-size:13px;
  background:var(--panel2);display:flex;align-items:center;gap:8px;user-select:none;list-style:none}
.card > summary::-webkit-details-marker{display:none}
.card > summary:before{content:"▸";color:var(--fg3);font-size:11px;transition:.15s;display:inline-block}
.card[open] > summary:before{transform:rotate(90deg)}
.card > summary:hover{background:var(--hover)}
.card .body{padding:10px 14px}
.card .badge{margin-left:auto}

/* ── 一周表格 ── */
table.week{width:100%;border-collapse:collapse;font-size:13px}
table.week th{text-align:center;padding:6px 4px;color:var(--fg3);font-size:12px;font-weight:500;
  border-bottom:1px solid var(--line)}
table.week td{padding:3px 4px;border-bottom:1px solid var(--hover)}
table.week td.rowhead{color:var(--fg2);font-size:12px;padding-right:9px;white-space:nowrap}
table.week input[type=number]{max-width:74px;width:100%;padding:3px 6px;font-size:12px;text-align:center}
table.week select{max-width:110px;width:100%;padding:3px 5px;font-size:12px}
table.week td.on-hover{background:var(--hover)}
table.week th.weekend{color:var(--err)}

/* ── 右键菜单 ── */
.menu{position:fixed;z-index:9000;min-width:190px;background:var(--panel2);border:1px solid var(--line2);
  border-radius:9px;padding:4px;box-shadow:0 8px 28px rgba(0,0,0,.55);display:none}
.menu.open{display:block}
.menu .item{padding:6px 11px;border-radius:6px;font-size:13px;cursor:pointer;color:var(--fg2);
  display:flex;align-items:center;gap:8px;white-space:nowrap}
.menu .item:hover{background:var(--hover);color:var(--fg)}
.menu .item.danger:hover{background:var(--errbg);color:var(--err)}
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
  display:flex;align-items:center;gap:8px;border-bottom:1px solid var(--hover)}
.pick .row:hover{background:var(--hover);color:var(--fg)}
.pick .row.sel{background:var(--tag);color:var(--tagfg)}
.pick .empty{padding:22px;text-align:center;color:var(--fg3);font-size:13px}

/* ── 提示条 ── */
.toast{position:fixed;left:50%;bottom:28px;transform:translateX(-50%);z-index:9800;
  background:var(--panel2);border:1px solid var(--line2);color:var(--fg);padding:9px 18px;
  border-radius:10px;font-size:13px;box-shadow:0 8px 28px rgba(0,0,0,.5);display:none}
.toast.open{display:block}
.toast.ok{border-color:var(--ok);background:var(--okbg)}
.toast.err{border-color:var(--err);background:var(--errbg)}
.banner{padding:9px 13px;border-radius:9px;font-size:13px;margin-bottom:12px;
  border:1px solid;display:flex;align-items:center;gap:9px}
.banner.warn{background:var(--warnbg);border-color:var(--warn);color:var(--warn)}
.banner.err{background:var(--errbg);border-color:var(--err);color:var(--err)}
.banner.info{background:var(--tag);border-color:var(--line);color:var(--tagfg)}

/* ── 加载/忙碌 ── */
.busy{display:inline-block;width:13px;height:13px;border:2px solid var(--line2);
  border-top-color:var(--acc);border-radius:50%;animation:spin .7s linear infinite;vertical-align:-2px}
@keyframes spin{to{transform:rotate(360deg)}}
.loading{color:var(--fg3);padding:26px;text-align:center;font-size:13px}
.row{display:flex;gap:10px;align-items:center;flex-wrap:wrap}
.grow{flex:1}
.dragon-layout{display:grid;grid-template-columns:minmax(240px,300px) minmax(0,1fr);gap:16px;align-items:start}
@media(max-width:800px){.dragon-layout{grid-template-columns:minmax(0,1fr)}}
#gTable .advanced{display:none}
#gTable.expanded .advanced{display:table-cell}
.project-scroll{overflow-x:auto}
@media(max-width:760px){
  .object-grid,.group-fields{grid-template-columns:minmax(0,1fr)}
  #objectPicker{margin:8px 10px 0}.top{gap:6px;padding:7px 10px}.tabs{max-width:100%;overflow:auto}.tab{padding:5px 9px}.modal{max-height:90dvh}
  .canvas{padding:12px 10px}
  .field{flex-direction:column;gap:4px}
  .field label{flex:none;padding-top:0}
}
.js-settings .card>.body{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,290px),1fr));gap:12px;align-items:start}
.js-settings .body>.field{display:flex;flex-direction:column;align-items:stretch;gap:6px;padding:12px;margin:0;min-width:0;border:1px solid var(--line);border-radius:8px}
.js-settings .field>label{width:auto;line-height:1.5;overflow-wrap:anywhere}
.js-settings .field>.ctl{min-width:0;width:100%;line-height:1.55;overflow-wrap:anywhere}
.js-settings .ctl input:not([type=checkbox]),.js-settings .ctl select,.js-settings .ctl textarea{width:100%;max-width:100%;box-sizing:border-box}
.js-settings .body>hr,.js-settings .body>b{grid-column:1/-1}
""";
    }
}
