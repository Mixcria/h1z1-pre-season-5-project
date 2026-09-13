// Execute the re-exported ActionScript handlers with mocked native game bindings.
// This checks behavior after FFDec compilation, including invitation closures and GUID strings.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const scripts = process.argv[2];
const lobby = fs.readFileSync(path.join(scripts, 'views/lobbyfriends/UILobbyFriendsManager.as'), 'utf8');
const menu = fs.readFileSync(path.join(scripts, 'views/mainmenu/UiMainMenuManager.as'), 'utf8');
const consoleSource = fs.readFileSync(path.join(scripts, 'views/console/UIConsoleManager.as'), 'utf8');

function method(source, name) {
  const at = source.indexOf('function ' + name + '(');
  assert(at >= 0, name);
  const start = source.indexOf('{', at);
  let depth = 1, end = start + 1;
  while (depth) { if (source[end] === '{') depth++; if (source[end] === '}') depth--; end++; }
  return source.slice(at, end)
    .replace(/:Vector\.<Object>/g, '')
    .replace(/:\s*(?:String|Boolean|Number|int|uint|void|Array|Object|\*|TimerEvent|GameEvent|uiDBEvent|ListEvent|ButtonEvent|Button|CoreList|ListItemRenderer|FriendData|LobbyData|IUIDataBinding)\b/g, '')
    .replace(/:\*/g, '')
    .replace(/ as (?:Boolean|Button|CoreList|ListItemRenderer)/g, '')
    .replace(/for each\(((?:var )?\w+) in (.+)\)/g, 'for($1 of $2)');
}

function client() {
  const calls = [], dialogs = [], values = {};
  const context = vm.createContext({
    calls, dialogs, values, int: Math.trunc, uint: n => n >>> 0,
    UIBindingSystem: { DispatchWallOfData: (window, action) => calls.push([window, action]), InInvitational: () => false },
    UIBindingChat: { MakeHtmlSafe: text => text.replaceAll('<', '&lt;').replaceAll('>', '&gt;') },
    UIBindingLocale: { translateCodeString: text => text },
    UIBindingSound: {UI_INVITE_NOTIFICATION: 3783808453, PlayUiSound: sound => calls.push(['inviteSound', sound])},
    UIConsoleManager: {OverlayCloseForInvite: () => calls.push(['closeOverlayForInvite'])},
    MainController: { showSystemMessage: data => dialogs.push(data),
      getWidgetManager: () => ({isWidgetLoaded: () => false, isWidgetLoading: () => false}) },
    WidgetNames: {CONFIRMATION_DIALOG_WINDOW: 99},
    ListEvent: {INDEX_CHANGE:'index', ITEM_CLICK:'click'},
    DataProvider: function(rows = []) { return rows; },
    GameEvent: Object.assign(function(type) { this.type = type; }, {EVENT_PLAY_NOTIFICATION2: 'notification', EVENT_ON_STEAM_LOBBY_START_GAME: 'groupStart'}),
    MainMenuEvent: Object.assign(function(type) { this.type = type; }, {CANCEL_QUEUE: 'cancel', SHOW_GROUP_ELEMENTS: 'show'}),
    FriendData: function(row) { this.userId = row.UserId; this.name = row.Name; },
    LobbyData: function(row) { this.userId = row.UserId; this.name = row.Name; },
    TimerEvent: { TIMER: 'timer' },
  });
  vm.runInContext(`
    Array.DESCENDING = 2; Array.NUMERIC = 16; Array.CASEINSENSITIVE = 1;
    Array.prototype.sortOn = function() { return this.sort((a,b) => Number(b.InGame)-Number(a.InGame) || a.Name.localeCompare(b.Name)); };
    var m_localSelf = "0", m_localLeader = "0", m_localMembers = [], m_localFriends = [], m_localInvite = "", m_localQueue = "", m_localAlertedInvite = "";
    var LOBBY_MAX = 5, INVITE_TO_LOBBY = "InviteToLobby", LEAVE_LOBBY = "LeaveLobby";
    var BINDING_LOBBY_MEMBER_NAME = "name", BINDING_LOBBY_DATA = "members", BINDING_LOBBY_MINI_DATA = "mini",
        BINDING_FRIENDS_DATA = "friends", BINDING_RIGHTCLICK_DATA = "context", BINDING_RIGHTCLICK_POSITION = "position",
        BINDING_RIGHTCLICK_VISIBLE = "contextVisible";
    var m_local = {
      m_isInitalized: true, m_isConsole: false, m_bindings: new Proxy({length: 18}, {get: (target, key) => key === "length" ? 18 : ({setValue: value => values[key] = value})}),
      m_friendsButton: {count:{}}, m_friendsList: {focusable:true}, m_groupList: {focusable:true}, m_rightClickList: {focusable:true},
      m_stage: {dispatchEvent: event => calls.push([event.type, event.data])},
      focusFriends() { calls.push(["focusFriends"]); }, focusRightClick() {},
      showLeaveLobbyConfirmation() { calls.push(["confirmLeave"]); }
    };
  `, context);
  const names = ['LocalActive','LocalLeader','LocalCanQueue','LocalSend','LocalNotice','LocalReceive','pollLocalSocial',
    'handleLobbyDataChange','handleFriendsDataChange','handleLobbyMenuIndexChange','handleFriendsMenuItemPress',
    'handleRightClickMenuIndexChange','leaveLobby','getRightClickData'];
  if(lobby.includes('function LocalShowInvite(')) names.push('LocalShowInvite','processRightClickMenu');
  for (const name of names) {
    vm.runInContext(method(lobby, name), context, {filename:name});
    vm.runInContext(`m_local.${name} = ${name};`, context);
  }
  context.UILobbyFriendsManager = context;
  // Fixtures omit the queue context for menu snapshots.
  return {context, calls, dialogs, values, receive: text => context.LocalReceive(text.replace(/^(.*?;S\|[^;]+\|Menu\|0);/, '$1|0|0;'))};
}

