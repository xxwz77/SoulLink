using System;

namespace SoulLink.TaskEdit
{
    // 一条龙分区卡片（★ t27 按真实 97 字段重写，对齐 architect 的 FIELD-COVERAGE-20261125）。
    internal static partial class TaskEditStatic
    {
        private const string DragonSectionsJs = """
'use strict';
(function (TE) {

  Object.assign(TE.dragon, {

  /*
   * ★★ 分区表按 **architect 的 t30 穷尽清单** 组织：97 = form 85 + readonly 2 + ignore 10。
   *    ignore/readonly 不在这里渲染（见 readonlyCard），但**必须原样写回**（JsonNode 保留未知字段）。
   *    每个 key 都来自 A1 真实配置实测（见 docs/research/FIELD-COVERAGE-20261125.md）。
   */
  sectionsHtml: function (d) {
    var html = '';

    /* ── 账户 ── */
    html += TE.dragon.card(d, '账户', 'acct', [
      { k: 'GenshinUid', l: '游戏 UID', t: 'text' },
      { k: 'AccountBinding', l: '绑定账号', t: 'bool' },
      { k: 'AccountBindingCode', l: '绑定验证码', t: 'text' }
    ]);

    /* ── 周期 / 计划表 ── */
    html += TE.dragon.card(d, '周期与计划表', 'period', [
      { k: 'Period', l: '周期', t: 'enum', opts: ['每日', '每周', '每月'] },
      { k: 'ScheduleName', l: '计划表名', t: 'text' },
      { k: 'NextConfiguration', l: '使用下一配置', t: 'bool' }
    ]);
    html += TE.dragon.periodListCard(d);
    html += TE.dragon.card(d, '完成条件', 'name', [
      { k: 'Name', l: '配置名称', t: 'text' }
    ]);

    /* ── 合成树脂 ── */
    html += TE.dragon.card(d, '合成树脂', 'resin', [
      { k: 'SpecifyResinUse', l: '指定树脂用途', t: 'bool' },
      { k: 'MinResinToKeep', l: '最少保留树脂', t: 'num', min: 0 }
    ]);
    html += TE.dragon.resinCountCard(d);

    /* ── 自动秘境 ── */
    html += TE.dragon.card(d, '自动秘境', 'domain', [
      { k: 'PartyName', l: '队伍名', t: 'text' },
      { k: 'DomainName', l: '秘境名', t: 'text' },
      { k: 'WeeklyDomainEnabled', l: '启用周本', t: 'bool' },
      { k: 'CraftingBenchCountry', l: '合成台国家', t: 'text' },
      { k: 'AdventurersGuildCountry', l: '冒险家协会国家', t: 'text' },
      { k: 'SundayDaySelectedValue', l: '周日（按日）', t: 'text' }
    ]);
    html += TE.dragon.customDomainCard(d);

    /* ── 自动首领讨伐 ── */
    html += TE.dragon.card(d, '自动首领讨伐', 'boss', [
      { k: 'AutoBossName', l: '首领名', t: 'text' },
      { k: 'AutoBossStrategyName', l: '战斗策略', t: 'text' },
      { k: 'AutoBossTeamName', l: '队伍名', t: 'text' },
      { k: 'AutoBossRunCount', l: '运行次数', t: 'num', min: 1, d: '必须 ≥ 1' },
      { k: 'AutoBossTotalRunCountLimit', l: '总次数上限', t: 'num', min: 0 },
      { k: 'AutoBossReviveRetryCount', l: '复活重试次数', t: 'num', min: 0 },
      { k: 'AutoBossTimeout', l: '超时(秒)', t: 'num', min: 1 },
      { k: 'AutoBossSpecifyRunCount', l: '指定运行次数', t: 'bool' },
      { k: 'AutoBossUseTransientResin', l: '使用须臾树脂', t: 'bool' },
      { k: 'AutoBossUseFragileResin', l: '使用脆弱树脂', t: 'bool' },
      { k: 'AutoBossRewardRecognitionEnabled', l: '奖励识别', t: 'bool' },
      { k: 'AutoBossReturnToStatueAfterEachRound', l: '每轮后回神像', t: 'bool' }
    ]);

    /* ── 自动地脉花：3 个模式开关 + 主参数 + 一周表格 ── */
    html += TE.dragon.card(d, '自动地脉花', 'leyline', [
      { k: 'LeyLineOneDragonMode', l: '一条龙模式', t: 'bool' },
      { k: 'LeyLineOpenModeCountMin', l: '开启次数最少模式', t: 'bool' },
      { k: 'LeyLineResinExhaustionMode', l: '树脂耗尽模式', t: 'bool' },
      { k: 'LeyLineRunCount', l: '地脉花次数', t: 'num', min: 0 }
    ]);

    /* ── 区域 / 每周开关 / 尘歌壶 / 完成动作 ── */
    html += TE.dragon.card(d, '其它', 'misc', [
      { k: 'DailyRewardPartyName', l: '每日奖励队伍', t: 'text' },
      { k: 'SereniteaPotTpType', l: '尘歌壶传送方式', t: 'text' },
      { k: 'CompletionAction', l: '完成后操作', t: 'enum',
        opts: ['不执行', '关闭游戏', '关闭BGI', '关闭游戏和BGI', '关闭BGI并关机', '关闭游戏和BGI并关机'] }
    ]);
    html += TE.dragon.secretTreasureCard(d);

    /* ── ★ 一周表格（21 个 form 字段 + LeyLineRunXxx 7 个开关）── */
    html += TE.dragon.weekCard(d);

    /* ── 只读（不编辑但显示；写回保留）── */
    html += TE.dragon.readonlyCard(d);

    return html;
  },

  /* PeriodList：一周哪几天启用（对象 {每日/周一..} → 7+1 个开关） */
  periodListCard: function (d) {
    var pl = TE.dragon.read(d, 'PeriodList');
    if (!pl || typeof pl !== 'object') return '';
    var DAYS = [['每日','每日'],['周一','Mon'],['周二','Tue'],['周三','Wed'],['周四','Thu'],['周五','Fri'],['周六','Sat'],['周日','Sun']];
    var html = '<details class="card"><summary>周期启用日（PeriodList） ' +
      '<span class="badge tag">' + Object.keys(pl).length + '</span></summary>' +
      '<div class="body"><div class="row" style="flex-wrap:wrap;gap:14px">';
    for (var i = 0; i < DAYS.length; i++) {
      var key = DAYS[i][0];
      var has = Object.prototype.hasOwnProperty.call(pl, key);
      html += '<label style="display:flex;align-items:center;gap:6px;color:#a8b0c0">' +
        '<input type="checkbox" data-plist="' + TE.attr(key) + '"' +
        (pl[key] === true ? ' checked' : '') + (has ? '' : ' disabled') + '>' +
        TE.esc(key) + '</label>';
    }
    html += '</div></div></details>';
    return html;
  },

  /* ResinCount：嵌套对象（浓缩/原粹/须臾/脆弱 各一个数字） */
  resinCountCard: function (d) {
    var rc = TE.dragon.read(d, 'ResinCount');
    if (!rc || typeof rc !== 'object') return '';
    var html = '<details class="card" open><summary>树脂数量（ResinCount） ' +
      '<span class="badge tag">' + Object.keys(rc).length + '</span></summary><div class="body">';
    for (var k in rc) {
      if (!Object.prototype.hasOwnProperty.call(rc, k)) continue;
      if (typeof rc[k] === 'object') continue;
      html += '<div class="field"><label>' + TE.esc(k) + '</label><div class="ctl">' +
        '<input type="number" min="0" data-nested="ResinCount.' + TE.attr(k) + '" value="' +
        TE.attr(rc[k] === null || rc[k] === undefined ? '' : rc[k]) + '"></div></div>';
    }
    html += '</div></details>';
    return html;
  },

  /* CustomDomainList：自定义秘境列表（数组）→ 逐项编辑 + 增删 */
  customDomainCard: function (d) {
    var list = TE.dragon.arr(TE.dragon.read(d, 'CustomDomainList'));
    var html = '<details class="card"><summary>自定义秘境（CustomDomainList） ' +
      '<span class="badge tag">' + list.length + '</span></summary><div class="body">';
    if (!list.length) {
      html += '<div class="hint">当前为空。' +
        '该字段是对象数组，结构随 BGI 版本变化 —— 用下面的原始 JSON 编辑更安全。</div>';
    }
    html += TE.dragon.rawEditor('CustomDomainList', TE.dragon.read(d, 'CustomDomainList'));
    html += '</div></details>';
    return html;
  },

  /* SecretTreasureObjects：秘宝罗盘目标（多选，已知 8 种商品） */
  secretTreasureCard: function (d) {
    var cur = TE.dragon.arr(TE.dragon.read(d, 'SecretTreasureObjects'));
    var OPTS = ['布匹', '须臾树脂', '大英雄的经验', '流浪者的经验', '精锻用魔矿', '摩拉', '祝圣精华', '祝圣油膏'];
    var html = '<details class="card"><summary>秘宝罗盘目标（SecretTreasureObjects） ' +
      '<span class="badge tag">' + cur.length + '</span></summary><div class="body">' +
      '<div class="desc" style="margin-bottom:8px">勾选要兑换的商品：</div>' +
      '<div class="row" style="flex-wrap:wrap;gap:14px">';
    for (var i = 0; i < OPTS.length; i++) {
      html += '<label style="display:flex;align-items:center;gap:6px;color:#a8b0c0">' +
        '<input type="checkbox" data-sto="' + TE.attr(OPTS[i]) + '"' +
        (cur.indexOf(OPTS[i]) >= 0 ? ' checked' : '') + '>' + TE.esc(OPTS[i]) + '</label>';
    }
    html += '</div>';
    /* 未知项（配置里有但不在已知 8 种里）也要能看见/保留 */
    var unknown = [];
    for (var j = 0; j < cur.length; j++) if (OPTS.indexOf(cur[j]) < 0) unknown.push(cur[j]);
    if (unknown.length) {
      html += '<div class="banner warn" style="margin-top:10px">配置里还有 ' + unknown.length +
        ' 项不在已知列表：' + TE.esc(unknown.join('、')) + '（会原样保留）</div>';
    }
    html += '</div></details>';
    return html;
  },

  /*
  /*
   * 复杂控件的绑定（PeriodList / ResinCount / SecretTreasureObjects / 原始 JSON）。
   * 从 bind() 里抽出来，让 DragonBindJs 保持精简。
   */
  bindExtras: function (d, c) {
    /* PeriodList：对象里每个布尔 */
    TE.$$('[data-plist]', c).forEach(function (cb) {
      cb.onchange = function () {
        var pl = TE.dragon.read(d, 'PeriodList');
        if (!pl || typeof pl !== 'object') return;
        var key = cb.getAttribute('data-plist');
        if (!Object.prototype.hasOwnProperty.call(pl, key)) return;
        pl[key] = cb.checked;
        TE.dirty.mark('dragon:' + TE.dragon.name);
      };
    });

    /* ResinCount：嵌套对象数字（路径 ResinCount.xxx） */
    TE.$$('[data-nested]', c).forEach(function (inp) {
      inp.addEventListener('change', function () {
        var path = inp.getAttribute('data-nested').split('.');
        var obj = TE.dragon.read(d, path[0]);
        if (!obj || typeof obj !== 'object') return;
        if (!Object.prototype.hasOwnProperty.call(obj, path[1])) return;
        var v = inp.value === '' ? null : TE.num(inp.value, 0);
        if (typeof v === 'number' && v < 0) { v = 0; inp.value = '0'; }
        obj[path[1]] = v;
        TE.dirty.mark('dragon:' + TE.dragon.name);
      });
    });

    /* SecretTreasureObjects：多选数组 */
    TE.$$('[data-sto]', c).forEach(function (cb) {
      cb.onchange = function () {
        var cur = TE.dragon.arr(TE.dragon.read(d, 'SecretTreasureObjects'));
        var val = cb.getAttribute('data-sto');
        var at = cur.indexOf(val);
        if (cb.checked && at < 0) cur.push(val);
        if (!cb.checked && at >= 0) cur.splice(at, 1);
        TE.dragon.write(d, 'SecretTreasureObjects', cur);
        TE.dirty.mark('dragon:' + TE.dragon.name);
      };
    });

    /* 原始 JSON 兜底：语法错误标红且不写入 */
    TE.$$('[data-raw]', c).forEach(function (ta) {
      var handler = function () {
        var field = ta.getAttribute('data-raw');
        try {
          var parsed = JSON.parse(ta.value);
          TE.dragon.write(d, field, parsed);
          ta.classList.remove('bad');
          var tip = ta.parentNode.querySelector('.err-tip');
          if (tip) tip.remove();
          TE.dirty.mark('dragon:' + TE.dragon.name);
        } catch (e) {
          ta.classList.add('bad');
          var t2 = ta.parentNode.querySelector('.err-tip');
          if (!t2) { t2 = document.createElement('div'); t2.className = 'err-tip'; ta.parentNode.appendChild(t2); }
          t2.textContent = 'JSON 语法错误：' + e.message + '（不会保存）';
        }
      };
      ta.addEventListener('input', handler);
      ta.addEventListener('change', handler);
    });
  },

  /* 通用原始 JSON 编辑器（结构复杂字段的兜底，界面上明确标注） */
  rawEditor: function (fieldName, value) {
    var id = 'raw-' + fieldName;
    var txt = '';
    try { txt = JSON.stringify(value === undefined ? null : value, null, 2); } catch (e) { txt = 'null'; }
    return '<div class="field"><label>原始 JSON</label><div class="ctl">' +
      '<textarea id="' + id + '" data-raw="' + TE.attr(fieldName) + '" ' +
      'style="min-height:120px;font-family:monospace">' + TE.esc(txt) + '</textarea>' +
      '<div class="desc" style="color:#ffb454">⚠ 该项使用<b>原始 JSON 编辑</b>：' +
      '结构随 BGI 版本变化，没有稳定表单。语法错误会标红且不会保存。</div></div></div>';
  },

  /* 只读字段卡片（readonly 2 个 + ignore 10 个：不编辑，但写回保留） */
  readonlyCard: function (d) {
    var RO = [
      ['AutoBossCompletedRunCount', '首领已完成次数', '运行时累计'],
      ['NextTaskId', '下次执行任务', '由右键「从此执行」菜单驱动'],
      ['IndexId', '排序索引', '系统维护'],
      ['Version', 'schema 版本', '迁移用'],
      ['EverySelectedValueListConverter', '序列化转换器', 'BGI 内部']
    ];
    var IGN = ['IsAutoDomainExpanded', 'IsAutoLeyLineExpanded', 'IsBindUidExpanded',
      'IsClaimRewardsExpanded', 'IsCompletionActionExpanded', 'IsCraftingResinExpanded',
      'IsSereniteaPotExpanded'];
    var html = '<details class="card"><summary>只读 / 系统字段 ' +
      '<span class="badge tag">' + (RO.length + IGN.length) + ' 项（不编辑，写回原样保留）</span>' +
      '</summary><div class="body" style="font-size:13px;color:#a8b0c0">';
    for (var i = 0; i < RO.length; i++) {
      var v = TE.dragon.read(d, RO[i][0]);
      html += '<div class="field"><label>' + TE.esc(RO[i][0]) + '</label><div class="ctl">' +
        '<span class="mono">' + TE.esc(v === null || v === undefined ? '—' : String(v)) + '</span>' +
        '<div class="desc">' + TE.esc(RO[i][2]) + '</div></div></div>';
    }
    html += '<div class="desc" style="margin-top:10px">UI 折叠状态（不渲染，写回保留）：' +
      TE.esc(IGN.join(' / ')) + '</div>';
    html += '</div></details>';
    return html;
  },

  /* 通用卡片 */
  card: function (d, title, key, fields) {
    var html = '<details class="card" open><summary>' + TE.esc(title) + '</summary><div class="body">';
    for (var i = 0; i < fields.length; i++) {
      var f = fields[i];
      var raw = TE.dragon.read(d, f.k);
      var has = raw !== null && raw !== undefined;
      var id = 'd-' + key + '-' + f.k;
      html += '<div class="field"><label for="' + id + '">' + TE.esc(f.l) +
        (has ? '' : ' <span class="tag" style="opacity:.5" title="配置中不存在">缺</span>') +
        '</label><div class="ctl">';
      if (f.t === 'bool') {
        html += '<label class="sw"><input type="checkbox" id="' + id + '" data-dk="' + TE.attr(f.k) +
          '" data-dt="bool"' + (raw === true ? ' checked' : '') + (has ? '' : ' disabled') + '><i></i></label>';
      } else if (f.t === 'num') {
        html += '<input type="number" id="' + id + '" data-dk="' + TE.attr(f.k) + '" data-dt="num"' +
          (f.min !== undefined ? ' min="' + f.min + '"' : '') +
          ' value="' + TE.attr(has ? raw : '') + '"' + (has ? '' : ' disabled') + '>';
      } else if (f.t === 'enum') {
        html += '<select id="' + id + '" data-dk="' + TE.attr(f.k) + '" data-dt="text"' +
          (has ? '' : ' disabled') + '>';
        for (var o = 0; o < f.opts.length; o++) {
          html += '<option value="' + TE.attr(f.opts[o]) + '"' +
            (String(raw) === f.opts[o] ? ' selected' : '') + '>' + TE.esc(f.opts[o]) + '</option>';
        }
        html += '</select>';
      } else {
        html += '<input type="text" id="' + id + '" data-dk="' + TE.attr(f.k) +
          '" data-dt="text" value="' + TE.attr(TE.str(raw)) + '"' + (has ? '' : ' disabled') + '>';
      }
      if (f.d) html += '<div class="desc">' + TE.esc(f.d) + '</div>';
      html += '</div></div>';
    }
    html += '</div></details>';
    return html;
  }
  });
})(window.TE);
""";
    }
}