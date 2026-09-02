const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const JSON5 = require('json5');

const {
  addMappingToConfig,
  deleteMappingFromConfig,
  loadConfig,
  parseBinding,
  renameConfigFile,
  resolveConfigPath,
} = require('../libs/config');

function withConfigDir(callback) {
  const baseDir = fs.mkdtempSync(path.join(os.tmpdir(), 'miditap-config-'));
  const configDir = path.join(baseDir, 'config');
  fs.mkdirSync(configDir);
  try {
    return callback({ baseDir, configDir });
  } finally {
    fs.rmSync(baseDir, { recursive: true, force: true });
  }
}

test('configuration paths stay inside the config directory', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    const outsidePath = path.join(baseDir, 'outside.json');
    fs.writeFileSync(configPath, '{}');
    fs.writeFileSync(outsidePath, '{}');

    assert.equal(resolveConfigPath(baseDir, configPath), fs.realpathSync(configPath));
    assert.equal(resolveConfigPath(baseDir, 'mapping.json'), fs.realpathSync(configPath));
    assert.equal(resolveConfigPath(baseDir, '../outside.json'), null);
    assert.equal(resolveConfigPath(baseDir, outsidePath), null);
  });
});

test('configuration paths reject symbolic-link escapes', { skip: process.platform === 'win32' }, () => {
  withConfigDir(({ baseDir, configDir }) => {
    const outsidePath = path.join(baseDir, 'outside.json');
    const linkedPath = path.join(configDir, 'linked.json');
    fs.writeFileSync(outsidePath, '{}');
    fs.symlinkSync(outsidePath, linkedPath);

    assert.equal(resolveConfigPath(baseDir, linkedPath), null);
  });
});

test('renaming and adding mappings preserve JSON5 comments and escape strings', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    fs.writeFileSync(configPath, [
      '{',
      '  // keep this comment',
      '  "name": "old",',
      '  "48": "a",',
      '}',
      '',
    ].join('\n'));

    assert.equal(renameConfigFile(baseDir, 'mapping.json', 'line\nname $1'), true);
    assert.equal(addMappingToConfig(baseDir, configPath, '50', 'ctrl+"$1"\n'), true);

    const content = fs.readFileSync(configPath, 'utf8');
    const parsed = JSON5.parse(content);
    assert.equal(parsed.name, 'line\nname $1');
    assert.equal(parsed['50'], 'ctrl+"$1"\n');
    assert.match(content, /keep this comment/);
    assert.equal(loadConfig(baseDir, { configPath, silent: true }).name, 'line\nname $1');
  });
});

test('writers modify top-level properties instead of nested or commented lookalikes', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    fs.writeFileSync(configPath, [
      '{',
      '  // "name": "commented"',
      '  "nested": { "name": "nested", "48": "b" },',
      '  "name": "top",',
      '  "48": "a",',
      '}',
      '',
    ].join('\n'));

    assert.equal(renameConfigFile(baseDir, 'mapping.json', 'updated'), true);
    assert.equal(addMappingToConfig(baseDir, configPath, '48', 'c'), true);

    const parsed = JSON5.parse(fs.readFileSync(configPath, 'utf8'));
    assert.equal(parsed.name, 'updated');
    assert.equal(parsed['48'], 'c');
    assert.equal(parsed.nested.name, 'nested');
    assert.equal(parsed.nested['48'], 'b');
  });
});

test('writers leave malformed configuration files untouched', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    const malformed = '{ broken';
    fs.writeFileSync(configPath, malformed);

    assert.equal(renameConfigFile(baseDir, 'mapping.json', 'changed'), false);
    assert.equal(addMappingToConfig(baseDir, configPath, '48', 'a'), false);
    assert.equal(fs.readFileSync(configPath, 'utf8'), malformed);
  });
});