const prefix = '@cranberry/social/1;';
const largeGuid = '18446744073709551610';
const a = client(), b = client();
assert.equal(a.receive('Normal console text'), false);
a.receive(prefix + 'S|1|1|Menu|0;M|1|Alice|AliceChar|Menu;F|0|OfflineFriend||Offline|0;F|' + largeGuid + '|Bob|B%7Cob%3B%C3%A9|Menu|1');
b.receive(prefix + 'S|' + largeGuid + '|' + largeGuid + '|Menu|0;M|' + largeGuid + '|Bob|BobChar|Menu;F|1|Alice|AliceChar|Menu|1');
assert.equal(a.values.friends[0].UserId, largeGuid);
assert.equal(a.values.friends[0].CharacterName, 'B|ob;é');
assert.equal(a.values.members.length, 5);
assert.equal(a.values.members[0].Name, 'Alice');
assert.equal(a.values.members[1].UserId, '0');
assert.equal(a.context.LocalLeader(), true);
const click = { index: 0, itemData: a.values.friends[0], itemRenderer:{y:20}, target:{y:10} };
a.context.handleFriendsMenuItemPress.call(a.context.m_local, click);
assert.equal(a.values.context[0].enabled, true);
a.context.handleRightClickMenuIndexChange.call(a.context.m_local, {index:0, itemData:a.values.context[0]});
assert(a.calls.some(c => c[1] === 'invite ' + largeGuid));
const invitation = prefix + 'S|' + largeGuid + '|' + largeGuid + '|Menu|0;M|' + largeGuid + '|Bob|BobChar|Menu;I|42|%3CAlice%3E';
b.receive(invitation); b.receive(invitation);
assert.equal(b.dialogs.length, 1);
if(lobby.includes('m_localAlertedInvite')) {
  assert.match(lobby,/private static var m_localAlertedInvite:String = "";/, 'sound deduplication state must be declared in the compiled class');
  assert.equal(b.calls.filter(c => c[0] === 'inviteSound').length, 1, 'one sound for duplicate delivery');
  assert.equal(b.calls.filter(c => c[0] === 'closeOverlayForInvite').length, 1, 'release overlay input before invitation');
  assert(b.calls.some(c => c[0] === 'notification' && c[1][1].includes('Duos or Fives')));
}
assert(b.dialogs[0].message.startsWith('&lt;Alice&gt;'));
b.dialogs[0].callback(true);
assert(b.calls.some(c => c[1] === 'accept 42'));
b.receive(invitation.replace('I|42|', 'I|43|'));
const beforeStale = b.calls.length;
b.dialogs[0].callback(true);
assert.equal(b.calls.length, beforeStale);
b.dialogs[1].callback(false);
assert(b.calls.some(c => c[1] === 'decline 43'));
b.receive(prefix + 'S|' + largeGuid + '|1|Menu|0;M|1|Alice|AliceChar|Menu;M|' + largeGuid + '|Bob|BobChar|Menu');
assert.equal(b.values.members[0].UserId, largeGuid); // The local player's slot remains first.
assert.equal(b.values.members[1].IsOwner, true);
assert.equal(b.context.LocalLeader(), false);
assert.equal(b.context.LocalCanQueue(5), false);
assert.equal(b.values.friends.length, 0); // Removed friends disappear, including the last one.
const staleCount = b.calls.length; b.dialogs[1].callback(true); assert.equal(b.calls.length, staleCount);
b.context.leaveLobby.call(b.context.m_local);
assert(b.calls.some(c => c[0] === 'cancel'));
assert(b.calls.some(c => c[1] === 'leave'));
a.context.handleFriendsMenuItemPress.call(a.context.m_local, {...click, itemData:a.values.friends[1]});
assert.equal(a.values.context[0].enabled, false);
const beforeDisabled = a.calls.length;
a.context.handleRightClickMenuIndexChange.call(a.context.m_local, {index:0, itemData:a.values.context[0]});
assert.equal(a.calls.length, beforeDisabled);
a.receive(prefix + 'N|Invitation%20sent.');
assert(a.calls.some(c => c[0] === 'notification' && c[1][1] === 'Invitation sent.'));

