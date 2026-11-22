using System;

namespace SoulLink.Services
{
    /// <summary>
    /// 网页界面。
    ///
    /// ★ 为什么单独一个文件：WebServer.cs 里 400+ 行是 HTML 字符串，
    ///   混在一起改页面时容易把逻辑文件写坏（实际发生过多次截断事故）。
    ///   这里用 C# 11 raw string literal，HTML/JS/CSS 不需要任何转义。
    ///
    /// ★ 首页设计（用户要求）：
    ///   不是直接放视频，而是"运行状态总览 + 账号列表"。
    ///   总览指标与桌面版一致：绑定账号 / 会话在线 / BGI 运行。
    ///   视频降级为模块，点某个账号的「查看画面」才展开。
    /// </summary>
    internal static class WebPages
    {
        /// <summary>主页：运行状态 + 账号列表 + 可展开的实时画面模块。</summary>
        public const string Index = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<title>SoulLink</title>
<style>
  *{box-sizing:border-box;-webkit-tap-highlight-color:transparent;
    -webkit-user-select:none;user-select:none}
  body{margin:0;background:#080b16;color:#e6ecff;
       font-family:-apple-system,BlinkMacSystemFont,"Microsoft YaHei",sans-serif}
  header{position:sticky;top:0;z-index:30;padding:11px 14px 9px;
         background:rgba(8,11,22,.96);backdrop-filter:blur(14px);
         border-bottom:1px solid #1a2238;display:flex;align-items:center;gap:10px}
  .logo{width:26px;height:26px;border-radius:8px;flex:0 0 auto;
        background:linear-gradient(135deg,#6c5ce7,#4fe3ff);display:flex;
        align-items:center;justify-content:center;font-size:13px;font-weight:700}
  .ttl{font-size:14px;font-weight:600}
  .ver{font-size:10px;color:#5a6889;margin-left:2px}
  .right{margin-left:auto}
  .pill{font-size:10.5px;padding:3px 9px;border-radius:20px;
        background:#151f38;color:#7fd8ff;border:1px solid #233052}
  main{padding:12px 12px 30px}

  /* ── 状态卡 ── */
  .stat{background:linear-gradient(135deg,#161d36,#111729);border:1px solid #232d4a;
        border-radius:13px;padding:13px 14px;margin-bottom:11px}
  .stat h2{font-size:12px;margin:0 0 3px;color:#8b9bc4;font-weight:500}
  .stat .desc{font-size:10.5px;color:#5a6889;margin-bottom:10px}
  .metrics{display:flex;gap:8px}
  .metric{flex:1;background:#0d1324;border:1px solid #1d2742;border-radius:9px;
          padding:9px 10px;text-align:center}
  .metric .v{font-size:17px;font-weight:600;color:#4fe3ff}
  .metric .k{font-size:9.5px;color:#6f7fa8;margin-top:2px}

  /* ── 用户列表 ── */
  .sect-t{font-size:12px;color:#8b9bc4;margin:16px 0 9px;font-weight:500}
  .urow{background:linear-gradient(135deg,#161d36,#111729);border:1px solid #232d4a;
        border-radius:12px;padding:11px 12px;margin-bottom:8px;
        display:flex;align-items:center;gap:10px;cursor:pointer}
  .urow:active{background:#1c2444}
  .udot{width:7px;height:7px;border-radius:50%;flex:0 0 auto;
        background:#3a4460}
  .udot.on{background:#4fe3ff;box-shadow:0 0 6px #4fe3ff}
  .uinfo{flex:1;min-width:0}
  .un{font-size:12.5px;font-weight:600;display:flex;align-items:center;gap:6px}
  .us{font-size:10px;color:#6f7fa8;margin-top:2px}
  .tag{font-size:9px;padding:1px 6px;border-radius:5px;
       background:#22315c;color:#7fd8ff}
  .tag.rec{background:#392040;color:#e08fff}
  .tag.bgi{background:#1d3a2a;color:#6ee7a8}
  .uact{display:flex;gap:6px;flex:0 0 auto}
  .ibtn{padding:6px 11px;border-radius:8px;border:1px solid #232d4a;background:#111729;
        color:#8b9bc4;font-size:11px;cursor:pointer}
  .ibtn.on{background:#22315c;color:#7fd8ff;border-color:#2f4372}

  /* ── 视频模块（可展开） ── */
  #viewer{display:none;margin-top:12px}
  #scr{position:relative;background:#000;border-radius:12px;overflow:hidden;
       border:1px solid #1e2742;touch-action:none}
  #scr img{width:100%;display:block;min-height:120px;object-fit:contain;pointer-events:none}
  #ph{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;
      color:#5a6889;font-size:12px;padding:20px;text-align:center}
  #rip{position:absolute;width:44px;height:44px;margin:-22px 0 0 -22px;border-radius:50%;
       border:2px solid #4fe3ff;opacity:0;pointer-events:none}
  #rip.go{animation:rp .45s ease-out}
  @keyframes rp{0%{opacity:.9;transform:scale(.35)}100%{opacity:0;transform:scale(1.5)}}
  .row{display:flex;align-items:center;justify-content:space-between;margin-top:10px;gap:9px}
  .row label{font-size:11px;color:#8b9bc4;flex:0 0 auto}
  .seg{display:flex;gap:5px;flex-wrap:wrap;justify-content:flex-end}
  .seg button{padding:5px 10px;border-radius:7px;border:1px solid #232d4a;background:#111729;
              color:#8b9bc4;font-size:10.5px;cursor:pointer}
  .seg button.on{background:#22315c;color:#7fd8ff;border-color:#2f4372}
  #kbTip{display:none;margin-top:9px;padding:8px 10px;border-radius:8px;
         background:#1a2340;border:1px solid #2f4372;color:#7fd8ff;font-size:11px}

  /* ── 录像 ── */
  .item{background:#111729;border:1px solid #232d4a;border-radius:9px;padding:10px;
        margin-bottom:8px;cursor:pointer;display:flex;align-items:center;gap:9px}
  .item:active{background:#18203a}
  .inm{font-size:12px;color:#d3ddff}
  .imt{font-size:10px;color:#6f7fa8;margin-top:2px}
  .badge{padding:1px 6px;border-radius:5px;font-size:9.5px;background:#22315c;
         color:#7fd8ff;margin-left:5px}
  #player{display:none;margin-top:12px}
  #vid{width:100%;display:block;background:#000;border-radius:11px;border:1px solid #1e2742}
  .tools{display:flex;gap:7px;margin-top:9px}
  .tools button{flex:1;padding:9px;border-radius:9px;border:1px solid #232d4a;
                background:#111729;color:#a8b6d8;font-size:11.5px;cursor:pointer}
  .empty{color:#5a6889;font-size:12px;text-align:center;padding:32px 18px;
         border:1px dashed #232d4a;border-radius:11px}
</style>
</head>
<body>

<header>
  <div class="logo">S</div>
  <div>
    <div class="ttl">SoulLink</div>
    <div class="ver" id="ver"></div>
  </div>
  <div class="right"><span class="pill" id="liveCount">— 在线</span></div>
</header>

<main>
  <!-- ══ 运行状态总览 ══ -->
  <div class="stat">
    <h2>运行状态</h2>
    <div class="desc">监控各挂机账号的 BGI 运行情况</div>
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
    <div class="sect-t" id="viewerTitle" style="margin-top:4px">实时画面</div>
    <div id="scr">
      <img id="frame" alt="" style="display:none">
      <div id="ph">连接中…</div>
      <div id="rip"></div>
    </div>
    <div class="row">
      <label>刷新</label>
      <div class="seg" id="speed">
        <button data-ms="1000">1 秒</button>
        <button data-ms="500" class="on">0.5 秒</button>
        <button data-ms="250">0.25 秒</button>
      </div>
    </div>
    <div class="row">
      <label>操作</label>
      <div class="seg">
        <button id="rightBtn">右键</button>
        <button id="kbBtn">键盘</button>
        <button id="pauseBtn">暂停</button>
      </div>
    </div>
    <div class="row">
      <label id="fpsinfo" style="color:#5a6889;font-size:10px"></label>
      <div class="seg">
        <button id="wheelUp">滚轮↑</button>
        <button id="wheelDown">滚轮↓</button>
        <button id="closeViewer">收起</button>
      </div>
    </div>
    <div id="kbTip">键盘已开启 —— 直接敲键盘发送到当前账号</div>

    <div class="sect-t">录像回放</div>
    <div id="saved"></div>
    <div id="player">
      <video id="vid" controls playsinline preload="metadata"></video>
      <div class="imt" id="pinfo" style="text-align:center;margin-top:6px"></div>
      <div class="tools">
        <button id="pback">关闭播放器</button>
        <button id="dl">下载</button>
      </div>
    </div>
  </div>
</main>

<script>
const $ = id => document.getElementById(id);
let users = [], cur = null;
let liveTimer = null, liveMs = 500, paused = false;
let rightMode = false, kbMode = false;

async function loadUsers() {
  try {
    const r = await fetch('/api/sessions');
    const d = await r.json();
    users = (d.sessions || []).filter(s => s.bound);
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

  for (const u of users) {
    const row = document.createElement('div');
    row.className = 'urow';
    const tags = [];
    if (u.bgiRunning) tags.push('<span class="tag bgi">BGI ' +
      (u.bgiLogIdleMinutes >= 0 ? '空闲 ' + u.bgiLogIdleMinutes + ' 分' : '运行中') + '</span>');
    if (u.recording) tags.push('<span class="tag rec">录制中</span>');
    if (!u.online) tags.push('<span class="tag">离线</span>');

    // 副标题：会话 + BGI
    let sub = '';
    if (!u.online) sub = '会话未连接';
    else sub = esc(u.screen) + ' · 通道就绪 · ' + (u.bgiRunning ? 'BGI#' + u.bgiPid : 'BGI 未运行');

    row.innerHTML =
      '<div class="udot' + (u.online ? ' on' : '') + '"></div>'
      + '<div class="uinfo">'
      + '<div class="un">' + esc(u.label) + tags.join('') + '</div>'
      + '<div class="us">' + sub + '</div>'
      + '</div>'
      + '<div class="uact"><button class="ibtn">查看画面</button></div>';
    row.querySelector('.ibtn').onclick = e => { e.stopPropagation(); openViewer(u); };
    box.appendChild(row);
  }
}

// ── 实时画面 ────────────────────────────────────────────────
function openViewer(u) {
  cur = u;
  $('viewerTitle').textContent = '实时画面 · ' + u.label;
  $('viewer').style.display = 'block';
  $('player').style.display = 'none';
  $('ph').style.display = 'flex';
  $('ph').textContent = u.online ? '连接中…' : '该账号不在线';
  $('frame').style.display = 'none';
  $('fpsinfo').textContent = (u.screen || '');
  startLive();
  loadSaved();
  setTimeout(() => $('viewer').scrollIntoView({ behavior: 'smooth', block: 'start' }), 60);
}

function closeViewer() {
  stopLive();
  $('player').style.display = 'none';
  const v = $('vid'); v.pause(); v.removeAttribute('src'); v.load();
  cur = null;
  $('viewer').style.display = 'none';
}

function startLive() {
  stopLive();
  if (!cur || !cur.online) return;
  pullFrame();
  liveTimer = setInterval(() => { if (!paused) pullFrame(); }, liveMs);
}
function stopLive() { if (liveTimer) { clearInterval(liveTimer); liveTimer = null; } }

function pullFrame() {
  if (!cur) return;
  const t = new Image();
  t.onload = () => {
    const f = $('frame');
    f.src = t.src; f.style.display = 'block';
    $('ph').style.display = 'none';
  };
  t.onerror = () => {
    $('ph').style.display = 'flex';
    $('ph').textContent = '暂时没有画面（该账号未开启录制）';
  };
  t.src = '/api/live?sid=' + cur.sessionId + '&t=' + Date.now();
}

// ── 输入 ────────────────────────────────────────────────────
async function sendInput(action, nx, ny, extra) {
  if (!cur) return null;
  const body = Object.assign({ action, x: nx, y: ny }, extra || {});
  try {
    const r = await fetch('/api/input?sid=' + cur.sessionId, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    return await r.json();
  } catch { return null; }
}

function norm(e) {
  const el = $('scr'), rect = el.getBoundingClientRect(), img = $('frame');
  const iw = img.naturalWidth || 16, ih = img.naturalHeight || 9;
  const sc = Math.min(rect.width / iw, rect.height / ih);
  const dw = iw * sc, dh = ih * sc;
  const ox = (rect.width - dw) / 2, oy = (rect.height - dh) / 2;
  const cx = (e.touches ? e.touches[0].clientX : e.clientX) - rect.left - ox;
  const cy = (e.touches ? e.touches[0].clientY : e.clientY) - rect.top - oy;
  return { nx: Math.max(0, Math.min(1, cx / dw)), ny: Math.max(0, Math.min(1, cy / dh)) };
}

function ripple(e) {
  const el = $('rip'), rect = $('scr').getBoundingClientRect();
  el.style.left = ((e.touches ? e.touches[0].clientX : e.clientX) - rect.left) + 'px';
  el.style.top = ((e.touches ? e.touches[0].clientY : e.clientY) - rect.top) + 'px';
  el.classList.remove('go'); void el.offsetWidth; el.classList.add('go');
}

let drag = null;
$('scr').addEventListener('pointerdown', e => {
  if (e.button === 2) return;
  const p = norm(e);
  drag = { sx: e.clientX, sy: e.clientY, moved: false, nx: p.nx, ny: p.ny };
  $('scr').setPointerCapture(e.pointerId);
});
$('scr').addEventListener('pointermove', e => {
  if (!drag) return;
  const dx = e.clientX - drag.sx, dy = e.clientY - drag.sy;
  if (!drag.moved && Math.hypot(dx, dy) < 8) return;
  if (!drag.moved) {
    drag.moved = true;
    sendInput('down', drag.nx, drag.ny, { button: rightMode ? 'right' : 'left' });
  }
  const now = Date.now();
  if (!drag.last || now - drag.last > 60) {
    drag.last = now;
    const p = norm(e);
    sendInput('move', p.nx, p.ny);
  }
});
$('scr').addEventListener('pointerup', e => {
  if (!drag) return;
  const moved = drag.moved; drag = null;
  const p = norm(e);
  if (moved) sendInput('up', p.nx, p.ny, { button: rightMode ? 'right' : 'left' });
  else { ripple(e); sendInput('click', p.nx, p.ny, { button: rightMode ? 'right' : 'left' }); }
});
$('scr').addEventListener('pointercancel', () => {
  if (drag && drag.moved) sendInput('up', -1, -1, { button: 'left' });
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
  const parts = [];
  if (e.ctrlKey) parts.push('Ctrl');
  if (e.altKey) parts.push('Alt');
  if (e.shiftKey) parts.push('Shift');
  parts.push(keyName(e));
  e.preventDefault();
  sendInput('key', -1, -1, { key: parts.join('+') });
});

$('closeViewer').onclick = closeViewer;
$('rightBtn').onclick = () => {
  rightMode = !rightMode;
  $('rightBtn').classList.toggle('on', rightMode);
  $('rightBtn').textContent = rightMode ? '右键开' : '右键';
};
$('kbBtn').onclick = () => {
  kbMode = !kbMode;
  $('kbBtn').classList.toggle('on', kbMode);
  $('kbBtn').textContent = kbMode ? '键盘开' : '键盘';
  $('kbTip').style.display = kbMode ? 'block' : 'none';
};
$('pauseBtn').onclick = () => {
  paused = !paused;
  $('pauseBtn').classList.toggle('on', paused);
  $('pauseBtn').textContent = paused ? '继续' : '暂停';
};
$('wheelUp').onclick = () => sendInput('wheel', -1, -1, { delta: 120 });
$('wheelDown').onclick = () => sendInput('wheel', -1, -1, { delta: -120 });
$('speed').onclick = e => {
  const b = e.target.closest('button'); if (!b) return;
  liveMs = parseInt(b.dataset.ms);
  document.querySelectorAll('#speed button').forEach(x => x.classList.toggle('on', x === b));
  startLive();
};

// ── 录像 ────────────────────────────────────────────────────
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

loadUsers();
</script>
</body>
</html>
""";
    }
}
