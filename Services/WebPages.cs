using System;

namespace SoulLink.Services
{
    /// <summary>
    /// 网页界面（由 docs/web-index.html 生成，改样式请改那个文件）。
    ///
    /// ★ 生成命令：scripts\sync-webpages.ps1
    ///
    /// ★ 本常量只是**兜底**：主程序优先读 exe 同目录的 web-index.html，
    ///   这样改页面样式只需刷新浏览器，不用重编译、不用重启程序
    ///   （重启会断开所有 RDP 会话 —— 用户明确抱怨过）。
    /// </summary>
    internal static class WebPages
    {
        public const string Index = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<title>SoulLink</title>
<style>
  /* ══════════ 配色系统 ══════════
     ★ 与应用内主题保持一致（ThemeManager.cs 的颜色）：
         暗色                              亮色
       Bg0      #05060c         →        #faf2fc
       Bg1      #0b0f22         →        #f3ebfc
       TextPri  #f2f5ff         →        #30254c
       TextSec  rgba(194,232,255,.71) →  #392b56
       TextMut  #a5abcd         →        #5c4c78
       Cyan     #4fe3ff         →        #0087a7
       Mint     #41f0c0         →        #008869
       Rose     #ff6b8b         →        #c02753

     实现方式：全部走 CSS 变量，<html data-theme="dark|light"> 一键切换，
     选择记在 localStorage，刷新后保持。
  */
  :root, :root[data-theme="dark"] {
    --bg0:#05060c; --bg1:#0b0f22;
    --card:#121a2e; --card2:#0e1526;
    --card-hover:#182236; --card-active:#1c2740;
    --border:#25314b; --border-hi:#3b4c6e;
    --txt:#f2f5ff;
    --txt2:rgba(194,232,255,.78);
    --txt3:#a5abcd;
    --txt4:#7b86a8;
    --accent:#4fe3ff; --accent-bg:#1a3050; --accent-border:#2f4a72;
    --violet:#8b6bff;
    --mint:#41f0c0; --mint-bg:#163328; --mint-border:#1f4d3c;
    --rose:#ff6b8b; --rose-bg:#341f28; --rose-border:#5a2f3e;
    --magenta:#ff5fd2; --magenta-bg:#331e3a; --magenta-border:#4d2a58;
    --shadow:0 3px 18px rgba(0,0,0,.42);
    --shadow-sm:0 2px 10px rgba(0,0,0,.28);
    /* 背景装饰 */
    --nebula1:rgba(139,107,255,.46);
    --nebula2:rgba(79,227,255,.34);
    --glow:rgba(139,107,255,.16);
  }
  :root[data-theme="light"] {
    --bg0:#faf2fc; --bg1:#f3ebfc;
    --card:#ffffff; --card2:#fdf9ff;
    --card-hover:#f7f0ff; --card-active:#f0e6fc;
    --border:#e2d6f0; --border-hi:#c9b4e4;
    --txt:#30254c;
    --txt2:#392b56;
    --txt3:#5c4c78;
    --txt4:#8678a0;
    --accent:#0087a7; --accent-bg:#e0f4fa; --accent-border:#a8dcea;
    --violet:#7649c4;
    --mint:#008869; --mint-bg:#dff7ef; --mint-border:#9ddcca;
    --rose:#c02753; --rose-bg:#fbe4eb; --rose-border:#efb0c2;
    --magenta:#c83491; --magenta-bg:#fbe3f3; --magenta-border:#eeabd8;
    --shadow:0 3px 18px rgba(102,66,139,.13);
    --shadow-sm:0 2px 10px rgba(102,66,139,.09);
    /* 背景装饰：亮色下星云改淡紫，星点改深色才看得见 */
    /* ★ 亮色下必须用更浓的颜色，否则叠在 #faf2fc 上完全看不出
          （用户实测：.20 透明度肉眼不可见） */
    --nebula1:rgba(118,73,196,.42);
    --nebula2:rgba(0,140,175,.32);
    --glow:rgba(139,107,255,.18);
  }

  *{box-sizing:border-box;-webkit-tap-highlight-color:transparent;
    -webkit-user-select:none;user-select:none}
  /* ══════════ 背景：多层渐变 + 星点，模拟应用内的星空主题 ══════════
     ★ 用户要求：「你没做网页背景啊」——原来只有纯色 var(--bg0)。
       现在用 CSS 多层 radial-gradient 叠出：
         · 左上角紫色星云
         · 右下角青色星云
         · 中间微弱光晕
         · 细密星点（radial-gradient 重复）
     全部走 CSS，不引入任何图片资源，零体积。
  */
  /* ★ html 不能设不透明背景 —— 它会盖住 body 上的渐变。
     实测：html{background:#05060c} + body{background-image:radial-gradient(...)}
     结果是只看到纯色，渐变完全不可见。 */
  html{min-height:100%;background:transparent}
  body{margin:0;color:var(--txt);
       font-family:-apple-system,BlinkMacSystemFont,"Microsoft YaHei",sans-serif;
       -webkit-font-smoothing:antialiased;
       min-height:100vh;
       /* ★ 背景直接画在 body 上（不要用负 z-index 伪元素 ——
            那会跑到 html 背后被纯色盖住，实测就是这个原因导致"看不见背景"）。
            用 background-attachment:fixed 让渐变固定在视口，滚动时不跟着走。 */
       background-color:var(--bg0);
       /* ★ 星云：柔和的大面积渐变，边缘渐隐到透明 */
       background-image:
         radial-gradient(ellipse 80% 60% at 12% -8%, var(--nebula1) 0%, transparent 60%),
         radial-gradient(ellipse 75% 65% at 100% 105%, var(--nebula2) 0%, transparent 58%),
         radial-gradient(ellipse 90% 70% at 50% 42%, var(--glow) 0%, transparent 65%);
       background-attachment:fixed;
       background-repeat:no-repeat;
       transition:background-color .25s,color .25s}



  header{position:sticky;top:0;z-index:30;padding:13px 16px 11px;
         background:color-mix(in srgb, var(--bg0) 82%, transparent);
         backdrop-filter:blur(18px);
         border-bottom:1px solid var(--border);
         display:flex;align-items:center;gap:11px;
         transition:background .22s,border-color .22s}
  .logo{width:30px;height:30px;border-radius:9px;flex:0 0 auto;
        background:linear-gradient(135deg,var(--violet),var(--accent));
        display:flex;align-items:center;justify-content:center;
        font-size:15px;font-weight:700;color:#fff;
        box-shadow:var(--shadow-sm)}
  .ttl{font-size:15px;font-weight:650;letter-spacing:.2px;color:var(--txt)}
  .ver{font-size:10.5px;color:var(--txt4);margin-top:1px}
  .right{margin-left:auto;display:flex;align-items:center;gap:8px}
  .pill{font-size:11px;padding:4px 11px;border-radius:20px;
        background:var(--accent-bg);color:var(--accent);
        border:1px solid var(--accent-border);font-weight:550}
  .themebtn{width:32px;height:32px;border-radius:9px;flex:0 0 auto;
            margin-left:2px;
            border:1px solid var(--border);background:var(--card);
            color:var(--txt2);font-size:15px;cursor:pointer;
            display:flex;align-items:center;justify-content:center;
            transition:.15s}
  .themebtn:hover{border-color:var(--border-hi);background:var(--card-hover)}
  .themebtn:active{transform:scale(.94)}



  main{padding:14px 14px 40px;max-width:720px;margin:0 auto;background:transparent}

  .stat{background:linear-gradient(150deg,var(--card),var(--card2));
        border:1px solid var(--border);border-radius:15px;
        padding:16px 16px 15px;margin-bottom:14px;
        box-shadow:var(--shadow);transition:.22s}
  .stat h2{font-size:13px;margin:0 0 3px;color:var(--txt);
           font-weight:600;letter-spacing:.3px}
  .stat .desc{font-size:11px;color:var(--txt4);margin-bottom:13px}
  .metrics{display:flex;gap:9px}
  .metric{flex:1;background:var(--card2);border:1px solid var(--border);
          border-radius:11px;padding:11px 8px;text-align:center;transition:.22s}
  .metric .v{font-size:21px;font-weight:700;color:var(--accent);
             line-height:1.15;font-variant-numeric:tabular-nums}
  .metric .k{font-size:10px;color:var(--txt3);margin-top:3px}

  .sect-t{font-size:12.5px;color:var(--txt3);margin:18px 0 10px;
          font-weight:600;letter-spacing:.3px}
  .urow{background:linear-gradient(150deg,var(--card),var(--card2));
        border:1px solid var(--border);border-radius:14px;
        padding:13px 14px;margin-bottom:10px;
        display:flex;align-items:flex-start;gap:11px;cursor:pointer;
        box-shadow:var(--shadow-sm);
        transition:border-color .15s,background .15s,transform .1s}
  .urow:hover{border-color:var(--border-hi);background:var(--card-hover)}
  .urow:active{background:var(--card-active);transform:scale(.995)}

  .udot{width:8px;height:8px;border-radius:50%;flex:0 0 auto;margin-top:6px;
        background:var(--txt4)}
  .udot.on{background:var(--mint);box-shadow:0 0 8px var(--mint-border)}
  .uinfo{flex:1;min-width:0}
  .un{font-size:13.5px;font-weight:600;display:flex;align-items:center;
      gap:7px;flex-wrap:wrap;color:var(--txt)}
  .us{font-size:11px;color:var(--txt3);margin-top:3px}
  .tag{font-size:9.5px;padding:2px 7px;border-radius:5px;font-weight:550;
       background:var(--accent-bg);color:var(--accent)}
  .tag.rec{background:var(--magenta-bg);color:var(--magenta)}
  .tag.bgi{background:var(--mint-bg);color:var(--mint)}
  .tag.off{background:var(--rose-bg);color:var(--rose)}
  .uact{display:flex;gap:6px;flex:0 0 auto}

  .ibtn{padding:8px 14px;border-radius:9px;border:1px solid var(--accent-border);
        background:var(--accent-bg);color:var(--accent);font-size:11.5px;
        font-weight:550;cursor:pointer;white-space:nowrap;transition:.15s}
  .ibtn:hover{border-color:var(--accent);filter:brightness(1.08)}
  .ibtn:active{transform:scale(.97)}
  .ibtn.on{background:var(--accent);color:var(--bg0);border-color:var(--accent)}

  /* ══════════ 折叠卡片总览（2026-11-23 网页改版）══════════
     每账号一张卡片，默认收起，点头部展开功能按钮。 */
  .ucard{background:linear-gradient(150deg,var(--card),var(--card2));
         border:1px solid var(--border);border-radius:14px;margin-bottom:10px;
         box-shadow:var(--shadow-sm);overflow:hidden}
  .ucard-head{display:flex;align-items:center;gap:10px;padding:12px 14px;cursor:pointer;
              user-select:none;transition:background .15s}
  .ucard-head:hover{background:var(--card-hover)}
  .ucard-head .chev{margin-left:auto;font-size:10px;color:var(--txt3);
                    transition:transform .18s;display:inline-block;flex:0 0 auto}
  .ucard.open .ucard-head .chev{transform:rotate(90deg)}
  .ucard-body{display:none;padding:0 14px 13px;border-top:1px solid var(--border)}
  .ucard.open .ucard-body{display:block}
  .ucard-actions{display:flex;gap:8px;flex-wrap:wrap;margin-top:11px}
  .uact-btn{padding:9px 14px;border-radius:9px;border:1px solid var(--accent-border);
            background:var(--accent-bg);color:var(--accent);font-size:12px;
            font-weight:550;cursor:pointer;transition:.15s}
  .uact-btn:hover{border-color:var(--accent);filter:brightness(1.08)}
  .uact-btn:active{transform:scale(.97)}

  /* ══════════ 直播画质设置（滑杆/下拉，2026-11-23 手机端原型）══════════ */
  .qgroup{display:flex;align-items:center;gap:8px;flex:0 0 auto}
  .qgroup>span{font-size:10px;color:var(--txt3);white-space:nowrap}
  .qgroup input[type=range]{width:76px;height:20px;cursor:pointer;accent-color:var(--accent)}
  .qgroup .qval{font-size:10.5px;color:var(--txt2);min-width:40px;text-align:right;
                 font-variant-numeric:tabular-nums}
  .qguide{font-size:10px;color:var(--txt4);line-height:1.6;margin-top:6px}
  .capgroup{display:flex;align-items:center;gap:8px;flex:0 0 auto}
  .capgroup>span{font-size:10px;color:var(--txt3);white-space:nowrap}
  .capbackend{font-size:10.5px;color:var(--mint);font-weight:600;white-space:nowrap;
              font-variant-numeric:tabular-nums}
  .select-mini{height:26px;line-height:1;padding:0 16px 0 6px;font-size:11px;
                color:var(--txt);background:var(--card);border:1px solid var(--border);
                border-radius:8px;cursor:pointer}

  /* ══════════ 功能页（hash 路由：配置组分析 / 摩拉收益）══════════ */
  body.page-func main>.stat,
  body.page-func main>.sect-t,
  body.page-func main>#users,
  body.page-func main>#empty,
  body.page-func main>#viewer,
  body.page-func main>#saved{display:none !important}
  .page-back{display:inline-flex;align-items:center;gap:5px;padding:7px 12px;
             border-radius:8px;border:1px solid var(--border);background:var(--card2);
             color:var(--txt2);font-size:12px;cursor:pointer;margin-bottom:12px}
  .page-back:hover{border-color:var(--border-hi);color:var(--txt)}
  .userswitch{display:flex;gap:6px;flex-wrap:wrap;margin:2px 0 14px}
  .userswitch .uchip{padding:6px 12px;border-radius:20px;border:1px solid var(--border);
                      background:var(--card2);color:var(--txt3);font-size:11.5px;
                      cursor:pointer;transition:.15s;font-weight:550}
  .userswitch .uchip:hover{border-color:var(--border-hi);color:var(--txt2)}
  .userswitch .uchip.on{background:var(--accent);color:#fff;border-color:var(--accent)}
  .ldtable{width:100%;border-collapse:collapse;font-size:11.5px}
  .ldtable th{text-align:left;color:var(--txt4);font-weight:600;padding:7px 8px;
              border-bottom:1px solid var(--border);white-space:nowrap}
  .ldtable td{padding:7px 8px;border-bottom:1px solid var(--border);color:var(--txt2)}
  .ldtable tr:last-child td{border-bottom:none}
  .badge-fail{color:var(--rose)} .badge-ok{color:var(--mint)}
  .num{font-variant-numeric:tabular-nums}
  .mtxt{font-size:13px;color:var(--txt2)}
  .pageempty{padding:18px 14px;color:var(--txt4);font-size:12px}

  .detail{margin-top:10px;padding-top:10px;border-top:1px solid var(--border)}
  .dline{display:flex;gap:10px;font-size:11.5px;line-height:1.85}
  .dk{color:var(--txt4);flex:0 0 56px;font-size:11px}
  .dv{color:var(--txt2);flex:1;word-break:break-all}

  #viewer{display:none;margin-top:14px}

/* ── 独立窗口模式（?view=账号Id）───────────────────────────
   用户要求「查看画面要打开一个新页面」：新标签页只显示画面，
   把总览、账号列表、录像列表全部隐藏，画面占满可视区。 */
body.viewonly main>.stat,
body.viewonly main>.sect-t,
body.viewonly main>#users,
body.viewonly main>#empty,
body.viewonly #viewer .sect-t,
body.viewonly #saved,
body.viewonly #pback{display:none !important}
/* 让画面尽可能大：去掉外层留白和卡片内边距，并垂直居中 */
/* ★ 整个页面**不滚动**：header 固定高度，剩下全给画面。
   之前留了滚动条是因为 main 的 min-height + 默认 padding 叠加超出视口。 */
html.viewonly-root,body.viewonly{overflow:hidden;height:100%;margin:0;padding:0}
body.viewonly{display:flex;flex-direction:column;height:100vh}
body.viewonly main{flex:1 1 auto;min-height:0;max-width:100%;padding:0;margin:0;
                   display:flex;align-items:center;justify-content:center;overflow:hidden}
body.viewonly #viewer{display:flex !important;margin:0;padding:0;width:100%;height:100%;
                      align-items:center;justify-content:center}
body.viewonly #viewerBox{padding:0;margin:0;width:100%;height:100%;display:flex;
                         align-items:center;justify-content:center}
body.viewonly #viewerTitle{display:none}
body.viewonly header{padding:6px 12px}
body.viewonly header .logo{width:26px;height:26px;font-size:14px}
body.viewonly header .ttl{font-size:13px}
body.viewonly header .ver{display:none}
body.viewonly #liveCount{display:none}
/* 画面：宽度撑满，高度由图片比例决定（16:9 就正好是 16:9）。
   ★ 不要给 #scr 固定 height —— 那会让图片在容器里居中缩成小块，
     两侧露出 #scr 的黑色背景，看起来就是"画面很小 + 黑边"。
   ★ 也不要 height:100%：同理。容器必须由图片撑开。 */
/* ★ 画面按"能装进可视区"缩放：宽和高都不超过，比例不变。
   这样无论窗口是宽扁还是竖长，画面都是最大的那一个，
   多余空间均匀分布在四周（居中），而不是全堆在底部。 */
body.viewonly #scr{height:auto;background:transparent;width:100%;
                   min-height:160px;   /* ★ 首帧到达前也保持可视高度，避免 #scr 塌陷成空白 */
                   display:flex;align-items:center;justify-content:center}
