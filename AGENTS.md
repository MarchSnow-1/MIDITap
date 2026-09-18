# AGENTS.md

本文件为人类开发者/Coding Agent 所写，用于规范化本项目开发过程中的一些细节

Written for human developers and coding agents, to lay out the details of how this project is developed.

## 0. 先看什么 / Read first

1. `README.md` 与 `README_zh-CN.md` —— 功能、安装、更新机制
2. `docs/DEVELOPMENT_zh-CN.md` —— 环境、常用命令、各脚本分别管什么
3. 要改哪个模块，先读那个文件开头的注释

本项目每个源文件开头均说明了其编写原因。请遵守第三条，不要凭猜测推断代码意图，也不要进行破坏性修改

1. `README.md` and `README_zh-CN.md` — features, installation, update mechanism
2. `docs/DEVELOPMENT.md` — environment, common commands, and what each script owns
3. Before changing a module, read the header comment of that file

Every source file explains at its top why it is written that way. Follow rule 3 above: do not infer intent
by guesswork, and do not make destructive changes.

## 1. 这是什么 / What this is

MIDITap 将 MIDI 设备（电钢琴、MIDI 键盘）的输入**实时转换为键盘按键**，使 MIDI 设备可当键盘使用

- 仅支持 Windows 64 位，最低版本 Windows 10 1809
- 发布形式为自包含便携包，解压后即可运行，用户无需安装 .NET、Node.js 或 Windows App SDK
- 默认在最新的 `v2` 分支上开发，技术栈为 C# + WinUI 3

MIDITap turns input from a MIDI device (digital piano, MIDI keyboard) into **keyboard key presses in real
time**, so a MIDI device can act as a keyboard.

- Windows 64-bit only, minimum version Windows 10 1809
- Distributed as a self-contained portable package: unzip and run, with no .NET, Node.js or Windows App SDK
  installation required
- Developed on the `v2` branch (currently the latest), using C# + WinUI 3

## 2. 四个项目 / Projects

```
src/MIDITap.Core                纯逻辑，与 WinUI 隔离，依赖需谨慎评估（见 2.1）
src/MIDITap.App                 WinUI 3 界面，只有它引用 WindowsAppSDK 和 NAudio
src/MIDITap.Core.Tests          单元测试，不需要硬件
src/MIDITap.Integration.Tests   集成测试，要真实 MIDI 驱动
```

```
src/MIDITap.Core                Pure logic, isolated from WinUI, dependencies need careful review (see 2.1)
src/MIDITap.App                 WinUI 3 UI; the only project referencing WindowsAppSDK and NAudio
src/MIDITap.Core.Tests          Unit tests, no hardware needed
src/MIDITap.Integration.Tests   Integration tests, need a real MIDI driver
```

### 2.1 Core 与界面隔离，引入依赖前需评估

`MIDITap.Core` 不得引用 `WindowsAppSDK` 或 WinUI。它必须能在无桌面环境下测试，
整套测试都建立在其之上，且界面重构不应有可能破坏核心逻辑

第三方依赖的处理原则：**优先采用成熟的库，不要自行实现**

自行实现容易在边界条件上出错，版本比较、字符编码、时间处理尤其如此
这类错误往往只在特定输入下才会暴露，而成熟库的使用者众多，边界情况已被逐一发现并修正

引入依赖前应确认：

- 它消除的是**容易出错**的代码，还是仅减少了几行？
- 它会引入多少传递依赖，体积与供应链风险是否可接受？
- 项目是否仍在维护，许可证是否允许当前用法？

简单来说：**依赖并非不可引入，但不应随意引入**

`MIDITap.Core` must not reference `WindowsAppSDK` or WinUI. It must be testable without a desktop, the
entire test suite rests on it, and UI rework must not be able to break core logic.

Third-party dependencies: **prefer a mature library over a hand-written implementation.**

Hand-written code tends to fail at boundary conditions, particularly version comparison, character encoding
and time handling. Such errors often surface only under specific inputs, whereas a mature library has many
users and its boundary cases have already been found and fixed.

Confirm the following before adding one:

- Does it eliminate **error-prone** code, or merely save a few lines?
- How many transitive dependencies does it introduce, and are the size and supply-chain cost acceptable?
- Is it still maintained, and does the licence permit the intended use?

In short: **dependencies are permitted, but must not be added arbitrarily.**

### 2.2 App 仅为薄层

界面只负责显示与转发，**业务规则属于 Core**

若在 `.xaml.cs` 中进行判断（版本比较、音符状态推导、路径拼接），说明该逻辑放在了错误的位置

The UI only displays and forwards; **business rules belong in Core**.

If decisions are being made in `.xaml.cs` (version comparison, note-state derivation, path construction),
that logic is in the wrong place.

### 2.3 配置以文本为准，修改须保留注释与格式

`config/*.json` 是 **JSON5**，允许写注释

用户会在其中写入注释，**那些注释属于用户**

因此修改配置必须经由 `ConfigEditor` / `Json5TextScanner` 的**文本区间替换**，
不得将整个文件重新序列化为规范 JSON —— 那会丢失用户的注释、键顺序与缩进

`config/*.json` files are **JSON5**, so comments are allowed.

Users write comments there, and **those comments belong to the user**.

Config edits must therefore go through the **text-range splicing** in `ConfigEditor` / `Json5TextScanner`.
The file must never be re-serialised into canonical JSON — that discards the user's comments, key order and
indentation.

### 2.4 用户数据与程序分离

- `config/` —— 映射配置，属于用户
- `.storage/` —— 应用设置（语言、主题、日志开关、更新代理）

打包、更新、卸载均须**跳过**这两棵子树

升级导致用户数据丢失属于严重缺陷

- `config/` — mapping config, owned by the user
- `.storage/` — app settings (language, theme, log toggle, update proxy)

Packaging, updating and uninstalling must all **skip** these two subtrees.

User data lost during an upgrade constitutes a serious defect.

## 3. 安全 / Security

### 3.1 按键注入属特权操作

注入经由 `SendInput`，**只能由真实 MIDI 事件驱动**

不得为便于测试而加入「直接触发注入」的后门或调试开关

Injection goes through `SendInput`, and **must be driven only by real MIDI events**.

Backdoors or debug switches that trigger injection directly must not be added for testing convenience.

### 3.2 配置名须防范路径穿越

配置名来自用户输入。`ConfigLocator` 拒绝绝对路径，拒绝以 `..` 开头的输入

后续新增任何「按名称查找文件」的入口，均须复用该检查

Config names come from user input. `ConfigLocator` reject absolute paths, and reject anything starting with `..`

Any future entry point that looks up a file by name must reuse this check.

### 3.3 自更新须满足三项独立条件

1. **校验完整性** —— 用 GitHub 自带的 SHA-256（API 的 `assets[].digest`，或网页路径 `expanded_assets` 里同一个摘要），**先校验再解压**，顺序反了校验等于没做
2. **确认包是我们的** —— 解压后必须有 `MIDITap.exe` 和 `scripts/apply-update.ps1`，摘要对**不代表**包是本应用
3. **替换时排除用户数据** —— 跳过 `config/` 和 `.storage/`，这条在 Core 和 `apply-update.ps1` 里**各写一遍**，因为辅助脚本是独立进程，不共享代码

另有两项：解压须防范 **zip-slip**，校验不通过时须**拒绝安装并删除已下载的包**

1. **Verify integrity** — use GitHub's own SHA-256 (the API's `assets[].digest`, or the same digest on the web
   path inside `expanded_assets`). **Verify before extracting**; reversing the order makes verification
   pointless
2. **Confirm the package is ours** — after extraction there must be `MIDITap.exe` and
   `scripts/apply-update.ps1`. A matching digest **does not** mean the package is this app
3. **Exclude user data when replacing** — skip `config/` and `.storage/`. This is **written twice**, in Core
   and in `apply-update.ps1`, because the helper script is a separate process and shares no code

Two further requirements: extraction must guard against **zip-slips**, and a failed verification must
**refuse installation and delete the downloaded package**.

## 4. 改配置的约定 / Configuration

```
config/*.json    映射：MIDI note -> 按键（JSON5，保留注释）
.storage/        应用设置（语言、主题、日志开关、更新代理）
```

