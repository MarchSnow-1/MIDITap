// WebSocket communication layer for NeutralinoJS extension

const WebSocket = require("ws");
const { ensureConfigDir } = require("../../libs/config");
const { checkForUpdates } = require("../../libs/updater");

function uuid() {
  return "10000000-1000-4000-8000-100000000000".replace(/[018]/g, (c) =>
    (c ^ (Math.random() * 16 >> c / 4)).toString(16)
  );
}

// Send a native API call through the NeutralinoJS WebSocket
function createCallNative(ctx) {
  return function callNative(method, data) {
    return new Promise((resolve, reject) => {
      const id = uuid();
      const msg = JSON.stringify({
        id,
        method,
        accessToken: ctx.NL_TOKEN,
        data,
      });

      const handler = (raw) => {
        const res = JSON.parse(raw.toString());
        if (res.id !== id) return;
        ctx.ws.removeListener("message", handler);
        if (res.data && res.data.error) {
          reject(res.data.error);
        } else {
          resolve(res.data);
        }
      };

      ctx.ws.on("message", handler);
      ctx.ws.send(msg);
    });
  };
}

// Broadcast event to frontend
function createBroadcast(ctx) {
  const callNative = createCallNative(ctx);
  return function broadcast(event, data) {
    callNative("app.broadcast", { event, data }).catch(() => {});
  };
}

// Connect to NeutralinoJS server
function connect(ctx, APP_VERSION, { handleEvent, stopMonitoring }) {
  const url =
    "ws://127.0.0.1:" + ctx.NL_PORT + "?extensionId=" + ctx.NL_EXTID + "&connectToken=" + ctx.NL_CTOKEN;

  ctx.ws = new WebSocket(url, { maxPayload: 10 * 1024 * 1024 });

  ctx.ws.on("open", () => {
    ensureConfigDir(ctx.baseDir);
    ctx.log("Connected to NeutralinoJS server at ws://127.0.0.1:" + ctx.NL_PORT);

    // Check for updates (non-blocking)
    checkForUpdates(APP_VERSION).then((updateInfo) => {
      if (updateInfo) {
        ctx.log("Update available — latest=" + updateInfo.latest + " current=" + APP_VERSION);
        ctx.broadcast("updateAvailable", updateInfo);
      } else {
        ctx.log("No updates available (current=" + APP_VERSION + ")");
      }
    }).catch((err) => {
      ctx.warn("Update check failed: " + err.message);
    });

    ctx.broadcast("backendReady", { version: process.version });
  });

  ctx.ws.on("message", (raw) => {
    try {
      const msg = JSON.parse(raw.toString());
      // Frontend events are dispatched as { event, data } messages
      if (msg.event && !msg.id) {
        handleEvent(msg.event, msg.data);
      }
    } catch (e) {
      // Ignore parse errors from non-JSON messages
    }
  });

  ctx.ws.on("close", () => {
    ctx.log("WebSocket closed, shutting down");
    stopMonitoring();
    ctx.broadcast("midiLog", { message: "Backend disconnected" });
    process.exit(0);
  });

  ctx.ws.on("error", (err) => {
    ctx.error("WebSocket error: " + err.message);
    stopMonitoring();
    ctx.broadcast("midiError", { message: "Connection error: " + err.message });
    process.exit(1);
  });
}

module.exports = { createCallNative, createBroadcast, connect };