/* 画面按可用区域缩放：宽高都不越界，比例不变，四周均匀留白 */
body.viewonly #scr canvas{width:auto;height:auto;max-width:100%;
                          max-height:calc(100vh - 46px);object-fit:contain;
                          margin:0 auto;display:none}
body.viewonly #scr canvas.show{display:block}
body.viewonly #viewerBox{background:transparent;box-shadow:none;border:none}
  #viewerBox{background:linear-gradient(150deg,var(--card),var(--card2));
             border:1px solid var(--border);border-radius:14px;
             padding:13px;box-shadow:var(--shadow);transition:.22s}
  #viewerTitle{font-size:13px;font-weight:600;color:var(--txt);
               margin-bottom:11px;display:flex;align-items:center;gap:8px}
  /* 容器宽度决定画面宽度；高度交给图片自己撑（见 img 规则）。
     背景用透明而不是纯黑 —— 纯黑在画面比容器小时会露出黑边。 */
  #scr{position:relative;background:transparent;border-radius:11px;overflow:hidden;
       border:1px solid var(--border);touch-action:none;cursor:default;
       min-height:220px;   /* ★ canvas 未出首帧时保持容器高度，连接中提示可见，不塌陷空白 */}
  /* ★ 宽度撑满、高度按比例自适应，并封顶在可视区内。
     历史教训：写成 height:100% 会让图在容器里居中缩小 → 画面变小 + 黑边。 */
  /* ★ 2026-11-23：JPEG 轮询通路按用户指令整个移除（「很卡很卡」），
     画面只有 <canvas id="vframe">（WebCodecs 解码直绘）。
     默认隐藏，JS 在首帧解码成功后加 .show（viewonly 的 display 规则
     优先级高于这里，见上面的 body.viewonly 块）。 */
  #scr canvas{display:none;width:100%;height:auto;max-height:70vh;
              object-fit:contain;pointer-events:none}
  #scr canvas.show{display:block}
  /* 占位提示层里的文字不可点（不挡 #scr 的手势），但重连按钮要可点：
     文字层 pointer-events:none，按钮单独恢复 auto。 */
  #ph{position:absolute;inset:0;display:flex;align-items:center;
      justify-content:center;color:#8b9bc4;font-size:12.5px;
      padding:20px;text-align:center;pointer-events:none;flex-direction:column;
      gap:10px;line-height:1.7}
  #ph #reconn{pointer-events:auto}
  #rip{position:absolute;width:46px;height:46px;margin:-23px 0 0 -23px;
       border-radius:50%;border:2px solid var(--accent);opacity:0;pointer-events:none}
  #rip.go{animation:rp .5s ease-out}
  /* 可见光标指示（2026-11-24）：绝对模式跟随鼠标 move 的小圆点，锁定态隐藏。
     叠在 canvas 上、pointer-events:none 不挡点击；margin 负偏移使圆心对准落点。 */
  #plCursor{position:absolute;width:18px;height:18px;margin:-9px 0 0 -9px;
            border:1.5px solid var(--accent);border-radius:50%;
            background:rgba(255,255,255,.16);box-shadow:0 0 4px rgba(0,0,0,.45);
            pointer-events:none;display:none;z-index:12}
  @keyframes rp{0%{opacity:.95;transform:scale(.3)}100%{opacity:0;transform:scale(1.6)}}

  .row{display:flex;align-items:center;justify-content:space-between;
       margin-top:11px;gap:10px;flex-wrap:wrap}
  .row label{font-size:11.5px;color:var(--txt3);flex:0 0 auto;font-weight:500}
  .seg{display:flex;gap:6px;flex-wrap:wrap}
  .seg button{padding:6px 12px;border-radius:8px;border:1px solid var(--border);
              background:var(--card2);color:var(--txt3);font-size:11px;
              font-weight:500;cursor:pointer;transition:.15s}
  .seg button:hover{border-color:var(--border-hi);color:var(--txt2)}
  .seg button.on{background:var(--accent-bg);color:var(--accent);
                 border-color:var(--accent-border)}

  /* 顶栏里的画面控制（viewonly 时才显示） */
.hdrinfo{font-size:10.5px;color:var(--txt3);margin-right:4px}