新增配置项须包含三项内容：明确的默认值、取值范围校验、中英双语文案
（`i18n` 下两个文件的键须一致）

与安全相关的开关，默认值应取更安全的一侧

```
config/*.json    Mappings: MIDI note -> key press (JSON5, comments preserved)
.storage/        App settings (language, theme, log toggle, update proxy)
```

A new config item must include three things: an explicit default, range validation, and both Chinese and
English copy (the keys in the two files under `i18n` must match).

For security-related switches, the default must be the safer option.

## 5. 工作方式 / Workflow

- **先说明再修改** —— 阐明目标与影响范围；涉及更新流程、用户数据或按键注入时，先给出方案
- **小步提交** —— 每个批次须保持可编译，一批只做一件事，并明确修改范围，便于后续回溯问题来源
- **未经允许不推送** —— 提交可以；推送分支、打 tag 与发布 Release 均由维护者决定
- 只格式化**本次修改的文件**，不进行全仓库格式化，不改动行尾
- 注释和文案统一使用**中英双语**，完成一个任务后需排查本次变更是否存在遗漏注释的地方，如有遗漏需补全
- 注释须写成**连续的整段中文 + 连续的整段英文**，不得中英逐行交替
- **临时脚本一律放 `.dev/`** —— 那是供测试使用的忽略路径，放测试脚本、临时工具都行。
  `scripts/` 下的内容会被打进用户发布包，因此排查用脚本不得放在那里
- **禁止妄下定论** —— 注释、文案与说明中不得使用「永久」「永远」「绝不」「一定会」这类
  无限定范围的断言，也不得把当前观察到的情况说成普遍规律。改为写清边界：在什么条件下、
  哪个函数走哪条分支、因此不会出现什么结果。例如不写「内容永远不可能比控件宽」，
  而写「尺寸在 `ArrangeOverride` 里按最终分配值计算，因此内容不会超出控件宽度」

- **Explain before changing** — state the objective and the blast radius; for update flow, user data or key
  injection, propose an approach first
- **Commit in small steps** — each batch must remain compilable, one batch does one thing, and the change
  scope stays clear so the source of a problem can be traced later
- **Do not push without permission** — committing is permitted; pushing branches, creating tags and
  publishing releases are decided by the maintainer
- Format only **the files changed in this batch**. No repo-wide formatting, no line-ending changes
- Comments and copy are **bilingual (Chinese and English)** throughout. After completing a task, check whether
  any comments were missed in this change and fill them in
- Write comments as **one contiguous Chinese block followed by one contiguous English block**; never alternate
  line by line
- **Throwaway scripts belong in `.dev/`** — a git-ignored path for test scripts and temporary tools.
  Anything under `scripts/` ships to users, so investigation scripts must not be placed there
- **No unfounded absolute claims** — comments, copy and documentation must not use "permanently", "never",
  "always" or "cannot possibly" style claims with no stated scope, and must not present a currently observed
  behaviour as a general law. State the boundary instead: under which condition, which branch of which
  function runs, and therefore which outcome cannot occur. Rather than "the content can never be wider than
  the control", write "sizing happens in `ArrangeOverride` from the final allotted size, so the content
  does not exceed the control's width"

### 5.1 提交信息 / Commit messages

