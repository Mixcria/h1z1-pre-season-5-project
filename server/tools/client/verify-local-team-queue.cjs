// Run the rebuilt/re-exported requestEnterGameQueue body with mock native bindings.
const fs = require('node:fs');
const assert = require('node:assert/strict');
const source = fs.readFileSync(process.argv[2], 'utf8');
function extract(name) {
  const at = source.indexOf('function ' + name + '(');
  assert(at >= 0, name + ' must exist');
  const start = source.indexOf('{', at);
  let depth = 1, end = start + 1;
  while (depth) {
    if (source[end] === '{') depth++;
    if (source[end] === '}') depth--;
    end++;
  }
  return source.slice(start + 1, end - 1)
    .replace(/var (_loc\d+_):(String|Boolean|Button|Vector\.<Object>)/g, 'var $1')
    .replace(/for each\((\w+) in (\w+)\)/g, 'for($1 of $2)')
    .replace(/ as Boolean/g, '');
}
const requestBody = extract('requestEnterGameQueue');
const groupBody = extract('CallGroupTransfer');
const afterQueueBody = extract('transferAfterQueue');
const minTimerBody = extract('handleTransferMinTimerEvent');
const roleBody = extract('TransferCharacterAsRole');
const cancelBody = extract('cancelQueue');
const cancelEventBody = extract('handleCancelQueue');
function scenario({team, lobby = false, owner = false, invitational = false, queued = false, world = 6, override = -1}) {
  const calls = [];
  const shared = {};
  const lobbyBindings = {
    IsInLobby: () => lobby,
    IsLobbyOwner: () => owner,
    AutoFillTeam: value => calls.push(['autofill', value]),
    SendStartGameMessage: id => calls.push(['steamStart', id]),
  };
  const bindings = {
    UIBindingLobby: lobbyBindings,
    UIBindingSystem: { InInvitational: () => invitational },
    UIBindingCharacterCreate: {
      TransferCharacter: (guid, id) => calls.push(['transfer', guid, id]),
      CancelQueue: () => calls.push(['cancel']), ROLE_HOST: 2, ROLE_MODERATOR: 3, ROLE_SPECTATOR: 0,
    },
    SharedGlobalData: { GetInstance: () => shared },
    UIBindingLocale: { translateCodeString: value => value },
    TimerEvent: { TIMER: 'timer' },
    UIDataBindingWithBoundObjects: binding => binding,
    BINDING_MAINMENU_QUEUE_WINDOW_VISIBLE: 'visible',
    BINDING_MAINMENU_ADMIN_MESSAGE: 'admin',
    BINDING_MAINMENU_LOGIN_QUEUE_MESSAGE: 'message',
    BINDING_MAINMENU_CANCEL_QUEUE_BUTTON: 'cancel',
  };
  const args = Object.keys(bindings), values = Object.values(bindings);
  const run = new Function('param1', ...args, requestBody);
  const group = new Function(...args, groupBody);
  const afterQueue = new Function(...args, afterQueueBody);
  const minTimerEvent = new Function('param1', ...args, minTimerBody);
  const asRole = new Function(...args, roleBody);
  const cancel = new Function(...args, cancelBody);
  const cancelEvent = new Function('param1', ...args, cancelEventBody);
  const timer = { addEventListener() {}, start() {}, stop() {} };
  const transferTimer = {
    callback: null, running: false,
    addEventListener(type, callback) { this.callback = callback; },
    start() { this.running = true; }, stop() { this.running = false; },
  };
  const binding = { setValue() {}, getBoundObjs: () => [] };
  const widget = {
    m_lobbyType: team, m_currentlySelectedServer: world, m_playerGuid: '12345',
    m_isAdminClient: false, m_isAutoFill: true, m_SearchTimer: timer,
    m_SearchMinTimer: timer, m_isObserver: true, isInQueue: () => queued,
    m_TransferMinTimer: transferTimer, m_role: 1,
    m_bindings: {visible: binding, admin: binding, message: binding, cancel: binding},
    showRequeueConfirmation: value => calls.push(['requeue', value]),
    OpenTransferScreen: text => calls.push(['screen', text]),
    CallGroupTransfer() { group.call(this, ...values); },
    transferAfterQueue() { afterQueue.call(this, ...values); },
    handleTransferMinTimerEvent(event) { minTimerEvent.call(widget, event, ...values); },
    TransferCharacterAsRole() { asRole.call(this, ...values); },
    cancelQueue() { cancel.call(this, ...values); },
    handleCancelQueue() { cancelEvent.call(this, null, ...values); },
    hideLegend() {}, hidePTCWCancelButton() {}, hideQueueTimeDisplay() {}, CloseTransferScreen() {},
  };
  run.call(widget, override, ...values);
  return { calls, shared, widget, transferTimer };
}
for (const [team, world] of [[2, 6], [5, 7]]) {
  const result = scenario({team, world});
  assert.deepEqual(result.calls.filter(call => call[0] === 'transfer'), [['transfer', '12345', world]]);
  assert(!result.calls.some(call => ['autofill', 'steamStart'].includes(call[0])));
  assert.equal(result.shared.isTeam2, team === 2);
  assert.equal(result.shared.isInGroupGame, true);
  assert.equal(result.widget.m_isObserver, false);
  // QueueExit follows the existing 1.5-second minimum timer and role transfer.
  // There must be no second Steam-owner gate after the first EC.
  result.widget.transferAfterQueue();
  assert.equal(result.transferTimer.running, true);
  result.transferTimer.callback(null);
  assert.equal(result.transferTimer.running, false);
  assert.deepEqual(result.calls.filter(call => call[0] === 'transfer'),
    [['transfer', '12345', world], ['transfer', '12345', world]]);
}
for (const options of [{lobby: true, owner: true}, {invitational: true}]) {
  const result = scenario({team: 2, ...options});
  assert.equal(result.calls.filter(call => call[0] === 'transfer').length, 1);
  assert.equal(result.calls.filter(call => call[0] === 'steamStart').length, 1);
  assert.equal(result.calls.filter(call => call[0] === 'autofill').length, 1);
}
const follower = scenario({team: 5, lobby: true, owner: false});
assert(!follower.calls.some(call => call[0] === 'transfer'));
const solo = scenario({team: 0, world: 1});
assert.deepEqual(solo.calls.filter(call => call[0] === 'transfer'), [['transfer', '12345', 1]]);
assert.equal(solo.shared.isInGroupGame, false);
const queued = scenario({team: 2, queued: true});
assert.deepEqual(queued.calls, [['requeue', -1]]);
queued.widget.handleCancelQueue();
assert.equal(queued.calls.filter(call => call[0] === 'cancel').length, 1);
assert(!queued.calls.some(call => call[0] === 'transfer'));
const idle = scenario({team: 2});
idle.widget.handleCancelQueue();
assert(!idle.calls.some(call => call[0] === 'cancel'));
assert.deepEqual(scenario({team: 2, world: 0}).calls, []);
assert.equal(scenario({team: 0, override: 5}).shared.isTeam2, false);
console.log('Re-exported team queue behavior passed: duos, fives, queue acceptance timer, owner, follower, invitational, solo, cancel, requeue, no selection.');
