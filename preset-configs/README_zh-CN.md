# 预设配置

社区贡献的 MIDITap 键位预设合集

[English](README.md) | [简体中文](README_zh-CN.md)

## 📖 说明

本目录存放一些由社区贡献者提交的键位预设，可供快捷下载使用

如遇到问题，欢迎至 [Issues](../../issues) 进行反馈

## 🚀 怎么用

1. 下载本仓库，或者只单独下载你要的那个 `.json`
2. 把它放进 `MIDITap.exe` 旁边的 `config/` 目录
3. 回到程序，打开 **按键映射** 页，点 **刷新**，在下拉框里选中它
4. 开始使用

## 📋 预设列表

| 文件 | 名称 | 适配的软件 | 音符范围 | 输出键 | 说明 |
|---|---|---|---|---|---|
| `mapping-overfield.json` | OverField - 钢琴 | 开放空间 OverField | 48–83 | `asdfghj` / `qwertyu` / `1234567` | 三个八度的白键，每排一个八度 |

## 🤝 提交你的预设

欢迎提交你的预设！

提交预设需要注意以下几点：

1. 文件放 `preset-configs/` 下，命名成 `mapping-<用途>.json`
2. 文件开头用注释写明适配哪个软件，可以适当说明键位为什么这么排
3. 需要自己实测过正常使用再提交

怎么提交：

- **会用 git**：fork 一份，加上你的文件，提个 PR，维护者测试后会进行合并
- **不太会用 git**：开个 issue，把你配置好的 `.json` 贴上来或者作为附件上传，维护者会帮忙提交并备注贡献者

## ✍️ 手动编写配置格式

配置文件以 JSON5 格式存放在 `MIDITap.exe` 旁的 `config/` 目录 (支持注释与尾随逗号)
可以创建多个 `.json` 文件并在 GUI 下拉框中切换

### 基本步骤

1. 启动程序，在主页选择 MIDI 设备 (程序会自动开始监听)
2. 弹奏 MIDI 琴键，在主页面右侧的实时日志或是在 **日志** 页面中观察音符编号 (如 `按键按下: 65` 中的 `65`)
3. 在 `config/` 目录新建或编辑 `.json` 文件
4. 在按键映射页点击 **刷新**，在下拉框中切换到你的文件

### 配置结构

```json5
{
  "48": "a",                    // 单键：MIDI 音符 → 键名
  "50": "ctrl+shift+escape",    // 组合键：多个键名用 + 连接
  "60": "f1",                   // 功能键
  "62": "up"                    // 方向键
}
```

### 规则

- MIDI 音符号范围 0–127；建议使用字符串形式 (如 `"65"`)
- 组合键按下时从左到右触发，释放时从右到左抬起
- 编辑配置文件后重新加载或切换即可生效，无需重启
- 一个音符只能对应一个操作，比如 48 不能同时绑定单键+组合键或两个单键或两个组合键

### 支持按键列表

| 分类 | 键名 |
|---|---|
| 字母 | `a` `b` `c` `d` `e` `f` `g` `h` `i` `j` `k` `l` `m` `n` `o` `p` `q` `r` `s` `t` `u` `v` `w` `x` `y` `z` |
| 数字 | `0` `1` `2` `3` `4` `5` `6` `7` `8` `9` |
| 功能键 | `f1` `f2` `f3` `f4` `f5` `f6` `f7` `f8` `f9` `f10` `f11` `f12` `f13` `f14` `f15` `f16` `f17` `f18` `f19` `f20` `f21` `f22` `f23` `f24` |
| 控制键 | `enter` `space` `tab` `backspace` `shift` `ctrl` `alt` `escape` (别名 `esc`) `capslock` `pause` |
| 导航键 | `up` `down` `left` `right` `home` `end` `pageup` `pagedown` `insert` `delete` |
| 左右修饰键 | `lshift` `rshift` `lctrl` `rctrl` `lalt` `ralt` `lwin` `rwin` `win` (等同 `lwin`) |
| 小键盘 | `num0` `num1` `num2` `num3` `num4` `num5` `num6` `num7` `num8` `num9` `numlock` `add` `subtract` `multiply` `divide` `decimal` `separator` |
| 系统键 | `printscreen` `scrolllock` `apps` |
| 媒体键 | `mute` `volumedown` `volumeup` `nexttrack` `prevtrack` `stop` `playpause` |
| 其他 | `select` `print` `execute` `help` `sleep` |

### 美式键盘标点

| 按键 | 配置键名 |
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

## 💡 写预设的建议

- 用不到的音符就不写，不一定要凑满一整套
- 尽量避开一些常用快捷键，避免误触发