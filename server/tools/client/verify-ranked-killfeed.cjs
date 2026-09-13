// Execute re-exported notification layout methods with display objects stubbed.
// node verify-ranked-killfeed.cjs <client-build/verify/scripts>
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');

function compile(source) {
  const marker = source.indexOf('override protected function updateUI');
  assert(marker >= 0);
  const start = source.indexOf('{', marker);
  let end = start + 1, depth = 1;
  while (depth) {
    if (source[end] === '{') depth++;
    if (source[end] === '}') depth--;
    assert(end++ < source.length, 'Method braces must balance');
  }
  const body = source.slice(start + 1, end - 1)
    .replace(/var (\w+):(Number|String|ColorTransform)/g, 'var $1')
    .replace(/super\.height/g, '20');
  return new Function('m_notifParams', 'ColorTransform', 'SPACING', 'TIER_BG_PADDING',
    'Constants', 'DynamicImage', 'int', body);
}
function clip(width = 32) {
  return { width, transform: {}, gotoAndStop(frame) { this.frame = frame; } };
}
function textField() {
  return { width: 0, textWidth: 70 };
}
function ColorTransform() { this.color = null; }

let paths = 0;
for (const [name, players] of [
  ['KillNotification', ['killer', 'killed']],
  ['DeathNotification', ['killed']],
  ['AssistNotification', ['killer', 'killed', 'assist']],
]) {
  const source = fs.readFileSync(path.join(process.argv[2], 'views/hudkillfeed', name + '.as'), 'utf8');
  const update = compile(source);
  const widget = { m_bg: { height: 20 }, width: 400, formatTextfield() {} };
  const params = {};
  for (const who of players) {
    widget['m_' + who + 'Name'] = textField();
    widget['m_' + who + 'Tier'] = { m_icon: clip(), m_bg: clip() };
    params[who + 'Name'] = who;
  }
  function refresh(tier, division = 2) {
    for (const who of players) {
      params[who + 'Tier'] = tier;
      params[who + 'Subtier'] = division;
    }
    update.call(widget, params, ColorTransform, 3, 25, {}, {}, Math.trunc);
  }
  for (const tier of [8, 1, 8, 7, 0, 8, 3]) {
    refresh(tier, 1);
    for (const who of players) {
      const rank = widget['m_' + who + 'Tier'];
      const text = widget['m_' + who + 'Name'];
      assert.equal(rank.visible, tier > 0);
      if (tier === 0) continue;
      assert.equal(text.x - rank.x, tier === 8 ? 0 : 41,
        name + ': staff text uses the emblem origin; competitive ranks keep their spacing');
      assert.equal(rank.m_bg.width, text.width + (tier === 8 ? 16 : 57));
      assert.equal(rank.m_icon.visible, tier !== 8);
      assert.equal(rank.m_bg.transform.colorTransform.color, tier === 8 ? 0xD92332 : null);
      if (tier !== 8) assert.equal(rank.m_icon.frame, tier === 7 ? 8 : tier);
    }
  }
  paths += players.length;
}
console.log(`Ranked feed layout verified: ${paths} staff paths, no inset, plate sizing, retail ranks and pooled reuse.`);
