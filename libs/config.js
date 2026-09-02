const fs = require('fs');
const path = require('path');
const JSON5 = require('json5');
const { VK } = require('./keyboard');

function findRootObject(content) {
  let quote = null;
  let escaped = false;
  let lineComment = false;
  let blockComment = false;
  let depth = 0;
  let opening = -1;

  for (let i = 0; i < content.length; i++) {
    const char = content[i];
    const next = content[i + 1];

    if (lineComment) {
      if (char === '\n' || char === '\r') lineComment = false;
      continue;
    }
    if (blockComment) {
      if (char === '*' && next === '/') {
        blockComment = false;
        i++;
      }
      continue;
    }
    if (quote) {
      if (escaped) {
        escaped = false;
      } else if (char === '\\') {
        escaped = true;
      } else if (char === quote) {
        quote = null;
      }
      continue;
    }
    if (char === '/' && next === '/') {
      lineComment = true;
      i++;
      continue;
    }
    if (char === '/' && next === '*') {
      blockComment = true;
      i++;
      continue;
    }
    if (char === '"' || char === "'") {
      quote = char;
    } else if (char === '{') {
      if (depth === 0) opening = i;
      depth++;
    } else if (char === '}' && depth > 0 && --depth === 0) {
      return { opening, closing: i };
    }
  }
  return null;
}

function readStringEnd(content, start) {
  const quote = content[start];
  let escaped = false;
  for (let i = start + 1; i < content.length; i++) {
    if (escaped) {
      escaped = false;
    } else if (content[i] === '\\') {
      escaped = true;
    } else if (content[i] === quote) {
      return i;
    }
  }
  return -1;
}

function skipTrivia(content, start, limit) {
  let i = start;
  while (i < limit) {
    if (/\s/.test(content[i])) {
      i++;
    } else if (content[i] === '/' && content[i + 1] === '/') {
      const newline = content.indexOf('\n', i + 2);
      i = newline === -1 ? limit : newline + 1;
    } else if (content[i] === '/' && content[i + 1] === '*') {
      const end = content.indexOf('*/', i + 2);
      i = end === -1 ? limit : end + 2;
    } else {
      break;
    }
  }
  return i;
}

function isIdentifierStart(ch) {
  return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || ch === '_' || ch === '$' || ch.charCodeAt(0) > 127;
}

function isIdentifierPart(ch) {
  return isIdentifierStart(ch) || (ch >= '0' && ch <= '9');
}

// Read a JSON5 unquoted IdentifierName starting at `start`. Returns the index
// just past the identifier, or -1 when `start` is not an identifier start.
function readIdentifierEnd(content, start, limit) {
  if (start >= limit || !isIdentifierStart(content[start])) return -1;
  let i = start + 1;
  while (i < limit && isIdentifierPart(content[i])) i++;
  return i;
}

function findTopLevelStringValue(content, propertyName, root) {
  let objectDepth = 1;
  let arrayDepth = 0;
  for (let i = root.opening + 1; i < root.closing; i++) {
    const char = content[i];
    const next = content[i + 1];
    if (char === '/' && next === '/') {
      const newline = content.indexOf('\n', i + 2);
      if (newline === -1) break;
      i = newline;
    } else if (char === '/' && next === '*') {
      const end = content.indexOf('*/', i + 2);
      if (end === -1) break;
      i = end + 1;
    } else if (char === '"' || char === "'") {
      const stringEnd = readStringEnd(content, i);
      if (stringEnd === -1) return null;
      if (objectDepth === 1 && arrayDepth === 0) {
        const colon = skipTrivia(content, stringEnd + 1, root.closing);
        if (content[colon] === ':') {
          let key;
          try { key = JSON5.parse(content.slice(i, stringEnd + 1)); } catch { return null; }
          const valueStart = skipTrivia(content, colon + 1, root.closing);
          if (key === propertyName && (content[valueStart] === '"' || content[valueStart] === "'")) {
            const valueEnd = readStringEnd(content, valueStart);
            return valueEnd === -1 ? null : { start: valueStart, end: valueEnd + 1 };
          }
          if (content[valueStart] === '"' || content[valueStart] === "'") {
            const valueEnd = readStringEnd(content, valueStart);
            if (valueEnd === -1) return null;
            i = valueEnd;
            continue;
          }
        }
      }
      i = stringEnd;
    } else if (objectDepth === 1 && arrayDepth === 0 && isIdentifierStart(char)) {
      // JSON5 permits unquoted IdentifierName keys (e.g. `name: "x"`).
      const identEnd = readIdentifierEnd(content, i, root.closing);
      if (identEnd !== -1) {
        const colon = skipTrivia(content, identEnd, root.closing);
        if (content[colon] === ':') {
          const keyText = content.slice(i, identEnd);
          const valueStart = skipTrivia(content, colon + 1, root.closing);
          if (keyText === propertyName && (content[valueStart] === '"' || content[valueStart] === "'")) {
            const valueEnd = readStringEnd(content, valueStart);
            return valueEnd === -1 ? null : { start: valueStart, end: valueEnd + 1 };
          }
          if (content[valueStart] === '"' || content[valueStart] === "'") {
            const valueEnd = readStringEnd(content, valueStart);
            if (valueEnd === -1) return null;
            i = valueEnd;
          } else {
            i = identEnd - 1;
          }
          continue;
        }
        // Not a property key (e.g. a bare true/false/null value): skip the token.
        i = identEnd - 1;
      }
    } else if (char === '{') {
      objectDepth++;
    } else if (char === '}') {
      objectDepth--;
    } else if (char === '[') {
      arrayDepth++;
    } else if (char === ']') {
      arrayDepth--;
    }
  }
  return null;
}

