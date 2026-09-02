// Event router — dispatches frontend events to handler modules

function createEventHandler(ctx, midiHandlers, configHandlers) {
  return function handleEvent(event, data) {
    switch (event) {
      case "listPorts":
        midiHandlers.handleListPorts(ctx);
        break;
      case "start":
        midiHandlers.handleStart(ctx, data || {});
        break;
      case "stop":
        midiHandlers.handleStop(ctx);
        break;
      case "loadConfig":
        configHandlers.handleLoadConfig(ctx, data || {});
        break;
      case "listConfigs":
        configHandlers.handleListConfigs(ctx);
        break;
      case "renameConfig":
        configHandlers.handleRenameConfig(ctx, data || {});
        break;
      case "openConfigDir":
        configHandlers.handleOpenConfigDir(ctx);
        break;
      case "addMapping":
        configHandlers.handleAddMapping(ctx, data || {});
        break;
      case "deleteMapping":
        configHandlers.handleDeleteMapping(ctx, data || {});
        break;
      case "captureNote":
        midiHandlers.handleCaptureNote(ctx, data || {});
        break;
      case "stopCapture":
        midiHandlers.handleStopCapture(ctx);
        break;
      case "getStatus":
        // 就绪事件已由 ws.js 的 open 处理器统一广播，这里只记录日志，
        // 避免与 open 的 backendReady 重复、导致前端连打两行"后端就绪"。
        // backendReady is already broadcast once by the ws open handler;
        // only log here so the frontend does not receive it twice.
        ctx.log("Received getStatus — backend ready");
        break;
      default:
        // Internal NeutralinoJS events (appClientConnect, windowBlur, etc.) — silently ignore
    }
  };
}

module.exports = { createEventHandler };
