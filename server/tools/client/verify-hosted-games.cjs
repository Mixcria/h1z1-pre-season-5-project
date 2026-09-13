// Execute the rebuilt/re-exported hosted row adapter and events refresh method.
const fs = require('node:fs');
const assert = require('node:assert/strict');
const source = fs.readFileSync(process.argv[2], 'utf8');
function extract(name) {
  const at = source.indexOf('function ' + name + '(');
  assert(at >= 0, name + ' must exist');
  const start = source.indexOf('{', at);
  let depth = 1, end = start + 1;
  while (depth) {
    assert(end < source.length, name + ' must have balanced braces');
    if (source[end] === '{') depth++;
    if (source[end] === '}') depth--;
    end++;
  }
  return source.slice(start + 1, end - 1)
    .replace(/var (\w+):(Array|Object|String|CoreList|Boolean|uint|int)/g, 'var $1')
    .replace(/for each\((.*?) in (.*?)\)/g, 'for($1 of $2)')
    .replace(/ as CoreList/g, '');
}
let values = {};
const binding = { GetStringHashValue(key, fallback) {
  assert.equal(typeof fallback, 'string', 'String defaults select the native string return branch');
  return Object.hasOwn(values, key) ? values[key] : fallback;
} };
const adapter = new Function('param1', 'UIBindingSystem', extract('hostedGameDisplayRows'));
const native = [
  {WorldId: 8, Name: 'HOSTED GAMES', GameModeName: 'SOLO', StartTime: '17:00', Role: 1, CanEnter: 1, IsLocked: 0},
  {WorldId: 1000, Name: 'HOSTED GAMES', GameModeName: 'SOLO', StartTime: '18:00', Role: 1, CanEnter: 0, IsLocked: 1},
];
const original = JSON.parse(JSON.stringify(native));
assert.deepEqual(adapter(native, binding), original, 'Absent metadata retains native rows');
values = {'Cranberry.Hosted.8.Name': 'Sam\'s cup', 'Cranberry.Hosted.8.Mode': 'Duos',
  'Cranberry.Hosted.1000.Name': 'Evening game', 'Cranberry.Hosted.1000.Mode': 'Fives'};
const displayed = adapter(native, binding);
assert.equal(displayed[0].Name, "Sam's cup (Duos)");
assert.equal(displayed[1].Name, 'Evening game (Fives)');
assert.equal(displayed[0].GameModeName, 'Duos');
assert.deepEqual(native, original, 'Display must never mutate MatchSchedule database rows');
assert.deepEqual(adapter(native, binding), displayed, 'Refresh does not duplicate mode suffixes');
for (let i = 0; i < native.length; i++) {
  for (const key of ['WorldId', 'StartTime', 'Role', 'CanEnter', 'IsLocked'])
    assert.equal(displayed[i][key], native[i][key], key + ' retains server admission data');
}
values['Cranberry.Hosted.8.Name'] = '<Game & friends>';
assert.equal(adapter(native, binding)[0].Name, '<Game & friends> (Duos)');
delete values['Cranberry.Hosted.8.Name'];
assert.equal(adapter(native, binding)[0].Name, 'HOSTED GAMES (Duos)', 'Partial metadata has native fallback');
assert.deepEqual(adapter([], binding), []);
const setValues = [];
const refresh = new Function('param1', 'uiDBManager', 'WidgetNames', 'DataProvider',
  'BINDING_EVENTS_SPECIAL_EVENTS_LISTDATA', extract('handleSpecialEventDataChanged'));
const widget = {
  m_specialEventData: true, m_currentOpenWindow: 2,
  m_bindings: {list: {setValue: value => setValues.push(value)}},
  hostedGameDisplayRows: rows => adapter(rows, binding),
  getBoundObject: () => ({selectedIndex: -1}),
};
const database = {query: sql => {
  assert.equal(sql, widget.m_currentOpenWindow === 2
    ? 'SELECT * FROM MatchSchedule WHERE IsHostedGame = 1'
    : 'Select * From MatchSchedule WHERE IsHostedGame = 0');
  return native;
} };
function DataProvider(rows) { this.rows = rows; }
function runRefresh() {
  refresh.call(widget, null, database, {EVENTS_HOSTED_GAMES: 2, SPECIAL_EVENTS_WINDOW: 1}, DataProvider, 'list');
}
runRefresh();
assert.equal(setValues.at(-1).rows[0].Name, 'HOSTED GAMES (Duos)');
widget.m_currentOpenWindow = 1;
runRefresh();
assert.deepEqual(setValues.at(-1).rows, native, 'Special/public events retain their native display');
widget.m_currentOpenWindow = 2;
values = {};
runRefresh();
assert.deepEqual(setValues.at(-1).rows, original, 'Metadata removed on next refresh cannot leave stale names');
const management = new Function('param1', 'WidgetNames', 'GameEvent', 'UIBindingChat', 'MainController',
  extract('handleHostedManagement'));
