// Run the re-exported compiled handlers with native bindings replaced by a small deterministic UI model.
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm'), assert = require('node:assert/strict');
const scripts = process.argv[2];
require('node:child_process').execFileSync(process.execPath,[path.join(__dirname,'verify-ingame-social.cjs'),scripts],{stdio:'inherit'});
const source = fs.readFileSync(path.join(scripts,'views/console/UIConsoleManager.as'),'utf8');
function method(name) {
  const at=source.indexOf('function '+name+'('); assert(at>=0,name);
  const start=source.indexOf('{',at); let end=start+1,depth=1;
  while(depth) { if(source[end]==='{') depth++; if(source[end]==='}') depth--; end++; }
  return source.slice(at,end)
    .replace(/:\s*(String|Boolean|Number|int|uint|void|Array|Object|Function|TimerEvent|KeyboardEvent|MouseEvent|WidgetEvent|Event|DisplayObjectContainer|TextField|Sprite|InteractiveObject|AccessibilityProperties|Date)\b(?!\s*\()/g,'')
    .replace(/ as (Sprite|DisplayObjectContainer|InteractiveObject)/g,'')
    .replace(/m_overlay\.m_overlayPainted\[picture\] = key;/g,'m_overlay.m_overlayPainted.set(picture,key);')
    .replace(/m_overlay\.m_overlayPainted\[picture\]/g,'m_overlay.m_overlayPainted.get(picture)')
    .replace(/for each\(((?:var )?\w+) in (.+)\)/g,'for($1 of $2)');
}
class Sprite {
  constructor(){this.children=[];this.visible=true;this.name='';this.x=this.y=0;this.graphics={count:0,beginFill(){},endFill(){},clear(){},drawRect(){this.count++},drawCircle(){},drawRoundRect(){}};}
  get numChildren(){return this.children.length}
  addChild(c){this.children.push(c);c.parent=this;return c}
  removeChild(c){this.children.splice(this.children.indexOf(c),1);c.parent=null}
  removeChildAt(i){this.removeChild(this.children[i])}
  getChildByName(n){return this.children.find(c=>c.name===n)||null}
  setChildIndex(){} addEventListener(){} removeEventListener(){} dispatchEvent(){return true;}
}
class TextField extends Sprite {
  constructor(){super();this.text='';this.scrollV=1;this.maxScrollV=1}
  set htmlText(value){throw Error('Private message text must not become HTML')}
}
let flags=7,hidden=true,movement=true,keyboard=true,time=10000;
const calls=[], ctx=vm.createContext({Sprite,TextField,DisplayObjectContainer:Sprite,TextFormat:function(){},AccessibilityProperties:function(){},
  Timer:function(){this.addEventListener=this.removeEventListener=this.start=this.stop=()=>{}},
  Keyboard:{ESCAPE:27,ENTER:13,NUMPAD_ENTER:108},KeyboardEvent:{KEY_DOWN:'key'},MouseEvent:{CLICK:'click',MOUSE_WHEEL:'wheel'},Event:Object.assign(class{},{RESIZE:'resize'}),DataEvent:class{},flash:{events:{DataEvent:class{}}},TimerEvent:{TIMER:'timer'},
  TextFieldType:{INPUT:'input'},uint:n=>Number(n)>>>0,int:Math.trunc,getTimer:()=>time,
  UILobbyFriendsManager:{LocalAvatars(){}},UIBindingSystem:{GetMouseHidden:()=>hidden,SetMouseHidden:n=>hidden=n,DispatchWallOfData:(...c)=>calls.push(c)},
  UIBindingInput:{GetInputFlags:()=>flags,SetInputFlags:n=>flags=n,INPUT_MOUSE:8,INPUT_KEYBOARD:16},
  UIBindingKeyboard:{GetMovementEnabled:()=>movement,SetMovementEnabled:n=>movement=n,GetKeyboardEnabled:()=>keyboard,SetKeyboardEnabled:n=>keyboard=n},
});
const names=[...source.matchAll(/function ((?:[Oo]verlay)\w+)\(/g)].map(m=>m[1]);
for(const name of names) vm.runInContext(method(name),ctx,{filename:name});
const stage=Object.assign(new Sprite(),{stageWidth:1920,stageHeight:1080,focus:null});
const ui={m_stage:stage,m_overlayRoot:null,m_overlayJobs:[],m_overlayFriends:[],m_overlayAvatars:{},m_overlayDrafts:{},m_overlayPending:null,
  m_overlayPainted:new Map(),
  m_overlayFriend:'',m_overlaySelf:'',m_overlayWaiting:'',m_overlayToggleId:'',m_overlayHistoryKey:'',m_overlayStateKey:'',m_overlayScroll:0,m_overlayNextPoll:0};
for(const name of names) ui[name]=ctx[name];
ctx.m_overlay=ui;ctx.ui=ui;
assert.equal(ui.overlayReceive('ordinary console output'),false);
const prefix='@cranberry/overlay/1;';
ui.overlayReceive(prefix+'T|one');
assert(ctx.OverlayActive());assert.equal(movement,false);assert.equal(keyboard,false);assert.equal(hidden,false);assert.equal(flags,31);
ui.overlayReceive(prefix+'T|one');assert(ctx.OverlayActive(),'duplicate toggle must not close');
ui.overlayReceive(prefix+'S|alice|Alice|;F|bob|Bobby|Offline||2');
assert.equal(ui.m_overlayFriends.length,1);assert.equal(ui.m_overlayFriends[0].unread,2);
ui.overlaySelect(ui.m_overlayFriends[0]);ui.m_overlayInput.text='Hello | ; <b> 🌲';ui.overlaySendMessage();
const sent=ui.m_overlayJobs.find(c=>c.startsWith('send|'));
if(source.includes('function overlayInviteFriend(')) {
  ui.overlayInviteFriend();
  assert(ui.m_overlayJobs.includes('invite|bob'), 'invite uses the selected account, not a display name');
  ui.overlayReceive(prefix+'P|Invitation%20sent');
  assert.equal(ui.m_overlayStatus.text,'Invitation sent');
}
assert.match(sent,/^send\|bob\|[0-9a-f]{32}\|/);assert.equal(decodeURIComponent(sent.split('|')[3]),ui.m_overlayInput.text);
ui.overlaySendMessage();assert.equal(ui.m_overlayJobs.filter(c=>c.startsWith('send|')).length,1);
ui.overlayReceive(prefix+'D|'+sent.split('|')[2]+'|1');assert.equal(ui.m_overlayInput.text,'');assert.equal(ui.m_overlayPending,null);
ui.overlayReceive(prefix+'H|bob|0;M|9007199254740993|bob|1788888888|'+encodeURIComponent('<img src=x>;| Ω')+'|'+ 'a'.repeat(32));
assert(ui.m_overlayHistory.text.includes('<img src=x>;| Ω'));
assert(ui.m_overlayJobs.includes('read|bob|9007199254740993'),'message ID must remain a string');
ui.m_overlayInput.text='draft';ui.overlayReceive(prefix+'T|two');
assert.equal(ctx.OverlayActive(),false);assert.equal(flags,7);assert.equal(movement,true);assert.equal(keyboard,true);assert.equal(hidden,true);
if(source.includes('function OverlayCloseForInvite(')) {
  ui.overlayToggle(); ctx.OverlayCloseForInvite();
  assert.equal(ctx.OverlayActive(),false); assert.equal(flags,7); assert.equal(movement,true);
}
ui.overlayReceive(prefix+'T|three');assert.equal(ui.m_overlayInput.text,'draft');
ui.overlayKey({keyCode:27,preventDefault(){},stopImmediatePropagation(){}});assert.equal(ctx.OverlayActive(),false);
const host=new Sprite();let pixels='AABBCC'.repeat(48*48);
ui.overlayReceive(prefix+'A|bob|v1|'+pixels);
assert.equal(ctx.OverlayAvatar(host,'bob','v1',48,48),true);assert.equal(host.numChildren,1);
const picture=host.children[0];assert.equal(picture.graphics.count,48,'uniform pixels combine into horizontal runs');
ctx.OverlayAvatar(host,'bob','v1',48,48);assert.equal(host.children[0],picture,'cached avatars must not redraw on every timer');
ctx.OverlayAvatar(host,'bob','v2',48,48);assert(ui.m_overlayJobs.includes('avatar|bob'),'new avatar version requests new pixels');
ui.overlayReceive(prefix+'S|alice|Alice|');assert.equal(ui.m_overlayFriend,'');assert.equal(ui.m_overlayInput.text,'');
const lobby=fs.readFileSync(path.join(scripts,'views/lobbyfriends/UILobbyFriendsManager.as'),'utf8');
assert(lobby.includes('person.AccountId = decodeURIComponent(fields[2])'));
assert(lobby.includes('localAvatarList(m_local.m_groupList)'));assert(lobby.includes('localAvatarList(m_local.m_miniGroupList)'));
console.log('Overlay compiled-handler checks passed: toggle/deduplication, input restore, chat, escaping, retry, drafts, avatar cache and lobby binding.');
