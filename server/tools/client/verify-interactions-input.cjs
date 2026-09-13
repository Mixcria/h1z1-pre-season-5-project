// Interaction regressions against exported methods, including the real pending-load cancel path.
// Same arguments as verify-inventory-movement; failure remains a nonzero exit on the baseline.
const fs=require('node:fs'), path=require('node:path'), assert=require('node:assert/strict');
const h=require('./verify-inventory-movement.cjs');
const {compile,scope,Event,widgets,input,fresh,opened,close,expectMovement,expectCursor}=h;
const scripts=process.argv[3];
const managerSource=fs.readFileSync(path.join(scripts,'ui/core/managers/WidgetManager.as'),'utf8');
const consoleSource=fs.readFileSync(path.join(scripts,'views/console/UIConsoleManager.as'),'utf8');
const overlayScope={...scope,OverlayActive:()=>scope.UIConsoleManager.OverlayActive(),getTimer:()=>0};
function overlay(state) {
  const panel={visible:false};
  state.overlay={m_overlayRoot:panel,m_stage:Object.assign(state.m_stageRef,{focus:null,numChildren:1,setChildIndex(){}}),
    m_overlayFriend:'',m_overlayInput:{text:''},m_overlayDrafts:{},overlayQueue(){}};
  for(const name of ['overlayToggle','overlayClose']) state.overlay[name]=compile(consoleSource,name,overlayScope);
  return state.overlay;
}
const cases=[];
function test(name,fn){cases.push([name,fn]);}
for(const failure of [false,true]) test(failure?'failed pending menu load releases input':'cancelled pending menu load releases input',()=>{
  const s=fresh(), loading={}; loading[widgets.TAB_NAVIGATION_WINDOW]={};
  // Execute the real manager cancellation/IO-error method with its native dictionary ownership.
  s.manager={_loadingWidgets:loading,_stageRef:s.m_stageRef,
    isWidgetLoading:id=>loading[id]!=null,isWidgetLoaded:()=>false};
  const bindings={...scope,WidgetDefinitions:{WIDGET_INVALID:0},WidgetLoader:Object};
  const text=managerSource.replace(/ as (WidgetLoader|DisplayObject|InteractiveObject)/g,'');
  s.manager.unloadWidget=compile(text,'unloadWidget',bindings);
  s.manager.handleIOError=compile(text,'handleIOError',bindings);
  // A HUD event sees the pending menu; handleSetMouse captures the mouse before load completion.
  opened(s,'HUD_RETICLE_WINDOW'); expectCursor(s,true,'pending menu captures cursor');
  if(failure) s.manager.handleIOError({target:{loader:{widgetId:widgets.TAB_NAVIGATION_WINDOW}}});
  else assert.equal(s.manager.unloadWidget(widgets.TAB_NAVIGATION_WINDOW),false,'stock close propagation remains suppressed');
  assert.equal(s.manager.isWidgetLoading(widgets.TAB_NAVIGATION_WINDOW),false);
  expectCursor(s,false,'no window remains after cancellation/failure');
  expectMovement(s,true,'cancelled menu returns control');
});
test('overlay close uses current inventory after native end-access',()=>{
  const s=fresh(); opened(s,'TAB_NAVIGATION_WINDOW'); opened(s,'INVENTORY_WINDOW');
  const o=overlay(s); o.overlayToggle();
  s.handleEndCharacterAccessEvent({});
  o.overlayClose();
  expectCursor(s,false,'inventory closed under overlay'); expectMovement(s,true,'gameplay recovered');
  assert.equal(s.flags & input.INPUT_KEYBOARD,0);
});
test('overlay remains interactive while background widgets change',()=>{
  const s=fresh(),o=overlay(s);o.overlayToggle();opened(s,'HUD_RETICLE_WINDOW');
  expectCursor(s,true,'unrelated widget cannot hide overlay cursor');expectMovement(s,false,'overlay blocks gameplay');
});
for(const blocker of ['MAP_WINDOW','CONFIRMATION_DIALOG_WINDOW','CONSOLE_WINDOW'])
test(`overlay close retains a newly opened ${blocker}`,()=>{
  const s=fresh(),o=overlay(s);o.overlayToggle();opened(s,blocker);o.overlayClose();
  expectCursor(s,true,blocker);expectMovement(s,false,blocker);
});
test('overlay close retains current inventory and unrelated input bits',()=>{
  const s=fresh();s.flags=0x100;const o=overlay(s);o.overlayToggle();
  opened(s,'TAB_NAVIGATION_WINDOW');opened(s,'INVENTORY_WINDOW');s.flags|=0x200;o.overlayClose();
  expectCursor(s,true,'new inventory');expectMovement(s,true,'inventory walking');
  assert.equal(s.flags & 0x300,0x300,'unrelated capture bits survive the ownership handoff');
  assert.equal(s.flags & input.INPUT_KEYBOARD,0,'overlay keyboard capture released');
});
for(const guard of ['knockedOut','m_matchOver','keybinding']) test(`recovery preserves ${guard} input ownership`,()=>{
  const s=fresh(),o=overlay(s);o.overlayToggle();
  const captured=s.flags;
  if(guard==='keybinding') scope.UISettingsManager.HAS_KEY_TRAPPED_FOR_KEYBINDING=true;
  else s[guard]=true;
  try {
    o.overlayClose();assert.equal(s.keyboard,false);assert.equal(s.movement,false);assert.equal(s.mouseHidden,false);
    assert.equal(s.flags,captured,'native capture ownership survives the overlay closing');
  }
  finally {scope.UISettingsManager.HAS_KEY_TRAPPED_FOR_KEYBINDING=false;}
});
test('overlay close preserves keyboard capture owned before it opened',()=>{
  const s=fresh();s.flags=input.INPUT_KEYBOARD;const o=overlay(s);o.overlayToggle();o.overlayClose();
  assert.equal(s.flags & input.INPUT_KEYBOARD,input.INPUT_KEYBOARD);
});
test('overlay outside gameplay retains original fallback restoration',()=>{
  const s=fresh(),o=overlay(s);delete s.handleInteractionInputRefresh;
  o.overlayToggle();o.overlayClose();expectCursor(s,false,'menu fallback');expectMovement(s,true,'menu fallback');
});
test('ordinary vehicle end-access close releases capture without a selection request',()=>{
  const s=fresh();opened(s,'TAB_NAVIGATION_WINDOW');opened(s,'INVENTORY_WINDOW');
  s.handleEndCharacterAccessEvent({type:'EVENT_END_CHARACTER_ACCESS'});
  expectCursor(s,false,'vehicle inventory end access');expectMovement(s,true,'vehicle close');assert.equal(s.inventoryClosed,1);
});
test('input tracing is bounded and contains only UI state',()=>{
  const s=fresh();assert.equal(typeof s.traceInteractionInput,'function');
  for(let i=0;i<200;i++){opened(s,'INVENTORY_WINDOW');close(s,'INVENTORY_WINDOW');}
  assert.equal(s.traces.length,128);
  assert(s.traces.every(([type,text])=>type==='InteractionInput' && text.includes('flags=') && !text.includes('draft')));
});
let failures=0;
for(const [name,fn] of cases) {
  try{fn();console.log('PASS '+name);}
  catch(error){failures++;console.error('FAIL '+name+': '+error.message);}
}
console.log(`Interactions input: ${cases.length-failures}/${cases.length} passed. Native rendering/input remains pending.`);
if(failures) process.exitCode=1;
