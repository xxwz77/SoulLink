using System;

namespace SoulLink.TaskEdit
{
    // 配置组设置面板：按**真实嵌套结构**渲染（t27 订正）。
    internal static partial class TaskEditStatic
    {
        private const string GroupSettingsJs = """
'use strict';
(function (TE) {

  /*
   * ★★ 真实结构（A1 39 个配置组全量扫描 + Captain 复核，2026-11-25）★★
   *
   *   config = {
   *     pathingConfig        { 45 个字段 + 6 个嵌套对象 }   ← 主区，且**战斗/吃食物都在它里面**
   *       ├─ autoFightConfig          47 字段（含 useOfficialAutoFight 茶包独有）
   *       │    └─ finishDetectConfig  24 字段（战斗结束检测）
   *       ├─ autoFightOfficialConfig  24 字段（公版战斗引擎独立一套）
   *       ├─ autoEatConfig             8 字段（自动吃食物）
   *       ├─ taskCycleConfig           5 字段
   *       ├─ taskCompletionSkipRuleConfig 6 字段
   *       └─ preExecutionPriorityConfig   3 字段
   *     shellConfig          { disable, timeout, noWindow, output }
   *     enableShellConfig    bool
   *     autoPickConfig       { whiteList, blackList }
   *   }
   *
   * ★ 设计文档 §4.1 写的是「config{pathingConfig, shellConfig, enableShellConfig, autoPickConfig}」，
   *   这个顶层四区是对的；但我 P1 版把 autoFightConfig / autoEatConfig 当成了**顶层**区 —— 错的。
   *   它们嵌在 pathingConfig 里。本文件按真实结构渲染。
   */

  /* 各区字段表在 GroupSchemaJs.cs（TE.group.TE.group.SECTIONS） */

  /* 沿 path 取值（不存在返回 null）。 */
  TE.group.walk = function (root, path) {
    var cur = root;
    for (var i = 0; i < path.length; i++) {
      if (!cur || typeof cur !== 'object') return null;
      cur = cur[path[i]];
    }
    return cur || null;
  };

  TE.group.settingsHtml = function (cfg) {
    cfg = cfg || {};
    var html = '<details class="card" id="sec-config"><summary>配置组设置 ' +
      '<span class="badge tag">' + TE.group.SECTIONS.length + ' 个区</span></summary><div class="body">';

    html += '<div class="field"><label>enableShellConfig</label><div class="ctl">' +
      '<label class="sw"><input type="checkbox" data-cfg="enableShellConfig" data-ct="bool"' +
      (cfg.enableShellConfig === true ? ' checked' : '') + '><i></i></label>' +
      '<div class="desc">顶层开关：启用 Shell 配置</div></div></div>';

    for (var s = 0; s < TE.group.SECTIONS.length; s++) {
      var sec = TE.group.SECTIONS[s];
      var obj = TE.group.walk(cfg, sec.path) || {};
      var keys = Object.keys(obj).length;
      html += '<details class="card" id="sec-' + sec.id + '" open><summary>' + TE.esc(sec.title) +
        ' <span class="badge tag">' + keys + ' 键</span></summary><div class="body">';
      if (sec.note) html += '<div class="desc" style="margin-bottom:8px">' + TE.esc(sec.note) + '</div>';
      if (!keys) {
        html += '<div class="banner warn">该区在配置里不存在（可能是老版本或未启用）。' +
          '保存时不会凭空创建。</div>';
      } else {
        html += TE.group.fieldsHtml(sec, obj);
      }
      html += '</div></details>';
    }
    html += '</div></details>';
    return html;
  };

  TE.group.fieldsHtml = function (sec, obj) {
    var html = '';
    for (var i = 0; i < sec.fields.length; i++) {
      var f = sec.fields[i];
      var name = f[0], label = f[1], type = f[2], extra = f[3];
      var id = 'f-' + sec.id + '-' + name;
      /* ★ 只渲染**真实存在**的键：不凭空造字段（避免写坏别人的配置） */
      var has = Object.prototype.hasOwnProperty.call(obj, name);
      var raw = has ? obj[name] : null;

      html += '<div class="field"><label for="' + id + '">' + TE.esc(label) +
        (extra === 'teabag' ? ' <span class="tag" title="仅茶包版">茶包</span>' : '') +
        (!has ? ' <span class="tag" style="opacity:.5" title="配置中不存在该键">缺</span>' : '') +
        '</label><div class="ctl">';

      if (type === 'bool') {
        html += '<label class="sw"><input type="checkbox" id="' + id + '" data-cpath="' +
          sec.path.join('.') + '" data-cname="' + TE.attr(name) + '" data-ct="bool"' +
          (raw === true ? ' checked' : '') + (has ? '' : ' disabled') + '><i></i></label>';
      } else if (type === 'num') {
        html += '<input type="number" id="' + id + '" data-cpath="' + sec.path.join('.') +
          '" data-cname="' + TE.attr(name) + '" data-ct="num" value="' +
          TE.attr(raw === null || raw === undefined ? '' : raw) + '"' + (has ? '' : ' disabled') + '>';
      } else if (type === 'enum' && Array.isArray(extra)) {
        html += '<select id="' + id + '" data-cpath="' + sec.path.join('.') +
          '" data-cname="' + TE.attr(name) + '" data-ct="text"' + (has ? '' : ' disabled') + '>';
        for (var o = 0; o < extra.length; o++) {
          html += '<option value="' + TE.attr(extra[o]) + '"' +
            (String(raw) === extra[o] ? ' selected' : '') + '>' + TE.esc(extra[o]) + '</option>';
        }
        html += '</select>';
      } else if (type === 'textarea') {
        html += '<textarea id="' + id + '" data-cpath="' + sec.path.join('.') +
          '" data-cname="' + TE.attr(name) + '" data-ct="text"' + (has ? '' : ' disabled') + '>' +
          TE.esc(TE.str(raw)) + '</textarea>';
      } else {
        html += '<input type="text" id="' + id + '" data-cpath="' + sec.path.join('.') +
          '" data-cname="' + TE.attr(name) + '" data-ct="text" value="' +
          TE.attr(TE.str(raw)) + '"' + (has ? '' : ' disabled') + '>';
      }
      html += '</div></div>';
    }
    return html;
  };

  /* 绑定：只写回**原本就存在**的键（不新造）。 */
  TE.group.bindSettings = function (cfg) {
    var c = TE.el('canvas');

    TE.$$('[data-cfg]', c).forEach(function (inp) {
      inp.addEventListener('change', function () {
        cfg[inp.getAttribute('data-cfg')] = inp.checked;
        TE.dirty.mark('group:' + TE.group.name);
      });
    });

    TE.$$('[data-cpath]', c).forEach(function (inp) {
      var handler = function () {
        var path = inp.getAttribute('data-cpath').split('.');
        var name = inp.getAttribute('data-cname');
        var t = inp.getAttribute('data-ct');
        var obj = TE.group.walk(cfg, path);
        if (!obj) return;                       // 区不存在 → 不写
        if (!Object.prototype.hasOwnProperty.call(obj, name)) return;  // 键不存在 → 不写
        var v;
        if (t === 'bool') v = inp.checked;
        else if (t === 'num') v = inp.value === '' ? null : TE.num(inp.value, 0);
        else v = inp.value;
        obj[name] = v;
        TE.group.validateNum(inp, name, v);
        TE.dirty.mark('group:' + TE.group.name);
      };
      inp.addEventListener('change', handler);
      if (inp.tagName === 'INPUT' && inp.type !== 'checkbox') inp.addEventListener('input', handler);
    });
  };

  TE.group.validateNum = function (inp, name, v) {
    var bad = null;
    if (v !== null && v !== '' && typeof v === 'number') {
      if ((name === 'timeout' || name === 'Timeout') && v < 1) bad = '必须 ≥ 1';
      if (name === 'rotaryFactor' && (v < 1 || v > 13)) bad = '范围 1~13';
      if (v < 0) bad = '不能为负';
    }
    inp.classList.toggle('bad', !!bad);
    var tip = inp.parentNode.querySelector('.err-tip');
    if (bad) {
      if (!tip) { tip = document.createElement('div'); tip.className = 'err-tip'; inp.parentNode.appendChild(tip); }
      tip.textContent = bad;
    } else if (tip) { tip.remove(); }
  };

})(window.TE);
""";
    }
}