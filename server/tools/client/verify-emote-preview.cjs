// Execute the rebuilt, FFDec re-exported ActionScript handlers with native/UI stubs.
// Usage: node verify-emote-preview.cjs <re-exported CustomizationWindow.as>
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');

assert(process.argv[2], 'Pass the re-exported CustomizationWindow.as path.');
const source = fs.readFileSync(process.argv[2], 'utf8');

// Ignore braces in quoted strings and comments while locating method boundaries.
function balancedEnd(start, open, close) {
  let depth = 0, quote = '', lineComment = false, blockComment = false;
  for (let i = start; i < source.length; i++) {
    const c = source[i], next = source[i + 1];
    if (lineComment) { if (c === '\n') lineComment = false; continue; }
    if (blockComment) { if (c === '*' && next === '/') { blockComment = false; i++; } continue; }
    if (quote) {
      if (c === '\\') i++;
      else if (c === quote) quote = '';
      continue;
    }
    if (c === '/' && next === '/') { lineComment = true; i++; continue; }
    if (c === '/' && next === '*') { blockComment = true; i++; continue; }
    if (c === '"' || c === "'") { quote = c; continue; }
    if (c === open) depth++;
    if (c === close && --depth === 0) return i;
  }
  throw new Error('Unterminated method delimiter at ' + start);
}

function extract(name) {
  const match = new RegExp('\\bfunction\\s+' + name + '\\s*\\(').exec(source);
  assert(match, name + ' must exist in the re-exported artifact');
  const argsStart = source.indexOf('(', match.index);
  const argsEnd = balancedEnd(argsStart, '(', ')');
  const bodyStart = source.indexOf('{', argsEnd);
  const bodyEnd = balancedEnd(bodyStart, '{', '}');
  const args = source.slice(argsStart + 1, argsEnd)
    .replace(/:\s*(?:Vector\.<[^>]+>|[\w.*]+)/g, '');
  const body = source.slice(bodyStart + 1, bodyEnd)
    .replace(/\b(var\s+\w+)\s*:\s*(?:Vector\.<[^>]+>|[\w.*]+)/g, '$1')
    .replace(/\s+as\s+(?:TextField|SkinListItemRenderer)\b/g, '')
    .replace(/\bsuper\./g, '__base.');
  return { args, body };
}

const methodNames = [
  'previewEmoteSlot', 'handleEmoteSlotListItemClick', 'handleEmoteSlotListIndexChange',
  'handleSkinsListItemClick', 'handleSkinsListIndexChange', 'stopEmotePreview',
  'requestPreviewEmoteTimeout', 'requestPreviewEmote', 'enter', 'exit',
];
const methods = Object.fromEntries(methodNames.map(name => [name, extract(name)]));

