const midi = require('midi');
const path = require('path');
const { version } = require('./package.json');
const { sendKey, getKeyName } = require('./libs/keyboard');
const { loadConfig } = require('./libs/config');

// 报错时暂停，等待用户按键后退出
function pauseAndExit(code = 1) {
  console.log('MIDITap exited. Press any key to close...');
  process.stdin.setRawMode(true);
  process.stdin.resume();
  process.stdin.once('data', () => process.exit(code));
}

// Dev 环境指定配置文件路径
const baseDir = path.basename(process.execPath).startsWith('node')
  ? __dirname
  : path.dirname(process.execPath);

// Load Config
const noteMap = loadConfig(baseDir);
if (!noteMap) { pauseAndExit(); return; }

// Initializing MIDI
const input = new midi.Input();
const portCount = input.getPortCount();
for (let i = 0; i < portCount; i++) console.log(`MIDI Device Found: Port ${i} (${input.getPortName(i)})`);
if (portCount === 0) {
  console.error('MIDI Device Not Found');
  pauseAndExit();
  return;
}

// 打开 MIDI 端口
// 多设备切换功能待实现
input.openPort(0);
input.ignoreTypes(true, true, true);

// 监听
input.on('message', (deltaTime, message) => {
  const status = message[0] & 0xF0;
  const note = message[1];
  const velocity = message[2];
  const vk = noteMap.get(note);

  if (status === 0x90 && velocity > 0) {
    // Note ON
    const keyInfo = vk !== undefined ? `, Key: '${getKeyName(vk)}'` : ' (unbound)';
    console.log(`Note ON: ${note}, Velocity: ${velocity}${keyInfo}`);
    if (vk === undefined) return;
    sendKey(vk, 0);
  } else if (status === 0x80 || (status === 0x90 && velocity === 0)) {
    // Note OFF（包含力度为 0 的 ON 状态）
    const keyInfo = vk !== undefined ? `, Key: '${getKeyName(vk)}'` : ' (unbound)';
    console.log(`Note OFF: ${note}${keyInfo}`);
    if (vk === undefined) return;
    sendKey(vk, 0x0002);
  }
});

console.log(`MIDITap v${version} is running, press Ctrl+C to exit.`);

// Ctrl+C
process.on('SIGINT', () => { 
  input.closePort();
  process.stdin.setRawMode(false);
  process.exit(0); 
});