test('deleting a mapping removes the entry and preserves comments', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    fs.writeFileSync(configPath, [
      '{',
      '  // keep this comment',
      '  "name": "test",',
      '  "48": "a",',
      '  "50": "b",',
      '}',
      '',
    ].join('\n'));

    assert.equal(deleteMappingFromConfig(baseDir, configPath, '48'), true);

    const content = fs.readFileSync(configPath, 'utf8');
    const parsed = JSON5.parse(content);
    assert.equal(Object.prototype.hasOwnProperty.call(parsed, '48'), false);
    assert.equal(parsed['50'], 'b');
    assert.match(content, /keep this comment/);

    const loaded = loadConfig(baseDir, { configPath, silent: true });
    assert.equal(loaded.noteMap.size, 1);
  });
});

test('deleting a non-existent mapping returns true without changing the file', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    const original = '{\n  "name": "test",\n  "48": "a"\n}\n';
    fs.writeFileSync(configPath, original);

    assert.equal(deleteMappingFromConfig(baseDir, configPath, '50'), true);
    assert.equal(fs.readFileSync(configPath, 'utf8'), original);
  });
});

test('deleting an out-of-range note returns false', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    fs.writeFileSync(configPath, '{"48": "a"}');
    assert.equal(deleteMappingFromConfig(baseDir, configPath, '200'), false);
  });
});

test('deleting a mapping still works when a non-string top-level value precedes it', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    fs.writeFileSync(configPath, [
      '{',
      '  "name": "test",',
      '  "port": 0,', // numeric global field before the target note
      '  "48": "a",',
      '  "60": "b",',
      '}',
      '',
    ].join('\n'));

    assert.equal(deleteMappingFromConfig(baseDir, configPath, '60'), true);

    const content = fs.readFileSync(configPath, 'utf8');
    const parsed = JSON5.parse(content);
    assert.equal(Object.prototype.hasOwnProperty.call(parsed, '60'), false);
    assert.equal(parsed['48'], 'a');
    assert.equal(parsed.port, 0);
    assert.equal(parsed.name, 'test');
  });
});

test('deleting a mapping still works when a nested-object value precedes it', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    fs.writeFileSync(configPath, [
      '{',
      '  "name": "test",',
      '  "meta": { "note": "60" },', // nested object with a lookalike key
      '  "48": "a",',
      '}',
      '',
    ].join('\n'));

    assert.equal(deleteMappingFromConfig(baseDir, configPath, '48'), true);

    const content = fs.readFileSync(configPath, 'utf8');
    const parsed = JSON5.parse(content);
    assert.equal(Object.prototype.hasOwnProperty.call(parsed, '48'), false);
    assert.equal(parsed.meta.note, '60'); // nested lookalike untouched
  });
});

test('renaming updates an unquoted JSON5 IdentifierName name key in place', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    fs.writeFileSync(configPath, [
      '{',
      '  name: "old",', // JSON5 unquoted key is valid
      '  "48": "a",',
      '}',
      '',
    ].join('\n'));

    assert.equal(renameConfigFile(baseDir, 'mapping.json', 'new'), true);

    const content = fs.readFileSync(configPath, 'utf8');
    const parsed = JSON5.parse(content);
    assert.equal(parsed.name, 'new');
    // exactly one name property must remain (no duplicate inserted at the top)
    const nameOccurrences = (content.match(/(?:^|,)\s*"?name"?\s*:/gm) || []).length;
    assert.equal(nameOccurrences, 1, content);
    assert.equal(parsed['48'], 'a');
  });
});

test('adding a mapping validates the note range like deletion does', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    fs.writeFileSync(configPath, '{\n  "name": "t",\n  "port": 0\n}\n');

    // Out-of-range and reserved-key-style notes are rejected and never written.
    assert.equal(addMappingToConfig(baseDir, configPath, '200', 'a'), false);
    assert.equal(addMappingToConfig(baseDir, configPath, 'port', 'a'), false);
    assert.equal(addMappingToConfig(baseDir, configPath, 'name', 'a'), false);
    assert.equal(addMappingToConfig(baseDir, configPath, '-1', 'a'), false);

    const content = fs.readFileSync(configPath, 'utf8');
    const parsed = JSON5.parse(content);
    assert.equal(parsed.port, 0);
    assert.equal(parsed.name, 't');
    assert.equal(Object.keys(parsed).length, 2, content);

    // Valid note + key still works.
    assert.equal(addMappingToConfig(baseDir, configPath, '60', 'ctrl+b'), true);
    assert.equal(JSON5.parse(fs.readFileSync(configPath, 'utf8'))['60'], 'ctrl+b');
  });
});

