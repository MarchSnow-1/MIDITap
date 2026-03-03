const midi = require('midi');
const path = require('path');
const { version } = require('./package.json');
const { sendKey, getKeyName } = require('./libs/keyboard');
const { loadConfig } = require('./libs/config');
const minimist = require('minimist');

// 解析命令行参数：
// - --port <number>：手动指定 MIDI 端口号（优先级高于配置文件）
// - --verbose / -v：输出详细日志（包括每条 Note ON/OFF）
const args = minimist(process.argv.slice(2), {
  alias: { v: 'verbose' },
  boolean: ['verbose'],
});
const verbose = args.verbose === true;

// 统一的退出辅助函数：
// 遇到启动失败或配置错误时，保持窗口不立即关闭，方便用户看到报错信息。
function pauseAndExit(code = 1) {
  console.log('MIDITap exited. Press any key to close...');
  process.stdin.setRawMode(true);
  process.stdin.resume();
  process.stdin.once('data', () => process.exit(code));
}

// 兼容两种运行方式的配置目录计算：
// 1. node index.js（开发态） => 使用项目目录 __dirname
// 2. 打包后的 MIDITap.exe（发布态） => 使用可执行文件所在目录
const baseDir = path.basename(process.execPath).startsWith('node')
  ? __dirname
  : path.dirname(process.execPath);

// 读取并解析配置文件（mapping.json），返回：
// - noteMap: MIDI note -> 虚拟键码（VK）映射
// - port: 配置文件中可选的默认端口号
const configResult = loadConfig(baseDir, { verbose });
if (!configResult) {
  pauseAndExit();
  return;
}
const { noteMap, port: configPort } = configResult;

// 端口选择优先级：CLI --port > 配置文件 port > 默认 0
const selectedPort = args.port !== undefined ? Number(args.port) : (configPort !== null ? configPort : 0);

// 启动前进行严格端口参数校验，避免 NaN、负数等非法输入继续执行。
if (!Number.isInteger(selectedPort) || selectedPort < 0) {
  console.error(`Invalid port "${args.port ?? configPort}". Expected a non-negative integer.`);
  pauseAndExit();
  return;
}

const portIndex = selectedPort;

// 初始化 MIDI 输入对象并查询系统中可用的 MIDI 输入端口数量。
const input = new midi.Input();
const portCount = input.getPortCount();

// 没有可用 MIDI 设备时直接退出。
if (portCount === 0) {
  console.error('MIDI Device Not Found');
  pauseAndExit();
  return;
}

// 端口号越界时给出明确范围提示。
if (portIndex >= portCount) {
  console.error(`Port ${portIndex} not found, available: 0 ~ ${portCount - 1}`);
  pauseAndExit();
  return;
}

// 打印所有可用端口，便于用户确认设备和端口序号。
for (let i = 0; i < portCount; i++) {
  const selected = i === portIndex ? ' <-- selected' : '';
  console.log(`Port ${i}: ${input.getPortName(i)}${selected}`);
}

// 打开目标端口并忽略 SysEx / Timing / Active Sensing 三类消息，
// 仅保留核心 MIDI 通道消息，减少不必要事件干扰。
input.openPort(portIndex);
input.ignoreTypes(true, true, true);

// 监听 MIDI 消息：
// message 典型格式为 [status, note, velocity]
// - status 高 4 位表示消息类型（0x90=Note On, 0x80=Note Off）
// - note 是音符编号（0~127）
// - velocity 是力度（0~127）
input.on('message', (deltaTime, message) => {
  // 屏蔽 MIDI 通道号，仅提取消息类型：
  // 例如 0x91（通道2 Note On）与 0xF0 后得到 0x90。
  const status = message[0] & 0xF0;
  const note = message[1];
  const velocity = message[2];

  // 兼容两种“抬键”语义：
  // 1) 标准 Note Off（0x80）
  // 2) Note On 且 velocity=0（许多设备会这样发送）
  const isNoteOn = status === 0x90 && velocity > 0;
  const isNoteOff = status === 0x80 || (status === 0x90 && velocity === 0);

  // 仅处理音符按下/抬起事件，其它 MIDI 消息直接忽略。
  if (!isNoteOn && !isNoteOff) return;

  // 查询当前音符是否已配置映射键位。
  const vk = noteMap.get(note);

  // 详细日志默认关闭，仅在 --verbose 下输出，避免高频日志影响性能。
  if (verbose) {
    const keyName = vk !== undefined ? getKeyName(vk) : undefined;
    const keyInfo = keyName ? `, Key: '${keyName}'` : ' (unbound)';
    if (isNoteOn) {
      console.log(`Note ON: ${note}, Velocity: ${velocity}${keyInfo}`);
    } else {
      console.log(`Note OFF: ${note}${keyInfo}`);
    }
  }

  // 未绑定键位时不发送键盘事件。
  if (vk === undefined) return;

  // Note On -> 键按下（flags=0）
  // Note Off -> 键抬起（KEYEVENTF_KEYUP=0x0002）
  sendKey(vk, isNoteOn ? 0 : 0x0002);
});

console.log(`MIDITap v${version} is running, press Ctrl+C to exit.`);

// 手动接管 Ctrl+C：
// 在退出前主动关闭 MIDI 端口，避免设备占用状态残留。
process.stdin.setRawMode(true);
process.stdin.resume();
process.stdin.on('data', (key) => {
  if (key[0] === 0x03) {
    input.closePort();
    process.exit(0);
  }
});
