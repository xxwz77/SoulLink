namespace SoulLink.TaskEdit
{
    internal static partial class TaskEditStatic
    {
        private const string TreeJs = """
'use strict';
(function (TE) {
  TE.guardNavigation = function (go) {
    if (TE.app.saving) return;
    if (!TE.dirty.any()) { go(); return; }
    TE.confirm('有未保存的修改', '继续将丢弃当前修改。', function () {
      TE.dirty.keys().forEach(TE.draft.del);
      TE.dirty.map = {}; TE.dirty.paint(); go();
    }, true);
  };

  TE.tree = {
    groups: [], onedragons: [], remembered: {}, generation: 0,
    readDetail: function (path, onData, onError) {
      var generation = TE.tree.generation;
      return TE.api.get(path).then(function (data) {
        if (generation === TE.tree.generation) onData(data);
      }).catch(function (e) { if (generation === TE.tree.generation) onError(e); });
    },
    load: async function () {
      function names(r) {
        var items = Array.isArray(r) ? r : r.items;
        if (!Array.isArray(items)) throw new Error(r.error || '列表格式错误');
        return items.map(function (x) { return x.name || x; });
      }
      var lists = await Promise.all([TE.api.get('/groups'), TE.api.get('/onedragons')]);
      TE.tree.groups = names(lists[0]); TE.tree.onedragons = names(lists[1]);
    },
    show: function (kind, requested) {
      TE.tree.generation++;
      TE.tabs.cur = kind;
      TE.$$('.tab').forEach(function (t) {
        t.classList.toggle('on', t.dataset.tab === kind);
        t.setAttribute('aria-pressed', t.dataset.tab === kind);
      });
      var picker = TE.el('objectPicker');
      picker.open = false; picker.hidden = kind === 'pick';
      TE[kind].data = null;
      if (kind === 'pick') { TE.pick.open(); return; }
      var names = kind === 'group' ? TE.tree.groups : TE.tree.onedragons;
      var label = kind === 'group' ? '配置组' : '一条龙';
      var name = requested || TE.tree.remembered[kind];
      if (!names.includes(name)) name = names[0];
      TE.tree.remembered[kind] = name;
      picker.querySelector('summary').textContent = label + ' · ' + (name || '暂无配置') + '（' + names.length + '）';
      picker.querySelector('.object-grid').innerHTML = names.map(function (n) {
        return '<button type="button" class="object-option' + (n === name ? ' on' : '') + '" aria-pressed="' + (n === name) + '" data-name="' + TE.attr(n) + '">' + TE.esc(n) + '</button>';
      }).join('');
      if (kind === 'dragon') TE.dragonOps.enter();
      if (name) TE[kind].open(name);
      else if (kind === 'dragon') TE.dragonOps.empty();
      else TE.el('canvas').innerHTML = '<div class="canvas-empty">当前账号暂无' + label + '配置</div>';
    }
  };

  TE.tabs = {
    cur: 'dragon',
    init: function () {
      TE.$$('.tab').forEach(function (t) {
        t.onclick = function () {
          if (t.dataset.tab !== TE.tabs.cur) TE.guardNavigation(function () { TE.tree.show(t.dataset.tab); });
        };
      });
      TE.el('objectPicker').onclick = function (e) {
        var b = e.target.closest('button[data-name]');
        if (!b) return;
        var kind = TE.tabs.cur, name = b.dataset.name;
        if (name === TE.tree.remembered[kind]) { TE.el('objectPicker').open = false; return; }
        TE.guardNavigation(function () { TE.tree.show(kind, name); });
      };
    }
  };
})(window.TE);
""";
    }
}
