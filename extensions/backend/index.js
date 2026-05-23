// MIDITap Extension Backend
// Connects to NeutralinoJS server via WebSocket, handles MIDI input and keyboard output

const fs = require("fs");
const { exec } = require("child_process");
const WebSocket = require("ws");
const midi = require("midi");
const path = require("path");
const { loadConfig, listConfigFiles, renameConfigFile, addMappingToConfig, ensureConfigDir, getLastConfigPath, saveLastConfigPath } = require("../../libs/config");
const { sendKey, getKeyName } = require("../../libs/keyboard");

// Read connection parameters from stdin (official NeutralinoJS extension protocol)
let processInput;
try {
  processInput = JSON.parse(fs.readFileSync(process.stdin.fd, "utf-8"));
} catch (e) {
  console.error("[miditap.backend]: Failed to read extension config from stdin:", e.message);
  process.exit(1);
}

const NL_PORT = processInput.nlPort;
const NL_TOKEN = processInput.nlToken;
const NL_CTOKEN = processInput.nlConnectToken;
const NL_EXTID = processInput.nlExtensionId;

// State
let input = null;
const activeNotes = new Set();
const activeNoteBindings = new Map();
const activeVkCount = new Map();
let noteMap = new Map();
let currentConfigName = "mapping.json";
let ws = null;

function uuid() {
  return "10000000-1000-4000-8000-100000000000".replace(/[018]/g, (c) =>
    (c ^ (Math.random() * 16 >> c / 4)).toString(16)
  );
}

// Send a native API call through the NeutralinoJS WebSocket
function callNative(method, data) {
  return new Promise((resolve, reject) => {
    const id = uuid();
    const msg = JSON.stringify({
      id,
      method,
      accessToken: NL_TOKEN,
      data,
    });

    const handler = (raw) => {
      const res = JSON.parse(raw.toString());
      if (res.id !== id) return;
      ws.removeListener("message", handler);
      if (res.data && res.data.error) {
        reject(res.data.error);
      } else {
        resolve(res.data);
      }
    };

    ws.on("message", handler);
    ws.send(msg);
  });
}

// Broadcast event to frontend
function broadcast(event, data) {
  callNative("app.broadcast", { event, data }).catch(() => {});
}

// Keyboard cleanup
function closeInputPortSafely() {
  if (!input) return;
  try {
    input.closePort();
  } catch (err) {
    // ignore
  } finally {
    input = null;
  }
}

function sendAllKeysUp() {
  for (const [vkCode, count] of activeVkCount.entries()) {
    if (count > 0) {
      sendKey(vkCode, 0x0002);
    }
  }
  activeVkCount.clear();
  activeNoteBindings.clear();
  activeNotes.clear();
}

function stopMonitoring() {
  if (input) {
    input.removeAllListeners("message");
    closeInputPortSafely();
  }
  sendAllKeysUp();
}

