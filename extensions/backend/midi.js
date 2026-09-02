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
  ctx.activeNoteChannels.clear();
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
  const statusByte = message[0];
  const status = statusByte & 0xf0;
  const channel = statusByte & 0x0f;
  const note = message[1];
  const velocity = message[2];

  const isNoteOn = status === 0x90 && velocity > 0;
  const isNoteOff = status === 0x80 || (status === 0x90 && velocity === 0);

  if (!isNoteOn && !isNoteOff) return;

  const binding = ctx.noteMap.get(note);
  // 当前按住该音号的所有 MIDI 通道集合。
  // Channels that currently hold this note number.
  const holding = ctx.activeNoteChannels.get(note) || new Set();

  if (isNoteOn) {
    // 同一通道重复触发已按住的音，才是真正的重复事件。
    // Same channel re-triggering an already-held note is a real duplicate.
    if (holding.has(channel)) {
      ctx.broadcast("midiDuplicateOn", { note });
      return;
    }
    holding.add(channel);
    ctx.activeNoteChannels.set(note, holding);

    // 只有该音号的第一次物理按下才触发映射按键；其他通道的分层叠加音色
    // 不应再次触发它。
    // Only the first physical press of this note presses the mapped key; a
    // layered voice on another channel must not re-trigger it.
    if (holding.size === 1 && binding) {
      for (const vkCode of binding) {
        const count = ctx.activeVkCount.get(vkCode) || 0;
        if (count === 0) {
          sendKey(vkCode, 0);
        }
        ctx.activeVkCount.set(vkCode, count + 1);
      }
      ctx.activeNoteBindings.set(note, binding);
    }
    const keyLabel = binding
      ? binding
          .map((vk) => getKeyName(vk) || "0x" + vk.toString(16).toUpperCase())
          .join("+")
      : null;
    ctx.broadcast("midiNoteOn", { note, velocity, key: keyLabel });
    return;
  }

  // Note-off.
  if (!holding.has(channel)) {
    ctx.broadcast("midiUnexpectedOff", { note });
    return;
  }
  holding.delete(channel);
  if (holding.size === 0) {
    ctx.activeNoteChannels.delete(note);
  } else {
    // 仍被其他通道按住——保持按键按下不放。
    // Still held by another channel — keep the key down.
    return;
  }

  // 全部通道都已抬起。使用按下时记录的绑定来释放按键，而不是当前的
  // 配置绑定——若在按住期间切换或删除了映射，按当前 noteMap 释放会让
  // 物理按键一直卡住，直到用户手动点击停止。
  // Fully released. Release keys using the binding recorded at press time,
  // NOT the current config binding — if the mapping was switched or deleted
  // while the note was held, releasing against the current noteMap would leave
  // the physical key stuck until the user manually hits Stop.
  const pressBinding = ctx.activeNoteBindings.get(note) || binding;
  ctx.activeNoteBindings.delete(note);
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
  let listInput = null;
  try {
    listInput = new midi.Input();
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
  } finally {
    // 枚举用实例用完即关，避免每次刷新（new midi.Input()）都累积一个底层
    // RtMidi 客户端句柄。
    // Close the enumeration instance so repeated refreshes do not accumulate
    // underlying RtMidi client handles.
    if (listInput) {
      try { listInput.closePort(); } catch (ignore) {}
    }
  }
}

// 让一次启动失败以用户可见错误告终。若此前有会话正在运行（例如用户正在
// 切换设备），同时广播 midiStopped，避免 GUI 在后端已停止后仍停留在假的
// "Running" 状态。
// Fail a start attempt with a user-visible error. If a previous session was
// running (e.g. the user switched devices), also broadcast midiStopped so the
// GUI never stays in a fake "Running" state after the backend stopped.
function failStart(ctx, wasRunning, message) {
  if (wasRunning) {
    ctx.broadcast("midiStopped", {});
  }
  ctx.error(message);
  ctx.broadcast("midiError", { message });
}

function handleStart(ctx, data) {
  const portIndex = typeof data.port === "number" ? data.port : 0;
  const wasRunning = !!ctx.input;
  ctx.log("Handling start command — port=" + portIndex + " configPath=" + (data.configPath || "(none)"));
  stopMonitoring(ctx);

  // 若提供了配置，则加载它。加载失败必须中止启动，而不是静默地用陈旧/
  // 空映射继续监听。
  // Load config if provided. A failed load must abort the start instead of
  // silently monitoring with a stale/empty mapping.
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
    if (!configResult) {
      failStart(ctx, wasRunning, "Failed to load config: " + data.configPath);
      return;
    }
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

  let input;
  try {
    input = new midi.Input();
  } catch (err) {
    failStart(ctx, wasRunning, "MIDI error: " + err.message);
    return;
  }

  const portCount = input.getPortCount();
  if (portCount === 0) {
    failStart(ctx, wasRunning, "No MIDI devices found");
    return;
  }

  if (portIndex >= portCount) {
    failStart(ctx, wasRunning, "Port " + portIndex + " not found (available: 0-" + (portCount - 1) + ")");
    return;
  }

  try {
    input.openPort(portIndex);
    input.ignoreTypes(true, true, true);
    input.on("message", function (deltaTime, message) {
      handleMidiMessage(ctx, deltaTime, message);
    });
    ctx.input = input;
    const portName = input.getPortName(portIndex);
    ctx.log("MIDI monitoring started — port=" + portIndex + " name=" + portName + " mappings=" + ctx.noteMap.size);
    ctx.broadcast("midiStarted", {
      port: portIndex,
      portName: portName,
    });
  } catch (err) {
    // A port that opened part-way must be released, not leaked.
    try { input.closePort(); } catch (ignore) {}
    failStart(ctx, wasRunning, "Failed to open port: " + err.message);
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

  let captureInput;
  try {
    captureInput = new midi.Input();
  } catch (err) {
    ctx.broadcast("midiError", { message: "Capture error: " + err.message });
    return;
  }

  const portCount = captureInput.getPortCount();
  if (portCount === 0) {
    // 打开失败/无设备时同样要关闭已创建的实例，避免泄漏底层句柄。
    // Close the freshly created instance on failure paths to avoid leaks.
    try { captureInput.closePort(); } catch {}
    ctx.broadcast("midiError", { message: "No MIDI devices found" });
    return;
  }

  if (portIndex >= portCount) {
    try { captureInput.closePort(); } catch {}
    ctx.broadcast("midiError", { message: "Port " + portIndex + " not found" });
    return;
  }

  try {
    captureInput.openPort(portIndex);
    captureInput.ignoreTypes(true, true, true);
    ctx.captureInput = captureInput;
    ctx.captureInput.on("message", function (deltaTime, message) {
      const status = message[0] & 0xf0;
      const note = message[1];
      if (status === 0x90 && message[2] > 0) {
        ctx.broadcast("midiNoteCaptured", { note });
        // Close after capture
        if (ctx.captureInput) {
          try { ctx.captureInput.closePort(); } catch {}
          ctx.captureInput = null;
        }
      }
    });
    ctx.broadcast("midiCaptureReady", {});
  } catch (err) {
    if (ctx.captureInput) {
      try { ctx.captureInput.closePort(); } catch {}
      ctx.captureInput = null;
    }
    ctx.broadcast("midiError", { message: "Failed to open port for capture: " + err.message });
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
