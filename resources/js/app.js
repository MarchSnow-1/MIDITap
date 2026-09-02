// MIDITap Frontend - uses NeutralinoJS extensions API for backend communication

const EXTENSION_ID = "miditap.backend";

const elements = {
  statusIndicator: document.getElementById("statusIndicator"),
  deviceList: document.getElementById("deviceList"),
  refreshPorts: document.getElementById("refreshPorts"),
  startStop: document.getElementById("startStop"),
  activeNotes: document.getElementById("activeNotes"),
  logOutput: document.getElementById("logOutput"),
  langSwitch: document.getElementById("langSwitch"),
  langToggle: document.getElementById("langToggle"),
  langMenu: document.getElementById("langMenu"),
  langValue: document.getElementById("langValue"),
  logOutputFull: document.getElementById("logOutputFull"),
  editConfigSelectWrap: document.getElementById("editConfigSelectWrap"),
  editConfigToggle: document.getElementById("editConfigToggle"),
  editConfigValue: document.getElementById("editConfigValue"),
  editConfigMenu: document.getElementById("editConfigMenu"),
  editRefreshConfigs: document.getElementById("editRefreshConfigs"),
  editPickConfig: document.getElementById("editPickConfig"),
  editConfigNameInput: document.getElementById("editConfigNameInput"),
  editRenameConfigBtn: document.getElementById("editRenameConfigBtn"),
  newNoteInput: document.getElementById("newNoteInput"),
  clearNoteBtn: document.getElementById("clearNoteBtn"),
  newKeyInput: document.getElementById("newKeyInput"),
  newComboInput: document.getElementById("newComboInput"),
  clearKeyBtn: document.getElementById("clearKeyBtn"),
  keyModeTabSingle: document.getElementById("keyModeTabSingle"),
  keyModeTabCombo: document.getElementById("keyModeTabCombo"),
  addMappingBtn: document.getElementById("addMappingBtn"),
  editMappingList: document.getElementById("editMappingList")
};

let selectedPortIndex = -1;

// --- I18N System ---

const I18N_CONFIG = {
  base: "en_US",
  storageKey: "miditap_locale"
};

let i18nDict = {};
let currentLocale = I18N_CONFIG.base;
let availableLocales = new Set();
let langOptions = new Map();
let localeAliases = new Map();
let localeCanonical = new Map();
let localeLabels = new Map();

function getBaseLocale() {
  return I18N_CONFIG.base;
}

function getFallbackLocale() {
  return I18N_CONFIG.base;
}

function normalizeLocaleKey(value) {
  if (!value) return "";
  return String(value).trim().toLowerCase().replace(/_/g, "-");
}

function normalizeLocale(locale) {
  var fallback = getBaseLocale();
  if (!locale) return fallback;
  var normalized = normalizeLocaleKey(locale);
  if (!normalized) return fallback;
  var direct = localeCanonical.get(normalized);
  if (direct) return direct;
  var alias = resolveAliasLocale(normalized);
  if (alias) return alias;
  return fallback;
}

function resolveAvailableLocale(locale) {
  var normalized = normalizeLocale(locale);
  if (!availableLocales.size) return normalized;
  return availableLocales.has(normalized) ? normalized : getFallbackLocale();
}

function resolveStrictLocale(locale) {
  if (!locale) return getFallbackLocale();
  var candidate = String(locale).trim();
  if (!candidate) return getFallbackLocale();
  return availableLocales.has(candidate) ? candidate : getFallbackLocale();
}

function resolveAliasLocale(normalized) {
  var candidate = normalized;
  while (candidate) {
    if (localeAliases.has(candidate)) return localeAliases.get(candidate);
    var index = candidate.lastIndexOf("-");
    if (index === -1) break;
    candidate = candidate.slice(0, index);
  }
  return null;
}

function isJsonObject(value) {
  return !!value && typeof value === "object" && !Array.isArray(value);
}

function parseJsonObject(text) {
  if (!text) return null;
  try {
    var cleaned = String(text).replace(/^﻿/, "");
    var parsed = JSON.parse(cleaned);
    return isJsonObject(parsed) ? parsed : null;
  } catch (e) {
    return null;
  }
}

function getLocaleRegionFromFilename(filename) {
  if (!filename) return "";
  var base = String(filename).replace(/\.json$/i, "");
  var parts = base.split("_");
  if (parts.length < 2) return "";
  var region = parts[parts.length - 1].trim();
  return region ? region.toUpperCase() : "";
}

function getLocaleLanguageFromFilename(filename) {
  if (!filename) return "";
  var base = String(filename).replace(/\.json$/i, "");
  return base.split("_")[0]?.trim() || "";
}

function collectLangOptions() {
  var options = new Map();
  if (!elements.langMenu) return options;
  elements.langMenu.querySelectorAll(".lang-option").forEach(function (option) {
    var locale = (option.dataset.lang || "").trim();
    if (!locale || options.has(locale)) return;
    var display = (option.dataset.display || "").trim();
    var spanLabel = option.querySelector(".lang-option-label")?.textContent?.trim() || "";
    var label = display || spanLabel || locale || option.textContent?.trim() || locale;
    options.set(locale, label);
  });
  return options;
}

function buildLocaleLabels() {
  return new Map(langOptions);
}

async function fetchJson(path) {
  var response = await fetch(path, { cache: "no-store" });
  if (!response.ok) {
    throw new Error("Failed to load " + path);
  }
  return response.json();
}

async function fetchJsonObject(path) {
  try {
    var data = await fetchJson(path);
    return isJsonObject(data) ? data : null;
  } catch (e) {
    return null;
  }
}

