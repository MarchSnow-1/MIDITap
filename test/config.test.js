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
