// Execute the actual re-exported AS3 label method with native bindings stubbed.
// node verify-world-display-label.cjs <client-build/verify/scripts/.../HudCompassWindow.as>
const fs = require('node:fs');
const assert = require('node:assert/strict');
const source = fs.readFileSync(process.argv[2], 'utf8');
const marker = 'private function updateClientVersionVisibility';
const start = source.indexOf('{', source.indexOf(marker));
assert(start > 0, 'Re-exported HUD must contain the label method');
let end = start + 1, depth = 1;
while (depth) {
  if (source[end] === '{') depth++;
  if (source[end] === '}') depth--;
  end++;
  assert(end < source.length, 'Method braces must balance');
}
const body = source.slice(start + 1, end - 1).replace('var worldName:String', 'var worldName');
const update = new Function('UIBindingSystem', 'UIBindingSettings', 'TextFieldAutoSize', body);
let worldName = '', versionEnabled = true, invitational = false;
const bindings = {
  GetStringHashValue(key, fallback) {
    assert.equal(key, 'Cranberry.WorldDisplayName');
    assert.equal(fallback, '', 'String default selects the native string return branch');
    return worldName;
  },
  GetClientVersion: () => '0.0.118.208059',
  InInvitational: () => invitational,
};
const settings = { GetClientVersionDisplayEnabled: () => versionEnabled };
const widget = { m_clientVersion: {}, m_serverName: {} };
function refresh() { update.call(widget, bindings, settings, { RIGHT: 'right' }); }
for (const label of ['Solo EU World 1', 'Duos EU World 1', 'Fives EU World 2', 'Hosted Games']) {
  worldName = label;
  refresh();
  assert.equal(widget.m_clientVersion.text, label);
  assert.equal(widget.m_clientVersion.visible, true);
  assert.equal(widget.m_clientVersion.autoSize, 'right');
  assert.equal(widget.m_serverName.visible, false, 'The old localized server name is hidden');
}
versionEnabled = false;
invitational = true;
refresh();
assert.equal(widget.m_clientVersion.text, 'Hosted Games');
assert.equal(widget.m_clientVersion.visible, true, 'Hosted labels work with hidden build versions');
worldName = '';
refresh();
assert.equal(widget.m_clientVersion.visible, false);
invitational = false;
versionEnabled = true;
refresh();
assert.equal(widget.m_clientVersion.text, 'v0.0.118.208059');
assert.equal(widget.m_serverName.visible, true);
assert.equal(widget.m_clientVersion.visible, true);
assert(source.indexOf('this.updateClientVersionVisibility();', source.indexOf('private function updatePosition')) > 0);
const spectator = source.slice(source.indexOf('private function updatePosition'), source.indexOf('private function getPos'));
assert(spectator.includes('this.m_clientVersion.visible = false;'), 'Spectator privacy remains in place');
console.log('World label re-exported HUD behavior verified (modes, world changes, hosted, fallback, alignment, spectator).');
