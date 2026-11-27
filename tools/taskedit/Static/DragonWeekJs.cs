using System;

namespace SoulLink.TaskEdit
{
    // 一条龙「一周表格」（逐日配置）：真 7 列表格，共 42 个控件。
    internal static partial class TaskEditStatic
    {
        private const string DragonWeekJs = """
'use strict';
(function (TE) {

  Object.assign(TE.dragon, {

    /*
     * ★ 一周表格（真表格，不是 7 个折叠面板 —— 用户明确要求）。
     *   覆盖 28 个 form 字段：
     *     · 每天 3 个：{Day}SelectedValue / {Day}PartyName / {Day}DomainName（7×3 = 21）
     *     · 地脉跑日开关：LeyLineRun{Mon..Sun}（7 个）
     *     · 地脉每天 2 个：LeyLine{Day}Type / LeyLine{Day}Country（7×2 = 14）
     *   合计 21 + 7 + 14 = 42 个控件。
     */
    weekCard: function (d) {
      var DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
      var CN = ['周一', '周二', '周三', '周四', '周五', '周六', '周日'];
      var SHORT = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
  
      var html = '<details class="card"><summary>逐日配置 ' +
        '<span class="badge tag">一周表格</span></summary><div class="body">';
  
      /* ── 表 1：秘境/队伍/选择值 ── */
      html += '<div style="overflow:auto"><table class="week"><thead><tr><th>项目</th>';
      for (var i = 0; i < 7; i++) html += '<th class="' + (i >= 5 ? 'weekend' : '') + '">' + CN[i] + '</th>';
      html += '</tr></thead><tbody>';
  
      var ROWS1 = [
        { suffix: 'SelectedValue', label: '选择值', t: 'text' },
        { suffix: 'PartyName', label: '队伍名', t: 'text' },
        { suffix: 'DomainName', label: '秘境名', t: 'text' }
      ];
      for (var r = 0; r < ROWS1.length; r++) {
        html += '<tr><td class="rowhead">' + TE.esc(ROWS1[r].label) + '</td>';
        for (var j = 0; j < 7; j++) {
          var key = DAYS[j] + ROWS1[r].suffix;
          var v = TE.dragon.read(d, key);
          var has = v !== null && v !== undefined;
          html += '<td><input type="text" data-dk="' + TE.attr(key) + '" data-dt="text" value="' +
            TE.attr(TE.str(v)) + '" style="max-width:110px;font-size:12px"' +
            (has ? '' : ' disabled') + '></td>';
        }
        html += '</tr>';
      }
  
      /* ── 表 2：地脉花每天类型/国家 + 是否跑 ── */
      html += '<tr><td class="rowhead" colspan="8" style="padding-top:12px;color:var(--acc)">' +
        '地脉花（每周）</td></tr>';
      html += '<tr><td class="rowhead">是否跑</td>';
      for (var k = 0; k < 7; k++) {
        var rk = 'LeyLineRun' + DAYS[k];
        var rv = TE.dragon.read(d, rk);
        html += '<td style="text-align:center"><input type="checkbox" data-dk="' + TE.attr(rk) +
          '" data-dt="bool"' + (rv === true ? ' checked' : '') + '></td>';
      }
      html += '</tr>';
      html += '<tr><td class="rowhead">类型</td>';
      for (var m = 0; m < 7; m++) {
        var tk = 'LeyLine' + DAYS[m] + 'Type';
        var tv = TE.dragon.read(d, tk);
        html += '<td><input type="text" data-dk="' + TE.attr(tk) + '" data-dt="text" value="' +
          TE.attr(TE.str(tv)) + '" style="max-width:110px;font-size:12px"></td>';
      }
      html += '</tr>';
      html += '<tr><td class="rowhead">国家</td>';
      for (var n = 0; n < 7; n++) {
        var ck = 'LeyLine' + DAYS[n] + 'Country';
        var cv = TE.dragon.read(d, ck);
        html += '<td><input type="text" data-dk="' + TE.attr(ck) + '" data-dt="text" value="' +
          TE.attr(TE.str(cv)) + '" style="max-width:110px;font-size:12px"></td>';
      }
      html += '</tr>';
      html += '</tbody></table></div>';
  
      html += '<div class="desc" style="margin-top:8px">' +
        '共 42 个控件（选择值/队伍/秘境 × 7 + 地脉跑日 × 7 + 地脉类型/国家 × 14）。' +
        '「是否跑」对应 LeyLineRun{Mon..Sun}；4 点起算服务日边界。</div>';
      html += '</div></details>';
      return html;
    },
  
  });
})(window.TE);
""";
    }
}
