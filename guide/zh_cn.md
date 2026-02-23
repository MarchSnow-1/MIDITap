# MIDITap 配置教程

配置文件位于 `config/mapping.json`，使用 JSON5 格式（支持注释）

## 基本格式

```json
{
  "MIDI编号": "按键名称"
}
```

---

## 第一步：找到 MIDI 编号

启动程序后触发 MIDI 设备，控制台会输出：

```
Note ON: 65, Velocity: 80 (unbound)
```

左边的数字（`65`）就是该信号的 MIDI 编号。

---

## 第二步：选择对应的按键名称

### 字母键
直接写字母：`"a"` ~ `"z"`

### 数字键
直接写数字：`"0"` ~ `"9"`

### 功能键
`"f1"` ~ `"f12"`，以及 `"f13"` ~ `"f24"`

### 常用控制键

| 名称 | 按键 |
|------|------|
| `space` | 空格 |
| `enter` | 回车 |
| `backspace` | 退格 |
| `tab` | Tab |
| `escape` | Esc |
| `shift` / `lshift` / `rshift` | Shift（左/右） |
| `ctrl` / `lctrl` / `rctrl` | Ctrl（左/右） |
| `alt` / `lalt` / `ralt` | Alt（左/右） |
| `capslock` | 大写锁定 |
| `pause` | 暂停键 |

### 方向键
`"up"` `"down"` `"left"` `"right"`

### 导航键
`"pageup"` `"pagedown"` `"home"` `"end"` `"insert"` `"delete"`

### 小键盘

| 名称 | 按键 |
|------|------|
| `num0` ~ `num9` | 小键盘数字 |
| `add` | 小键盘 `+` |
| `subtract` | 小键盘 `-` |
| `multiply` | 小键盘 `*` |
| `divide` | 小键盘 `/` |
| `decimal` | 小键盘 `.` |
| `numlock` | Num Lock |

### 标点符号（美式键盘布局）

| 名称 | 对应按键 |
|------|----------|
| `minus` | `-` |
| `equal` | `=` |
| `lbracket` | `[` |
| `rbracket` | `]` |
| `semicolon` | `;` |
| `quote` | `'` |
| `comma` | `,` |
| `period` | `.` |
| `slash` | `/` |
| `backslash` | `\` |
| `backquote` | `` ` `` |

### 媒体键

| 名称 | 功能 |
|------|------|
| `mute` | 静音 |
| `volumeup` / `volumedown` | 音量 |
| `playpause` | 播放/暂停 |
| `nexttrack` / `prevtrack` | 切曲 |
| `stop` | 停止 |

---

## 指定 MIDI 端口

如果你有多个 MIDI 设备，可以在配置文件中指定端口号：

```json
{
  // 指定端口号（从 0 开始，0 = 第一个设备，1 = 第二个设备）
  "port": 1,

  "48": "a",
  "50": "s"
}
```

启动时控制台会列出所有可用设备和当前选中的端口：

```
Port 0: Other Device
Port 1: Digital Piano <-- selected
```

端口优先级（从高到低）：
1. 命令行参数 `--port`, 优先级最高，有传入参数即强制使用
2. 配置文件中的 `port` 字段
3. 若以上两种均未配置, 默认使用 `0`（第一个设备）

```bash
# 命令行指定端口，忽略配置文件中的 port
MIDITap.exe --port 1
```

---

## 完整示例

```json
{
  // 程序使用 JSON5 读取，可以写注释

  // 指定端口号（从 0 开始，0 = 第一个设备，1 = 第二个设备）
  "port": 1,

  // 映射关系配置
  "48": "a",
  "50": "s",
  "52": "d",
  "53": "f",
  "55": "g",
  "57": "h",
  "59": "j",
  "49": "lshift",
  "51": "space",
  "54": "lctrl",
  "60": "f1",
  "62": "f2",
  "64": "f3"
}
```

---

## 注意事项

- MIDI 编号左侧必须带引号，写成字符串形式（`"48"` 而不是 `48`）
- 同一个按键名可以映射给多个不同的 MIDI 编号
- 启动时若按键名不在支持列表内，会显示警告并跳过该条目：
  ```
  Can't find 'xxx' in VK Code List, Skipping...
  ```
- 支持长按：按住 MIDI 设备按键不放时，对应按键也会持续按下，松开时同步释放
- 触发信号时控制台显示 `Note ON: <编号>, Velocity: <力度>`，释放时显示 `Note OFF: <编号>`

## 控制台输出说明

| 信息 | 含义 |
|------|------|
| `Config Loaded: {...}` | 配置文件加载成功 |
| `note 48 -> 'a' (VK=0x41)` | 启动时显示的信号映射关系 |
| `Note ON: 65, Velocity: 80, Key: 'r'` | 信号触发（已绑定，会显示绑定的按键） |
| `Note ON: 65, Velocity: 80 (unbound)` | 信号触发（未绑定） |
| `Note OFF: 65, Key: 'r'` | 信号释放（已绑定） |
| `Note OFF: 65 (unbound)` | 信号释放（未绑定） |
| `Can't find 'xxx' in VK Code List, Skipping...` | 配置中存在未知按键名，已跳过 |
| `SendInput Return 0` | 输入被拦截，请尝试以管理员身份运行 |