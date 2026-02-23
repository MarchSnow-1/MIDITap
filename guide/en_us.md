# MIDITap Configuration Guide

The configuration file is located at `config/mapping.json`, using JSON5 format (comments supported).

## Basic Format

```json
{
  "MIDI_number": "key_name"
}
```

---

## Step 1: Find the MIDI Number

Start the program and trigger your MIDI device. The console will output:

```
Note ON: 65, Velocity: 80 (unbound)
```

The first number (`65`) is the MIDI number of that signal.

---

## Step 2: Choose the Corresponding Key Name

### Letter Keys
Write the letter directly: `"a"` ~ `"z"`

### Number Keys
Write the number directly: `"0"` ~ `"9"`

### Function Keys
`"f1"` ~ `"f12"`, and `"f13"` ~ `"f24"`

### Common Control Keys

| Name | Key |
|------|-----|
| `space` | Space |
| `enter` | Enter |
| `backspace` | Backspace |
| `tab` | Tab |
| `escape` | Esc |
| `shift` / `lshift` / `rshift` | Shift (left/right) |
| `ctrl` / `lctrl` / `rctrl` | Ctrl (left/right) |
| `alt` / `lalt` / `ralt` | Alt (left/right) |
| `capslock` | Caps Lock |
| `pause` | Pause |

### Arrow Keys
`"up"` `"down"` `"left"` `"right"`

### Navigation Keys
`"pageup"` `"pagedown"` `"home"` `"end"` `"insert"` `"delete"`

### Numpad Keys

| Name | Key |
|------|-----|
| `num0` ~ `num9` | Numpad digits |
| `add` | Numpad `+` |
| `subtract` | Numpad `-` |
| `multiply` | Numpad `*` |
| `divide` | Numpad `/` |
| `decimal` | Numpad `.` |
| `numlock` | Num Lock |

### Punctuation (US keyboard layout)

| Name | Key |
|------|-----|
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

### Media Keys

| Name | Function |
|------|----------|
| `mute` | Mute |
| `volumeup` / `volumedown` | Volume |
| `playpause` | Play / Pause |
| `nexttrack` / `prevtrack` | Next / Previous track |
| `stop` | Stop |

---

## Specify MIDI Port

If you have multiple MIDI devices, you can specify the port number in the config file:
```json
{
  // Port index (zero-indexed: 0 = first device, 1 = second device)
  "port": 1,

  "48": "a",
  "50": "s"
}
```

On startup, all available devices and the selected port will be listed in the console:
```
Port 0: Other Device
Port 1: Digital Piano <-- selected
```

Port priority (highest to lowest):
1. Command line argument `--port`, highest priority, overrides everything when provided
2. `port` field in the config file
3. Defaults to `0` (first device) if neither is configured
```bash
# Specify port via command line, overrides config file
MIDITap.exe --port 1
```

---

## Full Example

```json
{
  // JSON5 format — comments are allowed

  // Port index (zero-indexed: 0 = first device, 1 = second device)
  "port": 1,

  // Mapping configuration
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

## Notes

- MIDI numbers must be quoted as strings (`"48"` not `48`)
- Multiple MIDI numbers can map to the same key name
- If a key name is not found in the supported list, a warning will be shown at startup and the entry will be skipped:
  ```
  Can't find 'xxx' in VK Code List, Skipping...
  ```
- Long press is supported: holding a MIDI device button keeps the mapped key held down, releasing it simultaneously
- Triggering a signal outputs `Note ON: <number>, Velocity: <velocity>` in the console; releasing outputs `Note OFF: <number>`

## Console Output Reference

| Message | Meaning |
|---------|---------|
| `Config Loaded: {...}` | Config file loaded successfully |
| `note 48 -> 'a' (VK=0x41)` | Signal mapping shown at startup |
| `Note ON: 65, Velocity: 80, Key: 'r'` | Signal triggered (bound, shows mapped key) |
| `Note ON: 65, Velocity: 80 (unbound)` | Signal triggered (not bound) |
| `Note OFF: 65, Key: 'r'` | Signal released (bound) |
| `Note OFF: 65 (unbound)` | Signal released (not bound) |
| `Can't find 'xxx' in VK Code List, Skipping...` | Unknown key name in config, skipped |
| `SendInput Return 0` | Input blocked, try running as Administrator |