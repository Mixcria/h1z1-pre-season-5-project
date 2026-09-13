// Exercise the compiled/re-exported client handlers, including the native logout boundary.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const scripts = process.argv[2];
const read = p => fs.readFileSync(path.join(scripts, p), 'utf8');
const manager = read('ui/managers/UIMatchManager.as');
const settings = read('views/settings/UISettingsManager.as');
const consoleSource = read('views/console/UIConsoleManager.as');
function method(source, name) {
  const at = source.indexOf('function ' + name + '(');
  assert(at >= 0, name);
  let start = source.indexOf('{', at), depth = 1, end = start + 1;
  while (depth) { if(source[end] === '{') depth++; if(source[end] === '}') depth--; end++; }
  return source.slice(at, end)
    .replace(/:\s*(?:String|Boolean|Number|int|uint|void|Array|Object|Sprite|TextField|Button|GameEvent|Event|MouseEvent|KeyboardEvent|MatchEvent)\b/g, '')
    .replace(/ as (?:Sprite|Button)/g, '');
}
function client() {
  const calls = [], timers = [], events = [];
  class Sprite {
    constructor() { this.children = []; this.x = 0; this.y = 0; this.graphics = {
      beginFill: color => { this.color = color; },
      drawRect: (x,y,w,h) => { this.width=w; this.height=h; }, endFill() {} }; }
    addChild(c) { c.parent=this; this.children.push(c); return c; }
    removeChild(c) { this.children.splice(this.children.indexOf(c),1); c.parent=null; }
    addEventListener() {} removeEventListener() {}
  }
  const context = vm.createContext({
    calls,timers,events,Sprite,TextField: class {},TextFormat: class {constructor(...args) {this.args=args;}},
    Event:{REMOVED_FROM_STAGE:'removed'},MouseEvent:{CLICK:'click'},KeyboardEvent:{KEY_UP:'keyup'},
    UIBindingSystem:{DispatchWallOfData:(...x)=>calls.push(x),Logout:()=>calls.push(['nativeLogout'])},
    UIBindingMatch:{ClearMatchData:()=>calls.push(['clearMatchData'])},
    UIBindingSound:{UI_MATCH_END_PROGRESS_BAR_STOP:42,PlayUiSound:x=>calls.push(['stopSound',x])},
    setTimeout:(fn,ms)=>{timers.push(fn);calls.push(['timer',ms]);return timers.length;},
    clearTimeout:id=>calls.push(['clearTimer',id]),
    GameEvent:class {constructor(type){this.type=type;}},
    m_stage:{dispatchEvent:e=>events.push(e.type)},
    m_resultRows:{},m_exitRequested:false,m_nativeExitStarted:false,m_windowTimeoutId:15,m_victoryTimeoutId:16,
    removeMatchListeners:()=>calls.push(['removeListeners']),closeAllWindows:()=>calls.push(['closeWindows']),
    handleCloseSettings:()=>calls.push(['closeSettings'])
  });
  for(const name of ['handleMatchSessionReady','requestMatchAction','handleNativeMatchExit','finishNativeMatchExit','cleanUpMatchData',
      'installResultButtons','makeResultButton','removeResultButtons','handleResultClick','handleResultKey'])
    vm.runInContext(method(manager,name),context,{filename:name});
  vm.runInContext(method(settings,'handleLogoutConfirmationResponse'),context);
  return {context,calls,timers,events,Sprite};
}
{
  const {context:c,calls}=client();
  c.handleLogoutConfirmationResponse(false); assert.equal(calls.length,0);
  c.handleLogoutConfirmationResponse(true);
  assert.deepEqual(calls,[['closeSettings'],['CRANBERRY_MATCH_ACTION_V1','exit']]);
  assert(!calls.some(x=>x[0]==='nativeLogout'));
}
for(const action of ['play','menu']) {
  const {context:c,calls,timers}=client();
  c.requestMatchAction(action); c.requestMatchAction(action);
  assert.deepEqual(calls,[['CRANBERRY_MATCH_ACTION_V1',action]]);
  c.handleNativeMatchExit({}); c.handleNativeMatchExit({});
  assert.equal(timers.length,1);
  assert(!calls.some(x=>x[0]==='nativeLogout'));
  timers[0].call(c);
  assert.equal(calls.filter(x=>x[0]==='nativeLogout').length,1);
  for(const event of ['clearTimer','clearMatchData','removeListeners','closeWindows'])
    assert(calls.findIndex(x=>x[0]===event)<calls.findIndex(x=>x[0]==='nativeLogout'));
  assert(calls.some(x=>x[0]==='clearTimer' && x[1]===15));
  assert(calls.some(x=>x[0]==='clearTimer' && x[1]===16));
  c.handleMatchSessionReady({});
  c.requestMatchAction(action);
  c.handleNativeMatchExit({});
  assert.equal(timers.length,2); // A second pregame exit works even without EVENT_START_MATCH.
}
for(const width of [260,400,600]) {
  const {context:c,Sprite}=client();
  const parent=new Sprite(), original=new Sprite();
  original.width=width;original.height=60;original.x=100;original.y=200;
  parent.addChild(original);
  c.installResultButtons(original); c.installResultButtons(original);
  assert.equal(parent.children.length,2);
  const row=parent.children[1];
  assert.equal(row.x,100);assert.equal(row.y,200);assert.equal(original.visible,false);
  assert.deepEqual(row.children.map(x=>x.children[0].text),['PLAY AGAIN','MAIN MENU']);
  assert(row.children[1].x+row.children[1].width<=width);
  c.removeResultButtons({currentTarget:original});
  assert.equal(parent.children.length,1);
}
{
  const {context:c,calls}=client();
  c.handleResultKey({keyCode:9,currentTarget:{name:'play'}});assert.equal(calls.length,0);
  c.handleResultKey({keyCode:13,currentTarget:{name:'play'}});
  assert.deepEqual(calls,[['CRANBERRY_MATCH_ACTION_V1','play']]);
}
assert(consoleSource.includes('if(_loc2_ == "@cranberry/match-exit/1;logout")'));
assert(consoleSource.includes('new GameEvent("CranberryMatchExit")'));
for(const field of ['m_resultRows:Dictionary = new Dictionary(true)','m_exitRequested:Boolean = false',
    'm_nativeExitStarted:Boolean = false','m_victoryTimeoutId:uint = 0']) assert(manager.includes(field),field);
assert(!settings.includes('Not decompiled due to error'));
assert(!manager.includes('Not decompiled due to error'));
console.log('PASS: compiled exit request, native teardown ordering, deduplication, timer cleanup, both result actions, keyboard input, layout bounds and row cleanup');
