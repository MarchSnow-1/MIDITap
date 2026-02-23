const fs = require('fs');
const path = require('path');
const JSON5 = require('json5');
const { VK } = require('./keyboard');

function loadConfig(baseDir) {
  const configPath = path.join(baseDir, 'config', 'mapping.json');
  let rawMapping = {};
  try {
    rawMapping = JSON5.parse(fs.readFileSync(configPath, 'utf8'));
    console.log('Config Loaded:', rawMapping);
  } catch (err) {
    console.error('Failed to Load Config:', err.message);
    return null;
  }

  const noteMap = new Map();
  for (const [noteStr, keyChar] of Object.entries(rawMapping)) {
    const vk = VK[keyChar.toLowerCase()];
    if (vk === undefined) {
      console.warn(`Can't find '${keyChar}' in VK Code List, Skipping...`);
      continue;
    }
    noteMap.set(parseInt(noteStr), vk);
    console.log(`note ${noteStr} -> '${keyChar}' (VK=0x${vk.toString(16).toUpperCase()})`);
  }

  return noteMap;
}

module.exports = { loadConfig };