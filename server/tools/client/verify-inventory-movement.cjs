// Execute rebuilt/re-exported AS3 methods, not a hand-written replacement policy.
// Usage: node verify-inventory-movement.cjs UIState_InGame.as [UIRoot/scripts] [BRWidgetDefinitions.xml]
// Behavioral harness only: this does not replace a live Scaleform/client playtest.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const source = fs.readFileSync(process.argv[2], 'utf8');
function body(text, name) {
  const at = text.indexOf(`function ${name}(`);
  assert(at >= 0, `Missing actual AS3 method ${name}`);
  const start = text.indexOf('{', at);
  let end = start + 1, depth = 1;
  while (depth && end < text.length) {
    if (text[end] === '{') depth++;
    if (text[end] === '}') depth--;
    end++;
  }
  assert.equal(depth, 0);
  return text.slice(start + 1, end - 1).replace(/\bvar (\w+):[\w.]+/g, 'var $1')
    .replace(/\bis (flash\.events\.)?DataEvent\b/g, 'instanceof DataEvent');
}
// August BRWidgetDefinitions.xml SHA256:
// 1c385fc74b5012e580c6fede78006ced81f50824a8e64bea404b50e7c5018407.
const shippedIds = {
  CONSOLE_WINDOW:1, TAB_NAVIGATION_WINDOW:7, INVENTORY_WINDOW:8, SETTINGS_WINDOW:9,
  CONFIRMATION_DIALOG_WINDOW:18, HUD_RETICLE_WINDOW:20, MAP_WINDOW:35,
  TAB_NAVIGATION_BACKGROUND:50, SETTINGS_MENU_IN_GAME_WINDOW:51,
  INGAME_BROWSER_WINDOW:58, DEATH_SCREEN_WINDOW:62, BOUNTY_WINDOW:68,
  CRAFTING_WINDOW:69, MORE_INFO_WINDOW:95, DEATH_SCREEN_WINDOW_BACKGROUND:103,
};
let widgets = shippedIds;
if (process.argv[4]) {
  widgets = Object.fromEntries([...fs.readFileSync(process.argv[4], 'utf8')
    .matchAll(/<widget\s+id="(\d+)"\s+key="([^"]+)"/g)].map(([, id, key]) => [key, Number(id)]));
  for (const [key, id] of Object.entries(shippedIds)) assert.equal(widgets[key], id, key);
}
class WidgetEvent {
  constructor(type, widgetId=0, isModal=false, data=null) {Object.assign(this, {type, widgetId, isModal, data});}
}
class Event {
  constructor(type, bubbles=false, cancelable=false) {Object.assign(this,{type,bubbles,cancelable,defaultPrevented:false});}
  preventDefault() {if(this.cancelable) this.defaultPrevented=true;}
}
class DataEvent extends Event {
  constructor(type,bubbles=false,cancelable=false,data='') {super(type,bubbles,cancelable);this.data=data;}
}
Object.assign(WidgetEvent, {OPEN_WIDGET:'openWidget', WIDGET_OPENED:'widgetOpened', CLOSE_WIDGET:'closeWidget', CLOSE_ALL:'closeAll', UPDATE_WIDGET_DATA:'onUpdateWidgetData'});
class TabNavigationWindowData {constructor(index) {this.index=index;}}
class UIDataBindingEvent {constructor(type, key, data) {Object.assign(this, {type, key, data});}}
UIDataBindingEvent.UPDATE='bindingUpdate';
class DataProvider extends Array {constructor(items) {super(...items);}}
let current;
// Native constant table 1431e5e10: mouse + gamepad capture must NOT capture keyboard bit2.
const input = {
  INPUT_MOUSE:1, INPUT_KEYBOARD:2, INPUT_GAMEPAD_BTNS:0x1c, INPUT_GAMEPAD_LS:0x40, INPUT_GAMEPAD_ALL:0xfc,
  GetInputFlags:()=>current.flags, SetInputFlags:value=>{current.flags=value;},
};
const scope = {
  WidgetNames:widgets, WidgetEvent, Event, DataEvent, flash:{events:{DataEvent}}, uint:value=>Number(value)>>>0, TabNavigationWindowData, UIDataBindingEvent, DataProvider,
  UIBindingKeyboard:{SetMovementEnabled:value=>{current.movement=value;}, SetKeyboardEnabled:value=>{current.keyboard=value;},
    GetMovementEnabled:()=>current.movement, GetKeyboardEnabled:()=>current.keyboard},
  UIBindingInput:input,
  UIBindingSystem:{SetMouseHidden:value=>{current.mouseHidden=value;}, GetMouseHidden:()=>current.mouseHidden, isController:()=>false,
    DispatchWallOfData:(...args)=>current.traces.push(args)},
  UIBindingPlayer:{IsKnockedOut:()=>current.knockedOut}, UIBindingMatch:{IsInBox:()=>current.inBox},
  UIBindingLoadouts:{OnInventoryUIClosed:()=>{current.inventoryClosed++;}},
  UIBindingLocale:{translateCodeString:value=>value}, UITabNavigationManager:{KEY_NAVIGATION_DATA:'tabData'},
  UIMatchManager:{gameType:'solo', GAMETYPE_SOLO:'solo'},
  UISettingsManager:{HAS_KEY_TRAPPED_FOR_KEYBINDING:false, SETTINGS_MENU_WIDGET_ID:widgets.SETTINGS_MENU_IN_GAME_WINDOW},
  MainController:{getWidgetManager:()=>current.manager},
  UIConsoleManager:{OverlayActive:()=>current.overlay?.m_overlayRoot?.visible===true},
};
function compile(text, name, bindings) {
  const at=text.indexOf(`function ${name}(`)+`function ${name}(`.length;
  const args=text.slice(at,text.indexOf(')',at)).split(',').filter(arg=>arg.trim()).map(arg=>arg.trim().split(':')[0]).join(',');
  return new Function(...Object.keys(bindings), `return function(${args}) {${body(text,name)}}`)(...Object.values(bindings));
}
const methods = {};
for (const name of ['handleWidgetEvent','refreshInventoryMovement','handleSetMouse','handleInventoryToggleEvent',
  'handleEndCharacterAccessEvent','openMenu','closeMenu','closeLoadingMenuWidgets','handleMenuIndexChange',
  'setSelectedMenuIndex','getMenuDataProvider']) methods[name]=compile(source,name,scope);
for(const name of ['handleInteractionInputRefresh','applyInventoryMovement','traceInteractionInput'])
  if(source.includes(`function ${name}(`)) methods[name]=compile(source,name,scope);
function fresh(inBox=false) {
  const state=Object.assign({m_movementWindows:{}, m_matchOver:false, movement:true, keyboard:true, mouseHidden:true,
    flags:0, knockedOut:false, inventoryClosed:0, inBox, loaded:new Set(), loading:new Set(), windows:[], modalId:null,
    traces:[], m_interactionInputTraceCount:0, m_interactionInputTraceLast:'',
    hideLeftSideHud(){}, showLeftSideHud(){}},methods);
  current=state;
  state.manager={isWidgetLoaded:id=>state.loaded.has(id), isWidgetLoading:id=>state.loading.has(id)};
  state.m_displayStack={
    get windowDepth(){return state.windows.length;},
    get top(){const id=state.windows.at(-1);return id===undefined?null:{widget:{id}};},
    get modalOpen(){return state.modalId!==null;},
    get modal(){return {widget:{id:state.modalId},closeWidget:()=>close(state,state.modalId)};},
    isWidgetStacked:id=>state.windows.includes(id)||state.modalId===id,
    pop:id=>close(state,id), closeModal:()=>close(state,state.modalId),
  };
  state.m_stageRef={dispatchEvent(event){
    current=state;
    if(event.type==='cranberryInputRefresh' && state.handleInteractionInputRefresh) {
      state.handleInteractionInputRefresh(event); return !event.defaultPrevented;
    }
    if(event.type===WidgetEvent.OPEN_WIDGET) {if(!state.loaded.has(event.widgetId)) state.loading.add(event.widgetId);}
    else if(event.type===WidgetEvent.CLOSE_WIDGET) close(state,event.widgetId);
    else if(event.type===WidgetEvent.UPDATE_WIDGET_DATA && event.widgetId===widgets.TAB_NAVIGATION_WINDOW)
      state.handleMenuIndexChange({index:event.data.index});
    return true;
  }};
  return state;
}
function opened(state,name) {
  const id=widgets[name];assert(id!==undefined,name);current=state;
  state.loading.delete(id);state.loaded.add(id);
  if(name==='CONFIRMATION_DIALOG_WINDOW') state.modalId=id;
  else if(['INVENTORY_WINDOW','MAP_WINDOW','INGAME_BROWSER_WINDOW','SETTINGS_MENU_IN_GAME_WINDOW','BOUNTY_WINDOW'].includes(name)) {
    if(!state.windows.includes(id)) state.windows.push(id);
  }
  state.handleWidgetEvent(new WidgetEvent(WidgetEvent.WIDGET_OPENED,id));
}
function close(state,idOrName) {
  current=state;
  const id=typeof idOrName==='string'?widgets[idOrName]:idOrName;
  state.loading.delete(id);
  // WidgetManager stops propagation when cancelling a load or closing an absent widget.
  if(!state.loaded.delete(id)) return;
  state.windows=state.windows.filter(value=>value!==id);
  if(state.modalId===id) state.modalId=null;
  // DisplayStack removes the stack entry before the successful nested close event.
  state.handleWidgetEvent(new WidgetEvent(WidgetEvent.CLOSE_WIDGET,id));
}
let count=0;
function expectMovement(state,allowed,label) {
  // SetKeyboardEnabled 141210af0 writes 143cd6049; false skips fresh WASD at
  // 14158fa07..0c -> 1415926fd. SetMovementEnabled 141210b20 writes 143cd6048;
  // its false gate at 1415927cc zeroes axes. BOTH gates must permit new input.
  for(const [key,vector] of Object.entries({W:[0,1],A:[-1,0],S:[0,-1],D:[1,0]})) {
    const sampled=state.keyboard&&state.movement&&!(state.flags&input.INPUT_KEYBOARD)?vector:[0,0];
    assert.deepEqual(sampled,allowed?vector:[0,0],`${label}: fresh ${key} (movement=${state.movement}, keyboard=${state.keyboard}, flags=${state.flags})`);
    count++;
  }
  assert.equal(state.movement,allowed,`${label}: movement gate`);
  assert.equal(state.keyboard,allowed,`${label}: keyboard gate`);
}
function expectCursor(state,shown,label) {
  assert.equal(state.mouseHidden,!shown,`${label}: cursor`);
  assert.equal(Boolean(state.flags&input.INPUT_MOUSE),shown,`${label}: UI mouse capture`);count+=2;
}
for(const reverse of [false,true]) {
  const state=fresh();
  for(const name of reverse?['INVENTORY_WINDOW','TAB_NAVIGATION_WINDOW']:['TAB_NAVIGATION_WINDOW','INVENTORY_WINDOW']) opened(state,name);
  expectMovement(state,true,'inventory in either asynchronous open order');expectCursor(state,true,'inventory clickable');
  close(state,'INVENTORY_WINDOW');expectMovement(state,false,'remaining options/bounty tab blocks gameplay');
  opened(state,'INVENTORY_WINDOW');expectMovement(state,true,'inventory reopens');
  for(const name of reverse?['TAB_NAVIGATION_WINDOW','INVENTORY_WINDOW']:['INVENTORY_WINDOW','TAB_NAVIGATION_WINDOW']) close(state,name);
  expectMovement(state,true,'either close order restores gameplay');expectCursor(state,false,'closed menu');
}
for(const blocker of ['CONFIRMATION_DIALOG_WINDOW','INGAME_BROWSER_WINDOW','MAP_WINDOW','CONSOLE_WINDOW']) {
  for(const first of [false,true]) {
    const state=fresh();if(first) opened(state,blocker);
    opened(state,'TAB_NAVIGATION_WINDOW');opened(state,'INVENTORY_WINDOW');if(!first) opened(state,blocker);
    expectMovement(state,false,`${blocker} blocks both orderings`);expectCursor(state,true,`${blocker} interactive`);
    opened(state,'INVENTORY_WINDOW');expectMovement(state,false,'late/repeated inventory event cannot bypass blocker');
    close(state,blocker);expectMovement(state,true,'closing blocker restores inventory');expectCursor(state,true,'inventory still clickable');
  }
}
{
  const state=fresh();opened(state,'MAP_WINDOW');opened(state,'CONFIRMATION_DIALOG_WINDOW');close(state,'MAP_WINDOW');
  expectMovement(state,false,'second blocker survives close');opened(state,'HUD_RETICLE_WINDOW');
  expectMovement(state,false,'unrelated widget cannot enable gameplay');
  close(state,'CONFIRMATION_DIALOG_WINDOW');close(state,'HUD_RETICLE_WINDOW');
  state.handleWidgetEvent(new WidgetEvent(WidgetEvent.CLOSE_ALL));
  expectMovement(state,true,'close all resets state');assert.deepEqual(state.m_movementWindows,{});
}
// Actual menu routing, including the lobby bounty-tab offset. Native
// FUN_140d83bf0 emits EVENT_TOGGLE_INVENTORY for nonself/nonvehicle access;
// FUN_140d83ff0 emits EVENT_END_CHARACTER_ACCESS when external access ends.
for(const inBox of [false,true]) {
  for(const endAccess of [false,true]) {
    const state=fresh(inBox);state.handleInventoryToggleEvent({type:'EVENT_TOGGLE_INVENTORY'});
    assert(state.loading.has(widgets.TAB_NAVIGATION_WINDOW));opened(state,'TAB_NAVIGATION_WINDOW');
    state.handleMenuIndexChange({index:inBox?1:0});
    assert(state.loading.has(widgets.INVENTORY_WINDOW),'menu selects actual inventory widget');opened(state,'INVENTORY_WINDOW');
    expectMovement(state,true,'ordinary/bag inventory after native toggle');expectCursor(state,true,'ordinary/bag inventory');
    if(endAccess) state.handleEndCharacterAccessEvent({type:'EVENT_END_CHARACTER_ACCESS'});
    else state.handleInventoryToggleEvent({type:'EVENT_TOGGLE_INVENTORY'});
    expectMovement(state,true,'toggle/end-access close releases both gates');expectCursor(state,false,'toggle/end-access close');
    assert.equal(state.inventoryClosed,1);assert(!state.loaded.has(widgets.INVENTORY_WINDOW));assert(!state.loaded.has(widgets.TAB_NAVIGATION_WINDOW));
  }
}
{
  const state=fresh();opened(state,'INVENTORY_WINDOW');expectMovement(state,true,'direct container window without tabs');expectCursor(state,true,'direct container');
  state.handleEndCharacterAccessEvent({type:'EVENT_END_CHARACTER_ACCESS'});
  expectMovement(state,true,'direct access closes cleanly');expectCursor(state,false,'direct access close');
}
{
  const state=fresh();state.handleInventoryToggleEvent({});opened(state,'TAB_NAVIGATION_WINDOW');state.handleMenuIndexChange({index:0});
  state.handleEndCharacterAccessEvent({});assert.equal(state.loading.has(widgets.INVENTORY_WINDOW),false);
  expectMovement(state,true,'end access during asynchronous load cancels and releases gates');
}
if(process.argv[3]) {
  const read=relative=>fs.readFileSync(path.join(process.argv[3],relative),'utf8');
  const managerSource=read('views/inventory/UIInventoryManager.as');
  const enter=body(managerSource,'handleUIEnter');
  const inventoryCase=enter.slice(enter.indexOf('case WidgetNames.INVENTORY_WINDOW:'),enter.indexOf('case WidgetNames.MORE_INFO_WINDOW:'));
  for(const table of ['AccessedCharacterCurrentContainers','AccessedCharacterInventory','AccessedCharacterInfo'])
    assert(inventoryCase.includes(`getTable("${table}")`),`${table} belongs to INVENTORY_WINDOW`);
  let rows=[],inspected,drop;
  const containerScope={uiDBManager:{query:()=>rows},InventoryQueryStrings:{getAccessedCharacterCurrentContainersQuery:()=>''},
    DropManager:{getInstance:()=>drop},ContainerData:class{constructor(row){Object.assign(this,row);}},BINDING_INSPECTED_CONTAINER_DATA:0};
  const update=compile(managerSource,'updateAccessedCharacterCurrentContainersDb',containerScope);
  const manager={m_bindings:[{setValue:value=>{inspected=value;}}]};
  // Newer managers cancel a queued bag refresh when returning to proximity.
  // Run that actual helper too; its frame scheduling is covered by verify-inventory-refresh.
  if (managerSource.includes('function cancelInspectedUpdate(')) {
    manager.m_stage={removeEventListener(){}};
    manager.cancelInspectedUpdate=compile(managerSource,'cancelInspectedUpdate',{Event:{ENTER_FRAME:'enterFrame'}});
  }
  for(const container of [null,{Guid:'bodybag'},{Guid:'world-container'}]) {
    rows=container?[container]:[];drop={};update.call(manager);
    assert.equal(manager.m_isInProximity,!container);assert.equal(inspected?.Guid??null,container?.Guid??null);
    assert.equal(drop.containerData,inspected);count+=3;
  }
  assert(body(read('ui/core/UIWidget.as'),'configUI').includes('WidgetEvent.WIDGET_OPENED,this.m_widget != null ? this.m_widget.id : 0'));
  const load=body(read('ui/core/managers/WidgetManager.as'),'handleWidgetLoadComplete');
  assert(load.indexOf('_loc4_.widget =')<load.indexOf('this._displayStack.push('),'identity assigned before stage insertion');
  assert(body(read('ui/constants/WidgetNames.as'),'updateWidgetDefinitions').includes('WidgetNames[param1.getWidgetKeyById(_loc2_.id)] = _loc2_.id'));
  console.log('Supporting exports: real widget identity and shared bodybag/container/proximity bindings verified.');
}
assert(source.includes('addEventListener(GameEvent.EVENT_TOGGLE_INVENTORY,this.handleInventoryToggleEvent'));
assert(source.includes('addEventListener(GameEvent.EVENT_END_CHARACTER_ACCESS,this.handleEndCharacterAccessEvent'));
console.log(`Inventory movement: ${count} assertions passed through actual dispatch/menu/cursor methods and both native gates.`);
module.exports={source,body,compile,scope,Event,widgets,input,fresh,opened,close,expectMovement,expectCursor};
