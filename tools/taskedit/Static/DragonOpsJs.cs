namespace SoulLink.TaskEdit
{
    internal static partial class TaskEditStatic
    {
        private const string DragonOpsJs = """
'use strict';
(function (TE) {
  const ops = TE.dragonOps = {state:'unknown', bar, bind, enter, empty, save};
  const labels = {running:'运行中 · 请先关闭 BGI 再编辑', stopped:'已关闭', unknown:'状态未知 · 只读'};
  let detail = '', serial = 0;
  const account = TE.api.account;
  const path = name => '/onedragons/' + encodeURIComponent(name);

  function bar() {
    return '<div class="native-tools banner info"><span id="dState" role="status"></span>' +
      '<button class="btn" id="dRefresh">刷新状态</button><button class="btn" id="dStop">关闭 BGI</button></div>';
  }
  function empty() {
    TE.dragon.name = '';
    TE.el('canvas').innerHTML = bar() + '<p>当前账号暂无一条龙配置</p><button class="btn" id="dCreate">新建</button>';
    bind();
  }
  function paint() {
    if (TE.tabs.cur !== 'dragon' || !TE.el('dState')) return;
    TE.el('dState').textContent = (account() || '未选择账号') + ' · BGI ' + labels[ops.state] + (detail ? '（' + detail + '）' : '');
    TE.el('dStop').hidden = ops.state !== 'running';
    TE.el('dRefresh').disabled = !account() || !!TE.app.saving;
    TE.el('dStop').disabled = !!TE.app.saving;
    const locked = ops.state !== 'stopped' || !!TE.app.saving;
    const layout = document.querySelector('.dragon-layout');
    if (layout) layout.inert = locked;
    TE.el('dCreate').disabled = locked;
    TE.dirty.paint();
  }
  async function runtime(action, name) {
    if (!account()) throw Error('请从账号入口打开');
    const url = '/api/bgi/editor/' + action;
    const status = action === 'status';
    const response = await fetch(status ? url + '?account=' + encodeURIComponent(account()) : url, {
      credentials:'same-origin', cache:'no-store', method:status ? 'GET' : 'POST',
      ...(status ? {} : {headers:{'Content-Type':'application/json'}, body:JSON.stringify({account:account(),name})})
    });
    const r = await response.json();
    if (!response.ok || r.ok !== true || r.account !== account() || !['running','stopped'].includes(r.state))
      throw Error(r.error || '无法核实本账号的 BGI 状态');
    return r.state;
  }
  async function refresh() {
    const ticket = ++serial;
    let state = 'unknown', reason = '';
    try { state = await runtime('status'); } catch (e) { reason = e.message; }
    if (ticket === serial) { ops.state = state; detail = reason; paint(); }
    return state;
  }
  function enter() { ops.state = 'unknown'; detail = ''; refresh(); }
  async function checked() {
    if (await refresh() !== 'stopped') throw Error(detail || 'BGI 已重新运行，请确认关闭后重试');
  }
  async function locked(work) {
    if (TE.app.saving || TE.tabs.cur !== 'dragon') return;
    TE.app.saving = true; paint();
    try { await work(); }
    catch (e) { await refresh(); TE.toast(e.message, 'err', 6000); }
    finally { TE.app.saving = false; paint(); }
  }
  function bind() {
    TE.el('dRefresh').onclick = () => locked(refresh);
    TE.el('dStop').onclick = () => TE.confirm('关闭当前账号的 BGI？', '将停止账号「' + account() + '」正在运行的 BGI。', () => locked(async () => {
      await runtime('stop', TE.dragon.name);
      if (await refresh() !== 'stopped') throw Error('尚未确认关闭，请刷新状态后重试');
      TE.toast('已确认 BGI 关闭，可以继续编辑', 'ok');
    }), true);
    TE.el('dCreate').onclick = () => fileDialog('create');
    if (TE.el('dRun')) {
      TE.el('dRun').onclick = () => TE.confirm('保存并执行一条龙？', '将保存「' + TE.dragon.name + '」，然后在账号「' + account() + '」执行。', () => save(true));
      TE.el('dRename').onclick = () => fileDialog('rename');
      TE.el('dDelete').onclick = () => fileDialog('delete');
    }
    paint();
  }
  function fileDialog(action) {
    if (TE.dirty.any()) { TE.toast('请先保存当前草稿，再管理配置', 'warn'); return; }
    const name = TE.dragon.name;
    const title = {create:'新建',rename:'改名',delete:'删除'}[action] + '一条龙配置';
    TE.modal({title, okText:action === 'delete' ? '确认删除' : '确定', bodyHtml:action === 'delete'
      ? '<p>确认删除账号「' + TE.esc(account()) + '」的「' + TE.esc(name) + '」？</p>'
      : '<label>配置名称 <input id="dName" value="' + TE.attr(action === 'rename' ? name : '') + '"></label>', onOk:() => {
      const value = action === 'delete' ? '' : TE.el('dName').value.trim();
      if (action !== 'delete' && (!value || (action === 'rename' && value === name))) {
        TE.toast('请输入新的配置名称', 'warn'); return false;
      }
      mutate(action, name, value, false);
    }});
  }
  function mutate(action, name, value, confirmed) {
    return locked(async () => {
      await checked();
      const body = action === 'delete' ? {} : {name:value};
      if (confirmed) body.confirmReferences = true;
      const r = await TE.api.post(action === 'create' ? '/onedragons' : path(name) + '/' + action, body);
      if (r._status === 409 && r.code === 'references' && !confirmed && Array.isArray(r.references)) {
        TE.confirm('此配置仍被引用', r.references.join('、') + '。继续后这些引用可能失效。', () => mutate(action, name, value, true), true);
        return;
      }
      if (r.ok !== true || r._status >= 400) throw Error(r.error || '配置操作失败');
      TE.draft.del('dragon:' + (action === 'create' ? value : name));
      const names = TE.tree.onedragons.filter(n => action === 'create' || n !== name);
      if (action !== 'delete') names.push(r.name || value);
      TE.tree.onedragons = names;
      TE.tree.show('dragon', action === 'delete' ? names[0] : r.name || value);
      TE.toast('配置已' + {create:'创建',rename:'改名',delete:'删除'}[action], 'ok');
    });
  }
  function save(run) {
    return locked(async () => {
      const name = TE.dragon.name;
      if (!name || !TE.dragon.data) throw Error('请先选择一条龙配置');
      await checked();
      const r = await TE.api.post(path(name), TE.dragon.data);
      if (r.ok !== true || r._status >= 400) {
        if (Array.isArray(r.errors) && r.errors.length) { TE.app.showErrors(r.errors); return; }
        throw Error(r.error || '保存失败，请重试');
      }
      TE.draft.del('dragon:' + name); TE.dirty.clear('dragon:' + name);
      if (run) {
        try {
          await checked();
          if (await runtime('start', name) !== 'running') throw Error('尚未确认启动');
          ops.state = 'running';
        } catch (e) { throw Error('配置已保存，执行未确认：' + e.message); }
      }
      TE.toast(run ? '已保存并启动一条龙' : '配置已保存', 'ok');
    });
  }
})(window.TE);
""";
    }
}
