#!/usr/bin/env node
// Exercise business logic from FFDec's re-export of the compiled patch. This is
// not a Flash renderer: only the selected method bodies run with binding stubs.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

if (process.argv.length !== 3) throw new Error('Usage: node verify-crate-unlock-ten.cjs <build/verify/scripts>');
const root = process.argv[2];
const manager = fs.readFileSync(path.join(root, 'views/marketplace/managers/MarketplaceManager.as'), 'utf8');
const button = fs.readFileSync(path.join(root, 'views/marketplace/BuyButton.as'), 'utf8');
const panel = fs.readFileSync(path.join(root, 'views/marketplace/pages/store/AccountDetailPanel.as'), 'utf8');

function body(source, method) {
  const start = source.indexOf('function ' + method + '(');
  assert(start >= 0, 'Missing function ' + method);
  const begin = source.indexOf('{', start);
  let depth = 1, end = begin + 1;
  while (depth && end < source.length) {
    if (source[end] === '{') depth++;
    if (source[end] === '}') depth--;
    end++;
  }
  assert.equal(depth, 0);
  return source.slice(begin + 1, end - 1)
    .replace(/var (\w+):(?:\w+|\*)/g, 'var $1')
    .replace(/ as Boolean/g, '')
    .replace(/super\.draw\(\);/g, '');
}
const MarketplaceManager = { getInstance: () => ({ getCurrencyIconId: () => 1 }) };
const MarketplacePageManager = { RECEIVE_CRATE_PAGE: 'receive' };
const GameUtils = { TracePrint() {} };
const Colors = { GRAY: 0 };
const DynamicImage = { IMAGE_OPTION_32: 32 };
let added = [], funds = 0, allowed = 0, denied = 0, confirmation;
const UIBindingIngamePurchase = {
  CancelOrder() {}, CreateNewOrder() { return true; }, PlaceOrder() { return true; }, IsWalletZipcodeOnFile() { return true; },
  AddStoreBundleToOrder(...args) { added.push(args); return true; }
};
const CurrencyManager = { getInstance: () => ({ getCurrencyAmount: () => funds }) };
const UIBindingLocale = { translateCodeString: value => value };
const StringUtils = { ReplaceInString: (...args) => args };
const bindings = { MarketplaceManager, MarketplacePageManager, GameUtils,
  Colors, DynamicImage, UIBindingIngamePurchase, CurrencyManager, UIBindingLocale, StringUtils };
function compile(source, method) {
  return Function(...Object.keys(bindings), 'return function(param1) {' + body(source, method) + '}')(...Object.values(bindings));
}
const price = compile(manager, 'getpriceFromData');
const purchase = compile(manager, 'purchaseCallback');
const checkFunds = compile(manager, 'checkAvailableFunds');
const approval = compile(manager, 'checkForApproval');
const draw = compile(button, 'draw');
let cases = 0;
for (const quantity of [1, 9, 10, 11, 100, 500, 1000]) {
  const expected = Math.min(quantity, 10);
  for (const stack of [false, true]) for (const sale of [false, true]) {
    const selected = stack ? expected : 1;
    const item = { Quantity: quantity, Price: 250, CurrencyPrice: 250, SalePrice: 200, IsOnSale: sale, CurrencyId: 2, Name: 'Test crate' };
    const state = {
      m_currentItemData: item, m_isCurrentItemAStack: stack,
      m_playerInfo: { guid: 'test', walletCurrencyId: -1 },
      m_pageManager: { isAccountPage: true, setPage() {}, showError() { denied++; },
        showConfirmation(value) { confirmation = value; } },
      getCurrentCrateBundleId: () => 3620, getCurrentCrateCurrencyId: () => 2,
      getCurrencyName: () => 'Crowns', checkForZipcode() { allowed++; }
    };
    state.getCurrencyPrice = () => price.call(state, item);
    const total = selected * (sale ? 200 : 250);
    assert.equal(state.getCurrencyPrice(), total);
    added = [];
    purchase.call(state, false);
    assert.equal(added.length, 0, 'Cancelled approval must not create an order');
    purchase.call(state, true);
    assert.deepEqual(added, [[3620, selected, 0, 0, 2]], 'Native order quantity must match the shown total');
    funds = total - 1; allowed = denied = 0;
    checkFunds.call(state);
    assert.equal(denied, 1); assert.equal(allowed, 0);
    funds = total; allowed = denied = 0;
    checkFunds.call(state);
    assert.equal(denied, 0); assert.equal(allowed, 1);
    approval.call(state);
    assert.equal(confirmation[2], total, 'Confirmation must use the actual batch price');
    const display = { m_itemData: item, m_isStack: stack, m_priceTxt: {}, m_discountedPriceTxt: {},
      m_crossOut: {}, m_currencyIcon: { setImageBySetId() {} } };
    draw.call(display);
    assert.equal(display.m_priceTxt.text, String(250 * selected));
    if (sale) assert.equal(display.m_discountedPriceTxt.text, String(total));
    // Exercise the complete original mouse/controller target through wallet
    // preflight and confirmation, then the accepted callback and native order.
    for (const method of ['unlockOneCrate', 'unlockAllCrates', 'validatePurchase', 'checkAvailableFunds', 'checkForZipcode', 'checkForApproval', 'purchaseCallback']) {
      state[method] = compile(manager, method);
    }
    state.getCurrencyPrice = () => price.call(state, item);
    let callback;
    state.m_pageManager.showConfirmation = (value, action) => { confirmation = value; callback = action; };
    funds = total;
    added = [];
    const click = compile(panel, stack ? 'onUnlockStackButtonClick' : 'onUnlockOneButtonClick');
    click.call({ m_manager: state });
    assert.equal(added.length, 0, 'Click must await the original confirmation');
    assert.equal(confirmation[2], total);
    assert.equal(typeof callback, 'function');
    callback.call(state, true);
    assert.deepEqual(added, [[3620, selected, 0, 0, 2]], 'Full click/preflight/confirmation path must place the correct order');
    cases++;
  }
}
assert(panel.includes('"Unlock " + Math.min(10,this.m_itemData.Quantity)'));
assert(!panel.includes('UI.CharacterMenu.AccountInventory.UnlockAll'));
for (const action of ['unlockOneCrate()', 'openOneCrate(this.m_itemData.ItemId)', 'openAllCrates(this.m_itemData.ItemId)']) {
  assert.equal(panel.split(action).length - 1, 2, 'Both mouse and controller paths must preserve ' + action);
}
console.log(`Passed ${cases} quantity/pricing and full click/preflight/confirmation/order cases plus input and cancellation checks from patched AS3.`);
