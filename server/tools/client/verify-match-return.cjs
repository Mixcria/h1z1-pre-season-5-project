// Execute handlers re-exported from the compiled GFx, with UI/native boundaries mocked.
const fs=require('node:fs'), path=require('node:path'), vm=require('node:vm'), assert=require('node:assert/strict');
const dir=process.argv[2], read=p=>fs.readFileSync(path.join(dir,p),'utf8');
const manager=read('ui/managers/UIMatchManager.as'), settings=read('views/settings/UISettingsManager.as');
const group=read('ui/managers/UIGroupManager.as'), state=read('ui/states/UIState_InGame.as');
const confirmationDir=process.argv[3];assert(confirmationDir,'Pass the unchanged ConfirmationDialogWindow export directory');
const confirmation=fs.readFileSync(path.join(confirmationDir,'views/confirmation/ConfirmationDialogWindow.as'),'utf8');
let cases=0;
function method(source,name) {
  let i=source.indexOf('function '+name+'(');assert(i>=0,name);
  let a=source.indexOf('{',i),b=a+1,depth=1;
  while(depth) {if(source[b]==='{')depth++;if(source[b]==='}')depth--;b++;assert(b<=source.length,name);}
  return source.slice(i,b).replace(/:\s*(?:String|Boolean|Number|int|uint|void|Array|Object|Sprite|TextField|Button|GameEvent|Event|MouseEvent|KeyboardEvent|MatchEvent|ButtonEvent|IUIDataBinding)\b/g,'')
    .replace(/ as (?:Sprite|Button)/g,'')
    .replace(/for\(var (\w+) in this\.m_resultRows\)/g,'for(var $1 of this.m_resultRows.keys())')
    .replace(/delete this\.m_resultRows\[(\w+)\];/g,'this.m_resultRows.delete($1);')
    .replace(/this\.m_resultRows\[(\w+)\] = (\w+);/g,'this.m_resultRows.set($1,$2);')
    .replace(/this\.m_resultRows\[(\w+)\]/g,'this.m_resultRows.get($1)');
}
function client(mode='GAMETYPE_SOLO',flags={}) {
  const calls=[],timers=new Map();let nextTimer=0;
  class Sprite {
    constructor(){this.children=[];this.x=0;this.y=0;this.alpha=1;this.events=new Map();this.graphics={beginFill:c=>this.color=c,drawRect:(x,y,w,h)=>{this.width=w;this.height=h;},endFill(){}};}
    addChild(c){c.parent=this;c.stage=this.stage;this.children.push(c);return c;}
    removeChild(c){this.children.splice(this.children.indexOf(c),1);c.parent=null;c.stage=null;}
    addEventListener(name,fn){this.events.set(name,fn);}
    removeEventListener(name,fn){if(this.events.get(name)===fn)this.events.delete(name);}
  }
  const stageEvents=new Map();
  const stage={dispatchEvent:e=>{calls.push(['event',e.type]);if(e.type==='CranberryCloseMatchMenu')c.handleCloseMatchMenu(e);},
    addEventListener:(name,fn)=>stageEvents.set(name,fn),removeEventListener:(name,fn)=>{if(stageEvents.get(name)===fn)stageEvents.delete(name);}};
  const c=vm.createContext({calls,Sprite,TextField:class{},TextFormat:class{},gameType:mode,GAMETYPE_SOLO:'GAMETYPE_SOLO',
    Event:{ENTER_FRAME:'frame',REMOVED_FROM_STAGE:'removed'},MouseEvent:{CLICK:'click'},KeyboardEvent:{KEY_UP:'keyup'},ButtonEvent:{CLICK:'clik'},
    GameEvent:class{constructor(type){this.type=type;}},
    UIBindingSystem:{DispatchWallOfData:(...v)=>calls.push(['action',...v]),Logout:()=>calls.push(['nativeLogout']),
      IsTeamBattleRoyale:()=>!!flags.team,IsTraining:()=>!!flags.training,InInvitational:()=>!!flags.hosted},
    UIBindingMatch:{ClearMatchData:()=>calls.push(['clearNativeData'])},
    UIBindingSound:{UI_MATCH_END_PROGRESS_BAR_STOP:42,PlayUiSound:id=>calls.push(['stopSound',id])},
    setTimeout:(fn,ms)=>{timers.set(++nextTimer,fn);return nextTimer;},clearTimeout:id=>{timers.delete(id);calls.push(['clearTimer',id]);},
    _stage:stage,m_stageRef:stage,m_displayStack:{modalOpen:false},m_resultRows:new Map(),m_exitRequested:false,m_nativeExitStarted:false,m_nativeExitTimeoutId:0,
    m_windowTimeoutId:15,m_victoryTimeoutId:16,_bindings:[],
    removeMatchListeners:()=>calls.push(['removeListeners']),closeAllWindows:()=>calls.push(['closeWindows']),closeMenu:()=>calls.push(['closePause'])});
  for(const name of ['canPlayAgain','requestMatchAction','handleMatchSessionReady','handleNativeMatchExit','finishNativeMatchExit',
    'installResultButtons','layoutResultButtons','makeResultButton','removeResultButtons','detachResultButton','clearResultButtons',
    'handleResultClick','handleResultKey','processActionButton','handleButtonEvent','exitMatch','cleanUpMatchData','deinitialize'])
    vm.runInContext(method(manager,name),c,{filename:name});
  vm.runInContext(method(settings,'handleLogoutConfirmationResponse'),c);
  vm.runInContext(method(state,'handleCloseMatchMenu'),c);
  vm.runInContext(method(state,'closeMatchMenuAfterConfirmation'),c);
  vm.runInContext(method(group,'handleSpectateExitButtonEvent').replace(/this\._stage\.dispatchEvent\([^;]+;/,'calls.push(["closeSpectate"]);'),c);
  function button(width=360,height=60,attached=true){const parent=new Sprite();parent.stage=stage;const b=new Sprite();b.width=width;b.height=height;b.x=23;b.y=47;b.alpha=.8;if(attached)parent.addChild(b);return {b,parent};}
  return {c,calls,timers,button,Sprite,stage,stageEvents};
}
{
  const {c,calls,stageEvents}=client();c.handleLogoutConfirmationResponse(false);assert.equal(calls.length,0);
  c.handleLogoutConfirmationResponse(true);
  assert.deepEqual(calls,[['event','CranberryCloseMatchMenu'],['action','CRANBERRY_MATCH_ACTION_V1','exit']]);
  stageEvents.get('frame').call(c,{});assert.equal(stageEvents.size,0);assert.deepEqual(calls.at(-1),['closePause']);cases++;
}
{
  const {c,calls,stageEvents}=client();c.m_displayStack.modalOpen=true;
  let callbacks=0;
  c.m_callbackFunc=()=>{assert.equal(++callbacks,1,'Confirmation response re-entered');c.handleLogoutConfirmationResponse(true);};
  c.response=true;c.superClose=()=>{c.m_displayStack.modalOpen=false;};
  vm.runInContext(method(confirmation,'closeWidget').replace('super.closeWidget();','this.superClose();'),c);
  c.closeMenu=()=>{if(c.m_displayStack.modalOpen)c.closeWidget();else calls.push(['closedAfterConfirmation']);};
  c.closeWidget();assert.equal(callbacks,1);assert.equal(c.m_callbackFunc,null);
  assert(!calls.some(x=>x[0]==='closedAfterConfirmation'));
  stageEvents.get('frame').call(c,{});assert.equal(stageEvents.size,0);assert.deepEqual(calls.at(-1),['closedAfterConfirmation']);cases++;
}
{
  const {c,calls,stageEvents}=client();c.m_displayStack.modalOpen=true;c.handleCloseMatchMenu({});
  stageEvents.get('frame').call(c,{});assert(!calls.some(x=>x[0]==='closePause'));assert.equal(stageEvents.size,1);
  c.m_displayStack.modalOpen=false;stageEvents.get('frame').call(c,{});assert.equal(stageEvents.size,0);cases++;
}
for(const mode of ['GAMETYPE_SOLO','GAMETYPE_DUO','GAMETYPE_FIVES','GAMETYPE_TRAINING','GAMETYPE_EVENTS','GAMETYPE_NONE']) {
  for(const width of [60,260,600]) {
    const {c,button}=client(mode),{b,parent}=button(width);c.installResultButtons(b);c.installResultButtons(b);assert.equal(b.events.size,2);
    c.layoutResultButtons({currentTarget:b});let row=c.m_resultRows.get(b);assert(row);assert.equal(parent.children.length,2);
    assert.deepEqual(row.children.map(x=>x.children[0].text),mode==='GAMETYPE_SOLO'?['PLAY AGAIN','MAIN MENU']:['MAIN MENU']);
    assert.equal(row.x,b.x);assert.equal(row.y,b.y);assert.equal(row.alpha,b.alpha);assert.equal(b.visible,false);assert.equal(b.focusable,false);
    assert(row.children.every(x=>x.x+x.width<=width));
    b.x=72;b.width=width+40;b.visible=true;c.layoutResultButtons({currentTarget:b});row=c.m_resultRows.get(b);assert.equal(row.x,72);assert.equal(b.visible,false);assert.equal(parent.children.length,2);
    c.removeResultButtons({currentTarget:b});assert.equal(parent.children.length,1);assert.equal(c.m_resultRows.size,0);assert.equal(b.events.size,0);cases++;
  }
}
for(const flags of [{team:true},{training:true},{hosted:true}]) {
  const {c,calls,button}=client('GAMETYPE_SOLO',flags),{b}=button();c.installResultButtons(b);c.layoutResultButtons({currentTarget:b});
  assert.equal(c.m_resultRows.get(b).children.length,1);c.requestMatchAction('play');assert.equal(calls.length,0);assert.equal(c.m_exitRequested,false);cases++;
}
{
  const {c,button}=client(),{b,parent}=button(400,60,false);c.installResultButtons(b);c.layoutResultButtons({currentTarget:b});assert.equal(c.m_resultRows.size,0);
  parent.addChild(b);c.layoutResultButtons({currentTarget:b});assert.equal(c.m_resultRows.size,1);
  c.processActionButton(null,b);assert.equal(parent.children.length,1);assert.equal(b.events.size,0);cases++;
}
{
  const {c,button}=client();for(let i=0;i<3;i++){let {b}=button();c.installResultButtons(b);c.layoutResultButtons({currentTarget:b});}
  assert.equal(c.m_resultRows.size,3);c.clearResultButtons();assert.equal(c.m_resultRows.size,0);cases++;
}
for(const action of ['play','menu']) {
  const {c,calls,timers}=client();c.requestMatchAction(action);c.requestMatchAction(action);assert.equal(calls.length,1);
  c.handleNativeMatchExit({});c.handleNativeMatchExit({});assert.equal(timers.size,1);assert(!calls.some(x=>x[0]==='nativeLogout'));
  const timer=[...timers.values()][0];timers.clear();timer.call(c);
  const order=key=>calls.findIndex(x=>x[0]===key);
  assert(order('removeListeners')<order('closeWindows'));assert(order('closeWindows')<order('clearNativeData'));assert(order('clearNativeData')<order('nativeLogout'));
  assert.equal(calls.filter(x=>x[0]==='nativeLogout').length,1);
  c.handleMatchSessionReady({});c.requestMatchAction(action);c.handleNativeMatchExit({});assert.equal(timers.size,1);cases++;
}
for(const transition of ['ready','deinitialize']) {
  const {c,calls,timers}=client();c.handleNativeMatchExit({});const old=[...timers.values()][0];
  if(transition==='ready')c.handleMatchSessionReady({});else c.deinitialize();assert.equal(timers.size,0);old.call(c);assert(!calls.some(x=>x[0]==='nativeLogout'));cases++;
}
{
  const {c,calls}=client();c.handleButtonEvent({target:{data:{type:'processActionButton'}}});assert.deepEqual(calls,[['action','CRANBERRY_MATCH_ACTION_V1','menu']]);cases++;
}
for(const [kind,key] of [['click',0],['key',13],['key',32]]) {
  const {c,calls}=client();let stopped=false;const e={keyCode:key,currentTarget:{name:'play'},stopPropagation:()=>stopped=true};
  if(kind==='click')c.handleResultClick(e);else c.handleResultKey(e);assert(stopped);assert.deepEqual(calls,[['action','CRANBERRY_MATCH_ACTION_V1','play']]);cases++;
}
{
  const {c,calls}=client();c.handleSpectateExitButtonEvent({});assert.deepEqual(JSON.parse(JSON.stringify(calls)),[['closeSpectate'],['action','CRANBERRY_MATCH_ACTION_V1','menu']]);cases++;
}
for(const source of [manager,settings,group,state])assert(!source.includes('Not decompiled due to error'));
assert(method(manager,'handleGroupWin').includes('this.m_victoryTimeoutId = setTimeout'));
assert(state.includes('addEventListener("CranberryCloseMatchMenu",this.handleCloseMatchMenu'));
assert(state.includes('removeEventListener("CranberryCloseMatchMenu",this.handleCloseMatchMenu'));
console.log(JSON.stringify({passed:cases,failed:0,scope:'compiled UI actions, solo modes, delayed stage/layout, teardown ordering and stale callbacks'}));