function findTopLevelEntryRange(content, propertyName, root) {
  let objectDepth = 1;
  let arrayDepth = 0;
  for (let i = root.opening + 1; i < root.closing; i++) {
    const char = content[i];
    const next = content[i + 1];
    if (char === '/' && next === '/') {
      const newline = content.indexOf('\n', i + 2);
      if (newline === -1) break;
      i = newline;
    } else if (char === '/' && next === '*') {
      const end = content.indexOf('*/', i + 2);
      if (end === -1) break;
      i = end + 1;
    } else if (char === '"' || char === "'") {
      const stringEnd = readStringEnd(content, i);
      if (stringEnd === -1) return null;
      if (objectDepth === 1 && arrayDepth === 0) {
        const colon = skipTrivia(content, stringEnd + 1, root.closing);
        if (content[colon] === ':') {
          let key;
          try { key = JSON5.parse(content.slice(i, stringEnd + 1)); } catch { return null; }
          const valueStart = skipTrivia(content, colon + 1, root.closing);
          if (key === propertyName) {
            // Found the target property. Deletion only applies to string
            // (key-spec) values; a non-string value here is not a removable
            // mapping entry.
            if (valueStart >= root.closing || (content[valueStart] !== '"' && content[valueStart] !== "'")) return null;
            const valueEnd = readStringEnd(content, valueStart);
            if (valueEnd === -1) return null;
            const afterValue = skipTrivia(content, valueEnd + 1, root.closing);
            return { start: i, end: content[afterValue] === ',' ? afterValue + 1 : valueEnd + 1 };
          }
          // Not the target property. If its value is a string, skip past it so
          // the outer loop never misreads string content as structure. Non-string
          // values (numbers, booleans, nested objects/arrays, e.g. "port": 0)
          // are traversed safely by the loop's brace/bracket tracking, so we
          // keep scanning for the target instead of aborting.
          if (content[valueStart] === '"' || content[valueStart] === "'") {
            const valueEnd = readStringEnd(content, valueStart);
            if (valueEnd === -1) return null;
            i = valueEnd;
            continue;
          }
        }
      }
      i = stringEnd;
    } else if (char === '{') {
      objectDepth++;
    } else if (char === '}') {
      objectDepth--;
    } else if (char === '[') {
      arrayDepth++;
    } else if (char === ']') {
      arrayDepth--;
    }
  }
  return null;
}

function insertRootProperty(content, root, propertyName, serializedValue) {
  const lineBreak = content.includes('\r\n') ? '\r\n' : '\n';
  const firstLineBreak = content.slice(root.opening + 1).match(/^(\r\n|\n|\r)/);
  if (firstLineBreak) {
    const insertAt = root.opening + 1 + firstLineBreak[0].length;
    const indent = content.slice(insertAt).match(/^[\t ]*/)[0] || '  ';
    return content.slice(0, insertAt) + indent + JSON.stringify(propertyName) + ': ' + serializedValue + ',' + lineBreak + content.slice(insertAt);
  }
  return content.slice(0, root.opening + 1) + lineBreak + '  ' + JSON.stringify(propertyName) + ': ' + serializedValue + ',' + lineBreak + content.slice(root.opening + 1);
}

