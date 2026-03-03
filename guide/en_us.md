# MIDITap Configuration Guide

This guide covers the full flow: basic mapping, combo keys, CLI options, and strict config validation.

## 1. Quick Start (Shortest Path)

1. Start MIDITap and press a key on your MIDI device.
2. Read the MIDI note number from console output (for example, `65` in `Note ON: 65, Velocity: 80`).
3. Add a mapping in `config/mapping.json`, then restart MIDITap.

Minimal example:

```json5
{
  "65": "a"
}
```

## 2. Config File Structure

Default config path is `config/mapping.json`. Format is JSON5 (comments and trailing commas are allowed).

```json5
{
  // Optional: default MIDI port index (zero-based)
  "port": 0,

  // MIDI note -> key name
  "48": "a",
  "50": "enter",
  "84": "ctrl+shift+esc"
}
```

Rules:
- MIDI note numbers are recommended as strings (for example `"48"`).
- `port` is optional.
- Other fields are treated as key mappings.

## 3. Key Name Rules (Single Keys and Combos)

### 3.1 Single Key Format

Any value without `+` is treated as a single key, even if the key name has multiple characters (for example `enter`, `delete`, `escape`).

Common single keys:
- Letters: `a` ~ `z`
- Numbers: `0` ~ `9`
- Function keys: `f1` ~ `f24`
- Control keys: `enter` `space` `tab` `backspace` `shift` `ctrl` `alt`
- Navigation keys: `up` `down` `left` `right` `home` `end` `pageup` `pagedown` `insert` `delete`
- Numpad keys: `num0` ~ `num9` `add` `subtract` `multiply` `divide` `decimal` `numlock`

### 3.2 Combo Key Format

Any value containing `+` is treated as a combo. MIDITap splits by `+` and executes in order.

```json5
{
  "84": "ctrl+shift+esc",
  "85": "alt+tab"
}
```

Execution order:
- `Note ON`: press left to right.
- `Note OFF`: release right to left.

### 3.3 Esc Alias

Both are valid and equivalent:
- `esc`
- `escape`

So both `ctrl+shift+esc` and `ctrl+shift+escape` work.

## 4. MIDI Port Selection Priority

Priority (high to low):
1. CLI argument `--port <index>`
2. `"port"` in config file
3. Default `0`

Example:

```bash
MIDITap.exe --port 1
```

## 5. CLI Options

Available options:
- `--port <index>`: set MIDI port index
- `--list-ports`: list all MIDI input ports and exit
- `--config <path>`: specify config file path (absolute or relative)
- `-config <path>`: compatibility form of `--config`
- `--check-config`: strict config validation, prints `true/false`
- `--verbose` / `-v`: verbose logs (forced on in devmode)
- `--help` / `-h`: show help

Config file resolution priority:
1. If `--config/-config` is provided, use that file
2. Otherwise, if `.dev` exists in the app directory, default to `config/mapping-dev.json`
3. Otherwise, default to `config/mapping.json`

Examples:

```bash
MIDITap.exe --list-ports
MIDITap.exe --config .\config\mapping.json
MIDITap.exe -config .\config\mapping-dev.json
MIDITap.exe --check-config --config .\config\mapping.json
MIDITap.exe --help
```

## 6. Config Check Mode (Strict)

Command:

```bash
MIDITap.exe --check-config
```

Return behavior:
- Valid config: prints `true`, exit code `0`
- Invalid config: prints `false`, exit code `1`

In strict mode, any invalid item causes failure, for example:
- out-of-range MIDI note
- unknown key name
- malformed combo key

## 7. Notes

- If a key name is invalid, MIDITap warns and skips that mapping:
  ```
  Can't find 'xxx' in VK Code List, skipping...
  ```
- Long press is supported: as long as MIDI note is held, the mapped key stays pressed.
- Restart MIDITap after editing config files.

## 8. Console Output Reference

| Message | Meaning |
|---------|---------|
| `Config Loaded (...): {...}` | Config loaded successfully |
| `note 48 -> 'a' (VK=0x41)` | Mapping list printed at startup |
| `Note ON: 65, Velocity: 80, Key: 'r'` | Bound mapping triggered |
| `Note ON: 65, Velocity: 80 (unbound)` | Unbound note triggered |
| `Note OFF: 65, Key: 'r'` | Bound mapping released |
| `Note OFF: 65 (unbound)` | Unbound note released |
| `SendInput Return 0` | Input blocked, try running as Administrator |