// Run the actual rebuilt menu action. The group leader uses native game transfer;
// a follower or an undersized mode must never open a stalled queue screen.
for (const player of [a,b]) {
  const ctx = player.context;
  ctx.SharedGlobalData = {GetInstance: () => ({})};
  ctx.UIBindingLobby = {IsInLobby: () => true, IsLobbyOwner: () => false,
    AutoFillTeam: () => { throw Error('unexpected Steam operation'); }, SendStartGameMessage: () => { throw Error('unexpected Steam operation'); }};
  ctx.UIBindingCharacterCreate = {TransferCharacter: (guid, world) => player.calls.push(['transfer',guid,world])};
  vm.runInContext(method(menu, 'requestEnterGameQueue'), ctx);
  vm.runInContext(method(menu, 'CallGroupTransfer'), ctx);
  const timer = {addEventListener(){}, start(){}};
  const widget = {m_isAdminClient:false, m_lobbyType:2, m_currentlySelectedServer:6, m_playerGuid:ctx.m_localSelf,
    m_SearchTimer:timer, m_SearchMinTimer:timer, isInQueue:()=>false,
    OpenTransferScreen: () => player.calls.push(['queueScreen']), CallGroupTransfer: ctx.CallGroupTransfer};
  ctx.requestEnterGameQueue.call(widget, 2);
}
assert(a.calls.some(c => c[0] === 'transfer' && c[2] === 6));
assert(!b.calls.some(c => ['transfer','queueScreen'].includes(c[0])));

