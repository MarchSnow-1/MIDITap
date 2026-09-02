// Config file handlers — load, list, rename, add mappings

const { execFile } = require("child_process");
const path = require("path");
const { loadConfig, listConfigFiles, renameConfigFile, addMappingToConfig, deleteMappingFromConfig, ensureConfigDir, getLastConfigPath, saveLastConfigPath } = require("../../libs/config");
const { getKeyName, sendKeySync } = require("../../libs/keyboard");

// Release every key that is currently held down and clear the note-tracking
// state. Used when a config is (re)applied: if the mapping for a held note
// disappears from the new config, its note-off would otherwise find no binding
// to release against, leaving the physical key stuck until Stop.
function releaseHeldKeys(ctx) {
  for (const [vkCode, count] of ctx.activeVkCount.entries()) {
    if (count > 0) {
      try {
        sendKeySync(vkCode, 0x0002);
      } catch (err) {
        ctx.error("Failed to release key 0x" + vkCode.toString(16) + ": " + err.message);
      }
    }
  }
  ctx.activeVkCount.clear();
  ctx.activeNoteBindings.clear();
  ctx.activeNotes.clear();
}

// Load a config into ctx and broadcast the authoritative configLoaded event.
// Returns the config result, or null on failure.
function applyConfig(ctx, configPath, options) {
  options = options || {};
  const configResult = loadConfig(ctx.baseDir, { verbose: false, silent: true, configPath });
  if (!configResult) return null;

  // Any mappings that are currently pressed will be replaced by the new
  // config; release them before swapping the table so notes never get stuck.
  if (ctx.activeVkCount && ctx.activeVkCount.size > 0) {
    releaseHeldKeys(ctx);
  }

  if (options.persist) saveLastConfigPath(ctx.baseDir, configResult.configPath);
  ctx.noteMap = configResult.noteMap;
  ctx.currentConfigName = configResult.name;
  ctx.currentConfigPath = configResult.configPath;

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
  return configResult;
}

function handleLoadConfig(ctx, data) {
  ctx.log("Handling loadConfig command — requested path=" + (data.path || "(none)"));
  let resolvedPath = data.path || null;

  if (!resolvedPath) {
    resolvedPath = getLastConfigPath(ctx.baseDir);
    if (resolvedPath) {
      ctx.log("Restoring last config: " + resolvedPath);
    }
  }
  if (resolvedPath && !path.isAbsolute(resolvedPath)) {
    resolvedPath = path.resolve(ctx.baseDir, resolvedPath);
  }

  const configResult = applyConfig(ctx, resolvedPath, { persist: true });
  if (!configResult) {
    ctx.warn("Failed to load config: " + (data.path || "default"));
    ctx.broadcast("midiError", {
      message: "Failed to load config: " + (data.path || "default"),
    });
    return;
  }
  ctx.log("Config loaded — name=" + configResult.name + " file=" + path.basename(configResult.configPath) + " mappings=" + ctx.noteMap.size);
}

function handleListConfigs(ctx) {
  const list = listConfigFiles(ctx.baseDir);
  ctx.broadcast("configList", { configs: list });
}

function handleRenameConfig(ctx, data) {
  const filename = data.filename || "";
  const newName = (data.name || "").trim();
  if (!filename || !newName) {
    ctx.broadcast("midiError", { message: "Rename requires filename and name" });
    return;
  }
  const ok = renameConfigFile(ctx.baseDir, filename, newName);
  if (ok) {
    ctx.broadcast("configRenamed", { filename, name: newName });
    handleListConfigs(ctx);
  } else {
    ctx.broadcast("midiError", { message: "Failed to rename config: " + filename });
  }
}

function handleOpenConfigDir(ctx) {
  ensureConfigDir(ctx.baseDir);
  const dirPath = path.join(ctx.baseDir, "config");
  const command = process.platform === 'win32'
    ? { file: 'explorer.exe', args: [dirPath] }
    : process.platform === 'darwin'
      ? { file: 'open', args: [dirPath] }
      : { file: 'xdg-open', args: [dirPath] };
  execFile(command.file, command.args, (err) => {
    if (err) ctx.broadcast("midiError", { message: "Failed to open config dir: " + err.message });
  });
}

function handleAddMapping(ctx, data) {
  const note = String(data.note || "0");
  const key = String(data.key || "");
  if (!note || !key) {
    ctx.warn("addMapping missing fields — note=" + note + " key=" + key);
    ctx.broadcast("midiError", { message: "addMapping requires note and key" });
    return;
  }

  // Determine which config file to write to
  let configFilename = data.filename || "mapping.json";
  if (!configFilename.endsWith(".json")) configFilename += ".json";
  const configPath = data.configPath || path.join(ctx.baseDir, "config", configFilename);

  ctx.log("addMapping — note=" + note + " key=" + key + " config=" + path.basename(configPath));
  const ok = addMappingToConfig(ctx.baseDir, configPath, note, key);
  if (ok) {
    applyConfig(ctx, configPath);
    ctx.broadcast("midiLog", { message: "Mapping added: note " + note + " → " + key });
  } else {
    ctx.broadcast("midiError", { message: "Failed to save mapping to config file" });
  }
}

function handleDeleteMapping(ctx, data) {
  const note = String(data.note || "");
  const configPath = ctx.currentConfigPath;
  if (!note || !configPath) {
    ctx.broadcast("midiError", { message: "deleteMapping requires an active config and note" });
    return;
  }

  ctx.log("deleteMapping — note=" + note + " config=" + path.basename(configPath));
  const ok = deleteMappingFromConfig(ctx.baseDir, configPath, note);
  if (ok) {
    applyConfig(ctx, configPath);
    ctx.broadcast("midiLog", { message: "Mapping deleted: note " + note });
  } else {
    ctx.broadcast("midiError", { message: "Failed to delete mapping from config file" });
  }
}

module.exports = {
  handleLoadConfig,
  handleListConfigs,
  handleRenameConfig,
  handleOpenConfigDir,
  handleAddMapping,
  handleDeleteMapping,
};
