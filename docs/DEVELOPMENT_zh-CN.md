# 开发流程

[English](DEVELOPMENT.md) | [简体中文](DEVELOPMENT_zh-CN.md)

本文件讲**怎么跑起来**：环境、命令、脚本各自管什么
项目的规则与约定（安全、依赖、注释、提交信息、禁止事项）在 [`AGENTS.md`](../AGENTS.md)，两者冲突时以 `AGENTS.md` 为准

---

## 1. 环境

| 需要 | 版本 | 用途 |
|---|---|---|
| .NET SDK | 10.0（见 `global.json`） | 构建与测试 |
| Node.js | 22 | `dev-scripts/sort-i18n.js`、发布说明 |
| PowerShell | 7（端到端测试）；5.1 也须可用（随包脚本） | 脚本与端到端测试 |
| Inno Setup | 6 | 只在打包安装包时需要 |

端到端测试需要 PowerShell 7：`e2e-log.ps1` 解开整天归档用的是 `System.Formats.Tar`，5.1 上没有这个类型
随包发布的 `scripts/apply-update.ps1` 仍须在 5.1 上可用，因为用户的机器上可能只有 5.1

---

## 2. 目录约定

```
src/MIDITap.Core               纯逻辑，不得引用 WinUI
src/MIDITap.App                WinUI 3 界面，只有它引用 WindowsAppSDK 与 NAudio
src/MIDITap.Core.Tests         单元测试，无需硬件
src/MIDITap.Integration.Tests  集成测试，需要真实 MIDI 回环端口
scripts/                       随用户包发布的脚本
dev-scripts/                   只在仓库中的开发辅助脚本
.dev/                          临时排查脚本（已 gitignore）
installer/                     Inno Setup 脚本
preset-configs/                预设配置与说明
```

判断一个脚本该放哪：**用户需不需要它**

需要就放 `scripts/`，不需要就放 `dev-scripts/`，本地一次性使用的脚本可以放入 `.dev/` 目录 (已加入 .gitignore)

---

## 3. 改动的完整流程

### 3.1 先读再改

每个源文件开头说明了它为什么这样写

改动前先读对应注释，不要凭猜测推断意图

### 3.2 改代码

- 业务规则属于 `MIDITap.Core`，界面只负责显示与转发
- 配置改动必须走 `ConfigEditor` / `Json5TextScanner` 的文本区间替换，不得整体重新序列化 (因为会丢用户注释)
- 注释中英双语，各写成**连续的一整段**，不逐行交替
- 注释行尾不加句号，一句话写在一行内

### 3.3 改文案

界面文案全部在 `src/MIDITap.App/i18n/*.json`

增删键之后**必须**运行排序脚本，两份文件的键集也须一致：

```bash
node dev-scripts/sort-i18n.js          # 排序并写回
node dev-scripts/sort-i18n.js --check  # 只检查，CI 用这个
```

CI 会在 `Build Dev` 与 `Release` 中执行 `--check`，未排序或键集不一致会直接失败

### 3.4 本地验证

每批改动至少跑这三条：

```bash
dotnet test src/MIDITap.Core.Tests/MIDITap.Core.Tests.csproj # 单元测试
dotnet build MIDITap.sln -c Debug # 构建 Debug
pwsh dev-scripts/e2e-all.ps1 # 端到端测试（依次跑三个用例脚本）
```

改动涉及 MIDI、按键注入或界面行为时，**端到端测试不可省略**

它需要一个 MIDI 回环端口（loopMIDI 的 `MIDITap-TestConfig`）与一个交互式桌面

CI 上没有这两样，因此它只在本地跑，CI 只检查语法

端到端测试分成一个共用库加三个用例脚本：

- `e2e-common.ps1` 断言、MIDI 打包与发送、按键状态、界面读取
- `e2e-seed.ps1` 现场准备与还原（测试配置、日志种子），结束时必定还原
- `e2e-app.ps1` 应用生命周期（初始化、启动、就绪、收尾、汇总），并把上面两个库点源进来

用例脚本只点源 `e2e-app.ps1`。三个都能单独运行，各自启动一次应用；`e2e-all.ps1` 依次跑完三个

### 3.5 提交

- 每个提交都必须能构建，不留半成品
- 一次提交只做一件事，便于回溯
- 提交信息用英文、约定式提交、**只写一行**

---

## 4. 验证脚本

| 脚本 | 作用 | 何时跑 |
|---|---|---|
| `dev-scripts/sort-i18n.js --check` | 语言文件排序与键集 | 改过 i18n |
| `dev-scripts/e2e-all.ps1` | 依次跑下面三个端到端脚本 | 改动涉及 MIDI、界面或日志 |
| `dev-scripts/e2e-midi.ps1` | 真实 MIDI 到按键注入 | 改动涉及 MIDI 或注入 |
| `dev-scripts/e2e-ui.ps1` | 日志页、主页实时预览、切页后补回 | 改动涉及界面 |
| `dev-scripts/e2e-log.ps1` | 日志落盘与启动时的整理 | 改动涉及日志 |
| `dev-scripts/key-monitor.ps1` | 观察实际被注入的按键 | 排查按键问题 |
| `dev-scripts/input-sink.ps1` | 按键接收端 | 配合上一个使用 |