/* ★ 用 flex + 统一间距，保证垂直居中对齐（之前按钮高低不齐就是缺这个） */
.hdrctrl{display:flex;align-items:center;gap:10px;flex-wrap:nowrap}
  /* ══════════ 顶栏「设置」弹出菜单（2026-11-23）══════════
     默认只显示「设置」按钮；点开浮出面板，面板内放抓屏/鼠标/直播画质/录制参数。
     点面板外/ESC 自动收起（JS 已有）。 */
  .settingswrap{position:relative;flex:0 0 auto}
  .settingsbtn{height:22px;padding:0 12px;font-size:10.5px;border-radius:8px;
               border:1px solid var(--border);background:var(--card);color:var(--txt2);
               cursor:pointer;white-space:nowrap}
  .settingsbtn:hover{border-color:var(--accent);color:var(--accent)}
  .settingsbtn.on{background:var(--accent);color:#fff;border-color:var(--accent)}
  /* ★ 弹出面板定位在「设置」按钮正下方（right:0 贴按钮右缘），
      不飘到页面角落；z-index 高于 header，有背景/边框/阴影。 */
  .settingsmenu{position:absolute;top:30px;right:0;z-index:120;min-width:320px;
                max-width:min(90vw,420px);max-height:70vh;overflow:auto;
                background:var(--card);border:1px solid var(--border-hi);
                border-radius:12px;box-shadow:var(--shadow);padding:10px 12px;
                display:none}
  .settingsmenu.open{display:block}
  .settingsmenu .mgroup{padding:8px 0;border-top:1px solid var(--border)}
  .settingsmenu .mgroup:first-child{border-top:none;padding-top:0}
  .settingsmenu .mtitle{font-size:10px;color:var(--txt4);margin-bottom:6px;
                         font-weight:600;letter-spacing:.3px}
  .settingsmenu .hfield,.settingsmenu .qgroup{margin-bottom:6px;display:flex}

  /* ══════════ 顶栏收敛（2026-11-23：抓屏/鼠标/直播画质折进紧凑单行）══════════
     ★ 原来 hgroup 各自 padding 3px 8px + gap 6px，三组叠加顶栏过高。
       这里收紧：hdrctrl 允许换行但不撑高、hgroup 降 padding、gtag 更小、
       select/range 降高度，qguide 改单行省略。 */
  .hdrctrl{flex-wrap:wrap;row-gap:4px}
  .hgroup{padding:1px 6px;gap:4px;border-radius:7px}
  .gtag{font-size:9px}
  .hfield>span,.qgroup>span{font-size:9.5px}
  .hfield select,.select-mini{height:22px;padding:0 14px 0 5px;font-size:10.5px;
      background-position:calc(100% - 9px) 9px, calc(100% - 5px) 9px;background-size:3px 3px,3px 3px}
  .qgroup{height:22px}
  .qgroup input[type=range]{width:64px;height:16px}
  .qgroup .qval{font-size:9.5px;min-width:32px}
  .qguide{font-size:9px;line-height:1.4;margin-top:2px;white-space:nowrap;
           overflow:hidden;text-overflow:ellipsis;max-width:100%}
  .hbtn{height:22px;padding:0 10px;font-size:10.5px}
  .capbackend{font-size:10px}
  .hdrinfo{font-size:9.5px}
  #kbBadge{font-size:9.5px}
  .pill{font-size:10px;padding:3px 9px}
  header{padding:8px 12px 7px;gap:8px}

/* ★ 两组设置各自成块，用底色+边框区分，避免"帧率"到底指哪个的歧义。
     data-kind 只用于配色，不参与逻辑。 */
.hgroup{display:flex;align-items:center;gap:6px;padding:3px 8px;
        border-radius:9px;border:1px solid var(--border);
        background:color-mix(in srgb, var(--card) 65%, transparent)}
.hgroup[data-kind="record"]{--gtag:var(--accent)}
.hgroup[data-kind="view"]{--gtag:#7dd3a0}
.gtag{font-size:9.5px;font-weight:600;letter-spacing:.4px;
      color:var(--gtag);white-space:nowrap;padding-right:2px;
      border-right:1px solid var(--border);margin-right:2px}

/* 每一项 = 小标签 + 下拉，标签在上方不占横向空间 */
.hfield{display:flex;align-items:center;gap:4px;flex:0 0 auto}
.hfield>span{font-size:10px;color:var(--txt3);white-space:nowrap;letter-spacing:.2px}

/* 原生 select 统一外观：和按钮等高（26px）、同圆角、同配色 */
.hfield select{
  height:26px;line-height:1;padding:0 22px 0 8px;font-size:11.5px;
  color:var(--txt);background:var(--card);border:1px solid var(--border);
  border-radius:8px;cursor:pointer;white-space:nowrap;
  /* 右侧留出原生箭头的空间，避免文字被压 */
  appearance:none;-webkit-appearance:none;-moz-appearance:none;
  background-image:linear-gradient(45deg,transparent 50%,currentColor 50%),
                   linear-gradient(135deg,currentColor 50%,transparent 50%);
  background-position:calc(100% - 13px) 11px, calc(100% - 9px) 11px;
  background-size:4px 4px, 4px 4px;background-repeat:no-repeat;
}
.hfield select:hover{border-color:var(--accent)}
.hfield select:focus{outline:none;border-color:var(--accent)}

.hbtn{height:26px;padding:0 12px;font-size:11.5px;border-radius:8px;
      border:1px solid var(--border);background:var(--card);color:var(--txt);
      cursor:pointer;white-space:nowrap;display:inline-flex;
      align-items:center;justify-content:center}
.hbtn:hover{border-color:var(--accent)}
.hbtn.on{background:var(--accent);color:#fff;border-color:var(--accent)}

/* 可折叠的区块标题（「录像回放」用）：默认收起，点了才取数据 */
.sect-toggle{cursor:pointer;user-select:none;display:flex;align-items:center;gap:6px}
.sect-toggle:hover{color:var(--accent)}
.sect-toggle .chev{transition:transform .18s;display:inline-block;font-size:9px}
.sect-toggle.open .chev{transform:rotate(90deg)}
body.viewonly header{flex-wrap:nowrap;gap:8px}
body.viewonly .right{gap:6px;flex-wrap:nowrap}
/* 键盘状态用小圆点+文字提示，不再占一整行 */
#kbBadge{display:none;font-size:10.5px;color:var(--accent);white-space:nowrap}
#kbTip{display:none;margin-top:10px;padding:10px 12px;border-radius:9px;
         background:var(--accent-bg);border:1px solid var(--accent-border);
         color:var(--accent);font-size:11.5px;line-height:1.6}

  .item{background:var(--card2);border:1px solid var(--border);border-radius:10px;
        padding:11px 12px;margin-bottom:8px;cursor:pointer;
        display:flex;align-items:center;gap:10px;transition:.15s}
  .item:hover{border-color:var(--border-hi);background:var(--card-hover)}
  .inm{font-size:12.5px;color:var(--txt);font-weight:500}
  .imt{font-size:10.5px;color:var(--txt4);margin-top:3px}
  .badge{padding:2px 7px;border-radius:5px;font-size:9.5px;
         background:var(--accent-bg);color:var(--accent);margin-left:6px;font-weight:550}

  #player{display:none;margin-top:13px}
  #vid{width:100%;display:block;background:#000;border-radius:11px;
       border:1px solid var(--border)}
  .tools{display:flex;gap:8px;margin-top:10px}
  .tools button{flex:1;padding:10px;border-radius:9px;border:1px solid var(--border);
                background:var(--card2);color:var(--txt2);font-size:11.5px;
                font-weight:500;cursor:pointer;transition:.15s}
  .tools button:hover{border-color:var(--border-hi);color:var(--txt)}
  .tools button:active{background:var(--card-active)}

  .empty{color:var(--txt4);font-size:12.5px;text-align:center;padding:40px 20px;
         border:1px dashed var(--border);border-radius:13px;line-height:1.7}

  /* ══════════ 星空 ══════════
     ★ 用户反馈：「不是太密，是矩阵，很难看，圆点，没有星星的感觉」
       原来用 radial-gradient + repeat 平铺 = 规则网格 = 噪点观感。

     ★ 现在用 box-shadow 画星：
         · 1 个 1px 的圆点元素，用 100+ 个 box-shadow 在**随机坐标**画星
         · 分三层：远星(80颗/暗/小) 中星(45颗/中) 近星(18颗/亮/带光晕)
         · 坐标是伪随机生成的，不成行列、不重复
         · 纯 CSS，零图片体积
  */
  /* ★ 关键：body 必须建立层叠上下文（isolation），
        否则 z-index:0 的固定元素会和 body 背景同层，
        被 background-color 盖住 —— 这就是"星点看不见"的真正原因。 */
  /* 内容层（背景渐变已画在 body 上，无需额外层级处理） */
  video,header,main,.stat,.urow{position:relative}

</style>
</head>
<body>

<header>
  <div class="logo">S</div>
  <div>
    <div class="ttl">SoulLink</div>
    <div class="ver" id="ver"></div>
  </div>
  <div class="right">
    <!-- ★ 3.3.8：实时画面的操作全部移到顶栏。
         原来它们在画面下方单独占一整行，白吃掉可视高度（用户明确要求）。 -->
    <span id="kbBadge">⌨ 键盘已开</span>
    <span id="fpsinfo" class="hdrinfo"></span>
    <span class="pill" id="liveCount">— 在线</span>
        <div class="hdrctrl" id="viewctrls" style="display:none">
      <button id="pauseBtn" class="hbtn">暂停</button>
      <button id="closeViewer" class="hbtn">收起</button>
      <div class="settingswrap">
        <button id="settingsBtn" class="settingsbtn" type="button">设置</button>
        <div class="settingsmenu" id="settingsMenu">
          <div class="mgroup">
            <div class="mtitle">抓屏方式（配置 vs 实际）</div>
            <select id="capMode" class="hfield select-mini">
              <option value="0">自动(DDA→WGC→BitBlt)</option>
              <option value="1">DXGI DDA</option>
              <option value="2">WGC</option>
              <option value="3">BitBlt</option>
            </select>
            <span class="capbackend" id="capBackend">—</span>
          </div>
          <div class="mgroup">
            <div class="mtitle">鼠标模式</div>
            <div class="seg" id="mouseModeSeg">
              <button id="mmAbs" class="on" type="button">绝对</button>
              <button id="mmRel" type="button">相对</button>
            </div>
          </div>
          <div class="mgroup">
            <div class="mtitle">鼠标锁定（桌面转视角）</div>
            <label class="hfield"><span>逃生门</span>
              <select id="plEscape" class="hfield select-mini">
                <option value="ctrl_alt_shift">Ctrl+Alt+Shift</option>
                <option value="ctrl_alt">Ctrl+Alt</option>
                <option value="alt">单纯 Alt</option>
                <option value="alt_hold">长按 Alt</option>
              </select>
            </label>
            <label class="qgroup"><span>灵敏度</span><input id="plSens" type="range" min="0.1" max="3" step="0.1" value="1.0"><span class="qval" id="plSensV">1.0</span></label>
            <div class="qguide">ESC 一键开菜单 · 逃生门纯解锁 · 移动端自动禁用</div>
          </div>
          <div class="mgroup">
            <div class="mtitle">直播画质</div>
            <label class="qgroup"><span>码率</span><input id="qBitrate" type="range" min="1000" max="500000" step="1000" value="8000"><span class="qval" id="qBitrateV">8M</span></label>
            <label class="qgroup"><span>帧率</span><input id="qStreamFps" type="range" min="30" max="120" step="15" value="30"><span class="qval" id="qStreamFpsV">30fps</span></label>
            <label class="qgroup"><span>预设</span><select id="qPreset" class="hfield select-mini">
              <option value="1">P1 最快</option>
              <option value="3">P3 均衡</option>
              <option value="5">P5 高质</option>
              <option value="7">P7 最慢</option>
            </select></label>
            <label class="qgroup"><span>VBV</span><input id="qVbv" type="range" min="0" max="200" step="10" value="0"><span class="qval" id="qVbvV">0%</span></label>
            <div class="qguide">挂机用默认 · vbv 50-100% 画质更清晰 · preset 复杂画面调高</div>
          </div>
          <div class="mgroup">
            <div class="mtitle">录制参数（影响所有观看者）</div>
            <label class="hfield"><span>帧率</span>
              <select id="selFps">
                <option value="5">5 fps</option>
                <option value="10">10 fps</option>
                <option value="15">15 fps</option>
                <option value="30">30 fps</option>
              </select>
            </label>
            <label class="hfield"><span>画质</span>
              <select id="selQuality">
                <option value="60">标准 60</option>
                <option value="75">清晰 75</option>
                <option value="90">高清 90</option>
              </select>
            </label>
            <label class="hfield"><span>保留</span>
              <select id="selKeep">
                <option value="10">10 秒</option>
                <option value="30">30 秒</option>
                <option value="60">60 秒</option>
                <option value="120">120 秒</option>
              </select>
            </label>
            <label class="hfield"><span>直播帧率</span>
              <select id="selLiveFps">
                <option value="30">30 fps</option>
                <option value="45">45 fps</option>
                <option value="60">60 fps</option>
                <option value="120">120 fps</option>
              </select>
            </label>
          </div>
        </div>
      </div>
    </div>
        <button class="themebtn" id="themeBtn" title="切换明暗主题">
      <span id="themeIcon">🌙</span>
    </button>
  </div>
</header>

<main>
  <!-- ══ 运行状态总览 ══ -->
  <div class="stat">
    <h2>运行状态</h2>
    <div class="desc">监控各挂机账号的 BGI 运行情况 · 数据来自数据中心</div>
    <div class="metrics">
      <div class="metric"><div class="v" id="mTotal">0</div><div class="k">绑定账号</div></div>
      <div class="metric"><div class="v" id="mOnline">0</div><div class="k">会话在线</div></div>
      <div class="metric"><div class="v" id="mBgi">0</div><div class="k">BGI 运行</div></div>
    </div>
  </div>

  <div class="sect-t">账号</div>
  <div id="users"></div>
  <div class="empty" id="empty" style="display:none"></div>

  <!-- ══ 实时画面（点"查看画面"才展开）══ -->
  <div id="viewer">
   <div id="viewerBox">
    <div id="viewerTitle">实时画面</div>
    <div id="remoteSwitch" class="userswitch" style="display:none"></div>
    <div id="scr">
      <!-- ★ 2026-11-23：画面只有 WebCodecs 解码直绘的 canvas（用户指令：
           「我压根不需要 jpeg 序列，只要视频流」—— /live 轮询通路已移除）。
           #ph 是提示层：连接中 / 断开（内部含重连按钮，见 CSS 的 pointer-events）。 -->
      <canvas id="vframe"></canvas>
      <div id="ph"><span id="phtxt">连接中…</span><button id="reconn" class="hbtn" style="display:none">重新连接</button></div>
      <div id="rip"></div>
      <div id="plCursor"></div>
    </div>
    <!-- ★ 3.3.8：这里的操作行已全部移到顶栏（见 <header>）。
         画面区只留视频本身，不再被按钮占掉可视高度。
         原来的「刷新 1/0.5/0.25 秒」也已删除：那个间隔对使用者没有意义。 -->

    <!-- ★ 3.3.17：录像列表改成**点了才取**。
         /api/list 要扫磁盘目录 + 读每个 meta.json，打开画面时不需要。 -->
    <div class="sect-t sect-toggle" id="savedToggle">录像回放 <span class="chev">▸</span></div>
    <div id="savedWrap" style="display:none"><div id="saved"></div></div>
    <div id="player">
      <video id="vid" controls playsinline preload="metadata"></video>
      <div class="imt" id="pinfo" style="text-align:center;margin-top:8px"></div>
      <div class="tools">
        <button id="pback">关闭播放器</button>
        <button id="dl">下载</button>
      </div>
    </div>
   </div>
  </div>

  <!-- ══ 配置组分析页（#/ledger/:id，2026-11-23 网页改版）══ -->
  <div id="ledgerPage" style="display:none">
    <button class="page-back" id="ledgerBack" type="button">‹ 返回总览</button>
    <div class="sect-t">配置组分析</div>
    <div class="userswitch" id="ledgerSwitch" style="display:none"></div>
    <div id="ledgerBody"></div>
  </div>

  <!-- ══ 摩拉收益页（#/earnings/:id，2026-11-23 网页改版）══ -->
  <div id="earningsPage" style="display:none">
    <button class="page-back" id="earningsBack" type="button">‹ 返回总览</button>
    <div class="sect-t">摩拉收益</div>
    <div class="userswitch" id="earningsSwitch" style="display:none"></div>
    <div id="earningsBody"></div>
  </div>
</main>

<script>
const $ = id => document.getElementById(id);

// ══════════ 明暗主题 ══════════
//
//  ★ 与应用内主题联动（用户要求：「把应用的亮色与暗色主题做进去」）。
//    优先级：用户手动选择（localStorage）> 应用当前主题（API 下发）> 系统偏好。
//    应用里改主题后，网页重新加载会自动跟随；用户也可以在本页手动覆盖。

let themeMode = 'auto';   // auto | dark | light
let appIsDark = true;     // 来自后端 /api/sessions 的 theme 字段

function applyTheme() {
  let dark;
  if (themeMode === 'dark') dark = true;
  else if (themeMode === 'light') dark = false;
  else if (themeMode !== 'auto' && typeof appIsDark === 'boolean') dark = appIsDark;
  else dark = appIsDark;

  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
  const icon = $('themeIcon');
  if (icon) icon.textContent = dark ? '🌙' : '☀️';
  try { localStorage.setItem('soullink-theme', themeMode); } catch (e) {}
}

// 启动时读用户偏好（★ 整个主题初始化必须容错，
//   任何一个元素缺失都不能让后面的 loadUsers 停下 —— 实测踩过：
//   主题按钮没渲染出来时 $('themeBtn').onclick 抛异常，
//   导致账号列表永远空白。）
try {
  const saved = localStorage.getItem('soullink-theme');
  if (saved === 'dark' || saved === 'light' || saved === 'auto') themeMode = saved;
} catch (e) {}
try { applyTheme(); } catch (e) { console.warn('主题初始化失败', e); }

// 点击：在"跟随应用"和"手动取反"之间轮换
const tb = $('themeBtn');
if (tb) tb.onclick = () => {
  const cur = document.documentElement.getAttribute('data-theme') === 'dark';
  // 先手动切到相反主题；如果本来就是手动模式且与应用相反，则回到 auto
  if (themeMode === 'auto') {
    themeMode = cur ? 'light' : 'dark';
  } else if (themeMode === (cur ? 'dark' : 'light')) {
    themeMode = 'auto';
  } else {
    themeMode = cur ? 'light' : 'dark';
  }
  applyTheme();
  // 短暂提示当前模式
  const btn = $('themeBtn');
  const tips = { auto: '跟随应用', dark: '暗色', light: '亮色' };
  btn.title = '主题：' + tips[themeMode] + '（点击切换）';
};
let users = [], cur = null;
// ══════════ 两组完全不同的参数，别混 ══════════
//
//   录制（recFps/recQuality/recKeep）
//     · 由**代理**执行：多久截一次屏、压多清、内存留多久
//     · 写回应用配置，影响所有观看者
//
//   播放（viewFps）
//     · 由**本浏览器**执行：多久来取一次已经录好的帧
//     · 只存在本机 localStorage，不改配置、不影响别人
//     · 0 = 跟随录制帧率（取图频率 = 录制频率，最省事）
//
//   ★ 上限关系：实际取图频率 = min(viewFps, recFps)，
//     取再快也只能拿到同一帧（代理还没录新的）。
let paused = false;
let recFps = 5, recQuality = 75, recKeep = 30, streamFps = 30, actualLiveFps = 0;
let videoBitrate = 8000, nvencPreset = 3, vbvBufferIncreasePercent = 0;   // 直播画质（2026-11-23）
let captureMode = 0;                       // 抓屏后端（0=自动/1=DDA/2=WGC/3=BitBlt）
let viewFps = 0;              // 0 = 跟随录制（2026-11-23 起只用于鼠标移动节流，
                              // 不再控制拉帧 —— 画面是 /stream 视频流直推）
let liveMs = 500;
const VIEWFPS_KEY = 'sl_view_fps';

function computeLiveMs() {
  const recordHz = Math.max(1, Math.min(30, recFps || 10));
  let hz = (viewFps > 0) ? viewFps : recordHz;
  if (hz > recordHz) hz = recordHz;      // 不可能比录制更快拿到新画面
  liveMs = Math.max(33, Math.round(1000 / hz));
}
let kbMode = false;   // 右键已直接支持，不再需要开关

async function loadUsers() {
  try {
    const r = await fetch('/api/sessions');
    const d = await r.json();
    users = (d.sessions || []).filter(s => s.bound);

    // ★ 3.3.5：直连代理的口令（后端随会话列表下发，本接口本身已鉴权）
    //   必须拿到它，否则 <img src> 打到代理会 401（浏览器不会给 img 加请求头）。
    window.__slToken = d.directToken || "";

    // ★ 录制参数：以应用设置为准（用户要求「两边同步」）
    applySettings(d);

    // ★ 跟随应用主题：后端下发当前是暗色还是亮色
    if (typeof d.themeDark === 'boolean') {
      appIsDark = d.themeDark;
      applyTheme();     // 用户手动选过就忽略，否则自动跟随
    }
    $('ver').textContent = d.version ? ('v' + d.version) : '';
    const on = users.filter(u => u.online).length;
    $('liveCount').textContent = users.filter(u => u.bgiRunning).length + ' BGI';
    $('mTotal').textContent = users.length;
    $('mOnline').textContent = on;
    $('mBgi').textContent = users.filter(u => u.bgiRunning).length;
    renderUsers();
  } catch (e) {
    $('empty').style.display = 'block';
    $('empty').textContent = '加载失败，请刷新重试';
  }
}

function renderUsers() {
  const box = $('users');
  box.innerHTML = '';
  if (!users.length) {
    $('empty').style.display = 'block';
    $('empty').textContent = '还没有绑定账号。在「设置 → BetterGI 路径」里绑定后，这里会出现。';
    return;
  }
  $('empty').style.display = 'none';

  // ★ 2026-11-23 网页改版：每账号一张可折叠卡片（默认收起），
  //   点卡片头部展开该账号的功能按钮（查看画面 / 配置组分析 / 摩拉收益）。
  for (const u of users) {
    const card = document.createElement('div');
    card.className = 'ucard';

    const tags = [];
    if (u.bgiRunning) tags.push('<span class="tag bgi">BGI ' +
      (u.bgiLogIdleMinutes >= 0 ? '空闲 ' + u.bgiLogIdleMinutes + ' 分' : '运行中') + '</span>');
    if (u.recording) tags.push('<span class="tag rec">录制中</span>');
    if (!u.online) tags.push('<span class="tag off">离线</span>');

    let sub = '';
    if (!u.online) sub = '会话未连接';
    else sub = esc(u.screen) + ' · 通道就绪 · ' + (u.bgiRunning ? 'BGI#' + u.bgiPid : 'BGI 未运行');

    // 运行详情（展开体里显示，与旧版同源同字段）
    let detail = '';
    if (u.bgiRunning) {
      const parts = [];
      if (u.configGroup) parts.push(['配置组', u.configGroup]);
      if (u.taskName) parts.push(['任务', u.taskName]);
      if (u.routeFile) parts.push(['路线', u.routeFile]);
      if (u.round || u.role) parts.push(['轮次/角色',
        [u.round, u.role].filter(Boolean).join(' · ')]);
      if (u.eta) parts.push(['预计', u.eta]);
      if (u.action) parts.push(['当前动作', u.action]);
      if (u.pickTotal > 0) parts.push(['拾取', u.pickTotal + ' 个']);
      if (u.errorCount > 0) parts.push(['报错', u.errorCount + ' 次']);

      detail = parts.length === 0
        ? '<div class="detail"><div class="dline"><span class="dk">状态</span>'
          + '<span class="dv">等待日志中的状态信息…</span></div></div>'
        : '<div class="detail">'
          + parts.map(p => '<div class="dline"><span class="dk">' + esc(p[0])
              + '</span><span class="dv">' + esc(String(p[1])) + '</span></div>').join('')
          + '</div>';
    }

    card.innerHTML =
      '<div class="ucard-head">'
      + '<span class="udot' + (u.online ? ' on' : '') + '"></span>'
      + '<div class="uinfo">'
      + '<div class="un">' + esc(u.label) + tags.join('') + '</div>'
      + '<div class="us">' + sub + '</div>'
      + '</div>'
      + '<span class="chev">▸</span>'
      + '</div>'
      + '<div class="ucard-body">'
      + detail
      + '<div class="ucard-actions">'
      + '<button class="uact-btn" data-act="remote">查看画面</button>'
      + '<button class="uact-btn" data-act="ledger">配置组分析</button>'
      + '<button class="uact-btn" data-act="earnings">摩拉收益</button>'
      + '</div>'
      + '</div>';

    // 点头部折叠/展开
    card.querySelector('.ucard-head').onclick = () => card.classList.toggle('open');

    // 功能按钮：hash 路由同页跳转（不再 window.open 新标签）
    card.querySelectorAll('.uact-btn').forEach(btn => {
      btn.onclick = e => {
        e.stopPropagation();
        const act = btn.getAttribute('data-act');
        location.hash = act === 'remote'
          ? ('#/remote/' + encodeURIComponent(u.id))
          : ('#/' + act + '/' + encodeURIComponent(u.id));
      };
    });

    box.appendChild(card);
  }
}

function closeViewer() {
  plLifecycleExit();
  stopLive();
  $('player').style.display = 'none';
  const v = $('vid'); v.pause(); v.removeAttribute('src'); v.load();
  cur = null;
  $('viewer').style.display = 'none';

  // ★ 2026-11-23 hash 路由：收起 = 回总览（同页跳转），
  //   不再 window.close / 跳 "/"（那是旧 ?view= 独立窗口的语义）。
  location.hash = '#/';
}

// ══════════ 视频流播放（2026-11-23 按用户指令重写）══════════════
//
// ★★ 用户指令：「我压根不需要 jpeg 序列，只要视频流，上个工作会话还保留它，很卡很卡」。
//
//   /live（单帧 JPEG 轮询）通路**整个移除**，不再是降级备胎。
//   /stream（H.264 Annex-B + WebCodecs）是唯一画面通路：
//     · 断开 → 画面上明确提示 + 自动退避重连（1s→2s→4s→8s）+ 手动重连按钮；
//     · 重连是全新的 HTTP 连接，从头等 SPS/IDR 再起播（不从半个 GOP 里捡）；
//     · 页面隐藏 → 断流省资源；回前台 → 自动重连；
//     · 暂停 → 断流；继续 → 重连。
//   （保留的 liveMs/computeLiveMs 只服务鼠标移动节流和设置回显，与拉帧无关。）
let liveRunning = false;      // 是否处于「应播放」状态（true 时才允许重连）
let failStreak = 0;           // 连续连接失败次数（出画面即清零）
let reconnectTimer = null;    // 自动重连的 setTimeout 句柄

const webCodecsSupported = (typeof VideoDecoder !== 'undefined')
    && (typeof EncodedVideoChunk !== 'undefined');

// ═════════════ 播放器状态必须放在模块级（2026-11-23 修复）═════════════
//   旧版 h264Gener/h264Reader/dirtyFrame 声明在 pullFrame() 内部，
//   每次 pullFrame 都重建闭包 —— 代际永远等于自己，作废机制完全失效：
//   stopLive() 之后流还在拉、切账号时两条流并发、decode 回调还在画旧画面。
let h264Gen = 0;              // 代际：停流/重连/切账号时 +1，作废旧流的一切回调
let h264Reader = null;        // /stream 的 ReadableStreamDefaultReader
let h264Active = false;       // /stream 连接是否在跑

// 收掉当前流（作废回调 + 断开连接），不动画面
function abortStream() {
  h264Gen++;                  // 读流循环、decode 回调全部作废
  h264Active = false;
  const r = h264Reader; h264Reader = null;
  if (r) { try { r.cancel().catch(() => {}); } catch (e) {} }
}

// 停流 + 隐藏画面（切账号 / 暂停用）
function stopH264() {
  abortStream();
  $('vframe').classList.remove('show');
}

function stopLive() {
  liveRunning = false;
  stopH264();                  // 也断掉视频流（旧版漏了，流会在后台一直跑）
  if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
}

function startLive() {
  stopLive();
  if (!cur) return;
  if (!cur.online) { setPh('该账号不在线', false); return; }
  liveRunning = true;
  failStreak = 0;
  if (!webCodecsSupported) {
    // 没有降级通路（用户指令），只能明确告知
    setPh('当前浏览器不支持 H.264 视频流播放（需要 WebCodecs，请用新版 Chrome/Edge）', false);
    return;
  }
  setPh('连接中…', false);
  startStream();
}

// 断开后自动重连：按失败次数指数退避，正在排队时不重复排
function scheduleReconnect() {
  if (!liveRunning || reconnectTimer) return;
  const delay = Math.min(1000 * Math.pow(2, failStreak), 8000);
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    if (liveRunning) startStream();
  }, delay);
}