function scenario(mode = 'Emotes') {
  const calls = [], timers = new Map(), listeners = new Map();
  let rows = [{ EmoteAnimationItemId: '3877' }], now = 0, nextTimer = 1;
  const native = name => (...args) => calls.push([name, ...args]);
  function display(name) {
    return {
      selectedIndex: 0, dataProvider: [], play() {},
      addEventListener(type, callback) { listeners.set(name + ':' + type, callback); },
      removeEventListener(type, callback) {
        assert.equal(listeners.get(name + ':' + type), callback, name + ' removes its installed listener');
        listeners.delete(name + ':' + type);
      },
    };
  }
  function CustomizationEvent(type, value) { this.type = type; this.value = value; }
  Object.assign(CustomizationEvent, {
    SET_EMOTE_SLOT: 'slot', HIDE_GRIND_BUTTON: 'hideGrind', SHOW_GRIND_BUTTON: 'showGrind', GRIND: 'grind',
  });
  const stage = display('stage');
  stage.dispatchEvent = event => calls.push(['event', event.type, event.value]);
  const bindings = {
    EMOTES: 'Emotes', GEAR: 'Gear', WEAPONS: 'Weapons', VEHICLES: 'Vehicles', GRINDER_BUNDLE_ID: 244,
    int: value => Number(value) | 0,
    MouseEventEx: { LEFT_BUTTON: 0, RIGHT_BUTTON: 1 },
    ListEvent: { INDEX_CHANGE: 'index', ITEM_CLICK: 'click', ITEM_PRESS: 'press', ITEM_ROLL_OVER: 'over', ITEM_ROLL_OUT: 'out' },
    ButtonEvent: { CLICK: 'click' }, GameEvent: { EVENT_REWARD_SCRAP: 'scrap' },
    CustomizationEvent, stage,
    UIBindingLocale: { translateCodeString: value => value },
    UIBindingSound: { UI_KOTK_SELECT: 'selectSound', PlayUiSound: native('sound') },
    UIBindingIngamePurchase: { RequestPreviewCrateRewardsByBundle() {} },
    UIBindingVehicleSkins: { DeselectPreviewVehicle: native('deselectVehicle') },
    UIBindingItem: Object.fromEntries([
      'ResetPreviewEmoteAnimation', 'PreviewEmoteItem', 'SelectEditorEmoteAnimationSlotId',
      'SetEmoteItemByItemIdWithCachedData', 'PreviewSkinItem', 'SetSkinItemByItemIdWithCachedData',
      'RemoveNewAccountItemRecByItemId', 'OpenGearSkinEditor', 'OpenWeaponSkinEditor',
      'CloseGearSkinEditor', 'CloseWeaponSkinEditor',
    ].map(name => [name, native(name)])),
    uiDBManager: { query(sql) { calls.push(['query', sql]); return rows; } },
    setTimeout(callback, delay, ...args) {
      const id = nextTimer++;
      timers.set(id, { callback, args, due: now + delay });
      calls.push(['schedule', delay, ...args]);
      return id;
    },
    clearTimeout(id) { timers.delete(id); },
    __base: { enter: native('baseEnter'), exit: native('baseExit') },
  };
  const context = vm.createContext(bindings);
  const widget = {
    m_windowType: mode, m_previewEmoteTimeout: 0, m_vehicleTimeout: 0,
    m_isConfirmationRequested: false, m_isScrapping: false, m_menuId: 0,
    m_skinLegend: { m_scrap: { m_scrapBindText: {}, m_scrapValue: { m_scrapValueText: {} } } },
    m_crateInfo: { m_moreInfoBtn: display('moreInfo') },
    populateTitle() {}, animateSkins() { calls.push(['animateSkins']); },
    createBindings() {}, deleteBindings() { calls.push(['deleteBindings']); },
    getRarityString: () => 'Common', strReplace: (value, old, replacement) => value.replace(old, replacement),
  };
  for (const name of ['m_loadoutSlotList', 'm_loadoutCategoryList', 'm_loadoutSkinsList', 'm_emoteSlotsList',
    'm_loadoutSlotTitle', 'm_loadoutCategoryTitle', 'm_loadoutEmotesTitle', 'm_loadoutSkinsTitle',
    'm_slotsTitleBar', 'm_categoryTitleBar', 'm_emotesTitleBar', 'm_emotesPanel', 'm_scrollBar', 'm_slotPanel']) {
    widget[name] = display(name);
  }
  for (const [name, method] of Object.entries(methods)) {
    widget[name] = vm.runInContext('(function(' + method.args + '){' + method.body + '\n})', context,
      { filename: name + '.reexported.js' }).bind(widget);
  }
  return {
    calls, timers, listeners, widget,
    setRows(value) { rows = value; },
    advance(milliseconds) {
      now += milliseconds;
      for (const [id, timer] of timers) {
        if (timer.due <= now) { timers.delete(id); timer.callback(...timer.args); }
      }
    },
    previews: () => calls.filter(call => call[0] === 'PreviewEmoteItem'),
    resets: () => calls.filter(call => call[0] === 'ResetPreviewEmoteAnimation'),
  };
}

const event = (index, itemData, buttonIdx = 0) => ({ index, itemData, buttonIdx });
const slotEvent = (index, slotId, buttonIdx = 0) => event(index, { slotId, mappedKeyForInputAction: 'F' + slotId }, buttonIdx);

// Slot previews query the current assignment, rather than assuming the default F-key layout.
const assigned = scenario();
assigned.widget.previewEmoteSlot(9);
assert.deepEqual(assigned.calls.filter(call => call[0] === 'query'), [
  ['query', 'SELECT EmoteAnimationItemId FROM EmoteItems WHERE EmoteAnimationSlotId=9'],
]);
assert.equal(assigned.resets().length, 1);
assigned.advance(299);
assert.deepEqual(assigned.previews(), []);
assigned.advance(1);
assert.deepEqual(assigned.previews(), [['PreviewEmoteItem', 3877]]);
assigned.setRows([{ EmoteAnimationItemId: '3287' }]);
assigned.widget.handleEmoteSlotListItemClick(slotEvent(0, 9));
assigned.advance(300);
assert.deepEqual(assigned.previews().at(-1), ['PreviewEmoteItem', 3287]);

// CoreList dispatches ITEM_CLICK before updating selectedIndex and INDEX_CHANGE.
const selection = scenario();
selection.widget.handleEmoteSlotListItemClick(slotEvent(1, 4));
assert.deepEqual(selection.calls, [], 'a new index must not also preview from ITEM_CLICK');
selection.widget.m_emoteSlotsList.selectedIndex = 1;
selection.widget.handleEmoteSlotListIndexChange(slotEvent(1, 4));
assert.deepEqual(selection.calls.filter(call => ['event', 'SelectEditorEmoteAnimationSlotId'].includes(call[0])),
  [['event', 'slot', 4], ['SelectEditorEmoteAnimationSlotId', 4]]);
assert.equal(selection.calls.filter(call => call[0] === 'schedule').length, 1);
assert(selection.calls.findIndex(call => call[0] === 'SelectEditorEmoteAnimationSlotId') <
  selection.calls.findIndex(call => call[0] === 'query'), 'select the slot before reading its assignment');
selection.advance(300);
assert.equal(selection.previews().length, 1);
assert.equal(selection.widget.m_selectedEmoteSlotName, 'F4');

