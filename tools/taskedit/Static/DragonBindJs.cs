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

      TE.el('dRun').onclick = function () {
        TE.confirm('运行一条龙', '将请求主程序启动「' + TE.dragon.name + '」。（本页不直接启停 BGI）',
          function () { TE.toast('已通知主程序（控制面通道在 P2 接入）', 'warn', 3200); });
      };
      TE.el('dAdd').onclick = function () {
        TE.toast('任务定义由 BGI 内置 + 脚本注册提供，P2 接入', 'warn', 3000);
      };

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
          var list = TE.dragon.arr(d.TaskEnabledList);
          var at = -1;
          for (var i = 0; i < list.length; i++) if (String(list[i]) === raw) { at = i; break; }
          if (cb.checked && at < 0) list.push(raw);
          if (!cb.checked && at >= 0) list.splice(at, 1);
          if ('TaskEnabledList' in d || !('taskEnabledList' in d)) d.TaskEnabledList = list;
          else d.taskEnabledList = list;
          TE.dirty.mark('dragon:' + TE.dragon.name);
        };
      });

      TE.dragon.bindExtras(d, c);

      /* 任务拖拽排序 */
      var tbody = TE.el('dTable') && TE.el('dTable').querySelector('tbody');
      if (tbody) TE.dragon.bindDrag(tbody, d);
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
        var ids = TE.dragon.arr(d.TaskOrder || d.taskOrder);
        var defKeys = Object.keys(TE.dragon.defsMap(d.TaskDefinitions || d.taskDefinitions));
        for (var k = 0; k < defKeys.length; k++) {
          if (ids.indexOf(defKeys[k]) < 0) ids.push(defKeys[k]);
        }
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
