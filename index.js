const midi = require('midi');
const fs = require('fs');
const path = require('path');
const { version } = require('./package.json');
const { sendKey, sendKeySync, getKeyName } = require('./libs/keyboard');
const { loadConfig } = require('./libs/config');
const minimist = require('minimist');

// 统一处理命令行参数：
// - 对外约定使用单横杠长参数（例如 -config / -check-config / -list-ports）
// - 为了兼容 minimist 的长参数解析，内部会把 `-xxx` 归一化为长参数格式
// - 单字符短参数（如 -v / -h / -c）与负数（如 -1）保持原样
function normalizeCliArgs(argv) {
  // 这些参数在“不带 = 的情况下”需要读取下一个 token 作为值。
  // 这里使用“参数名”本身，而不是具体前缀，便于同时兼容 -config 与 --config 两种输入。
  const optionsExpectValue = new Set(['port', 'config', 'c']);
  const normalized = [];
  let shouldTreatNextAsValue = false;

  // 将单个参数标准化并提取参数名，返回：
  // - normalizedArg: 归一化后的参数
  // - keyName: 参数名（若无法识别则为 null）
  // - hasInlineValue: 是否是 key=value 形式
  function normalizeSingleArg(arg) {
    if (!arg.startsWith('-') || /^-\d/.test(arg)) {
      return { normalizedArg: arg, keyName: null, hasInlineValue: false };
    }

    // 已经是双横杠长参数：直接保留。
    const longMatch = arg.match(/^--([a-zA-Z][a-zA-Z0-9-]*)(=.*)?$/);
    if (longMatch) {
      return {
        normalizedArg: arg,
        keyName: longMatch[1],
        hasInlineValue: Boolean(longMatch[2]),
      };
    }

    // 单字符短参数（例如 -v / -h / -c）：保持原样。
    const shortMatch = arg.match(/^-([a-zA-Z])$/);
    if (shortMatch) {
      return {
        normalizedArg: arg,
        keyName: shortMatch[1],
        hasInlineValue: false,
      };
    }

    // 单横杠长参数（例如 -config / -check-config / -config=path）：
    // 统一转换为长参数格式，以便 minimist 稳定解析。
    const singleDashLongMatch = arg.match(/^-([a-zA-Z][a-zA-Z0-9-]*)(=.*)?$/);
    if (singleDashLongMatch) {
      const keyName = singleDashLongMatch[1];
      const suffix = singleDashLongMatch[2] || '';
      return {
        normalizedArg: `--${keyName}${suffix}`,
        keyName,
        hasInlineValue: Boolean(singleDashLongMatch[2]),
      };
    }

    return { normalizedArg: arg, keyName: null, hasInlineValue: false };
  }

  for (const arg of argv) {
    // 若上一参数需要“下一个值”，则本项必须按值透传，避免误判成新参数。
    // 例如：-config -my-file.json 中，-my-file.json 是路径值而不是参数名。
    if (shouldTreatNextAsValue) {
      normalized.push(arg);
      shouldTreatNextAsValue = false;
      continue;
    }

    const {
      normalizedArg,
      keyName,
      hasInlineValue,
    } = normalizeSingleArg(arg);
    normalized.push(normalizedArg);

    // 当前参数若需要值且不是 key=value 形式，则将下一项视作值。
    if (!hasInlineValue && keyName !== null && optionsExpectValue.has(keyName)) {
      shouldTreatNextAsValue = true;
    }
  }

  return normalized;
}