// 提示层：msg=null 隐藏；否则显示文本（可选重连按钮）。
// 半透明底色盖在最后一帧上 —— 用户能看到断开瞬间的画面，而不是黑屏。
function setPh(msg, showReconn) {
  const ph = $('ph');
  if (!ph) return;
  if (msg === null) { ph.style.display = 'none'; return; }
  ph.style.display = 'flex';
  $('phtxt').textContent = msg;
  $('reconn').style.display = showReconn ? 'inline-flex' : 'none';
}

// 手动重连按钮：清退避、立即重连
const __rc = $('reconn');
if (__rc) __rc.onclick = () => {
  if (!cur || !liveRunning) return;
  if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
  failStreak = 0;
  setPh('连接中…', false);
  startStream();
};

// ── 直连代理（3.3.5）───────────────────────────────────────────
//   用户在局域网里访问 http://主机:10800，但画面/按键直连代理的端口：
//     http://主机:(sessions[i].directPort)
//   这样主程序只在加载列表时被访问一次，之后不碰任何流量。
//
//   主机名沿用当前页面访问用的那个（手机连的 192.168.x.x 不会变成 localhost）。
function directToken() { return window.__slToken || ''; }

function directBase(sess) {
  const p = sess && sess.directPort;
  if (!p || p <= 0) return '';
  return location.protocol + '//' + location.hostname + ':' + p;
}

// ═════════════ Annex-B → AVCC 的格式选择（2026-11-23，查证规范后定）═════════════
//
// 依据 W3C「AVC (H.264) WebCodecs Registration」§2/§3：
//   · VideoDecoderConfig.description 存在 → 码流按 AVCC（长度前缀）解析，
//     description 必须是 AVCDecoderConfigurationRecord（即 MP4 的 avcC）。
//   · description 不存在 → 码流按 Annex-B（起始码）解析，
//     且 key chunk 必须自带解码所需的全部参数集（SPS/PPS）。
//
// 旧版把 Annex-B 转成长度前缀喂 decoder 却不给 description ——
// 按 AVCC 解析无起始码的裸 NAL，与规范相矛盾，必然一帧都解不出来。
//
// ★ 本实现选择：不给 description + 纯 Annex-B。
//   理由：① 服务端 MfFrameEncoder 输出本来就是带 00000001 起始码的
//            Annex-B，且 IDR 帧带 SPS/PPS，正好是规范对 annexb 模式
//            key chunk 的要求 —— 零转换、零重组；
//         ② 走 AVCC 得自己拼 AVCDecoderConfigurationRecord（含 SPS/PPS
//            重组和长度表），多一层易错的手工活，收益为零。
//   EncodedVideoChunk.data 直接给「起始码+NAL」的原始字节。
// ══════════════════════════════════════════════════════════════════

  // 从 SPS NAL（已去掉起始码、**含 1 字节 NAL 头**）解析 profile_idc /
  // constraint flags / level_idc，拼成 WebCodecs 的 codec 字符串
  // 'avc1.PPCCLL'（PP=profile, CC=constraint, LL=level，各 2 位十六进制，
  // 见 RFC 6381 §3.4）。解析失败返回 null，由调用方走默认值。
  // ★ SPS NAL 布局（ITU-T H.264 §7.3.2.1）：
  //   byte0 = NAL 头（type=7，必须跳过！）
  //   byte1 = profile_idc
  //   byte2 = constraint_set0..5 + reserved_zero（codec 串要整个字节，含保留位）
  //   byte3 = level_idc
  //   （单测曾抓到没跳 NAL 头的 bug：nal[0]=0x67 被当 profile → 解析必失败）
  function avcCodecFromSps(sps) {
    if (!sps || sps.length < 4) return null;
    const p = sps[1], c = sps[2], l = sps[3];
    // 合理性检查：profile_idc 只可能是这些已定义值
    const known = [44, 66, 77, 83, 86, 88, 100, 110, 118, 122, 128,
                   138, 139, 134, 135, 244, 247];
    if (!known.includes(p)) return null;
    const hex = n => n.toString(16).padStart(2, '0').toUpperCase();
    return 'avc1.' + hex(p) + hex(c) + hex(l);
  }
  // ═════════════ splitAnnexB：重写（2026-11-23，单测校验过）═════════════
  // 旧版三处错误：
  //   ① 循环里 `i += 2`/`i += 3` 跳过起始码后会**越过下一个起始码**的检测位置
  //     （起始码可以紧贴在前一个 NAL 尾部，跳着扫会漏检）；
  //   ② 检测 4 字节起始码要求 i+4 < len，缓冲末尾恰好停在 00 00 00 01 时
  //     检测不到 → 起始码被永久丢弃 → 丢帧；
  //   ③ 前导零归属错：00 00 00 00 01 应整体视为起始码区（Annex-B 的
  //     leading_zero_8bits 属于起始码不属于上一个 NAL），否则切出的 NAL
  //     尾部挂着起始码前缀字节。
  //
  // 新实现：逐字节状态扫描，起始码区 = 遇到 1 时的整段连零 + 这个 1，
  // 即 at = i - zeros、len = zeros + 1（3/4 字节起始码及任意长度前导零
  // 都正确），上一个 NAL 止于 at，下一个 NAL 始于 i+1。
  // 末段（最后一个起始码之后）一律留到下一轮 —— 可能是不完整的 NAL，
  // 下轮新起始码到达时被安全切出，不丢帧也不吐半 NAL。
  function splitAnnexB(buf) {
    const units = [];
    const starts = [];      // 每个起始码的位置（at）与长度（len）
    let zeros = 0;
    for (let i = 0; i < buf.length; i++) {
      const b = buf[i];
      if (b === 0) { zeros++; continue; }
      if (b === 1 && zeros >= 2) {
        starts.push({ at: i - zeros, len: zeros + 1 });
        zeros = 0;
        continue;
      }
      zeros = 0;
    }
    if (!starts.length) {
      // 整个缓冲没有起始码：流头垃圾或起始码前缀还没到齐。
      // 全部保留；超过 1MB 仍无起始码视为坏流，清空（防内存无限涨）。
      return { units, pending: buf.length > 1048576 ? new Uint8Array(0) : buf };
    }
    for (let k = 0; k < starts.length - 1; k++) {
      const from = starts[k].at + starts[k].len;
      const to = starts[k + 1].at;
      if (to > from) units.push(buf.subarray(from, to));
    }
    // 末段从最后一个起始码起保留（含起始码本身，下轮重新定位）。
    // 末段超过 4MB 说明坏流（单个 NAL 不可能这么大），丢弃防涨。
    const lastStart = starts[starts.length - 1];
    if (buf.length - lastStart.at > 4 * 1048576) {
      return { units, pending: new Uint8Array(0) };
    }
    return { units, pending: buf.subarray(lastStart.at) };
  }

  // H.264 主循环：拉 /stream → 切 NAL → 找 SPS 配置 decoder → 解码画 canvas
  async function startStream() {
    const myGen = ++h264Gen;
    h264Active = true;
    const base = directBase(cur);
    const tok = directToken();
    const url = (base || '') + '/stream'
        + (base && tok ? '?token=' + encodeURIComponent(tok) : '');

    let decoder = null;
    let configured = false;
    let firstKey = false;
    let gotFrame = false;        // 本轮是否已出过画面（区分「连不上」和「断了」）
    let stallTimer = null;       // 画面看门狗 setInterval 句柄（函数级声明，finally 才能安全 clear）
    let lastFrameAt = 0;         // 最近一次解码出帧时间戳（函数级声明，output/finally 共用）

    // 解码帧直接画到 DOM 里的 <canvas id="vframe">，不再经过 <img>。
    // 旧版每帧 canvas.toDataURL('image/jpeg') 等于把省下的带宽又花回去
    //（JPEG 重编码 + base64 膨胀 1.33 倍），已删除。
    const canvas = $('vframe');
    const ctx = canvas.getContext('2d', { alpha: false });

    // 失败 = 停流 + 提示 + 自动退避重连（没有 JPEG 降级，用户指令移除）
    const fail = (msg, e) => {
      if (myGen !== h264Gen) return;      // 已换代：静默退出，别干扰新流
      if (e) console.warn(msg, e); else console.warn(msg);
      abortStream();                        // 只收流，画面留着（冻结在最后一帧）
      setPh(gotFrame
          ? '视频流已断开 · 正在自动重连（' + Math.min(1000 * Math.pow(2, failStreak), 8000) + 'ms 后）'
          : '连接不上视频流（第 ' + (failStreak + 1) + ' 次）· 正在自动重连', true);
      failStreak++;
      scheduleReconnect();
    };

    try {
      decoder = new VideoDecoder({
        output: (frame) => {
          if (myGen !== h264Gen) { frame.close(); return; }
          try {
            if (canvas.width !== frame.displayWidth
                || canvas.height !== frame.displayHeight) {
              canvas.width = frame.displayWidth;
              canvas.height = frame.displayHeight;
            }
            ctx.drawImage(frame, 0, 0);
            canvas.classList.add('show');
            setPh(null, false);
            lastFrameAt = Date.now();     // 真的画出一帧 → 重置画面看门狗
            if (!gotFrame) {
              gotFrame = true;
              failStreak = 0;             // 出画面了，失败计数清零
            }
          } finally { frame.close(); }
        },
        error: (e) => fail('[H264] 解码错误，断开重连:', e)
      });

      const resp = await fetch(url, { cache: 'no-store' });
      if (myGen !== h264Gen) { try { resp.body && resp.body.cancel(); } catch (e) {} return; }
      if (!resp.ok || !resp.body) throw new Error('HTTP ' + resp.status);
      const reader = resp.body.getReader();
      h264Reader = reader;

      // ── 拉流自愈：无数据超时 → 断流重连（2026-11-23）──
      //   修「DXGI 播一会儿自己停」：服务端 /stream 断开不一定走 done/异常，
      //   可能只是编码器停转/后端切换卡住后不再吐数据，read() 永远 pending。
      //   只在**出过画面之后**才启用看门狗 —— 首帧慢（软编 warmup/等 IDR）不算断流。
      const STALL_MS = 10000;       // 出过画面后 10s 无「新解码帧」= 画面卡住，触发带退避重连
      // ★ 看门狗以「解码帧」为准，而非「网络字节」（2026-11-23 修复）：
      //   旧实现 lastDataAt 在每次 reader.read() 返回字节时刷新，但 /stream 连接保持、
      //   编码器停转时 read() 会永久 pending；更糟的是即便连接还活着，也可能只吐
      //   SPS/PPS/SEI 等非画面字节 —— 这些字节一直刷新 lastDataAt，看门狗永远不触发。
      //   改为 lastFrameAt：只有 decoder output 真的画出一帧才更新，画面卡住必重连。
      lastFrameAt = Date.now();
      stallTimer = setInterval(() => {
        if (myGen !== h264Gen) return;
        if (gotFrame && Date.now() - lastFrameAt >= STALL_MS) {
          console.warn('[H264] 画面 ' + STALL_MS + 'ms 无新帧，自动重连');
          fail('[H264] 画面无新帧超时', null);
        }
      }, 1000);

      let pending = new Uint8Array(0);
      // ── AU（access unit）攒批 ──
      // ★ WebCodecs 的 chunk.data 必须是完整 AU（AVC Registration §2/§4）：
      //   annexb 模式的 key chunk 必须同时含 SPS/PPS/IDR。逐 NAL 喂
      //   decode() 是非法的（自测页实测：decode×14 出 0 帧 + "Decoding error"）。
      //   所以按 Annex-B 惯例把 NAL 攒成 AU 再喂：SPS/PPS/SEI/新 slice
      //   （type 1-5）出现 = 上一个 AU 结束。
      let auNals = [];           // 当前 AU 的 NAL（纯 NAL 体，无起始码）
      let auHasKey = false, auHasData = false;
      let ts = 0;                // AU 时间戳（单调微秒）

      // ── P0：落后检测 + 主动跳帧（2026-11-23，用户实测 4.3 秒画面延迟）──
      //
      // 根因链：服务端 200ms 批量推流 + 解码跟不上 → decodeQueue 积压
      // → WebCodecs 不会主动丢帧 → 每帧落后 66ms 逐帧累积 → 4.3 秒。
      //
      // 策略（直播标准做法）：
      //   · decodeQueueSize > SKIP_QUEUE 阈值 → 停止喂 delta 帧（在 AU 层丢，
      //     不浪费带宽解码再扔），只放行 key 帧；
      //   · 丢帧期间若来了 key AU → 立刻恢复喂（解码链从关键帧重建）；
      //   · 连续跳帧计数达 SKIP_RESET_LIMIT → 直接断流重连重同步
      //     （无限 GOP 下本地 reset 等不到 IDR，重连=新订阅=按需 IDR，兜底手段）。
      //   丢的是「追赶过去的旧画面」，用户看到的永远是较新的帧。
      const SKIP_QUEUE = 2;          // 队列超过它就进入跳帧模式（30fps 下 ~66ms 即反应）
      const SKIP_RESET_LIMIT = 10;   // 连续跳过 10 个 AU（≈330ms@30fps）→ 断流重连重同步
      let skipping = false;          // 是否处于跳帧模式
      let skippedAUs = 0;            // 本轮连续跳过的 AU 数
      let skippedTotal = 0;          // 累计跳过（日志用）

      const flushAu = () => {
        if (!auHasData || !configured) { auNals = []; auHasKey = false; auHasData = false; return; }

        // ── 跳帧判定 ──
        if (decoder.decodeQueueSize > SKIP_QUEUE && !auHasKey) {
          // 积压且不是关键帧：整个 AU 丢弃（不解码旧画面）
          auNals = []; auHasKey = false; auHasData = false;
          skippedAUs++; skippedTotal++;
          skipping = true;
          if (skippedAUs >= SKIP_RESET_LIMIT) {
            // 兜底：队列长期消化不动 → 重连式重同步（2026-11-23 修复 W2）
            //   无限 GOP 下 decoder.reset() 后服务端不会主动发 SPS/IDR，本地 reset
            //   干等新 IDR 是死路（每轮白付 10s 黑屏）。改为直接断流重连：
            //   重连 = 新订阅，服务端按需发 IDR，立刻重建解码链。
            console.warn('[H264] 解码积压 ' + skippedAUs + ' 帧，直接断流重连重同步');
            fail('[H264] 解码积压过重，重连重同步', null);
            return;
          }
          return;
        }
        if (auHasKey) {
          // 关键帧永远放行：既是恢复点，也顺带清掉跳帧状态
          if (skipping) console.warn('[H264] 跳帧 ' + skippedAUs + ' 个 AU 后由 IDR 恢复');
          skipping = false; skippedAUs = 0;
        }
        // AU → 单个 annexb 字节块（4 字节起始码 + 各 NAL）
        let total = 0;
        for (const n of auNals) total += 4 + n.length;
        const data = new Uint8Array(total);
        let off = 0;
        for (const n of auNals) {
          data[off] = 0; data[off+1] = 0; data[off+2] = 0; data[off+3] = 1;
          data.set(n, off + 4);
          off += 4 + n.length;
        }
        try {
          decoder.decode(new EncodedVideoChunk({
            type: auHasKey ? 'key' : 'delta',
            timestamp: ts,
            data
          }));
        } catch (e) { /* 解码器饱和时丢弃本 AU，等下一帧 */ }
        ts += 66666;             // 固定 15fps 节拍（与服务端一致）；单调即可
        auNals = [];
        auHasKey = false;
        auHasData = false;
      };

      while (myGen === h264Gen) {
        const { done, value } = await reader.read();
        if (myGen !== h264Gen) break;      // 换代：立刻退出，不碰已换人的 reader
        if (done) break;

        const merged = new Uint8Array(pending.length + value.length);
        merged.set(pending, 0);
        merged.set(value, pending.length);
        pending = merged;

        const nals = splitAnnexB(pending);
        pending = nals.pending;
        if (!nals.units.length) continue;

        for (const nal of nals.units) {
          if (myGen !== h264Gen) break;
          const type = nal[0] & 0x1f;
          const isKey = (type === 5);
          const isSps = (type === 7), isPps = (type === 8);
          const isVcl = (type >= 1 && type <= 5);   // slice/IDR = 图像数据

          // 首个 SPS 之前绝不能 decode —— 没 configure 的 decoder 会抛
          // InvalidStateError。SPS 一到就从它解析 codec 字符串再 configure。
          if (isSps && !configured) {
            const codec = avcCodecFromSps(nal);
            if (!codec) {
              console.warn('[H264] SPS 解析失败，用兜底 avc1.4D4028（Main@4.0）');
            }
            // ★ codec 字符串从 SPS 动态生成（profile/constraint/level 与
            //   服务端实际输出一致）；解析失败才退到 Main@L4.0 兜底并打日志。
            //   旧版硬编码 'avc1.4D4028'，与服务端 profile/level 不符时
            //   decoder.configure 直接 NotSupportedError。
            const cfg = {
              codec: codec || 'avc1.4D4028',
              optimizeForLatency: true     // 直播：不攒帧，低延迟优先
            };
            try {
              decoder.configure(cfg);
              configured = true;
            } catch (e) {
              // 配置不支持（老设备的硬解不支持该 profile）→ 报错并重试
              fail('[H264] decoder.configure 失败:', e);
              return;
            }
          }

          if (!configured) continue;       // 等 SPS

          // ── AU 边界判定：SPS/PPS/新 VCL = 新 AU 开始 ──
          if (isVcl && auHasData) flushAu();   // 新图像数据 → 先结上一 AU
          if (isSps || isPps) {
            // 参数集挂在「即将开始的 AU」上：若已攒着 AU 尾巴，先结掉
            if (auHasData) flushAu();
          }

          // ★ 从 IDR 起播：重连后第一个 key 之前的 delta 一律丢弃 ——
          //   半个 GOP 的 delta 帧缺参考帧，喂进去只会花屏/报错。
          if (!firstKey && !isKey && !isSps && !isPps) continue;
          if (isKey) firstKey = true;

          auNals.push(nal);
          if (isVcl) auHasData = true;   // ★ 只有 VCL 算「AU 有图像」；SPS/PPS/SEI
                                        //   是参数，单独不能成 AU（否则会 flush
                                        //   出没有图像数据的非法 chunk）
          if (isKey) auHasKey = true;
        }
        // network chunk 边界不等于 AU 边界，攒批的 AU 留到下一个 VCL 到达
        // 或流结束时 flush（见下方 done 分支）。
      }
      // 流结束：把攒着的最后一个 AU 喂掉
      if (myGen === h264Gen) {
        flushAu();
        if (skippedTotal > 0) console.info('[H264] 本轮累计跳帧 ' + skippedTotal + ' 个 AU（解码积压保护）');
        fail('[H264] 流结束', null);
      }
    } catch (e) {
      fail('[H264] 流断开:', e);
    } finally {
      if (stallTimer) { clearInterval(stallTimer); stallTimer = null; }
      if (myGen === h264Gen) {
        h264Active = false;
        h264Reader = null;
      }
      try { if (decoder && decoder.state !== 'closed') decoder.close(); } catch (e) {}
    }
  }