// MIDI message handler
function handleMidiMessage(deltaTime, message) {
  const status = message[0] & 0xf0;
  const note = message[1];
  const velocity = message[2];

  const isNoteOn = status === 0x90 && velocity > 0;
  const isNoteOff = status === 0x80 || (status === 0x90 && velocity === 0);

  if (!isNoteOn && !isNoteOff) return;

  const binding = noteMap.get(note);
  const noteIsActive = activeNotes.has(note);
  const isDuplicateNoteOn = isNoteOn && noteIsActive;
  const isUnexpectedNoteOff = isNoteOff && !noteIsActive;

  if (isNoteOn && !isDuplicateNoteOn) {
    activeNotes.add(note);
  } else if (isNoteOff && !isUnexpectedNoteOff) {
    activeNotes.delete(note);
  }

  if (isDuplicateNoteOn) {
    broadcast("midiDuplicateOn", { note });
    return;
  }
  if (isUnexpectedNoteOff) {
    broadcast("midiUnexpectedOff", { note });
    return;
  }

  if (!binding) {
    if (isNoteOn) {
      broadcast("midiNoteOn", { note, velocity, key: null });
    } else {
      broadcast("midiNoteOff", { note });
    }
    return;
  }

  if (isNoteOn) {
    for (const vkCode of binding) {
      const count = activeVkCount.get(vkCode) || 0;
      if (count === 0) {
        sendKey(vkCode, 0);
      }
      activeVkCount.set(vkCode, count + 1);
    }
    activeNoteBindings.set(note, binding);
    const keyLabel = binding
      .map((vk) => getKeyName(vk) || "0x" + vk.toString(16).toUpperCase())
      .join("+");
    broadcast("midiNoteOn", { note, velocity, key: keyLabel });
  } else {
    const activeBinding = activeNoteBindings.get(note) || binding;
    for (let i = activeBinding.length - 1; i >= 0; i--) {
      const vkCode = activeBinding[i];
      const count = activeVkCount.get(vkCode) || 0;
      if (count > 0) {
        const newCount = count - 1;
        if (newCount === 0) {
          sendKey(vkCode, 0x0002);
          activeVkCount.delete(vkCode);
        } else {
          activeVkCount.set(vkCode, newCount);
        }
      }
    }
    activeNoteBindings.delete(note);
    broadcast("midiNoteOff", { note });
  }
}

// Command handlers
function handleListPorts() {
  try {
    const listInput = new midi.Input();
    const portCount = listInput.getPortCount();
    const ports = [];
    for (let i = 0; i < portCount; i++) {
      ports.push({ index: i, name: listInput.getPortName(i) });
    }
    broadcast("midiPorts", { ports });
  } catch (err) {
    broadcast("midiError", { message: "MIDI error: " + err.message });
    broadcast("midiPorts", { ports: [] });
  }
}

function handleStart(data) {
  stopMonitoring();
  const portIndex = typeof data.port === "number" ? data.port : 0;

  // Load config if provided
  if (data.configPath) {
    const baseDir = path.join(__dirname, "..", "..");
    let resolvedPath = data.configPath;
    if (!path.isAbsolute(resolvedPath)) {
      resolvedPath = path.resolve(baseDir, resolvedPath);
    }
    const configResult = loadConfig(baseDir, {
      verbose: false,
      silent: true,
      configPath: resolvedPath,
    });
    if (configResult) {
      saveLastConfigPath(baseDir, configResult.configPath);
      noteMap = configResult.noteMap;
      currentConfigName = configResult.name;
      const mapping = {};
      noteMap.forEach((vkCodes, note) => {
        mapping[note] = vkCodes
          .map((vk) => getKeyName(vk) || "0x" + vk.toString(16).toUpperCase())
          .join("+");
      });
      broadcast("configLoaded", {
        path: configResult.configPath,
        filename: path.basename(configResult.configPath),
        name: configResult.name,
        noteCount: noteMap.size,
        mapping,
      });
    }
  }

  try {
    input = new midi.Input();
  } catch (err) {
    broadcast("midiError", { message: "MIDI error: " + err.message });
    input = null;
    return;
  }

  const portCount = input.getPortCount();
  if (portCount === 0) {
    broadcast("midiError", { message: "No MIDI devices found" });
    input = null;
    return;
  }

  if (portIndex >= portCount) {
    broadcast("midiError", {
      message: "Port " + portIndex + " not found (available: 0-" + (portCount - 1) + ")",
    });
    input = null;
    return;
  }

  try {
    input.openPort(portIndex);
    input.ignoreTypes(true, true, true);
    input.on("message", handleMidiMessage);
    broadcast("midiStarted", {
      port: portIndex,
      portName: input.getPortName(portIndex),
    });
  } catch (err) {
    broadcast("midiError", { message: "Failed to open port: " + err.message });
    input = null;
  }
}

function handleStop() {
  stopMonitoring();
  broadcast("midiStopped", {});
}

