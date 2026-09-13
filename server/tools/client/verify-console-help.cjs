// Execute the actual rebuilt/re-exported console keyboard handler with native mocks.
const fs = require('node:fs');
const assert = require('node:assert/strict');
const source = fs.readFileSync(process.argv[2], 'utf8');
const marker = 'function handleTextInputKeyboardEvent(';
assert.equal(source.split(marker).length - 1, 1, 'Exactly one console keyboard handler must exist');
const start = source.indexOf('{', source.indexOf(marker));
let depth = 1, end = start + 1;
while (depth) {
  assert(end < source.length, 'Console keyboard handler must have balanced braces');
  if (source[end] === '{') depth++;
  if (source[end] === '}') depth--;
  end++;
}
const body = source.slice(start + 1, end - 1).replace(/ as TextInput/g, '');
const execute = new Function('param1', 'KeyboardEvent', 'Keyboard', 'UIBindingChat', 'GameEvent', body);
const KeyboardEvent = {KEY_DOWN: 'keyDown', KEY_UP: 'keyUp'};
const Keyboard = {ENTER: 13, NUMPAD_ENTER: 108, UP: 38, DOWN: 40, TAB: 9, ESCAPE: 27, BACKQUOTE: 192};
function GameEvent(type) { this.type = type; }
GameEvent.EVENT_TOGGLE_DEBUG_CONSOLE = 'toggle-console';

function invoke(text, keyCode = Keyboard.ENTER, type = KeyboardEvent.KEY_DOWN, localResult = false) {
  const calls = [];
  const input = {text};
  const native = {
    ProcessChatCommand: line => calls.push(['native', line]),
    ChatSetPreviousCommand: () => calls.push(['previous']),
    ChatSetNextCommand: () => calls.push(['next']),
    ChatTabCompletion: line => calls.push(['complete', line]),
  };
  const context = {
    _commandsProcessor: {processCommand(line) { calls.push(['local', line]); return localResult; }},
    inputText(target) {
      assert.equal(target, input, 'Existing input-clear path receives the original text field');
      calls.push(['clear']);
      target.text = '';
    },
    m_stage: {focus: input, dispatchEvent: event => calls.push(['event', event.type])},
  };
  const event = {type, keyCode, currentTarget: input,
    stopImmediatePropagation: () => calls.push(['stop'])};
  execute.call(context, event, KeyboardEvent, Keyboard, native, GameEvent);
  return {calls, input, context};
}

const translations = [
  ['/help', '/commands'],
  ['/HELP', '/commands'],
  ['/help hostgame', '/commands hostgame'],
  ['/HeLp   HoStGaMe  2 ', '/commands   HoStGaMe  2 '],
  [' \t/help 3', ' \t/commands 3'],
  ['/help\tHoStGaMe', '/commands\tHoStGaMe'],
  ['/help\n2', '/commands\n2'],
  ['\r\n/HeLp\r\nHostGame', '\r\n/commands\r\nHostGame'],
  ['help', '/commands'],
  ['./vehicle 1', '/vehicle 1'],
  ['./VEHICLE list', '/VEHICLE list'],
  [' \t./spawnvehicle\tPoliceCar  ', ' \t/spawnvehicle\tPoliceCar  '],
  [' \tvehicle\t3 0 1 ', ' \t/vehicle\t3 0 1 '],
  ['car ATV', '/car ATV'],
  ['CAR   PickupTruck  ', '/CAR   PickupTruck  '],
  ['spawncar jeep', '/spawncar jeep'],
  ['item add 2229 1 0', '/item add 2229 1 0'],
  ['./goto MixedCaseName', '/goto MixedCaseName'],
  ['warp MixedCaseName', '/warp MixedCaseName'],
  ['./god', '/god'],
  [' \t./help native vehicle', ' \t/commands native vehicle'],
  ['fog off', '/fog off'],
];
const unchanged = [
  '', ' \t', '/helper', '/helpful hostgame', '/help?', '/help/hostgame',
  '/commands hostgame', '/clear', '/binding input iskeyboardenabled',
  '/vehicles', '/vehicle?', '/vehicle/list', '//vehicle', '.vehicle', '../vehicle',
  '/carpet', '/spawnvehicles 2', '/say /vehicle', '/car ATV',
  '/vehicle', '/vehicle list', '/vehicle 1', '/vehicle 5 0 1 0', '/VeHiClE 2',
  '/item add 2229 1 0', '/goto MixedCaseName', '/god', '/loc',
  '/vehicle MixedCase /vehicle \u00c9t\u00e9 \u676f',
  '/hostgame',
  '/hostgame redeem HGK-AbCdEf0123456789_-ZyXw',
  '/hostgame invite 8 2h MixedCaseAccountId',
  '/hostgame create eu duos Sam\'s Été 杯',
  '/hostgame revoke MixedCaseKeyId',
  '  /HOSTGAME  mode 8 fives  ',
];
for (const key of [Keyboard.ENTER, Keyboard.NUMPAD_ENTER]) {
  for (const [line, expected] of [...translations, ...unchanged.map(line => [line, line])]) {
    const {calls, input} = invoke(line, key);
    assert.deepEqual(calls, [['local', line], ['native', expected], ['clear'], ['stop']],
      `Enter ${key} forwards exactly once and preserves submit order for ${JSON.stringify(line)}`);
    assert.equal(input.text, '', 'Submission still clears the input');
    if (line === expected)
      assert.deepEqual(Buffer.from(calls[1][1], 'utf8'), Buffer.from(line, 'utf8'),
        'Unrelated commands, key tokens, argument case and Unicode retain exact UTF-8 bytes');
  }
  assert.deepEqual(invoke('/help hostgame', key, KeyboardEvent.KEY_UP).calls, [],
    'Enter release does not submit a second command');
  assert.deepEqual(invoke('/vehicle ATV', key, KeyboardEvent.KEY_UP).calls, [],
    'Vehicle alias Enter release does not submit a second command');
}
assert.deepEqual(invoke('/clear', Keyboard.ENTER, KeyboardEvent.KEY_DOWN, true).calls,
  [['local', '/clear'], ['native', '/clear'], ['clear'], ['stop']],
  'The alias does not alter handling of a command accepted by the local UI processor');

const originalLine = '/hostgame redeem HGK-KeepCase_123';
assert.deepEqual(invoke(originalLine, Keyboard.UP).calls, [['previous'], ['stop']], 'History-up stays native');
assert.deepEqual(invoke(originalLine, Keyboard.DOWN).calls, [['next'], ['stop']], 'History-down stays native');
assert.deepEqual(invoke(originalLine, Keyboard.TAB).calls, [['complete', originalLine], ['stop']],
  'Tab completion retains the exact original line');
const escape = invoke(originalLine, Keyboard.ESCAPE);
assert.deepEqual(escape.calls, [['stop']]);
assert.equal(escape.context.m_stage.focus, null);
assert.equal(escape.input.text, originalLine, 'The original console Escape branch does not submit or clear input');
assert.deepEqual(invoke('', Keyboard.BACKQUOTE).calls, [['event', 'toggle-console'], ['stop']]);
assert.deepEqual(invoke(originalLine, Keyboard.BACKQUOTE).calls, [['stop']]);
assert.deepEqual(invoke(originalLine, 65).calls, [], 'Other keys remain untouched');
console.log('Console native input verified: slash and ./ prefixes accepted; original vehicle/item/goto and all command names retained, arguments remain exact, both Enter keys submit once, and input clearing, history, completion and other keys retain their behavior.');
