using System;

namespace SoulLink.TaskEdit
{
    // 一条龙编辑器：任务列表 + 可折叠分区卡片。
    internal static partial class TaskEditStatic
    {
        private const string DragonJs = """
'use strict';
(function (TE) {

  TE.dragon = TE.dragon || {};

  Object.assign(TE.dragon, {
    name: '', data: null,

    open: function (name) {
      TE.dragon.name = name;
      TE.busy(true, '加载一条龙「' + name + '」…');
      TE.api.get('/onedragons/' + encodeURIComponent(name)).then(function (d) {
        if (!d || d._status >= 400 || d.ok === false) {
          TE.el('canvas').innerHTML = '<div class="banner err">读取失败：' + TE.esc(d.error || '未知错误') + '</div>';
          return;
        }
        TE.dragon.data = d;
        TE.dragon.render();
      }).catch(function (e) {
        TE.el('canvas').innerHTML = '<div class="banner err">读取失败：' + TE.esc(e.message) + '</div>';
      });
    },

    render: function () {
      var d = TE.dragon.data || {};
      var html = '';

      html += '<div class="head"><h2>' + TE.esc(TE.dragon.name) + '</h2>' +
        '<span class="meta">一条龙</span><span class="grow"></span>' +
        '<button class="btn" id="dRun">运行</button>' +
        '<button class="btn" id="dAdd">添加任务</button></div>';

      html += '<div class="banner info" style="font-size:12px">' +
        '任务顺序可在左侧列表拖拽调整；右侧分区按下表顺序渲染，字段校验错误会标红到具体输入框。</div>';

      /* 两栏：左任务列表 / 右分区卡片 */
      html += '<div style="display:flex;gap:14px;align-items:flex-start;flex-wrap:wrap">';
      html += '<div style="flex:0 0 300px;min-width:240px">' + TE.dragon.taskListHtml(d) + '</div>';
      html += '<div style="flex:1;min-width:340px">' + TE.dragon.sectionsHtml(d) + '</div>';
      html += '</div>';

      TE.el('canvas').innerHTML = html;
      TE.dragon.bind(d);
    },

    /* 左：任务列表（拖拽 + 开关） */
    taskListHtml: function (d) {
      /* ★★ 实测订正（2026-11-25，Captain 复核 + 本人二次实测 A2 全部 4 个一条龙）★★
         这三个字段全部是 **PascalCase**（设计文档 §4.1 的 camelCase 示例是错的）。
         **TaskDefinitions 是 GUID → 中文名 的字典（对象），不是数组、也不总是空**：
             实测：测试测试测试=9 项 / 联机=4 项 / 启动=2 项 / 炸车=4 项
             例：{"1bba35cb-…":"异常处理", "758467e1-…":"进入莫酱-循环上线", …}
         TaskOrder 是这些 GUID 的有序数组，TaskEnabledList 是启用的 GUID 子集。
         → 任务中文名**优先从 TaskDefinitions 取**（用户自定义名只存在于那里，
           例如「进入莫酱-循环上线」内置表里根本没有）；
           只有 TaskDefinitions 缺该项时才降级到内置常量对照表。 */
      var defsMap = TE.dragon.defsMap(d.TaskDefinitions || d.taskDefinitions);
      var enabled = TE.dragon.arr(d.TaskEnabledList || d.taskEnabledList);
      var order = TE.dragon.arr(d.TaskOrder || d.taskOrder);

      /* 按 TaskOrder 排序；TaskDefinitions 里多出来的（未编排）追加在后 */
      var ids = order.slice();
      var defKeys = Object.keys(defsMap);
      for (var k = 0; k < defKeys.length; k++) {
        if (ids.indexOf(defKeys[k]) < 0) ids.push(defKeys[k]);
      }

      var html = '<details class="card" open><summary>任务列表 <span class="badge tag">' +
        ids.length + '</span></summary><div class="body" style="padding:6px">';
      if (!ids.length) {
        html += '<div class="hint">该一条龙没有编排任务（TaskOrder 与 TaskDefinitions 均为空）。</div>' +
          '</div></details>';
        return html;
      }
      html += '<table class="grid" id="dTable"><tbody>';
      for (var j = 0; j < ids.length; j++) {
        var id = String(ids[j]);
        /* ★ 中文名：TaskDefinitions 优先（含用户自定义名），取不到才用内置对照表 */
        var nm = defsMap[id] || TE.dragon.taskLabel(id);
        var on = enabled.indexOf(ids[j]) >= 0 || enabled.indexOf(id) >= 0;
        html += '<tr data-id="' + TE.attr(id) + '" data-i="' + j + '" draggable="true">' +
          '<td class="grip" style="width:20px">≡</td>' +
          '<td title="' + TE.attr(nm) + '">' + TE.esc(nm) + '</td>' +
          '<td style="width:48px"><label class="sw"><input type="checkbox" data-den="' + TE.attr(id) + '"' +
          (on ? ' checked' : '') + '><i></i></label></td></tr>';
      }
      html += '</tbody></table></div></details>';
      return html;
    },

    /* 把任意值规整成数组（真实配置里可能是 null / 对象 / 数组）。 */
    arr: function (v) {
      if (Array.isArray(v)) return v.slice();
      if (v === null || v === undefined) return [];
      if (typeof v === 'object') return Object.keys(v);
      return [];
    },

    /*
     * TaskDefinitions 归一成 { guid: 显示名 } 字典。
     * 真实形状（A2 实测）：{"<guid>":"异常处理", "<guid>":"进入莫酱-循环上线", …}
     * 也兼容两种退化形状：数组 [{id,name}] 与纯字符串数组。
     */
    defsMap: function (v) {
      var out = {};
      if (!v) return out;
      if (Array.isArray(v)) {
        for (var i = 0; i < v.length; i++) {
          var e = v[i];
          if (e === null || e === undefined) continue;
          if (typeof e === 'object') {
            var k = e.id || e.Id || e.key || e.Key;
            var n = e.name || e.Name || e.value || e.Value || k;
            if (k) out[String(k)] = String(n);
          } else out[String(e)] = String(e);
        }
        return out;
      }
      if (typeof v === 'object') {
        for (var key in v) {
          if (!Object.prototype.hasOwnProperty.call(v, key)) continue;
          var val = v[key];
          /* 值可能是字符串（正常）或对象（防御） */
          out[key] = (val && typeof val === 'object')
            ? String(val.name || val.Name || val.value || key)
            : String(val === null || val === undefined ? key : val);
        }
      }
      return out;
    },

    /* 任务 ID → 中文显示名（BGI 内置任务的中文名对照；未知则原样显示）。 */
    taskLabel: function (id) {
      var MAP = {
        'AutoGetReward': '领取邮件奖励',
        'AutoDailyReward': '领取每日奖励',
        'AutoDomain': '自动秘境',
        'AutoBoss': '自动首领讨伐',
        'AutoLeyLineOutcrop': '自动地脉花',
        'AutoHoeing': '自动尘歌壶',
        'AutoFight': '自动战斗',
        'AutoCook': '自动烹饪',
        'AutoWood': '自动伐木',
        'AutoFishing': '自动钓鱼',
        'AutoArtifactSalvage': '圣遗物分解',
        'AutoMusicGame': '自动音游'
      };
      var key = String(id || '');
      if (MAP[key]) return MAP[key];
      /* 宽松匹配：以某个键开头/包含 */
      for (var k in MAP) {
        if (key.indexOf(k) >= 0) return MAP[k] + '（' + key + '）';
      }
      return key || '(未命名任务)';
    },

    /* 读字段：PascalCase 优先，再试 camelCase */
    read: function (d, name) {
      if (!d) return null;
      if (name in d) return d[name];
      var camel = name.charAt(0).toLowerCase() + name.slice(1);
      if (camel in d) return d[camel];
      return null;
    },

    write: function (d, name, v) {
      if (name in d) { d[name] = v; return; }
      var camel = name.charAt(0).toLowerCase() + name.slice(1);
      if (camel in d) { d[camel] = v; return; }
      d[name] = v;
    },
  });
})(window.TE);
""";
    }
}