const shared = {};
b.context.SharedGlobalData = {GetInstance: () => shared};
const queuedSnapshot = prefix + 'S|' + largeGuid + '|1|Queued|2|6|1;M|1|Alice|AliceChar|Queued;M|' + largeGuid + '|Bob|BobChar|Queued';
b.receive(queuedSnapshot);
assert.equal(shared.isTeam2, true);
assert.equal(shared.isInGroupGame, true);
const groupStart = b.calls.find(c => c[0] === 'groupStart')[1];
b.receive(queuedSnapshot);
assert.equal(b.calls.filter(c => c[0] === 'groupStart').length, 1);
vm.runInContext(method(menu, 'handleSteamLobbyStartGame'), b.context);
const followerMenu = {m_playerGuid:largeGuid, setCurrentlySelectedServer(id) {this.m_currentlySelectedServer=id;}};
b.context.handleSteamLobbyStartGame.call(followerMenu, {data:groupStart});
assert(b.calls.some(c => c[0] === 'transfer' && c[1] === largeGuid && c[2] === 6));
assert.equal(followerMenu.m_lobbyType, 2);

// A console intercept must consume bridge data before the ordinary history/log paths.
assert(consoleSource.indexOf('UILobbyFriendsManager.LocalReceive(_loc2_)') < consoleSource.indexOf('if(this.m_isLogging)', consoleSource.indexOf('function handlePrintConsole')));

if(lobby.includes('function LocalShowInvite(')) {
  const waiting = client();
  let busy = true;
  waiting.context.MainController.getWidgetManager = () => ({isWidgetLoaded:()=>busy,isWidgetLoading:()=>false});
  waiting.receive(invitation);
  assert.equal(waiting.dialogs.length,0);
  assert(!waiting.calls.some(c => c[1] === 'seen 42'));
  busy = false;
  waiting.receive(invitation);
  assert.equal(waiting.dialogs.length,1);
  assert(waiting.calls.some(c => c[1] === 'seen 42'));
  waiting.receive(invitation);
  assert.equal(waiting.dialogs.length,1);
  waiting.dialogs[0].callback(true);
  assert(waiting.calls.some(c => c[1] === 'accept 42'));

  const retry = client();
  retry.context.MainController.showSystemMessage = () => { throw Error('modal temporarily unavailable'); };
  assert.throws(() => retry.receive(invitation),/temporarily unavailable/);
  assert(!retry.calls.some(c => c[1] === 'seen 42'));
  retry.context.MainController.showSystemMessage = data => retry.dialogs.push(data);
  retry.receive(invitation);
  assert.equal(retry.dialogs.length,1);

  const renderer = client();
  renderer.context.m_local.handleLobbyDataChange = () => { throw Error('list refresh failed'); };
  assert.throws(() => renderer.receive(invitation),/list refresh failed/);
  assert.equal(renderer.dialogs.length,1);
  renderer.dialogs[0].callback(true);
  assert(renderer.calls.some(c => c[1] === 'accept 42'));

  // Exercise registered events, including two clicks on the same selected row.
  // The old tests called the action directly and missed the mouse activation seam.
  for (const isConsole of [false,true]) {
    const clicked = client(), listeners = new Map();
    const list = {focusable:true, addEventListener:(event,fn)=>listeners.set(event,fn),
      removeEventListener:event=>listeners.delete(event)};
    const widget = clicked.context.m_local;
    widget.m_isConsole = isConsole;
    clicked.context.processRightClickMenu.call(widget,{},list);
    assert.equal(listeners.has('index'),false);
    assert.equal(listeners.has('click'),true);
    list.focusable = true;
    const event = {index:0,itemData:{enabled:true,type:'InviteToLobby',data:{userId:largeGuid}}};
    listeners.get('click').call(widget,event);
    listeners.get('click').call(widget,event);
    assert.equal(clicked.calls.filter(c=>c[1] === 'invite '+largeGuid).length,2);
    clicked.context.processRightClickMenu.call(widget,null,list);
    assert.equal(listeners.size,0);
  }
  console.log('PASS: actual click subscriptions, same-row activation, busy-modal retries, receipt acknowledgement and invitation delivery before list rendering.');
}
console.log('PASS: rebuilt friends list, 64-bit identities, invite/accept/decline, stale prompts, empty list, leave, notices, leader queue and follower rejection.');
