// Shared mutable state for MIDITap backend modules

function createContext({ nlPort, nlToken, nlConnectToken, nlExtensionId, baseDir }) {
  const ctx = {
    // NeutralinoJS connection params (read-only)
    NL_PORT: nlPort,
    NL_TOKEN: nlToken,
    NL_CTOKEN: nlConnectToken,
    NL_EXTID: nlExtensionId,

    // Pre-computed project root
    baseDir,

    // WebSocket
    ws: null,

    // MIDI state
    input: null,
    captureInput: null,
    // note number -> Set of MIDI channels currently holding that note. A note
    // is fully released only when every channel holding it has sent note-off,
    // so layered voices on different channels do not prematurely lift a key.
    activeNoteChannels: new Map(),
    activeNoteBindings: new Map(),
    activeVkCount: new Map(),
    noteMap: new Map(),
    currentConfigName: "mapping.json",
    currentConfigPath: null,

    // Log helpers — prefix all output with extension ID
    log(msg) {
      console.log("[" + this.NL_EXTID + "]: " + msg);
    },
    warn(msg) {
      console.warn("[" + this.NL_EXTID + "]: " + msg);
    },
    error(msg) {
      console.error("[" + this.NL_EXTID + "]: " + msg);
    },
  };

  return ctx;
}

module.exports = { createContext };
