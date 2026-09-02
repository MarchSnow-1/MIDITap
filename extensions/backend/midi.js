// MIDI state machine — input monitoring, note tracking, keyboard output

const midi = require("midi");
const path = require("path");
const { sendKey, sendKeySync, getKeyName } = require("../../libs/keyboard");
const { loadConfig, saveLastConfigPath } = require("../../libs/config");

// --- Internal helpers (no ctx needed) ---

function closeInputPortSafely(ctx) {
  if (!ctx.input) return;
  try {
    ctx.input.closePort();
  } catch (err) {
    // ignore
  } finally {
    ctx.input = null;
  }
}

function sendAllKeysUpSync(ctx) {
  for (const [vkCode, count] of ctx.activeVkCount.entries()) {
    if (count > 0) {
      sendKeySync(vkCode, 0x0002);
    }
  }
  ctx.activeVkCount.clear();
  ctx.activeNoteBindings.clear();
  ctx.activeNotes.clear();
}

function stopMonitoring(ctx) {
  if (ctx.input) {
    ctx.input.removeAllListeners("message");
    closeInputPortSafely(ctx);
  }
  if (ctx.captureInput) {
    try { ctx.captureInput.closePort(); } catch {}
    ctx.captureInput = null;
  }
  sendAllKeysUpSync(ctx);
}

// --- MIDI message handler ---

function handleMidiMessage(ctx, deltaTime, message) {
  const status = message[0] & 0xf0;
  const note = message[1];
  const velocity = message[2];

  const isNoteOn = status === 0x90 && velocity > 0;
  const isNoteOff = status === 0x80 || (status === 0x90 && velocity === 0);

  if (!isNoteOn && !isNoteOff) return;

  const binding = ctx.noteMap.get(note);
  const noteIsActive = ctx.activeNotes.has(note);

  if (isNoteOn) {
    if (noteIsActive) {
      ctx.broadcast("midiDuplicateOn", { note });
      return;
    }
    ctx.activeNotes.add(note);
    if (!binding) {
      ctx.broadcast("midiNoteOn", { note, velocity, key: null });
      return;
    }
    for (const vkCode of binding) {
      const count = ctx.activeVkCount.get(vkCode) || 0;
      if (count === 0) {
        sendKey(vkCode, 0);
      }
      ctx.activeVkCount.set(vkCode, count + 1);
    }
    ctx.activeNoteBindings.set(note, binding);
    const keyLabel = binding
      .map((vk) => getKeyName(vk) || "0x" + vk.toString(16).toUpperCase())
      .join("+");
    ctx.broadcast("midiNoteOn", { note, velocity, key: keyLabel });
    return;
  }

  // Note-off.
  if (!noteIsActive) {
    ctx.broadcast("midiUnexpectedOff", { note });
    return;
  }

  // Release keys using the binding that was recorded when the note was
  // pressed, NOT the current config binding. If the mapping was switched or
  // deleted while the note was held, the current noteMap no longer contains
  // this note — releasing based on it would leave the physical key stuck
  // down until the user manually hits Stop.
  const pressBinding = ctx.activeNoteBindings.get(note) || binding;
  ctx.activeNoteBindings.delete(note);
  ctx.activeNotes.delete(note);

  if (pressBinding) {
    for (let i = pressBinding.length - 1; i >= 0; i--) {
      const vkCode = pressBinding[i];
      const count = ctx.activeVkCount.get(vkCode) || 0;
      if (count > 0) {
        const newCount = count - 1;
        if (newCount === 0) {
          sendKey(vkCode, 0x0002);
          ctx.activeVkCount.delete(vkCode);
        } else {
          ctx.activeVkCount.set(vkCode, newCount);
        }
      }
    }
  }
  ctx.broadcast("midiNoteOff", { note });
}

// --- Command handlers ---

function handleListPorts(ctx) {
  ctx.log("Handling listPorts command");
  try {
    const listInput = new midi.Input();
    const portCount = listInput.getPortCount();
    const ports = [];
    for (let i = 0; i < portCount; i++) {
      ports.push({ index: i, name: listInput.getPortName(i) });
    }
    ctx.log("Found " + portCount + " MIDI port(s)");
    ctx.broadcast("midiPorts", { ports });
  } catch (err) {
    ctx.error("listPorts error: " + err.message);
    ctx.broadcast("midiError", { message: "MIDI error: " + err.message });
    ctx.broadcast("midiPorts", { ports: [] });
  }
}

