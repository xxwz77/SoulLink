using System;

namespace SoulLink.TaskEdit
{
    // 一条龙表单的绑定与校验（从 DragonJs 拆出控制体积）。
    internal static partial class TaskEditStatic
    {
        private const string DragonBindJs = """
'use strict';
(function (TE) {

  Object.assign(TE.dragon, {

    bind: function (d) {
      var c = TE.el('canvas');
      function markStart(id) {
        TE.dragon.write(d, 'NextTaskId', id);
        TE.dirty.mark('dragon:' + TE.dragon.name);
        TE.dragon.render();
      }
      TE.el('dAdd').onclick = function () { TE.dragon.pickGroups(d); };
      var clear = TE.el('dClear');
      if (clear) clear.onclick = function () { markStart(''); };
      TE.el('dChoose').onclick = function () {
        var picker = TE.el('objectPicker');
        picker.open = true;
        picker.querySelector('summary').focus();
        picker.scrollIntoView({ block: 'nearest' });
      };
      TE.$$('[data-start]', c).forEach(function (btn) {
        btn.onclick = function () { markStart(btn.dataset.start); };
      });

      /* 字段改动 → 写回 + 校验 */
      TE.$$('[data-dk]', c).forEach(function (inp) {
        var ev = (inp.type === 'checkbox' || inp.tagName === 'SELECT') ? 'change' : 'input';
        inp.addEventListener(ev, function () { TE.dragon.applyField(d, inp); });
        inp.addEventListener('change', function () { TE.dragon.applyField(d, inp); });
      });

      /* 任务启用开关 */
      TE.$$('[data-den]', c).forEach(function (cb) {
        cb.onchange = function () {
          var raw = cb.getAttribute('data-den');
          var list = TE.dragon.read(d, 'TaskEnabledList') || Object.create(null);
          if (Array.isArray(list)) {
            list = list.filter(function (id) { return id !== raw; });
            if (cb.checked) list.push(raw);
          } else {
            Object.defineProperty(list, raw, { value: cb.checked, enumerable: true, configurable: true, writable: true });
          }
          TE.dragon.write(d, 'TaskEnabledList', list);
          if (!cb.checked && TE.dragon.read(d, 'NextTaskId') === raw) markStart('');
          else TE.dragon.render();
          TE.dirty.mark('dragon:' + TE.dragon.name);
        };
      });

      TE.dragon.bindExtras(d, c);

      /* 任务拖拽排序 */
      var tbody = TE.el('dTable') && TE.el('dTable').querySelector('tbody');
      if (tbody) TE.dragon.bindDrag(tbody, d);
    },

    pickGroups: function (d) {
      var groups = TE.tree.groups;
      if (!groups.length) { TE.toast('当前没有可添加的配置组', 'warn'); return; }
      var defs = TE.dragon.read(d, 'TaskDefinitions');
      if (Array.isArray(defs)) { TE.toast('此版本的任务定义格式不支持添加配置组', 'warn'); return; }
      TE.modal({ title: '选择要添加的配置组', okText: '添加', bodyHtml:
        '<div class="pick">' + groups.map(function (name, i) {
          return '<label class="row"><input type="checkbox" data-add-group="' + i + '">' + TE.esc(name) + '</label>';
        }).join('') + '</div>', onOk: function () {
          var selected = TE.$$('[data-add-group]:checked', TE.el('modalBody'));
          if (!selected.length) { TE.toast('请选择至少一个配置组', 'warn'); return false; }
          var ids = TE.dragon.taskIds(d);
          var enabled = TE.dragon.read(d, 'TaskEnabledList') || Object.create(null);
          var definitions = Object.assign(Object.create(null), defs);
          // 原生旧格式以任务名作键；转入定义字典时保留原任务名和顺序。
          ids.forEach(function (id) { if (!(id in definitions)) definitions[id] = id; });
          selected.forEach(function (cb) {
            // getRandomValues also works on HTTP LAN origins where randomUUID is unavailable.
            var bytes = crypto.getRandomValues(new Uint8Array(16));
            bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
            var hex = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
            var id = hex.replace(/^(........)(....)(....)(....)(............)$/, '$1-$2-$3-$4-$5');
            definitions[id] = groups[+cb.dataset.addGroup];
            ids.push(id);
            if (Array.isArray(enabled)) enabled.push(id); else enabled[id] = true;
          });
          TE.dragon.write(d, 'TaskDefinitions', definitions);
          TE.dragon.write(d, 'TaskOrder', ids);
          TE.dragon.write(d, 'TaskEnabledList', enabled);
          TE.dirty.mark('dragon:' + TE.dragon.name);
          TE.dragon.render();
        }
      });
    },

    applyField: function (d, inp) {
      var k = inp.getAttribute('data-dk'), t = inp.getAttribute('data-dt');
      var v;
      if (t === 'bool') v = inp.checked;
      else if (t === 'num') v = inp.value === '' ? null : TE.num(inp.value, 0);
      else v = inp.value;
      TE.dragon.write(d, k, v);
      TE.dragon.validate(inp, k, v);
      TE.dirty.mark('dragon:' + TE.dragon.name);
    },

    validate: function (inp, name, v) {
      var bad = null;
      if (v !== null && v !== '' && typeof v === 'number') {
        if (name === 'AutoBossRunCount' && v < 1) bad = '必须 ≥ 1';
        else if (name === 'AutoBossTimeout' && v < 1) bad = '必须 ≥ 1';
        else if (v < 0) bad = '必须 ≥ 0';
      }
      inp.classList.toggle('bad', !!bad);
      var tip = inp.parentNode.querySelector('.err-tip');
      if (bad) {
        if (!tip) { tip = document.createElement('div'); tip.className = 'err-tip'; inp.parentNode.appendChild(tip); }
        tip.textContent = bad;
      } else if (tip) { tip.remove(); }
    },

    bindDrag: function (tbody, d) {
      var from = -1;
      tbody.ondragstart = function (e) {
        var tr = e.target.closest('tr[data-id]');
        if (!tr) return;
        from = +tr.getAttribute('data-i');
        tr.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        try { e.dataTransfer.setData('text/plain', String(from)); } catch (x) { }
      };
      tbody.ondragover = function (e) {
        var tr = e.target.closest('tr[data-id]');
        if (!tr) return;
        e.preventDefault();
        TE.$$('tr', tbody).forEach(function (r) { r.classList.remove('drop-before', 'drop-after'); });
        var r = tr.getBoundingClientRect();
        tr.classList.add(e.clientY < r.top + r.height / 2 ? 'drop-before' : 'drop-after');
      };
      tbody.ondrop = function (e) {
        var tr = e.target.closest('tr[data-id]');
        if (!tr) return;
        e.preventDefault();
        var to = +tr.getAttribute('data-i');
        var r = tr.getBoundingClientRect();
        if (e.clientY >= r.top + r.height / 2) to++;
        if (to > from) to--;
        if (from < 0 || to === from) return;
        var ids = TE.dragon.taskIds(d);
        var item = ids.splice(from, 1)[0];
        ids.splice(to, 0, item);
        if ('TaskOrder' in d || !('taskOrder' in d)) d.TaskOrder = ids;
        else d.taskOrder = ids;
        TE.dirty.mark('dragon:' + TE.dragon.name);
        TE.dragon.render();
      };
      tbody.ondragend = function () {
        TE.$$('tr', tbody).forEach(function (r) { r.classList.remove('dragging', 'drop-before', 'drop-after'); });
        from = -1;
      };
    }
  });
})(window.TE);
""";
    }
}