// A missing or invalid assignment cancels a pending selection and never replays stale data.
for (const rows of [null, [], [{}], [{ EmoteAnimationItemId: 0 }], [{ EmoteAnimationItemId: -1 }],
  [{ EmoteAnimationItemId: 'invalid' }]]) {
  const result = scenario();
  result.widget.previewEmoteSlot(1);
  result.setRows(rows);
  result.widget.previewEmoteSlot(2);
  result.advance(300);
  assert.deepEqual(result.previews(), [], 'invalid assignment must cancel the preceding timer');
  assert.equal(result.timers.size, 0);
}
for (const slot of [0, -1]) {
  const result = scenario();
  result.widget.previewEmoteSlot(slot);
  assert.deepEqual(result.calls, [], 'invalid slots must not query or play');
}
for (const invalid of [null, event(-1, null), event(0, null), slotEvent(0, 1, 1)]) {
  const result = scenario();
  result.widget.handleEmoteSlotListItemClick(invalid);
  assert.deepEqual(result.calls, [], 'invalid or right-button slot clicks must be ignored');
}

// Existing item selection still previews/equips; repeat clicks replay only the selected item.
const grid = scenario();
grid.widget.m_crateInfo = null;
grid.widget.handleSkinsListItemClick(event(1, { itemId: '3280' }));
assert.deepEqual(grid.calls, []);
grid.widget.m_loadoutSkinsList.selectedIndex = 1;
grid.widget.handleSkinsListIndexChange(event(1, { itemId: '3280', name: 'TeaBag', isEmoteOwned: true }));
assert.equal(grid.calls.filter(call => call[0] === 'schedule').length, 1);
assert.deepEqual(grid.calls.filter(call => call[0] === 'SetEmoteItemByItemIdWithCachedData'),
  [['SetEmoteItemByItemIdWithCachedData', 3280]]);
grid.advance(300);
grid.widget.handleSkinsListItemClick(event(1, { itemId: '3280' }));
grid.advance(300);
assert.deepEqual(grid.previews(), [['PreviewEmoteItem', 3280], ['PreviewEmoteItem', 3280]]);
for (const invalid of [null, event(-1, { itemId: 3276 }), event(0, null), event(0, { itemId: 3276 }, 1)]) {
  const result = scenario();
  result.widget.handleSkinsListItemClick(invalid);
  assert.deepEqual(result.calls, [], 'invalid or right-button item clicks must be ignored');
}

for (const mode of ['Gear', 'Weapons', 'Vehicles']) {
  const result = scenario(mode);
  result.widget.previewEmoteSlot(1);
  result.widget.handleEmoteSlotListItemClick(slotEvent(0, 1));
  result.widget.handleSkinsListItemClick(event(0, { itemId: 3276 }));
  result.widget.stopEmotePreview();
  assert.deepEqual(result.calls, [], mode + ' must ignore emote-only paths');
  if (mode !== 'Vehicles') {
    result.widget.m_crateInfo = null;
    result.widget.handleSkinsListIndexChange(event(0, { itemId: 1234, name: 'Skin', isAccountItemOwned: true }));
    assert.deepEqual(result.calls.filter(call => ['PreviewSkinItem', 'SetSkinItemByItemIdWithCachedData'].includes(call[0])),
      [['PreviewSkinItem', 1234], ['SetSkinItemByItemIdWithCachedData', 1234]]);
    assert.deepEqual(result.previews(), []);
    assert.equal(result.timers.size, 0);
  }
}

// Rapid changes replace the queued preview, and all exits cancel it, including confirmation exits.
const rapid = scenario();
rapid.widget.requestPreviewEmoteTimeout(3276);
rapid.advance(100);
rapid.widget.requestPreviewEmoteTimeout(3287);
rapid.advance(200);
assert.deepEqual(rapid.previews(), []);
rapid.advance(100);
assert.deepEqual(rapid.previews(), [['PreviewEmoteItem', 3287]]);
for (const confirmation of [false, true]) {
  const result = scenario();
  result.widget.enter();
  assert.equal(result.listeners.get('m_emoteSlotsList:click'), result.widget.handleEmoteSlotListItemClick);
  assert.equal(result.listeners.get('m_loadoutSkinsList:click'), result.widget.handleSkinsListItemClick);
  result.widget.previewEmoteSlot(1);
  result.widget.m_isConfirmationRequested = confirmation;
  result.calls.length = 0;
  result.widget.exit();
  assert.deepEqual(result.calls.slice(0, 2), [['baseExit'], ['ResetPreviewEmoteAnimation']]);
  assert.equal(result.widget.m_previewEmoteTimeout, 0);
  result.advance(1000);
  assert.deepEqual(result.previews(), []);
  assert.equal(result.timers.size, 0);
  if (!confirmation) {
    assert.equal(result.listeners.has('m_emoteSlotsList:click'), false);
    assert.equal(result.listeners.has('m_loadoutSkinsList:click'), false);
  }
}

console.log('Re-exported emote preview behavior passed: actual slot assignments, selection and repeat clicks, invalid assignments, native equip preservation, skin/weapon isolation, timer replacement, and exit cleanup.');
