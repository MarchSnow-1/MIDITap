// WebSocket communication layer for NeutralinoJS extension

const WebSocket = require("ws");
const { ensureConfigDir } = require("../../libs/config");
const { checkForUpdates } = require("../../libs/updater");

function uuid() {
  return "10000000-1000-4000-8000-100000000000".replace(/[018]/g, (c) =>
    (c ^ (Math.random() * 16 >> c / 4)).toString(16)
  );
}

// 通过 NeutralinoJS WebSocket 发起一次原生 API 调用。
// 每次调用注册一个按 id 匹配的临时监听器，并带超时：若服务端在超时前没有
// 响应（丢包、关闭竞态、方法失败），就移除监听器并 reject，避免 Promise
// 与监听器永久泄漏、随广播次数无限累积。
// Send a native API call through the NeutralinoJS WebSocket. Each call
// registers a per-id listener with a timeout: if the server does not answer in
// time the listener is removed and the promise rejects, so unresolved calls
// cannot leak listeners as broadcasts pile up.
const NATIVE_CALL_TIMEOUT_MS = 8000;
const pendingNativeCalls = new Set();

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

      let settled = false;
      const cleanup = () => {
        ctx.ws.removeListener("message", handler);
        pendingNativeCalls.delete(cleanup);
        clearTimeout(timer);
      };
      const finish = (fn, value) => {
        if (settled) return;
        settled = true;
        cleanup();
        fn(value);
      };

      const handler = (raw) => {
        let res;
        // 收到的帧可能不是 JSON（例如非匹配的原始数据）；解析失败时直接忽略
        // 该帧，而不是让异常穿透到事件回调。
        // A received frame may not be JSON; ignore parse failures instead of
        // letting the exception escape into the event callback.
        try {
          res = JSON.parse(raw.toString());
        } catch (e) {
          return;
        }
        if (!res || res.id !== id) return;
        if (res.data && res.data.error) {
          finish(reject, res.data.error);
        } else {
          finish(resolve, res.data);
        }
      };

      const timer = setTimeout(() => {
        finish(reject, new Error("Native call timed out: " + method));
      }, NATIVE_CALL_TIMEOUT_MS);

      // 若 socket 尚未处于 OPEN 状态（已关闭/正在连接），调用注定无法送达。
      // If the socket is not OPEN the call can never be delivered.
      if (!ctx.ws || ctx.ws.readyState !== ctx.ws.OPEN) {
        finish(reject, new Error("WebSocket is not open: " + method));
        return;
      }

      ctx.ws.on("message", handler);
      pendingNativeCalls.add(cleanup);
      ctx.ws.send(msg);
    });
  };
}

// 清理所有未决的原生调用（连接关闭/出错时调用），释放其监听器与定时器。
// Clear all pending native calls (used on connection close/error) so their
// listeners and timers are released.
function clearPendingNativeCalls() {
  for (const cleanup of Array.from(pendingNativeCalls)) {
    cleanup();
  }
}

// Broadcast event to frontend
// 向前端广播事件。socket 不在 OPEN 状态时（已关闭/正在连接/出错）直接丢弃：
// 对已关闭的 socket send 没有意义，还可能触发 error 处理器导致二次退出。
// Broadcast an event to the frontend. If the socket is not OPEN (closed,
// connecting, errored) the broadcast is dropped: sending on a closed socket is
// pointless and can re-trigger the error handler.
function createBroadcast(ctx) {
  const callNative = createCallNative(ctx);
  return function broadcast(event, data) {
    if (!ctx.ws || ctx.ws.readyState !== ctx.ws.OPEN) return;
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

  // 连接断开/出错：清理未决调用与输入状态后退出，让 Neutralino 服务端以全新
  // 的一次性 connectToken 重新拉起扩展（tokenSecurity=one-time，同一 token 不能
  // 复用，因此同进程内无法自行重连）。这里不再向已关闭的 socket 广播。
  // On close/error: clear pending calls and input state, then exit so the
  // Neutralino server respawns the extension with a fresh one-time connectToken
  // (tokenSecurity=one-time means the token cannot be reused, so the process
  // cannot reconnect by itself). No broadcast is attempted on the dead socket.
  ctx.ws.on("close", () => {
    ctx.log("WebSocket closed, shutting down");
    clearPendingNativeCalls();
    stopMonitoring();
    process.exit(0);
  });

  ctx.ws.on("error", (err) => {
    ctx.error("WebSocket error: " + err.message);
    clearPendingNativeCalls();
    stopMonitoring();
    process.exit(1);
  });
}

module.exports = { createCallNative, createBroadcast, connect };
