// MIDITap Extension Backend — entry point
// Connects to NeutralinoJS server via WebSocket, handles MIDI input and keyboard output

const fs = require("fs");
const path = require("path");
const { createContext } = require("./context");
const { createBroadcast, connect } = require("./ws");
const midiHandlers = require("./midi");
const configHandlers = require("./config");
const { createEventHandler } = require("./handlers");

const APP_VERSION = (() => {
  try {
    const pkg = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "..", "package.json"), "utf8"));
    return pkg.version || "0.0.0";
  } catch { return "0.0.0"; }
})();

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
