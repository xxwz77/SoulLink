using System;

namespace SoulLink.TaskEdit
{
    // JS 脚本动态设置表单：按项目实例隔离配置，并区分 schema 状态。
    internal static partial class TaskEditStatic
    {
        private const string JsSettingsJs = """
'use strict';
(function (TE) {
  TE.js = {
    projIdx: -1, schema: null, values: {}, schemaState: 'loading',
    open: function (projIdx) {
      TE.js.projIdx = projIdx;
      var arr = (TE.group.data.projects || TE.group.data.Projects || []), p = arr[projIdx] || {};
      var folder = p.folderName || p.FolderName || p.name || p.Name || '';
      TE.js.values = JSON.parse(JSON.stringify(p.jsScriptSettingsObject || p.JsScriptSettingsObject || {}));
      TE.js.schema = null; TE.js.schemaState = 'loading';
      TE.busy(true, '读取脚本设置…');
      TE.api.get('/jssettings?folder=' + encodeURIComponent(folder)).then(function (r) {
        if (r && r._status === 404) TE.js.schemaState = r.state || 'missing';
        else if (r && r._status === 422 && r.state === 'invalid') TE.js.schemaState = 'invalid';
        else if (!r || r._status >= 400 || r.ok === false) TE.js.schemaState = 'request-failed';
        else if (r.declared === false) TE.js.schemaState = 'undeclared';
        else if (r.settingsUi !== undefined) { TE.js.schema = r.settingsUi; TE.js.schemaState = 'ready'; }
        else TE.js.schemaState = 'invalid';
        TE.js.render();
      }).catch(function (e) { TE.js.schemaState = 'request-failed'; TE.js.requestError = e.message; TE.js.render(); });
    },
    render: function () {
      var arr = (TE.group.data.projects || TE.group.data.Projects || []), p = arr[TE.js.projIdx] || {};
      var name = p.name || p.Name || '';
      var html = '<div class="head"><h2>' + TE.esc(name) + '</h2><span class="tag js">JS 脚本实例</span><span class="grow"></span><button class="btn" id="jsBack">返回配置组</button></div>';
      var items = TE.js.normalize(TE.js.schema);
      if (TE.js.schemaState !== 'ready') {
        var msgs = {loading:'正在读取设置定义…', undeclared:'该脚本未声明 SettingsUi。', missing:'设置文件缺失。', 'missing-script':'脚本 manifest.json 文件缺失。', 'missing-settings-file':'manifest.json 声明的 settings_ui 文件缺失。', invalid:'设置定义格式错误，无法生成表单。', 'request-failed':'读取设置定义请求失败：' + (TE.js.requestError || '服务不可用')};
        html += '<div class="banner warn">' + TE.esc(msgs[TE.js.schemaState] || '设置定义不可用') + ' 可查看并编辑实例现有值。</div>' + TE.js.rawJsonHtml();
      } else if (!items.length) html += '<div class="banner info">SettingsUi 已读取，但未声明设置项。</div>' + TE.js.rawJsonHtml();
      else { html += '<div class="banner info">' + items.length + ' 个设置项，来自脚本 SettingsUi。</div><div class="card"><div class="body">';
        for (var i=0;i<items.length;i++) html += TE.js.itemHtml(items[i],i);
        html += '</div></div>'; }
      TE.el('canvas').innerHTML='<section class="js-settings">'+html+'</section>'; TE.el('jsBack').onclick=function(){TE.group.render();}; TE.js.bind();
    },
    normalize:function(s){ if(!s)return []; var a=Array.isArray(s)?s:(s.items||s.Items||s.settingItems||s.SettingItems||[]); return Array.isArray(a)?a:[]; },
    rawJsonHtml:function(){return '<details class="card" open><summary>该实例的配置值（原始 JSON）</summary><div class="body"><textarea id="jsRaw" style="min-height:200px">'+TE.esc(JSON.stringify(TE.js.values||{},null,2))+'</textarea></div></details>';},
    itemHtml:function(it,i){
      var type=String(it.type||it.Type||it.itemType||'').toLowerCase(), name=it.name||it.Name||it.key||('item'+i), label=it.label||it.Label||it.title||it.Title||it.description||it.Description||name, optsRaw=it.options||it.Options||it.values||it.Values||it.cascadeOptions||it.CascadeOptions||it.levels||it.Levels;
      var val=TE.js.values[name]; if(val===undefined) val=it.default!==undefined?it.default:(it.Default!==undefined?it.Default:null); if(optsRaw && !it.options && !it.Options)it.options=optsRaw;
      var id='js-'+i, html='<div class="field"><label>'+TE.esc(label)+'</label><div class="ctl">';
      if(type==='separator')return '<hr><b>'+TE.esc(label)+'</b>';
      if(type==='checkbox') html+='<label class="sw"><input type="checkbox" data-js="'+TE.attr(name)+'" data-jt="bool"'+(val===true?' checked':'')+'><i></i></label>';
      else if(type==='select') {var opts=TE.js.optionsOf(it);html+='<select data-js="'+TE.attr(name)+'" data-jt="text">'; for(var j=0;j<opts.length;j++)html+='<option value="'+TE.attr(opts[j].value)+'"'+(String(val)===String(opts[j].value)?' selected':'')+'>'+TE.esc(opts[j].label)+'</option>';html+='</select>';}
      else if(type==='multi-checkbox'){var opts=TE.js.optionsOf(it),cur=Array.isArray(val)?val:[]; for(var j=0;j<opts.length;j++)html+='<label><input type="checkbox" data-jsm="'+TE.attr(name)+'" data-jv="'+TE.attr(opts[j].value)+'"'+(cur.indexOf(opts[j].value)>=0?' checked':'')+'> '+TE.esc(opts[j].label)+'</label> ';}
      else if(type==='cascade-select'){var levels=it.levels||it.Levels||it.cascadeOptions||it.CascadeOptions||[], cur=Array.isArray(val)?val:[]; if(!Array.isArray(levels))levels=[]; for(var l=0;l<levels.length;l++){var level=levels[l],opts=TE.js.optionsOf(level); html+='<select data-jc="'+TE.attr(name)+'" data-jl="'+l+'">'; for(var j=0;j<opts.length;j++)html+='<option value="'+TE.attr(opts[j].value)+'"'+(String(cur[l]||'')===String(opts[j].value)?' selected':'')+'>'+TE.esc(opts[j].label)+'</option>'; html+='</select> ';}}
      else html+='<input type="text" data-js="'+TE.attr(name)+'" data-jt="'+(['input-text','text'].indexOf(type)>=0?'text':'json')+'" value="'+TE.attr(TE.str(typeof val==='object'?JSON.stringify(val):val))+'">'+(['input-text','text',''].indexOf(type)<0?'<div class="desc">未知类型「'+TE.esc(type)+'」按 JSON 文本降级，其他值不受影响。</div>':'');
      return html+(it.description||it.Description?'<div class="desc">'+TE.esc(it.description||it.Description)+'</div>':'')+'</div></div>';
    },
    optionsOf:function(o){var a=o.options||o.Options||o.values||[];return Array.isArray(a)?a.map(function(x){return typeof x==='object'?{label:x.label||x.Label||x.value||x.Value,value:x.value||x.Value}:{label:String(x),value:x};}):[];},
    bind:function(){
      var c=TE.el('canvas'); TE.$$('[data-js]',c).forEach(function(x){x.addEventListener(x.type==='checkbox'||x.tagName==='SELECT'?'change':'input',function(){var v=x.getAttribute('data-jt')==='bool'?x.checked:x.value;if(x.getAttribute('data-jt')==='json'){try{v=JSON.parse(x.value);}catch(e){x.classList.add('bad');return;}}x.classList.remove('bad');TE.js.values[x.getAttribute('data-js')]=v;TE.js.commit();});});
      TE.$$('[data-jsm]',c).forEach(function(x){x.onchange=function(){var n=x.getAttribute('data-jsm');TE.js.values[n]=TE.$$('[data-jsm]',c).filter(function(y){return y.getAttribute('data-jsm')===n&&y.checked;}).map(function(y){return y.getAttribute('data-jv');});TE.js.commit();};});
      TE.$$('[data-jc]',c).forEach(function(x){x.onchange=function(){var n=x.getAttribute('data-jc');TE.js.values[n]=TE.$$('[data-jc]',c).filter(function(y){return y.getAttribute('data-jc')===n;}).sort(function(a,b){return +a.getAttribute('data-jl')- +b.getAttribute('data-jl');}).map(function(y){return y.value;});TE.js.commit();};});
      var raw=TE.el('jsRaw');if(raw)raw.oninput=function(){try{var v=JSON.parse(raw.value);if(!v||typeof v!=='object'||Array.isArray(v))throw Error();TE.js.values=v;raw.classList.remove('bad');TE.js.commit();}catch(e){raw.classList.add('bad');}};
    },
    commit:function(){var arr=(TE.group.data.projects||TE.group.data.Projects||[]),p=arr[TE.js.projIdx];if(!p)return;if('jsScriptSettingsObject'in p)p.jsScriptSettingsObject=TE.js.values;else p.JsScriptSettingsObject=TE.js.values;TE.dirty.mark('group:'+TE.group.name);}
  };
})(window.TE);
""";
    }
}