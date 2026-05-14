<div align="center">

# MIDITap

![GitHub Release](https://img.shields.io/github/v/release/MarchSnow-1/MIDITap?style=for-the-badge)
![GitHub Last Commit](https://img.shields.io/github/last-commit/MarchSnow-1/MIDITap?style=for-the-badge)
![GitHub Repo stars](https://img.shields.io/github/stars/MarchSnow-1/MIDITap?style=for-the-badge)
[![Total Download](https://img.shields.io/github/downloads/MarchSnow-1/MIDITap/total?style=for-the-badge)](https://github.com/MarchSnow-1/MIDITap/releases)

[English](README.md) | [简体中文](README_zh-CN.md)

Effortless MIDI Device Mapping
A lightweight tool to map MIDI keyboard input to keystrokes in real-time.

</div>

## 📖 Introduction

MIDITap is a low-latency MIDI-to-keyboard mapping tool built on native Windows APIs.

If you encounter any issues, please feel free to submit feedback via [Issues](../../issues).

## ✨ Features

* 🎹 **Native Mapping**: Leverages Windows APIs to convert MIDI input into keyboard events with minimal latency.
* 🎵 **Sustain Support**: Keys remain triggered while the MIDI note is held and release instantly upon let-go for a natural feel.
* 🔑 **Full Key Support**: Supports letters, numbers, function keys, arrow keys, Numpad, media keys, and more.

## 🛠️ Requirements

* Windows (x64)
* A MIDI-compatible device

## 🚀 Quick Start

1. Download the latest version from the [Releases](../../releases) page

2. Extract the archive to any directory

3. Double-click **MIDITap.exe** to launch, no installation required

- [Preset Configurations](/preset-configs) are available here for reference or direct use.

## 📚 Usage Guide

### GUI Interface

The interface has three tabs: **Home**, **Configs**, and **Log**.

#### Home — Device Selection & Monitoring

1. The left **MIDI Devices** list shows available MIDI devices. Click to select one.
2. Click **Start** to begin monitoring. Pressing MIDI keys will trigger the mapped keyboard output.
3. **Active Notes** on the right shows currently held notes and their key bindings.
4. **Activity Log** records all MIDI events (note on/off, warnings, errors).

#### Configs — Mapping Management

- **Config**: Switch between configuration files. Click to load.
- **Refresh**: Reload the configuration file list.
- **Browse**: Open the folder containing configuration files.
- **Config Name**: Edit the display name shown in the UI.
- **Add Mapping**: Three input capture methods —
  - **MIDI Note**: Click the input and play a MIDI key to auto-fill the note number.
  - **Single Key**: Click the input and press a keyboard key (a / enter / f1 etc.).
  - **Combo Key**: Click the input and press multiple keys in sequence (e.g. ctrl+shift+escape).
  - Single Key and Combo Key are mutually exclusive; the last focused one takes effect.
  - Click **Add** to create the mapping.
- **Current Mappings**: Displays all mappings in the current config. Click `×` to delete.

#### Log Tab

Full-window view of the Activity Log, useful for debugging.

## ✍️ Writing Config Files Manually

Config files are stored in the `config/` directory in JSON5 format (comments and trailing commas are supported). You can create multiple `.json` files and switch between them via the dropdown in the GUI.

#### Basic Steps

1. Start the program, select a MIDI device on the Home tab, then click **Start**
2. Play MIDI keys and watch the **Activity Log** for note numbers (e.g., the `65` in `Note ON: 65`)
3. Create or edit a `.json` file in the `config/` directory with your mappings
4. Click **Refresh** on the Configs tab, then switch to your file in the dropdown

#### Config Structure

```json5
{
  "name": "My Config",          // Required: display name shown in the GUI
  "48": "a",                    // Single key: MIDI note → key name
  "50": "ctrl+shift+escape",    // Combo key: join multiple keys with +
  "60": "f1",                   // Function key
  "62": "up"                    // Navigation key
}
```

#### Rules

- MIDI note numbers range from 0–127; using strings (e.g., `"65"`) is recommended
- Combo keys trigger left-to-right on press and release right-to-left on release
- After editing a config file, just reload or switch to it — no restart needed

#### Supported Keys

| Category | Key Names |
|---|---|
| Letters | `a` `b` `c` `d` `e` `f` `g` `h` `i` `j` `k` `l` `m` `n` `o` `p` `q` `r` `s` `t` `u` `v` `w` `x` `y` `z` |
| Numbers | `0` `1` `2` `3` `4` `5` `6` `7` `8` `9` |
| Function | `f1` `f2` `f3` `f4` `f5` `f6` `f7` `f8` `f9` `f10` `f11` `f12` `f13` `f14` `f15` `f16` `f17` `f18` `f19` `f20` `f21` `f22` `f23` `f24` |
| Control | `enter` `space` `tab` `backspace` `shift` `ctrl` `alt` `escape` (alias `esc`) `capslock` `pause` |
| Navigation | `up` `down` `left` `right` `home` `end` `pageup` `pagedown` `insert` `delete` |
| Modifiers (L/R) | `lshift` `rshift` `lctrl` `rctrl` `lalt` `ralt` `lwin` `rwin` |
| Numpad | `num0` `num1` `num2` `num3` `num4` `num5` `num6` `num7` `num8` `num9` `numlock` `add` `subtract` `multiply` `divide` `decimal` `separator` |
| System | `printscreen` `scrolllock` `apps` |
| Media | `mute` `volumedown` `volumeup` `nexttrack` `prevtrack` `stop` `playpause` |
| Other | `select` `print` `execute` `help` `sleep` |

#### US Keyboard Punctuation

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

## Build from Source

### Prerequisites

- [Node.js](https://nodejs.org/) 22+
- [Git](https://git-scm.com/)
- npm (included with Node.js)

### Steps

```bash
# 1. Clone the repository
git clone https://github.com/MarchSnow-1/MIDITap.git
cd MIDITap

# 2. Install NeutralinoJS CLI globally
npm install -g @neutralinojs/neu

# 3. Install Node.js dependencies
npm install

# 4. Download NeutralinoJS runtime binary
neu update

# 5. Start in development mode
neu run
```

## ⚠️ Disclaimer

* This tool works by simulating keyboard input, similar to utilities like AutoHotkey.
* MIDITap is designed for general MIDI-to-keystroke mapping and is not specifically developed for gaming.
* While there are currently no known cases of account bans resulting from using this tool in games, some strict anti-cheat systems may be sensitive to third-party input software.
* **Please review the rules of your specific game before use. By downloading this software, you assume all risks; the developer holds no responsibility for any consequences.**
