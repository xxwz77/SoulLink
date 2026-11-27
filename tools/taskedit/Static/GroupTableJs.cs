using System;

namespace SoulLink.TaskEdit
{
    // 配置组项目表格渲染（真实 project 的 13 个字段全覆盖）。从 GroupJs 拆出控制体积。
    internal static partial class TaskEditStatic
    {
        private const string GroupTableJs = """
'use strict';
(function (TE) {

  /*
   * 渲染项目表格。
   * ★ t27 补齐 Captain 指出的缺口 —— 真实 project 共 13 个字段，全部要有去处：
   *   index（= 行号）/ name / type / status / nextFlag / runNum / schedule /
   *   allowJsNotification / allowJsHTTPHash / folderName / taskCenterTaskId /
   *   jsScriptSettingsObject / soloTaskSettingsObject（后两个由各自表单编辑）
   */
  TE.group.tableHtml = function (projects) {
  var html = '';
  html += '<div class="project-scroll"><table class="grid' + (TE.group.details ? ' expanded' : '') + '" id="gTable"><thead><tr>' +
    '<th class="idx">#</th><th style="width:24px"></th><th>名称</th>' +
    '<th style="width:92px">类型</th><th style="width:64px">启用</th>' +
    '<th>操作</th><th class="advanced" style="width:72px" title="nextFlag：下次从这一项开始执行">下次启</th>' +
    '<th class="advanced" style="width:74px" title="runNum：本项运行次数">次数</th>' +
    '<th class="advanced" style="width:88px" title="schedule：调度表达式">调度</th>' +
    '<th class="advanced" style="width:104px" title="JS 权限开关">JS 权限</th>' +
    '<th class="advanced" style="width:96px" title="taskCenterTaskId：任务中心 ID">任务中心</th>' +
    '</tr></thead><tbody>';
  if (!projects.length) {
    html += '<tr><td colspan="11" style="padding:26px;text-align:center;color:var(--fg3)">' +
      '暂无项目。右键空白处或点「添加」新增。</td></tr>';
  }
  for (var i = 0; i < projects.length; i++) {
    var p = projects[i];
    var type = TE.group.typeOf(p);
    /* 兼容两种命名 */
    var status = TE.group.fv(p, 'status');
    var enabled = status ? status.toLowerCase() === 'enabled' : TE.group.fv(p, 'enabled') !== false;
    var nextFlag = TE.group.fv(p, 'nextFlag') === true;
    var runNum = TE.group.fv(p, 'runNum');
    var schedule = TE.group.fv(p, 'schedule') || '';
    var allowNotif = TE.group.fv(p, 'allowJsNotification') === true;
    var allowHash = TE.group.fv(p, 'allowJsHTTPHash');
    var tcId = TE.group.fv(p, 'taskCenterTaskId') || '';
    var folder = TE.group.fv(p, 'folderName') || '';
  
    html += '<tr data-i="' + i + '" draggable="true">' +
      '<td class="idx">' + (i + 1) + '</td>' +
      '<td class="grip" title="拖拽排序">≡</td>' +
      '<td title="' + TE.attr(p.name || p.Name || '') +
        (folder ? '  (folderName: ' + TE.attr(folder) + ')' : '') + '">' +
        TE.esc(p.name || p.Name || '(未命名)') + (nextFlag ? ' <strong>起点</strong>' : '') + '</td>' +
      '<td><span class="tag ' + type.cls + '">' + TE.esc(type.label) + '</span></td>' +
      '<td><label class="sw"><input type="checkbox" data-en="' + i + '"' +
        (enabled ? ' checked' : '') + '><i></i></label></td>' +
      '<td><button class="btn" data-menu="' + i + '">操作</button></td>' +
      '<td class="advanced"><label class="sw"><input type="checkbox" data-nf="' + i + '"' +
        (nextFlag ? ' checked' : '') + '><i></i></label></td>' +
      '<td class="advanced"><input type="number" min="0" data-rn="' + i + '" value="' +
        TE.attr(runNum === null || runNum === undefined ? '' : runNum) + '" style="width:100%"></td>' +
      '<td class="advanced"><input type="text" data-sch="' + i + '" value="' + TE.attr(schedule) +
        '" style="width:100%;font-size:12px" placeholder="—"></td>' +
      '<td class="advanced">' + (type.key === 'javascript' ?
        '<label><input type="checkbox" data-ajn="' + i + '"' + (allowNotif ? ' checked' : '') + '> 通知</label>' +
        '<div title="' + TE.attr(allowHash || '') + '">HTTP：' + (allowHash ? '已有授权记录' : '无记录') + '</div>' : '—') + '</td>' +
      '<td class="mono advanced" style="font-size:11px" title="' + TE.attr(tcId) + '">' +
        (tcId ? TE.esc(String(tcId).substring(0, 8)) : '—') + '</td>' +
      '</tr>';
  }
  return html + '</tbody></table></div>';
  };
})(window.TE);
""";
    }
}
