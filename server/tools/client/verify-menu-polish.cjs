// Execute methods re-exported from the rebuilt GFX, with native UI bindings stubbed.
// node tools/client/verify-menu-polish.cjs <build-menu-polish output directory>
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const root = process.argv[2];
assert(root, 'Pass the rebuilt menu directory');

function methods(asset, relative, names, bindings) {
  const source = fs.readFileSync(path.join(root, asset, 'after/scripts', relative), 'utf8');
  function end(start, open, close) {
    let depth = 0, quote = '', comment = '';
    for (let i = start; i < source.length; i++) {
      const c = source[i], next = source[i + 1];
      if (comment === '//') { if (c === '\n') comment = ''; continue; }
      if (comment === '/*') { if (c === '*' && next === '/') { comment = ''; i++; } continue; }
      if (quote) { if (c === '\\') i++; else if (c === quote) quote = ''; continue; }
      if (c === '/' && (next === '/' || next === '*')) { comment = c + next; i++; continue; }
      if (c === '"' || c === "'") { quote = c; continue; }
      if (c === open) depth++;
      if (c === close && --depth === 0) return i;
    }
    throw new Error('Unterminated method');
  }
  const context = vm.createContext(bindings);
  const result = {};
  for (const name of names) {
    const match = new RegExp('\\bfunction\\s+' + name + '\\s*\\(').exec(source);
    assert(match, name);
    const argStart = source.indexOf('(', match.index), argEnd = end(argStart, '(', ')');
    const bodyStart = source.indexOf('{', argEnd), bodyEnd = end(bodyStart, '{', '}');
    const args = source.slice(argStart + 1, argEnd).replace(/:\s*[\w.*]+/g, '');
    const body = source.slice(bodyStart + 1, bodyEnd)
      .replace(/\b(var\s+\w+)\s*:\s*[\w.*]+/g, '$1')
      .replace(/\s+as\s+[\w.]+/g, '').replace(/\bsuper\./g, '__base.');
    result[name] = vm.runInContext('(function(' + args + '){' + body + '})', context);
  }
  return { result, context };
}

const calls = [];
const bindings = {
  VEHICLES: 'Vehicles', EMOTES: 'Emotes', GEAR: 'Gear', WEAPONS: 'Weapons',
  INVALIDATE_LOADOUT_SLOTS: 1, INVALIDATE_LOADOUT_CATEGORY: 2, INVALIDATE_LOADOUT_SKINS: 3,
  invalidate: value => calls.push(['invalidate', value]),
  DataProvider: function(rows) { this.rows = rows; }, int: Number,
  UIBindingItem: { SelectEditorSkinTargetPrototypeItemId: id => calls.push(['category', id]) },
  UIBindingStaticView: { SetStaticView: view => calls.push(['view', view]) },
  stage: { dispatchEvent() {} }, CustomizationEvent: function() {},
};
const names = ['updateLoadoutSlotData', 'updateLoadoutCategoryData', 'updateLoadoutSkinsData',
  'updateVehicleLoadOutSlots', 'updateVehicleLoadoutCat', 'updateVehicleLoadoutSkins',
  'handleCategoryListIndexChange'];
const custom = methods('CustomizationWindow', 'views/customization/CustomizationWindow.as', names, bindings).result;
for (const mode of ['Gear', 'Weapons', 'Vehicles', 'Emotes']) {
  const widget = { ...custom, m_windowType: mode, m_loadoutSlotList: {}, m_loadoutCategoryList: {},
    m_loadoutSkinsList: {}, m_crateInfo: {}, populateTitle() {}, animateSkins() {} };
  const fields = ['m_loadoutSlotList', 'm_loadoutCategoryList', 'm_loadoutSkinsList'];
  for (let i = 0; i < 3; i++) {
    const original = { existingRows: true };
    widget[fields[i]].dataProvider = original;
    widget[names[i]](['ordinary']);
    if (mode === 'Vehicles' || mode === 'Emotes') assert.equal(widget[fields[i]].dataProvider, original);
    else assert.equal(widget[fields[i]].dataProvider.rows[0], 'ordinary');
    const beforeVehicle = widget[fields[i]].dataProvider;
    widget[names[i + 3]](['vehicle']);
    if (mode === 'Vehicles') assert.equal(widget[fields[i]].dataProvider.rows[0], 'vehicle');
    else assert.equal(widget[fields[i]].dataProvider, beforeVehicle);
  }
  calls.length = 0;
  widget.handleCategoryListIndexChange({ index: 0, itemData: { prototypeItemId: 2229, name: 'AK-47' } });
  assert.deepEqual(calls.filter(c => c[0] === 'view'), mode === 'Weapons' ? [['view', 'kotkweaponpreview:2229']] : []);
  calls.length = 0;
  widget.handleCategoryListIndexChange({ index: -1 });
  assert.equal(calls.length, 0);
}

const motdBindings = { _purchaseSuccess: false, int: Number,
  UIBindingIngamePurchase: { GetTargetedPromoSaleSeconds: () => 60 } };
const motd = methods('CharacterSelectWindow', 'ui/viewControllers/MOTDViewController.as',
  ['refreshViewState', 'handleBillboardPanelChangeEvent'], motdBindings);
const news = { ...motd.result, _container: {}, _success: {}, _bbpanels: { selectedItem: null },
  _timerLabel: { visible: true }, _title: {}, _panelHidden: false,
  getSKUByNudgeOfferId: id => ({ NudgeOfferId: id }) };
news.refreshViewState();
news.handleBillboardPanelChangeEvent({});
assert.equal(news._container.visible, false);
assert.equal(news._timerLabel.visible, false);
news._bbpanels.selectedItem = { promo: false, label: 'Server news' };
news.handleBillboardPanelChangeEvent({});
assert.equal(news._container.visible, true);
assert.equal(news._title.htmlText, 'Server news');
assert.equal(news._timerLabel.visible, false);
news._bbpanels.selectedItem = { promo: true, id: 1, label: 'Timed offer' };
news.handleBillboardPanelChangeEvent({});
assert.equal(news._timerLabel.visible, true);
assert.equal(news._timerLabel.timeRemainingValue, 60);
motd.context._purchaseSuccess = true;
news.refreshViewState();
assert.equal(news._container.visible, false);
assert.equal(news._success.visible, true);
assert.equal(news._bbpanels.paused, true);

for (const asset of ['MOTDWidget', 'CharacterSelectWindow']) {
  const initialize = methods(asset, 'views/motd/MOTDWidget.as', ['initialize'],
    { __base: { initialize() {} }, Extensions: { isScaleform: true } }).result.initialize;
  const timer = { visible: true };
  const widget = { container: { getChildByName: name => name === 'timerLabel' ? timer : {} },
    success: { getChildByName: () => ({}) } };
  initialize.call(widget);
  assert.equal(timer.visible, false, asset + ' suppresses the EXPIRED placeholder before data arrives');
  assert.equal(widget.container.visible, false);
}
console.log('PASS: category previews, Rides/Emotes list isolation, empty/populated news, promotion timer and purchase-success display.');