提交信息**用英文写**，遵循[约定式提交](https://www.conventionalcommits.org/)：
`<type>(<scope>): <description>`，**只写一行**，简洁有力地说明这次改了什么

英语是通用语言，使用英语作为提交信息可以覆盖更多人群，便于后期其他外部开发者做出贡献

不写多行小作文
把写小作文的精力放到**完善注释**上去
提交信息只需要让人知道"这一步做了什么"，而"为什么这么做"应当写在代码注释里，那里才是下一个读代码的人会看的地方

常用 type：`feat` `fix` `refactor` `perf` `docs` `test` `build` `ci` `chore` `style`

```
feat(update): use the Semver library for version comparison
fix(capture): capture modifier keys such as the Windows key
docs: document the .dev/ convention in AGENTS.md
```

Commit messages are **written in English** and follow
[Conventional Commits](https://www.conventionalcommits.org/):
`<type>(<scope>): <description>`, **on a single line**, stating concisely what changed.

English is the lingua franca.
So writing commit messages in English reaches a wider audience and makes it easier for other external developers to contribute later.

No multi-paragraph essays.
Spend that effort on **improving the comments** instead.
The commit messageonly needs to say what this step did, while the reasoning belongs in a code comment, which is where the
next person to read the code will actually look.

Common types: `feat`, `fix`, `refactor`, `perf`, `docs`, `test`, `build`, `ci`, `chore`, `style`

## 6. 测试 / Testing

测试分为三层，各自职责不同：

| 层 | 位置 | 需要什么 | 在 CI 上 |
|---|---|---|---|
| 单元 | `src/MIDITap.Core.Tests` | 无 | 全跑 |
| 集成 | `src/MIDITap.Integration.Tests` | 真实 MIDI 回环端口 | **跳过**，CI 没有 MIDI 设备 |
| 端到端 | `dev-scripts/e2e-all.ps1` | 回环端口 + 交互式桌面 + PowerShell 7 | 只检查语法 |

端到端脚本都在 `dev-scripts/`，**不随用户包发布**

| 脚本 | 负责 | 怎么调 |
|---|---|---|
| `e2e-all.ps1` | 依次跑下面三个并汇总结果 | `pwsh dev-scripts/e2e-all.ps1` |
| `e2e-midi.ps1` | 按键注入：按下/抬起、边界音符 0 与 127、velocity 0、重复按下、引用计数、组合键、未映射音符、非音符消息、突发 | `pwsh dev-scripts/e2e-midi.ps1` |
| `e2e-ui.ps1` | 界面：日志页记录、主页实时预览、切页后仍被按住的音符补回 | `pwsh dev-scripts/e2e-ui.ps1` |
| `e2e-log.ps1` | 日志：开关打开后写文件（含补写开关打开前内存里已有的行）、启动时的整理 | `pwsh dev-scripts/e2e-log.ps1` |

三个用例脚本各自启动一次应用，因此互不干扰，也都能单独跑
公共参数：`-PortName`（默认 `MIDITap-TestConfig`）、`-AppDir`（默认构建输出目录）、`-SkipBuild`（跳过 `dotnet build`）
共用三个库：`e2e-common.ps1`（断言、MIDI 发送、界面读取）、`e2e-seed.ps1`（现场准备与还原）、`e2e-app.ps1`（应用生命周期），用例脚本只点源 `e2e-app.ps1`

具体要求：

- 无回环端口时，集成测试**跳过而非失败**（`[MidiFact]` / `[MidiTheory]`）；
  CI 会断言其确实被跳过 —— 静默跳过会让人误以为一切正常
- **测试更新相关的网络逻辑应使用本地服务器，不连接公网**（`MiniHttpServer`）
- 修复缺陷须附带**回归测试**，安全与更新相关的问题尤其如此
- 每批至少执行：单元测试 + `dotnet build MIDITap.sln -c Debug`

Three layers, each with its own job:

| Layer | Location | Needs | On CI |
|---|---|---|---|
| Unit | `src/MIDITap.Core.Tests` | Nothing | All run |
| Integration | `src/MIDITap.Integration.Tests` | A real MIDI loopback port | **Skipped**, CI has no MIDI device |
| End-to-end | `dev-scripts/e2e-all.ps1` | Loopback port + interactive desktop + PowerShell 7 | Syntax check only |

The end-to-end scripts live in `dev-scripts/` and are **not packaged**

| Script | Owns | How to run |
|---|---|---|
| `e2e-all.ps1` | runs the three below in turn and summarises the result | `pwsh dev-scripts/e2e-all.ps1` |
| `e2e-midi.ps1` | injection: press and release, boundary notes 0 and 127, velocity 0, a repeated note-on, reference counting, combos, unmapped notes, non-note messages, a burst | `pwsh dev-scripts/e2e-midi.ps1` |
| `e2e-ui.ps1` | the UI: what the log page records, the home page live preview, notes still held replayed after a page switch | `pwsh dev-scripts/e2e-ui.ps1` |
| `e2e-log.ps1` | logging: writing the file once the switch is on (including backfilling what was already in memory), the start-up housekeeping | `pwsh dev-scripts/e2e-log.ps1` |

Each case script starts the app once, so they do not interfere and each runs on its own
Common parameters: `-PortName` (default `MIDITap-TestConfig`), `-AppDir` (default the build output directory), `-SkipBuild` (skip `dotnet build`)
Three shared libraries: `e2e-common.ps1` (assertions, MIDI sending, UI reading), `e2e-seed.ps1` (preparing and restoring the scene), `e2e-app.ps1` (the app lifecycle); a case script dot-sources `e2e-app.ps1` alone

Specific requirements:

- With no loopback port, integration tests **skip rather than fail** (`[MidiFact]` / `[MidiTheory]`). CI
  asserts that they were in fact skipped — a silent skip would suggest everything is working
- **Update-related network logic must be tested against a local server, not the public internet**
  (`MiniHttpServer`)
- Defect fixes must include a **regression test**; this applies especially to security and update issues
- Every batch must run at least: the unit tests plus `dotnet build MIDITap.sln -c Debug`

### 6.1 测试数据与示例值 / Test data and fixtures

**代码与测试数据应该使用规范、清晰易懂的通用名称进行命名**

- 示例值一律用 `example` / `Example` / `EXAMPLE` / `examples` 这类**通用占位**，
  例如用户名用 `example`，域名用 `example.com`，仓库用 `username/repository`
- **不得**使用真实域名、仓库名、用户名、设备序列号或使用开发机器上的绝对路径来编写代码/测试用例/注释，也不得使用**由它们截取或变体而来的名字**
- `src/MIDITap.Core/Update/UpdateRepository.cs` 是**唯一**允许写真实仓库地址的地方
  那是程序查更新所必需的功能配置，不属于「关联」

**Code and test data must be named with standard, clear, generic terms**

- Fixtures use generic placeholders such as `example` / `Example` / `EXAMPLE` / `examples` —
  for instance `example` for a user name, `example.com` for a domain and `username/repository`
  for a repository
- Do **not** write a real domain, repository name, user name or device serial into code, test cases or
  comments, and do not use an absolute path from a development machine — nor a name **derived or
  abbreviated** from any of them
- `src/MIDITap.Core/Update/UpdateRepository.cs` is the **only** place a real repository address may
  appear; the app needs it to check for updates, so it is functional configuration, not an association

## 7. 针对Agent的协作方式建议

- 分批修改 - 对用户下定的任务进行拆分，分多次进行小幅度的修改，适当多用 commit，避免一次性提交大量代码
- 可维护性 - 保证每次 commit 的代码完全可构建并具有清晰易懂的 commit 消息，便于后期 review 与分析问题
- 保持透明 — 每批工作完成后报告：已完成事项、验证结果、遗留风险
- 存在不确定时先行确认，禁止猜测
- 全部代码注释与文档需保持中英双语，若变更代码内容需同步更新对应注释
- 尽量使用简洁易懂的语言来编写注释，降低维护成本
- 不要在注释行尾添加句号，除非一行有两个句子需要区分
- 如需多行注释，保证一句话在一行注释内写完，严禁一句话分多行来写
- 修改前端界面 **必须** 同步更新全部 i18n 文件并进行实机验证
- 严禁修改 AGENTS.md 文件

- **Work in batches** — split a task into several small, incremental changes and commit often; avoid landing a large body of code in a single commit
- **Maintainability** — every commit must build and carry a clear, readable message, so later review and problem tracing stay easy
- Be transparent — after each batch, report what was completed, the verification result and any remaining risks
- When uncertain, confirm first; guessing is not permitted
- All code comments and documentation must be bilingual (Chinese and English), and any change to code content must update the corresponding comments
- Use simple, easy-to-understand language when writing comments to reduce maintenance cost
- Do not add a period at the end of a comment line unless two sentences on one line need to be distinguished
- If a multi-line comment is needed, ensure that a sentence is written in one line of comment; it is strictly forbidden to split a sentence into multiple lines
- Changing the front-end interface **must** synchronously update all i18n files and perform real-machine verification
- Modifying AGENTS.md is strictly forbidden