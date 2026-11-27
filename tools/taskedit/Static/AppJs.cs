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
      TE.tabs.init(); TE.dirty.paint();
      var theme;
      try { theme = localStorage.getItem('soullink-theme'); } catch (_) {}
      if (theme !== 'dark' && theme !== 'light')
        theme = window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
      var toggle = TE.el('themeBtn');
      function paintTheme() {
        document.documentElement.setAttribute('data-theme', theme);
        toggle.textContent = theme === 'dark' ? '切换亮色' : '切换暗色';
      }
      paintTheme();
      toggle.onclick = function () {
        theme = theme === 'dark' ? 'light' : 'dark'; paintTheme();
        try { localStorage.setItem('soullink-theme', theme); } catch (_) {}
      };
      TE.el('saveBtn').onclick = TE.app.save;
      TE.el('reloadBtn').onclick = function () { TE.guardNavigation(TE.app.reload); };
      TE.app.reload();
    },

    chooseAccount: async function () {
      var resp = await fetch('/api/sessions', { credentials: 'same-origin' });
      if (!resp.ok) throw new Error('账号列表不可用');
      var sessions = (await resp.json()).sessions || [];
      var users = [...new Set(sessions.filter(s => s.bound && s.user).map(s => s.user))];
      var select = TE.el('accountSelect');
      select.innerHTML = '<option value="">请选择账号</option>' + users.map(function (user) {
        return '<option value="' + TE.attr(user) + '">' + TE.esc(user) + '</option>';
      }).join('');
      select.hidden = false;
      select.onchange = function () {
        if (select.value) location.assign('/taskedit/?account=' + encodeURIComponent(select.value));
      };
      TE.el('canvas').textContent = users.length ? '请选择要编辑的 BGI 账号。' : '当前没有已绑定的账号。';
    },

    reload: async function () {
      var reload = TE.el('reloadBtn');
      if (reload.disabled) return;
      reload.disabled = true;
      TE.tree.generation++;
      TE.$$('.tab').forEach(t => t.disabled = true);
      TE.el('objectPicker').hidden = true;
      TE.busy(true);
      var account = TE.api.account(), hasAccount = new URLSearchParams(location.search).has('account');
      var label = TE.el('accountLabel'), ver = TE.el('verBar');
      label.textContent = account ? '账号 · ' + account : 'BGI 账号';
      ver.textContent = '';
      try {
        if (hasAccount && !account.trim()) throw new Error('账号不能为空');
        if (!hasAccount && !TE.api.bgi()) { await TE.app.chooseAccount(); return; }
        var v = await TE.api.get('/version');
        if (!v.root || v._status >= 400) throw new Error(v.error || '该账号未绑定可用的 BGI 目录');
        TE.app.version = v;
        ver.textContent = [v.channel, v.baseVersion, v.productVersion, v.root].filter(Boolean).join(' · ');
        var banner = TE.el('capBanner');
        if (v.channel && !String(v.channel).toLowerCase().includes('teabag')) {
          banner.className = 'banner warn';
          banner.textContent = '当前 BGI 渠道为「' + v.channel + '」，保存时将校验茶包版独有字段。';
        }
        await TE.tree.load();
        TE.$$('.tab').forEach(t => t.disabled = false);
        TE.tree.show(TE.tabs.cur);
      } catch (e) {
        TE.el('canvas').textContent = '无法打开任务编辑器：' + e.message;
      } finally { reload.disabled = false; }
    },

    /*
     * 保存：把当前选中的对象 POST 回去。
     * ★ 服务端会跑 BgiValidator（字段级 + 能力集），返回 422 + errors 时
     *   前端要把错误**定位到具体字段并标红**（用户要求）。
     */
    save: function () {
      if (!TE.dirty.any()) { TE.toast('没有需要保存的修改', 'warn'); return; }

      if (TE.app.saving) return;
      var kind = TE.tabs.cur;
      if (kind === 'dragon') return TE.dragonOps.save(false);

      var btn = TE.el('saveBtn');
      btn.disabled = true;
      btn.innerHTML = '<span class="busy"></span> 保存中';

      var task;
      if (kind === 'group' && TE.group.data) {
        task = TE.api.post('/groups/' + encodeURIComponent(TE.group.name), TE.group.data);

      } else if (TE.tabs.cur === 'pick' && TE.pick.data) {
        task = TE.pick.save();
      } else {
        btn.disabled = false; TE.dirty.paint();
        return;
      }

      TE.app.saving = true;
      TE.el('canvas').inert = true;
      task.then(function (r) {
        TE.app.saving = false; TE.el('canvas').inert = false;
        btn.disabled = false; TE.dirty.paint();
        if (r && (r.ok === true || r._status === 200)) {
          if (kind === 'pick') TE.pick.acceptSaved();
          TE.dirty.map = {}; TE.dirty.paint();
          TE.toast('已保存' + (r.backup ? '（备份：' + r.backup + '）' : ''), 'ok', 3600);
        } else if (r && r.errors && r.errors.length) {
          TE.app.showErrors(r.errors);
        } else {
          TE.toast('保存失败：' + ((r && r.error) || '未知错误'), 'err', 4200);
        }
      }).catch(function (e) {
        TE.app.saving = false; TE.el('canvas').inert = false;
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
          '<td>' + (e.blocking ? '<span class="tag" style="background:var(--errbg);color:var(--err)">是</span>' : '否') +
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
