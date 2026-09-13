// Exercise re-exported handlers in a JS mock; the live client must verify AVM2 rendering.
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm'), assert = require('node:assert/strict');
const build = process.argv[2];
const source = (asset, file) => fs.readFileSync(path.join(build, asset, 'after/scripts', file), 'utf8');
const window = source('HudGroupVoiceWindow', 'views/hudgroupvoice/HudGroupVoiceWindow.as');
const row = source('HudGroupVoiceWindow', 'views/hudgroupvoice/VoiceItemRenderer.as');
const consoleSource = source('UIRoot', 'views/console/UIConsoleManager.as');
function method(text, name) {
  const at = text.indexOf('function ' + name + '('); assert(at >= 0, name);
  const start = text.indexOf('{', at); let depth = 1, end = start + 1;
  while (depth) { if (text[end] === '{') depth++; if (text[end] === '}') depth--; end++; }
  return text.slice(at, end).replace(/:\s*(?:String|Boolean|Number|int|uint|void|Array|Object|GameEvent|TimerEvent|Error|VoiceItemRenderer|DisplayObject|Point)\b/g, '');
}
const events = [], widgets = {}; let now = 1000;
const context = vm.createContext({getTimer: () => now, int: Math.trunc, uint: x => x >>> 0, events,
  HudGroupVoiceManager: {getInstance: () => ({getVoiceMember: () => null})},
  MainController: {getWidgetManager: () => ({getLoadedWidget: id => widgets[id]})},
  WidgetNames: {HUD_GROUP_VOICE_WINDOW:24, HUD_GROUP_VOICE_WINDOW_TS:93},
  UIBindingSystem: {DispatchWallOfData: (window, action) => events.push({window, action})},
  Point: function(x = 0, y = 0) {this.x = x; this.y = y;},
  GameEvent: function(type) {this.type = type;}, PADDING: 4});
// Scaleform can be built without PCRE: a JS engine's working regex masked this
// native compatibility failure. The speaker parser must work with that stubbed.
vm.runInContext('RegExp.prototype.test = function() { return false; };', context);
function objectFrom(methods, text, initial) {
  const o = initial;
  for (const name of methods) o[name] = vm.runInContext('(' + method(text, name) + ')', context);
  return o;
}
const rows = Array.from({length: 10}, (_, i) => objectFrom(['setCompanion', 'update'], row, {
  m_companion: false, m_companionName: '', m_index: 0, alpha: 1, parent: null,
  y: 420 + i * 26, localToGlobal() {return {x:32, y:440 + this.y};},
  m_playerName: {text: '', textWidth: 80}, m_twitchIcon: {visible: true},
  m_voiceIcon: {frame: 0, currentFrame: 0, gotoAndStop(frame) { this.currentFrame = this.frame = frame; }}
}));
const hud = objectFrom(['clearCompanionVoice','receiveCompanionVoice','isCompanionVoiceId','layoutCompanionVoice','expireCompanionVoice','reportCompanionVoice'], window, {
  m_voiceMembers: rows, m_voiceReceivedAt: -10000, m_voiceReport: ''
});
hud.clearCompanionVoice(); assert(rows.every(r => !r.visible));
for (const value of ['1','0','18446744073709551615']) assert(hud.isCompanionVoiceId(value));
for (const value of [null,'','-1','+1','1.5','1e3',' 1','1\n','１２','123456789012345678901']) assert(!hud.isCompanionVoiceId(value));
const receive = text => hud.receiveCompanionVoice(text);
receive('@cranberry/voice/1;18446744073709551614|Samuel;2|A%20%7C%20B%3B%20%F0%9F%8E%99');
assert.equal(rows[0].m_playerName.text, 'Samuel'); assert.equal(rows[0].m_voiceIcon.frame, 1);
assert.equal(rows[1].m_playerName.text, 'A | B; 🎙'); assert(!rows[2].visible);
assert(!rows[0].m_twitchIcon.visible);
assert.equal(rows[0].y, -58); assert.equal(rows[1].y, -32);
// Native empty datasource events must not erase a companion speaker row.
rows[0].update(); assert(rows[0].visible);
now = 2200; hud.expireCompanionVoice({}); assert(rows[0].visible);
now = 2300; hud.expireCompanionVoice({}); assert(rows.every(r => !r.visible));
receive('@cranberry/voice/1;1|First;1|Duplicate;2|%zz;NaN|Invalid;3|<b>Literal</b>');
assert.equal(rows[0].m_playerName.text, 'First'); assert.equal(rows[1].m_playerName.text, '<b>Literal</b>');
assert(!rows[2].visible); // Plain .text, never markup.
receive('@cranberry/voice/1;' + Array.from({length: 15}, (_,i) => `${i+1}|Player${i+1}`).join(';'));
assert.equal(rows.filter(r => r.visible).length, 10);
assert.equal(rows[9].y, -32); assert.equal(rows[0].y, -266);
receive('@cranberry/voice/1;'); assert(rows.every(r => !r.visible));
receive('@cranberry/social/1;1|WrongChannel'); assert(rows.every(r => !r.visible));
hud.receiveCompanionVoice(null);
events.length = 0;
const consoleHandler = objectFrom(['handlePrintConsole','deliverCompanionVoice'], consoleSource, {m_voiceRouteStatus:''});
// The console calls the actual loaded widget, with no custom Stage event dependency.
widgets[24] = hud;
consoleHandler.handlePrintConsole({data:['@cranberry/voice/1;1|Samuel']});
assert(rows[0].visible); assert.equal(rows[0].m_playerName.text, 'Samuel');
assert.equal(events.at(-1).action, 'route:ready');
assert.equal(rows[0].y, -32);
assert(events.some(e => e.action === 'render:1:6:1:1:1:32:408'));
events.length = 0;
consoleHandler.handlePrintConsole({data:['@cranberry/voice/1;1|Samuel']});
assert.equal(events.length, 0, 'Unchanged refreshes do not spam diagnostic replies');
delete widgets[24]; widgets[93] = hud;
consoleHandler.handlePrintConsole({data:['@cranberry/voice/1;2|Spectator']});
assert.equal(rows[0].m_playerName.text, 'Spectator');
delete widgets[93];
consoleHandler.handlePrintConsole({data:['@cranberry/voice/1;']});
assert.equal(events.at(-1).action, 'route:missing');
widgets[24] = {receiveCompanionVoice() {throw {errorID:1234};}};
consoleHandler.handlePrintConsole({data:['@cranberry/voice/1;1|Samuel']});
assert.equal(events.at(-1).action, 'route:error:1234');
for (const token of ['"CRANBERRY_VOICE_HUD_V1","open"','"CRANBERRY_VOICE_HUD_V1","close"']) assert(window.includes(token), token);
assert(!window.includes('"CranberryVoiceHud"'));
console.log('Re-exported voice handlers: parsing, native-row calls, expiry, direct normal/spectator widget delivery, and bounded diagnostic acknowledgments passed. Actual AVM2 rendering requires live validation.');
