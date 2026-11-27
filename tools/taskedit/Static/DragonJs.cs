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
      return TE.tree.readDetail('/onedragons/' + encodeURIComponent(name), function (d) {
        if (!d || d._status >= 400 || d.ok === false) {
          TE.el('canvas').innerHTML = '<div class="banner err">读取失败：' + TE.esc((d && d.error) || '未知错误') + '</div>';
          return;
        }
        var draft = TE.draft.load('dragon:' + name);
        TE.dragon.data = draft || d;
        if (draft) { TE.dirty.mark('dragon:' + name); TE.toast('已恢复本账号的未保存草稿', 'warn'); }
        TE.dragon.render();
      }, function (e) {
        TE.el('canvas').innerHTML = '<div class="banner err">读取失败：' + TE.esc(e.message) + '</div>';
      });
    },

    render: function () {
      var d = TE.dragon.data || {};
      var html = TE.dragonOps.bar();

      html += '<div class="dragon-layout"><section>' + TE.dragon.taskListHtml(d) + '</section>';
      html += '<section><div class="native-tools"><h2>配置</h2>' +
        '<button class="btn config-choice" id="dChoose" aria-label="切换一条龙配置">' + TE.esc(TE.dragon.name) + ' ▾</button>' +
        '<button class="btn" id="dCreate">新建</button>' +
        '<button class="btn" id="dRename">改名</button>' +
        '<button class="btn" id="dDelete">删除</button></div>' +
        TE.dragon.sectionsHtml(d) + '</section>';
      html += '</div>';

      TE.el('canvas').innerHTML = html;
      TE.dragon.bind(d);
      TE.dragonOps.bind();
    },

    taskIds: function (d) {
      var order = TE.dragon.arr(TE.dragon.read(d, 'TaskOrder'));
      var definitions = TE.dragon.defsMap(TE.dragon.read(d, 'TaskDefinitions'));
      var enabled = TE.dragon.arr(TE.dragon.read(d, 'TaskEnabledList'));
      return Array.from(new Set(order.concat(Object.keys(definitions), enabled)));
    },

    taskListHtml: function (d) {
      var defsMap = TE.dragon.defsMap(TE.dragon.read(d, 'TaskDefinitions'));
      var enabled = TE.dragon.read(d, 'TaskEnabledList') || {};
      var ids = TE.dragon.taskIds(d);
      var next = TE.dragon.read(d, 'NextTaskId');

      var html = '<div class="native-tools"><h2>任务列表</h2>' +
        '<button class="btn" id="dRun">保存并执行</button>' +
        '<button class="btn" id="dAdd" title="添加配置组">添加</button></div><div class="task-list">';
      if (!ids.length) {
        html += '<div class="hint">该一条龙没有编排任务（TaskOrder 与 TaskDefinitions 均为空）。</div>' +
          '</div>';
        return html;
      }
      html += '<table class="grid" id="dTable"><tbody>';
      for (var j = 0; j < ids.length; j++) {
        var id = String(ids[j]);
        /* ★ 中文名：TaskDefinitions 优先（含用户自定义名），取不到才用内置对照表 */
        var nm = defsMap[id] || TE.dragon.taskLabel(id);
        var on = Array.isArray(enabled) ? enabled.includes(id) : enabled[id] === true;
        html += '<tr data-id="' + TE.attr(id) + '" data-i="' + j + '" draggable="true">' +
          '<td class="grip" style="width:20px">≡</td>' +
          '<td title="' + TE.attr(nm) + '">' + TE.esc(nm) + (next === id ? ' <strong>起点</strong>' : '') + '</td>' +
          '<td><button class="btn" data-start="' + TE.attr(id) + '"' + (on && next !== id ? '' : ' disabled') + '>从此执行</button></td>' +
          '<td style="width:48px"><label class="sw"><input type="checkbox" data-den="' + TE.attr(id) + '"' +
          (on ? ' checked' : '') + '><i></i></label></td></tr>';
      }
      html += '</tbody></table></div><button class="btn sm" id="dClear"' +
        (next ? '' : ' disabled') + '>清除起点</button>';
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
      var out = Object.create(null);
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