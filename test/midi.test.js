// MIDI 状态机单元测试：不触碰真实硬件/原生模块。
// 通过 stub 掉 require.cache 里的 keyboard（FFI user32）与 midi（RtMidi），
// 让 handleMidiMessage 的按键输出变为可断言的记录数组。
// Unit tests for the MIDI state machine. Native modules are stubbed out of
// require.cache (keyboard's user32 FFI and the RtMidi wrapper) so key output
// becomes an assertable log array.
const assert = require('node:assert/strict');
const test = require('node:test');

// --- Stub keyboard (FFI) --------------------------------------------------
const keyPath = require.resolve('../libs/keyboard');
const keyCalls = [];
const keyStub = {
  VK: { a: 0x41, b: 0x42, c: 0x43, ctrl: 0x11, shift: 0x10, escape: 0x1b },
  sendKey(vk, flags) { keyCalls.push(['send', vk, flags]); },
  sendKeySync(vk, flags) { keyCalls.push(['sync', vk, flags]); },
  getKeyName(vk) {
    const byCode = { 0x41: 'a', 0x42: 'b', 0x43: 'c', 0x11: 'ctrl', 0x10: 'shift', 0x1b: 'escape' };
    return byCode[vk] || ('0x' + vk.toString(16));
  },
};
require.cache[keyPath] = { id: keyPath, filename: keyPath, loaded: true, exports: keyStub };

// --- Stub midi (native RtMidi wrapper) ------------------------------------
const midiPath = require.resolve('midi');
require.cache[midiPath] = { id: midiPath, filename: midiPath, loaded: true, exports: {} };

const midi = require('../extensions/backend/midi');

function makeCtx(noteMap) {
  return {
    noteMap,
    activeNoteChannels: new Map(),
    activeNoteBindings: new Map(),
    activeVkCount: new Map(),
    broadcast() {},
  };
}

function noteOn(ctx, channel, note, velocity = 100) {
  midi.handleMidiMessage(ctx, 0, [0x90 | channel, note, velocity]);
}
function noteOff(ctx, channel, note) {
  midi.handleMidiMessage(ctx, 0, [0x80 | channel, note, 0]);
}
function downKeys() {
  return keyCalls.filter((c) => c[0] === 'send' && c[2] === 0).map((c) => c[1]);
}
function upKeys() {
  return keyCalls.filter((c) => c[0] === 'send' && c[2] === 0x0002).map((c) => c[1]);
}

test('single-channel note on then off presses and releases the mapped key once', () => {
  keyCalls.length = 0;
  const ctx = makeCtx(new Map([[60, [0x41]]])); // note 60 -> 'a'
  noteOn(ctx, 0, 60);
  assert.deepEqual(downKeys(), [0x41]);
  noteOff(ctx, 0, 60);
  assert.deepEqual(upKeys(), [0x41]);
  assert.equal(ctx.activeNoteChannels.size, 0);
  assert.equal(ctx.activeVkCount.size, 0);
  assert.equal(ctx.activeNoteBindings.size, 0);
});

test('duplicate note-on on the same channel is broadcast and ignored', () => {
  keyCalls.length = 0;
  const events = [];
  const ctx = makeCtx(new Map([[60, [0x41]]]));
  ctx.broadcast = (ev) => events.push(ev);
  noteOn(ctx, 0, 60);
  noteOn(ctx, 0, 60);
  assert.ok(events.includes('midiDuplicateOn'));
  assert.deepEqual(downKeys(), [0x41]); // pressed only once
});

test('cross-channel same pitch keeps the key held until every channel releases', () => {
  keyCalls.length = 0;
  const ctx = makeCtx(new Map([[60, [0x41]]]));
  noteOn(ctx, 0, 60); // ch0 presses -> key down
  noteOn(ctx, 1, 60); // ch1 layered voice
  assert.deepEqual(downKeys(), [0x41]); // no second key press
  keyCalls.length = 0;
  noteOff(ctx, 0, 60); // ch0 releases, ch1 still holds
  assert.deepEqual(upKeys(), []); // key must NOT release yet
  noteOff(ctx, 1, 60); // ch1 releases -> fully released
  assert.deepEqual(upKeys(), [0x41]);
  assert.equal(ctx.activeNoteChannels.size, 0);
});

test('note-off after the mapping is removed still releases the pressed key', () => {
  keyCalls.length = 0;
  const ctx = makeCtx(new Map([[60, [0x41]]]));
  noteOn(ctx, 0, 60);
  ctx.noteMap.delete(60); // mapping removed / config hot-switched while held
  noteOff(ctx, 0, 60);
  assert.deepEqual(upKeys(), [0x41]); // stuck-key regression
  assert.equal(ctx.activeVkCount.size, 0);
});

test('unexpected note-off is broadcast without sending key output', () => {
  keyCalls.length = 0;
  const events = [];
  const ctx = makeCtx(new Map([[60, [0x41]]]));
  ctx.broadcast = (ev) => events.push(ev);
  noteOff(ctx, 0, 61); // note never pressed
  assert.ok(events.includes('midiUnexpectedOff'));
  assert.deepEqual(upKeys(), []);
});

test('combo keys are pressed left-to-right and released right-to-left', () => {
  keyCalls.length = 0;
  const ctx = makeCtx(new Map([[60, [0x11, 0x42]]])); // ctrl+b
  noteOn(ctx, 0, 60);
  assert.deepEqual(downKeys(), [0x11, 0x42]);
  noteOff(ctx, 0, 60);
  assert.deepEqual(upKeys(), [0x42, 0x11]);
});

test('shared key across two held notes uses reference counting', () => {
  keyCalls.length = 0;
  // both notes map to the same physical key 'a'
  const ctx = makeCtx(new Map([[60, [0x41]], [61, [0x41]]]));
  noteOn(ctx, 0, 60); // press a (count 1)
  noteOn(ctx, 0, 61); // a already down (count 2) — no extra key-down
  assert.deepEqual(downKeys(), [0x41]);
  keyCalls.length = 0;
  noteOff(ctx, 0, 60); // count back to 1 — key stays down
  assert.deepEqual(upKeys(), []);
  noteOff(ctx, 0, 61); // count 0 — key released
  assert.deepEqual(upKeys(), [0x41]);
});
