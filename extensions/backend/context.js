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
    // 音号 -> 当前按住该音的所有 MIDI 通道集合。只有当持有该音的全部通道
    // 都发送了 note-off 后该音才算完全抬起，因此不同通道上的分层叠加音色
    // 不会过早释放按键。
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
