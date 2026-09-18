# Development Workflow

[English](DEVELOPMENT.md) | [简体中文](DEVELOPMENT_zh-CN.md)

This file covers **how to get things running**: the environment, the commands, and what each script owns
The project's rules and conventions (security, dependencies, comments, commit messages, prohibitions) live in [`AGENTS.md`](../AGENTS.md); where the two disagree, `AGENTS.md` wins

---

## 1. Environment

| Needed | Version | Purpose |
|---|---|---|
| .NET SDK | 10.0 (see `global.json`) | build and test |
| Node.js | 22 | `dev-scripts/sort-i18n.js`, release notes |
| PowerShell | 7 for the end-to-end tests; 5.1 must also work for the shipped script | scripts and the end-to-end test |
| Inno Setup | 6 | only needed when packaging the installer |

The end-to-end tests need PowerShell 7: `e2e-log.ps1` reads a whole-day archive through `System.Formats.Tar`, a type that does not exist on 5.1
The shipped `scripts/apply-update.ps1` must still work on 5.1, because a user's machine may have only that

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

Packaging copies `scripts/*.ps1` only (see CI), so a `.js` under `scripts/` is just as misplaced as a user-facing script under `dev-scripts/`

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
node dev-scripts/sort-i18n.js          # sort and write back
node dev-scripts/sort-i18n.js --check  # check only, which is what CI runs
```

CI runs `--check` in both `Build Dev` and `Release`, and fails on an unsorted file or a differing key set

### 3.4 Verify locally

Every batch runs at least these three:

```bash
dotnet test src/MIDITap.Core.Tests/MIDITap.Core.Tests.csproj # unit tests
dotnet build MIDITap.sln -c Debug # Debug build
pwsh dev-scripts/e2e-all.ps1 # end-to-end tests (runs the three case scripts in turn)
```

The end-to-end test is **not optional** when the change touches MIDI, key injection or UI behaviour

It needs a MIDI loopback port (loopMIDI's `MIDITap-TestConfig`) and an interactive desktop

CI has neither, so it runs locally only and CI just parses it

The end-to-end suite is one shared library plus three case scripts:

- `e2e-common.ps1` assertions, MIDI packing and sending, key state, UI reading
- `e2e-seed.ps1` preparing and restoring the scene (test config, log seed files), always restored at the end
- `e2e-app.ps1` the app lifecycle (initialize, start, readiness, teardown, summary); it dot-sources the other two

A case script dot-sources `e2e-app.ps1` alone. Each of the three runs on its own and starts the app once; `e2e-all.ps1` runs all three in turn

### 3.5 Commit

- Every commit must build; no half-finished state
- One commit does one thing, so a problem can be traced afterwards
- Commit messages are English, Conventional Commits, **a single line**

---

## 4. Verification scripts

| Script | Purpose | When to run |
|---|---|---|
| `dev-scripts/sort-i18n.js --check` | language file order and key set | after touching i18n |
| `dev-scripts/e2e-all.ps1` | runs the three end-to-end scripts below in turn | after touching MIDI, the UI or logging |
| `dev-scripts/e2e-midi.ps1` | real MIDI through key injection | after touching MIDI or injection |
| `dev-scripts/e2e-ui.ps1` | the log page, the home page live preview, the replay after a page switch | after touching the UI |
| `dev-scripts/e2e-log.ps1` | writing the log to disk and the start-up housekeeping | after touching logging |
| `dev-scripts/key-monitor.ps1` | observes the keys actually injected | when debugging keys |
| `dev-scripts/input-sink.ps1` | a key sink | used together with the above |
