using System;

namespace SoulLink.TaskEdit
{
    // 左树（配置组 / 一条龙 / 组合）+ 顶部 Tab 状态。
    internal static partial class TaskEditStatic
    {
        private const string TreeJs = """
'use strict';
(function (TE) {

  /* 树状态：groups/onedragons 是名字数组；selected 是当前节点 key */
  TE.tree = {
    groups: [], onedragons: [], selected: '', kind: '',
    loaded: false,

    /* 载入两份列表（并行） */
    load: function (force) {
      if (TE.tree.loaded && !force) return Promise.resolve();
      return Promise.all([
        TE.api.get('/groups').then(function (r) { return Array.isArray(r) ? r : (r && r.items) || []; }),
        TE.api.get('/onedragons').then(function (r) { return Array.isArray(r) ? r : (r && r.items) || []; })
      ]).then(function (res) {
        TE.tree.groups = (res[0] || []).map(function (x) { return x.name || x; });
        TE.tree.onedragons = (res[1] || []).map(function (x) { return x.name || x; });
        TE.tree.loaded = true;
        TE.tree.render();
      }).catch(function (e) {
        TE.toast('加载列表失败：' + e.message, 'err');
      });
    },

    /* 渲染左树 */
    render: function () {
      var box = TE.el('tree');
      if (!box) return;
      var html = '';

      html += TE.tree.section('配置组', 'group', TE.tree.groups);
      html += TE.tree.section('一条龙', 'dragon', TE.tree.onedragons);

      /* 组合：P1 只占位（数据层尚未提供组合 API） */
      html += '<div class="node root" data-k="combo" data-kind="combo" title="组合（暂未开放）">' +
              '<span class="caret">▸</span>组合 <span class="cnt">0</span></div>';

      box.innerHTML = html;

      box.onclick = function (e) {
        var n = e.target.closest('.node');
        if (!n) return;
        var k = n.getAttribute('data-k');
        var kind = n.getAttribute('data-kind') || '';
        if (kind === 'combo') { TE.toast('组合功能尚未开放（P2）', 'warn'); return; }
        if (n.classList.contains('root')) {
          /* 折叠/展开 */
          var kids = box.querySelector('[data-parent="' + k + '"]');
          if (kids) {
            kids.classList.toggle('children');
            kids.classList.toggle('open');
            var caret = n.querySelector('.caret');
            if (caret) caret.textContent = kids.classList.contains('open') ? '▾' : '▸';
          }
          return;
        }
        TE.tree.select(k, kind);
      };
    },

    section: function (title, kind, names) {
      var id = 'sec-' + kind;
      var html = '<div class="node root" data-k="' + id + '" data-kind="' + kind + '">' +
        '<span class="caret">▸</span>' + TE.esc(title) +
        ' <span class="cnt">' + names.length + '</span></div>';
      html += '<div data-parent="' + id + '">';
      if (!names.length) {
        html += '<div class="hint" style="padding-left:26px">（空）</div>';
      } else {
        for (var i = 0; i < names.length; i++) {
          var n = names[i];
          var on = (TE.tree.selected === kind + ':' + n) ? ' on' : '';
          html += '<div class="node child' + on + '" data-k="' + TE.attr(n) + '" data-kind="' + kind +
            '" title="' + TE.attr(n) + '">' + TE.esc(n) + '</div>';
        }
      }
      html += '</div>';
      return html;
    },

    /* 选中节点 → 交给对应编辑器加载 */
    select: function (name, kind) {
      /* ★ 脏状态保护：切节点前确认 */
      if (TE.dirty.any()) {
        TE.confirm('有未保存的修改', '切换后将丢弃当前修改，确定继续吗？', function () {
          TE.dirty.map = {};
          TE.dirty.paint();
          TE.tree._do(name, kind);
        }, true);
        return;
      }
      TE.tree._do(name, kind);
    },

    _do: function (name, kind) {
      TE.tree.selected = kind + ':' + name;
      TE.tree.kind = kind;
      TE.tree.render();
      if (kind === 'group' && TE.group) TE.group.open(name);
      else if (kind === 'dragon' && TE.dragon) TE.dragon.open(name);
    },

    /* 高亮当前节点（不重渲染，只是切 class） */
    paint: function () {
      var box = TE.el('tree');
      if (!box) return;
      TE.$$('.node.child', box).forEach(function (n) {
        var k = n.getAttribute('data-kind') + ':' + n.getAttribute('data-k');
        n.classList.toggle('on', k === TE.tree.selected);
      });
    }
  };

  /* ── 顶部 Tab ───────────────────────────────────────────── */
  TE.tabs = {
    cur: 'edit',
    init: function () {
      TE.$$('.tab').forEach(function (t) {
        t.onclick = function () { TE.tabs.go(t.getAttribute('data-tab')); };
      });
    },
    go: function (name) {
      if (name === TE.tabs.cur) return;
      if (TE.dirty.any() && name !== 'edit') {
        TE.confirm('有未保存的修改', '切换后将丢弃当前修改，确定继续吗？', function () {
          TE.dirty.map = {}; TE.dirty.paint(); TE.tabs._paint(name);
        }, true);
        return;
      }
      TE.tabs._paint(name);
    },
    _paint: function (name) {
      TE.tabs.cur = name;
      TE.$$('.tab').forEach(function (t) {
        t.classList.toggle('on', t.getAttribute('data-tab') === name);
      });
      var side = TE.el('side');
      if (side) side.style.display = (name === 'edit') ? '' : 'none';
      if (name === 'pick' && TE.pick) TE.pick.open();
      else if (name === 'edit') {
        var c = TE.el('canvas');
        if (c && !TE.tree.selected) c.innerHTML = TE.tabs.emptyHint();
      }
    },
    emptyHint: function () {
      return '<div class="canvas-empty">从左侧选择<b>配置组</b>或<b>一条龙</b>开始编辑<br>' +
        '<span style="font-size:12px;color:#7d8798">改动需点右上角「保存」才会写盘；保存前会先备份</span></div>';
    }
  };

})(window.TE);
""";
    }
}