async function loadAvailableLocales() {
  var discovered = [];
  var addLocale = function (locale, parsed) {
    if (!locale) return;
    if (!discovered.includes(locale)) discovered.push(locale);
    var code = parsed ? parsed["lang.code"] : null;
    var codes = Array.isArray(code) ? code : (code ? [code] : []);
    codes.forEach(function (entry) {
      if (typeof entry !== "string") return;
      var normalized = normalizeLocaleKey(entry);
      if (!normalized || localeAliases.has(normalized)) return;
      localeAliases.set(normalized, locale);
    });
  };

  var tryReadDirectory = async function (directory) {
    try {
      var entries = await Neutralino.filesystem.readDirectory(directory);
      if (!Array.isArray(entries)) return false;
      for (var i = 0; i < entries.length; i++) {
        var entry = entries[i];
        var name = entry.entry || entry.name || entry.path?.split(/[\\/]/).pop();
        if (!name || !name.endsWith(".json")) continue;
        var type = entry.type ? String(entry.type).toLowerCase() : "";
        if (type && type !== "file") continue;
        var filePath = entry.path || (directory.replace(/[/\\]$/, "") + "/" + name);
        var content = await Neutralino.filesystem.readFile(filePath);
        var parsed = parseJsonObject(content);
        if (parsed) {
          var localeName = name.slice(0, -5);
          addLocale(localeName, parsed);
          var region = getLocaleRegionFromFilename(name);
          var language = getLocaleLanguageFromFilename(name);
          if (region && language) {
            var combined = normalizeLocaleKey(language + "-" + region);
            if (combined && !localeAliases.has(combined)) {
              localeAliases.set(combined, localeName);
            }
          }
        }
      }
      return discovered.length > 0;
    } catch (e) {
      return false;
    }
  };

  if (window.Neutralino && Neutralino.filesystem?.readDirectory) {
    var candidates = ["resources/i18n", "/resources/i18n"];
    for (var c = 0; c < candidates.length; c++) {
      if (await tryReadDirectory(candidates[c])) break;
    }
  }

  if (discovered.length === 0) {
    langOptions.forEach(function (_, locale) {
      // will be resolved via fetch in loadLocale
      discovered.push(locale);
    });
  }

  return discovered;
}

async function initI18n() {
  langOptions = collectLangOptions();
  localeAliases = new Map();
  var locales = await loadAvailableLocales();
  availableLocales = new Set(locales);
  localeCanonical = new Map();
  availableLocales.forEach(function (locale) {
    var normalized = normalizeLocaleKey(locale);
    if (normalized && !localeCanonical.has(normalized)) {
      localeCanonical.set(normalized, locale);
    }
  });
  localeLabels = buildLocaleLabels();
  availableLocales.add(I18N_CONFIG.base);
}

function getLangLabel(locale) {
  if (localeLabels.has(locale)) return localeLabels.get(locale);
  var baseLabel = localeLabels.get(getFallbackLocale());
  if (baseLabel) return baseLabel;
  return locale || getFallbackLocale();
}

async function getSavedLocale() {
  try {
    var value = await Neutralino.storage.getData(I18N_CONFIG.storageKey);
    if (!value) return null;
    try {
      var parsed = JSON.parse(value);
      if (parsed && typeof parsed.language === "string") return parsed.language;
    } catch (e) {}
    return value;
  } catch (e) {
    return null;
  }
}

async function saveLocale(locale) {
  try {
    await Neutralino.storage.setData(
      I18N_CONFIG.storageKey,
      JSON.stringify({ language: locale })
    );
  } catch (e) {}
}

async function resolveLocale() {
  var saved = await getSavedLocale();
  if (saved) return resolveAvailableLocale(saved);
  return resolveAvailableLocale(getFallbackLocale());
}

async function loadLocale(locale) {
  var baseLocale = getBaseLocale();
  var base = {};
  var baseData = await fetchJsonObject("/i18n/" + baseLocale + ".json");
  if (baseData) base = baseData;

  var target = {};
  if (locale !== baseLocale) {
    var targetData = await fetchJsonObject("/i18n/" + locale + ".json");
    if (targetData) target = targetData;
  }

  i18nDict = Object.assign({}, base, target);
  currentLocale = locale;
}

function t(key, vars) {
  var text = i18nDict[key] || key;
  if (vars) {
    Object.entries(vars).forEach(function (entry) {
      text = text.replace(new RegExp("\\{" + entry[0] + "\\}", "g"), String(entry[1]));
    });
  }
  return text;
}

function applyTranslations() {
  document.querySelectorAll("[data-i18n]").forEach(function (element) {
    element.textContent = t(element.dataset.i18n);
  });

  document.querySelectorAll("[data-i18n-html]").forEach(function (element) {
    element.innerHTML = t(element.dataset.i18nHtml);
  });

  document.querySelectorAll("[data-i18n-placeholder]").forEach(function (element) {
    element.setAttribute("placeholder", t(element.dataset.i18nPlaceholder));
  });

  document.querySelectorAll("[data-i18n-title]").forEach(function (element) {
    element.setAttribute("data-tooltip", t(element.dataset.i18nTitle));
    element.removeAttribute("title");
  });

  document.title = t("app.title");
  document.documentElement.lang = currentLocale;

  renderLangValue();
  // data-i18n 批量覆盖后再重新应用运行态文案：开始/停止按钮带有
  // data-i18n="control.start"，若缺此步，运行中切换语言会把 "Stop/停止"
  // 错误覆盖为 "Start/启动"。
  // Re-apply the running-state labels AFTER the data-i18n pass. The Start/Stop
  // button carries data-i18n="control.start", so without this a language switch
  // while monitoring would overwrite "Stop" with "Start" even though the app is
  // still running.
  setRunningState(isRunning);
  renderActiveNotes();
  renderMapping();
}

async function setLang(locale, options) {
  options = options || {};
  var notifyFallback = options.notifyFallback || false;
  var strict = options.strict || false;
  var requested = locale == null ? "" : String(locale).trim();
  var resolved = strict ? resolveStrictLocale(requested) : resolveAvailableLocale(requested);
  var fallbackUsed = requested && resolved !== requested;
  await loadLocale(resolved);
  await saveLocale(resolved);
  applyTranslations();
  if (fallbackUsed && notifyFallback) {
    addLog("Language pack \"" + requested + "\" not found. Fallback to " + getFallbackLocale() + ".", "log-warn");
  }
}

