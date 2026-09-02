// MIDITap Extension Backend — entry point
// Connects to NeutralinoJS server via WebSocket, handles MIDI input and keyboard output

const fs = require("fs");
const path = require("path");
const { createContext } = require("./context");
const { createBroadcast, connect } = require("./ws");

// --- Runtime prerequisites --------------------------------------------------
// The backend relies on native modules (koffi, node-midi) that are compiled
// against a specific Node.js ABI, and on Windows-only Win32 APIs. Fail with a
// clear, actionable message instead of an opaque crash when the environment is
// wrong (e.g. the end-user machine has no Node, or a different Node major).

if (process.platform !== "win32" || process.arch !== "x64") {
  console.error(
    "[miditap.backend]: MIDITap currently supports Windows x64 only. " +
    "Detected " + process.platform + " " + process.arch + "."
  );
  process.exit(1);
}

const APP_VERSION = (() => {
  try {
    const pkg = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "..", "package.json"), "utf8"));
    return pkg.version || "0.0.0";
  } catch { return "0.0.0"; }
})();

// Native modules are required indirectly by the handler modules; wrap them so a
// missing or ABI-mismatched Node.js produces a helpful message.
let midiHandlers, configHandlers;
try {
  midiHandlers = require("./midi");
  configHandlers = require("./config");
} catch (err) {
  console.error(
    "[miditap.backend]: Failed to load a native backend module: " + (err && err.message) + "\n" +
    "MIDITap needs Node.js 22 (x64) — the same major version the release was built with — " +
    "because its native modules (node-midi / koffi) are compiled against that ABI.\n" +
    "Install Node.js 22+ from https://nodejs.org (make sure `node` is on your PATH) and relaunch."
  );
  process.exit(1);
}

const { createEventHandler } = require("./handlers");

// Read connection parameters from stdin (official NeutralinoJS extension protocol)
let processInput;
try {
  processInput = JSON.parse(fs.readFileSync(process.stdin.fd, "utf-8"));
} catch (e) {
  console.error("[miditap.backend]: Failed to read extension config from stdin:", e.message);
  process.exit(1);
}

// Build shared context
const ctx = createContext({
  nlPort: processInput.nlPort,
  nlToken: processInput.nlToken,
  nlConnectToken: processInput.nlConnectToken,
  nlExtensionId: processInput.nlExtensionId,
  baseDir: path.join(__dirname, "..", ".."),
});

ctx.log("Starting MIDITap backend v" + APP_VERSION + " (Node.js " + process.version + ", " + process.platform + " " + process.arch + ")");
ctx.log("Extension config loaded — nlPort=" + ctx.NL_PORT + " nlExtensionId=" + ctx.NL_EXTID);

// Wire WebSocket broadcast into context
ctx.broadcast = createBroadcast(ctx);

// Create event router
const handleEvent = createEventHandler(ctx, midiHandlers, configHandlers);

// Connect to NeutralinoJS server
connect(ctx, APP_VERSION, {
  handleEvent,
  stopMonitoring: function () { midiHandlers.stopMonitoring(ctx); },
});

// Handle shutdown
process.on("SIGINT", () => {
  ctx.log("Received SIGINT, shutting down");
  midiHandlers.stopMonitoring(ctx);
  process.exit(0);
});

process.on("SIGTERM", () => {
  ctx.log("Received SIGTERM, shutting down");
  midiHandlers.stopMonitoring(ctx);
  process.exit(0);
});
