#!/usr/bin/env node
// 在 `npm version` 期间把 package.json 的版本号同步到 neutralino.config.json，
// 避免两处版本漂移（此前手动维护经常导致不一致）。
// Sync the version from package.json into neutralino.config.json during
// `npm version` so the two never drift apart.

const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..');
const pkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
const neuConfigPath = path.join(root, 'neutralino.config.json');
const neuConfigText = fs.readFileSync(neuConfigPath, 'utf8');
const neu = JSON.parse(neuConfigText);

if (!pkg.version) {
  console.error('[sync-neutralino-version] package.json has no version');
  process.exit(1);
}

// 已经一致就无需改动（避免无谓的工作区抖动）。
// Already in sync, so nothing to do (avoids pointless working-tree churn).
if (neu.version === pkg.version) {
  console.log('[sync-neutralino-version] neutralino.config.json already at ' + pkg.version);
  process.exit(0);
}

// 只替换顶层 "version" 字段的值，其余文本（缩进/紧凑数组等格式）原样保留。
// Replace only the top-level "version" value, leaving the rest of the file
// byte-for-byte intact so the sync never produces formatting noise.
const versionPattern = /^(\s*"version"\s*:\s*)("[^"]*")/m;
if (!versionPattern.test(neuConfigText)) {
  console.error('[sync-neutralino-version] top-level "version" not found in neutralino.config.json');
  process.exit(1);
}
const newVersionLiteral = JSON.stringify(pkg.version);
const updated = neuConfigText.replace(versionPattern, function (match, prefix, oldVersion) {
  return prefix + newVersionLiteral;
});
fs.writeFileSync(neuConfigPath, updated, 'utf8');
console.log('[sync-neutralino-version] neutralino.config.json version -> ' + pkg.version);