// ── 输入 ────────────────────────────────────────────────────
// ★ 2026-11-23 输入链路修复（INPUT-CHANNEL-GAP-20261123.md）：
//   · down/up/click 走**同一 FIFO 串行队列**（sendInputOrdered）→ 保序，
//     替代旧「每个 pointer 事件一次独立 fetch 并发无序」导致的 up 丢失/粘键；
//   · up/releaseall 传输失败重试 1 次（幂等，重发无害）；down/click 不重试（避免双按/双击）；
//   · move 走 noWait 不等回包（RTT 不再拖慢拖动，latest-wins 由 movePending 保留）；
//   · 页面 pagehide/beforeunload/visibilitychange(hidden) → releaseAllInputs 防粘键。
async function postInputRaw(body) {
  const base = directBase(cur);
  const url = base ? (base + '/api/input') : ('/api/input?sid=' + cur.sessionId);
  const headers = { 'Content-Type': 'application/json' };
  if (base && directToken()) headers['X-SoulLink-Token'] = directToken();
  const r = await fetch(url, {
    method: 'POST', headers: headers,
    body: JSON.stringify(Object.assign({}, body, base ? { sid: cur.sessionId } : {}))
  });
  return r.json();
}

// 通用发送（仍可 await 结果；wheel/key/moverel 等非保序、非高频动作沿用）
async function sendInput(action, nx, ny, extra) {
  if (!cur) return null;
  try { return await postInputRaw(Object.assign({ action, x: nx, y: ny }, extra || {})); }
  catch { return null; }
}

// ── 按键状态动作的 FIFO 串行队列（保序）──────────────────────
// down/up/click 是「状态转移」，必须严格按产生顺序送达代理：
//   拖动 = down → (n×move) → up，若 down/up 并发乱序，远端会停在按下态。
// 这里所有 down/up/click 进同一个队列，单 in-flight 逐条 await 发送，天然保序。
let inputQueue = [];           // FIFO：{ body, resolve }
let inputSending = false;      // 队列是否正在发送头部
const INPUT_RETRYABLE = new Set(['up', 'releaseall']);   // 幂等：重发无害

function sendInputOrdered(action, nx, ny, extra) {
  if (!cur) return Promise.resolve(null);
  const body = Object.assign({ action, x: nx, y: ny }, extra || {});
  return new Promise(resolve => {
    inputQueue.push({ body, resolve });
    pumpInput();
  });
}

function pumpInput() {
  if (inputSending || inputQueue.length === 0) return;
  const item = inputQueue.shift();
  inputSending = true;
  (async () => {
    let resp = null;
    try { resp = await postInputRaw(item.body); }
    catch (e) { resp = null; }
    // up/releaseall 幂等：传输失败（fetch 抛异常）重试 1 次；
    // down/click 不重试——down 重试可能双 LEFTDOWN 反而粘键，click 重试会双击。
    if (resp == null && INPUT_RETRYABLE.has(item.body.action)) {
      try { resp = await postInputRaw(item.body); }
      catch (e) { resp = null; }
    }
    if (resp == null || resp.ok === false) {
      console.warn('[input] ' + item.body.action + ' 发送失败', resp);
    }
    item.resolve(resp);
    inputSending = false;
    pumpInput();               // 串行发下一条
  })();
}

// 单向发送：fire-and-forget，不等回包（move/moverel 高频位移专用）
function sendInputNoWait(action, nx, ny, extra) {
  if (!cur) return;
  const body = Object.assign({ action, x: nx, y: ny }, extra || {});
  postInputRaw(body).catch(() => {});
}

// ── 按住心跳（Web 前端断连看门狗的「我还活着」信号）────────────
// 代理端只能通过「是否有输入活动」判断前端是否断连；但「按住不动」是正常操作，
// 会产生 0 输入活动。所以按住期间每 2s 发一次 noop 心跳，代理据此区分：
//   活着但静止按住（有心跳） ≠ 前端已断开（心跳停了 → 看门狗释放）。
// 阈值：代理 WebIdleReleaseMs=5000 > 2×2000，静止按住不会误释放。
function hasHeldInput() {
  if (drag && drag.moved) return true;
  if (look && look.mode === 'drag') return true;
  return false;
}
let heldHeartbeat = null;
function ensureHeldHeartbeat() {
  if (heldHeartbeat) return;
  heldHeartbeat = setInterval(() => {
    if (hasHeldInput()) sendInputNoWait('noop', -1, -1, {});
  }, 2000);
}
ensureHeldHeartbeat();

// ★ 释放所有按住的键：切后台/刷新/关页时兜底（防「松手远端没松开」）。
//   直接 fire-and-forget + keepalive（卸载阶段仍能送达）；幂等，重发无害。
function releaseAllInputs() {
  if (!cur) return;
  inputQueue.length = 0;       // 丢弃未发送的 down/up/click，避免它们在 releaseall 之后到达又粘键
  const base = directBase(cur);
  const body = Object.assign({ action: 'releaseall', x: -1, y: -1 }, base ? { sid: cur.sessionId } : {});
  const url = base ? (base + '/api/input') : ('/api/input?sid=' + cur.sessionId);
  const headers = { 'Content-Type': 'application/json' };
  if (base && directToken()) headers['X-SoulLink-Token'] = directToken();
  try {
    fetch(url, { method: 'POST', headers: headers, body: JSON.stringify(body), keepalive: true }).catch(() => {});
  } catch (e) {}
}

function norm(e) {
  // ★ 画面唯一来源是 <canvas id="vframe">（JPEG img 已随轮询通路一起移除）。
  //   canvas.width/height 是解码帧的原始分辨率；还没出画面时退回 16:9 兜底，
  //   此时点击坐标本来就不该生效（ph 提示层挡着）。
  const cv = $('vframe');
  const iw = (cv && cv.width > 0) ? cv.width : 16;
  const ih = (cv && cv.height > 0) ? cv.height : 9;
  // ★ 2026-11-24 修落点偏移：用 canvas 自身实际显示矩形（含 max-height 夹住 +
  //   object-fit letterbox 黑边），而非 #scr 的 rect。#scr 高度可能 > canvas 显示高度，
  //   旧实现按 #scr 算居中偏移 → 纵向落点系统性偏移（见 CURSOR-AND-POINTER-LOCK）。
  const rect = cv && cv.classList.contains('show') ? cv.getBoundingClientRect() : $('scr').getBoundingClientRect();
  // canvas 内 object-fit:contain 的黑边：视频内容区 = min(rect.w, rect.h * 比例)
  const dwContent = Math.min(rect.width, rect.height * iw / ih);
  const dhContent = Math.min(rect.height, rect.width * ih / iw);
  const ox = (rect.width - dwContent) / 2, oy = (rect.height - dhContent) / 2;
  const cx = (e.touches ? e.touches[0].clientX : e.clientX) - rect.left - ox;
  const cy = (e.touches ? e.touches[0].clientY : e.clientY) - rect.top - oy;
  return { nx: Math.max(0, Math.min(1, cx / dwContent)), ny: Math.max(0, Math.min(1, cy / dhContent)) };
}

// ══════════ 可见光标指示（2026-11-24）═════════════
//   绝对模式（非锁定态）跟随鼠标 move 显示小圆点；锁定态/触屏隐藏。
//   复用 norm 的同一套 canvas 实际矩形 + object-fit 黑边，保证指示位置 = 实际落点。
const plCursorEl = $('plCursor');
function plCursorGeometry() {
  const cv = $('vframe');
  const iw = (cv && cv.width > 0) ? cv.width : 16;
  const ih = (cv && cv.height > 0) ? cv.height : 9;
  const rect = cv && cv.classList.contains('show') ? cv.getBoundingClientRect() : $('scr').getBoundingClientRect();
  const dwContent = Math.min(rect.width, rect.height * iw / ih);
  const dhContent = Math.min(rect.height, rect.width * ih / iw);
  const ox = (rect.width - dwContent) / 2, oy = (rect.height - dhContent) / 2;
  return { rect, ox, oy, dwContent, dhContent };
}
function updateVisibleCursor(nx, ny) {
  if (!plCursorEl) return;
  const g = plCursorGeometry();
  // ★ 相对 #scr：cursor 是 #scr 的绝对定位子元素，需把 rect(视口坐标) 转 #scr 局部坐标
  const scrRect = $('scr').getBoundingClientRect();
  const x = (g.rect.left - scrRect.left) + g.ox + nx * g.dwContent;
  const y = (g.rect.top - scrRect.top) + g.oy + ny * g.dhContent;
  plCursorEl.style.left = x + 'px';
  plCursorEl.style.top = y + 'px';
  plCursorEl.style.display = 'block';
}
function hideVisibleCursor() {
  if (plCursorEl) plCursorEl.style.display = 'none';
}

function ripple(e) {
  const el = $('rip'), rect = $('scr').getBoundingClientRect();
  el.style.left = ((e.touches ? e.touches[0].clientX : e.clientX) - rect.left) + 'px';
  el.style.top = ((e.touches ? e.touches[0].clientY : e.clientY) - rect.top) + 'px';
  el.classList.remove('go'); void el.offsetWidth; el.classList.add('go');
}

let drag = null;

// ══════════ 鼠标模式：绝对 / 相对（月光式，2026-11-23）══════════
//   绝对（默认）= 现状：点哪指哪 + 长按拖动（触屏状态机原样）。
//   相对 = 月光语义：单指滑动 → moverel 转视角；单指点一下 → 绝对定位 + 左键点击；
//           双指 → 滚轮（上下滑）/ 右键（点一下）。
//   选择持久化到 localStorage，退出画面/刷新后保留。
const MOUSE_MODE_KEY = 'sl_mouse_mode';
let mouseMode = 'absolute';    // 'absolute' | 'relative'
try {
  const m = localStorage.getItem(MOUSE_MODE_KEY);
  if (m === 'relative' || m === 'absolute') mouseMode = m;
} catch (e) {}

function applyMouseMode() {
  const abs = $('mmAbs'), rel = $('mmRel');
  if (abs) abs.classList.toggle('on', mouseMode === 'absolute');
  if (rel) rel.classList.toggle('on', mouseMode === 'relative');
}
const __mmAbs = $('mmAbs'), __mmRel = $('mmRel');
if (__mmAbs) __mmAbs.onclick = () => { mouseMode = 'absolute'; try { localStorage.setItem(MOUSE_MODE_KEY, 'absolute'); } catch (e) {} applyMouseMode(); };
if (__mmRel) __mmRel.onclick = () => { mouseMode = 'relative'; try { localStorage.setItem(MOUSE_MODE_KEY, 'relative'); } catch (e) {} applyMouseMode(); };
applyMouseMode();

// ══════════ Pointer Lock（桌面鼠标转视角，2026-11-23）═════════════
// ★ 对齐 POINTER-LOCK-DESIGN-20261123.md + decision/pointer-lock-esc-shortcut。
//   进入：桌面左键点画面 → requestPointerLock()，成功 pointerLocked=true，光标隐藏（浏览器自动）。
//   转视角：锁定态 mousemove 的 movementX/Y × sensitivity → 复用 lookDelta/lookAcc/flushLook 8ms 合并。
//   点击：复用现有 pointerdown/up drag 状态机（锁定态 pointermove 被跳过，单击=click）。
//   逃生门：ESC = 浏览器强制解锁 + 补发一次 Esc 开菜单（方案 C）；自定义快捷键 = 纯解锁不弹菜单。
const PL_SENS_KEY = 'sl_pl_sens';
const PL_ESC_KEY = 'sl_pl_escape';
const PL_SUPPORTED = ('pointerLockElement' in document) && ('requestPointerLock' in document.body);
let pointerLocked = false;
let plLockPending = null;      // 首击挂起：{nx,ny} 待锁定结果决定是否补发 click
let plExitByShortcut = false;    // 快捷键逃生门主动退出（不补发 ESC）
let plExitByLifecycle = false;   // 生命周期/断线/关画面主动退出（不补发 ESC）
let plLockCooldownUntil = 0;     // Esc 后重锁冷却（规范：立即重锁会被拒）
let plSensitivity = 1.0;         // movementX/Y 灵敏度（浏览器单位不统一）
let plEscape = 'ctrl_alt_shift'; // 逃生门快捷键
let plAltHoldTimer = null;       // 长按 Alt 计时器

try {
  const s = parseFloat(localStorage.getItem(PL_SENS_KEY));
  if (isFinite(s) && s >= 0.1 && s <= 3) plSensitivity = s;
  const esc = localStorage.getItem(PL_ESC_KEY);
  if (esc === 'ctrl_alt_shift' || esc === 'alt' || esc === 'alt_hold' || esc === 'ctrl_alt') plEscape = esc;
} catch (e) {}

