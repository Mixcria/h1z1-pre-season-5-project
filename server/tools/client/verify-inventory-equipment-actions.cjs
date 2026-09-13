// Exercise the re-exported menu against SQLite and the stock loadout query. The main-menu
// editor and its filtered datasource are deliberately absent. Live rendering is a playtest.
const fs=require('node:fs'), assert=require('node:assert/strict'), {DatabaseSync}=require('node:sqlite');
const source=fs.readFileSync(process.argv[2],'utf8'), stock=process.argv[3];
function extract(text,name) {
  const at=text.indexOf(`function ${name}(`); assert(at>=0,name);
  const start=text.indexOf('{',at); let end=start+1, depth=1;
  while(depth) { depth+=(text[end]==='{')-(text[end]==='}'); end++; }
  const params=text.slice(text.indexOf('(',at)+1,text.indexOf(')',at)).replace(/:\w+/g,'');
  const body=text.slice(start+1,end-1).replace(/\bvar (\w+):[\w.]+/g,'var $1')
    .replace(/for each\(var (\w+) in (\w+)\)/g,'for(var $1 of $2)')
    .replace(/\bas (?:ItemActionData|int|String|Number)\b/g,'').replace(/super(?:\.draw)?\(\);/g,'')
    .replace(/(?<![\w.])(stage|parent|invalidate|dispatchEvent)\b/g,'this.$1')
    .replace(/choices.sortOn\("name",Array.CASEINSENSITIVE\);/g,'choices.sort((a,b)=>a.name.localeCompare(b.name));');
  return new Function(...Object.keys(scope),`return function(${params}) {${body}}`)(...Object.values(scope));
}
function map(name) {return JSON.parse(source.match(new RegExp(name+':Object = (\\{[\\s\\S]*?\\});'))[1]);}
class DataProvider extends Array {constructor(items){super(...items);}requestItemAt(i){return this[i];}}
class Point {constructor(x,y){Object.assign(this,{x,y});}}
class Event {constructor(type){this.type=type;}} Event.CLOSE='close';
const db=new DatabaseSync(':memory:');
db.exec(`CREATE TABLE PlayerInventory(ItemId INTEGER,ItemGuid INTEGER,ContainerGuid INTEGER,ContainerSlotId INTEGER,ItemCount INTEGER,MaxDurability INTEGER,CurrentDurability INTEGER);
CREATE TABLE AccountInventory(ItemId INTEGER,ItemCount INTEGER);
CREATE TABLE ItemDefinitions(ItemId INTEGER PRIMARY KEY,ItemName TEXT,ItemImageSetId INTEGER,ItemDescription TEXT,ItemBulk INTEGER,IsWeapon TEXT,IsArmor TEXT,IsRecipeComponent TEXT);
CREATE TABLE PlayerInfo(Guid INTEGER); INSERT INTO PlayerInfo VALUES(4129);
CREATE TABLE LoadoutSlotWeapons(ItemGuid INTEGER,ShouldShowAmmo TEXT,CurrentAmmo INTEGER,ReserveAmmo INTEGER);`);
let sent=[],actions=[{Id:4,Name:'Drop',IconId:0},{Id:88,Name:'Skin',IconId:151}],hoodState='',skinTargets='';
const scope={int:v=>Number(v)|0,Number,String,Boolean,Math,Array,DataProvider,Point,Event,MouseEvent:{MOUSE_DOWN:'down'},
  SKIN_CATEGORY_BY_ITEM:map('SKIN_CATEGORY_BY_ITEM'),
  SHREDDABLE_FOOTWEAR:source.includes('SHREDDABLE_FOOTWEAR')?map('SHREDDABLE_FOOTWEAR'):{},
  APPAREL_SLOT_BY_CATEGORY:map('APPAREL_SLOT_BY_CATEGORY'),HOODIE_ITEMS:map('HOODIE_ITEMS'),
  UIBindingItem:{ItemUseOptionDataSourceUpdate(){},
    RequestUseItem:(...args)=>sent.push(['RequestUseItem',...args]),
    RequestDropItem:(...args)=>sent.push(['RequestDropItem',...args])},
  UIBindingSystem:{Print(){},DispatchWallOfData:(window,action)=>sent.push([window,action]),
    GetStringHashValue:key=>key==='Cranberry.Inventory.Hood'?hoodState:key==='Cranberry.Inventory.SkinTargets'?skinTargets:''},
  uiDBManager:{query:sql=>{assert(!sql.includes('AccountInventory'));return sql==='ACTIONS'?actions:db.prepare(sql).all();}},
  InventoryQueryStrings:{getitemUseOptions:()=> 'ACTIONS'},
  ItemActions:{ACTION_TYPE_DROP:4},
  ActionManager:{PROPERTY_ID_QUANTITY:1}};
