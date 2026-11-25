using System;

namespace SoulLink.TaskEdit
{
    // 前端核心：API 封装 / 转义 / 脏状态 / 弹窗 / toast。
    internal static partial class TaskEditStatic
    {
        private const string CoreJs = """
'use strict';
/* ★ 全局命名空间：所有模块挂在 TE 上，避免污染 window。 */
var TE = window.TE || (window.TE = {});

/* ── 基础工具 ─────────────────────────────────────────────── */

/* ★ 所有进 HTML 的用户输入必须过这里（XSS 防护）。 */
TE.esc = function (s) {
  if (s === null || s === undefined) return '';
  return String(s).replace(/[&<>"']/g, function (c) {
    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
  });
};

/* 属性值转义（用于 value="..." 等双引号上下文）。 */
TE.attr = function (s) { return TE.esc(s).replace(/"/g, '&quot;'); };

TE.el = function (id) { return document.getElementById(id); };
TE.$$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };
TE.qs = function (k) {
  var m = new RegExp('[?&]' + k + '=([^&]*)').exec(location.search);
  return m ? decodeURIComponent(m[1]) : '';
};

/* ── API 封装 ─────────────────────────────────────────────── */
/* ★ 只在 /taskedit/api/* 下工作；bgi 根由 ?bgi= 指定（主程序/用户配置决定）。 */
TE.api = {
  base: '/taskedit/api',
  bgi: function () { return TE.qs('bgi'); },
  account: function () { return TE.qs('account') || localStorage.getItem('te.account') || ''; },

  url: function (p) {
    var u = TE.api.base + p;
    var q = [];
    var bgi = TE.api.bgi();
    if (bgi) q.push('bgi=' + encodeURIComponent(bgi));
    var acc = TE.api.account();
    if (acc) q.push('account=' + encodeURIComponent(acc));
    if (q.length) u += (u.indexOf('?') < 0 ? '?' : '&') + q.join('&');
    return u;
  },

  get: function (p) { return TE.api._req('GET', p, null); },
  post: function (p, body) { return TE.api._req('POST', p, body); },

  _req: function (method, p, body) {
    var opt = { method: method, credentials: 'same-origin', headers: {} };
    if (body !== null && body !== undefined) {
      opt.headers['Content-Type'] = 'application/json; charset=utf-8';
      opt.body = JSON.stringify(body);
    }
    return fetch(TE.api.url(p), opt).then(function (r) {
      return r.text().then(function (t) {
        var data = null;
        try { data = t ? JSON.parse(t) : null; } catch (e) { data = { ok: false, error: t }; }
        if (!r.ok && data && !data.errors && !data.error) data.error = 'HTTP ' + r.status;
        data = data || {};
        data._status = r.status;
        return data;
      });
    });
  }
};

/* ── 脏状态（未保存保护）───────────────────────────────────── */
TE.dirty = {
  map: {},
  mark: function (key) { TE.dirty.map[key] = true; TE.dirty.paint(); },
  clear: function (key) { delete TE.dirty.map[key]; TE.dirty.paint(); },
  any: function () { return Object.keys(TE.dirty.map).length > 0; },
  keys: function () { return Object.keys(TE.dirty.map); },
  paint: function () {
    var b = TE.el('saveBtn');
    if (!b) return;
    var d = TE.dirty.any();
    b.classList.toggle('pri', d);
    b.disabled = !d;
    b.textContent = d ? '保存 *' : '保存';
    /* ★ 离开页面时拦一下（浏览器只允许这个文案，不能自定义） */
    window.onbeforeunload = d ? function () { return '有未保存的修改'; } : null;
  }
};

/* ── toast ────────────────────────────────────────────────── */
TE.toast = function (msg, kind, ms) {
  var t = TE.el('toast');
  if (!t) return;
  t.textContent = msg;
  t.className = 'toast open' + (kind ? ' ' + kind : '');
  clearTimeout(TE.toast._t);
  TE.toast._t = setTimeout(function () { t.className = 'toast'; }, ms || 2600);
};

/* ── 弹窗 ─────────────────────────────────────────────────── */
/* opts: {title, bodyHtml, okText, cancelText, onOk(close), wide} */
TE.modal = function (opts) {
  var mask = TE.el('modalMask');
  TE.el('modalTitle').textContent = opts.title || '';
  TE.el('modalBody').innerHTML = opts.bodyHtml || '';
  TE.el('modalOk').textContent = opts.okText || '确定';
  TE.el('modalCancel').textContent = opts.cancelText || '取消';
  TE.el('modalOk').style.display = opts.okText === null ? 'none' : '';
  var box = TE.el('modalBox');
  box.style.maxWidth = opts.wide ? '960px' : '720px';

  function close() { mask.classList.remove('open'); TE.el('modalOk').onclick = null; TE.el('modalCancel').onclick = null; }
  TE.el('modalOk').onclick = function () { if (opts.onOk) { if (opts.onOk(close) !== false) close(); } else close(); };
  TE.el('modalCancel').onclick = close;
  mask.classList.add('open');
  mask.onclick = function (e) { if (e.target === mask) close(); };
  return { close: close };
};

TE.confirm = function (title, msg, onOk, danger) {
  return TE.modal({
    title: title,
    bodyHtml: '<p style="margin:4px 0;color:#a8b0c0">' + TE.esc(msg) + '</p>',
    okText: danger ? '仍然继续' : '确定',
    onOk: function (close) { close(); if (onOk) onOk(); }
  });
};

/* ── 忙碌态 ───────────────────────────────────────────────── */
TE.busy = function (on, text) {
  var c = TE.el('canvas');
  if (!c) return;
  if (on) {
    c.innerHTML = '<div class="loading"><span class="busy"></span> ' + TE.esc(text || '加载中…') + '</div>';
  }
};

/* ── 防抖 ─────────────────────────────────────────────────── */
TE.debounce = function (fn, ms) {
  var t = null;
  return function () {
    var a = arguments, self = this;
    clearTimeout(t);
    t = setTimeout(function () { fn.apply(self, a); }, ms || 250);
  };
};

/* ── 右键菜单 ─────────────────────────────────────────────── */
/*
 * items: [{label, action, danger, sep}]
 * ★ 菜单内容由调用方按**项目类型**决定（见 TE.group.menuFor）。
 */
TE.menu = function (x, y, items) {
  var m = TE.el('ctxMenu');
  var html = '';
  for (var i = 0; i < items.length; i++) {
    var it = items[i];
    if (it.sep) { html += '<div class="sep"></div>'; continue; }
    if (it.hdr) { html += '<div class="hdr">' + TE.esc(it.hdr) + '</div>'; continue; }
    html += '<div class="item' + (it.danger ? ' danger' : '') + '" data-i="' + i + '">' +
      TE.esc(it.label) + '</div>';
  }
  m.innerHTML = html;
  m.classList.add('open');
  /* 先显示再量尺寸，避免超出视口 */
  var w = m.offsetWidth, h = m.offsetHeight;
  m.style.left = Math.min(x, window.innerWidth - w - 8) + 'px';
  m.style.top = Math.min(y, window.innerHeight - h - 8) + 'px';

  m.onclick = function (e) {
    var n = e.target.closest('.item');
    if (!n) return;
    var it = items[+n.getAttribute('data-i')];
    m.classList.remove('open');
    if (it && it.action) it.action();
  };
  TE.menu._close = function () { m.classList.remove('open'); document.removeEventListener('click', TE.menu._close); };
  setTimeout(function () { document.addEventListener('click', TE.menu._close); }, 0);
};

/* ── localStorage 草稿（断线/刷新不丢）────────────────────── */
TE.draft = {
  key: function (k) { return 'te.draft.' + (TE.api.bgi() || '-') + '.' + k; },
  save: function (k, v) { try { localStorage.setItem(TE.draft.key(k), JSON.stringify({ at: Date.now(), v: v })); } catch (e) { } },
  load: function (k) {
    try {
      var raw = localStorage.getItem(TE.draft.key(k));
      if (!raw) return null;
      var o = JSON.parse(raw);
      /* 超过 2 小时的草稿不再提示，避免过时数据干扰 */
      if (!o || Date.now() - (o.at || 0) > 7200000) return null;
      return o.v;
    } catch (e) { return null; }
  },
  del: function (k) { try { localStorage.removeItem(TE.draft.key(k)); } catch (e) { } }
};

/* ── 表单取值助手 ─────────────────────────────────────────── */
TE.num = function (v, def) {
  var n = parseInt(v, 10);
  return isNaN(n) ? (def === undefined ? 0 : def) : n;
};
TE.bool = function (v) { return v === true || v === 'true' || v === 1 || v === '1'; };
TE.str = function (v) { return v === null || v === undefined ? '' : String(v); };
""";
    }
}