function renderLangValue() {
  if (!elements.langValue) return;
  var label = getLangLabel(currentLocale);
  elements.langValue.textContent = label;
}

// --- App State ---

let isRunning = false;
let backendReady = false;
let activeNotesMap = new Map();
let currentMapping = new Map();
let currentConfigFilename = null;

// Dispatch command to extension backend
function sendCommand(eventName, data) {
  if (typeof Neutralino === "undefined" || !Neutralino.extensions) {
    addLog(t("log.apiUnavailable"), "log-error");
    return;
  }
  Neutralino.extensions.dispatch(EXTENSION_ID, eventName, data || {}).catch(function (e) {
    addLog(t("log.dispatchError", { message: e.message || e }), "log-error");
  });
}

// Status
function setRunningState(running) {
  isRunning = running;
  if (running) {
    elements.startStop.textContent = t("control.stop");
    elements.startStop.classList.add("running");
    elements.statusIndicator.textContent = t("status.running");
    elements.statusIndicator.className = "value-row status-running";
  } else {
    elements.startStop.textContent = t("control.start");
    elements.startStop.classList.remove("running");
    elements.statusIndicator.textContent = t("status.stopped");
    elements.statusIndicator.className = "value-row status-stopped";
  }
}

function renderStatus() {
  if (!elements.statusIndicator) return;
  if (isRunning) {
    elements.statusIndicator.textContent = t("status.running");
    elements.statusIndicator.className = "value-row status-running";
  } else {
    elements.statusIndicator.textContent = t("status.stopped");
    elements.statusIndicator.className = "value-row status-stopped";
  }
}