const keyboard = new Function('param1', 'WidgetNames', 'Keyboard', 'KeyboardEvent', 'MainController',
  extract('handleHostedManagementKey'));
const calls = [];
function GameEvent(type) { this.type = type; }
GameEvent.EVENT_TOGGLE_DEBUG_CONSOLE = 'toggle-console';
const widgets = {EVENTS_HOSTED_GAMES: 2, SPECIAL_EVENTS_WINDOW: 1, CONSOLE_WINDOW: 3};
const keys = {F1: 112, ESCAPE: 27};
const keyEvents = {KEY_DOWN: 'keyDown', KEY_UP: 'keyUp'};
let loaded = false, loading = false;
const manager = {
  isWidgetLoaded(id) { assert.equal(id, widgets.CONSOLE_WINDOW); return loaded; },
  isWidgetLoading(id) { assert.equal(id, widgets.CONSOLE_WINDOW); return loading; },
};
const controller = {getWidgetManager: () => manager};
const chat = {ProcessChatCommand: command => calls.push(['command', command])};
widget.m_hostedManagementKeys = {};
widget.m_stage = {
  focus: null,
  dispatchEvent(event) {
    assert.equal(event.type, GameEvent.EVENT_TOGGLE_DEBUG_CONSOLE, 'Use the existing console cleanup route');
    if (loaded || loading) {
      assert.equal(this.focus, null, 'Closing clears text focus before dispatching the toggle');
      loaded = loading = false;
    } else loaded = true;
    calls.push(['event', event.type]);
  },
};
widget.handleHostedManagement = () => management.call(widget, null, widgets, GameEvent, chat, controller);
widget.handleHostedManagementKey = event => keyboard.call(widget, event, widgets, keys, keyEvents, controller);

// The client synthesizes a stage KEY_UP from a downstream InputDelegate/MainController
// KEY_DOWN after focus changes. Model that path plus real capture/target/bubble ordering:
// the management listener must consume the original event before it reaches that path.
const registrations = [];
const enter = extract('handleUIEnter');
for (const type of ['KEY_DOWN', 'KEY_UP']) {
  for (const capture of [true, false]) {
    const registration = `addEventListener(KeyboardEvent.${type},this.handleHostedManagementKey,${capture},1000,true)`;
    assert(enter.includes(registration), `Hosted entry registers ${type}, capture=${capture}, before normal input`);
    registrations.push({type: keyEvents[type], capture, priority: 1000, callback: widget.handleHostedManagementKey});
    const removal = `removeEventListener(KeyboardEvent.${type},this.handleHostedManagementKey,${capture})`;
    for (const method of ['handleUIExit', 'deinitialize'])
      assert(extract(method).includes(removal), `${method} removes ${type}, capture=${capture}`);
  }
}
let downstreamEvents = 0, synthesizedReleases = 0, previousFocus = true;
function sendKey(type, keyCode, target = 'child') {
  const event = {type, keyCode, stopped: false, stopImmediatePropagation() { this.stopped = true; }};
  function dispatchPhase(capture) {
    const listeners = registrations.filter(listener => listener.type === type && listener.capture === capture);
    if (!capture) listeners.push({priority: 0, callback: incoming => {
      downstreamEvents++;
      if (incoming.type === keyEvents.KEY_DOWN && previousFocus) {
        previousFocus = false;
        synthesizedReleases++;
        sendKey(keyEvents.KEY_UP, incoming.keyCode, 'stage');
      }
    }});
    for (const listener of listeners.sort((a, b) => b.priority - a.priority)) {
      if (event.stopped) break;
      listener.callback(event);
    }
  }
  if (target !== 'stage') dispatchPhase(true);
  if (!event.stopped) dispatchPhase(false);
  return event;
}
const down = (key = keys.F1, target = 'child') => sendKey(keyEvents.KEY_DOWN, key, target);
const up = (key = keys.F1, target = 'child') => sendKey(keyEvents.KEY_UP, key, target);
const toggleCount = () => calls.filter(call => call[0] === 'event').length;
const helpCount = () => calls.filter(call => call[0] === 'command').length;