const actionManagerSource=fs.readFileSync(stock+'/ui/managers/ActionManager.as','utf8');
const actionManager={m_shiftKeyDown:false};
scope.ActionManager.getInstance=()=>actionManager;
for(const name of ['doAction','requestUseItem']) actionManager[name]=extract(actionManagerSource,name);
const dto=fs.readFileSync(stock+'/ui/dto/ItemActionData.as','utf8');
class ItemActionData {constructor(row){extract(dto,'ItemActionData').call(this,row);}}
for(const [,name] of dto.matchAll(/function get (\w+)\(/g))
  Object.defineProperty(ItemActionData.prototype,name,{get:extract(dto,'get '+name)});
scope.ItemActionData=ItemActionData;
scope.InventoryQueryStrings.getLoadoutItemsQuery=extract(fs.readFileSync(stock+'/views/inventory/InventoryQueryStrings.as','utf8'),'getLoadoutItemsQuery');
const methods={};
for(const name of ['setItem','draw','equippedSkinCategory','currentLoadoutRows','hoodieContext','showActions','handleListItemClick','logInventoryContext']) methods[name]=extract(source,name);
if(source.includes('addFootwearShred')) methods.addFootwearShred=extract(source,'addFootwearShred');
function definition(id,name='item '+id){db.prepare('INSERT OR IGNORE INTO ItemDefinitions VALUES(?,?,1,\'\',0,\'0\',\'0\',\'0\')').run(id,name);}
function fresh(id=2425,slot=2,eligible=true) {
  db.exec('DELETE FROM PlayerInventory; DELETE FROM AccountInventory');sent=[];hoodState='';definition(id);
  const guid='3530822107858468897';
  skinTargets=eligible?guid:'';
  db.prepare('INSERT INTO PlayerInventory VALUES(?,?,?,?,1,950,417)').run(id,BigInt(guid),-1,slot);
  const item={itemDefinitionId:id,guid,containerOwnerGuid:'4129',containerGuid:'-1',containerSlotId:slot,
    stackCount:1,pseudoStackCount:1};
  const menu=Object.assign({m_item:null,m_skinCategory:0,m_regularWidth:0,
    m_list:{width:160,height:0,validateNow(){}},x:10,y:10,
    stage:{mouseX:900,mouseY:650,stageWidth:1920,stageHeight:1080,addEventListener(){},removeEventListener(){}},
    parent:{globalToLocal:p=>p},invalidate(){},dispatchEvent(){}},methods);
  menu.setItem(item);menu.draw();return menu;
}
function choose(menu,id){const i=menu.m_actions.findIndex(a=>a.id===id);assert(i>=0,`Missing choice ${id}`);menu.handleListItemClick({index:i});}
assert(!source.includes('AccountInventory')&&!source.includes('AvailableAccountSkinItemDataSource'));
let menu=fresh();
assert.equal(db.prepare('SELECT COUNT(*) AS n FROM AccountInventory').get().n,0);
choose(menu,88);
assert.deepEqual(sent.at(-1),['Cranberry.Inventory','skin:3530822107858468897:selected']);
assert.equal(menu.m_item,null);
menu=fresh(4073,2,false);assert(!menu.m_actions.some(a=>a.id===88),'already personal skin');
choose(menu,4);assert.deepEqual(sent.at(-1),['RequestUseItem','3530822107858468897',4,'4129']);
menu=fresh();skinTargets='13530822107858468897;35308221078584688970';menu.draw();
assert(!menu.m_actions.some(a=>a.id===88),'GUID substrings must not match');
skinTargets='1;3530822107858468897;2';menu.draw();assert(menu.m_actions.some(a=>a.id===88));
skinTargets='';const before=sent.length;choose(menu,88);assert.equal(sent.length,before,'stale eligibility');
for(const [id,category] of Object.entries(scope.SKIN_CATEGORY_BY_ITEM)) {
  const slot=scope.APPAREL_SLOT_BY_CATEGORY[category]||1;menu=fresh(Number(id),slot);
  assert.equal(menu.equippedSkinCategory(menu.m_item),category,`equipped ${id}`);
  assert(menu.m_actions.some(a=>a.id===88));
  skinTargets='';menu.draw();assert(!menu.m_actions.some(a=>a.id===88),`personal ${id}`);
  skinTargets=menu.m_item.guid;
  db.exec('UPDATE PlayerInventory SET ContainerGuid=123,ContainerSlotId=1');menu.draw();
  assert(menu.m_actions.some(a=>a.id===88),`server-approved carried ${id}`);
  skinTargets="";menu.draw();assert(!menu.m_actions.some(a=>a.id===88),`server-refused carried ${id}`);
}
for(const [category,slot] of Object.entries(scope.APPAREL_SLOT_BY_CATEGORY)) {
  const item=Number(Object.keys(scope.SKIN_CATEGORY_BY_ITEM).find(id=>scope.SKIN_CATEGORY_BY_ITEM[id]===Number(category)));
  menu=fresh(item,slot);choose(menu,88);
  assert.deepEqual(sent.at(-1),['Cranberry.Inventory','skin:3530822107858468897:selected']);
}
for(const slot of [0,5,7,10,11,40,41]) {menu=fresh(2425,slot,false);assert(!menu.m_actions.some(a=>a.id===88));}
menu=fresh();db.exec('DELETE FROM PlayerInventory');skinTargets='';choose(menu,88);
assert(!sent.some(a=>a[0]==='Cranberry.Inventory'));
for(const hoodie of Object.keys(scope.HOODIE_ITEMS)) {
  menu=fresh(Number(hoodie),10);const item=menu.m_item;
  assert(menu.m_actions.some(a=>a.id===96&&a.name==='Hood up'));choose(menu,96);
  assert.deepEqual(sent.at(-1),['Cranberry.Inventory',`hood:${item.guid}:1`]);
  menu.setItem(item);menu.draw();assert(menu.m_actions.some(a=>a.id===96));
  hoodState=item.guid+':1';menu.draw();assert(menu.m_actions.some(a=>a.id===97&&a.name==='Hood down'));
  choose(menu,97);assert.deepEqual(sent.at(-1),['Cranberry.Inventory',`hood:${item.guid}:0`]);
  menu.setItem(item);menu.draw();definition(2170);
  db.prepare('INSERT INTO PlayerInventory VALUES(2170,999,-1,11,1,100,100)').run();
  const before=sent.length;choose(menu,97);assert.equal(sent.length,before);
  menu.setItem(item);menu.draw();assert(!menu.m_actions.some(a=>a.id===96||a.id===97));
}
for(const item of Object.keys(scope.SHREDDABLE_FOOTWEAR)) {
  menu=fresh(Number(item),13,false);
  assert.equal(menu.m_actions.filter(a=>a.id===63).length,1,`footwear ${item}`);
  choose(menu,63);assert.deepEqual(sent.at(-1),['Cranberry.Inventory','shred:3530822107858468897:1']);
  assert.equal(menu.m_item,null);
  for(const owner of ['4129','3530822107858468870']) {
    menu=fresh(Number(item),13,false);db.exec('DELETE FROM PlayerInventory');
    menu.m_item.containerOwnerGuid=owner;menu.draw();choose(menu,63);
    assert.deepEqual(sent.at(-1),['Cranberry.Inventory','shred:3530822107858468897:1'],'backpack/ground/body bag dispatch');
  }
  for(const nativeOption of [6,63]) {
    actions=[{Id:nativeOption,Name:'Shred',IconId:131,RequiresQuantity:1}];menu=fresh(Number(item),13,false);
    assert.equal(menu.m_actions.filter(a=>a.id===6||a.id===63).length,1,'no duplicate shred');
    choose(menu,nativeOption);
    assert.deepEqual(sent.at(-1),['Cranberry.Inventory','shred:3530822107858468897:1']);
  }
  actions=[{Id:4,Name:'Drop',IconId:0},{Id:88,Name:'Skin',IconId:151}];
}
for(const item of [3711,2563,2550]) {
  actions=[{Id:6,Name:'Shred',IconId:131,RequiresQuantity:1}];menu=fresh(item,13,false);
  assert.deepEqual(menu.m_actions.filter(a=>a.id===6||a.id===63).map(a=>a.id),[6],'stealth unchanged');
  choose(menu,6);assert.deepEqual(sent.at(-1),['RequestUseItem','3530822107858468897',6,'4129',1,1]);
}
db.close();
console.log(`PASS: real SQLite + stock loadout query, ${Object.keys(scope.SKIN_CATEGORY_BY_ITEM).length} skin identities, ${Object.keys(scope.APPAREL_SLOT_BY_CATEGORY).length} apparel categories, 50 hoodies, ${Object.keys(scope.SHREDDABLE_FOOTWEAR).length} Fast/Sturdy footwear identities using the inventory WindowEvent, native Stealth quantity dispatch, exact server event strings and stale-item/headwear checks.`);