function applyPlEscape() {
  const el = $('plEscape');
  if (el) el.value = plEscape;
  const r = $('plSens');
  if (r) r.value = String(plSensitivity);
  const sv = $('plSensV');
  if (sv) sv.textContent = plSensitivity.toFixed(1);
}

// 逃生门快捷键命中判定（全部为修饰键组合，不会误注入远端——现有 keydown 会过滤纯修饰键）
function matchPlEscape(e) {
  if (plEscape === 'ctrl_alt_shift') return e.ctrlKey && e.altKey && e.shiftKey;
  if (plEscape === 'ctrl_alt') return e.ctrlKey && e.altKey;
  if (plEscape === 'alt') return e.altKey && (e.key === 'Alt');
  return false;
}

document.addEventListener('keydown', e => {
  if (!pointerLocked) return;
  // 长按 Alt：按下即计时，800ms 后仍按住 → 触发（alt_hold 不走 matchPlEscape）
  if (plEscape === 'alt_hold' && e.key === 'Alt' && !e.repeat) {
    if (plAltHoldTimer) clearTimeout(plAltHoldTimer);
    plAltHoldTimer = setTimeout(() => {
      plAltHoldTimer = null;
      if (pointerLocked) doPlShortcutExit();
    }, 800);
    return;
  }
  if (plEscape !== 'alt_hold' && matchPlEscape(e)) {
    doPlShortcutExit();
  }
});

document.addEventListener('keyup', e => {
  if (e.key === 'Alt' && plAltHoldTimer) { clearTimeout(plAltHoldTimer); plAltHoldTimer = null; }
});

function doPlShortcutExit() {
  if (!pointerLocked) return;
  plExitByShortcut = true;
  try { document.exitPointerLock(); } catch (err) {}
}

// 生命周期/断线/关画面主动退出锁定（不补发 ESC，不弹菜单）
function plLifecycleExit() {
  if (pointerLocked) {
    plExitByLifecycle = true;
    try { document.exitPointerLock(); } catch (err) {}
  }
  if (plAltHoldTimer) { clearTimeout(plAltHoldTimer); plAltHoldTimer = null; }
}

document.addEventListener('pointerlockchange', () => {
  const nowLocked = document.pointerLockElement === $('scr');
  if (nowLocked && !pointerLocked) {
    // 锁定成功
    pointerLocked = true;
    plExitByShortcut = false;
    plExitByLifecycle = false;
    plLockPending = null;    // 首击挂起 → 吃掉（对齐月光：进锁定那次不点远端）
    hideVisibleCursor();     // 锁定态隐藏落点指示（转视角无落点概念）
    return;
  }
  if (!nowLocked && pointerLocked) {
    // 解锁
    pointerLocked = false;
    if (plAltHoldTimer) { clearTimeout(plAltHoldTimer); plAltHoldTimer = null; }
    // 冲刷残留相对位移
    if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }
    flushLook();
    const byShortcut = plExitByShortcut;
    const byLifecycle = plExitByLifecycle;
    plExitByShortcut = false;
    plExitByLifecycle = false;
    // ★ 方案 C：ESC 一键开菜单。排除法判定触发源：
    //   非快捷键退出、非生命周期退出、且页面仍聚焦 → 只剩浏览器 ESC 强制解锁 → 补发 Esc 开菜单。
    if (!byShortcut && !byLifecycle && document.hasFocus()) {
      sendInput('key', -1, -1, { key: 'Esc' });
    }
    // Esc 后冷却：规范要求 default unlock gesture 后立即重锁会被拒
    plLockCooldownUntil = Date.now() + 500;
    hideVisibleCursor();     // 解锁后隐藏（等下次 pointermove 再按落点显示）
    return;
  }
});

document.addEventListener('pointerlockerror', () => {
  // 请求锁定失败（非手势/不支持/冷却期内）：保持未锁定，并补发挂起的首击
  if (!document.pointerLockElement) pointerLocked = false;
  if (plLockPending) {
    const p = plLockPending; plLockPending = null;
    sendInputOrdered('click', p.nx, p.ny, { button: 'left' });
  }
});

// 锁定态 mousemove：movementX/Y × sensitivity → 复用 lookDelta 8ms 合并
document.addEventListener('mousemove', e => {
  if (!pointerLocked) return;
  if (e.movementX || e.movementY) {
    lookDelta(e.movementX * plSensitivity, e.movementY * plSensitivity);
  }
});

// 设置控件绑定
const __plEscape = $('plEscape'), __plSens = $('plSens');
if (__plEscape) __plEscape.onchange = () => { plEscape = __plEscape.value; try { localStorage.setItem(PL_ESC_KEY, plEscape); } catch (e) {} };
if (__plSens) __plSens.oninput = () => { plSensitivity = parseFloat(__plSens.value) || 1.0; const sv = $('plSensV'); if (sv) sv.textContent = plSensitivity.toFixed(1); try { localStorage.setItem(PL_SENS_KEY, String(plSensitivity)); } catch (e) {} };
applyPlEscape();
// 相对模式手势状态（月光：单指滑动转视角 / 点按点击 / 双指滚轮或右键）
const REL_TAP_PX = 10;
let relPoints = new Map();   // pointerId -> { sx, sy, lx, ly, total }
let relWasTwo = false;       // 本次手势是否出现过双指
let relMaxMove = 0;          // 本次手势最大累计位移（判定「点一下」）
let relPinchY = null;        // 双指中点 Y（滚轮用）
function relReset() {
  relPoints.clear();
  relWasTwo = false;
  relMaxMove = 0;
  relPinchY = null;
}

// ══════════ 触屏手势状态机（手机转视角/拖动/点击，2026-11-22）══════════
// 判定依据（成熟手游标准方案，用户确认）：
//   点一下屏幕（按下即抬起，几乎没动）      → 左键单击
//   按下后**立即**滑动（长按窗口内越过位移阈值）→ 转视角（moverel 相对位移）
//   按住不动超过长按阈值后再滑动           → 左键按住拖动（down + move + up）
//   双指                                    → 滚轮/缩放（未实现，见 LOOK-OPTIMIZE 文档）
//
// ★ 转视角发 moverel 相对位移、不碰按键；拖动发 down + 绝对 move + up。
//   依据：游戏转视角只认相对位移（绝对 SetCursorPos 不转视角，见 HANDOFF 4.2.6），
//         而拖物品/拖地图是"光标跟随手指"，用绝对坐标。
// ★ 游戏内打开地图时滑动不会转视角 —— 游戏自己处理 UI 层，我们不用管。
let look = null;            // 触屏滑动状态 { id, sx, sy, mode, lastX, lastY, nx, ny, tapTimer }
const LOOK_TAP_MS = 250;    // 长按阈值：按住不动超过它 = 进入拖动（可调范围 200~300ms）
const LOOK_SLIDE_PX = 10;   // 位移阈值：抖动/误触容差（手指抖动比鼠标的 8px 更大）

// 转视角位移累积 + 节流（合并后发 moverel，避免手指高频抖动打爆代理有界队列）
let lookAcc = { dx: 0, dy: 0, lastSent: 0, timer: null };
const LOOK_HZ_MS = 8;       // 合并窗口：与桌面 LookInputQueue 的 8ms 对齐

function flushLook() {
  const d = lookAcc.dx, y = lookAcc.dy;
  lookAcc.dx = lookAcc.dy = 0;
  if (d || y) sendInput('moverel', -1, -1, { dx: d, dy: y });
}
function lookDelta(dx, dy) {
  lookAcc.dx += dx; lookAcc.dy += dy;
  const now = Date.now();
  if (now - lookAcc.lastSent >= LOOK_HZ_MS) {
    lookAcc.lastSent = now;
    if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }
    flushLook();
  } else if (!lookAcc.timer) {
    lookAcc.timer = setTimeout(() => {
      lookAcc.timer = null;
      lookAcc.lastSent = Date.now();
      flushLook();
    }, LOOK_HZ_MS - (now - lookAcc.lastSent));
  }
}

$('scr').addEventListener('pointerdown', e => {
  if (e.pointerType === 'touch') {
    // ★ 相对模式：走月光手势（滑动转视角 / 点按点击 / 双指滚轮·右键）
    if (mouseMode === 'relative') {
      if (relPoints.size === 0) { relWasTwo = false; relMaxMove = 0; relPinchY = null; }
      relPoints.set(e.pointerId, { sx: e.clientX, sy: e.clientY, lx: e.clientX, ly: e.clientY, total: 0 });
      if (relPoints.size >= 2) { relWasTwo = true; relPinchY = null; }
      $('scr').setPointerCapture(e.pointerId);
      return;
    }
    // ★ 命中虚拟按钮的区域由按钮层自己吃掉（阻止事件穿透），走不到这里。
    //   走到这里 = 落在空白处：记录起点，进入 pending 状态。
    const p = norm(e);
    look = { id: e.pointerId, sx: e.clientX, sy: e.clientY, mode: 'pending',
             lastX: e.clientX, lastY: e.clientY, nx: p.nx, ny: p.ny, tapTimer: null };
    look.tapTimer = setTimeout(() => {
      // 长按阈值内没怎么动 → 进入拖动模式：先按住左键
      if (look && look.mode === 'pending') {
        look.mode = 'drag';
        sendInputOrdered('down', look.nx, look.ny, { button: 'left' });
      }
    }, LOOK_TAP_MS);
    $('scr').setPointerCapture(e.pointerId);
    return;
  }

  // 鼠标 / 触控笔：保持桌面原有行为不变
  const btn = e.button === 2 ? 'right' : (e.button === 1 ? 'middle' : 'left');
  // ★ Pointer Lock（2026-11-24 修吞点击死循环）：桌面左键且未锁定 → 发起锁定请求并挂起首击。
  //   requestPointerLock 是异步，成功/失败由 pointerlockchange/error 决定：
  //     · 锁定成功 → 吃掉首击（不补发 click，对齐月光）
  //     · 锁定失败 → 补发 click（回退绝对模式，本次点击不丢）
  //   挂起期间 return，绝不吞掉首击；失败后 pointerLocked 保持 false，下次点击重试。
  if (e.pointerType === 'mouse' && btn === 'left' && PL_SUPPORTED && window.isSecureContext
      && !pointerLocked && Date.now() >= plLockCooldownUntil) {
    const p0 = norm(e);
    plLockPending = { nx: p0.nx, ny: p0.ny };
    try { $('scr').requestPointerLock(); } catch (err) {}
    return;
  }
  const p = norm(e);
  // ★ 右键 = 转视角模式（与桌面版 RdpConsole「按住右键拖动」一致）：
  //   按住右键 + 移动 → 复用触屏的 lookDelta/flushLook（8ms 合并 + moverel）。
  //   游戏只认相对位移（Raw Input），绝对坐标 SetCursorPos 不转视角，
  //   所以这里绝不发 move/down，只发 moverel —— 见 HANDOFF 4.2.6。
  //   右键单击（按下即抬起没怎么动）仍发 click right，见 pointerup。
  if (btn === 'right') {
    look = { id: e.pointerId, sx: e.clientX, sy: e.clientY, mode: 'look',
             lastX: e.clientX, lastY: e.clientY, nx: p.nx, ny: p.ny,
             tapTimer: null, mouseRight: true };
    $('scr').setPointerCapture(e.pointerId);
    return;
  }
  drag = { sx: e.clientX, sy: e.clientY, moved: false, nx: p.nx, ny: p.ny, btn };
  $('scr').setPointerCapture(e.pointerId);
});
// ★★ 鼠标移动：latest-wins 单 in-flight（2026-11-23 重写）★★
//
//   旧版问题（用户实测「点右键要 5-7 秒才见菜单」的帮凶之一）：
//     · 节流窗口 = liveMs（画面帧间隔，15fps≈66ms）被夹到 50ms ——
//       输入响应被画面帧率拖累，这个耦合本来就是错的；
//     · 每个 pointermove 独立 fetch，快速滑动并发几十个请求，
//       浏览器每域 6 连接 → 点击类请求排在 move 后面排队。
//
//   新版：与 lookAcc(moverel) 同源的「合并 + 8ms 节流」策略——
//     · pointermove 只更新 pending（绝不发 fetch）；
//     · move 走 noWait 不等回包（RTT 不再拖慢拖动）；
//     · latest-wins：movePending 永远只记最新位置，中间位置不发；
//     · 8ms 节流窗口与画面帧率完全解耦（MOVE_MIN_INTERVAL_MS）。
//   实测（60Hz 滑动 3s、RTT 3ms）：旧版 3s 只发 46 个请求（≈15/s，
//   全被 50ms 窗口夹死）；新版跟随到 125Hz 且并发恒为 1。
const MOVE_MIN_INTERVAL_MS = 8;   // 与 LOOK_HZ_MS 对齐；125Hz 上限足够顺滑
let movePending = null;           // 待发的最新位置（latest-wins）
let moveLastSentAt = 0;           // 上次实际发出的时刻（节流用）

function sendMoveThrottled(nx, ny) {
  movePending = { nx, ny };       // 永远只记最新位置，旧的自然被覆盖
  pumpMove();
}

function pumpMove() {
  if (!movePending) return;                      // 有货才发；move 已改 noWait，不再有 in-flight 等待
  const now = Date.now();
  const wait = MOVE_MIN_INTERVAL_MS - (now - moveLastSentAt);
  if (wait > 0) {                                 // 节流窗口内：只排一个 timer
    if (!pumpMove._t) {
      pumpMove._t = setTimeout(() => { pumpMove._t = null; pumpMove(); }, wait);
    }
    return;
  }
  const p = movePending; movePending = null;
  moveLastSentAt = Date.now();
  // ★ noWait：不等回包，仅靠 8ms 节流 + timer 控制频率；latest-wins 由 movePending 保留。
  //   单 in-flight 语义改为「节流窗口内最多排一条」——避免 RTT 拖慢拖动（WAN 下旧实现退化 1/RTT）。
  sendInputNoWait('move', p.nx, p.ny);
}