function handleLoadConfig(data) {
  const baseDir = path.join(__dirname, "..", "..");
  let resolvedPath = data.path || null;

  // 未指定路径时，优先恢复上次使用的配置文件
  if (!resolvedPath) {
    resolvedPath = getLastConfigPath(baseDir);
  }
  if (resolvedPath && !path.isAbsolute(resolvedPath)) {
    resolvedPath = path.resolve(baseDir, resolvedPath);
  }

  const configResult = loadConfig(baseDir, {
    verbose: false,
    silent: true,
    configPath: resolvedPath,
  });

  if (!configResult) {
    broadcast("midiError", {
      message: "Failed to load config: " + (data.path || "default"),
    });
    return;
  }

  saveLastConfigPath(baseDir, configResult.configPath);

  noteMap = configResult.noteMap;
  currentConfigName = configResult.name;
  const mapping = {};
  noteMap.forEach((vkCodes, note) => {
    mapping[note] = vkCodes
      .map((vk) => getKeyName(vk) || "0x" + vk.toString(16).toUpperCase())
      .join("+");
  });

  broadcast("configLoaded", {
    path: configResult.configPath,
    filename: path.basename(configResult.configPath),
    name: configResult.name,
    noteCount: noteMap.size,
    mapping,
  });
}

function handleListConfigs() {
  const baseDir = path.join(__dirname, "..", "..");
  const list = listConfigFiles(baseDir);
  broadcast("configList", { configs: list });
}

function handleRenameConfig(data) {
  const baseDir = path.join(__dirname, "..", "..");
  const filename = data.filename || "";
  const newName = (data.name || "").trim();
  if (!filename || !newName) {
    broadcast("midiError", { message: "Rename requires filename and name" });
    return;
  }
  const ok = renameConfigFile(baseDir, filename, newName);
  if (ok) {
    broadcast("configRenamed", { filename, name: newName });
    handleListConfigs();
  } else {
    broadcast("midiError", { message: "Failed to rename config: " + filename });
  }
}

function openDirectory(dirPath) {
  const cmd = process.platform === 'win32'
    ? 'start "" "' + dirPath + '"'
    : process.platform === 'darwin'
      ? 'open "' + dirPath + '"'
      : 'xdg-open "' + dirPath + '"';
  exec(cmd, (err) => {
    if (err) broadcast("midiError", { message: "Failed to open config dir: " + err.message });
  });
}

function handleOpenConfigDir() {
  const baseDir = path.join(__dirname, "..", "..");
  ensureConfigDir(baseDir);
  openDirectory(path.join(baseDir, "config"));
}

let captureInput = null;
let captureResolve = null;

function handleCaptureNote(data) {
  const portIndex = typeof data.port === "number" ? data.port : 0;
  const configPath = data.configPath || null;

  // If already monitoring, just use that — tell frontend to listen
  if (input) {
    broadcast("midiCaptureReady", {});
    return;
  }

  // If we already have a capture input, close it first
  if (captureInput) {
    try { captureInput.closePort(); } catch {}
    captureInput = null;
    captureResolve = null;
  }

  try {
    captureInput = new midi.Input();
  } catch (err) {
    broadcast("midiError", { message: "Capture error: " + err.message });
    captureInput = null;
    return;
  }

  const portCount = captureInput.getPortCount();
  if (portCount === 0) {
    broadcast("midiError", { message: "No MIDI devices found" });
    captureInput = null;
    return;
  }

  if (portIndex >= portCount) {
    broadcast("midiError", { message: "Port " + portIndex + " not found" });
    captureInput = null;
    return;
  }

  try {
    captureInput.openPort(portIndex);
    captureInput.ignoreTypes(true, true, true);
    captureInput.on("message", function (deltaTime, message) {
      const status = message[0] & 0xf0;
      const note = message[1];
      if (status === 0x90 && message[2] > 0) {
        broadcast("midiNoteCaptured", { note });
        // Close after capture
        try { captureInput.closePort(); } catch {}
        captureInput = null;
        captureResolve = null;
      }
    });
    broadcast("midiCaptureReady", {});
  } catch (err) {
    broadcast("midiError", { message: "Failed to open port for capture: " + err.message });
    captureInput = null;
  }
}

