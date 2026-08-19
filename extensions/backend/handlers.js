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
        ctx.log("Received getStatus — backend ready");
        ctx.broadcast("backendReady", { version: process.version });
        break;
      default:
        // Internal NeutralinoJS events (appClientConnect, windowBlur, etc.) — silently ignore
    }
  };
}

module.exports = { createEventHandler };
