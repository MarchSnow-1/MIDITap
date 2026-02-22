# MIDITap 配置教程

配置文件位于 `config/mapping.json`，使用 JSON5 格式（支持注释）。

## 基本格式

```json
{
  "MIDI编号": "按键名称"
}
```

---

## 第一步：找到琴键的 MIDI 编号

启动程序后按下任意琴键，控制台会输出：

```
Note ON: 65, Velocity: 80
```

左边的数字（`65`）就是该琴键的 MIDI 编号。标准钢琴范围是 21（最低音 A0）到 108（最高音 C8）。

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

## 完整示例

```json
{
  // 程序使用 JSON5 读取，可以写注释

  // 白键区域
  "48": "a",
  "50": "s",
  "52": "d",
  "53": "f",
  "55": "g",
  "57": "h",
  "59": "j",

  // 黑键区域
  "49": "lshift",
  "51": "space",
  "54": "lctrl",

  // 高音区映射功能键
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
- 支持长按：按住琴键不放时，游戏内对应按键也会持续按下，松开琴键时同步释放
- 按下琴键时控制台显示 `Note ON: <编号>, Velocity: <力度>`，松开时显示 `Note OFF: <编号>`

## 控制台输出说明

| 信息 | 含义 |
|------|------|
| `Config Loaded: {...}` | 配置文件加载成功 |
| `note 48 -> 'a' (VK=0x41)` | 启动时显示的音符映射关系 |
| `Note ON: 65, Velocity: 80` | 琴键按下 |
| `Note OFF: 65` | 琴键释放 |
| `Can't find 'xxx' in VK Code List, Skipping...` | 配置中存在未知按键名，已跳过 |
| `SendInput Return 0` | 输入被拦截，请尝试以管理员身份运行 |