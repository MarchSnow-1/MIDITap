# Development Workflow

[English](DEVELOPMENT.md) | [简体中文](DEVELOPMENT_zh-CN.md)

---

## 1. Environment

| Needed | Version | Purpose |
|---|---|---|
| .NET SDK | 10.0 (see `global.json`) | build and test |
| Node.js | 22 | `scripts/sort-i18n.js`, release notes |
| PowerShell | 7 preferred, 5.1 must also work | scripts and the end-to-end test |
| Inno Setup | 6 | only needed when packaging the installer |

---

## 2. Directory conventions

```
src/MIDITap.Core               pure logic, must not reference WinUI
src/MIDITap.App                WinUI 3 UI; the only project referencing WindowsAppSDK and NAudio
src/MIDITap.Core.Tests         unit tests, no hardware needed
src/MIDITap.Integration.Tests  integration tests, need a real MIDI loopback port
scripts/                       scripts that ship with the user package
dev-scripts/                   development helpers kept in the repository only
.dev/                          throwaway investigation scripts (git-ignored)
installer/                     the Inno Setup script
preset-configs/                preset configs and their README
```

To decide where a script belongs, ask whether the user needs it
If yes it goes in `scripts/`, if no in `dev-scripts/`, and a script used only once locally can go in `.dev/` (already git-ignored)

---

## 3. The flow of a change

### 3.1 Read before editing

Every source file states at its top why it is written that way
Read the corresponding comments before changing anything rather than guessing at intent

### 3.2 Make the change

- Business rules belong in `MIDITap.Core`; the UI only displays and forwards
- Config edits must go through the text-range splicing in `ConfigEditor` / `Json5TextScanner`, never re-serialising the file (because that discards the user's comments)
- Comments are bilingual, each language as **one contiguous block**, never alternating line by line
- No trailing period on a comment line, and one sentence per line

### 3.3 Changing UI copy

All UI copy lives in `src/MIDITap.App/i18n/*.json`
After adding or removing a key you **must** run the sort script, and the two files must carry the same key set:

```bash
node scripts/sort-i18n.js          # sort and write back
node scripts/sort-i18n.js --check  # check only, which is what CI runs
```

CI runs `--check` in both `Build Dev` and `Release`, and fails on an unsorted file or a differing key set

### 3.4 Verify locally

Every batch runs at least these three:

```bash
dotnet test src/MIDITap.Core.Tests/MIDITap.Core.Tests.csproj # unit tests
dotnet build MIDITap.sln -c Debug # Debug build
pwsh scripts/e2e-midi.ps1 # end-to-end test script
```

The end-to-end test is **not optional** when the change touches MIDI, key injection or UI behaviour
It needs a MIDI loopback port (loopMIDI's `MIDITap-TestConfig`) and an interactive desktop
CI has neither, so it runs locally only and CI just parses it

### 3.5 Commit

- Every commit must build; no half-finished state
- One commit does one thing, so a problem can be traced afterwards
- Commit messages are English, Conventional Commits, **a single line**

---

## 4. Verification scripts

| Script | Purpose | When to run |
|---|---|---|
| `scripts/sort-i18n.js --check` | language file order and key set | after touching i18n |
| `scripts/e2e-midi.ps1` | real MIDI through key injection | after touching MIDI or UI behaviour |
| `dev-scripts/key-monitor.ps1` | observes the keys actually injected | when debugging keys |
| `dev-scripts/input-sink.ps1` | a key sink | used together with the above |