$('scr').addEventListener('pointermove', e => {
  // ★ 锁定态：转视角已由 document mousemove（movementX/Y）处理；clientX/Y 冻结为常量，
  //   跳过绝对 move，避免重复发固定坐标。点击（down/up/click）仍走下面状态机。
  if (pointerLocked && e.pointerType === 'mouse') return;
  // ★ 可见光标：非锁定态 + 非触屏 → 跟随 move 显示落点指示
  if (!pointerLocked && e.pointerType !== 'touch') {
    const pc = norm(e);
    updateVisibleCursor(pc.nx, pc.ny);
  }
  // ── 相对模式（月光）：单指滑动转视角 / 双指滚轮 ──
  if (e.pointerType === 'touch' && mouseMode === 'relative' && relPoints.has(e.pointerId)) {
    const pt = relPoints.get(e.pointerId);
    const dx = e.clientX - pt.lx, dy = e.clientY - pt.ly;
    pt.lx = e.clientX; pt.ly = e.clientY;
    pt.total += Math.hypot(dx, dy);
    relMaxMove = Math.max(relMaxMove, pt.total);
    if (relPoints.size >= 2) {
      const pts = [...relPoints.values()];
      const my = (pts[0].ly + pts[1].ly) / 2;
      if (relPinchY !== null) {
        const steps = Math.round((my - relPinchY) / 8);
        if (steps !== 0) sendInput('wheel', -1, -1, { delta: steps * 120 });
      }
      relPinchY = my;
      return;
    }
    lookDelta(dx, dy);   // 单指滑动 → moverel 转视角（复用 8ms 合并）
    return;
  }
  // ── 触屏 / 鼠标右键：转视角 / 拖动状态机 ──
  // （mouseRight 的右键 look 直接进 look 模式：桌面版没有「先长按判定」，
  //   按住右键滑动就是转视角，位移阈值只防抖动误触）
  if (look && e.pointerId === look.id) {
    const dx = e.clientX - look.lastX, dy = e.clientY - look.lastY;
    look.lastX = e.clientX; look.lastY = e.clientY;

    if (look.mode === 'pending') {
      const moved = Math.hypot(e.clientX - look.sx, e.clientY - look.sy);
      if (moved < LOOK_SLIDE_PX) return;   // 抖动容差，不算滑动
      // 长按窗口内越过位移阈值 → 转视角
      look.mode = 'look';
      if (look.tapTimer) { clearTimeout(look.tapTimer); look.tapTimer = null; }
      lookDelta(dx, dy);
      return;
    }
    if (look.mode === 'look') {
      // ★ 鼠标右键没有「点一下=click」的需求冲突：滑动即转视角。
      //   但要防抖：几乎没动（<1px）就不发，避免点住不动时狂发 0 位移。
      if (look.mouseRight && Math.abs(dx) + Math.abs(dy) < 1) return;
      lookDelta(dx, dy);
      return;
    }
    if (look.mode === 'drag') {
      // 拖动：光标跟随手指，用绝对坐标（拖物品/地图）
      const p = norm(e);
      sendMoveThrottled(p.nx, p.ny);
      return;
    }
    return;
  }

  // ── 鼠标 / 触控笔：保持桌面原有行为 ──
  const p = norm(e);
  if (!drag) { sendMoveThrottled(p.nx, p.ny); return; }

  const dx = e.clientX - drag.sx, dy = e.clientY - drag.sy;
  if (!drag.moved) {
    if (Math.hypot(dx, dy) < 8) {
      sendMoveThrottled(p.nx, p.ny);
      return;
    }
    drag.moved = true;
    sendInputOrdered('down', drag.nx, drag.ny, { button: drag.btn });
  }
  sendMoveThrottled(p.nx, p.ny);
});
$('scr').addEventListener('pointerup', e => {
  // ── 相对模式（月光）：点一下=绝对定位+左键；双指点一下=右键 ──
  if (e.pointerType === 'touch' && mouseMode === 'relative' && relPoints.has(e.pointerId)) {
    relPoints.delete(e.pointerId);
    if (relPoints.size < 2) relPinchY = null;
    if (relPoints.size === 0) {
      const p = norm(e);
      if (relWasTwo) {
        if (relMaxMove < REL_TAP_PX) { ripple(e); sendInputOrdered('click', p.nx, p.ny, { button: 'right' }); }
      } else {
        if (relMaxMove < REL_TAP_PX) { ripple(e); sendInputOrdered('click', p.nx, p.ny, { button: 'left' }); }
      }
      if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }
      flushLook();
      relReset();
    }
    return;
  }
  // ── 触屏 / 鼠标右键 ──
  if (look && e.pointerId === look.id) {
    const was = look; look = null;
    if (was.tapTimer) { clearTimeout(was.tapTimer); lookAcc.timer && clearTimeout(lookAcc.timer), lookAcc.timer = null; }
    if (was.mode === 'pending') {
      // 没动就抬起 → 左键单击
      const p = norm(e);
      ripple(e); sendInputOrdered('click', p.nx, p.ny, { button: 'left' });
    } else if (was.mode === 'drag') {
      sendInputOrdered('up', -1, -1, { button: 'left' });
    } else if (was.mode === 'look' && was.mouseRight) {
      // ★ 鼠标右键转视角结束：几乎没动 = 右键单击（菜单照常出）；
      //   拖动过 = 纯转视角，不发 click（不然转完视角还会弹出菜单）。
      const movedTotal = Math.hypot(e.clientX - was.sx, e.clientY - was.sy);
      if (movedTotal < LOOK_SLIDE_PX) {
        const p = norm(e);
        ripple(e); sendInputOrdered('click', p.nx, p.ny, { button: 'right' });
      }
    }
    // 转视角模式：最后残留的位移已由 timer 冲刷，无需额外动作
    if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }
    flushLook();
    return;
  }

  // ── 鼠标 / 触控笔 ──
  // ★ 首击挂起兜底：requestPointerLock 既未成功也未报错（罕见），pointerup 时补发 click
  if (!drag && plLockPending) {
    const p = plLockPending; plLockPending = null;
    sendInputOrdered('click', p.nx, p.ny, { button: 'left' });
    return;
  }
  if (!drag) return;
  const moved = drag.moved, btn = drag.btn || 'left';
  drag = null;
  const p = norm(e);
  if (moved) sendInputOrdered('up', p.nx, p.ny, { button: btn });
  else { ripple(e); sendInputOrdered('click', p.nx, p.ny, { button: btn }); }
});
$('scr').addEventListener('pointercancel', () => {
  if (mouseMode === 'relative' && relPoints.size > 0) {
    if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }
    flushLook();
    relReset();
    return;
  }
  if (look) {
    if (look.mode === 'drag') sendInputOrdered('up', -1, -1, { button: 'left' });
    if (look.mode === 'look' && look.mouseRight) {
      // 右键转视角被系统打断：不补发 click（无法区分用户意图），只退出
      if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }
      flushLook();
      look = null;
      return;
    }
    look = null;
    if (lookAcc.timer) { clearTimeout(lookAcc.timer); lookAcc.timer = null; }
    flushLook();
    return;
  }
  if (drag && drag.moved) sendInputOrdered('up', -1, -1, { button: drag.btn || 'left' });
  drag = null;
});
$('scr').addEventListener('wheel', e => {
  e.preventDefault();
  sendInput('wheel', -1, -1, { delta: e.deltaY > 0 ? -120 : 120 });
}, { passive: false });
$('scr').addEventListener('contextmenu', e => e.preventDefault());
$('scr').addEventListener('dblclick', e => e.preventDefault());
$('scr').addEventListener('selectstart', e => e.preventDefault());

function keyName(e) {
  const k = e.key;
  const map = { ' ': 'Space', 'Enter': 'Enter', 'Escape': 'Esc', 'Backspace': 'Backspace',
    'Tab': 'Tab', 'Delete': 'Delete', 'ArrowUp': 'Up', 'ArrowDown': 'Down',
    'ArrowLeft': 'Left', 'ArrowRight': 'Right', 'Home': 'Home', 'End': 'End',
    'PageUp': 'PageUp', 'PageDown': 'PageDown' };
  if (map[k]) return map[k];
  if (k.length === 1) return k.toUpperCase();
  return k;
}
document.addEventListener('keydown', e => {
  if (!kbMode || e.target.tagName === 'INPUT') return;
  if (['Control','Alt','Shift','Meta'].includes(e.key)) return;
  // 锁定态 ESC 由 pointerlockchange 统一补发（浏览器解锁 + 补发一次 Esc 开菜单）；
  // 这里拦掉，避免「ESC keydown 派发时」键盘注入又发一次导致双重开菜单。
  if (pointerLocked && e.key === 'Escape') return;
  const parts = [];
  if (e.ctrlKey) parts.push('Ctrl');
  if (e.altKey) parts.push('Alt');
  if (e.shiftKey) parts.push('Shift');
  parts.push(keyName(e));
  e.preventDefault();
  sendInput('key', -1, -1, { key: parts.join('+') });
});

$('closeViewer').onclick = closeViewer;

// 录像回放：点了才展开 + 才取数据（避免打开画面时白扫一遍磁盘）
const savedToggle = $('savedToggle');
if (savedToggle) savedToggle.onclick = () => {
  const wrap = $('savedWrap');
  const open = savedToggle.classList.toggle('open');
  if (wrap) wrap.style.display = open ? 'block' : 'none';
  if (open) ensureSaved();     // ← 只有展开时才请求 /api/list
};

// ── 录制参数：与应用设置双向同步（3.3.12）──────────────────
//
//   ★ 用户要求：「把这些设置都加到浏览器，两边同步」
//
//   单向下发是不够的 —— 网页改了也要回写应用配置，
//   并且立刻下发给正在录制的代理（不用等用户去应用里重开一次）。
function applySettings(d) {
  if (typeof d.recordFps === 'number' && d.recordFps > 0) recFps = d.recordFps;
  if (typeof d.recordQuality === 'number' && d.recordQuality > 0) recQuality = d.recordQuality;
  if (typeof d.recordKeepSeconds === 'number' && d.recordKeepSeconds > 0) recKeep = d.recordKeepSeconds;
  if (typeof d.streamFps === 'number' && d.streamFps > 0) streamFps = d.streamFps;
  if (typeof d.videoBitrate === 'number' && d.videoBitrate > 0) videoBitrate = d.videoBitrate;
  if (typeof d.nvencPreset === 'number' && d.nvencPreset > 0) nvencPreset = d.nvencPreset;
  if (typeof d.vbvBufferIncreasePercent === 'number') vbvBufferIncreasePercent = d.vbvBufferIncreasePercent;
  if (typeof d.captureMode === 'number') captureMode = d.captureMode;

  // viewFps 从 localStorage 恢复（2026-11-23 起仅用于鼠标移动节流，
  // 「取图」下拉框已随 JPEG 轮询通路移除）
  try {
    const v = localStorage.getItem(VIEWFPS_KEY);
    if (v !== null) viewFps = parseInt(v) || 0;
  } catch (e) { }

  computeLiveMs();
  syncDropdowns();
}

// 把当前值回填到下拉菜单（值不在预设列表里就动态补一个选项）
function syncDropdowns() {
  // 先清掉上次补的动态选项，避免反复变化时累积
  document.querySelectorAll('option[data-dyn]').forEach(o => o.remove());
  setSelect('selFps', recFps, v => v + ' fps');
  setSelect('selLiveFps', streamFps, v => v + ' fps');
  setSelect('selQuality', recQuality, v => qualityLabel(v));
  setSelect('selKeep', recKeep, v => v + ' 秒');
  syncQuality();
  syncCapture();
}

// 抓屏方式下拉回显 + 当前实际后端（从 /api/info 刷新）
function syncCapture() {
  const sel = $('capMode');
  if (sel) sel.value = String(captureMode);
}
async function refreshCaptureBackend() {
  if (!cur) return;
  const base = directBase(cur);
  if (!base) { const b = $('capBackend'); if (b) b.textContent = '—'; return; }
  try {
    const headers = {};
    if (directToken()) headers['X-SoulLink-Token'] = directToken();
    const r = await fetch(base + '/api/info', { headers });
    const d = await r.json();
    const b = $('capBackend');
    if (b) b.textContent = (d && d.captureBackend) ? d.captureBackend : '—';
  } catch (e) {
    const b = $('capBackend'); if (b) b.textContent = '—';
  }
}

// 直播画质滑杆/下拉回显
function syncQuality() {
  const bit = $('qBitrate'), bitV = $('qBitrateV');
  if (bit) bit.value = videoBitrate;
  if (bitV) bitV.textContent = Math.round(videoBitrate / 1000) + 'M';
  const fps = $('qStreamFps'), fpsV = $('qStreamFpsV');
  if (fps) fps.value = streamFps;
  if (fpsV) fpsV.textContent = streamFps + 'fps';
  const pre = $('qPreset');
  if (pre) pre.value = String(nvencPreset);
  const vbv = $('qVbv'), vbvV = $('qVbvV');
  if (vbv) vbv.value = vbvBufferIncreasePercent;
  if (vbvV) vbvV.textContent = vbvBufferIncreasePercent + '%';
}

function qualityLabel(v) {
  if (v >= 85) return '高清 ' + v;
  if (v >= 70) return '清晰 ' + v;
  return '标准 ' + v;
}

// 把 value 选中到下拉框。预设里没有这个值就补一项（并标记，下次可清掉）。
// ★ 补出来的项带 data-dyn="1"，syncDropdowns 开头会先清掉旧的动态项，
//   否则配置反复变化时选项会越堆越多。
function setSelect(id, value, fmt) {
  const el = $(id); if (!el) return;
  const v = String(value);
  let opt = el.querySelector('option[value="' + v + '"]:not([data-dyn])');
  if (!opt) {
    opt = document.createElement('option');
    opt.value = v;
    opt.textContent = fmt(value);
    opt.setAttribute('data-dyn', '1');
    el.appendChild(opt);
  }
  el.value = v;
}

// 提交改动：先乐观更新界面，再等后端确认（失败就回滚）
let saveTimer = null;
async function pushSettings(patch) {
  try {
    const r = await fetch('/api/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(patch)
    });
    const d = await r.json();
    if (d && d.ok) {
      recFps = d.fps; recQuality = d.quality; recKeep = d.keepSeconds;
      if (typeof d.streamFps === 'number' && d.streamFps > 0) streamFps = d.streamFps;
      else if (typeof d.liveFps === 'number' && d.liveFps > 0) streamFps = d.liveFps;
      if (typeof d.videoBitrate === 'number' && d.videoBitrate > 0) videoBitrate = d.videoBitrate;
      if (typeof d.nvencPreset === 'number' && d.nvencPreset > 0) nvencPreset = d.nvencPreset;
      if (typeof d.vbvBufferIncreasePercent === 'number') vbvBufferIncreasePercent = d.vbvBufferIncreasePercent;
      if (typeof d.captureMode === 'number') captureMode = d.captureMode;
      computeLiveMs(); syncDropdowns(); startLive();
      return true;
    }
  } catch (e) { }
  return false;
}

function bindSelect(id, onPick) {
  const el = $(id); if (!el) return;
  el.onchange = () => { onPick(parseInt(el.value) || 0); };
}
// 录制组：改了就回写应用配置 + 通知代理
bindSelect('selFps',     v => pushSettings({ fps: v }));
bindSelect('selLiveFps', v => pushSettings({ liveFps: v }));
bindSelect('selQuality', v => pushSettings({ quality: v }));
bindSelect('selKeep',    v => pushSettings({ keepSeconds: v }));

// 直播画质组（2026-11-23 手机端原型）：改完回写应用配置 + 即时下发
function bindRange(id, valId, fmt, onPick) {
  const el = $(id), val = $(valId);
  if (!el) return;
  const update = () => { if (val) val.textContent = fmt(el.value); };
  el.oninput = update;
  el.onchange = () => { onPick(parseInt(el.value) || 0); };
}
bindRange('qBitrate',   'qBitrateV',   v => Math.round(v / 1000) + 'M', v => pushSettings({ videoBitrate: v }));
bindRange('qStreamFps', 'qStreamFpsV', v => v + 'fps',                  v => pushSettings({ streamFps: v }));
bindRange('qVbv',       'qVbvV',       v => v + '%',                    v => pushSettings({ vbvBufferIncreasePercent: v }));
const qPre = $('qPreset');
if (qPre) qPre.onchange = () => pushSettings({ nvencPreset: parseInt(qPre.value) || 3 });

// 抓屏方式（2026-11-23）：切换 → pushSettings 改 CaptureMode → 下发 agent 热切
//   → 延时等 agent 帧边界切换完成 → 断开旧 /stream reader 重连拿新 SPS/IDR。
//   为什么不立即重连：热切在 worker 帧边界排队，立即重连会拿到旧后端；
//   且后端分辨率可能变化，旧 decoder 配置对新 SPS 会花屏，必须重连重新 configure。
const capSel = $('capMode');
if (capSel) capSel.onchange = async () => {
  const mode = parseInt(capSel.value) || 0;
  const ok = await pushSettings({ captureMode: mode });
  if (ok) {
    if (liveRunning && !paused) {
      setTimeout(() => {
        refreshCaptureBackend();
        if (liveRunning && !paused) {
          failStreak = 0;
          setPh('抓屏后端已切换 · 正在重连画面…', false);
          startLive();      // stopLive + startStream：断旧 reader 重连新流
        }
      }, 1200);
    }
  } else syncCapture();
};

// 设置弹出菜单（2026-11-23）：点「设置」展开，点面板外/ESC 收起
const settingsBtn = $('settingsBtn'), settingsMenu = $('settingsMenu');
if (settingsBtn && settingsMenu) {
  settingsBtn.onclick = e => {
    e.stopPropagation();
    const open = settingsMenu.classList.toggle('open');
    settingsBtn.classList.toggle('on', open);
  };
  document.addEventListener('pointerdown', e => {
    if (!settingsMenu.classList.contains('open')) return;
    if (!settingsMenu.contains(e.target) && e.target !== settingsBtn) {
      settingsMenu.classList.remove('open');
      settingsBtn.classList.remove('on');
    }
  });
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape' && settingsMenu.classList.contains('open')) {
      settingsMenu.classList.remove('open');
      settingsBtn.classList.remove('on');
    }
  });
}

// 「本机播放」下拉框已随 JPEG 轮询通路移除（2026-11-23）：
//   画面帧率由服务端 /stream 直推，本机没有取图频率概念。
//   viewFps 仅保留作为鼠标移动节流的基准值，localStorage 读取仍在 applySettings。
// ── 键盘：点画面即开启（不再用顶栏按钮）───────────────────
//   原来有个「键盘」按钮占着顶栏，而页面本来就已经全局监听 keydown。
//   现在改成 RDP 客户端的习惯：点一下画面 = 键盘发往远程，
//   点画面外 = 释放（避免 F5/Ctrl+W 被吞掉）。
function setKbMode(on) {
  kbMode = on;
  const badge = $('kbBadge');
  if (badge) badge.style.display = on ? 'inline' : 'none';
}
$('scr').addEventListener('pointerdown', () => setKbMode(true));
document.addEventListener('pointerdown', e => {
  if (!e.target.closest('#scr')) setKbMode(false);
}, true);

