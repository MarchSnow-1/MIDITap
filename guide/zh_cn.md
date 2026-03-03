# MIDITap 配置教程

本教程覆盖完整配置流程：从基础映射到组合键，再到命令行参数与配置校验。

## 1. 快速开始（最短路径）

1. 启动程序并按下 MIDI 设备上的一个键。
2. 在控制台记下对应的 MIDI 编号（例如 `Note ON: 65, Velocity: 80` 中的 `65`）。
3. 在 `config/mapping.json` 里写入映射并重启程序。

最小可用示例：

```json5
{
  "65": "a"
}
```

## 2. 配置文件结构

配置文件默认路径是 `config/mapping.json`，格式为 JSON5（支持注释、尾逗号）。

```json5
{
  // 可选：默认 MIDI 端口号（从 0 开始）
  "port": 0,

  // MIDI 编号 -> 按键名
  "48": "a",
  "50": "enter",
  "84": "ctrl+shift+esc"
}
```

规则：
- MIDI 编号建议写成字符串（例如 `"48"`）
- `port` 是可选字段
- 其余字段会作为按键映射解析

## 3. 键名规则（单键与组合键）

### 3.1 单键写法

不包含 `+` 的值都按单键处理，即使是多字符名称（例如 `enter`、`delete`、`escape`）。

常用单键：
- 字母键：`a` ~ `z`
- 数字键：`0` ~ `9`
- 功能键：`f1` ~ `f24`
- 控制键：`enter` `space` `tab` `backspace` `shift` `ctrl` `alt`
- 导航键：`up` `down` `left` `right` `home` `end` `pageup` `pagedown` `insert` `delete`
- 小键盘：`num0` ~ `num9` `add` `subtract` `multiply` `divide` `decimal` `numlock`

### 3.2 组合键写法

包含 `+` 的值按组合键处理，按 `+` 拆分并按顺序执行。

```json5
{
  "84": "ctrl+shift+esc",
  "85": "alt+tab"
}
```

执行顺序：
- `Note ON`：从左到右依次按下。
- `Note OFF`：从右到左依次抬起。

### 3.3 Esc 别名

以下写法等价：
- `esc`
- `escape`

例如 `ctrl+shift+esc` 与 `ctrl+shift+escape` 都可用。

## 4. MIDI 端口选择规则

端口优先级（从高到低）：
1. 命令行 `-port <index>`
2. 配置文件中的 `"port"`
3. 默认 `0`

示例：

```bash
MIDITap.exe -port 1
```

## 5. 命令行参数（CLI）

可用参数：
- `-port <index>`：指定 MIDI 端口号
- `-list-ports`：列出所有 MIDI 输入端口并退出
- `-config <path>`：指定配置文件路径（支持绝对/相对路径）
- `-check-config`：严格校验配置，输出 `true/false`
- `-verbose` / `-v`：输出详细日志（devmode 下会强制开启）
- `-help` / `-h`：显示帮助

配置文件选择优先级：
1. 传入 `-config` 时使用指定文件
2. 否则若程序目录存在 `.dev`，默认使用 `config/mapping-dev.json`
3. 否则默认使用 `config/mapping.json`

示例：

```bash
MIDITap.exe -list-ports
MIDITap.exe -config .\config\mapping.json
MIDITap.exe -config .\config\mapping-dev.json
MIDITap.exe -check-config -config .\config\mapping.json
MIDITap.exe -check-config -config .\config\mapping-dev.json
MIDITap.exe -help
```

## 6. 配置校验模式（严格）

命令：

```bash
MIDITap.exe -check-config
```

返回行为：
- 配置合法：输出 `true`，退出码 `0`
- 配置非法：输出 `false`，退出码 `1`

严格模式下，只要存在任意非法项即失败，例如：
- MIDI 编号越界
- 未知按键名
- 组合键写法错误

## 7. 注意事项

- 启动时若键名无效，会提示并跳过该条映射：
  ```
  Can't find 'xxx' in VK Code List, skipping...
  ```
- 支持长按：MIDI 按住不放时，映射按键也保持按下；松开后同步释放。
- 修改配置后需要重启程序才会生效。

## 8. 控制台输出参考

| 信息 | 含义 |
|------|------|
| `Config Loaded (...): {...}` | 配置加载成功 |
| `note 48 -> 'a' (VK=0x41)` | 启动时显示映射关系 |
| `Note ON: 65, Velocity: 80, Key: 'r'` | 触发已绑定映射 |
| `Note ON: 65, Velocity: 80 (unbound)` | 触发未绑定映射 |
| `Note OFF: 65, Key: 'r'` | 释放已绑定映射 |
| `Note OFF: 65 (unbound)` | 释放未绑定映射 |
| `SendInput Return 0` | 输入被拦截，建议尝试管理员权限运行 |