function handleStart(ctx, data) {
  const portIndex = typeof data.port === "number" ? data.port : 0;
  ctx.log("Handling start command — port=" + portIndex + " configPath=" + (data.configPath || "(none)"));
  stopMonitoring(ctx);

  // Load config if provided
  if (data.configPath) {
    let resolvedPath = data.configPath;
    if (!path.isAbsolute(resolvedPath)) {
      resolvedPath = path.resolve(ctx.baseDir, resolvedPath);
    }
    ctx.log("Loading config from path: " + resolvedPath);
    const configResult = loadConfig(ctx.baseDir, {
      verbose: false,
      silent: true,
      configPath: resolvedPath,
    });
    if (configResult) {
      saveLastConfigPath(ctx.baseDir, configResult.configPath);
      ctx.noteMap = configResult.noteMap;
      ctx.currentConfigName = configResult.name;
      ctx.currentConfigPath = configResult.configPath;
      ctx.log("Config loaded — name=" + configResult.name + " mappings=" + ctx.noteMap.size);
      const mapping = {};
      ctx.noteMap.forEach((vkCodes, note) => {
        mapping[note] = vkCodes
          .map((vk) => getKeyName(vk) || "0x" + vk.toString(16).toUpperCase())
          .join("+");
      });
      ctx.broadcast("configLoaded", {
        path: configResult.configPath,
        filename: path.basename(configResult.configPath),
        name: configResult.name,
        noteCount: ctx.noteMap.size,
        mapping,
      });
    }
  }

  try {
    ctx.input = new midi.Input();
  } catch (err) {
    ctx.error("Failed to create MIDI input: " + err.message);
    ctx.broadcast("midiError", { message: "MIDI error: " + err.message });
    ctx.input = null;
    return;
  }

  const portCount = ctx.input.getPortCount();
  if (portCount === 0) {
    ctx.warn("No MIDI devices found");
    ctx.broadcast("midiError", { message: "No MIDI devices found" });
    ctx.input = null;
    return;
  }

  if (portIndex >= portCount) {
    ctx.warn("Port " + portIndex + " not found (available: 0-" + (portCount - 1) + ")");
    ctx.broadcast("midiError", {
      message: "Port " + portIndex + " not found (available: 0-" + (portCount - 1) + ")",
    });
    ctx.input = null;
    return;
  }

  try {
    ctx.input.openPort(portIndex);
    ctx.input.ignoreTypes(true, true, true);
    ctx.input.on("message", function (deltaTime, message) {
      handleMidiMessage(ctx, deltaTime, message);
    });
    const portName = ctx.input.getPortName(portIndex);
    ctx.log("MIDI monitoring started — port=" + portIndex + " name=" + portName + " mappings=" + ctx.noteMap.size);
    ctx.broadcast("midiStarted", {
      port: portIndex,
      portName: portName,
    });
  } catch (err) {
    ctx.error("Failed to open port " + portIndex + ": " + err.message);
    ctx.broadcast("midiError", { message: "Failed to open port: " + err.message });
    ctx.input = null;
  }
}

function handleStop(ctx) {
  ctx.log("Handling stop command");
  stopMonitoring(ctx);
  ctx.broadcast("midiStopped", {});
}

function handleCaptureNote(ctx, data) {
  const portIndex = typeof data.port === "number" ? data.port : 0;

  // If already monitoring, just use that — tell frontend to listen
  if (ctx.input) {
    ctx.broadcast("midiCaptureReady", {});
    return;
  }

  // If we already have a capture input, close it first
  if (ctx.captureInput) {
    try { ctx.captureInput.closePort(); } catch {}
    ctx.captureInput = null;
  }

  try {
    ctx.captureInput = new midi.Input();
  } catch (err) {
    ctx.broadcast("midiError", { message: "Capture error: " + err.message });
    ctx.captureInput = null;
    return;
  }

  const portCount = ctx.captureInput.getPortCount();
  if (portCount === 0) {
    ctx.broadcast("midiError", { message: "No MIDI devices found" });
    ctx.captureInput = null;
    return;
  }

  if (portIndex >= portCount) {
    ctx.broadcast("midiError", { message: "Port " + portIndex + " not found" });
    ctx.captureInput = null;
    return;
  }

  try {
    ctx.captureInput.openPort(portIndex);
    ctx.captureInput.ignoreTypes(true, true, true);
    ctx.captureInput.on("message", function (deltaTime, message) {
      const status = message[0] & 0xf0;
      const note = message[1];
      if (status === 0x90 && message[2] > 0) {
        ctx.broadcast("midiNoteCaptured", { note });
        // Close after capture
        try { ctx.captureInput.closePort(); } catch {}
        ctx.captureInput = null;
      }
    });
    ctx.broadcast("midiCaptureReady", {});
  } catch (err) {
    ctx.broadcast("midiError", { message: "Failed to open port for capture: " + err.message });
    ctx.captureInput = null;
  }
}

function handleStopCapture(ctx) {
  if (ctx.captureInput) {
    try { ctx.captureInput.closePort(); } catch {}
    ctx.captureInput = null;
  }
}

module.exports = {
  stopMonitoring,
  handleMidiMessage,
  handleListPorts,
  handleStart,
  handleStop,
  handleCaptureNote,
  handleStopCapture,
};
