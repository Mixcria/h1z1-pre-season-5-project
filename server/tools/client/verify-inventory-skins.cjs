// Execute the compiled/re-exported ItemActionMenu methods, including the actual selector.
// This checks UI logic and native binding arguments; live Scaleform rendering remains a playtest.
// node verify-inventory-skins.cjs rebuilt.as original.as
const fs=require('node:fs'), path=require('node:path'), assert=require('node:assert/strict');
const source=fs.readFileSync(process.argv[2],'utf8');
function method(text,name) {
  const at=text.indexOf(`function ${name}(`); assert(at>=0,name);
  const start=text.indexOf('{',at); let end=start+1,depth=1;
  while(depth) {depth+=(text[end]==='{')-(text[end]==='}');end++;}
  const params=text.slice(text.indexOf('(',at)+1,text.indexOf(')',at)).replace(/:\w+/g,'');
  const body=text.slice(start+1,end-1).replace(/\bvar (\w+):[\w.]+/g,'var $1')
    .replace(/\bas (?:ItemActionData|InventoryItemData|TileList|int|String|Number|Class)\b/g,'').replace(/super(?:\.draw)?\(\);/g,'')
    .replace(/param1\.target is ItemActionMenuRenderer/g,'param1.target instanceof ItemActionMenuRenderer')
    .replace(/(?<![\w.])(stage|parent|invalidate|dispatchEvent)\b/g,'this.$1')
    .replace(/choices.sortOn\("name",Array.CASEINSENSITIVE\);/g,'choices.sort((a,b)=>a.name.localeCompare(b.name));');
  return new Function(...Object.keys(scope),`return function(${params}) {${body}}`)(...Object.values(scope));
}
function map(name) {const m=source.match(new RegExp(name+':Object = (\\{[\\s\\S]*?\\});'));assert(m,name);return JSON.parse(m[1]);}
class DataProvider extends Array {constructor(items) {super(...items);} requestItemAt(i) {return this[i];}}
class ItemActionData {constructor(row) {Object.assign(this,{id:row.Id,name:row.Name,iconId:row.IconId});}}
class Point {constructor(x,y) {Object.assign(this,{x,y});}}
class Event {constructor(type) {this.type=type;}} Event.CLOSE='close';
let rows=[],owner='self',owned=new Set(),dsRows=[],calls=[],updates=new Set(),changes=new Set(),carried=new Map(),carriedSlots=new Map(),head=0,hoodState='';
const native={};
for(const name of ['ItemUseOptionDataSourceUpdate','SelectEditorSkinItemCollectionId','SelectEditorSkinItemSlotType',
  'SelectEditorSkinTargetPrototypeItemId','SetSkinItemByItemIdWithCachedData','RequestUseItem']) native[name]=(...args)=>calls.push([name,...args]);
const ds={GetRowCount:()=>dsRows.length,GetData:(i,key)=>String(dsRows[i][key]),
  RemoveUpdateListener:f=>updates.delete(f),RemoveDataChangedListener:f=>changes.delete(f)};
const scope={int:v=>Number(v)|0,Number,String,Math,Array,Point,Event,MouseEvent:{MOUSE_DOWN:'down'},
  ItemActionData,DataProvider,SKIN_CATEGORY_BY_ITEM:map('SKIN_CATEGORY_BY_ITEM'),SKIN_CATEGORY_BY_ACCOUNT:map('SKIN_CATEGORY_BY_ACCOUNT'),
  UIBindingItem:native,UIBindingPlayer:{GetPlayerGuid:()=>owner},
  trace:(...args)=>calls.push(['trace',...args]),
  getDefinitionByName:name=>{assert.equal(name,'ui.datasource.DataSourceConnection');return scope.DataSourceConnection;},
  UIBindingSystem:{DispatchWallOfData:(...args)=>calls.push(['diagnostic',...args]),
    Print:(...args)=>calls.push(['print',...args]),
    GetStringHashValue:(key,fallback)=>{assert.equal(key,'Cranberry.Inventory.Hood');return hoodState||fallback;}},
  InventoryQueryStrings:{getitemUseOptions:()=> 'ACTIONS',getLoadoutItemsQuery:()=> 'LOADOUT'},
  uiDBManager:{query:q=>q==='ACTIONS'?rows:q==='LOADOUT'?loadoutRows():q.includes('FROM PlayerInventory')?inventoryQuery(q):
    q.includes('ItemUseOptionDefinitions')?[{Id:88,Name:'Skin',IconId:151}]:
    owned.has(Number(q.split('== ').at(-1)))?[{ItemCount:10}]:[]},
  DataSourceConnection:{GetInstance:(name,u,c)=>{assert.equal(name,'Items.AvailableAccountSkinItemDataSource');updates.add(u);changes.add(c);return ds;}},
  ActionManager:{getInstance:()=>({doAction:(item,action)=>calls.push(['doAction',item.guid,action.id])})}};
