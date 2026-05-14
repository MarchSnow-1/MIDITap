<div align="center">

# MIDITap

![GitHub Release](https://img.shields.io/github/v/release/MarchSnow-1/MIDITap?style=for-the-badge)
![GitHub Last Commit](https://img.shields.io/github/last-commit/MarchSnow-1/MIDITap?style=for-the-badge)
![GitHub Repo stars](https://img.shields.io/github/stars/MarchSnow-1/MIDITap?style=for-the-badge)
[![Total Download](https://img.shields.io/github/downloads/MarchSnow-1/MIDITap/total?style=for-the-badge)](https://github.com/MarchSnow-1/MIDITap/releases)

[简体中文](README.md) | [English](README_EN.md)

将 MIDI 键盘输入实时映射为键盘按键的轻量级工具

</div>

## 📖 使用须知

MIDITap 是一个调用 Windows 原生 API 的低延迟 MIDI → 键盘映射工具

如遇到问题, 欢迎至 [Issues](../../issues) 进行反馈

## ✨ 功能一览

- 🎹 **原生映射**：调用 Windows API 实时将 MIDI 输入转换为键盘事件
- 🎵 **长按支持**：按住琴键时对应按键持续触发，松开即释放，手感自然
- 🔑 **完整按键支持**：字母、数字、功能键、方向键、小键盘、媒体键……一应俱全

## 🛠️ 环境要求

- Windows（x64）
- 一台 MIDI 设备

## 🚀 快速开始

1. 前往 [Release](../../releases) 页面下载最新版本

2. 将压缩包解压至任意目录

3. 双击 **MIDITap.exe** 即可启动, 无需安装

- [此处](/preset-configs) 提供了一些预设供参考或使用, 可下载查看

## 📚 使用教程

### GUI 界面

启动后界面分为三个标签页：**首页**、**配置**、**日志**

#### 首页 — 设备选择与监听

1. 左侧 **MIDI 设备** 列表显示当前可用的 MIDI 设备，点击选中即可
2. 选好设备后点击 **启动** 开始监听，此时按下 MIDI 琴键，对应键盘按键便会触发
3. 右侧 **实时预览** 实时显示当前按住的音符及其映射按键
4. **活动日志** 记录所有 MIDI 事件（按下/抬起/异常）

#### 配置页 — 映射管理

- **配置**：切换配置文件，点击即加载
- **刷新**：刷新配置文件列表
- **浏览**：打开存储配置文件的文件夹
- **配置名称**：修改配置文件在软件内的显示名称
- **新增映射**：三种方式捕捉输入——
  - **MIDI 音符**：点击输入框后弹奏 MIDI 琴键自动填入音符编号（需提前选择好 MIDI 设备并在首页启动）
  - **单键**：点击输入框后按键盘单键（a / enter / f1 等）
  - **组合键**：点击输入框后依次按下组合键（如 ctrl+shift+escape）
  - 单键与组合键互斥，以最后点击的为准
  - 填写完毕后点击 **添加** 添加映射
- **当前映射**：展示当前配置的所有映射，点击 `×` 可删除单条

#### 日志页

显示与首页 **活动日志** 相同内容的完整窗口，方便查看

## ✍️ 手动编写配置文件

配置文件位于 `config/` 目录，使用 JSON5 格式（支持注释和尾逗号）
你可以在该目录下创建多个 `.json` 文件，通过 GUI 配置页的下拉框切换

#### 基本步骤

1. 启动程序，在首页选择 MIDI 设备后点击 **启动**
2. 弹奏 MIDI 琴键，观察 **活动日志** 中的音符编号（如 `[CLI] 音符按下:71 力度:44 → (unbound)` 中的 `71`）
3. 在 `config/` 目录下新建或编辑 `.json` 文件，写入映射
4. 在配置页点击 **刷新**，然后在下拉框中切换到该配置文件

#### 配置结构

```json5
{
  "name": "我的配置",          // 必填：在 GUI 中显示的配置名称
  "48": "a",                  // 单键映射：MIDI 音符 → 按键名
  "50": "ctrl+shift+escape",  // 组合键映射：用 + 连接多个按键
  "60": "f1",                 // 功能键
  "71": "up"                  // 导航键
}
```

#### 规则

- MIDI 音符编号范围为 0–127，建议写成字符串（如 `"65"`）避免歧义
- 组合键在按下时从左到右依次触发，松开时从右到左依次释放
- 修改配置文件后无需重启，切换到对应配置或重新加载即可

#### 支持按键列表

| 分类 | 按键名 |
|---|---|
| 字母 | `a` `b` `c` `d` `e` `f` `g` `h` `i` `j` `k` `l` `m` `n` `o` `p` `q` `r` `s` `t` `u` `v` `w` `x` `y` `z` |
| 数字 | `0` `1` `2` `3` `4` `5` `6` `7` `8` `9` |
| 功能 | `f1` `f2` `f3` `f4` `f5` `f6` `f7` `f8` `f9` `f10` `f11` `f12` `f13` `f14` `f15` `f16` `f17` `f18` `f19` `f20` `f21` `f22` `f23` `f24` |
| 控制 | `enter` `space` `tab` `backspace` `shift` `ctrl` `alt` `escape` (别名 `esc`) `capslock` `pause` |
| 导航 | `up` `down` `left` `right` `home` `end` `pageup` `pagedown` `insert` `delete` |
| 修饰键（左右） | `lshift` `rshift` `lctrl` `rctrl` `lalt` `ralt` `lwin` `rwin` |
| 小键盘 | `num0` `num1` `num2` `num3` `num4` `num5` `num6` `num7` `num8` `num9` `numlock` `add` `subtract` `multiply` `divide` `decimal` `separator` |
| 系统 | `printscreen` `scrolllock` `apps` |
| 媒体 | `mute` `volumedown` `volumeup` `nexttrack` `prevtrack` `stop` `playpause` |
| 其他 | `select` `print` `execute` `help` `sleep` |

#### 美式键盘标点

| 按键 | 配置名 |
|---|---|
| `` ` `` | `backquote` |
| `-` | `minus` |
| `=` | `equal` |
| `[` | `lbracket` |
| `]` | `rbracket` |
| `\` | `backslash` |
| `;` | `semicolon` |
| `'` | `quote` |
| `,` | `comma` |
| `.` | `period` |
| `/` | `slash` |

## 从源码启动

### 环境要求

- [Node.js](https://nodejs.org/) 22+
- [Git](https://git-scm.com/)
- npm（随 Node.js 一同安装）

### 步骤

```bash
# 1. 克隆仓库
git clone https://github.com/MarchSnow-1/MIDITap.git
cd MIDITap

# 2. 全局安装 NeutralinoJS CLI
npm install -g @neutralinojs/neu

# 3. 安装 Node.js 依赖
npm install

# 4. 下载 NeutralinoJS 运行时二进制
neu update

# 5. 启动开发模式
neu run
```

## ⚠️ 免责声明

- 本工具通过模拟键盘输入的方式工作，原理与 AutoHotkey 等工具相同
- 本工具的设计用途是将 MIDI 设备映射为键盘输入，并非专为游戏开发
- 目前尚无因在游戏中使用本工具导致封号的案例，但部分相对严格的反作弊系统可能对第三方输入工具较为敏感
- **使用前请自行了解所在游戏的相关规定，下载本软件视为风险自担，开发者不承担任何责任**
