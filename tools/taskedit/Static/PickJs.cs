using System;
namespace SoulLink.TaskEdit
{
    // BGI native AutoPick page. List/category controls are view aids; rules are all BGI's.
    internal static partial class TaskEditStatic
    {
        private const string PickJs = """
'use strict';
(function (TE) {
  var regions=['蒙德','璃月','稻妻','须弥','枫丹','纳塔','挪德卡莱','至冬','通属地区'];

  function filterSelect(id, label, allLabel, values, selected) {
    var options=values.map(function(value){
      return '<option value="'+TE.attr(value)+'"'+(selected===value?' selected':'')+'>'+TE.esc(value)+'</option>';
    }).join('');
    return '<select id="'+id+'" aria-label="'+label+'"><option value="all">'+allLabel+'</option>'+options+'</select>';
  }

  TE.pick = {
    data: null, model: null, filter: { q: '', category: 'all', region: 'all', color: 'all' },
    open: function () {
      TE.busy(true, '读取 BGI 自动拾取设置…');
      var request=TE.api.get('/picklists');
      TE.pick.request=request;
      request.then(function (m) {
        if(TE.tabs.cur!=='pick'||TE.pick.request!==request)return;
        if (!m || m.ok === false) throw new Error((m && m.error) || '读取自动拾取设置失败');
        TE.pick.model=m; TE.pick.data=m.autoPickConfig||{};
        TE.pick.scope=""; TE.pick.templateMode="blackList"; TE.pick.groupLists=null;
        TE.pick.acceptSaved();
        TE.pick.filter={q:'',category:'all',region:'all',color:'all'};
        TE.pick.render();
      }).catch(function(e){if(TE.tabs.cur!=='pick'||TE.pick.request!==request)return;TE.el('canvas').innerHTML='<div class="banner err">读取失败：'+TE.esc(e.message)+'</div>';});
    },
    arr:function(v){return Array.isArray(v)?v.slice():[];},
    templateList:function(){
      var p=TE.pick;
      return p.scope?(p.groupLists && p.groupLists[p.templateMode]):p.model.templateBlackList;
    },
    groupPath:function(name){return '/picklists?group='+encodeURIComponent(name);},
    openScope:function(scope){
      var p=TE.pick;
      if(Object.keys(p.groupPayload()).length){
        TE.el('pTemplateScope').value=p.scope;
        TE.toast('请先保存当前配置组的模板名单','warn');
        return;
      }
      p.scope=scope;p.groupLists=null;p.templateMode='blackList';p.render();
      if(!scope)return;
      var request=TE.api.get(p.groupPath(scope));
      p.scopeRequest=request;
      request.then(function(data){
        if(TE.tabs.cur!=='pick'||scope!==p.scope||request!==p.scopeRequest)return;
        if(!data||data.ok!==true)throw new Error((data&&data.error)||'读取配置组模板名单失败');
        p.groupLists={blackList:p.arr(data.blackList),whiteList:p.arr(data.whiteList)};
        p.savedGroup=JSON.stringify(p.groupLists);
        p.render();
      }).catch(function(e){
        if(TE.tabs.cur!=='pick'||scope!==p.scope||request!==p.scopeRequest)return;
        TE.el('pTemplates').textContent=e.message;
      });
    },
    filteredTemplates:function(){
      var m=TE.pick.model,f=TE.pick.filter,query=f.q.toLowerCase();
      return (m.templates||[]).filter(function(x){
        var name=String(x.displayName||x.itemName||x.name||'').toLowerCase();
        return (!query||name.includes(query))
          && (f.category==='all'||(x.category||'未知')===f.category)
          && (f.region==='all'||x.region===f.region)
          && (f.color==='all'||x.color===f.color);
      });
    },
    render: function(){
      var m=TE.pick.model||{}, d=TE.pick.data||{}, white=String(m.mode||d.mode||'Blacklist').toLowerCase()==='whitelist';
      var colors=Array.from(new Set((m.templates||[]).map(function(x){return x.color;}).filter(Boolean))).sort();
      var cats=Array.from(new Set((m.templates||[]).map(function(x){return x.category||'未知';}))).sort();
      var html='<div class="head"><h2>自动拾取</h2><span class="meta">BGI · '+TE.esc(m.root||'')+'</span><span class="grow"></span></div>';
      html+='<div class="banner info">按 '+TE.esc(white?'白名单模式':'黑名单模式')+' 展示配置；全部开关与名单均为此 BGI fork 原生设置。分类仅筛选模板列表，不改变 BGI 判定。</div>';
      html+='<details class="card" open><summary>拾取设置</summary><div class="body">';
      html+='<div class="field"><label>启用自动拾取</label><div class="ctl"><label class="sw"><input id="pEnabled" type="checkbox"'+(d.enabled===true?' checked':'')+'><i></i></label></div></div>';
      html+='<div class="field"><label>自动拾取名单模式</label><div class="ctl"><select id="pMode"><option value="Blacklist"'+(!white?' selected':'')+'>黑名单模式</option><option value="Whitelist"'+(white?' selected':'')+'>白名单模式</option></select><div class="desc">两种模式各自保存名单；切换不会合并或覆盖另一模式。</div></div></div>';
      html+='<div class="field"><label>OCR 识别引擎</label><div class="ctl"><select id="pOcr"><option value="Paddle"'+(d.ocrEngine==='Paddle'?' selected':'')+'>Paddle</option><option value="Yap"'+(d.ocrEngine==='Yap'?' selected':'')+'>Yap</option></select></div></div>';
      html+='<div class="field"><label>快速模式</label><div class="ctl"><label class="sw"><input id="pFast" type="checkbox"'+(d.fastModeEnabled===true?' checked':'')+'><i></i></label><div class="desc">忽略文字识别结果，直接拾取。</div></div></div>';
      html+='<div class="field"><label>拾取按键</label><div class="ctl"><input id="pKey" maxlength="1" value="'+TE.attr(d.pickKey||'F')+'"></div></div>';
      html+='<div class="grid2">'+TE.pick.numberField('图标左偏移','itemIconLeftOffset',d,0,800)+TE.pick.numberField('文字左偏移','itemTextLeftOffset',d,0,800)+TE.pick.numberField('文字右偏移','itemTextRightOffset',d,0,1200)+TE.pick.numberField('交互延迟 (ms)','interactionDelay',d,0,2000)+TE.pick.numberField('重复按键间隔 (ms)','repeatFInterval',d,0,5000)+TE.pick.numberField('确认间隔 (ms)','confirmInterval',d,0,5000)+TE.pick.numberField('滚动距离','scrollDistance',d,0,2000)+TE.pick.numberField('滚动间隔 (ms)','scrollInterval',d,0,5000)+TE.pick.numberField('滚动后等待 (ms)','scrollAfterInterval',d,0,5000)+TE.pick.numberField('日志级别','pickLogLevel',d,0,5)+'</div>';
      html+='<div class="field"><label>模板匹配</label><div class="ctl"><label class="sw"><input id="pMojang" type="checkbox"'+(d.mojangMatchEnabled===true?' checked':'')+'><i></i></label><div class="desc">使用 BGI 内置及 User/mojang_templates 模板；此设置由 BGI 原生逻辑生效。</div></div></div>';
      html+='<div class="field"><label>模板匹配阈值</label><div class="ctl"><input id="pThreshold" type="number" min="0" max="1" step="0.01" value="'+TE.attr(d.mojangMatchThreshold==null?'0.85':d.mojangMatchThreshold)+'"></div></div>';
      html+='<div class="field"><label>测试模式</label><div class="ctl"><label class="sw"><input id="pTest" type="checkbox"'+(d.testModeEnabled===true?' checked':'')+'><i></i></label></div></div>';
      html+=TE.pick.numberField('连续识别失败截图次数','autoScreenshotStreak',d,0,10000);
      html+='</div></details>';
      html+='<details class="card" open><summary>原版拾取名单</summary><div class="body">';
      if(white){html+='<h3>拾取（用户补充）</h3>'+TE.pick.listHtml(m.whiteModeAllowList,'whiteModeAllowList');html+='<h3>不拾取（精确及模糊排除）</h3><label class="sw"><input id="pWhiteExcludeEnabled" type="checkbox"'+(d.whitelistModeDoNotPickEnabled===true?' checked':'')+'><i></i></label> 启用排除规则<h4>精确匹配</h4>'+TE.pick.listHtml(m.whiteModeExcludeList,'whiteModeExcludeList')+'<h4>模糊匹配</h4>'+TE.pick.listHtml(m.whiteModeFuzzyExcludeList,'whiteModeFuzzyExcludeList');}
      else{html+='<h3>不拾取（精确）</h3>'+TE.pick.listHtml(m.blackList,'blackList');html+='<h3>不拾取（模糊）</h3>'+TE.pick.listHtml(m.fuzzyBlackList,'fuzzyBlackList');html+='<h3>拾取（优先放行）</h3><label class="sw"><input id="pBlackAllowEnabled" type="checkbox"'+(d.blacklistModePickEnabled===true?' checked':'')+'><i></i></label> 启用拾取规则'+TE.pick.listHtml(m.blackModeAllowList,'blackModeAllowList');}
      html+='</div></details>';
      var scopeOptions=(TE.tree.groups||[]).map(function(name){
        return '<option value="'+TE.attr(name)+'"'+(TE.pick.scope===name?' selected':'')+'>'+TE.esc(name)+'</option>';
      }).join('');
      var scopeControls='<div class="field"><label for="pTemplateScope">名单范围</label><div class="ctl"><select id="pTemplateScope"><option value="">全局模板黑名单</option>'+scopeOptions+'</select></div></div>';
      if(TE.pick.scope){
        scopeControls+='<div class="row" role="group" aria-label="配置组模板名单">'+[['whiteList','模板白名单'],['blackList','模板黑名单']].map(function(pair){
          return '<button type="button" class="btn sm" data-template-mode="'+pair[0]+'" aria-pressed="'+(TE.pick.templateMode===pair[0])+'">'+pair[1]+'</button>';
        }).join('')+'</div>';
      }
      var filter=TE.pick.filter;
      html+='<details class="card" open><summary>模板拾取</summary><div class="body">'+scopeControls
        +'<div class="row pick-filters"><input id="pSearch" aria-label="搜索模板物品" type="search" placeholder="搜索模板物品" value="'+TE.attr(filter.q)+'">'
        +filterSelect('pCategory','物品大类别','全部大类',cats,filter.category)
        +filterSelect('pRegion','物品地区','全部地区',regions,filter.region)
        +filterSelect('pColor','物品颜色','全部颜色',colors,filter.color)
        +'<span id="pCount" class="meta"></span></div><div class="desc">勾选代表加入当前模板名单。</div><div id="pTemplates" class="object-grid"></div></div></details>';
      if(m.warning) html='<div class="banner warn">'+TE.esc(m.warning)+'</div>'+html;
      TE.el('canvas').innerHTML=html; TE.pick.bind(); TE.pick.renderTemplates();
    },
    renderTemplates:function(){
      var list=TE.pick.templateList();
      if(!list){TE.el('pCount').textContent='';TE.el('pTemplates').textContent='正在读取配置组模板名单…';return;}
      var rows=TE.pick.filteredTemplates(), exact=new Set(list);
      TE.el('pCount').textContent=rows.length+' / '+TE.pick.model.templates.length;
      TE.el('pTemplates').innerHTML=rows.map(function(x){
        var n=x.itemName||x.name, alternate=x.alternateItemName;
        var checked=exact.has(n)||(alternate&&exact.has(alternate));
        var aliasAttribute=alternate?' data-template-alternate="'+TE.attr(alternate)+'"':'';
        var tags=[x.category||'未知',x.region,x.color].filter(Boolean).map(TE.esc).join(' · ');
        return '<label class="object-option"><input type="checkbox" data-template="'+TE.attr(n)+'"'+aliasAttribute+(checked?' checked':'')+'>'+TE.esc(x.displayName||n)+' <span class="badge tag">'+tags+'</span></label>';
      }).join('') || '<div class="desc">没有匹配的模板</div>';
    },
    listKeys:['blackList','fuzzyBlackList','blackModeAllowList','whiteModeAllowList','whiteModeExcludeList','whiteModeFuzzyExcludeList','templateBlackList'],
    acceptSaved:function(){
      var m=TE.pick.model;
      if(TE.pick.groupLists)TE.pick.savedGroup=JSON.stringify(TE.pick.groupLists);
      TE.pick.savedConfig=Object.assign({},TE.pick.data);
      TE.pick.savedLists={};
      m.templates=TE.pick.arr(m.templates);
      TE.pick.listKeys.forEach(function(k){m[k]=TE.pick.arr(m[k]);TE.pick.savedLists[k]=JSON.stringify(m[k]);});
    },
    numberField:function(label,key,d,min,max){return '<div class="field"><label>'+label+'</label><div class="ctl"><input type="number" data-number="'+key+'" min="'+min+'" max="'+max+'" value="'+TE.attr(d[key]==null?0:d[key])+'"></div></div>';},
    listHtml:function(items,key){items=TE.pick.arr(items);var placeholder=/fuzzy/i.test(key)?'输入模糊匹配关键词':'输入物品名称';return '<div class="row"><input type="text" id="add-'+key+'" placeholder="'+placeholder+'"><button class="btn sm" data-add="'+key+'">添加</button></div><div class="object-grid">'+items.map(function(n,i){return '<span class="object-option">'+TE.esc(n)+' <button class="btn sm danger" data-del="'+key+'" data-index="'+i+'">移除</button></span>';}).join('')+'</div>';},
    toPayload:function(){
      var payload={}, auto={}, d=TE.pick.data, m=TE.pick.model;
      Object.keys(d).forEach(function(k){if(d[k]!==TE.pick.savedConfig[k])auto[k]=d[k];});
      if(Object.keys(auto).length)payload.autoPickConfig=auto;
      TE.pick.listKeys.forEach(function(k){if(JSON.stringify(m[k])!==TE.pick.savedLists[k])payload[k]=m[k];});
      return payload;
    },
    groupPayload:function(){
      var p=TE.pick;
      if(!p.groupLists)return {};
      var saved=JSON.parse(p.savedGroup), patch={};
      ['whiteList','blackList'].forEach(function(key){
        if(JSON.stringify(saved[key])!==JSON.stringify(p.groupLists[key]))patch[key]=p.groupLists[key];
      });
      return patch;
    },
    save:function(){
      var p=TE.pick, global=p.toPayload(), lists=p.groupPayload(), path=p.groupPath(p.scope);
      var first=Object.keys(global).length?TE.api.post('/picklists',global):Promise.resolve({ok:true});
      return first.then(function(result){
        if(!result||result.ok!==true||!Object.keys(lists).length)return result;
        return TE.api.post(path,lists);
      });
    },
    bind:function(){var d=TE.pick.data,m=TE.pick.model;function dirty(){TE.dirty.mark('pick');}
      TE.el('pEnabled').onchange=function(){d.enabled=this.checked;dirty();};TE.el('pMode').onchange=function(){d.mode=this.value;m.mode=this.value;TE.pick.render();dirty();};TE.el('pOcr').onchange=function(){d.ocrEngine=this.value;dirty();};TE.el('pFast').onchange=function(){d.fastModeEnabled=this.checked;dirty();};TE.el('pKey').onchange=function(){d.pickKey=this.value.toUpperCase().slice(0,1)||'F';TE.pick.render();dirty();};TE.el('pMojang').onchange=function(){d.mojangMatchEnabled=this.checked;dirty();};TE.el('pThreshold').onchange=function(){setNumber(this,'mojangMatchThreshold');};TE.el('pTest').onchange=function(){d.testModeEnabled=this.checked;dirty();};
      function setNumber(input,key){
        if(!input.value || !input.checkValidity()){
          input.value=d[key]==null?input.defaultValue:d[key];
          TE.toast('请输入有效的数值','warn');
          return;
        }
        d[key]=input.valueAsNumber;dirty();
      }
      TE.$$('[data-number]').forEach(function(e){e.onchange=function(){setNumber(e,e.getAttribute('data-number'));};});
      if(TE.el('pBlackAllowEnabled'))TE.el('pBlackAllowEnabled').onchange=function(){d.blacklistModePickEnabled=this.checked;dirty();};if(TE.el('pWhiteExcludeEnabled'))TE.el('pWhiteExcludeEnabled').onchange=function(){d.whitelistModeDoNotPickEnabled=this.checked;dirty();};
      TE.el('pTemplateScope').onchange=function(){TE.pick.openScope(this.value);};
      TE.$$('[data-template-mode]').forEach(function(button){
        button.onclick=function(){
          TE.pick.templateMode=button.getAttribute('data-template-mode');
          TE.$$('[data-template-mode]').forEach(function(b){b.setAttribute('aria-pressed',String(b===button));});
          TE.pick.renderTemplates();
        };
      });
      TE.el('pSearch').oninput=function(){TE.pick.filter.q=this.value;TE.pick.renderTemplates();};
      TE.el('pCategory').onchange=function(){TE.pick.filter.category=this.value;TE.pick.renderTemplates();};
      TE.el('pRegion').onchange=function(){TE.pick.filter.region=this.value;TE.pick.renderTemplates();};
      TE.el('pColor').onchange=function(){TE.pick.filter.color=this.value;TE.pick.renderTemplates();};
      TE.el('pTemplates').onchange=function(event){
        var box=event.target,n=box.getAttribute('data-template');
        if(n===null)return;
        var a=TE.pick.templateList(),alternate=box.getAttribute('data-template-alternate');
        if(box.checked){
          if(!a.includes(n)&&!(alternate&&a.includes(alternate)))a.push(n);
        }else{
          for(var i=a.length-1;i>=0;i--){
            if(a[i]===n||a[i]===alternate)a.splice(i,1);
          }
        }
        TE.$$('[data-template]',this).forEach(function(other){
          if(other.getAttribute('data-template')===n||other.getAttribute('data-template-alternate')===n)
            other.checked=box.checked;
        });
        dirty();
      };
      TE.$$('[data-add]').forEach(function(b){b.onclick=function(){var key=b.getAttribute('data-add'),id='add-'+key,arr=m[key],v=TE.el(id).value.trim();if(v&&!arr.includes(v)){arr.push(v);TE.pick.render();dirty();}};});TE.$$('[data-del]').forEach(function(b){b.onclick=function(){m[b.getAttribute('data-del')].splice(+b.getAttribute('data-index'),1);TE.pick.render();dirty();};});
    }
  };
})(window.TE);
""";
    }
}