// 打印 CLI 帮助并退出：
// 1) 先输出中文说明，便于中文用户快速上手
// 2) 再输出英文说明，便于跨语言使用与分发
function printHelp() {
  console.log(`MIDITap v${version}

[中文]
用法:
  MIDITap.exe [选项]
  node index.js [选项]

参数:
  -port <index>             指定 MIDI 端口号（非负整数）
  -list-ports               列出所有 MIDI 输入端口并退出
  -config <path>            指定配置文件路径（支持绝对/相对路径）
  -check-config             严格校验配置并输出 true/false
  -verbose, -v              输出详细日志（devmode 下会强制开启）
  -help, -h                 显示帮助

配置文件选择优先级:
  1) 传入 -config：使用指定文件
  2) 若存在 .dev：默认使用 config/mapping-dev.json
  3) 否则默认使用 config/mapping.json

校验模式退出码:
  true  => exit 0
  false => exit 1

按键映射提示:
  单键: enter
  组合键: ctrl+shift+esc
  Esc 别名: esc = escape

示例:
  MIDITap.exe -list-ports
  MIDITap.exe -port 1
  MIDITap.exe -config .\\config\\mapping.json
  MIDITap.exe -config C:\\path\\to\\mapping.json
  MIDITap.exe -check-config
  MIDITap.exe -check-config -config .\\config\\mapping-dev.json

[English]
Usage:
  MIDITap.exe [options]
  node index.js [options]

Options:
  -port <index>             Select MIDI port index (non-negative integer)
  -list-ports               List all MIDI input ports and exit
  -config <path>            Specify config file path (absolute/relative)
  -check-config             Strict config validation, print true/false
  -verbose, -v              Enable verbose logs (forced in devmode)
  -help, -h                 Show help

Config Resolution Priority:
  1) If -config is provided: use that file
  2) If .dev exists: default to config/mapping-dev.json
  3) Otherwise: default to config/mapping.json

Check Mode Exit Code:
  true  => exit 0
  false => exit 1

Key Mapping Tips:
  Single key: enter
  Combo key: ctrl+shift+esc
  Esc alias: esc = escape

Examples:
  MIDITap.exe -list-ports
  MIDITap.exe -port 1
  MIDITap.exe -config .\\config\\mapping.json
  MIDITap.exe -config C:\\path\\to\\mapping.json
  MIDITap.exe -check-config
  MIDITap.exe -check-config -config .\\config\\mapping-dev.json
`);
}

// 解析命令行参数：
// - -port <number>：手动指定 MIDI 端口号（优先级高于配置文件）
// - -list-ports：仅列出当前可用 MIDI 输入端口并退出
// - -verbose / -v：输出详细日志（包括每条 Note ON/OFF）
// - -check-config：仅校验配置文件并输出 true/false
// - -config：指定配置文件（支持绝对路径与相对路径）
const args = minimist(normalizeCliArgs(process.argv.slice(2)), {
  alias: { v: 'verbose', c: 'config', h: 'help' },
  boolean: ['verbose', 'check-config', 'help', 'list-ports'],
  string: ['config'],
});

if (args.help) {
  printHelp();
  process.exit(0);
}

// 仅列举 MIDI 输入端口：
// - 不依赖配置文件
// - 可用于快速确认端口序号与设备名称
function listPortsAndExit() {
  const listInput = new midi.Input();
  const listPortCount = listInput.getPortCount();

  if (listPortCount === 0) {
    console.log('No MIDI input ports found.');
    process.exit(0);
  }

  console.log(`MIDI input ports (${listPortCount}):`);
  for (let i = 0; i < listPortCount; i++) {
    console.log(`Port ${i}: ${listInput.getPortName(i)}`);
  }
  process.exit(0);
}

if (args['list-ports']) {
  listPortsAndExit();
}

const cliVerbose = args.verbose === true;
const checkConfigOnly = args['check-config'] === true;

// 运行期状态追踪：
// - input: 已打开的 MIDI 输入实例（用于退出时安全关闭）
// - activeNotes: 当前处于按下态的 MIDI 音符编号（用于防重复 Note ON）
// - activeNoteBindings: 每个音符当前激活的键位序列（用于 Note OFF 与退出清理）
// - activeVkCount: 每个 VK 的按下引用计数（支持不同音符共享同一按键）
let input = null;
const activeNotes = new Set();
const activeNoteBindings = new Map();
const activeVkCount = new Map();
let hasCleanupRun = false;

// 增加 VK 按下计数：
// - Note ON 成功下发后调用
// - 同一 VK 可被多个音符共同“持有”
function increaseActiveVkCount(vkCodes) {
  for (const vkCode of vkCodes) {
    activeVkCount.set(vkCode, (activeVkCount.get(vkCode) ?? 0) + 1);
  }
}

// 减少 VK 按下计数：
// - Note OFF 成功下发后调用
// - 计数归零时表示该 VK 不再被任何音符持有
function decreaseActiveVkCount(vkCodes) {
  for (const vkCode of vkCodes) {
    const nextCount = (activeVkCount.get(vkCode) ?? 0) - 1;
    if (nextCount > 0) {
      activeVkCount.set(vkCode, nextCount);
    } else {
      activeVkCount.delete(vkCode);
    }
  }
}

