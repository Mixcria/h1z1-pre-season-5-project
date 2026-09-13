// Execute the re-exported native initialize() with fresh/existing first-time settings.
// A control run against the source must reproduce the old Appearance landing.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');

function initializeBody(path) {
  const script = fs.readFileSync(path, 'utf8');
  const at = script.indexOf('public function initialize(param1:Stage) : void');
  assert(at >= 0, 'Native initialize method is present');
  const start = script.indexOf('{', at);
  let depth = 1, end = start + 1;
  for (; depth > 0 && end < script.length; end++) {
    if (script[end] === '{') depth++;
    if (script[end] === '}') depth--;
  }
  assert.equal(depth, 0);
  return script.slice(start + 1, end - 1)
    .replace(/new Vector\.<int>\(\)/g, '[]')
    .replace(/ as Boolean/g, '');
}

function run(body, firstTime) {
  const shared = {};
  const routes = [];
  const inert = new Proxy(function () { return inert; }, {
    get(target, key) { return key === Symbol.toPrimitive ? () => 'binding' : inert; },
    construct() { return {}; },
  });
  const known = {
    Boolean,
    SharedGlobalData: { GetInstance: () => shared },
    UIBindingSettings: { GetFirstTimeEventEnabled: () => firstTime },
    UIBindingSteam: { SteamIsEnabled: () => true },
    UIBindingSystem: { IsAdmin: () => false, isController: () => false },
    UIBindingInput: { GetUsePs4ControlEmulation: () => false, GetUseXb1ControlEmulation: () => false },
    MenuItemId: { ROOT: 'root', ROOT_NPX: 'first-time' },
  };
  const scope = new Proxy(known, {
    has(target, key) { return key !== 'param1'; },
    get(target, key) { return key === Symbol.unscopables ? undefined : target[key] ?? inert; },
  });
  const manager = { m_isInitalized: false, m_bindings: {}, setMenuById: (...args) => routes.push(args) };
  const context = vm.createContext({ scope, manager, stage: { addEventListener() {} } });
  vm.runInContext('(function(param1) { with (scope) {' + body + '} }).call(manager, stage)', context, { timeout: 1000 });
  assert.equal(manager.m_isInitalized, true);
  assert.equal(routes.length, 1);
  return routes[0];
}

const [sourcePath, builtPath] = process.argv.slice(2);
assert(sourcePath && builtPath, 'Pass source and verified UiMainMenuManager.as');
const source = initializeBody(sourcePath), built = initializeBody(builtPath);
assert.deepEqual(run(source, true), ['first-time', 0, 'kotkappearancefte']);
assert.deepEqual(run(source, false), ['root', 0, 'kotkdefault']);
assert.deepEqual(run(built, true), ['root', 0, 'kotkdefault']);
assert.deepEqual(run(built, false), ['root', 0, 'kotkdefault']);
console.log('PASS native menu initialize: source reproduces Appearance; patched fresh/existing clients both choose main menu.');