const methods={};
for(const name of ['draw','setItem','handleListItemClick','ownedWeaponCategory','stopSkinChoices','refreshSkinChoices','showActions']) methods[name]=method(source,name);
const hasHood=source.includes('HOODIE_ITEMS');
if(hasHood) {scope.HOODIE_ITEMS=map('HOODIE_ITEMS');methods.hoodieContext=method(source,'hoodieContext');}
if(source.includes('function currentLoadoutRows(')) {
  for(const name of ['currentLoadoutRows','logInventoryContext']) methods[name]=method(source,name);
}
function loadoutRows() {
  const result=[];
  for(const [guid,id] of carried) {
    const slot=carriedSlots.get(guid);
    if(slot) result.push({ItemId:id,ItemGuid:guid,ContainerGuid:'-1',ContainerSlotId:slot});
  }
  if(head) result.push({ItemId:head,ItemGuid:'head',ContainerGuid:'-1',ContainerSlotId:11});
  return result;
}
function inventoryQuery(q) {
  if(q.endsWith('ContainerSlotId == 11')) return head?[{ItemId:head}]:[];
  const guid=q.match(/== '([^']+)'/)[1];
  if(!carried.has(guid)) return [];
  const slot=carriedSlots.get(guid);
  if(q.includes('ContainerSlotId IN') && ![1,2,4].includes(slot)) return [];
  if(q.includes('ContainerSlotId == 10') && slot!==10) return [];
  return [{ItemId:carried.get(guid)}];
}
function fresh(item={itemDefinitionId:2229,containerOwnerGuid:'self',itemIsWeapon:true,guid:'3530822107858468877'}) {
  calls=[];owned=new Set([2599]);dsRows=[];updates.clear();changes.clear();
  carried=new Map();carriedSlots=new Map();head=0;hoodState='';
  if(item) {
    if(!item.guid) item.guid='3530822107858468877';
    if(item.containerOwnerGuid===owner) {carried.set(item.guid,item.itemDefinitionId);carriedSlots.set(item.guid,1);}
  }
  rows=[{Id:7,Name:'Unload'},{Id:4,Name:'Drop'}];
  const menu=Object.assign({m_regularWidth:0,m_skinCategory:0,m_skinMode:false,m_item:null,m_skinSource:null,m_actions:[],
    m_list:{width:160,height:0,validateNow(){}},x:10,y:10,
    parent:{globalToLocal:p=>p},stage:{mouseX:1850,mouseY:1000,stageWidth:1920,stageHeight:1080,addEventListener(){},removeEventListener(){}},
    invalidate(){},dispatchEvent(e){calls.push(['event',e.type]);}},methods);
  menu.setItem(item);menu.draw();return menu;
}
function choose(menu,predicate) {const index=menu.m_list.dataProvider.findIndex(predicate);assert(index>=0);menu.handleListItemClick({index});}
function skin(accountId,name='Skin name',category=2229,isOwned=true) {
  assert.equal(scope.SKIN_CATEGORY_BY_ACCOUNT[accountId],category);
  return {'Item Definition Id':accountId,Name:name,'Icon ID':1,AccountItemIsOwned:isOwned?'1':'0',AccountItemCount:isOwned?10:0};
}
let menu=fresh();assert.deepEqual(menu.m_actions.map(x=>x.id),[7,4,88]);
assert(menu.x+menu.m_list.width<=1920 && menu.y+menu.m_list.height<=1080);
const anotherAk=Number(Object.keys(scope.SKIN_CATEGORY_BY_ACCOUNT).find(id=>Number(id)!==2599 && scope.SKIN_CATEGORY_BY_ACCOUNT[id]===2229));
dsRows=[skin(2599,'AK owned'),skin(4076,'AR wrong category',10),skin(anotherAk,'AK unowned',2229,false),skin(2599,'Duplicate')];
choose(menu,x=>x.id===88);assert(menu.m_skinMode);
assert.deepEqual(menu.m_actions.map(x=>x.accountId),[-1,2599]);
assert.deepEqual(calls.filter(x=>x[0].startsWith('SelectEditor')).slice(-3),[['SelectEditorSkinItemCollectionId',2],['SelectEditorSkinItemSlotType',2],['SelectEditorSkinTargetPrototypeItemId',2229]]);
choose(menu,x=>x.accountId===2599);
assert.deepEqual(calls.filter(x=>x[0]==='SetSkinItemByItemIdWithCachedData'),[['SetSkinItemByItemIdWithCachedData',2599]]);
if(hasHood) {
  const target=calls.findIndex(x=>x[0]==='RequestUseItem');
  assert.deepEqual(calls[target],['RequestUseItem','3530822107858468877',88,owner]);
  assert.equal(calls[target+1][0],'SetSkinItemByItemIdWithCachedData');
}
assert.equal(menu.m_item,null);assert.equal(updates.size,0);assert.equal(changes.size,0);
for(const item of [{itemDefinitionId:2229,containerOwnerGuid:'bag',itemIsWeapon:true},
  {itemDefinitionId:79,containerOwnerGuid:'self',itemIsWeapon:false},{itemDefinitionId:85,containerOwnerGuid:'self',itemIsWeapon:true}]) {
  menu=fresh(item);assert.equal(menu.m_skinCategory,0);assert(!menu.m_actions.some(x=>x.id===88));
}
menu=fresh({itemDefinitionId:2600,containerOwnerGuid:'self',itemIsWeapon:true});assert.equal(menu.m_skinCategory,2229);
rows.push({Id:88,Name:'Skin'});menu.draw();assert.equal(menu.m_actions.filter(x=>x.id===88).length,1);
choose(menu,x=>x.id===88);assert.equal(menu.m_actions[1].name,'No skins owned');
dsRows=[skin(2599)];menu.refreshSkinChoices();assert.equal(menu.m_actions.length,2);
owned.clear();choose(menu,x=>x.accountId===2599);assert(!calls.some(x=>x[0]==='SetSkinItemByItemIdWithCachedData'));
choose(menu,x=>x.accountId===-1);menu.draw();assert(!menu.m_skinMode);assert.equal(updates.size,0);assert.equal(menu.m_list.width,160);
choose(menu,x=>x.id===4);assert(calls.some(x=>x[0]==='doAction'&&x[2]===4));
for(const [id,category] of Object.entries(scope.SKIN_CATEGORY_BY_ITEM)) {
  menu=fresh({itemDefinitionId:Number(id),containerOwnerGuid:'self',itemIsWeapon:true});
  assert.equal(menu.m_skinCategory,category);assert(menu.m_actions.some(x=>x.id===88));
}
if(hasHood) {
  for(const slot of [0,5,7,40,41]) {
    menu=fresh();carriedSlots.set(menu.m_item.guid,slot);rows.push({Id:88,Name:'Skin'});menu.draw();
    assert.equal(menu.ownedWeaponCategory(menu.m_item),0);assert(!menu.m_actions.some(x=>x.id===88));
  }
  menu=fresh();dsRows=[skin(2599)];choose(menu,x=>x.id===88);
  carriedSlots.set(menu.m_item.guid,0);choose(menu,x=>x.accountId===2599);
  assert(!calls.some(x=>x[0]==='RequestUseItem'||x[0]==='SetSkinItemByItemIdWithCachedData'));
  for(const hoodie of Object.keys(scope.HOODIE_ITEMS)) {
    menu=fresh({itemDefinitionId:Number(hoodie),containerOwnerGuid:owner,guid:'123'});
    carriedSlots.set('123',10);rows.push({Id:96},{Id:97});menu.draw();
    assert.deepEqual(menu.m_actions.filter(x=>x.id===96||x.id===97).map(x=>[x.id,x.name]),[[96,'Hood up']]);
    const item=menu.m_item;
    choose(menu,x=>x.id===96);assert(calls.some(x=>x[0]==='doAction'&&x[2]===96));
    // No local optimistic flip: only the server's accepted state changes the action.
    menu.setItem(item);menu.draw();assert(menu.m_actions.some(x=>x.id===96));
    hoodState='123:1';menu.draw();
    assert.deepEqual(menu.m_actions.filter(x=>x.id===96||x.id===97).map(x=>[x.id,x.name]),[[97,'Hood down']]);
    choose(menu,x=>x.id===97);assert(calls.some(x=>x[0]==='doAction'&&x[2]===97));
    hoodState='123:0';menu.setItem(item);menu.draw();assert(menu.m_actions.some(x=>x.id===96));
    hoodState='999:1';menu.draw();assert(menu.m_actions.some(x=>x.id===96)); // another hoodie/session
  }
  for(const hat of [2158,2170]) {
    menu=fresh({itemDefinitionId:3405,containerOwnerGuid:owner,guid:'123'});
    carriedSlots.set('123',10);menu.draw();head=hat;
    choose(menu,x=>x.id===96);assert(!calls.some(x=>x[0]==='doAction'&&x[2]===96));
    menu.setItem({itemDefinitionId:3405,containerOwnerGuid:owner,guid:'123'});menu.draw();
    assert(!menu.m_actions.some(x=>x.id===96||x.id===97));
    head=0;menu.draw();assert(menu.m_actions.some(x=>x.id===96));
    carriedSlots.set('123',0);menu.draw();assert(!menu.m_actions.some(x=>x.id===96||x.id===97));
  }
  console.log(`PASS: weapon-slot-only Skin, native target before selection, stale slot refusal, ${Object.keys(scope.HOODIE_ITEMS).length} hoodie definitions, authoritative opposite-action labels, hat/helmet blocking and removal.`);
}
// Real InventoryWindow right-click path: loadout slot -> LoadoutManager -> InventoryItemData
// built from the native SQL row. Native ItemType is the factory enum, not the CSV ITEM_TYPE id.
// PID38164 readback confirmed20 for guns (CSV26),44 for account skins (CSV54).
const exportRoot=path.resolve(__dirname,'../../out/inventory-skin-20260906/full-after/scripts');
const dtoText=fs.readFileSync(path.join(exportRoot,'ui/dto/InventoryItemData.as'),'utf8');
const windowText=fs.readFileSync(path.join(exportRoot,'views/inventory/InventoryWindow.as'),'utf8');
const loadoutText=fs.readFileSync(path.join(exportRoot,'views/inventory/LoadoutManager.as'),'utf8');
class InventoryItemData {
  constructor(row) {
    for(const [,field,,value] of dtoText.matchAll(/private var (\w+):(\w+) = ([^;]+);/g)) this[field]=JSON.parse(value);
    method(dtoText,'InventoryItemData').call(this,row);
  }
}
for(const [,name] of dtoText.matchAll(/function get (\w+)\(/g))
  Object.defineProperty(InventoryItemData.prototype,name,{get:method(dtoText,'get '+name)});
class LoadoutListData {constructor(row) {this.slotId=row.SlotId;this.loadoutId=row.LoadoutId;}}
scope.InventoryItemData=InventoryItemData;scope.LoadoutListData=LoadoutListData;
scope.MouseEventEx={RIGHT_BUTTON:1};scope.TileList=class{};
const actualPress=method(windowText,'onListItemPress');
const actualSlotLookup=method(loadoutText,'getLoadoutItemBySlot');
for(const [id,category] of [[2425,10],[2229,2229],[1374,1374],[2662,2229],[2664,1374]]) {
  const dto=new InventoryItemData({ItemId:id,ItemGuid:'3530822107858468877',ContainerGuid:'-1',
    ContainerOwnerGuid:'self',ContainerSlotId:1,IsWeapon:'1',ItemCount:1});
  assert(dto.itemIsWeapon);assert.equal(dto.itemDefinitionId,id);
  menu=fresh(dto);
  const list={dataProvider:new DataProvider([{SlotId:1,LoadoutId:17}])};
  const view={x:0,m_itemActionMenu:menu,m_equippedList:list,m_vehiclePanel:{list2:{}},
    m_loadoutManager:{m_vehicleloadoutId:21,m_currentLoadoutItemMapBySlotId:{1:dto},getLoadoutItemBySlot:actualSlotLookup}};
  menu.setAdjustX=()=>{};
  actualPress.call(view,{target:list,index:0,buttonIdx:1});menu.draw();
  assert.equal(menu.m_skinCategory,category);assert(menu.m_actions.some(x=>x.id===88&&x.name==='Skin'));
}
// Eligibility follows the current player inventory, including DTO copies without owner/weapon
// metadata. An item removed since the popup opened must never remain eligible.
menu=fresh(); menu.m_item.itemIsWeapon=false;menu.m_item.containerOwnerGuid='0';
assert.equal(menu.ownedWeaponCategory(menu.m_item),2229);
carried.clear();assert.equal(menu.ownedWeaponCategory(menu.m_item),0);
assert.equal(menu.ownedWeaponCategory({guid:"1' OR 1=1"}),0);
// Negative control for the installed v1: the real carried AR2425 had no category at all.
const previous=fs.readFileSync(path.join(exportRoot,'ui/controls/ItemActionMenu.as'),'utf8');
const oldMap=scope.SKIN_CATEGORY_BY_ITEM;scope.SKIN_CATEGORY_BY_ITEM=JSON.parse(previous.match(/SKIN_CATEGORY_BY_ITEM:Object = (\{[\s\S]*?\});/)[1]);
assert.equal(method(previous,'ownedWeaponCategory').call({}, {itemDefinitionId:2425,itemIsWeapon:true,containerOwnerGuid:owner}),0);
scope.SKIN_CATEGORY_BY_ITEM=oldMap;
// The original menu, supplied the same native filtered actions, has no Skin row.
if(process.argv[3]) {menu=fresh();method(fs.readFileSync(process.argv[3],'utf8'),'draw').call(menu);assert(!menu.m_actions.some(x=>x.id===88));}
console.log(`PASS: actual InventoryWindow/LoadoutManager/InventoryItemData right-click path; player-inventory membership, owned/category filtering, cached selection, stale ownership, back/cleanup, ordinary actions; ${Object.keys(scope.SKIN_CATEGORY_BY_ITEM).length} weapon identities. Installed-v1 AR2425 negative control confirmed.`);
