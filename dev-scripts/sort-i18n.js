#!/usr/bin/env node
// sort-i18n.js — 把 i18n 语言文件按键名排序，并检查排序结果
//
// 语言文件按功能分组写入，读起来舒服，但两份文件一旦增删键就会错位
// 排序后键顺序一致，逐行对照即可看出差异，新键也不会挤在中间
// 本脚本既做格式化也做检查：--check 时只报告不写入，供 CI 与开发者使用
//
// 为什么用 Node 而不是 PowerShell：JSON 的键顺序对文本处理是语义问题，
// 而 Node 的 JSON.parse 天然给出键列表，排序后重写不会碰到值的转义
//
// sort-i18n.js — sorts the i18n language files by key and checks the result
//
// The files are grouped by feature for readability, yet the two drift apart once keys are added or removed
// Sorting gives both the same key order, so a line-by-line comparison shows the difference and a new key cannot land in the middle
// It both formats and checks: with --check it reports without writing, for CI and developers
//
// Why Node rather than PowerShell: key order in JSON is a semantic matter for text handling,
// and JSON.parse hands over the key list directly, so rewriting after a sort never touches the escaping of the values

const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const i18nDir = path.join(root, "src", "MIDITap.App", "i18n");
const checkOnly = process.argv.includes("--check");

// 语言文件是 UTF-8 + CRLF：读取时按原样保留，写入时显式用 CRLF，避免把行尾改掉
// The files are UTF-8 with CRLF: the text is read as-is and written back with CRLF, so line endings are left alone
function readFile(p) {
  return fs.readFileSync(p, "utf8");
}

function sortKeys(obj) {
  const out = {};
  for (const key of Object.keys(obj).sort()) {
    out[key] = obj[key];
  }
  return out;
}

// 逐键序列化：JSON.stringify 负责值的转义，键顺序由插入顺序决定，正是这里需要的
// Serialise key by key: JSON.stringify handles the escaping of the values, and the insertion order fixes the key order, which is what is wanted
function render(obj) {
  const lines = Object.entries(obj).map(
    ([k, v]) => "  " + JSON.stringify(k) + ": " + JSON.stringify(v)
  );
  return "{\n" + lines.join(",\n") + "\n}\n";
}

let failed = false;
const names = fs
  .readdirSync(i18nDir)
  .filter((f) => f.endsWith(".json"))
  .sort();

const keySets = [];

for (const name of names) {
  const p = path.join(i18nDir, name);
  const original = readFile(p);
  const parsed = JSON.parse(original);
  const originalKeys = Object.keys(parsed);
  const expectedOrder = Object.keys(sortKeys(parsed));

  keySets.push({ name, keys: new Set(originalKeys) });

  const inOrder = originalKeys.every((k, i) => k === expectedOrder[i]);
  if (inOrder) {
    console.log("  OK    " + name + "  (" + originalKeys.length + " keys, already sorted)");
    continue;
  }

  if (checkOnly) {
    failed = true;
    const at = originalKeys.findIndex((k, i) => k !== expectedOrder[i]);
    console.log("  FAIL  " + name + "  (" + originalKeys.length + " keys, not sorted)");
    console.log('        first difference at index ' + at + ': found "' + originalKeys[at] + '", expected "' + expectedOrder[at] + '"');
  } else {
    fs.writeFileSync(p, render(sortKeys(parsed)).replace(/\n/g, "\r\n"), "utf8");
    console.log("  FIXED " + name + "  (" + originalKeys.length + " keys, reordered)");
  }
}

// 两份文件的键集必须一致，缺键会让该语言回落成原始键名
//
// The two files must carry the same key set, or a missing key falls back to showing the raw key name
if (keySets.length > 1) {
  const base = keySets[0];
  for (const other of keySets.slice(1)) {
    const missing = [...base.keys].filter((k) => !other.keys.has(k));
    const extra = [...other.keys].filter((k) => !base.keys.has(k));
    if (missing.length || extra.length) {
      failed = true;
      console.log("  FAIL  key set differs: " + base.name + " vs " + other.name);
      if (missing.length) console.log("        missing in " + other.name + ": " + missing.join(", "));
      if (extra.length) console.log("        only in " + other.name + ": " + extra.join(", "));
    } else {
      console.log("  OK    key set matches: " + base.name + " == " + other.name + "  (" + base.keys.size + " keys)");
    }
  }
}

if (failed) {
  console.log("");
  console.log("Language files are out of order. Run: node dev-scripts/sort-i18n.js");
  process.exit(1);
}
