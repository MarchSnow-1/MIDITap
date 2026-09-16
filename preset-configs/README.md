# Preset configs

A collection of community-contributed MIDITap key presets

[English](README.md) | [简体中文](README_zh-CN.md)

## 📖 About

This folder holds key presets submitted by community contributors, ready to download and use

If you encounter any issues, please feel free to submit feedback via [Issues](../../issues)

## 🚀 How to use

1. Download this repository, or just the single `.json` you want
2. Put it into the `config/` folder next to `MIDITap.exe`
3. Back in the app, open the **Mappings** page, click **Refresh**, then pick it in the dropdown
4. Start using it

## 📋 Preset list

| File | Name | Made for | Note range | Output keys | Description |
|---|---|---|---|---|---|
| `mapping-overfield.json` | OverField - Piano | OverField | 48–83 | `asdfghj` / `qwertyu` / `1234567` | Three octaves of white keys, one octave per row |

## 🤝 Submit your preset

Your preset is welcome here!

A few things to note when submitting:

1. Put the file in `preset-configs/` and name it `mapping-<purpose>.json`
2. Open the file with a comment naming the software it is made for, and you may also explain why the keys are laid out that way
3. Test it yourself and confirm it works before submitting

How to submit:

- **Comfortable with git**: fork the repository, add your file, and open a PR. A maintainer will test it and merge
- **Prefer not to use git**: open an issue and paste your finished `.json` or upload it as an attachment. A maintainer will commit it for you and credit you as the contributor

## ✍️ Writing a Config File Manually

Config files are JSON5 and live in the `config/` directory next to `MIDITap.exe` (comments and trailing commas are allowed)
You can create several `.json` files and switch between them in the GUI dropdown

### Basic Steps

1. Start the app and select your MIDI device on the Home page (monitoring starts automatically)
2. Play MIDI keys and read the note number from the live log on the right of the Home page, or on the **Log** page (e.g. the `65` in `Note on: 65`)
3. Create or edit a `.json` file in the `config/` directory
4. Click **Refresh** on the Mappings page, then switch to your file in the dropdown

### Config Structure

```json5
{
  "48": "a",                    // Single key: MIDI note → key name
  "50": "ctrl+shift+escape",    // Combo: key names joined with +
  "60": "f1",                   // Function key
  "62": "up"                    // Navigation key
}
```

### Rules

- MIDI note numbers run 0–127. Writing them as strings such as `"65"` is recommended
- A combo fires left to right when pressed, and releases right to left when released
- No restart needed after editing: reload or switch the config and it takes effect
- One note maps to one action: 48 cannot be bound to a single key and a combo at once, nor to two single keys, nor to two combos

### Supported Keys

| Category | Key Names |
|---|---|
| Letters | `a` `b` `c` `d` `e` `f` `g` `h` `i` `j` `k` `l` `m` `n` `o` `p` `q` `r` `s` `t` `u` `v` `w` `x` `y` `z` |
| Numbers | `0` `1` `2` `3` `4` `5` `6` `7` `8` `9` |
| Function | `f1` `f2` `f3` `f4` `f5` `f6` `f7` `f8` `f9` `f10` `f11` `f12` `f13` `f14` `f15` `f16` `f17` `f18` `f19` `f20` `f21` `f22` `f23` `f24` |
| Control | `enter` `space` `tab` `backspace` `shift` `ctrl` `alt` `escape` (alias `esc`) `capslock` `pause` |
| Navigation | `up` `down` `left` `right` `home` `end` `pageup` `pagedown` `insert` `delete` |
| Modifiers (L/R) | `lshift` `rshift` `lctrl` `rctrl` `lalt` `ralt` `lwin` `rwin` `win` (same as `lwin`) |
| Numpad | `num0` `num1` `num2` `num3` `num4` `num5` `num6` `num7` `num8` `num9` `numlock` `add` `subtract` `multiply` `divide` `decimal` `separator` |
| System | `printscreen` `scrolllock` `apps` |
| Media | `mute` `volumedown` `volumeup` `nexttrack` `prevtrack` `stop` `playpause` |
| Other | `select` `print` `execute` `help` `sleep` |

### US Keyboard Punctuation

| Key | Config Name |
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

## 💡 Tips for writing a preset

- Skip notes you never use, there is no need to fill in a complete set
- Try to avoid common shortcuts, so they do not fire by accident
