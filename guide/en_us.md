# MIDITap Configuration Guide

The configuration file is located at `config/mapping.json`, using JSON5 format (comments supported).

## Basic Format

```json
{
  "MIDI_number": "key_name"
}
```

---

## Step 1: Find the MIDI Number of a Key

Start the program and press any piano key. The console will output:

```
Note ON: 65, Velocity: 80
```

The first number (`65`) is the MIDI number of that key. Standard piano range is 21 (lowest A0) to 108 (highest C8).

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

## Full Example

```json
{
  // JSON5 format — comments are allowed

  // White keys
  "48": "a",
  "50": "s",
  "52": "d",
  "53": "f",
  "55": "g",
  "57": "h",
  "59": "j",

  // Black keys
  "49": "lshift",
  "51": "space",
  "54": "lctrl",

  // Upper register mapped to function keys
  "60": "f1",
  "62": "f2",
  "64": "f3"
}
```

---

## Notes

- MIDI numbers on the left must be quoted as strings (`"48"` not `48`)
- Multiple MIDI numbers can map to the same key name
- If a key name is not found in the supported list, a warning will be shown at startup and the entry will be skipped:
  ```
  Can't find 'xxx' in VK Code List, Skipping...
  ```
- Long press is supported: holding a piano key down keeps the mapped key held in-game, and releasing the piano key releases it simultaneously
- Pressing a key outputs `Note ON: <number>, Velocity: <velocity>` in the console; releasing outputs `Note OFF: <number>`