// Config file handlers — load, list, rename, add mappings

const { execFile } = require("child_process");
const path = require("path");
const { loadConfig, listConfigFiles, renameConfigFile, addMappingToConfig, ensureConfigDir, getLastConfigPath, saveLastConfigPath } = require("../../libs/config");
const { getKeyName } = require("../../libs/keyboard");

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

  const configResult = loadConfig(ctx.baseDir, {
    verbose: false,
    silent: true,
    configPath: resolvedPath,
  });

  if (!configResult) {
    ctx.warn("Failed to load config: " + (data.path || "default"));
    ctx.broadcast("midiError", {
      message: "Failed to load config: " + (data.path || "default"),
    });
    return;
  }

  saveLastConfigPath(ctx.baseDir, configResult.configPath);

  ctx.noteMap = configResult.noteMap;
  ctx.currentConfigName = configResult.name;
  ctx.log("Config loaded — name=" + configResult.name + " file=" + path.basename(configResult.configPath) + " mappings=" + ctx.noteMap.size);
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
    // Reload config into memory
    const configResult = loadConfig(ctx.baseDir, {
      verbose: false,
      silent: true,
      configPath,
    });
    if (configResult) {
      ctx.noteMap = configResult.noteMap;
      ctx.currentConfigName = configResult.name;
      const mapping = {};
      ctx.noteMap.forEach(function (vkCodes, n) {
        mapping[n] = vkCodes
          .map(function (vk) { return getKeyName(vk) || "0x" + vk.toString(16).toUpperCase(); })
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
    ctx.broadcast("midiLog", { message: "Mapping added: note " + note + " → " + key });
  } else {
    ctx.broadcast("midiError", { message: "Failed to save mapping to config file" });
  }
}

module.exports = {
  handleLoadConfig,
  handleListConfigs,
  handleRenameConfig,
  handleOpenConfigDir,
  handleAddMapping,
};
