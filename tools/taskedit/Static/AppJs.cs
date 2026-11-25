using System;

namespace SoulLink.TaskEdit
{
    // 页面入口：初始化、保存流程、版本栏、字段级校验回填。
    internal static partial class TaskEditStatic
    {
        private const string AppJs = """
'use strict';
(function (TE) {

  TE.app = {
    version: '',

    init: function () {
      TE.tabs.init();
      TE.dirty.paint();

      /* 版本栏：拉 /version 显示渠道与版本（用户要确认「改的是哪个 BGI」） */
      TE.api.get('/version').then(function (v) {
        var parts = [];
        if (v.channel) parts.push(v.channel);
        if (v.baseVersion) parts.push(v.baseVersion);
        if (v.productVersion) parts.push('v' + v.productVersion);
        if (v.root) parts.push(v.root);
        TE.app.version = v;
        var el = TE.el('verBar');
        if (el) el.textContent = parts.length ? parts.join(' · ') : '(版本未知)';

        /* ★ 能力集告警：茶包独有字段在公版上会被服务端拦截，先提示 */
        if (v.channel && String(v.channel).toLowerCase().indexOf('teabag') < 0) {
          var b = TE.el('capBanner');
          if (b) {
            b.className = 'banner warn';
            b.innerHTML = '当前 BGI 渠道为「' + TE.esc(v.channel) + '」，' +
              '茶包版独有字段（如「使用公版自动战斗引擎」）在保存时会被拦截。';
          }
        }
      }).catch(function () {
        var el = TE.el('verBar');
        if (el) el.textContent = '(版本不可用)';
      });

      TE.tree.load().then(function () {
        TE.el('canvas').innerHTML = TE.tabs.emptyHint();
      });

      TE.el('saveBtn').onclick = function () { TE.app.save(); };
      TE.el('reloadBtn').onclick = function () {
        if (TE.dirty.any()) {
          TE.confirm('有未保存的修改', '重新加载会丢弃当前修改，确定吗？', function () {
            TE.dirty.map = {}; TE.dirty.paint(); TE.app.reload();
          }, true);
        } else TE.app.reload();
      };
    },

    reload: function () {
      TE.tree.loaded = false;
      TE.tree.selected = '';
      TE.tree.load(true).then(function () {
        TE.el('canvas').innerHTML = TE.tabs.emptyHint();
      });
    },

    /*
     * 保存：把当前选中的对象 POST 回去。
     * ★ 服务端会跑 BgiValidator（字段级 + 能力集），返回 422 + errors 时
     *   前端要把错误**定位到具体字段并标红**（用户要求）。
     */
    save: function () {
      if (!TE.dirty.any()) { TE.toast('没有需要保存的修改', 'warn'); return; }

      var kind = TE.tree.kind, name = '';
      if (TE.tree.selected) name = TE.tree.selected.substring(kind.length + 1);

      var btn = TE.el('saveBtn');
      btn.disabled = true;
      btn.innerHTML = '<span class="busy"></span> 保存中';

      var task;
      if (kind === 'group' && TE.group.data) {
        task = TE.api.post('/groups/' + encodeURIComponent(TE.group.name), TE.group.data);
      } else if (kind === 'dragon' && TE.dragon.data) {
        task = TE.api.post('/onedragons/' + encodeURIComponent(TE.dragon.name), TE.dragon.data);
      } else if (TE.tabs.cur === 'pick' && TE.pick.data) {
        /* ★ t27 已接入：POST /taskedit/api/picklists 写回
           autoPickConfig + pick_black_lists.txt + pick_fuzzy_black_lists.txt */
        task = TE.api.post('/picklists', TE.pick.data);
      } else {
        btn.disabled = false; TE.dirty.paint();
        return;
      }

      task.then(function (r) {
        btn.disabled = false; TE.dirty.paint();
        if (r && (r.ok === true || r._status === 200)) {
          TE.dirty.map = {}; TE.dirty.paint();
          TE.toast('已保存' + (r.backup ? '（备份：' + r.backup + '）' : ''), 'ok', 3600);
        } else if (r && r.errors && r.errors.length) {
          TE.app.showErrors(r.errors);
        } else {
          TE.toast('保存失败：' + ((r && r.error) || '未知错误'), 'err', 4200);
        }
      }).catch(function (e) {
        btn.disabled = false; TE.dirty.paint();
        TE.toast('保存失败：' + e.message, 'err', 4200);
      });
    },

    /* 把服务端校验错误标红到具体字段 */
    showErrors: function (errors) {
      var html = '<div class="banner err">保存被拒绝：' + errors.length + ' 个问题</div>';
      html += '<table class="grid"><thead><tr><th>字段</th><th>问题</th><th>阻断</th></tr></thead><tbody>';
      for (var i = 0; i < errors.length; i++) {
        var e = errors[i];
        html += '<tr><td class="mono">' + TE.esc(e.path || '') + '</td>' +
          '<td>' + TE.esc(e.message || '') + '</td>' +
          '<td>' + (e.blocking ? '<span class="tag" style="background:#3a1d1d;color:#ffb3b3">是</span>' : '否') +
          '</td></tr>';
      }
      html += '</tbody></table>';
      html += '<div class="desc">提示：错误路径形如 <code>config.autoFightConfig.Timeout</code>，' +
        '对应上面表单里的字段名。</div>';

      TE.modal({
        title: '保存被拒绝', bodyHtml: html, wide: true, okText: '我知道了',
        onOk: function () {
          /* 尽力把第一个错误字段标红（按 data-n 或 data-dk 匹配末段） */
          var first = errors.filter(function (x) { return x.blocking; })[0] || errors[0];
          if (first && first.path) {
            var leaf = String(first.path).split('.').pop();
            TE.$$('[data-n],[data-dk]').forEach(function (inp) {
              var n = inp.getAttribute('data-n') || inp.getAttribute('data-dk');
              if (n && n.toLowerCase() === leaf.toLowerCase()) {
                inp.classList.add('bad');
                var tip = inp.parentNode.querySelector('.err-tip');
                if (!tip) { tip = document.createElement('div'); tip.className = 'err-tip'; inp.parentNode.appendChild(tip); }
                tip.textContent = first.message || '';
                try { inp.scrollIntoView({ behavior: 'smooth', block: 'center' }); } catch (e) { }
              }
            });
          }
        }
      });
    }
  };

  /* 启动 */
  document.addEventListener('DOMContentLoaded', function () {
    try { TE.app.init(); } catch (e) {
      TE.toast('初始化失败：' + e.message, 'err', 5000);
    }
  });
})(window.TE);
""";
    }
}
