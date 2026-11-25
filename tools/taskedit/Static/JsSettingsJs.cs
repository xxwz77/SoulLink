using System;

namespace SoulLink.TaskEdit
{
    // JS 脚本动态设置表单（§14：SettingItem 六种类型 + 未知类型降级）。
    internal static partial class TaskEditStatic
    {
        private const string JsSettingsJs = """
'use strict';
(function (TE) {

  /*
   * JS 脚本自定义配置（用户硬需求）。
   * 数据来源：脚本 manifest.json 的 SettingsUi → SettingItem 列表。
   * 六种类型（§14.2）：
   *   separator / input-text / select / checkbox / multi-checkbox / cascade-select
   * ★ 未知类型必须降级显示（不崩、不丢数据），并在界面上说明。
   */
  TE.js = {
    projIdx: -1, schema: null, values: {},

    open: function (projIdx) {
      TE.js.projIdx = projIdx;
      var arr = (TE.group.data.projects || TE.group.data.Projects || []);
      var p = arr[projIdx] || {};
      var folder = p.folderName || p.FolderName || p.name || '';

      TE.js.values = p.jsScriptSettingsObject || p.JsScriptSettingsObject || {};
      TE.busy(true, '读取脚本设置 schema…');

      var q = '/getconfig?name=' + encodeURIComponent(TE.group.name) +
        '&index=' + projIdx + '&folder=' + encodeURIComponent(folder);
      TE.api.get(q).then(function (r) {
        TE.js.schema = (r && (r.settingsUi || r.SettingsUi || r.items)) || null;
        TE.js.render();
      }).catch(function () {
        /* ★ 拿不到 schema 不阻断：仍然允许用户看/改已有值（降级） */
        TE.js.schema = null;
        TE.js.render();
      });
    },

    render: function () {
      var arr = (TE.group.data.projects || TE.group.data.Projects || []);
      var p = arr[TE.js.projIdx] || {};
      var name = p.name || p.Name || '';

      var html = '<div class="head"><h2>' + TE.esc(name) + '</h2>' +
        '<span class="tag js">JS 脚本</span><span class="grow"></span>' +
        '<button class="btn" id="jsBack">返回配置组</button></div>';

      var items = TE.js.normalize(TE.js.schema);
      if (!items.length) {
        html += '<div class="banner warn">该脚本没有声明设置项（manifest.json 里没有 SettingsUi），' +
          '或读取失败。你仍可查看现有值。</div>';
        html += TE.js.rawJsonHtml();
      } else {
        html += '<div class="banner info" style="font-size:12px">共 ' + items.length +
          ' 个设置项，来自脚本 manifest.json 的 SettingsUi。</div>';
        html += '<div class="card"><div class="body">';
        for (var i = 0; i < items.length; i++) html += TE.js.itemHtml(items[i], i);
        html += '</div></div>';
      }

      TE.el('canvas').innerHTML = html;
      TE.el('jsBack').onclick = function () { TE.group.render(); };
      TE.js.bind(items);
    },

    /* 规范化：兼容若干种可能的字段命名 */
    normalize: function (schema) {
      if (!schema) return [];
      var arr = Array.isArray(schema) ? schema : (schema.items || schema.Items ||
        schema.settingItems || schema.SettingItems || []);
      return Array.isArray(arr) ? arr : [];
    },

    rawJsonHtml: function () {
      return '<details class="card"><summary>现有配置值（原始 JSON）</summary><div class="body">' +
        '<textarea id="jsRaw" style="min-height:200px">' +
        TE.esc(JSON.stringify(TE.js.values || {}, null, 2)) + '</textarea>' +
        '<div class="desc">⚠ 直接编辑 JSON 有风险；schema 可用时请用上面的表单。</div>' +
        '</div></details>';
    },

    itemHtml: function (it, idx) {
      var type = String(it.type || it.Type || it.itemType || '').toLowerCase();
      var name = it.name || it.Name || it.key || ('item' + idx);
      var label = it.title || it.Title || it.label || name;
      var desc = it.description || it.Description || it.tip || '';
      var val = TE.js.value(name);
      var id = 'js-' + idx;

      /* separator：只画一条分隔线 */
      if (type === 'separator') {
        return '<div style="margin:14px 0 8px;border-top:1px solid #262b36;padding-top:8px">' +
          (label ? '<b style="font-size:13px">' + TE.esc(label) + '</b>' : '') + '</div>';
      }

      var html = '<div class="field"><label for="' + id + '">' + TE.esc(label) + '</label><div class="ctl">';

      switch (type) {
        case 'input-text':
          html += '<input type="text" id="' + id + '" data-js="' + TE.attr(name) +
            '" data-jt="text" value="' + TE.attr(TE.str(val)) + '">';
          break;

        case 'select':
          var opts = TE.js.optionsOf(it);
          html += '<select id="' + id + '" data-js="' + TE.attr(name) + '" data-jt="text">';
          for (var i = 0; i < opts.length; i++) {
            html += '<option value="' + TE.attr(opts[i].value) + '"' +
              (String(val) === String(opts[i].value) ? ' selected' : '') + '>' +
              TE.esc(opts[i].label) + '</option>';
          }
          html += '</select>';
          break;

        case 'checkbox':
          html += '<label class="sw"><input type="checkbox" id="' + id + '" data-js="' + TE.attr(name) +
            '" data-jt="bool"' + (val === true ? ' checked' : '') + '><i></i></label>';
          break;

        case 'multi-checkbox':
          var mopts = TE.js.optionsOf(it);
          var cur = Array.isArray(val) ? val : [];
          html += '<div style="display:flex;flex-wrap:wrap;gap:10px">';
          for (var m = 0; m < mopts.length; m++) {
            var cid = id + '-' + m;
            html += '<label style="display:flex;align-items:center;gap:5px;color:#a8b0c0;font-size:13px">' +
              '<input type="checkbox" id="' + cid + '" data-jsm="' + TE.attr(name) + '" data-jv="' +
              TE.attr(mopts[m].value) + '"' + (cur.indexOf(mopts[m].value) >= 0 ? ' checked' : '') + '>' +
              TE.esc(mopts[m].label) + '</label>';
          }
          html += '</div>';
          break;

        case 'cascade-select':
          /* 两级联动：先选父级，再按父级列子级 */
          var levels = it.levels || it.Levels || it.options || [];
          html += TE.js.cascadeHtml(id, name, levels, val);
          break;

        default:
          /* ★ 未知类型降级：显示原始值 + 一个文本框，不丢数据、不崩 */
          html += '<input type="text" id="' + id + '" data-js="' + TE.attr(name) +
            '" data-jt="json" value="' + TE.attr(TE.str(typeof val === 'object' ?
              JSON.stringify(val) : val)) + '">' +
            '<div class="desc" style="color:#ffb454">⚠ 该控件类型「' + TE.esc(type || '(空)') +
            '」暂不支持，已降级为文本框（内容按原样保存）。</div>';
          break;
      }

      if (desc && type !== 'separator') html += '<div class="desc">' + TE.esc(desc) + '</div>';
      html += '</div></div>';
      return html;
    },

    cascadeHtml: function (id, name, levels, val) {
      if (!Array.isArray(levels) || !levels.length) {
        return '<input type="text" id="' + id + '" data-js="' + TE.attr(name) +
          '" data-jt="text" value="' + TE.attr(TE.str(val)) + '">';
      }
      var html = '';
      for (var L = 0; L < levels.length; L++) {
        var lv = levels[L];
        var opts = TE.js.optionsOf(lv);
        html += '<select id="' + id + '-L' + L + '" data-jc="' + TE.attr(name) + '" data-jl="' + L +
          '" style="margin-bottom:5px">';
        for (var i = 0; i < opts.length; i++) {
          html += '<option value="' + TE.attr(opts[i].value) + '">' + TE.esc(opts[i].label) + '</option>';
        }
        html += '</select>';
      }
      return html;
    },

    /* options 兼容：字符串数组 / {label,value} 数组 */
    optionsOf: function (obj) {
      var raw = obj.options || obj.Options || obj.items || obj.values || [];
      if (!Array.isArray(raw)) return [];
      var out = [];
      for (var i = 0; i < raw.length; i++) {
        var v = raw[i];
        if (v === null || v === undefined) continue;
        if (typeof v === 'object') out.push({ label: v.label || v.Label || v.value || v.Value || '', value: v.value || v.Value });
        else out.push({ label: String(v), value: v });
      }
      return out;
    },

    value: function (name) {
      var v = TE.js.values || {};
      if (name in v) return v[name];
      return null;
    },

    bind: function (items) {
      var c = TE.el('canvas');

      TE.$$('[data-js]', c).forEach(function (inp) {
        var ev = (inp.type === 'checkbox' || inp.tagName === 'SELECT') ? 'change' : 'input';
        inp.addEventListener(ev, function () {
          var name = inp.getAttribute('data-js'), t = inp.getAttribute('data-jt');
          var v;
          if (t === 'bool') v = inp.checked;
          else if (t === 'json') { try { v = JSON.parse(inp.value); } catch (e) { v = inp.value; } }
          else v = inp.value;
          TE.js.values[name] = v;
          TE.js.commit();
        });
      });

      /* multi-checkbox：每次改动重建该字段的数组 */
      TE.$$('[data-jsm]', c).forEach(function (cb) {
        cb.onchange = function () {
          var name = cb.getAttribute('data-jsm');
          var vals = TE.$$('[data-jsm="' + name.replace(/"/g, '\\"') + '"]', c)
            .filter(function (x) { return x.checked; })
            .map(function (x) { return x.getAttribute('data-jv'); });
          TE.js.values[name] = vals;
          TE.js.commit();
        };
      });

      /* cascade-select：任意一级变化都把各级当前值拼成数组 */
      TE.$$('[data-jc]', c).forEach(function (sel) {
        sel.onchange = function () {
          var name = sel.getAttribute('data-jc');
          var arr = TE.$$('[data-jc="' + name.replace(/"/g, '\\"') + '"]', c)
            .map(function (x) { return x.value; });
          TE.js.values[name] = arr;
          TE.js.commit();
        };
      });

      /* 原始 JSON 编辑（无 schema 时的兜底） */
      var raw = TE.el('jsRaw');
      if (raw) {
        raw.addEventListener('input', function () {
          try {
            TE.js.values = JSON.parse(raw.value);
            TE.js.commit();
            raw.classList.remove('bad');
          } catch (e) { raw.classList.add('bad'); }
        });
      }
    },

    /* 写回配置组内存模型（保存时随配置组一起提交） */
    commit: function () {
      var arr = (TE.group.data.projects || TE.group.data.Projects || []);
      var p = arr[TE.js.projIdx];
      if (!p) return;
      if ('jsScriptSettingsObject' in p) p.jsScriptSettingsObject = TE.js.values;
      else p.JsScriptSettingsObject = TE.js.values;
      TE.dirty.mark('group:' + TE.group.name);
    }
  };
})(window.TE);
""";
    }
}
