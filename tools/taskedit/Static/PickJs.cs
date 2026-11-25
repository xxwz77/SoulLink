using System;

namespace SoulLink.TaskEdit
{
    // 拾取名单（独立 Tab）：白/黑名单模式、基础拾取、排除项、导入导出。
    internal static partial class TaskEditStatic
    {
        private const string PickJs = """
'use strict';
(function (TE) {

  TE.pick = {
    data: null,

    open: function () {
      TE.busy(true, '加载拾取名单…');
      /* 真实端点（t27 接入）：读 User/config.json 的 autoPickConfig
         + User/pick_black_lists.txt + User/pick_fuzzy_black_lists.txt */
      TE.api.get('/picklists').then(function (d) {
        if (!d || d.ok === false) {
          TE.el('canvas').innerHTML = '<div class="banner err">读取失败：' +
            TE.esc((d && d.error) || '未知错误') + '</div>';
          return;
        }
        TE.pick.data = {
          enabled: d.enabled,
          mode: d.mode || 'Blacklist',
          blacklistModePickEnabled: d.blacklistModePickEnabled,
          whitelistModeDoNotPickEnabled: d.whitelistModeDoNotPickEnabled,
          blackList: TE.pick.arr(d.blackList),
          fuzzyBlackList: TE.pick.arr(d.fuzzyBlackList)
        };
        TE.pick.render();
      }).catch(function (e) {
        TE.el('canvas').innerHTML = '<div class="banner err">读取失败：' + TE.esc(e.message) + '</div>';
      });
    },

    arr: function (v) { return Array.isArray(v) ? v.slice() : []; },

    render: function () {
      var d = TE.pick.data || {};
      var isWhitelist = String(d.mode || '').toLowerCase() === 'whitelist';
      var list = d.blackList || [];
      var fuzzy = d.fuzzyBlackList || [];
      var html = '';

      html += '<div class="head"><h2>拾取名单</h2>' +
        '<span class="meta">mode = ' + TE.esc(d.mode || '') + '</span>' +
        '<span class="grow"></span>' +
        '<button class="btn" id="pExport">导出 JSON</button>' +
        '<button class="btn" id="pImport">导入 JSON</button></div>';

      html += '<div class="banner info" style="font-size:12px">' +
        '写入 <code>User/config.json</code> 的 <code>autoPickConfig</code> + ' +
        '<code>pick_black_lists.txt</code> + <code>pick_fuzzy_black_lists.txt</code>；' +
        '保存前会自动备份。黑白名单语义由 BGI 决定，本页只负责数据。</div>';

      /* ── 模式与开关 ── */
      html += '<div class="card"><div class="body">';
      html += '<div class="field"><label>名单模式</label><div class="ctl">' +
        '<select id="pMode">' +
        '<option value="Blacklist"' + (isWhitelist ? '' : ' selected') +
        '>黑名单（列表内★不★拾取）</option>' +
        '<option value="Whitelist"' + (isWhitelist ? ' selected' : '') +
        '>白名单（只拾取列表内）</option>' +
        '</select>' +
        '<div class="desc">对应 autoPickConfig.mode，取值 Blacklist / Whitelist</div></div></div>';

      html += '<div class="field"><label>基础拾取</label><div class="ctl">' +
        '<label class="sw"><input type="checkbox" id="pBase"' +
        (d.enabled === true ? ' checked' : '') + '><i></i></label>' +
        '<div class="desc">关闭后所有拾取都停用（autoPickConfig.enabled）</div></div></div>';

      html += '<div class="field"><label>黑名单模式下仍拾取</label><div class="ctl">' +
        '<label class="sw"><input type="checkbox" id="pBlPick"' +
        (d.blacklistModePickEnabled === true ? ' checked' : '') + '><i></i></label>' +
        '<div class="desc">autoPickConfig.blacklistModePickEnabled</div></div></div>';

      html += '<div class="field"><label>白名单模式下不拾取</label><div class="ctl">' +
        '<label class="sw"><input type="checkbox" id="pWhNo"' +
        (d.whitelistModeDoNotPickEnabled === true ? ' checked' : '') + '><i></i></label>' +
        '<div class="desc">autoPickConfig.whitelistModeDoNotPickEnabled</div></div></div>';
      html += '</div></div>';

      /* ── 精确名单 ── */
      html += '<details class="card" open><summary>' +
        (isWhitelist ? '白名单（只拾取这些）' : '黑名单（这些不拾取）') +
        ' <span class="badge tag">' + list.length + '</span></summary><div class="body">';
      html += TE.pick.addRowHtml('pAddName', 'pAdd', '输入物品名后回车添加');
      html += TE.pick.listHtml(list, 'pdel');
      html += '</div></details>';

      /* ── 模糊名单 ── */
      html += '<details class="card"><summary>模糊黑名单（关键字匹配）' +
        ' <span class="badge tag">' + fuzzy.length + '</span></summary><div class="body">';
      html += TE.pick.addRowHtml('pAddFuzzy', 'pAddFuzzyBtn', '输入关键字后回车添加');
      html += TE.pick.listHtml(fuzzy, 'fdel');
      html += '</div></details>';

      TE.el('canvas').innerHTML = html;
      TE.pick.bind(d);
    },

    addRowHtml: function (inputId, btnId, placeholder) {
      return '<div class="row" style="margin-bottom:9px">' +
        '<input type="text" id="' + inputId + '" placeholder="' + TE.attr(placeholder) + '" ' +
        'style="max-width:260px"><button class="btn sm" id="' + btnId + '">添加</button></div>';
    },

    listHtml: function (items, delAttr) {
      /* ★ 分页渲染：超过 100 条只渲染前 100 条，避免远程卡死（§17.4） */
      var MAX = 100;
      var html = '<table class="grid"><thead><tr><th class="idx">#</th><th>名称</th>' +
        '<th style="width:80px">操作</th></tr></thead><tbody>';
      if (!items.length) {
        html += '<tr><td colspan="3" style="padding:18px;text-align:center;color:#7d8798">' +
          '列表为空</td></tr>';
      }
      var n = Math.min(items.length, MAX);
      for (var i = 0; i < n; i++) {
        html += '<tr><td class="idx">' + (i + 1) + '</td>' +
          '<td>' + TE.esc(items[i]) + '</td>' +
          '<td><button class="btn sm danger" data-' + delAttr + '="' + i + '">移除</button></td></tr>';
      }
      html += '</tbody></table>';
      if (items.length > MAX) {
        html += '<div class="desc" style="margin-top:8px;color:#ffb454">' +
          '共 ' + items.length + ' 项，当前只显示前 ' + MAX + ' 项（远程访问保护）。</div>';
      }
      return html;
    },

    bind: function (d) {
      var c = TE.el('canvas');

      TE.el('pMode').onchange = function () {
        d.mode = TE.el('pMode').value;
        TE.dirty.mark('pick');
        TE.pick.render();
        TE.toast('已切换为' + (d.mode === 'Whitelist' ? '白' : '黑') + '名单（需保存生效）', 'ok');
      };
      TE.el('pBase').onchange = function () { d.enabled = TE.el('pBase').checked; TE.dirty.mark('pick'); };
      TE.el('pBlPick').onchange = function () { d.blacklistModePickEnabled = TE.el('pBlPick').checked; TE.dirty.mark('pick'); };
      TE.el('pWhNo').onchange = function () { d.whitelistModeDoNotPickEnabled = TE.el('pWhNo').checked; TE.dirty.mark('pick'); };

      TE.el('pAdd').onclick = function () { TE.pick.add(d.blackList, 'pAddName'); };
      TE.el('pAddName').onkeydown = function (e) { if (e.key === 'Enter') TE.pick.add(d.blackList, 'pAddName'); };
      TE.el('pAddFuzzyBtn').onclick = function () { TE.pick.add(d.fuzzyBlackList, 'pAddFuzzy'); };
      TE.el('pAddFuzzy').onkeydown = function (e) { if (e.key === 'Enter') TE.pick.add(d.fuzzyBlackList, 'pAddFuzzy'); };

      TE.$$('[data-pdel]', c).forEach(function (b) {
        b.onclick = function () {
          d.blackList.splice(+b.getAttribute('data-pdel'), 1);
          TE.dirty.mark('pick'); TE.pick.render();
        };
      });
      TE.$$('[data-fdel]', c).forEach(function (b) {
        b.onclick = function () {
          d.fuzzyBlackList.splice(+b.getAttribute('data-fdel'), 1);
          TE.dirty.mark('pick'); TE.pick.render();
        };
      });

      TE.el('pExport').onclick = function () {
        var json = JSON.stringify(d, null, 2);
        TE.modal({
          title: '导出 JSON', wide: true,
          bodyHtml: '<textarea id="pOut" style="min-height:260px">' + TE.esc(json) + '</textarea>' +
            '<div class="desc">复制保存即可；导入时整份替换。</div>',
          okText: '复制到剪贴板',
          onOk: function () {
            var el = TE.el('pOut'); el.select();
            try { document.execCommand('copy'); TE.toast('已复制', 'ok'); }
            catch (e) { TE.toast('复制失败，请手动选择', 'warn'); }
          }
        });
      };

      TE.el('pImport').onclick = function () {
        TE.modal({
          title: '导入 JSON', wide: true,
          bodyHtml: '<textarea id="pIn" placeholder="粘贴之前导出的 JSON" style="min-height:260px"></textarea>' +
            '<div class="desc">导入会替换当前名单，保存后生效。</div>',
          okText: '导入',
          onOk: function (close) {
            try {
              var v = JSON.parse(TE.el('pIn').value);
              TE.pick.data = {
                enabled: v.enabled === true,
                mode: v.mode || 'Blacklist',
                blacklistModePickEnabled: v.blacklistModePickEnabled === true,
                whitelistModeDoNotPickEnabled: v.whitelistModeDoNotPickEnabled === true,
                blackList: TE.pick.arr(v.blackList),
                fuzzyBlackList: TE.pick.arr(v.fuzzyBlackList)
              };
              TE.dirty.mark('pick');
              close(); TE.pick.render();
              TE.toast('已导入（需保存生效）', 'ok');
            } catch (e) {
              TE.toast('JSON 格式错误：' + e.message, 'err');
              return false;
            }
          }
        });
      };
    },

    add: function (list, inputId) {
      var inp = TE.el(inputId);
      var v = (inp.value || '').trim();
      if (!v) { TE.toast('请输入内容', 'warn'); return; }
      if (list.indexOf(v) >= 0) { TE.toast('已存在：' + v, 'warn'); return; }
      list.push(v);
      inp.value = '';
      TE.dirty.mark('pick');
      TE.pick.render();
    }
  };
})(window.TE);
""";
    }
}