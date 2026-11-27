'use strict';
const db = {
  '/version': {channel:'TeaBag', productVersion:'1.0', root:'C:/Fixture/BGI'}, '/groups': ['Demo', '演示路线', '每周任务'], '/onedragons': ['Flow'],
  '/groups/Demo': {name:'Demo', projects:[{name:'test', type:'Javascript', status:'Disabled', allowJsHTTPHash:'abc', custom:7}]},
  '/jssettings': {ok:true,declared:true,settingsUi:[{type:'input-text',name:'label',label:'Label'}]},
  '/onedragons/Flow': {Name:'Flow', Period:'每日', PartyName:'演示队伍', DomainName:'演示秘境', MinResinToKeep:20, TaskDefinitions:{a:'First', b:'Second'}, TaskOrder:['a','b'], TaskEnabledList:{a:false,b:true}, NextTaskId:'', custom:7},
  '/picklists': {ok:true,enabled:true,mode:'Blacklist',blacklistModePickEnabled:false,whitelistModeDoNotPickEnabled:false,blackList:[],fuzzyBlackList:[],autoPickConfig:{enabled:true,mode:'Blacklist',customNative:9}},
  '/templatelists': {ok:true,blackList:['北陆长柄武器原胚'],whiteList:['精锻用魔矿'],mode:'Blacklist',templates:[{itemName:'北陆长柄武器原胚',name:'北陆长柄武器原胚',color:'purple',category:'武器材料'},{itemName:'精锻用魔矿',name:'精锻用魔矿',color:'blue',category:'矿物'},{itemName:'松茸',name:'松茸',color:'green',category:'食材'}]}
};
db['/picklists'].templates=db['/templatelists'].templates;
db['/picklists'].templateBlackList=['北陆长柄武器原胚'];
const groupTemplates={ok:true,blackList:['松茸'],whiteList:[]};
const requests=[]; const operations=[]; const errors=[]; let state='stopped',fault='',relaunch=false;
const rejected=error=>Response.json({ok:false,error},{status:503});
window.addEventListener('error',e=>errors.push(e.message)); window.addEventListener('unhandledrejection',e=>errors.push(String(e.reason)));
const account='Fixture User'; localStorage.setItem('te.account','Stale Fixture');
const initialTheme=location.search.includes('viewport=mobile')?'light':'dark'; localStorage.setItem('soullink-theme',initialTheme);
window.fetch=async(url,opt={})=>{
 const target=new URL(url,'https://fixture.invalid'); requests.push(target);
 if(target.pathname==='/api/sessions'&&!new URLSearchParams(location.search).has('account')) return Response.json({sessions:[{user:account,bound:true},{user:'Unbound Fixture',bound:false}]});
 const operation=target.pathname.replace('/api/bgi/editor/','');
 if(['start','stop'].includes(operation)){const body=JSON.parse(opt.body);if(opt.method!=='POST'||body.account!==account||!Object.hasOwn(body,'name'))throw Error('Unsafe runtime payload');operations.push(operation+':'+body.name);if(fault===operation)return rejected('fixture '+fault);state=operation==='start'?'running':'stopped';return Response.json({ok:true,account,state});}
 if(target.searchParams.get('account')!==account)throw Error('Wrong account: '+url);
 const key=target.pathname.replace('/taskedit/api','');
 if(operation==='status')return fault==='status'?rejected('fixture status'):Response.json({ok:true,account,state});
 if(opt.method==='POST'&&key==='/templatelists'){const body=JSON.parse(opt.body);if(body.blackList)db[key].blackList=body.blackList;if(body.whiteList)db[key].whiteList=body.whiteList;return Response.json({ok:true});}
 if(key==='/picklists'&&target.searchParams.has('group')){if(target.searchParams.get('group')!=='Demo')throw Error('Wrong group');if(opt.method==='POST')Object.assign(groupTemplates,JSON.parse(opt.body));return Response.json(groupTemplates);}
 if(opt.method==='POST'&&key==='/picklists'){const body=JSON.parse(opt.body);db[key]=Object.assign({},db[key],body,{autoPickConfig:Object.assign({},db[key].autoPickConfig,body.autoPickConfig)});return Response.json({ok:true});}
 if(opt.method==='POST'&&key.startsWith('/onedragons')){const body=JSON.parse(opt.body),[, ,oldName,action]=key.split('/');operations.push(key);if(fault==='save')return rejected('fixture write');if(action&&!body.confirmReferences)return Response.json({ok:false,code:'references',references:['Timer <daily>']},{status:409});if(!oldName||action){const obj=db['/onedragons/'+oldName]||{};if(oldName){delete db['/onedragons/'+oldName];db['/onedragons']=db['/onedragons'].filter(n=>n!==oldName);}if(action!=='delete'){db['/onedragons/'+body.name]=obj;db['/onedragons'].push(body.name);}return Response.json({ok:true,name:body.name||oldName});}if(relaunch)state='running';}
 if(!(key in db))throw Error('Unexpected request '+key);if(opt.method==='POST')db[key]=JSON.parse(opt.body);return new Response(JSON.stringify(opt.method==='POST'?{ok:true}:db[key]));
};
window.addEventListener('load',()=>setTimeout(async()=>{
 const output=document.body.appendChild(document.createElement('output'));output.id='fixture-result';output.hidden=true;const q=s=>document.querySelector(s);const tick=()=>new Promise(r=>setTimeout(r,20));const check=(v,m)=>{if(!v)throw Error(m);};
 try{
  check(!errors.length,errors.join(' | '));
  if(!new URLSearchParams(location.search).has('account')){const choices=Array.from(q('#accountSelect').options,o=>o.value).filter(Boolean);check(choices.length===1&&choices[0]===account,'only bound accounts offered');check(requests.length&&requests.every(u=>u.pathname==='/api/sessions'),'no unscoped editor reads');output.textContent='PASS';return;}
  const viewport=new URLSearchParams(location.search).get('viewport');
  if(viewport==='picker'||viewport==='mobile-picker'){
    q('[data-tab=pick]').click();for(let i=0;i<30&&!q('#pCategory');i++)await tick();
    check(q('#pCategory')&&q('#pColor')&&q('#pEnabled').checked,'native configuration and filter controls');
    function change(selector,value){const el=q(selector);el.value=value;el.dispatchEvent(new Event('change',{bubbles:true}));}
    change('#pCategory','矿物');change('#pColor','green');
    check(!q('[data-template]'),'color intersects category');
    change('#pColor','blue');
    const search=q('#pSearch');search.focus();search.value='魔矿';search.dispatchEvent(new Event('input',{bubbles:true}));
    check(q('#pSearch')===search&&document.activeElement===search,'search retains focus');
    check(!TE.dirty.any(),'filters do not dirty native settings');
    q('[data-template="精锻用魔矿"]').click();
    change('#pMode','Whitelist');
    check(q('[data-template="精锻用魔矿"]').checked,'OCR mode must not change template selections');
    change('#pTemplateScope','Demo');await tick();
    check(!q('[data-template="精锻用魔矿"]').checked,'group blacklist independent of global');
    q('[data-template-mode="whiteList"]').click();q('[data-template="精锻用魔矿"]').click();
    q('[data-template-mode="blackList"]').click();
    check(!q('[data-template="精锻用魔矿"]').checked,'white selection must not appear in black list');
    q('#saveBtn').click();await tick();
    const saved=db['/picklists'];
    check(saved.blackList.length===0&&saved.templateBlackList.includes('精锻用魔矿'),'global template list saved separately from OCR');
    check(groupTemplates.whiteList.includes('精锻用魔矿')&&groupTemplates.blackList.join()==='松茸','group modes persisted separately');
    check(saved.autoPickConfig.mode==='Whitelist'&&saved.autoPickConfig.customNative===9,'native config save');
    check(!TE.dirty.any()&&Object.keys(TE.pick.groupPayload()).length===0,'saved snapshot reset');
    change('#pTemplateScope','');change('#pTemplateScope','Demo');await tick();
    q('[data-template-mode="whiteList"]').click();
    check(q('[data-template="精锻用魔矿"]').checked,'group white selection restored from disk model');
    check(document.documentElement.scrollWidth<=innerWidth,'picker fits viewport');
    check(!errors.length,errors.join(' | '));output.textContent='PASS';return;
  }
  if(viewport==='operations'){
    const click=id=>q('#'+id).click(),confirm=async()=>{click('modalOk');await tick();},run=async()=>{click('dRun');await confirm();},stop=async()=>{click('dStop');await confirm();},count=k=>operations.filter(v=>v===k).length;
    q('[data-den=a]').click();state='running';click('saveBtn');await tick();check(count('/onedragons/Flow')===0&&q('#saveBtn').disabled&&TE.draft.load('dragon:Flow').TaskEnabledList.a,'relaunch blocks save and preserves draft');click('dStop');click('modalCancel');check(!operations.length,'cancel stop untouched');fault='stop';await stop();check(!q('#dStop').hidden&&q('.dragon-layout').inert,'stop failure stays locked');fault='';await stop();check(!q('.dragon-layout').inert&&q('[data-den=a]').checked,'confirmed stop unlocks draft');fault='status';click('dRefresh');await tick();check(q('.dragon-layout').inert&&q('#dStop').hidden,'unknown read only');fault='';click('dRefresh');await tick();fault='save';await run();check(!count('start:Flow')&&TE.dirty.any(),'failed save blocks execution');TE.tree.show('dragon','Flow');await tick();check(q('[data-den=a]').checked&&TE.dirty.any(),'reopened draft');fault='';relaunch=true;await run();check(!count('start:Flow')&&!q('#dStop').hidden&&db['/onedragons/Flow'].TaskEnabledList.a,'relaunch after successful write blocks execute');relaunch=false;await stop();fault='start';await run();check(count('start:Flow')===1&&!q('.dragon-layout').inert,'start failure recoverable');fault='';click('dRun');click('modalCancel');check(count('start:Flow')===1,'cancel execute');click('dRun');click('modalOk');TE.dragonOps.save(true);await tick();check(count('start:Flow')===2&&!q('#dStop').hidden,'single successful retry');await stop();click('dCreate');q('#dName').value='New';await confirm();check(TE.dragon.name==='New','select created');click('dRename');q('#dName').value='Renamed';await confirm();check(q('#modalBody').textContent.includes('Timer <daily>')&&!q('#modalBody daily'),'escaped reference');click('modalCancel');check(TE.dragon.name==='New','cancel reference warning');click('dRename');q('#dName').value='Renamed';await confirm();await confirm();check(TE.dragon.name==='Renamed','confirmed rename');click('dDelete');click('modalCancel');check(!count('/onedragons/Renamed/delete'),'cancel deletion');click('dDelete');await confirm();await confirm();check(TE.dragon.name==='Flow'&&db['/onedragons'].length===1,'confirmed delete');click('dDelete');await confirm();await confirm();check(q('#dCreate')&&!q('#dRun'),'empty account can create');state='running';click('dRefresh');await tick();await stop();check(count('stop:')===1,'empty account close');check(!errors.length,errors.join(' | '));output.textContent='PASS';return;
  }
  const mobile=viewport.startsWith('mobile');check(mobile?innerWidth===390&&innerHeight===844:innerWidth>=1400,'viewport width '+innerWidth);check(q('#accountLabel').textContent.includes(account),'account card label');for(let i=0;i<25&&!q('[data-den=a]');i++)await tick();check(q('[data-den=a]'),'default dragon opens first object');const page=document.documentElement;check(page.dataset.theme===initialTheme,'inherit main site theme');const ink=getComputedStyle(document.body).color;q('#themeBtn').click();await tick();const changed=initialTheme==='light'?'dark':'light';check(page.dataset.theme===changed&&localStorage.getItem('soullink-theme')===changed,'theme persistence');check(getComputedStyle(document.body).color!==ink,'theme changes rendered text color');const [tasks,settings]=Array.from(q('.dragon-layout').children,el=>el.getBoundingClientRect());check(mobile?settings.top>=tasks.bottom-1:settings.left>=tasks.right-1,'responsive dragon columns');check(!q('[data-den=a]').checked&&q('[data-start=a]').disabled,'disabled start');q('[data-den=a]').click();q('[data-tab=group]').click();check(q('#modalMask').classList.contains('open'),'guard unsaved edits on tab switch');q('#modalCancel').click();check(q('[data-den=a]').checked,'cancel keeps edited dragon');q('[data-start=a]').click();q('#dClear').click();q('[data-start=b]').click();q('#dAdd').click();await tick();q('[data-add-group]').click();q('#modalOk').click();q('#saveBtn').click();await tick();const d=db['/onedragons/Flow'];check(d.NextTaskId==='b'&&d.TaskEnabledList.a===true&&d.TaskEnabledList.b===true&&d.custom===7&&!('_status'in d),'dragon POST');const added=d.TaskOrder[2];check(/^[0-9a-f-]{36}$/.test(added)&&d.TaskDefinitions[added]==='Demo'&&d.TaskEnabledList[added],'add group');q('[data-tab=group]').click();await tick();check(!q('[data-en]').checked&&getComputedStyle(q('th.advanced')).display==='none','native group list');q('#gMore').click();Array.from(document.querySelectorAll('#ctxMenu .item')).find(e=>e.textContent.includes('展开详细列')).click();check(getComputedStyle(q('th.advanced')).display==='table-cell','details');q('[data-en]').click();q('[data-menu]').click();Array.from(document.querySelectorAll('#ctxMenu .item')).find(e=>e.textContent.includes('从此处执行')).click();q('#saveBtn').click();await tick();const p=db['/groups/Demo'].projects[0];check(p.status==='Enabled'&&p.nextFlag===true&&p.allowJsHTTPHash==='abc'&&p.custom===7&&!('enabled'in p),'group POST');check(requests.length&&requests.every(u=>u.searchParams.get('account')===account),'all requests scoped');TE.group.data.projects.push({type:'Javascript',name:'test',jsScriptSettingsObject:{label:'other'}},{type:'SoloTask',name:'solo',soloTaskSettings:{count:2,unknown:[1]}});TE.js.open(0);await tick();const label=q('[data-js="label"]');label.value='roundtrip';label.dispatchEvent(new Event('input'));q('#jsBack').click();TE.group.openSoloSettings(2);q('[data-solo="count"]').value='5';q('#modalOk').click();q('#saveBtn').click();await tick();const saved=(await TE.api.get('/groups/Demo')).projects;check((saved[0].jsScriptSettingsObject||saved[0].JsScriptSettingsObject).label==='roundtrip'&&saved[1].jsScriptSettingsObject.label==='other','JS roundtrip');check(saved[2].soloTaskSettings.count===5&&Array.isArray(saved[2].soloTaskSettings.unknown),'solo roundtrip');const picker=q('#objectPicker');if(!picker.open)picker.querySelector('summary').click();const options=Array.from(picker.querySelectorAll('.object-option'),e=>e.getBoundingClientRect());check(options.length===3,'group choices');check(mobile?options[1].top>=options[0].bottom-1:options[1].left>=options[0].right-1,'object picker columns');if(!viewport.endsWith('-group')){q('[data-tab=dragon]').click();await tick();check(q('[data-den=a]').checked,'return to saved dragon');}check(document.documentElement.scrollWidth<=innerWidth,'page overflow');check(!errors.length,errors.join(' | '));output.textContent='PASS';
 }catch(e){output.textContent=String(e.stack||e);}finally{if(parent!==window)parent.postMessage(output.textContent,'*');}
},25));
