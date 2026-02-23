<div align="center">

# MIDITap

![GitHub Release](https://img.shields.io/github/v/release/MarchSnow-1/MIDITap?style=for-the-badge)
![GitHub Last Commit](https://img.shields.io/github/last-commit/MarchSnow-1/MIDITap?style=for-the-badge)
![GitHub Repo stars](https://img.shields.io/github/stars/MarchSnow-1/MIDITap?style=for-the-badge)

[简体中文](README.md) | [English](README_EN.md)

Effortless MIDI Device Mapping
A lightweight tool to map MIDI keyboard input to keystrokes in real-time.

</div>

## 📖 Introduction

MIDITap is a low-latency MIDI-to-keyboard mapping tool built on native Windows APIs.

If you encounter any issues, please feel free to submit feedback via [Issues](../../issues).

> 💡 **Tip**: Configuration files support comments. When sharing your config, you can annotate specific keys for better clarity.

## ✨ Features

* 🎹 **Native Mapping**: Leverages Windows APIs to convert MIDI input into keyboard events with minimal latency.
* 🎵 **Sustain Support**: Keys remain triggered while the MIDI note is held and release instantly upon let-go for a natural feel.
* 🔑 **Full Key Support**: Supports letters, numbers, function keys, arrow keys, Numpad, media keys, and more.

## 🛠️ Requirements

* Windows (x64)
* A MIDI-compatible device

## 🚀 Quick Start

1. Download the latest version from the [Releases](../../releases) page and extract the archive.

2. Follow the [Configuration Guide](guide/en_us.md) to set up your mappings.

3. After configuration, double-click **MIDITap.exe** to launch, no installation required
   - If you have multiple MIDI devices, you can specify the port via command line: `MIDITap.exe --port 1`
   - All connected MIDI devices and their port numbers will be listed in the console on startup

[Preset Configurations](/preset-configs) are available here for reference or direct use.

*Note: If you modify the configuration, you must restart the application for changes to take effect.*

> [!IMPORTANT]
> Some antivirus software may flag this tool. If you have concerns, you are encouraged to review the source code and compile the executable manually.

## ⚠️ Disclaimer

* This tool works by simulating keyboard input, similar to utilities like AutoHotkey.
* MIDITap is designed for general MIDI-to-keystroke mapping and is not specifically developed for gaming.
* While there are currently no known cases of account bans resulting from using this tool in games, some strict anti-cheat systems may be sensitive to third-party input software.
* **Please review the rules of your specific game before use. By downloading this software, you assume all risks; the developer holds no responsibility for any consequences.**