function writeConfigFile(filePath, content) {
  const tempPath = `${filePath}.${process.pid}.${Date.now()}.tmp`;
  try {
    fs.writeFileSync(tempPath, content, 'utf8');
    fs.renameSync(tempPath, filePath);
    return true;
  } catch {
    try { fs.unlinkSync(tempPath); } catch {}
    return false;
  }
}

function parseConfigObject(content) {
  try {
    const parsed = JSON5.parse(content);
    return typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

function isPathInside(parentPath, targetPath) {
  const relative = path.relative(parentPath, targetPath);
  return relative === '' || (relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative));
}

function resolveConfigPath(baseDir, configPath) {
  const configDir = path.resolve(baseDir, 'config');
  const candidate = path.isAbsolute(configPath)
    ? path.normalize(configPath)
    : path.resolve(configDir, configPath);

  try {
    const realConfigDir = fs.realpathSync(configDir);
    const realCandidate = fs.realpathSync(candidate);
    return isPathInside(realConfigDir, realCandidate) && path.extname(realCandidate).toLowerCase() === '.json'
      ? realCandidate
      : null;
  } catch {
    return null;
  }
}

// 解析并校验配置中的 port 字段。
// 返回约定：
// - number：合法端口（非负整数）
// - null：未配置或非法，主流程将回退到默认端口 0
function parsePort(portValue, silent = false) {
  if (portValue === undefined || portValue === null) return null;
  const port = Number(portValue);
  if (!Number.isInteger(port) || port < 0) {
    if (!silent) {
      console.warn(`Invalid config port "${portValue}", fallback to default port 0.`);
    }
    return null;
  }
  return port;
}


// 解析单条键位配置并返回 VK 序列：
// - 单键示例："a" / "enter" -> [VK_A] / [VK_ENTER]
// - 组合键示例："ctrl+b"      -> [VK_CTRL, VK_B]
// 规则：仅当字符串长度 > 1 且包含 '+' 时，才按组合键拆分。
function parseBinding(keySpec, noteStr, silent = false) {
  if (typeof keySpec !== 'string' || keySpec.trim() === '') {
    if (!silent) {
      console.warn(`Invalid key name for note "${noteStr}", expected non-empty string, skipping...`);
    }
    return null;
  }

  const normalizedSpec = keySpec.trim().toLowerCase();
  const isCombo = normalizedSpec.length > 1 && normalizedSpec.includes('+');
  const tokens = isCombo ? normalizedSpec.split('+').map((part) => part.trim()) : [normalizedSpec];

  if (tokens.some((token) => token === '')) {
    if (!silent) {
      console.warn(`Invalid key combo "${keySpec}" for note "${noteStr}", skipping...`);
    }
    return null;
  }

  const vkCodes = [];
  for (const token of tokens) {
    const vk = VK[token];
    if (vk === undefined) {
      if (!silent) {
        console.warn(`Can't find '${token}' in VK Code List, skipping note "${noteStr}"...`);
      }
      return null;
    }
    vkCodes.push(vk);
  }

  return {
    vkCodes,
    label: tokens.join('+'),
  };
}

// 加载并校验映射配置。
// 参数：
// - baseDir: 配置根目录（开发态是项目目录，打包态是 exe 所在目录）
// - options.verbose: 是否输出详细加载信息
// - options.silent: 是否静默（用于 --check-config，仅输出 true/false）
// - options.configPath: CLI 指定配置文件路径（绝对路径）
// 返回：
// - { noteMap, port, name, configPath }：成功
// - null：失败（如文件不存在、解析失败、结构非法）
function loadConfig(baseDir, options = {}) {
  const {
    verbose = false,
    silent = false,
    strict = false,
    configPath: configPathOverride = null,
  } = options;

  const configPath = configPathOverride
    ? resolveConfigPath(baseDir, configPathOverride)
    : resolveConfigPath(baseDir, path.join(baseDir, 'config', 'mapping.json'));
  if (!configPath) return null;
  const configFileName = path.basename(configPath);
  const effectiveVerbose = !silent && verbose;
  let rawMapping = {};
  let hasValidationError = false;

  if (effectiveVerbose) {
    console.log(`Config file: ${configPath}`);
  }

  // 使用 JSON5 读取配置，允许注释与更宽松的书写格式。
  try {
    rawMapping = JSON5.parse(fs.readFileSync(configPath, 'utf8'));
    if (effectiveVerbose) console.log(`Config Loaded (${configFileName}):`, rawMapping);
  } catch (err) {
    if (!silent) {
      console.error(`Failed to Load Config (${configFileName}):`, err.message);
    }
    return null;
  }

  // 顶层必须是普通对象，防止数组/null 等非法结构进入后续逻辑。
  if (typeof rawMapping !== 'object' || rawMapping === null || Array.isArray(rawMapping)) {
    if (!silent) {
      console.error('Invalid config format: expected an object.');
    }
    return null;
  }

  // 提取显示名称
  const configName = (typeof rawMapping.name === 'string' && rawMapping.name.trim()) || configFileName;

  // 运行时使用 Map，加快 MIDI note 到 VK 的查询速度。
  const noteMap = new Map();
  for (const [noteStr, keyChar] of Object.entries(rawMapping)) {
    // `port` 和 `name` 是全局配置项，不是音符映射。
    if (noteStr === 'port' || noteStr === 'name') continue;

    // MIDI note 必须是 0~127 的整数。
    const note = Number(noteStr);
    if (!Number.isInteger(note) || note < 0 || note > 127) {
      if (!silent) {
        console.warn(`Invalid MIDI note "${noteStr}", expected integer in range 0-127, skipping...`);
      }
      hasValidationError = true;
      continue;
    }

    const binding = parseBinding(keyChar, noteStr, silent);
    if (!binding) {
      hasValidationError = true;
      continue;
    }

    // 记录合法映射（单键和组合键统一为 VK 数组）。
    // 若同一 note 重复定义，后定义值会覆盖先定义值。
    noteMap.set(note, binding.vkCodes);
    if (effectiveVerbose) {
      const vkLabel = binding.vkCodes
        .map((vkCode) => `0x${vkCode.toString(16).toUpperCase()}`)
        .join('+');
      console.log(`note ${note} -> '${binding.label}' (VK=${vkLabel})`);
    }
  }

  const parsedPort = parsePort(rawMapping.port, silent);
  if (rawMapping.port !== undefined && rawMapping.port !== null && parsedPort === null) {
    hasValidationError = true;
  }

  // 严格模式下，只要存在任意校验错误就判定失败。
  if (strict && hasValidationError) {
    return null;
  }

  // 返回通过校验后的干净数据结构，供主流程直接使用。
  return { noteMap, port: parsedPort, name: configName, configPath };
}

// 扫描 config 目录下所有 .json 文件，读取 name 字段
function listConfigFiles(baseDir) {
  const configDir = path.join(baseDir, 'config');
  const results = [];
  try {
    const entries = fs.readdirSync(configDir);
    for (const entry of entries) {
      if (!entry.endsWith('.json')) continue;
      const filePath = path.join(configDir, entry);
      try {
        const raw = JSON5.parse(fs.readFileSync(filePath, 'utf8'));
        const name = (typeof raw === 'object' && raw !== null && !Array.isArray(raw) && typeof raw.name === 'string')
          ? raw.name.trim()
          : entry;
        results.push({ filename: entry, name, path: filePath });
      } catch {
        results.push({ filename: entry, name: entry, path: filePath });
      }
    }
  } catch {}
  return results;
}

// 更新配置文件的 name 字段（通过正则替换，保留注释和格式）
function renameConfigFile(baseDir, filename, newName) {
  const configPath = resolveConfigPath(baseDir, filename);
  if (!configPath) return false;
  try {
    let content = fs.readFileSync(configPath, 'utf8');
    const root = findRootObject(content);
    if (!parseConfigObject(content) || !root) return false;
    const serializedName = JSON.stringify(String(newName));
    const nameRange = findTopLevelStringValue(content, 'name', root);
    content = nameRange
      ? content.slice(0, nameRange.start) + serializedName + content.slice(nameRange.end)
      : insertRootProperty(content, root, 'name', serializedName);
    return writeConfigFile(configPath, content);
  } catch {
    return false;
  }
}

// 确保配置目录存在，且至少有一个 .json 配置文件。
// 若目录为空，自动生成默认 mapping.json。
function ensureConfigDir(baseDir) {
  const configDir = path.join(baseDir, 'config');
  if (!fs.existsSync(configDir)) {
    fs.mkdirSync(configDir, { recursive: true });
  }
  const hasJson = (() => {
    try {
      return fs.readdirSync(configDir).some((entry) => entry.endsWith('.json'));
    } catch { return false; }
  })();
  if (!hasJson) {
    const defaultConfig = JSON.stringify({
      name: 'Default Config',
    }, null, 2);
    fs.writeFileSync(path.join(configDir, 'mapping.json'), defaultConfig, 'utf8');
    return path.join(configDir, 'mapping.json');
  }
  return null;
}

// 读取上次使用的配置文件路径（.storage/last_config）。
// 返回 null 表示没有记录或记录的文件已不存在。
function getLastConfigPath(baseDir) {
  const storagePath = path.join(baseDir, '.storage', 'last_config');
  try {
    const content = fs.readFileSync(storagePath, 'utf8').trim();
    if (!content) return null;
    return resolveConfigPath(baseDir, content);
  } catch { /* ignore */ }
  return null;
}

// 保存最后使用的配置文件路径到 .storage/last_config。
function saveLastConfigPath(baseDir, configPath) {
  const resolvedPath = resolveConfigPath(baseDir, configPath);
  if (!resolvedPath) return false;
  const storageDir = path.join(baseDir, '.storage');
  if (!fs.existsSync(storageDir)) fs.mkdirSync(storageDir, { recursive: true });
  fs.writeFileSync(path.join(storageDir, 'last_config'), resolvedPath, 'utf8');
  return true;
}

// 向配置文件中添加或更新一个映射条目，保留原有注释和格式
function addMappingToConfig(baseDir, configPath, note, key) {
  const resolvedPath = resolveConfigPath(baseDir, configPath);
  if (!resolvedPath) return false;
  // 与 deleteMappingFromConfig 保持对称：note 必须是 0~127 的整数。
  // 这同时排除了 'name'/'port' 等保留键被误写入覆盖全局配置。
  // Mirrors deleteMappingFromConfig: note must be an integer in 0-127.
  // This also stops reserved keys such as 'name'/'port' from being
  // overwritten by an out-of-range note.
  const noteNum = Number(note);
  if (!Number.isInteger(noteNum) || noteNum < 0 || noteNum > 127) return false;
  try {
    let content = fs.readFileSync(resolvedPath, 'utf8');
    const root = findRootObject(content);
    if (!parseConfigObject(content) || !root) return false;
    const noteStr = String(noteNum);
    const serializedKey = JSON.stringify(String(key));
    const valueRange = findTopLevelStringValue(content, noteStr, root);
    content = valueRange
      ? content.slice(0, valueRange.start) + serializedKey + content.slice(valueRange.end)
      : insertRootProperty(content, root, noteStr, serializedKey);
    return writeConfigFile(resolvedPath, content);
  } catch {
    return false;
  }
}

// 从配置文件中删除一个映射条目，保留原有注释和格式
function deleteMappingFromConfig(baseDir, configPath, note) {
  const resolvedPath = resolveConfigPath(baseDir, configPath);
  if (!resolvedPath) return false;
  const noteNum = Number(note);
  if (!Number.isInteger(noteNum) || noteNum < 0 || noteNum > 127) return false;
  try {
    let content = fs.readFileSync(resolvedPath, 'utf8');
    const root = findRootObject(content);
    if (!parseConfigObject(content) || !root) return false;
    const entryRange = findTopLevelEntryRange(content, String(noteNum), root);
    if (!entryRange) return true;
    content = content.slice(0, entryRange.start) + content.slice(entryRange.end);
    return writeConfigFile(resolvedPath, content);
  } catch {
    return false;
  }
}

module.exports = { resolveConfigPath, loadConfig, listConfigFiles, renameConfigFile, addMappingToConfig, deleteMappingFromConfig, ensureConfigDir, getLastConfigPath, saveLastConfigPath, parseBinding };