assert(up(keys.F1, 'stage').stopped, 'Unmatched/synthetic F1 release is consumed while hosted');
assert.equal(toggleCount(), 0, 'A synthetic release never opens the console');
assert(down().stopped);
assert.equal(loaded, true, 'First F1 down opens the console');
assert.deepEqual(calls, [['event', 'toggle-console'], ['command', '/hostgame']],
  'An ordinary player opens the console and receives help without a tier check');
down();
down();
assert.equal(toggleCount(), 1, 'Holding F1 does not repeatedly toggle');
assert(up().stopped);
assert.equal(toggleCount(), 1, 'Opening F1 release does not close the console');
widget.m_stage.focus = {text: '/hostgame invite unfinished'};
assert(down().stopped);
assert.equal(loaded, false, 'The next F1 press closes even with nonempty console input');
assert.equal(helpCount(), 1, 'Closing does not request hosted help again');
down();
up();
up(keys.F1, 'stage');
assert.equal(toggleCount(), 2, 'Close repeats and physical/synthetic releases cannot reopen');
assert.equal(synthesizedReleases, 0, 'Hotkeys are captured before MainController synthesizes KEY_UP');
assert.equal(downstreamEvents, 0, 'Handled hotkeys cannot reach other UI input handlers');

down(keys.F1, 'stage');
assert.equal(loaded, true, 'Events targeting the stage also open exactly once');
up(keys.F1, 'stage');
widget.m_stage.focus = {text: '/hostgame redeem unfinished'};
down(keys.ESCAPE, 'stage');
assert.equal(loaded, false, 'Escape closes a focused console without clearing/submitting its command');
assert.equal(widget.m_stage.focus, null);
down(keys.ESCAPE, 'stage');
up(keys.ESCAPE, 'stage');
assert.equal(toggleCount(), 4, 'Escape repeat/release cannot reopen the console');
assert.equal(helpCount(), 2, 'Only actual openings request help');

const beforeClosedEscape = toggleCount();
assert.equal(down(keys.ESCAPE).stopped, false, 'Escape retains normal navigation when the console is closed');
assert.equal(up(keys.ESCAPE).stopped, false);
assert.equal(toggleCount(), beforeClosedEscape);
const beforeOtherKey = calls.length;
assert.equal(down(65).stopped, false);
assert.equal(up(65).stopped, false);
assert.equal(calls.length, beforeOtherKey, 'Unrelated keys do not affect management');

loading = true;
widget.m_stage.focus = {text: 'pending console'};
down(keys.ESCAPE);
up(keys.ESCAPE);
assert.equal(loading, false, 'Escape can also cancel a loading console');
assert.equal(helpCount(), 2);

const beforeGamepad = toggleCount();
widget.handleHostedManagement();
assert.equal(loaded, true, 'Gamepad opens using the same management route');
widget.m_stage.focus = {text: 'gamepad-open console'};
widget.handleHostedManagement();
assert.equal(loaded, false, 'Gamepad can close using the same cleanup route');
assert.equal(toggleCount(), beforeGamepad + 2);
assert.equal(helpCount(), 3, 'Gamepad closing does not request help');

// A tracked release remains harmless if the hosted screen loses activation mid-press.
down();
widget.m_currentOpenWindow = widgets.SPECIAL_EVENTS_WINDOW;
const beforeExitRelease = calls.length;
assert(up().stopped);
assert.equal(Object.hasOwn(widget.m_hostedManagementKeys, keys.F1), false, 'Release clears the held key after an exit');
assert.equal(calls.length, beforeExitRelease);
assert.equal(down().stopped, false);
assert.equal(up().stopped, false);
assert.equal(down(keys.ESCAPE).stopped, false);
assert.equal(up(keys.ESCAPE).stopped, false);
widget.handleHostedManagement();
assert.equal(calls.length, beforeExitRelease, 'Special-event windows cannot invoke hosted management');
assert(source.includes('F1: OPEN / CLOSE CONSOLE - ESC: CLOSE'));
assert(source.includes('NavigationCode.GAMEPAD_Y,"Open / close console (F1)",this.handleHostedManagement'));
console.log('Hosted UI verified: names/modes, native fallback, preserved join data, F1 press/repeat/release and focus routing, Escape close, loading cancellation, gamepad toggle, help only on open, hosted-only activation and complete listener cleanup.');
