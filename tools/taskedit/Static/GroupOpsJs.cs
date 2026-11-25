using System;

namespace SoulLink.TaskEdit
{
    // 配置组编辑器的辅助操作：添加（调 /pickable/*）、移除、批量删除、目录提示。
    // ★ 从 GroupJs 拆出，让每个前端模块都保持在单文件行数预算内。
    internal static partial class TaskEditStatic
    {
        private const string GroupOpsJs = """
'use strict';
(function (TE) {

  /* ── 辅助操作（挂在 TE.group 上）────────────────────────── */

  Object.assign(TE.group, {

    /* ★ 添加时的列表必须调 /pickable/*（与 BGI 看到的一致，不自己扫目录） */
    pick: function (apiPath, kind) {
      var m = TE.modal({
        title: '选择' + (kind === 'js' ? ' JS 脚本' : kind === 'pathing' ? '地图追踪任务' :
          kind === 'solotask' ? '独立任务' : '键鼠脚本'),
        bodyHtml: '<div class="pick" id="pickList"><div class="empty"><span class="busy"></span> 加载中…</div></div>',
        okText: '添加',
        onOk: function (close) {
          var sel = TE.el('pickList').querySelector('.row.sel');
          if (!sel) { TE.toast('请先选择一项', 'warn'); return false; }
          close();
          TE.group.addProject(kind, sel.getAttribute('data-name'), sel.getAttribute('data-folder'));
        }
      });
      TE.api.get(apiPath).then(function (r) {
        var list = Array.isArray(r) ? r : (r && r.items) || [];
        var box = TE.el('pickList');
        if (!box) return;
        if (!list.length) {
          box.innerHTML = '<div class="empty">没有可选项。请确认 BGI 根目录正确，且该类型资源已安装。</div>';
          return;
        }
        var html = '';
        for (var i = 0; i < list.length; i++) {
          var it = list[i];
          var nm = it.name || it.Name || it;
          var fd = it.folderName || it.folder || '';
          html += '<div class="row" data-name="' + TE.attr(nm) + '" data-folder="' + TE.attr(fd) + '">' +
            TE.esc(nm) + (fd ? ' <span class="mono" style="margin-left:auto;color:#7d8798">' +
              TE.esc(fd) + '</span>' : '') + '</div>';
        }
        box.innerHTML = html;
        box.onclick = function (e) {
          var r = e.target.closest('.row');
          if (!r) return;
          TE.$$('.row', box).forEach(function (x) { x.classList.remove('sel'); });
          r.classList.add('sel');
        };
      }).catch(function (e) {
        var box = TE.el('pickList');
        if (box) box.innerHTML = '<div class="empty">加载失败：' + TE.esc(e.message) + '</div>';
      });
      return m;
    },

    addProject: function (kind, name, folder) {
      var d = TE.group.data;
      var arr = d.projects || (d.projects = []);
      var item;
      if (kind === 'js') item = { type: 'Javascript', name: name, folderName: folder || name, enabled: true };
      else if (kind === 'pathing') item = { type: 'Pathing', name: name, folderName: folder || '', enabled: true };
      else if (kind === 'solotask') item = { type: 'SoloTask', name: name, folderName: name, enabled: true };
      else if (kind === 'keymouse') item = { type: 'KeyMouse', name: name, folderName: name, enabled: true };
      else item = { type: 'Shell', name: name, enabled: true };
      arr.push(item);
      TE.dirty.mark('group:' + TE.group.name);
      TE.group.render();
      TE.toast('已添加：' + name, 'ok');
    },

    addShell: function () {
      TE.modal({
        title: '添加 Shell',
        bodyHtml: '<div class="field"><label>命令</label><div class="ctl">' +
          '<textarea id="shCmd" placeholder="例如：echo hello"></textarea>' +
          '<div class="desc">按 BGI 语义，Shell 项目的 name 即命令本身</div></div></div>',
        okText: '添加',
        onOk: function (close) {
          var v = (TE.el('shCmd').value || '').trim();
          if (!v) { TE.toast('命令不能为空', 'warn'); return false; }
          close();
          TE.group.addProject('shell', v);
        }
      });
    },

    remove: function (i) {
      var arr = (TE.group.data.projects || TE.group.data.Projects || []);
      var p = arr[i] || {};
      TE.confirm('移除项目', '确定从配置组中移除「' + (p.name || p.Name || '') + '」吗？（不会删除磁盘上的文件）',
        function () {
          arr.splice(i, 1);
          TE.dirty.mark('group:' + TE.group.name);
          TE.group.render();
        }, true);
    },

    batchDelete: function (startIdx) {
      var arr = (TE.group.data.projects || TE.group.data.Projects || []);
      var html = '<div class="banner info">勾选要从本组移除的地图追踪项目（不删除磁盘文件）</div>';
      html += '<div class="pick" id="bdList">';
      for (var i = 0; i < arr.length; i++) {
        var t = TE.group.typeOf(arr[i]);
        if (t.key !== 'pathing') continue;
        html += '<label class="row"><input type="checkbox" data-bd="' + i + '"' +
          (i >= startIdx ? ' checked' : '') + '> ' + TE.esc(arr[i].name || arr[i].Name || '') + '</label>';
      }
      html += '</div>';
      TE.modal({
        title: '批量删除地图追踪项目', bodyHtml: html, okText: '删除所选',
        onOk: function (close) {
          var idxs = TE.$$('[data-bd]').filter(function (c) { return c.checked; })
            .map(function (c) { return +c.getAttribute('data-bd'); }).sort(function (a, b) { return b - a; });
          if (!idxs.length) { TE.toast('未选择任何项', 'warn'); return false; }
          close();
          idxs.forEach(function (k) { arr.splice(k, 1); });
          TE.dirty.mark('group:' + TE.group.name);
          TE.group.render();
          TE.toast('已移除 ' + idxs.length + ' 项', 'ok');
        }
      });
    },

    toggleNext: function (i) {
      var arr = (TE.group.data.projects || TE.group.data.Projects || []);
      for (var k = 0; k < arr.length; k++) {
        if (k === i) arr[k].nextFlag = true;
        else if ('nextFlag' in arr[k]) arr[k].nextFlag = false;
      }
      TE.dirty.mark('group:' + TE.group.name);
      TE.toast('已设置「下次从此处执行」：' + (arr[i].name || ''), 'ok');
      TE.group.render();
    },

    showDir: function () {
      TE.modal({
        title: '本地目录',
        bodyHtml: '<p style="color:#a8b0c0;margin:4px 0">BGI 根目录（由服务端解析，前端不拼接路径）：</p>' +
          '<pre class="mono" style="background:#0d0f14;padding:10px;border-radius:8px;overflow:auto">' +
          TE.esc(TE.api.bgi() || '(未指定 ?bgi=，由服务端按 config.json 解析)') + '</pre>' +
          '<p style="color:#7d8798;font-size:12px">配置组目录：User/ScriptGroup/&lt;组名&gt;.json</p>',
        okText: null
      });
    },

    openSoloSettings: function (i) {
      TE.toast('独立任务配置表单在 P1-b 接入（数据层已就绪）', 'warn', 3000);
    }
  };
  });

})(window.TE);
""";
    }
}
