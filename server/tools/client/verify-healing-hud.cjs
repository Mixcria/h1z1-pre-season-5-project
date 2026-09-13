// Exercise the compiled and re-exported August EffectView, not a copy of its logic.
const fs = require('node:fs'), assert = require('node:assert/strict');
const source = fs.readFileSync(process.argv[2], 'utf8');
const original = fs.readFileSync(process.argv[3], 'utf8');
let active = 'native', now = 0, queries = 0;
const scope = {
  String, getTimer: () => now, Event: { ENTER_FRAME: 'frame' },
  ResourceManager: { EFFECT_TAG_UPDATE: 'effect' },
  UIBindingSystem: { GetStringHashValue(key, fallback) {
    assert.equal(key, 'Cranberry.Healing'); assert.equal(fallback, 'native'); queries++; return active;
  } },
  HEALING: 'Healing', BLEEDING: 'Bleeding', MEDKIT_ID: 120581, BANDAGE_ID: 120583,
  BLEEDING_CRITICAL: 120114, BLEEDING_HEAVY: 120112, BLEEDING_LIGHT: 120107,
  BLEEDING_MODERATE: 120111, BLEEDING_SEVERE: 120113, EffectType: { BLEEDING_MINOR: 'minor' }
};
function method(text, name) {
  const at = text.indexOf(`function ${name}(`); assert(at >= 0, name);
  const start = text.indexOf('{', at); let end = start + 1, depth = 1;
  while (depth) { assert(end < text.length); depth += (text[end] === '{') - (text[end] === '}'); end++; }
  return {
    params: text.slice(text.indexOf('(', at) + 1, text.indexOf(')', at)).replace(/:\w+/g, ''),
    body: text.slice(start + 1, end - 1).replace(/\bvar (\w+):\w+/g, 'var $1')
  };
}
const tags = new Set(), frames = [], listeners = new Set();
const view = {
  m_lastHealingCheck: -250, m_lastHealingValue: '', m_type: '',
  m_manager: { hasEffectTagById: id => tags.has(id), hasEffectTag: id => tags.has(id),
    addEventListener() {}, removeEventListener() {} },
  gotoAndStop: frame => frames.push(frame),
  addEventListener: (event, fn) => listeners.add(fn),
  removeEventListener: (event, fn) => listeners.delete(fn)
};
for (const name of ['init', 'handleEffectTagUpdate', 'updateHealing', 'updateBleeding', 'pollHealing', 'cleanup']) {
  const m = method(source, name);
  view[name] = new Function(...Object.keys(scope), `return function(${m.params}) { ${m.body} }`)(...Object.values(scope));
}
assert.equal(method(source, 'updateBleeding').body.replace(/\s/g, ''),
  method(original, 'updateBleeding').body.replace(/\s/g, ''), 'bleeding behavior preserved');
view.init('Healing'); assert(listeners.has(view.pollHealing)); assert.equal(frames.at(-1), 1);
for (const [value, frame] of [['120583', 2], ['120581', 3], ['0', 1]]) {
  active = value; now += 250; view.pollHealing({}); assert.equal(frames.at(-1), frame);
}
const before = queries; now += 100; view.pollHealing({}); assert.equal(queries, before, '250ms throttle');
const draws = frames.length; now += 150; view.pollHealing({}); assert.equal(frames.length, draws, 'no repeat redraw');
active = 'native'; tags.add(120581); view.updateHealing(); assert.equal(frames.at(-1), 3);
tags.clear(); tags.add(120583); view.updateHealing(); assert.equal(frames.at(-1), 2);
tags.clear(); view.updateHealing(); assert.equal(frames.at(-1), 1);
view.cleanup(); assert.equal(listeners.size, 0);
view.init('Bleeding'); assert.equal(listeners.size, 0, 'bleeding has no healing poll'); view.cleanup();
console.log('PASS: compiled healing frames, native fallback, polling throttle, cleanup, unchanged bleeding.');
