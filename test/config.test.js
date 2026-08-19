const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const JSON5 = require('json5');

const {
  addMappingToConfig,
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
