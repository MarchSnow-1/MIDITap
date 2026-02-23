const midi = require('midi');
const fs = require('fs');
const path = require('path');
const JSON5 = require('json5');
const koffi = require('koffi');

// SendInput Buffer 填字节
const user32 = koffi.load('user32.dll');
const kernel32 = koffi.load('kernel32.dll');
const SendInput = user32.func('uint32 __stdcall SendInput(uint32 nInputs, uint8 *pInputs, int cbSize)');
const GetTickCount = kernel32.func('uint32 __stdcall GetTickCount()');

function makeKeyBuffer(vkCode, flags) {
  const buf = Buffer.alloc(40);
  buf.writeUInt32LE(1);   // type = 1 (keyboard)
  // union 从 offset 8 开始（x64 对齐）
  buf.writeUInt16LE(vkCode, 8); // wVk
  buf.writeUInt16LE(0, 10); // wScan
  buf.writeUInt32LE(flags, 12); // dwFlags
  buf.writeUInt32LE(GetTickCount(), 16);  // 每次调用时重新取，时间戳准确
  return buf;
}

function buildInputPair(vkCode) {
  return Buffer.concat([
    makeKeyBuffer(vkCode, 0),
    makeKeyBuffer(vkCode, 0x0002),
  ]);
}

// VK Code List

const VK = {
  // 字母
  'a':0x41,'b':0x42,'c':0x43,'d':0x44,'e':0x45,'f':0x46,'g':0x47,'h':0x48,
  'i':0x49,'j':0x4A,'k':0x4B,'l':0x4C,'m':0x4D,'n':0x4E,'o':0x4F,'p':0x50,
  'q':0x51,'r':0x52,'s':0x53,'t':0x54,'u':0x55,'v':0x56,'w':0x57,'x':0x58,
  'y':0x59,'z':0x5A,

  // 数字行
  '0':0x30,'1':0x31,'2':0x32,'3':0x33,'4':0x34,
  '5':0x35,'6':0x36,'7':0x37,'8':0x38,'9':0x39,

  // 功能键
  'f1':0x70,'f2':0x71,'f3':0x72,'f4':0x73,'f5':0x74,'f6':0x75,
  'f7':0x76,'f8':0x77,'f9':0x78,'f10':0x79,'f11':0x7A,'f12':0x7B,
  'f13':0x7C,'f14':0x7D,'f15':0x7E,'f16':0x7F,'f17':0x80,'f18':0x81,
  'f19':0x82,'f20':0x83,'f21':0x84,'f22':0x85,'f23':0x86,'f24':0x87,

  // 控制键
  'backspace':0x08,'tab':0x09,'enter':0x0D,'shift':0x10,'ctrl':0x11,
  'alt':0x12,'pause':0x13,'capslock':0x14,'escape':0x1B,'space':0x20,

  // 导航键
  'pageup':0x21,'pagedown':0x22,'end':0x23,'home':0x24,
  'left':0x25,'up':0x26,'right':0x27,'down':0x28,
  'insert':0x2D,'delete':0x2E,

  // 修饰键
  'lshift':0xA0,'rshift':0xA1,
  'lctrl':0xA2,'rctrl':0xA3,
  'lalt':0xA4,'ralt':0xA5,
  'lwin':0x5B,'rwin':0x5C,

  // 小键盘
  'num0':0x60,'num1':0x61,'num2':0x62,'num3':0x63,'num4':0x64,
  'num5':0x65,'num6':0x66,'num7':0x67,'num8':0x68,'num9':0x69,
  'multiply':0x6A,   // 小键盘 *
  'add':0x6B,        // 小键盘 +
  'separator':0x6C,  // 小键盘 Enter（部分键盘）
  'subtract':0x6D,   // 小键盘 -
  'decimal':0x6E,    // 小键盘 .
  'divide':0x6F,     // 小键盘 /
  'numlock':0x90,

  // 美式键盘标点
  'semicolon':0xBA,      // ;:
  'equal':0xBB,          // =+
  'comma':0xBC,          // ,
  'minus':0xBD,          // -_
  'period':0xBE,         // .>
  'slash':0xBF,          // /?
  'backquote':0xC0,      // `~
  'lbracket':0xDB,       // [{
  'backslash':0xDC,      // \|
  'rbracket':0xDD,       // ]}
  'quote':0xDE,          // '"

  // 系统
  'printscreen':0x2C,'scrolllock':0x91,'apps':0x5D,

  // 媒体
  'mute':0xAD,'volumedown':0xAE,'volumeup':0xAF,
  'nexttrack':0xB0,'prevtrack':0xB1,'stop':0xB2,'playpause':0xB3,

  // Other
  'select':0x29,'print':0x2A,'execute':0x2B,'help':0x2F,
  'sleep':0x5F,
};

// 加载配置

const baseDir = path.basename(process.execPath).startsWith('node')
  ? __dirname
  : path.dirname(process.execPath);

const configPath = path.join(baseDir, 'config', 'mapping.json');
let rawMapping = {};
try {
  rawMapping = JSON5.parse(fs.readFileSync(configPath, 'utf8'));
  console.log('Config Loaded:', rawMapping);
} catch (err) {
  console.error('Failed to Load Config:', err.message);
  process.exit(1);
}

// noteMap 存 vkCode 和 flags，发送时现构建 buffer
const noteMap = new Map();
for (const [noteStr, keyChar] of Object.entries(rawMapping)) {
  const vk = VK[keyChar.toLowerCase()];
  if (vk === undefined) {
    console.warn(`Can't find '${keyChar}' in VK Code List, Skipping...`);
    continue;
  }
  noteMap.set(parseInt(noteStr), vk);  // 存 vkCode
  console.log(`note ${noteStr} -> '${keyChar}' (VK=0x${vk.toString(16).toUpperCase()})`);
}

// MIDI 输入

const input = new midi.Input();
const portCount = input.getPortCount();
for (let i = 0; i < portCount; i++) console.log(`Port ${i}: ${input.getPortName(i)}`);
if (portCount === 0) { console.error('MIDI Device Not Found'); process.exit(1); }

input.openPort(0);
input.ignoreTypes(true, true, true);

input.on('message', (deltaTime, message) => {
  const status = message[0] & 0xF0;
  const note = message[1];
  const velocity = message[2];

  const vk = noteMap.get(note);

  if (status === 0x90 && velocity > 0) {
    const keyInfo = vk !== undefined ? `, Key: '${Object.keys(VK).find(k => VK[k] === vk)}'` : ' (unbound)';
    console.log(`Note ON: ${note}, Velocity: ${velocity}${keyInfo}`);
    if (vk === undefined) return;
    SendInput.async(1, makeKeyBuffer(vk, 0), 40, (err, ret) => {
      if (err) console.error('SendInput Error:', err);
      else if (ret === 0) console.error('SendInput Return 0');
    });
  } else if (status === 0x80 || (status === 0x90 && velocity === 0)) {
    const keyInfo = vk !== undefined ? `, Key: '${Object.keys(VK).find(k => VK[k] === vk)}'` : ' (unbound)';
    console.log(`Note OFF: ${note}${keyInfo}`);
    if (vk === undefined) return;
    SendInput.async(1, makeKeyBuffer(vk, 0x0002), 40, (err, ret) => {
      if (err) console.error('SendInput Error:', err);
      else if (ret === 0) console.error('SendInput Return 0');
    });
  }
});

console.log('MIDITap is running, press Ctrl+C to exit.');
process.on('SIGINT', () => { input.closePort(); process.exit(0); });