#!/usr/bin/env node
// sync-version.js — 把 package.json 的 version 同步进各 .csproj 的 <Version>
//
// 为什么需要：npm version 只改 package.json，而本地构建读的是 csproj 里的 <Version>
// 两者会各自漂移且都不会报错 —— 直到某天发现界面上显示的版本号和实际构建的不是一回事
// 因此把它挂进 npm 的 version 钩子（该钩子在版本号已改、提交之前运行），一起提交
//
// 注意：本文件是**开发期工具，不随用户包发布**
// 打包只复制 scripts/*.ps1（见 .github/workflows/dev.yml 与 release.yml）
// 因此这里的 .js 不在发布范围内
// 若将来改成复制 scripts/*，必须先把本文件移出去
//
// sync-version.js — copies package.json version into each csproj Version element
//
// Why: npm version only edits package.json, while a local build reads Version from the csproj
// So the two drift apart silently
// That lasts until the version shown in the UI stops matching the build
// This script is wired into npm version hook, which runs after the bump and before the commit
// That way both land in the same commit
//
// Note: this is a DEVELOPMENT-ONLY tool that does not ship with the user package
// Packaging copies scripts/*.ps1 only (see .github/workflows/dev.yml and release.yml)
// So a .js here is outside the release scope
// If that glob ever becomes scripts/*, move this file out first

const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const version = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8")).version;

if (!version) {
  console.error("sync-version: package.json 里没有 version / package.json has no version");
  process.exit(1);
}

// 只认源码树里的项目文件：obj/ 与 bin/ 下面是构建产物，不该被改写
// Only source-tree project files: anything under obj/ or bin/ is build output and must not be rewritten
const srcDir = path.join(root, "src");
const sep = path.sep;
const projects = fs
  .readdirSync(srcDir, { recursive: true })
  .map((p) => path.join(srcDir, p.toString()))
  .filter((p) => p.endsWith(".csproj"))
  .filter((p) => !p.includes(sep + "obj" + sep) && !p.includes(sep + "bin" + sep));

const pattern = /<Version>[^<]*<\/Version>/;
const changed = [];
const unchanged = [];
const withoutVersion = [];

for (const file of projects) {
  const rel = path.relative(root, file);
  const text = fs.readFileSync(file, "utf8");

  // 没有 <Version> 的项目（例如测试项目）按设计跳过，但要记录下来
  // 这样"某个项目忘了声明版本"仍然看得见
  // Projects without a Version element (test projects, by design) are skipped but recorded
  // So a project that simply forgot to declare one is still visible
  if (!pattern.test(text)) {
    withoutVersion.push(rel);
    continue;
  }

  const next = text.replace(pattern, "<Version>" + version + "</Version>");
  if (next === text) {
    unchanged.push(rel);
  } else {
    fs.writeFileSync(file, next, "utf8");
    changed.push(rel);
  }
}

if (changed.length === 0 && unchanged.length === 0) {
  console.error("sync-version: 没有找到任何带 <Version> 的 csproj, 项目结构可能变化");
  console.error("sync-version: no csproj with a Version element was found; the layout may have changed");
  process.exit(1);
}

if (changed.length > 0) {
  console.log("sync-version: 已把 " + version + " 写入 " + changed.length + " 个文件");
  for (const f of changed) console.log("  " + f);
} else {
  console.log("sync-version: " + unchanged.length + " 个 csproj 已是 " + version);
}

if (withoutVersion.length > 0) {
  console.log("sync-version: 跳过 " + withoutVersion.length + " 个未声明 <Version> 的项目: " + withoutVersion.join(", "));
}
