// Run the actual exported UIInventoryManager methods against a deterministic UI
// clock. The old restartable100ms timer must fail the sustained-update test.
// Usage: node verify-inventory-refresh.cjs UIInventoryManager.as
const fs = require('node:fs');
const assert = require('node:assert/strict');
const source = fs.readFileSync(process.argv[2], 'utf8');
function body(name) {
  const at = source.indexOf(`function ${name}(`);
  assert(at >= 0, `Missing actual method ${name}`);
  const start = source.indexOf('{', at);
  let end = start + 1, depth = 1;
  while (depth && end < source.length) {
    if (source[end] === '{') depth++;
    if (source[end] === '}') depth--;
    end++;
  }
  assert.equal(depth, 0);
  return source.slice(start + 1, end - 1).replace(/\bvar (\w+):[\w.]+/g, 'var $1');
}
const constants = Object.fromEntries([...source.matchAll(/public static const (\w+):uint = (\d+);/g)]
  .map(([, name, value]) => [name, Number(value)]));
const noop = () => {};
const db = {addEventListener: noop, removeEventListener: noop};
let now, timers, stage, snapshots, rows, containerRows, proximityRows;
class Dispatcher {
  constructor() {this.listeners = new Map();}
  addEventListener(type, fn) {
    if (!this.listeners.has(type)) this.listeners.set(type, new Set());
    this.listeners.get(type).add(fn);
  }
  removeEventListener(type, fn) {this.listeners.get(type)?.delete(fn);}
  dispatchEvent(event) {for (const fn of [...(this.listeners.get(event.type) ?? [])]) fn(event);}
}
class Timer extends Dispatcher {
  constructor(delay, repeatCount) {super(); Object.assign(this, {delay, repeatCount, running:false}); timers.push(this);}
  start() {if (!this.running) {this.running = true; this.due = now + this.delay;}}
  stop() {this.running = false;}
  reset() {this.stop();}
}
class UIDataBinding {
  constructor(name) {this.name = name;}
  setValue(value) {if (this.name === 'INV_INSPECTED_LIST_DATA') snapshots.push({time:now, rows:value});}
}
class LegendEvent {constructor(type) {this.type = type;}}
LegendEvent.HIDE = 'hideLegend';
class DataProvider extends Array {constructor(items) {super(...items);}}
const singleton = {addEventListener:noop, removeEventListener:noop, initialize:noop, deinitialize:noop};
const bindings = {
  ...constants, Timer, TimerEvent:{TIMER_COMPLETE:'timerComplete'}, Event:{ENTER_FRAME:'enterFrame'},
  UIEvent:{UI_ENTER:'enter', UI_EXIT:'exit'}, WidgetNames:{INVENTORY_WINDOW:8, MORE_INFO_WINDOW:95},
  InventoryEvent:{}, uiDBEvent:{CHANGED:'changed'}, LegendEvent, UIDataBinding, DataProvider,
  BindingNames:Object.fromEntries([...source.matchAll(/BindingNames\.(\w+)/g)].map(([, key]) => [key,key])),
  LoadoutManager:{getInstance:()=>singleton}, RecipeManager:{getInstance:()=>singleton},
  uiDBManager:{query:query => query === 'bag' ? rows : query === 'containers' ? containerRows : query === 'prox' ? proximityRows : [], getTable:()=>db},
  InventoryQueryStrings:{getAccessedCharacterInventoryQuery:()=>'bag', getAccessedCharacterCurrentContainersQuery:()=>'containers', getProximateItemsQuery:()=>'prox'},
  DropManager:{getInstance:()=>singleton}, ContainerData:class {constructor(data) {Object.assign(this,data);}},
  InventoryItemData:class {constructor(data) {Object.assign(this,data);}},
};
function fresh() {
  now = 0; timers = []; snapshots = []; rows = []; containerRows = []; proximityRows = []; stage = new Dispatcher();
  const state = {m_isInitalized:false, m_isInProximity:false, m_bindings:[],
    m_stackingManager:{processInspectedItems:items=>structuredClone(items)},
    m_playerInventoryDb:db, m_proximateItemsDb:db, m_accessedCharacterInventoryDb:db,
    m_accessedCharacterInfoDb:db, m_accessedCharacterCurrentContainersDb:db,
    onLoadoutSlotDataChange:noop, onVehicleLoadoutSlotDataChange:noop, onRecipeDataChange:noop, onRecipeCraftingStatus:noop,
  };
  for (const name of ['initialize','deinitialize','handleUIEnter','handleUIExit','updatePlayerInventoryDb',
    'updateProximateItemsDb','updateAccessedCharacterInventoryDb','updateAccessedCharacterCurrentContainersDb',
    'onSpamTimerComplete','onInspectedUpdateFrame','cancelInspectedUpdate']) {
    if (!source.includes(`function ${name}(`)) continue;
    state[name] = new Function(...Object.keys(bindings), `return function(param1) {${body(name)}}`)(...Object.values(bindings)).bind(state);
  }
  state.initialize(stage);
  return state;
}
function advance(to) {
  while (now < to) {
    now++;
    for (const timer of timers) {
      if (timer.running && timer.due <= now) {
        timer.running = false;
        timer.dispatchEvent({type:'timerComplete'});
      }
    }
    if (now % 16 === 0) stage.dispatchEvent({type:'enterFrame'});
  }
}
let count = 0;
function changed(state, version) {rows = [{ItemGuid:'bag-item', ItemCount:version}]; state.updateAccessedCharacterInventoryDb({type:'changed'});}
if (process.argv.includes('--trace')) {
  const state = fresh();
  for (let time=0; time<=300; time+=50) {advance(time); changed(state,time);}
  advance(400);
  console.log(JSON.stringify({changesMs:[0,50,100,150,200,250,300], paints:snapshots.map(item=>({atMs:item.time, version:item.rows[0].ItemCount}))},null,2));
  process.exit(0);
}
{
  const state = fresh();
  rows = [{ItemGuid:'initial',ItemCount:1}];
  state.updateAccessedCharacterInventoryDb();
  assert.equal(snapshots.length,1,'initial explicit refresh stays immediate');
  changed(state,2); changed(state,3); changed(state,4);
  assert.equal(snapshots.length,1,'same-frame table updates coalesce');
  advance(16);
  assert.equal(snapshots.length,2,'burst refresh must appear on the next frame, not after100ms');
  assert.equal(snapshots[1].rows[0].ItemCount,4,'coalesced refresh uses final database state');
  advance(160); assert.equal(snapshots.length,2,'completed refresh does not keep firing'); count += 5;
}
{
  const state = fresh();
  // Inventory/rights rows can change repeatedly while looting. Restarting100ms
  // on every50ms update postpones the old UI indefinitely until the burst ends.
  for (let time=0; time<=300; time+=50) {
    advance(time); changed(state,time);
    advance(Math.floor(time / 16) * 16 + 16);
    assert.equal(snapshots.at(-1)?.rows[0].ItemCount,time,`update at${time}ms visible by next frame`);
    assert(snapshots.at(-1).time - time <= 16, 'paint latency bounded by one frame'); count += 2;
  }
  assert.equal(snapshots.length,7,'ongoing updates cannot starve the bag display'); count++;
}
{
  const state = fresh(); changed(state,1);
  state.handleUIExit({data:8}); advance(160);
  assert.equal(snapshots.length,0,'inventory exit cancels pending list update'); count++;
}
{
  const state = fresh(); changed(state,1);
  state.deinitialize(); advance(160);
  assert.equal(snapshots.length,0,'deinitialize cancels callback before destroying bindings'); count++;
}
{
  const state = fresh(); changed(state,1);
  // Switching to ground proximity while an old bag update is queued must leave
  // the proximity list intact, even after the delayed bag callback would fire.
  state.updateAccessedCharacterCurrentContainersDb();
  proximityRows = [{ItemGuid:'ground-item'}]; state.updateProximateItemsDb();
  advance(160);
  assert.equal(snapshots.length,1); assert.equal(snapshots[0].rows[0].ItemGuid,'ground-item'); count += 2;
}
{
  const state = fresh(); changed(state,1);
  rows = [{ItemGuid:'new-snapshot'}]; state.updateAccessedCharacterInventoryDb(); advance(160);
  assert.equal(snapshots.length,1,'immediate refresh supersedes queued callback'); count++;
}
console.log(`Inventory refresh: ${count} assertions passed using actual exported methods; bursts coalesce within one frame.`);