// HTML helpers
function esc(str) {
  return String(str).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

function selectHTML(name, filename) {
  if (filename) {
    return '<span class="select-text"><span class="select-name">' + esc(name) + '</span><span class="select-file"> (' + esc(filename) + ')</span></span>';
  }
  return '<span class="select-text"><span class="select-name">' + esc(name) + '</span></span>';
}

// Device list (Home tab)
function updatePortList(ports) {
  if (!elements.deviceList) return;
  elements.deviceList.innerHTML = "";
  if (!ports || ports.length === 0) {
    elements.deviceList.innerHTML =
      '<span class="empty-hint">' + t("devices.empty") + '</span>';
    return;
  }
  ports.forEach(function (p) {
    var item = document.createElement("div");
    item.className = "device-item";
    if (p.index === selectedPortIndex) item.classList.add("selected");
    item.innerHTML =
      '<span class="device-index">#' + p.index + '</span>' +
      '<span class="device-name">' + esc(p.name) + '</span>';
    item.addEventListener("click", function () {
      var prevIndex = selectedPortIndex;
      selectedPortIndex = p.index;
      elements.deviceList.querySelectorAll(".device-item").forEach(function (el) {
        el.classList.remove("selected");
      });
      item.classList.add("selected");
      if (isRunning && p.index !== prevIndex) {
        // 切换设备会让后端重启监听并抬起所有已按下的键；先清掉旧设备遗留的
        // 音符显示，避免出现“幽灵音符”。若重启失败，后端会广播 midiStopped
        // 来复位运行状态。
        // Switching devices restarts monitoring on the backend, which releases
        // every held key. Clear the stale note display from the old device so
        // no ghost notes linger; a failed start resets the running state via
        // the backend's midiStopped broadcast.
        clearActiveNotes();
        var configPath = null;
        if (currentConfigFilename && elements.editConfigMenu) {
          var btn = elements.editConfigMenu.querySelector(".custom-option[data-filename=\"" + currentConfigFilename + "\"]");
          configPath = btn ? btn.dataset.path : null;
        }
        sendCommand("start", { port: selectedPortIndex, configPath: configPath });
      }
    });
    elements.deviceList.appendChild(item);
  });

  // Auto-select first device if none selected
  if (selectedPortIndex < 0 && ports.length > 0) {
    selectedPortIndex = ports[0].index;
    var firstItem = elements.deviceList.querySelector(".device-item");
    if (firstItem) firstItem.classList.add("selected");
  }
}

// Config list dropdown
function updateConfigList(configs) {
  function populate(menu) {
    menu.innerHTML = "";
    if (!configs || configs.length === 0) {
      return;
    }
    configs.forEach(function (c) {
      var btn = document.createElement("button");
      btn.className = "custom-option";
      btn.type = "button";
      btn.innerHTML = selectHTML(c.name, c.filename);
      btn.dataset.path = c.path;
      btn.dataset.filename = c.filename;
      btn.addEventListener("click", function () {
        sendCommand("loadConfig", { path: c.path });
        [elements.editConfigSelectWrap].forEach(function (wrap) {
          if (wrap) wrap.classList.remove("open");
        });
        if (elements.editConfigToggle) elements.editConfigToggle.setAttribute("aria-expanded", "false");
      });
      menu.appendChild(btn);
    });
  }

  populate(elements.editConfigMenu);

  if (!configs || configs.length === 0) {
    if (elements.editConfigValue) elements.editConfigValue.innerHTML = selectHTML("No configs found", null);
  }
}

function selectConfigInList(filename) {
  if (!elements.editConfigMenu) return;
  var btns = elements.editConfigMenu.querySelectorAll(".custom-option");
  for (var i = 0; i < btns.length; i++) {
    if (btns[i].dataset.filename === filename) {
      if (elements.editConfigValue) elements.editConfigValue.innerHTML = btns[i].innerHTML;
      return;
    }
  }
}

// Active notes
function renderActiveNotes() {
  elements.activeNotes.innerHTML = "";
  if (activeNotesMap.size === 0) {
    elements.activeNotes.innerHTML =
      '<span class="empty-hint">' + t("monitor.notes.empty") + '</span>';
    return;
  }
  var sorted = Array.from(activeNotesMap.entries()).sort(function (a, b) {
    return a[0] - b[0];
  });
  sorted.forEach(function (entry) {
    var note = entry[0];
    var key = entry[1];
    var badge = document.createElement("span");
    badge.className = "note-badge";
    badge.innerHTML =
      '<span class="note-num">' + note + '</span> → <span class="note-key">' + key + "</span>";
    elements.activeNotes.appendChild(badge);
  });
}

function clearActiveNotes() {
  activeNotesMap.clear();
  renderActiveNotes();
}

// Mapping display (Edit Config tab)
function renderEditMappingList() {
  if (!elements.editMappingList) return;
  elements.editMappingList.innerHTML = "";
  if (currentMapping.size === 0) {
    elements.editMappingList.innerHTML =
      '<span class="empty-hint">' + t("edit.mappingEmpty") + '</span>';
    return;
  }
  var sorted = Array.from(currentMapping.entries()).sort(function (a, b) {
    return a[0] - b[0];
  });
  sorted.forEach(function (entry) {
    var note = entry[0];
    var key = entry[1];
    var row = document.createElement("div");
    row.className = "edit-mapping-row";
    row.innerHTML =
      '<span class="map-note">' + note + '</span>' +
      '<span class="map-arrow">→</span>' +
      '<span class="map-key">' + esc(key) + '</span>' +
      '<button class="map-del" title="' + t("edit.delete") + '">×</button>';
    row.querySelector(".map-del").addEventListener("click", function () {
      sendCommand("deleteMapping", { note: String(note) });
    });
    elements.editMappingList.appendChild(row);
  });
}

function renderMapping() {
  renderEditMappingList();
}

// Log
// 日志
var MAX_LOG_LINES = 1000;

function appendLogLine(container, fullText, className) {
  var line = document.createElement("div");
  line.textContent = fullText;
  line.className = className || "";
  container.appendChild(line);
  // 限制 DOM 体量：超过上限后移除最旧的行，避免长时间监听导致内存/渲染
  // 成本无限增长。
  // Keep the DOM bounded: drop the oldest lines once the cap is exceeded so a
  // long monitoring session cannot grow memory/render cost without limit.
  while (container.childNodes.length > MAX_LOG_LINES) {
    container.removeChild(container.firstChild);
  }
  container.scrollTop = container.scrollHeight;
}

function addLog(text, className, source) {
  var prefix = source || "[GUI]";
  var fullText = prefix + " " + text;
  appendLogLine(elements.logOutput, fullText, className);

  if (elements.logOutputFull) {
    appendLogLine(elements.logOutputFull, fullText, className);
  }
}

// Initialize fake scrollbars
function initFakeScrollbars() {
  document.querySelectorAll(".fake-scroll").forEach(function (container) {
    var surface =
      container.querySelector(".scroll-surface") ||
      container.querySelector("textarea");
    var track = container.querySelector(".fake-scrollbar");
    var thumb = container.querySelector(".fake-thumb");
    if (!surface || !track || !thumb) return;

    var minThumb = 24;
    var isDragging = false;
    var dragStartY = 0;
    var dragStartScroll = 0;

    var update = function () {
      var scrollHeight = surface.scrollHeight;
      var clientHeight = surface.clientHeight;
      var trackHeight = track.clientHeight;
      if (scrollHeight <= clientHeight || trackHeight <= 0) {
        thumb.style.height = "0px";
        thumb.style.transform = "translateY(0)";
        return;
      }
      var ratio = clientHeight / scrollHeight;
      var thumbHeight = Math.max(minThumb, Math.floor(trackHeight * ratio));
      var maxThumbTop = trackHeight - thumbHeight;
      var maxScrollTop = scrollHeight - clientHeight;
      var top =
        maxScrollTop > 0
          ? (surface.scrollTop / maxScrollTop) * maxThumbTop
          : 0;
      thumb.style.height = thumbHeight + "px";
      thumb.style.transform = "translateY(" + Math.round(top) + "px)";
    };

    var schedule = function () {
      window.requestAnimationFrame(update);
    };

    // 每个容器只绑定一次监听器。initFakeScrollbars() 会在每次切换标签时被
    // 调用；若每次都重新添加 scroll/resize/pointer 监听，会导致监听器与闭包
    // 无限累积、事件处理重复执行。
    // Bind listeners only once per container. initFakeScrollbars() is invoked
    // on every tab switch; re-adding scroll/resize/pointer listeners each time
    // would leak handlers and duplicate work without bound.
    if (!container._fakeScrollInit) {
      container._fakeScrollInit = true;
      surface.addEventListener("scroll", schedule);
      window.addEventListener("resize", schedule);

      thumb.addEventListener("pointerdown", function (event) {
        if (thumb.offsetHeight === 0) return;
        isDragging = true;
        dragStartY = event.clientY;
        dragStartScroll = surface.scrollTop;
        container.classList.add("dragging");
        if (thumb.setPointerCapture) thumb.setPointerCapture(event.pointerId);
        event.preventDefault();
      });

      var onPointerMove = function (event) {
        if (!isDragging) return;
        var scrollHeight = surface.scrollHeight;
        var clientHeight = surface.clientHeight;
        var trackHeight = track.clientHeight;
        var thumbHeight = thumb.offsetHeight || minThumb;
        var maxScroll = Math.max(1, scrollHeight - clientHeight);
        var maxThumbTop = Math.max(1, trackHeight - thumbHeight);
        var delta = event.clientY - dragStartY;
        var nextScroll =
          dragStartScroll + (delta * maxScroll) / maxThumbTop;
        surface.scrollTop = Math.max(0, Math.min(maxScroll, nextScroll));
      };

      var stopDragging = function (event) {
        if (!isDragging) return;
        isDragging = false;
        container.classList.remove("dragging");
        if (thumb.releasePointerCapture)
          thumb.releasePointerCapture(event.pointerId);
      };

      window.addEventListener("pointermove", onPointerMove);
      window.addEventListener("pointerup", stopDragging);
      window.addEventListener("pointercancel", stopDragging);

      track.addEventListener("pointerdown", function (event) {
        if (event.target === thumb) return;
        var rect = track.getBoundingClientRect();
        var clickY = event.clientY - rect.top;
        var scrollHeight = surface.scrollHeight;
        var clientHeight = surface.clientHeight;
        var trackHeight = rect.height;
        var thumbHeight = thumb.offsetHeight || minThumb;
        var maxScroll = Math.max(1, scrollHeight - clientHeight);
        var maxThumbTop = Math.max(1, trackHeight - thumbHeight);
        var thumbTop = Math.min(
          Math.max(0, clickY - thumbHeight / 2),
          maxThumbTop
        );
        surface.scrollTop = (thumbTop / maxThumbTop) * maxScroll;
        schedule();
      });
    }

    // 每次调用都重新计算一次：切标签可能刚让该容器变为可见，或内容刚变化。
    // Always recompute: a tab switch may have just made this container visible
    // or its content may have changed since the last call.
    schedule();
  });
}

// Draggable region
function initDragRegion() {
  try {
    if (
      typeof Neutralino !== "undefined" &&
      Neutralino.window &&
      Neutralino.window.setDraggableRegion
    ) {
      Neutralino.window.setDraggableRegion("dragRegion");
    }
  } catch (e) {
    // non-critical
  }
}

// Open config directory
function pickConfigFile() {
  sendCommand("openConfigDir");
}

// Start/Stop
function toggleStartStop() {
  if (isRunning) {
    sendCommand("stop");
  } else {
    if (selectedPortIndex < 0) {
      addLog(t("log.warnEmptyPort"), "log-warn");
      return;
    }
    var configPath = null;
    if (currentConfigFilename && elements.editConfigMenu) {
      var btn = elements.editConfigMenu.querySelector(".custom-option[data-filename=\"" + currentConfigFilename + "\"]");
      configPath = btn ? btn.dataset.path : null;
    }
    sendCommand("start", { port: selectedPortIndex, configPath: configPath });
  }
}

// Register all event listeners from extension
function registerEvents() {
  Neutralino.events.on("backendReady", function (evt) {
    addLog(t("log.backendReady", { version: evt.detail.version }), "log-system");
  });

  Neutralino.events.on("midiPorts", function (evt) {
    var data = evt.detail;
    updatePortList(data.ports);
  });

  Neutralino.events.on("configList", function (evt) {
    var data = evt.detail;
    updateConfigList(data.configs);
    if (currentConfigFilename) {
      selectConfigInList(currentConfigFilename);
    }
  });

  Neutralino.events.on("configLoaded", function (evt) {
    var data = evt.detail;
    currentMapping = new Map(Object.entries(data.mapping));
    renderEditMappingList();

    currentConfigFilename = data.filename || null;
    if (currentConfigFilename) {
      if (elements.editConfigValue) elements.editConfigValue.innerHTML = selectHTML(data.name || data.filename, data.filename);
      if (elements.editConfigNameInput) elements.editConfigNameInput.value = data.name || data.filename;
    } else {
      if (elements.editConfigValue) elements.editConfigValue.innerHTML = selectHTML(data.name || data.path || "External config", null);
      if (elements.editConfigNameInput) elements.editConfigNameInput.value = "";
    }

    addLog(
      t("log.configLoaded", { name: data.name || data.path, count: String(data.noteCount) }),
      "log-info",
      "[CLI]"
    );
  });

  Neutralino.events.on("configRenamed", function (evt) {
    var data = evt.detail;
    addLog(t("log.configRenamed", { name: data.name }), "log-info", "[CLI]");
    if (currentConfigFilename === data.filename) {
      if (elements.editConfigNameInput) elements.editConfigNameInput.value = data.name;
    }
  });

  Neutralino.events.on("midiStarted", function (evt) {
    var data = evt.detail;
    setRunningState(true);
    addLog(
      t("log.monitorStarted", { port: String(data.port), portName: data.portName }),
      "log-system",
      "[CLI]"
    );
  });

  Neutralino.events.on("midiStopped", function () {
    setRunningState(false);
    addLog(t("log.monitorStopped"), "log-system", "[CLI]");
    clearActiveNotes();
  });

  Neutralino.events.on("midiNoteCaptured", function (evt) {
    var data = evt.detail;
    if (captureActive && elements.newNoteInput) {
      elements.newNoteInput.value = String(data.note);
      elements.newNoteInput.placeholder = "Click then play a MIDI note";
      captureActive = false;
      elements.newNoteInput.blur();
    }
  });

  Neutralino.events.on("midiNoteOn", function (evt) {
    var data = evt.detail;
    activeNotesMap.set(data.note, data.key || "unbound");
    renderActiveNotes();
    addLog(
      t("log.noteOn", { note: String(data.note), velocity: String(data.velocity), key: data.key || "(unbound)" }),
      "log-note-on",
      "[CLI]"
    );
    // Capture for edit tab "Add Mapping" note input
    if (listeningForNote && elements.newNoteInput) {
      elements.newNoteInput.value = String(data.note);
      elements.newNoteInput.placeholder = "Click then play a MIDI note";
      listeningForNote = false;
      elements.newNoteInput.blur();
    }
  });

  Neutralino.events.on("midiNoteOff", function (evt) {
    var data = evt.detail;
    activeNotesMap.delete(data.note);
    renderActiveNotes();
    addLog(t("log.noteOff", { note: String(data.note) }), "log-note-off", "[CLI]");
  });

  Neutralino.events.on("midiDuplicateOn", function (evt) {
    addLog(t("log.duplicateOn", { note: String(evt.detail.note) }), "log-warn", "[CLI]");
  });

  Neutralino.events.on("midiUnexpectedOff", function (evt) {
    addLog(t("log.unexpectedOff", { note: String(evt.detail.note) }), "log-warn", "[CLI]");
  });

  Neutralino.events.on("midiError", function (evt) {
    addLog(t("log.error", { message: evt.detail.message }), "log-error", "[CLI]");
  });

  Neutralino.events.on("midiLog", function (evt) {
    addLog(evt.detail.message, "log-info", "[CLI]");
  });

  Neutralino.events.on("updateAvailable", function (evt) {
    var data = evt.detail;
    showUpdateNotification(data);
  });
}

// Custom select toggle / close logic
function initCustomSelects() {
  function setup(wrap, toggle) {
    toggle.addEventListener("click", function () {
      var isOpen = wrap.classList.toggle("open");
      toggle.setAttribute("aria-expanded", isOpen ? "true" : "false");
    });
  }

  if (elements.editConfigSelectWrap && elements.editConfigToggle) {
    setup(elements.editConfigSelectWrap, elements.editConfigToggle);
  }

  document.addEventListener("click", function (event) {
    var wrap = elements.editConfigSelectWrap;
    if (wrap && !wrap.contains(event.target)) {
      wrap.classList.remove("open");
      if (elements.editConfigToggle) elements.editConfigToggle.setAttribute("aria-expanded", "false");
    }
  });
}

// Event listeners
elements.refreshPorts.addEventListener("click", function () {
  sendCommand("listPorts");
});

elements.startStop.addEventListener("click", toggleStartStop);

// Edit tab: config selector
if (elements.editRefreshConfigs) {
  elements.editRefreshConfigs.addEventListener("click", function () {
    sendCommand("listConfigs");
    if (currentConfigFilename && elements.editConfigMenu) {
      var btn = elements.editConfigMenu.querySelector(".custom-option[data-filename=\"" + currentConfigFilename + "\"]");
      var configPath = btn ? btn.dataset.path : null;
      if (configPath) {
        sendCommand("loadConfig", { path: configPath });
      }
    }
  });
}
if (elements.editPickConfig) {
  elements.editPickConfig.addEventListener("click", pickConfigFile);
}
if (elements.editRenameConfigBtn) {
  elements.editRenameConfigBtn.addEventListener("click", function () {
    if (!currentConfigFilename) return;
    var newName = elements.editConfigNameInput.value.trim();
    if (!newName) {
      addLog(t("log.warnEmptyName"), "log-warn");
      return;
    }
    sendCommand("renameConfig", { filename: currentConfigFilename, name: newName });
  });
}

// Edit tab: add mapping — capture next key press
var listeningForNote = false;
var captureActive = false;
var keyCaptureHandler = null;

if (elements.newNoteInput) {
  elements.newNoteInput.addEventListener("focus", function () {
    if (isRunning) {
      listeningForNote = true;
      elements.newNoteInput.placeholder = "Play a MIDI note...";
    } else {
      captureActive = true;
      elements.newNoteInput.placeholder = "Play a MIDI note...";
      var capPort = selectedPortIndex >= 0 ? selectedPortIndex : 0;
      sendCommand("captureNote", { port: capPort });
    }
  });
  elements.newNoteInput.addEventListener("blur", function () {
    listeningForNote = false;
    if (captureActive) {
      captureActive = false;
      sendCommand("stopCapture");
    }
    elements.newNoteInput.placeholder = "Click then play a MIDI note";
  });
}

// Edit tab: key mode tab switcher
var keyMode = "single"; // "single" | "combo"

function setKeyModeTab(mode) {
  keyMode = mode;
  var isSingle = mode === "single";
  elements.keyModeTabSingle.classList.toggle("active", isSingle);
  elements.keyModeTabCombo.classList.toggle("active", !isSingle);
  elements.newKeyInput.style.display = isSingle ? "" : "none";
  elements.newComboInput.style.display = isSingle ? "none" : "";
  // Clear both inputs on switch
  elements.newKeyInput.value = "";
  elements.newComboInput.value = "";
  // Clean up any dangling capture handlers
  if (keyCaptureHandler) {
    document.removeEventListener("keydown", keyCaptureHandler, true);
    keyCaptureHandler = null;
  }
  if (elements.newComboInput._cleanup) {
    elements.newComboInput._cleanup();
    elements.newComboInput._cleanup = null;
  }
}

if (elements.keyModeTabSingle) {
  elements.keyModeTabSingle.addEventListener("click", function () { setKeyModeTab("single"); });
}
if (elements.keyModeTabCombo) {
  elements.keyModeTabCombo.addEventListener("click", function () { setKeyModeTab("combo"); });
}

// --- 捕获键名归一化 Captured key normalization ------------------------------
// 后端 VK 表（libs/keyboard.js）只认识规范名（'up'、'ctrl'、'win'、
// 'semicolon' 等），而浏览器 KeyboardEvent.key 返回的是另一套拼写
// （'ArrowUp'、'Control'、'Meta'、';' 等）。因此捕获到的键在持久化前
// 必须翻译成 VK 词汇表——否则映射会在下次重载时被静默丢弃。
// The backend VK table (libs/keyboard.js) only understands its canonical
// names ('up', 'ctrl', 'win', 'semicolon', ...). Browser KeyboardEvent.key
// returns different spellings ('ArrowUp', 'Control', 'Meta', ';', ...), so
// keys captured here MUST be translated to the VK vocabulary before being
// persisted — otherwise the mapping is silently dropped on the next reload.

var CAPTURED_KEY_TO_VK = {
  ' ': 'space',
  'Spacebar': 'space',
  'Enter': 'enter',
  'Tab': 'tab',
  'Backspace': 'backspace',
  'Shift': 'shift',
  'Control': 'ctrl',
  'Alt': 'alt',
  'Meta': 'win',
  'CapsLock': 'capslock',
  'Escape': 'esc',
  'Esc': 'esc',
  'ArrowUp': 'up',
  'ArrowDown': 'down',
  'ArrowLeft': 'left',
  'ArrowRight': 'right',
  'PageUp': 'pageup',
  'PageDown': 'pagedown',
  'Home': 'home',
  'End': 'end',
  'Insert': 'insert',
  'Delete': 'delete',
  'NumLock': 'numlock',
  'PrintScreen': 'printscreen',
  'ScrollLock': 'scrolllock',
  'Pause': 'pause',
  'ContextMenu': 'apps',
  'MediaTrackNext': 'nexttrack',
  'MediaTrackPrevious': 'prevtrack',
  'MediaStop': 'stop',
  'MediaPlayPause': 'playpause',
  'AudioVolumeMute': 'mute',
  'AudioVolumeDown': 'volumedown',
  'AudioVolumeUp': 'volumeup'
};

// 美式布局物理键产生的可打印标点（含其 Shift 变体）映射回该物理键的 VK 名。
// Printable punctuation produced by a US-layout physical key (including its
// Shift variant) maps back to that physical key's VK name.
var CAPTURED_CHAR_TO_VK = {
  ';': 'semicolon', ':': 'semicolon',
  '=': 'equal', '+': 'equal',
  ',': 'comma', '<': 'comma',
  '-': 'minus', '_': 'minus',
  '.': 'period', '>': 'period',
  '/': 'slash', '?': 'slash',
  '`': 'backquote', '~': 'backquote',
  '[': 'lbracket', '{': 'lbracket',
  '\\': 'backslash', '|': 'backslash',
  ']': 'rbracket', '}': 'rbracket',
  "'": 'quote', '"': 'quote',
  '!': '1', '@': '2', '#': '3', '$': '4', '%': '5',
  '^': '6', '&': '7', '*': '8', '(': '9', ')': '0'
};

var CAPTURED_MODIFIERS = { 'shift': true, 'ctrl': true, 'alt': true, 'win': true };

function normalizeCapturedKey(keyEvent) {
  var key = typeof keyEvent.key === 'string' ? keyEvent.key : '';
  var mapped = CAPTURED_KEY_TO_VK[key];
  if (mapped) return mapped;
  if (/^F([1-9]|1[0-9]|2[0-4])$/i.test(key)) return key.toLowerCase();
  if (key.length === 1) {
    if (/[a-zA-Z0-9]/.test(key)) return key.toLowerCase();
    var charMapped = CAPTURED_CHAR_TO_VK[key];
    if (charMapped) return charMapped;
  }
  return null;
}

function isCapturedModifier(keyEvent) {
  var key = typeof keyEvent.key === 'string' ? keyEvent.key : '';
  return key === 'Shift' || key === 'Control' || key === 'Alt' || key === 'Meta';
}

// 向组合键累加器追加一个 token，避免重复项。
// Add a token to the combo accumulator without duplicating entries.
function pushComboToken(input, token) {
  var parts = input.value ? input.value.split('+').filter(Boolean) : [];
  if (parts.indexOf(token) === -1) {
    parts.push(token);
    input.value = parts.join('+');
  }
}

if (elements.newKeyInput) {
  elements.newKeyInput.addEventListener("focus", function () {
    elements.newKeyInput.placeholder = "Press a key...";
    keyCaptureHandler = function (e) {
      e.preventDefault();
      // 忽略孤立的修饰键按下，继续等待真正的按键（Shift+A → 'a'）。
      // Ignore bare modifier key-downs: wait for the actual key (Shift+A → 'a').
      if (isCapturedModifier(e)) return;
      var normalized = normalizeCapturedKey(e);
      if (!normalized) return;
      elements.newKeyInput.value = normalized;
      elements.newKeyInput.placeholder = "a–z, 0–9, F1–F12...";
      document.removeEventListener("keydown", keyCaptureHandler, true);
      keyCaptureHandler = null;
      elements.newKeyInput.blur();
    };
    document.addEventListener("keydown", keyCaptureHandler, true);
  });
  elements.newKeyInput.addEventListener("blur", function () {
    elements.newKeyInput.placeholder = "Click then press a key";
    if (keyCaptureHandler) {
      document.removeEventListener("keydown", keyCaptureHandler, true);
      keyCaptureHandler = null;
    }
  });
}

if (elements.newComboInput) {
  elements.newComboInput.addEventListener("focus", function () {
    elements.newComboInput.placeholder = "Press keys...";
    var keydownHandler = function (e) {
      e.preventDefault();
      e.stopPropagation();
      var normalized = normalizeCapturedKey(e);
      if (!normalized) return;
      pushComboToken(elements.newComboInput, normalized);
    };
    document.addEventListener("keydown", keydownHandler, true);
    elements.newComboInput._cleanup = function () {
      document.removeEventListener("keydown", keydownHandler, true);
    };
  });

  elements.newComboInput.addEventListener("blur", function () {
    elements.newComboInput.placeholder = "Click then press keys in sequence";
    if (elements.newComboInput._cleanup) {
      elements.newComboInput._cleanup();
      elements.newComboInput._cleanup = null;
    }
  });
}

if (elements.clearKeyBtn) {
  elements.clearKeyBtn.addEventListener("click", function () {
    elements.newKeyInput.value = "";
    elements.newComboInput.value = "";
  });
}
if (elements.clearNoteBtn) {
  elements.clearNoteBtn.addEventListener("click", function () {
    elements.newNoteInput.value = "";
  });
}

if (elements.addMappingBtn) {
  elements.addMappingBtn.addEventListener("click", function () {
    var note = elements.newNoteInput.value.trim();
    var keyValue = keyMode === "combo"
      ? (elements.newComboInput ? elements.newComboInput.value.trim() : "")
      : elements.newKeyInput.value.trim();
    if (!note || !keyValue) {
      addLog("Please capture MIDI Note and a Key (single or combo)", "log-warn");
      return;
    }
    var noteNum = parseInt(note, 10);
    if (isNaN(noteNum) || noteNum < 0 || noteNum > 127) {
      addLog("MIDI Note must be 0–127", "log-warn");
      return;
    }
    var configPath = null;
    if (currentConfigFilename && elements.editConfigMenu) {
      var btn = elements.editConfigMenu.querySelector(".custom-option[data-filename=\"" + currentConfigFilename + "\"]");
      configPath = btn ? btn.dataset.path : null;
    }
    sendCommand("addMapping", {
      note: String(noteNum),
      key: keyValue,
      filename: currentConfigFilename,
      configPath: configPath
    });
    currentMapping.set(String(noteNum), keyValue);
    renderEditMappingList();
    elements.newNoteInput.value = "";
    elements.newKeyInput.value = "";
    if (elements.newComboInput) elements.newComboInput.value = "";
  });
}

// Language switcher
function initLangSwitch() {
  if (!elements.langToggle || !elements.langSwitch || !elements.langMenu) return;

  elements.langToggle.addEventListener("click", function () {
    var isOpen = elements.langSwitch.classList.toggle("open");
    elements.langToggle.setAttribute("aria-expanded", isOpen ? "true" : "false");
  });

  elements.langMenu.querySelectorAll(".lang-option").forEach(function (option) {
    option.addEventListener("click", async function (event) {
      var locale = event.currentTarget.dataset.lang;
      await setLang(locale, { notifyFallback: true, strict: true });
      syncTabWidths();
      elements.langSwitch.classList.remove("open");
      elements.langToggle.setAttribute("aria-expanded", "false");
    });
  });

  document.addEventListener("click", function (event) {
    if (!elements.langSwitch.contains(event.target)) {
      elements.langSwitch.classList.remove("open");
      elements.langToggle.setAttribute("aria-expanded", "false");
    }
  });
}

// Sync tab button widths to match the widest one
function syncTabWidths() {
  var btns = document.querySelectorAll(".tab-btn");
  var maxW = 0;
  btns.forEach(function (btn) {
    btn.style.width = "";
    var w = btn.offsetWidth;
    if (w > maxW) maxW = w;
  });
  btns.forEach(function (btn) {
    btn.style.width = maxW + "px";
  });
}

// Tab switching
function initTabs() {
  var tabs = document.querySelectorAll(".tab-btn");
  var contents = document.querySelectorAll(".tab-content");

  function switchTab(target) {
    tabs.forEach(function (btn) {
      btn.classList.toggle("active", btn.dataset.tab === target);
    });
    contents.forEach(function (content) {
      content.classList.toggle("active", content.dataset.tab === target);
    });
    initFakeScrollbars();
  }

  tabs.forEach(function (btn) {
    btn.addEventListener("click", function () {
      switchTab(btn.dataset.tab);
    });
  });

  switchTab("home");
  syncTabWidths();
}

// Update notification
function showUpdateNotification(data) {
  // Prevent duplicate notifications
  if (document.getElementById("updateBanner")) return;

  var banner = document.createElement("div");
  banner.id = "updateBanner";
  banner.className = "update-banner";
  banner.innerHTML =
    '<div class="update-banner-content">' +
    '<svg class="update-icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2">' +
    '<circle cx="12" cy="12" r="10"/>' +
    '<path d="M12 16v-4M12 8h.01"/>' +
    '</svg>' +
    '<span class="update-text">' +
    esc(t("update.available", { latest: data.latest, current: data.current })) +
    '<br>' + esc(t("update.action")) +
    '<br><button class="update-link" id="updateOpenBtn">GitHub Releases</button>' +
    '</span>' +
    '<button class="update-dismiss" id="updateDismissBtn" title="' + esc(t("update.dismiss")) + '">' +
    '<img src="icons/cross.svg" alt="" width="16" height="16" draggable="false">' +
    '</button>' +
    '</div>';
  document.body.appendChild(banner);

  var releaseUrl = (typeof data.url === "string" && /^https:\/\/github\.com\/[^/]+\/[^/]+\/releases/.test(data.url))
    ? data.url : null;

  document.getElementById("updateOpenBtn").addEventListener("click", function () {
    if (!releaseUrl) return;
    if (typeof Neutralino !== "undefined" && Neutralino.os && Neutralino.os.open) {
      Neutralino.os.open(releaseUrl).catch(function () {
        addLog(t("log.error", { message: "Failed to open browser" }), "log-error");
      });
    }
  });

  document.getElementById("updateDismissBtn").addEventListener("click", function () {
    banner.remove();
  });

  addLog(t("update.available", { latest: data.latest, current: data.current }), "log-warn", "[UPD]");
}

// Init
async function init() {
  initFakeScrollbars();
  initDragRegion();
  initCustomSelects();

  // Initialize NeutralinoJS native API
  if (typeof Neutralino !== "undefined" && Neutralino.init) {
    Neutralino.init();
  }

  // Initialize i18n system
  await initI18n();
  // Initialize language switcher DOM events
  initLangSwitch();
  // Initialize tab switching
  initTabs();
  // Load saved/default locale and render all text
  await setLang(await resolveLocale());

  // Register extension event listeners
  registerEvents();

  // Wait for extension to be ready, then load initial data
  function onExtensionReady(evt) {
    if (evt.detail === EXTENSION_ID) {
      backendReady = true;
      addLog(t("log.connected"), "log-system");
      sendCommand("getStatus");
      sendCommand("listPorts");
      sendCommand("listConfigs");
      sendCommand("loadConfig", { path: null });
    }
  }

  Neutralino.events.on("extensionReady", onExtensionReady);

  // Also try immediately — extension may already be loaded
  setTimeout(function () {
    if (!backendReady) {
      if (Neutralino.extensions && Neutralino.extensions.getStats) {
        Neutralino.extensions.getStats().then(function (stats) {
          if (
            stats.connected &&
            stats.connected.indexOf(EXTENSION_ID) !== -1
          ) {
            backendReady = true;
            addLog(t("log.connected"), "log-system");
            sendCommand("getStatus");
            sendCommand("listPorts");
            sendCommand("listConfigs");
            sendCommand("loadConfig", { path: null });
          }
        });
      }
    }
  }, 1000);
}

document.addEventListener("DOMContentLoaded", init);