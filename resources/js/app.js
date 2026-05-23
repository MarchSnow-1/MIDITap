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
  renderStatus();
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
      currentMapping.delete(String(note));
      renderEditMappingList();
    });
    elements.editMappingList.appendChild(row);
  });
}

function renderMapping() {
  renderEditMappingList();
}

// Log
function addLog(text, className, source) {
  var prefix = source || "[GUI]";
  var fullText = prefix + " " + text;
  var line = document.createElement("div");
  line.textContent = fullText;
  line.className = className || "";
  elements.logOutput.appendChild(line);
  elements.logOutput.scrollTop = elements.logOutput.scrollHeight;

  if (elements.logOutputFull) {
    var line2 = document.createElement("div");
    line2.textContent = fullText;
    line2.className = className || "";
    elements.logOutputFull.appendChild(line2);
    elements.logOutputFull.scrollTop = elements.logOutputFull.scrollHeight;
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
    surface.addEventListener("scroll", schedule);
    window.addEventListener("resize", schedule);
    schedule();

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

if (elements.newKeyInput) {
  elements.newKeyInput.addEventListener("focus", function () {
    elements.newKeyInput.placeholder = "Press a key...";
    keyCaptureHandler = function (e) {
      e.preventDefault();
      var key = e.key;
      if (key === " ") key = "Space";
      if (key.length === 1 && key !== " ") key = key.toLowerCase();
      elements.newKeyInput.value = key;
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
      var key = e.key;
      if (key === " ") key = "Space";
      else if (key === "Control") key = "ctrl";
      else if (key === "Shift") key = "shift";
      else if (key === "Alt") key = "alt";
      else if (key === "Meta") key = "win";
      else if (key.length === 1) key = key.toLowerCase();

      var parts = elements.newComboInput.value ? elements.newComboInput.value.split("+").filter(Boolean) : [];
      if (parts.indexOf(key) === -1) {
        parts.push(key);
        elements.newComboInput.value = parts.join("+");
      }
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
    '<br><a href="' + esc(data.url) + '" class="update-link" target="_blank">GitHub Releases</a>' +
    '</span>' +
    '<button class="update-dismiss" id="updateDismissBtn" title="' + esc(t("update.dismiss")) + '">' +
    '<img src="icons/cross.svg" alt="" width="16" height="16" draggable="false">' +
    '</button>' +
    '</div>';
  document.body.appendChild(banner);

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