// 页面可见性（2026-11-23 随「移除 JPEG 轮询」重写）：
//   隐藏 → 断流省资源（挂机监控页常年开着的场景）；
//   回前台 → 自动重连（画面断开时的提示也会被新一轮连接刷新）。
//   ★ pause 状态下不自动恢复（用户点了暂停就尊重暂停）。
document.addEventListener('visibilitychange', () => {
  if (document.hidden) {
    plLifecycleExit();        // ★ 页面隐藏 → 退出 Pointer Lock（浏览器也会自动解锁，这里主动清状态）
    releaseAllInputs();       // ★ 切后台先释放所有按住的键，防「松手远端没松开」
    if (liveRunning && h264Active) {
      abortStream();          // 只断流，画面冻在最后一帧
      setPh('页面已隐藏，流已暂停 · 回到页面自动重连', false);
    }
  } else {
    if (liveRunning && !paused && !h264Active && !reconnectTimer) {
      setPh('连接中…', false);
      startStream();
    }
  }
});

// ★ 卸载/切后台释放按键：pagehide/beforeunload 触发时 fire-and-forget 发 releaseall。
//   只释放真正按过的键（代理侧 PressedButtons/PressedKeys 只抬按过的键，不会误触）。
window.addEventListener('pagehide', () => releaseAllInputs());
window.addEventListener('beforeunload', () => releaseAllInputs());

$('pauseBtn').onclick = () => {
  paused = !paused;
  $('pauseBtn').classList.toggle('on', paused);
  $('pauseBtn').textContent = paused ? '继续' : '暂停';
  // 暂停 = 断流（不是空转跳帧），继续 = 立刻重连
  if (paused) {
    stopLive();
    setPh('已暂停', false);
  } else { startLive(); }
};
// 滚轮用真实鼠标滚轮（见 #scr 的 wheel 监听），不需要按钮

// ── 录像 ────────────────────────────────────────────────────
// ── 录像列表：按需加载（3.3.17）───────────────────────────
//
//   ★ 用户原则：「网页从应用拿数据，也应该遵循 api 接口按需取用」
//
//   /api/list 要扫 录制回放\ 下的所有目录、读每个 meta.json ——
//   打开画面时根本不需要它。这里改成：
//     · 打开画面 → 清空列表，不发请求
//     · 用户真的展开「录像回放」→ 才取一次
//     · 同一账号 30 秒内重复点开 → 用缓存，不重复扫盘
let savedFetchedFor = null;   // 已取过哪个账号的列表
let savedFetchedAt = 0;
const SAVED_TTL_MS = 30000;

function resetSavedList() {
  savedFetchedFor = null;
  savedFetchedAt = 0;
  const box = $("saved");
  if (box) box.innerHTML = "";
  // 收起折叠：换账号后不该默认展开（那就又变成"一打开就扫盘"了）
  const t = $("savedToggle"), w = $("savedWrap");
  if (t) t.classList.remove("open");
  if (w) w.style.display = "none";
}

// 用户点开「录像回放」时调用：需要才取，且 30 秒内复用。
function ensureSaved() {
  if (!cur) return;
  const key = cur.id || cur.label;
  const fresh = savedFetchedFor === key && (Date.now() - savedFetchedAt) < SAVED_TTL_MS;
  if (fresh) return;
  savedFetchedFor = key;
  savedFetchedAt = Date.now();
  loadSaved();
}

async function loadSaved() {
  try {
    const r = await fetch('/api/list');
    const d = await r.json();
    const box = $('saved'); box.innerHTML = '';
    const mine = (d.items || []).filter(it => !cur || it.user === cur.label);
    if (!mine.length) {
      box.innerHTML = '<div class="imt" style="padding:8px 0">还没有录像</div>';
      return;
    }
    for (const it of mine) {
      let trig = '', fps = 10;
      try {
        if (it.meta) { const m = JSON.parse(it.meta); trig = m.trigger || ''; fps = m.fps || 10; }
      } catch (x) {}
      const short = it.name.replace(/^[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9]{6}-?/, '');
      const div = document.createElement('div');
      div.className = 'item';
      div.innerHTML = '<div style="flex:1">'
        + '<div class="inm">' + esc(short || it.name)
        + (trig ? '<span class="badge">' + esc(trig) + '</span>' : '') + '</div>'
        + '<div class="imt">' + it.frames + ' 帧 · ' + fps + ' fps</div></div>'
        + '<div style="color:#4fe3ff">&#9656;</div>';
      div.onclick = () => playVideo(it);
      box.appendChild(div);
    }
  } catch (x) {}
}

function playVideo(it) {
  const v = $('vid');
  const url = '/file/' + encodeURI(it.path) + '/replay.mp4';
  v.src = url; v.load();
  $('player').style.display = 'block';
  $('pinfo').textContent = it.frames + ' 帧';
  $('dl').onclick = () => window.open(url, '_blank');
  v.scrollIntoView({ behavior: 'smooth', block: 'center' });
}
$('pback').onclick = () => {
  const v = $('vid'); v.pause(); v.removeAttribute('src'); v.load();
  $('player').style.display = 'none';
};

function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g,
    c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

// ── hash 路由（2026-11-23 网页改版）────────────────────────────
//   #/                总览（每账号一张折叠卡片）
//   #/remote/:id      查看画面（同页跳转，取代旧 ?view= 独立窗口）
//   #/ledger/:id      配置组分析（顶部用户切换器）
//   #/earnings/:id    摩拉收益（顶部用户切换器）
//   三个功能页顶部都有「用户切换器」，页内直接切账号看对应数据，无需退回首页。
let curId = '';   // 功能页当前选中的账号 id（用户切换器回显用）

// 拉取会话列表（token / 主题 / 录制参数 / 账号），供功能页复用。
async function ensureSessions() {
  const r = await fetch('/api/sessions');
  const d = await r.json();
  window.__slToken = d.directToken || "";
  if (typeof d.themeDark === 'boolean') { appIsDark = d.themeDark; applyTheme(); }
  applySettings(d);
  users = (d.sessions || []).filter(s => s.bound);
  return users;
}

function hideRemoteUi() {
  const v = $('viewer'); if (v) v.style.display = 'none';
  const p = $('player'); if (p) p.style.display = 'none';
  const vc = $('viewctrls'); if (vc) vc.style.display = 'none';
}
function hideFuncPages() {
  const lp = $('ledgerPage'); if (lp) lp.style.display = 'none';
  const ep = $('earningsPage'); if (ep) ep.style.display = 'none';
}

// 用户切换器：容器 + 当前账号 + 选中回调。无账号时隐藏。
function buildUserSwitch(containerId, currentId, onPick) {
  const box = $(containerId);
  if (!box) return;
  box.innerHTML = '';
  const list = users.filter(u => u && u.id);
  if (!list.length) { box.style.display = 'none'; return; }
  box.style.display = 'flex';
  for (const u of list) {
    const chip = document.createElement('span');
    chip.className = 'uchip' + (u.id === currentId ? ' on' : '');
    chip.textContent = u.label || u.user || u.id;
    chip.onclick = () => onPick(u);
    box.appendChild(chip);
  }
}

// ── 查看画面（#/remote/:id）──────────────────────────────────
async function showRemote(id) {
  await ensureSessions();
  const u = users.find(x => x.id === id) || users[0];
  if (!u) { showOverview(); return; }
  document.body.classList.add('viewonly');
  document.documentElement.classList.add('viewonly-root');
  const vc = $('viewctrls'); if (vc) vc.style.display = 'flex';
  hideFuncPages();
  const v = $('viewer'); if (v) v.style.display = 'block';
  const p = $('player'); if (p) p.style.display = 'none';
  applyRemoteUser(u);
}

function applyRemoteUser(u) {
  cur = u;
  curId = u.id;
  document.title = (u.label || u.user) + ' · 实时画面';
  const t = $('viewerTitle'); if (t) t.textContent = '实时画面 · ' + (u.label || u.user);
  const fi = $('fpsinfo'); if (fi) fi.textContent = (u.screen || '');
  const cv = $('vframe'); if (cv) cv.classList.remove('show');
  const ph = $('ph'); if (ph) ph.style.display = 'none';
  buildUserSwitch('remoteSwitch', u.id, applyRemoteUser);
  resetSavedList();
  syncCapture();
  refreshCaptureBackend();
  startLive();      // startLive 内部先 stopLive()，切账号时旧流会被收掉
}

// ── 配置组分析（#/ledger/:id）────────────────────────────────
async function showLedger(id) {
  await ensureSessions();
  const u = users.find(x => x.id === id) || users[0];
  if (!u) { showOverview(); return; }
  document.body.classList.add('page-func');
  hideRemoteUi();
  const lp = $('ledgerPage'); if (lp) lp.style.display = 'block';
  const ep = $('earningsPage'); if (ep) ep.style.display = 'none';
  applyLedgerUser(u);
}
function applyLedgerUser(u) {
  curId = u.id;
  document.title = '配置组分析 · ' + (u.label || u.user);
  buildUserSwitch('ledgerSwitch', u.id, applyLedgerUser);
  loadLedger(u);
}
async function loadLedger(u) {
  const body = $('ledgerBody');
  body.innerHTML = '<div class="pageempty">加载中…</div>';
  try {
    const r = await fetch('/api/bgi/ledger?user=' + encodeURIComponent(u.user) + '&days=7');
    const d = await r.json();
    body.innerHTML = renderLedger(d);
  } catch (e) {
    body.innerHTML = '<div class="pageempty">加载失败：' + esc(e.message) + '</div>';
  }
}
function renderLedger(d) {
  if (!d) return '<div class="pageempty">暂无台账数据。</div>';
  const groups = d.groups || [];
  const records = d.records || [];
  const problems = (d.zeroReward || []).concat(d.failed || []);
  if (!groups.length && !records.length && !problems.length)
    return '<div class="pageempty">暂无台账数据（近 ' + (d.days || 7) + ' 天没有配置组运行记录）。</div>';
  let html = '';
  if (groups.length) {
    html += '<div class="sect-t">按配置组汇总 · 近 ' + (d.days || 7) + ' 天</div>'
      + '<table class="ldtable"><thead><tr>'
      + '<th>配置组</th><th>次数</th><th>失败</th><th>均经验</th><th>均摩拉</th><th>总经验</th><th>总摩拉</th>'
      + '</tr></thead><tbody>';
    for (const g of groups) {
      html += '<tr>'
        + '<td>' + esc(g.group) + '</td>'
        + '<td class="num">' + (g.runs || 0) + '</td>'
        + '<td class="num ' + ((g.failures || 0) > 0 ? 'badge-fail' : 'badge-ok') + '">' + (g.failures || 0) + '</td>'
        + '<td class="num">' + (g.avgExp || 0) + '</td>'
        + '<td class="num">' + (g.avgMora || 0) + '</td>'
        + '<td class="num">' + (g.totalExp || 0) + '</td>'
        + '<td class="num">' + (g.totalMora || 0) + '</td>'
        + '</tr>';
    }
    html += '</tbody></table>';
  }
  if (problems.length) {
    html += '<div class="sect-t">问题组（0 收益 / 失败）</div>';
    for (const rec of problems) {
      html += '<div class="dline"><span class="dk">' + esc(rec.group || '') + '</span>'
        + '<span class="dv">' + esc(rec.state || '') + ' · ' + esc(rec.day || '') + ' ' + esc(rec.start || '')
        + (rec.detail ? ' · ' + esc(rec.detail) : '') + '</span></div>';
    }
  }
  return html;
}

// ── 摩拉收益（#/earnings/:id）────────────────────────────────
async function showEarnings(id) {
  await ensureSessions();
  const u = users.find(x => x.id === id) || users[0];
  if (!u) { showOverview(); return; }
  document.body.classList.add('page-func');
  hideRemoteUi();
  const lp = $('ledgerPage'); if (lp) lp.style.display = 'none';
  const ep = $('earningsPage'); if (ep) ep.style.display = 'block';
  applyEarningsUser(u);
}
function applyEarningsUser(u) {
  curId = u.id;
  document.title = '摩拉收益 · ' + (u.label || u.user);
  buildUserSwitch('earningsSwitch', u.id, applyEarningsUser);
  loadEarnings(u);
}
async function loadEarnings(u) {
  const body = $('earningsBody');
  body.innerHTML = '<div class="pageempty">加载中…</div>';
  try {
    const r = await fetch('/api/bgi/stats?user=' + encodeURIComponent(u.user));
    const d = await r.json();
    body.innerHTML = renderEarnings(d);
  } catch (e) {
    body.innerHTML = '<div class="pageempty">加载失败：' + esc(e.message) + '</div>';
  }
}
function renderEarnings(d) {
  if (!d) return '<div class="pageempty">暂无收益数据。</div>';
  const exp = d.exp || d.expGained || d.dogfoodExp || 0;
  const mora = d.mora || d.moraGained || d.dogfoodMora || 0;
  let picks = d.pickTotal || 0;
  if (d.picks && typeof d.picks === 'object') {
    picks = Object.values(d.picks).reduce((a, b) => a + (typeof b === 'number' ? b : 0), 0);
  }
  let html = '<div class="metrics">'
    + '<div class="metric"><div class="v num">' + exp + '</div><div class="k">今日经验</div></div>'
    + '<div class="metric"><div class="v num">' + mora + '</div><div class="k">今日摩拉</div></div>'
    + '<div class="metric"><div class="v num">' + picks + '</div><div class="k">今日拾取</div></div>'
    + '</div>';
  if (d.lastRoute) html += '<div class="dline" style="margin-top:12px"><span class="dk">最近路线</span><span class="dv">' + esc(d.lastRoute) + '</span></div>';
  return html;
}

// ── 总览（#/）────────────────────────────────────────────────
function showOverview() {
  hideRemoteUi();
  hideFuncPages();
  document.body.classList.remove('viewonly', 'page-func');
  document.documentElement.classList.remove('viewonly-root');
  document.title = 'SoulLink';
  try { loadUsers(); } catch (e) { console.error('加载失败', e); }
}

// ── 路由分发 ─────────────────────────────────────────────────
function route() {
  stopLive();                              // 任何路由切换先停流
  document.body.classList.remove('viewonly', 'page-func');
  document.documentElement.classList.remove('viewonly-root');
  const parts = (location.hash || '').replace(/^#\/?/, '').split('/').filter(Boolean);
  const page = parts[0] || '';
  const id = parts.length > 1 ? decodeURIComponent(parts.slice(1).join('/')) : '';
  if (page === 'remote' && id) { showRemote(id); return; }
  if (page === 'ledger') { showLedger(id); return; }
  if (page === 'earnings') { showEarnings(id); return; }
  showOverview();
}

const lb = $('ledgerBack'); if (lb) lb.onclick = () => { location.hash = '#/'; };
const eb = $('earningsBack'); if (eb) eb.onclick = () => { location.hash = '#/'; };
window.addEventListener('hashchange', route);
/* ══════════ Pointer Lock 真机验收清单（2026-11-23，部署后实测）══════════
   对应 POINTER-LOCK-DESIGN-20261123.md §7.2 V1-V10：
   V1 进入锁定：桌面左键点画面 → 光标隐藏、pointerLockElement===#scr、移动转视角
   V2 相对位移正确：缓慢匀速移动鼠标 → 远端视角方向/幅度一致，无反向/漂移
   V3 点击不粘连：锁定态点击 → 远端正确点击，无「进锁定那次被误点」
   V4 ESC 一键开菜单：锁定态按 ESC → 光标恢复 + 远端弹出游戏菜单（补发一次 Esc）
   V5 快捷键逃生门：命中 Ctrl+Alt+Shift(默认) → 光标恢复，远端不弹菜单（纯解锁）
   V6 ESC 后重锁冷却：ESC 退出后立即点画面 → 500ms 内不重锁，无 error 抖动
   V7 触屏回退：触屏打开 → 无锁定，单指滑动仍转视角（moverel），不触发锁定
   V8 不支持浏览器降级：无 Pointer Lock → 走绝对模式 + 右键 look，无报错
   V9 锁死兜底：锁定态断网/JS 抛异常 → 按 ESC 仍退出（浏览器层），光标恢复
   V10 灵敏度：同机锁定态转 90° 视角 → 记录鼠标位移标定 sensitivity，二次进入一致
══════════════════════════════════════════════════════════════════ */

route();
</script>
</body>
</html>
""";
    }
}