test('parseBinding accepts single keys, combos and rejects unknown names', () => {
  assert.equal(parseBinding('a', '48').vkCodes.length, 1);
  assert.equal(parseBinding('ctrl+b', '50').vkCodes.length, 2);
  assert.equal(parseBinding('ArrowUp', '60'), null); // not a VK name
  assert.equal(parseBinding('', '60'), null);
  assert.equal(parseBinding('ctrl++', '60'), null);
});

test('loadConfig returns null for missing or malformed config files', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const missing = path.join(configDir, 'nope.json');
    assert.equal(loadConfig(baseDir, { configPath: missing, silent: true }), null);

    const badPath = path.join(configDir, 'bad.json');
    fs.writeFileSync(badPath, '{ broken');
    assert.equal(loadConfig(baseDir, { configPath: badPath, silent: true }), null);
  });
});

test('loadConfig builds a noteMap keyed by integer notes and keeps name/port', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    fs.writeFileSync(configPath, [
      '{',
      '  name: "My Config",', // unquoted JSON5 key
      '  "port": 2,',
      '  "48": "a",',
      '  "50": "ctrl+b",',
      '  "200": "z",', // out of range -> skipped
      '  "ArrowUp": "q"', // invalid note text -> skipped (not an integer key)
      '}',
      '',
    ].join('\n'));

    const result = loadConfig(baseDir, { configPath, silent: true });
    assert.ok(result);
    assert.equal(result.name, 'My Config');
    assert.equal(result.port, 2);
    assert.equal(result.noteMap.size, 2);
    assert.deepEqual(result.noteMap.get(48), [0x41]);
    assert.deepEqual(result.noteMap.get(50), [0x11, 0x42]);
    assert.equal(result.noteMap.has(200), false);
  });
});

test('strict mode rejects any config that contains an invalid entry', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const strictPath = path.join(configDir, 'strict.json');
    fs.writeFileSync(strictPath, '{\n  "name": "t",\n  "48": "notavalidkeyname"\n}\n');
    assert.equal(loadConfig(baseDir, { configPath: strictPath, silent: true, strict: true }), null);

    // Non-strict mode still returns the valid entries while dropping the bad one.
    const loose = loadConfig(baseDir, { configPath: strictPath, silent: true });
    assert.ok(loose);
    assert.equal(loose.noteMap.size, 0);
  });
});

test('loadConfig rejects an out-of-range note even in non-strict mode', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    fs.writeFileSync(configPath, '{\n  "name": "t",\n  "128": "a",\n  "127": "b"\n}\n');
    const result = loadConfig(baseDir, { configPath, silent: true });
    assert.ok(result);
    assert.equal(result.noteMap.has(128), false);
    assert.equal(result.noteMap.has(127), true);
  });
});

test('port field is parsed as a non-negative integer', () => {
  withConfigDir(({ baseDir, configDir }) => {
    const configPath = path.join(configDir, 'mapping.json');
    fs.writeFileSync(configPath, '{\n  "name": "t",\n  "port": 3,\n  "48": "a"\n}\n');
    assert.equal(loadConfig(baseDir, { configPath, silent: true }).port, 3);

    fs.writeFileSync(configPath, '{\n  "name": "t",\n  "port": "x",\n  "48": "a"\n}\n');
    // invalid port -> null (main flow falls back to default 0)
    assert.equal(loadConfig(baseDir, { configPath, silent: true }).port, null);
  });
});