// 退出前全量抬键：
// - 使用同步 SendInput，降低进程退出过快导致 keyup 丢失的风险
// - 仅对“当前仍处于按下态”的 VK 发送一次抬起事件
function releaseAllPressedKeys() {
  if (activeVkCount.size === 0) return;

  const activeVkCodes = Array.from(activeVkCount.keys()).reverse();
  for (const vkCode of activeVkCodes) {
    sendKeySync(vkCode, 0x0002);
  }

  activeVkCount.clear();
  activeNoteBindings.clear();
  activeNotes.clear();
}

// 安全关闭 MIDI 输入端口：
// - 仅在实例已创建时关闭
// - 忽略重复关闭或底层抛错，保证退出流程不中断
function closeInputPortSafely() {
  if (!input) return;
  try {
    input.closePort();
  } catch (err) {
    // 忽略关闭阶段异常，避免影响最终退出。
  } finally {
    input = null;
  }
}

// 统一退出清理：
// 1) 全量抬键，防止异常退出后按键残留按下态
// 2) 关闭 MIDI 端口，避免设备占用状态残留
function cleanupBeforeExit() {
  if (hasCleanupRun) return;
  hasCleanupRun = true;
  releaseAllPressedKeys();
  closeInputPortSafely();
}

// 统一的退出辅助函数：
// 遇到启动失败或配置错误时，保持窗口不立即关闭，方便用户看到报错信息。
function pauseAndExit(code = 1) {
  cleanupBeforeExit();
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

// 解析 CLI 传入的配置文件路径。
// - 绝对路径：直接使用
// - 相对路径：优先按当前工作目录解析；若不存在，再按程序目录解析
function resolveConfigOverride(configArg) {
  if (configArg === undefined) return { configPath: null, error: null };
  if (typeof configArg !== 'string' || configArg.trim() === '') {
    return { configPath: null, error: 'Invalid -config value. Expected a non-empty file path.' };
  }

  const inputPath = configArg.trim();
  if (path.isAbsolute(inputPath)) {
    return { configPath: inputPath, error: null };
  }

  const cwdPath = path.resolve(process.cwd(), inputPath);
  if (fs.existsSync(cwdPath)) {
    return { configPath: cwdPath, error: null };
  }

  return { configPath: path.resolve(baseDir, inputPath), error: null };
}

const { configPath: configPathOverride, error: configPathError } = resolveConfigOverride(args.config);
if (configPathError) {
  if (checkConfigOnly) {
    console.log('false');
    process.exit(1);
  }
  console.error(configPathError);
  pauseAndExit();
  return;
}

// 读取并解析配置文件，返回：
// - noteMap: MIDI note -> 键位序列映射（单键是长度 1，组合键是长度 > 1）
// - port: 配置文件中的默认端口号（可选）
// - devmode: 当检测到 .dev 标记文件时为 1，否则为 0
// - configPath: 实际使用的配置文件绝对路径
if (checkConfigOnly) {
  const checkResult = loadConfig(baseDir, {
    verbose: false,
    silent: true,
    strict: true,
    configPath: configPathOverride,
  });
  console.log(checkResult ? 'true' : 'false');
  process.exit(checkResult ? 0 : 1);
}

const configResult = loadConfig(baseDir, {
  verbose: cliVerbose,
  configPath: configPathOverride,
});
if (!configResult) {
  pauseAndExit();
  return;
}
const { noteMap, port: configPort, devmode, configPath: activeConfigPath } = configResult;

// devmode 为 1 时强制打开详细日志。
const verbose = cliVerbose || devmode === 1;

// 端口选择优先级：CLI -port > 配置文件 port > 默认 0
const selectedPort = args.port !== undefined ? Number(args.port) : (configPort !== null ? configPort : 0);

// 启动前进行严格端口参数校验，避免 NaN、负数等非法输入继续执行。
if (!Number.isInteger(selectedPort) || selectedPort < 0) {
  console.error(`Invalid port "${args.port ?? configPort}". Expected a non-negative integer.`);
  pauseAndExit();
  return;
}

const portIndex = selectedPort;

// 初始化 MIDI 输入对象并查询系统中可用的 MIDI 输入端口数量。
input = new midi.Input();
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

// 将键位序列格式化为可读日志字符串。
// 例如：[0x11, 0x42] -> "ctrl+b"
function formatBinding(vkCodes) {
  return vkCodes
    .map((vkCode) => getKeyName(vkCode) ?? `0x${vkCode.toString(16).toUpperCase()}`)
    .join('+');
}

// 发送键位序列：
// - 按下（Note On）：按配置顺序依次按下（左到右）
// - 抬起（Note Off）：按逆序依次抬起（右到左）
function sendBinding(vkCodes, isNoteOn) {
  if (isNoteOn) {
    for (const vkCode of vkCodes) {
      sendKey(vkCode, 0);
    }
    return;
  }

  for (let i = vkCodes.length - 1; i >= 0; i--) {
    sendKey(vkCodes[i], 0x0002);
  }
}

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

  // 查询当前音符是否已配置映射键位序列。
  const binding = noteMap.get(note);

  // 基于当前音符状态识别重复/异常事件：
  // - isDuplicateNoteOn：该音符已经按下，又收到一次 Note ON
  // - isUnexpectedNoteOff：该音符并未按下，却收到 Note OFF
  const noteIsActive = activeNotes.has(note);
  const isDuplicateNoteOn = isNoteOn && noteIsActive;
  const isUnexpectedNoteOff = isNoteOff && !noteIsActive;

  // 仅在“状态合法变化”时更新集合：
  // - 首次 Note ON：加入 activeNotes
  // - 对应 Note OFF：从 activeNotes 移除
  if (isNoteOn && !isDuplicateNoteOn) {
    activeNotes.add(note);
  } else if (isNoteOff && !isUnexpectedNoteOff) {
    activeNotes.delete(note);
  }

  // 详细日志默认关闭，仅在 -verbose 或 devmode 下输出，避免高频日志影响性能。
  if (verbose) {
    const keyInfo = binding ? `, Key: '${formatBinding(binding)}'` : ' (unbound)';
    const stateInfo = isDuplicateNoteOn
      ? ' (duplicate Note ON ignored)'
      : (isUnexpectedNoteOff ? ' (unexpected Note OFF ignored)' : '');
    if (isNoteOn) {
      console.log(`Note ON: ${note}, Velocity: ${velocity}${keyInfo}${stateInfo}`);
    } else {
      console.log(`Note OFF: ${note}${keyInfo}${stateInfo}`);
    }
  }

  // 重复/异常事件不发送键盘消息，避免重复 keydown 或错误 keyup。
  if (isDuplicateNoteOn || isUnexpectedNoteOff) return;

  // 未绑定键位时不发送键盘事件。
  if (!binding) return;

  // Note ON：
  // - 按序发送 keydown
  // - 记录该 note 当前激活的绑定，供后续 Note OFF/退出清理使用
  // - 增加 VK 引用计数，支持多个 note 共享同一按键
  if (isNoteOn) {
    sendBinding(binding, true);
    activeNoteBindings.set(note, binding);
    increaseActiveVkCount(binding);
    return;
  }

  // Note OFF：
  // - 优先使用 Note ON 时记录的绑定进行释放，避免配置变化造成释放不一致
  // - 按逆序发送 keyup，并减少对应 VK 引用计数
  const activeBinding = activeNoteBindings.get(note) || binding;
  sendBinding(activeBinding, false);
  decreaseActiveVkCount(activeBinding);
  activeNoteBindings.delete(note);
});

console.log(`MIDITap v${version} is running, press Ctrl+C to exit.`);
if (devmode === 1) {
  console.log(`[DEVMODE] Development mode enabled, using ${activeConfigPath}`);
}

// 捕获未处理异常/Promise 拒绝：
// - 先执行统一清理（全量抬键 + 关闭端口）
// - 再退出进程，降低异常退出导致“卡键”的概率
process.on('uncaughtException', (err) => {
  console.error('Unhandled Exception:', err);
  cleanupBeforeExit();
  process.exit(1);
});

process.on('unhandledRejection', (reason) => {
  console.error('Unhandled Rejection:', reason);
  cleanupBeforeExit();
  process.exit(1);
});

// 手动接管 Ctrl+C：
// 在退出前主动关闭 MIDI 端口，避免设备占用状态残留。
process.stdin.setRawMode(true);
process.stdin.resume();
process.stdin.on('data', (key) => {
  if (key[0] === 0x03) {
    cleanupBeforeExit();
    process.exit(0);
  }
});
