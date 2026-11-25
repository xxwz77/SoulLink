using System;

namespace SoulLink.TaskEdit
{
    // 配置组编辑器：项目表格 + 按类型区分的右键菜单 + 拖拽排序。
    internal static partial class TaskEditStatic
    {
        private const string GroupJs = """
'use strict';
(function (TE) {

  /* ★ 先用空对象占位：groupops.js / groupsettings.js 会 Object.assign 进来，
     而它们可能在 group.js 之前执行（脚本顺序）——没有这一行会 "Cannot read
     properties of undefined"。 */
  TE.group = TE.group || {};

  Object.assign(TE.group, {
    name: '', data: null, selIdx: -1, dragFrom: -1,

    open: function (name) {
      TE.group.name = name;
      TE.group.selIdx = -1;
      TE.busy(true, '加载配置组「' + name + '」…');
      TE.api.get('/groups/' + encodeURIComponent(name)).then(function (d) {
        if (!d || d._status >= 400 || d.ok === false) {
          TE.el('canvas').innerHTML = '<div class="banner err">读取失败：' + TE.esc(d.error || '未知错误') + '</div>';
          return;
        }
        TE.group.data = d;
        TE.group.render();
      }).catch(function (e) {
        TE.el('canvas').innerHTML = '<div class="banner err">读取失败：' + TE.esc(e.message) + '</div>';
      });
    },

    render: function () {
      var d = TE.group.data || {};
      var projects = d.projects || d.Projects || [];
      var cfg = d.config || d.Config || {};
      var html = '';

      html += '<div class="head"><h2>' + TE.esc(TE.group.name) + '</h2>' +
        '<span class="meta">' + projects.length + ' 个项目</span>' +
        '<span class="grow"></span>' +
        '<button class="btn" id="gRun">运行</button>' +
        '<button class="btn" id="gAdd">添加</button>' +
        '<button class="btn" id="gSet">设置</button>' +
        '<button class="btn" id="gDir">本地目录</button></div>';

      /* 项目表格（13 个字段全覆盖）在 GroupTableJs.cs 的 TE.group.tableHtml */
      html += TE.group.tableHtml(projects);
      /* 设置分区（四个区，字段一个不少，见 §15） */
      html += TE.group.settingsHtml(cfg);

      var c = TE.el('canvas');
      c.innerHTML = html;
      TE.group.bind(cfg);
    },

    /*
     * 读字段统一助手：兼容 camelCase 与 PascalCase 两种命名。
     * 返回 undefined 表示"两个命名都不存在"（调用方据此决定是否新建字段）。
     */
    fv: function (obj, name) {
      if (!obj) return undefined;
      if (name in obj) return obj[name];
      var pascal = name.charAt(0).toUpperCase() + name.slice(1);
      if (pascal in obj) return obj[pascal];
      return undefined;
    },

    /* 写字段：保留原有命名风格，不新造重复键。 */
    fvSet: function (obj, name, value) {
      if (name in obj) { obj[name] = value; return; }
      var pascal = name.charAt(0).toUpperCase() + name.slice(1);
      if (pascal in obj) { obj[pascal] = value; return; }
      obj[name] = value;
    },

    /* 项目类型识别（对应 §12.2 工厂方法） */
    typeOf: function (p) {
      var t = (p.type || p.Type || p.projectType || '').toString().toLowerCase();
      if (t.indexOf('solo') >= 0) return { key: 'solotask', label: '独立任务', cls: 'solo' };
      if (t.indexOf('javascript') >= 0 || t === 'js') return { key: 'javascript', label: 'JS 脚本', cls: 'js' };
      if (t.indexOf('pathing') >= 0) return { key: 'pathing', label: '地图追踪', cls: 'pathing' };
      if (t.indexOf('shell') >= 0) return { key: 'shell', label: 'Shell', cls: 'shell' };
      if (t.indexOf('keymouse') >= 0 || t.indexOf('key') >= 0) return { key: 'keymouse', label: '键鼠', cls: 'keymouse' };
      /* 退化：从字段猜（BGI 的项目对象没有统一 type 字段时） */
      if (p.soloTaskSettingsObject || p.soloTaskSettings) return { key: 'solotask', label: '独立任务', cls: 'solo' };
      if (p.jsScriptSettingsObject || p.folderName) return { key: 'javascript', label: 'JS 脚本', cls: 'js' };
      return { key: '', label: '未知', cls: '' };
    },

    /* 事件绑定 */
    bind: function (cfg) {
      var c = TE.el('canvas');

      TE.el('gRun').onclick = function () {
        /* ★ 只发通知给主程序（控制面），绝不在这里启停 BGI */
        TE.confirm('运行配置组', '将请求主程序启动配置组「' + TE.group.name + '」。' +
          '（本页不直接启停 BGI）', function () {
            TE.toast('已通知主程序（控制面通道在 P2 接入）', 'warn', 3200);
          });
      };
      TE.el('gAdd').onclick = function (e) { TE.group.showAddMenu(e.clientX, e.clientY); };
      TE.el('gSet').onclick = function () {
        var el = TE.el('sec-config');
        if (el) { el.open = true; el.scrollIntoView({ behavior: 'smooth', block: 'start' }); }
      };
      TE.el('gDir').onclick = function () { TE.group.showDir(); };

      /* ── 行内控件绑定（★ 覆盖全部 13 个 project 字段里的 9 个可编辑项）── */
      var rows = function () { return TE.group.data.projects || TE.group.data.Projects || []; };

      TE.$$('[data-en]', c).forEach(function (cb) {
        cb.onchange = function () {
          var p = rows()[+cb.getAttribute('data-en')];
          if (!p) return;
          TE.group.fvSet(p, 'enabled', cb.checked);
          TE.dirty.mark('group:' + TE.group.name);
        };
      });

      /* nextFlag：单选语义（BGI 里只有一项能是"下次从这里执行"） */
      TE.$$('[data-nf]', c).forEach(function (cb) {
        cb.onchange = function () {
          var idx = +cb.getAttribute('data-nf');
          var arr = rows();
          if (cb.checked) {
            for (var k = 0; k < arr.length; k++) TE.group.fvSet(arr[k], 'nextFlag', k === idx);
          } else {
            TE.group.fvSet(arr[idx], 'nextFlag', false);
          }
          TE.dirty.mark('group:' + TE.group.name);
          TE.group.render();     // 重渲染以反映其它行被取消勾选
        };
      });

      TE.$$('[data-rn]', c).forEach(function (inp) {
        var ev = 'change';
        inp.addEventListener(ev, function () {
          var p = rows()[+inp.getAttribute('data-rn')];
          if (!p) return;
          var v = inp.value === '' ? 0 : TE.num(inp.value, 0);
          if (v < 0) { v = 0; inp.value = '0'; }
          TE.group.fvSet(p, 'runNum', v);
          TE.dirty.mark('group:' + TE.group.name);
        });
      });

      TE.$$('[data-sch]', c).forEach(function (inp) {
        inp.addEventListener('input', function () {
          var p = rows()[+inp.getAttribute('data-sch')];
          if (!p) return;
          TE.group.fvSet(p, 'schedule', inp.value);
          TE.dirty.mark('group:' + TE.group.name);
        });
      });

      TE.$$('[data-ajn]', c).forEach(function (cb) {
        cb.onchange = function () {
          var p = rows()[+cb.getAttribute('data-ajn')];
          if (!p) return;
          TE.group.fvSet(p, 'allowJsNotification', cb.checked);
          TE.dirty.mark('group:' + TE.group.name);
        };
      });

      TE.$$('[data-ajh]', c).forEach(function (cb) {
        cb.onchange = function () {
          var p = rows()[+cb.getAttribute('data-ajh')];
          if (!p) return;
          TE.group.fvSet(p, 'allowJsHTTPHash', cb.checked);
          TE.dirty.mark('group:' + TE.group.name);
        };
      });

      /* 行右键：按类型给菜单 */
      var tbody = TE.el('gTable').querySelector('tbody');
      tbody.oncontextmenu = function (e) {
        var tr = e.target.closest('tr[data-i]');
        e.preventDefault();
        if (!tr) { TE.group.showAddMenu(e.clientX, e.clientY); return; }
        TE.group.showRowMenu(+tr.getAttribute('data-i'), e.clientX, e.clientY);
      };

      /* 拖拽排序 */
      tbody.ondragstart = function (e) {
        var tr = e.target.closest('tr[data-i]');
        if (!tr) return;
        TE.group.dragFrom = +tr.getAttribute('data-i');
        tr.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        try { e.dataTransfer.setData('text/plain', String(TE.group.dragFrom)); } catch (x) { }
      };
      tbody.ondragover = function (e) {
        var tr = e.target.closest('tr[data-i]');
        if (!tr) return;
        e.preventDefault();
        TE.$$('tr', tbody).forEach(function (r) { r.classList.remove('drop-before', 'drop-after'); });
        var r = tr.getBoundingClientRect();
        tr.classList.add(e.clientY < r.top + r.height / 2 ? 'drop-before' : 'drop-after');
      };
      tbody.ondrop = function (e) {
        var tr = e.target.closest('tr[data-i]');
        if (!tr) return;
        e.preventDefault();
        var to = +tr.getAttribute('data-i');
        var r = tr.getBoundingClientRect();
        if (e.clientY >= r.top + r.height / 2) to++;
        TE.group.moveTo(TE.group.dragFrom, to);
      };
      tbody.ondragend = function () {
        TE.$$('tr', tbody).forEach(function (r) { r.classList.remove('dragging', 'drop-before', 'drop-after'); });
        TE.group.dragFrom = -1;
      };

      TE.group.bindSettings(cfg);
    },

    moveTo: function (from, to) {
      var d = TE.group.data;
      var arr = d.projects || d.Projects;
      if (!arr || from < 0 || from >= arr.length) return;
      if (to > from) to--;
      if (to < 0 || to >= arr.length || to === from) { TE.group.render(); return; }
      var item = arr.splice(from, 1)[0];
      arr.splice(to, 0, item);
      TE.dirty.mark('group:' + TE.group.name);
      TE.group.render();
    },

    /* ── 右键菜单：★ 按项目类型区分（用户点名要求）── */
    menuFor: function (i) {
      var arr = (TE.group.data.projects || TE.group.data.Projects || []);
      var p = arr[i] || {};
      var t = TE.group.typeOf(p);
      var items = [];

      /* 类型专属项放在最前（用户最容易找的位置） */
      if (t.key === 'solotask') {
        items.push({ label: '修改独立任务配置', action: function () { TE.group.openSoloSettings(i); } });
        items.push({ sep: true });
      } else if (t.key === 'javascript') {
        items.push({ label: '修改 JS 脚本自定义配置', action: function () { TE.js.open(i); } });
        items.push({ label: '打开所在目录', action: function () { TE.group.showDir(p); } });
        items.push({ sep: true });
      } else if (t.key === 'pathing') {
        items.push({ label: '批量删除…', action: function () { TE.group.batchDelete(i); } });
        items.push({ sep: true });
      }

      items.push({ label: '下一次任务从此处执行', action: function () { TE.group.toggleNext(i); } });
      items.push({ label: '修改通用配置', action: function () { TE.el('sec-config').open = true; TE.el('sec-config').scrollIntoView({ block: 'start' }); } });
      items.push({ sep: true });
      items.push({ label: '移除', danger: true, action: function () { TE.group.remove(i); } });
      return items;
    },

    showRowMenu: function (i, x, y) { TE.menu(x, y, TE.group.menuFor(i)); },

    showAddMenu: function (x, y) {
      TE.menu(x, y, [
        { hdr: '添加项目' },
        { label: '添加 JS 脚本…', action: function () { TE.group.pick('/pickable/js', 'js'); } },
        { label: '添加地图追踪任务…', action: function () { TE.group.pick('/pickable/pathing', 'pathing'); } },
        { label: '添加键鼠脚本…', action: function () { TE.group.pick('/pickable/keymouse', 'keymouse'); } },
        { label: '添加 Shell…', action: function () { TE.group.addShell(); } },
        { label: '添加独立任务…', action: function () { TE.group.pick('/pickable/solotasks', 'solotask'); } }
      ]);
    }
  });
})(window.TE);
""";
    }
}