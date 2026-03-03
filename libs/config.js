const fs = require('fs');
const path = require('path');
const JSON5 = require('json5');
const { VK } = require('./keyboard');

// 解析并校验配置中的 port 字段。
// 返回约定：
// - number：合法端口（非负整数）
// - null：未配置或非法，主流程将回退到默认端口 0
function parsePort(portValue) {
  if (portValue === undefined || portValue === null) return null;
  const port = Number(portValue);
  if (!Number.isInteger(port) || port < 0) {
    console.warn(`Invalid config port "${portValue}", fallback to default port 0.`);
    return null;
  }
  return port;
}

// 根据运行目录是否存在 .dev 标记文件决定配置文件与 devmode：
// - 存在 .dev：使用 mapping-dev.json，devmode=1
// - 不存在 .dev：使用 mapping.json，devmode=0
function resolveConfigMeta(baseDir, verbose = false) {
  const devMarkerPath = path.join(baseDir, '.dev');
  const devmode = fs.existsSync(devMarkerPath) ? 1 : 0;
  const configFileName = devmode === 1 ? 'mapping-dev.json' : 'mapping.json';
  const configPath = path.join(baseDir, 'config', configFileName);

  if (verbose) {
    console.log(`Config mode: ${devmode === 1 ? 'dev' : 'default'}, file: ${configPath}`);
  }

  return { configPath, devmode, configFileName };
}

// 加载并校验映射配置。
// 参数：
// - baseDir: 配置根目录（开发态是项目目录，打包态是 exe 所在目录）
// - options.verbose: 是否输出详细加载信息
// 返回：
// - { noteMap, port, devmode }：成功
// - null：失败（如文件不存在、解析失败、结构非法）
function loadConfig(baseDir, options = {}) {
  const { verbose = false } = options;
  const { configPath, devmode, configFileName } = resolveConfigMeta(baseDir, verbose);
  let rawMapping = {};

  // 使用 JSON5 读取配置，允许注释与更宽松的书写格式。
  try {
    rawMapping = JSON5.parse(fs.readFileSync(configPath, 'utf8'));
    if (verbose) console.log(`Config Loaded (${configFileName}):`, rawMapping);
  } catch (err) {
    console.error(`Failed to Load Config (${configFileName}):`, err.message);
    return null;
  }

  // 顶层必须是普通对象，防止数组/null 等非法结构进入后续逻辑。
  if (typeof rawMapping !== 'object' || rawMapping === null || Array.isArray(rawMapping)) {
    console.error('Invalid config format: expected an object.');
    return null;
  }

  // 运行时使用 Map，加快 MIDI note 到 VK 的查询速度。
  const noteMap = new Map();
  for (const [noteStr, keyChar] of Object.entries(rawMapping)) {
    // `port` 是全局配置项，不是音符映射。
    if (noteStr === 'port') continue;

    // MIDI note 必须是 0~127 的整数。
    const note = Number(noteStr);
    if (!Number.isInteger(note) || note < 0 || note > 127) {
      console.warn(`Invalid MIDI note "${noteStr}", expected integer in range 0-127, skipping...`);
      continue;
    }

    // 按键名必须是非空字符串，避免错误类型导致运行时异常。
    if (typeof keyChar !== 'string' || keyChar.trim() === '') {
      console.warn(`Invalid key name for note "${noteStr}", expected non-empty string, skipping...`);
      continue;
    }

    // 统一清理空白并转为小写，减少配置书写差异。
    const normalizedKeyName = keyChar.trim().toLowerCase();
    const vk = VK[normalizedKeyName];
    if (vk === undefined) {
      console.warn(`Can't find '${keyChar}' in VK Code List, skipping...`);
      continue;
    }

    // 记录合法映射。若同一 note 重复定义，后定义值会覆盖先定义值。
    noteMap.set(note, vk);
    if (verbose) {
      console.log(`note ${note} -> '${normalizedKeyName}' (VK=0x${vk.toString(16).toUpperCase()})`);
    }
  }

  // 返回通过校验后的干净数据结构，供主流程直接使用。
  return { noteMap, port: parsePort(rawMapping.port), devmode };
}

module.exports = { loadConfig };