function handleStopCapture() {
  if (captureInput) {
    try { captureInput.closePort(); } catch {}
    captureInput = null;
    captureResolve = null;
  }
}

function handleAddMapping(data) {
  const baseDir = path.join(__dirname, "..", "..");
  const note = String(data.note || "0");
  const key = String(data.key || "");
  if (!note || !key) {
    broadcast("midiError", { message: "addMapping requires note and key" });
    return;
  }

  // Determine which config file to write to
  let configFilename = data.filename || "mapping.json";
  if (!configFilename.endsWith(".json")) configFilename += ".json";
  const configPath = data.configPath || path.join(baseDir, "config", configFilename);

  const ok = addMappingToConfig(baseDir, configPath, note, key);
  if (ok) {
    // Reload config into memory
    const configResult = loadConfig(baseDir, {
      verbose: false,
      silent: true,
      configPath,
    });
    if (configResult) {
      noteMap = configResult.noteMap;
      currentConfigName = configResult.name;
      const mapping = {};
      noteMap.forEach(function (vkCodes, n) {
        mapping[n] = vkCodes
          .map(function (vk) { return getKeyName(vk) || "0x" + vk.toString(16).toUpperCase(); })
          .join("+");
      });
      broadcast("configLoaded", {
        path: configResult.configPath,
        filename: path.basename(configResult.configPath),
        name: configResult.name,
        noteCount: noteMap.size,
        mapping,
      });
    }
    broadcast("midiLog", { message: "Mapping added: note " + note + " → " + key });
  } else {
    broadcast("midiError", { message: "Failed to save mapping to config file" });
  }
}

// Process events from frontend
function handleEvent(event, data) {
  switch (event) {
    case "listPorts":
      handleListPorts();
      break;
    case "start":
      handleStart(data || {});
      break;
    case "stop":
      handleStop();
      break;
    case "loadConfig":
      handleLoadConfig(data || {});
      break;
    case "listConfigs":
      handleListConfigs();
      break;
    case "renameConfig":
      handleRenameConfig(data || {});
      break;
    case "openConfigDir":
      handleOpenConfigDir();
      break;
    case "addMapping":
      handleAddMapping(data || {});
      break;
    case "captureNote":
      handleCaptureNote(data || {});
      break;
    case "stopCapture":
      handleStopCapture();
      break;
    case "getStatus":
      broadcast("midiLog", {
        message: "MIDITap backend ready. Node.js " + process.version,
      });
      break;
    default:
      // Internal NeutralinoJS events (appClientConnect, windowBlur, etc.) — silently ignore
  }
}

// Connect to NeutralinoJS server
function connect() {
  const url =
    "ws://127.0.0.1:" + NL_PORT + "?extensionId=" + NL_EXTID + "&connectToken=" + NL_CTOKEN;

  ws = new WebSocket(url);

  ws.on("open", () => {
    const baseDir = path.join(__dirname, "..", "..");
    ensureConfigDir(baseDir);
    console.log("[" + NL_EXTID + "]: Connected to NeutralinoJS server");
    broadcast("midiLog", {
      message: "MIDITap backend ready. Node.js " + process.version,
    });
  });

  ws.on("message", (raw) => {
    try {
      const msg = JSON.parse(raw.toString());
      // Frontend events are dispatched as { event, data } messages
      if (msg.event && !msg.id) {
        handleEvent(msg.event, msg.data);
      }
    } catch (e) {
      // Ignore parse errors from non-JSON messages
    }
  });

  ws.on("close", () => {
    stopMonitoring();
    broadcast("midiLog", { message: "Backend disconnected" });
    process.exit(0);
  });

  ws.on("error", (err) => {
    stopMonitoring();
    broadcast("midiError", { message: "Connection error: " + err.message });
    process.exit(1);
  });
}

// Handle shutdown
process.on("SIGINT", () => {
  stopMonitoring();
  process.exit(0);
});

process.on("SIGTERM", () => {
  stopMonitoring();
  process.exit(0);
});

connect();
