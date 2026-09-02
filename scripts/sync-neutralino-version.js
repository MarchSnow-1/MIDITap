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
const neu = JSON.parse(fs.readFileSync(neuConfigPath, 'utf8'));

if (!pkg.version) {
  console.error('[sync-neutralino-version] package.json has no version');
  process.exit(1);
}

// 已经一致就无需改动（避免无谓的 mtime/工作区抖动）。
// Already in sync, so nothing to do (avoids pointless working-tree churn).
if (neu.version === pkg.version) {
  console.log('[sync-neutralino-version] neutralino.config.json already at ' + pkg.version);
  process.exit(0);
}

neu.version = pkg.version;
fs.writeFileSync(neuConfigPath, JSON.stringify(neu, null, 2) + '\n', 'utf8');
console.log('[sync-neutralino-version] neutralino.config.json version -> ' + pkg.version);
