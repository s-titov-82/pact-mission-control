function getHostTransportName() {
  if (window.chrome?.webview) {
    return 'chrome.webview';
  }

  if (window.invokeCSharpAction) {
    return 'invokeCSharpAction';
  }

  if (window.__host) {
    return '__host';
  }

  return 'none';
}

function postHostMessage(message) {
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage(message);
    return;
  }

  if (window.invokeCSharpAction) {
    window.invokeCSharpAction(JSON.stringify(message));
    return;
  }

  if (window.__host) {
    window.__host.postMessage(JSON.stringify(message));
    return;
  }

  throw new Error('No Pact host transport.');
}

const terminalThemes = {
  dark: {
    background: '#09090b',
    foreground: '#e5e7eb',
    cursor: '#f8fafc',
    cursorAccent: '#09090b',
    selectionBackground: '#33415599',
    black: '#18181b',
    red: '#f87171',
    green: '#4ade80',
    yellow: '#fbbf24',
    blue: '#60a5fa',
    magenta: '#c084fc',
    cyan: '#22d3ee',
    white: '#d4d4d8',
    brightBlack: '#71717a',
    brightRed: '#fca5a5',
    brightGreen: '#86efac',
    brightYellow: '#fde68a',
    brightBlue: '#93c5fd',
    brightMagenta: '#d8b4fe',
    brightCyan: '#67e8f9',
    brightWhite: '#f8fafc',
    overlayPanel: '#111827',
    overlayBorder: '#334155',
    overlayAction: '#fca5a5'
  },
  light: {
    background: '#F8FAFC',
    foreground: '#111827',
    cursor: '#111827',
    cursorAccent: '#F8FAFC',
    selectionBackground: '#BFDBFE',
    black: '#1F2937',
    red: '#DC2626',
    green: '#15803D',
    yellow: '#A16207',
    blue: '#2563EB',
    magenta: '#9333EA',
    cyan: '#0E7490',
    white: '#E5E7EB',
    brightBlack: '#64748B',
    brightRed: '#B91C1C',
    brightGreen: '#166534',
    brightYellow: '#854D0E',
    brightBlue: '#1D4ED8',
    brightMagenta: '#7E22CE',
    brightCyan: '#155E75',
    brightWhite: '#FFFFFF',
    overlayPanel: '#FFFFFF',
    overlayBorder: '#CBD5E1',
    overlayAction: '#DC2626'
  }
};

let currentThemeName = 'dark';

const terminalOptions = {
  cursorBlink: true,
  convertEol: false,
  scrollback: 5000,
  allowTransparency: false,
  fontFamily: 'Cascadia Mono, Consolas, monospace',
  fontSize: 15,
  lineHeight: 1.1,
  theme: terminalThemes[currentThemeName]
};

const defaultSnapshotDebounceMs = 500;
const activeOutputBatchDelayMs = 33;
const hiddenOutputBatchDelayMs = 100;
const maximumOutputChunkLength = 64 * 1024;

// While an agent animates its screen (spinner), the debounce timer keeps
// resetting and the stable snapshot never fires. After this many consecutive
// resets one early snapshot is posted with stable:false so busy markers reach
// the host mid-activity; the host only trusts Busy verdicts from it.
const dynamicSnapshotChurnThreshold = 4;

// Live screen, not the scrolled viewport: baseY-anchored rows are what the
// terminal currently shows at the bottom, so user scrollback never affects
// classification and scrolling itself never produces snapshots.
function captureScreenText(term) {
  const buffer = term.buffer.active;
  const lines = [];
  for (let i = 0; i < term.rows; i++) {
    const line = buffer.getLine(buffer.baseY + i);
    const nextLine = i + 1 < term.rows
      ? buffer.getLine(buffer.baseY + i + 1)
      : null;
    const text = line ? line.translateToString(!nextLine?.isWrapped) : '';
    if (line?.isWrapped && lines.length > 0) {
      lines[lines.length - 1] += text;
    } else {
      lines.push(text);
    }
  }
  while (lines.length > 0 && lines[lines.length - 1].trim() === '') {
    lines.pop();
  }
  return lines.join('\n');
}

function resetSnapshotBaseline(instance) {
  if (instance.snapshotTimer !== null) {
    clearTimeout(instance.snapshotTimer);
    instance.snapshotTimer = null;
  }
  instance.lastSnapshotText = null;
  instance.churnResets = 0;
  instance.dynamicSnapshotSent = false;
}

function scheduleSnapshot(sessionId, instance) {
  if (instance.snapshotTimer !== null) {
    clearTimeout(instance.snapshotTimer);
    instance.churnResets++;
    if (instance.churnResets >= dynamicSnapshotChurnThreshold && !instance.dynamicSnapshotSent) {
      instance.dynamicSnapshotSent = true;
      postHostMessage({
        type: 'screenSnapshot',
        sessionId,
        text: captureScreenText(instance.term),
        stable: false
      });
    }
  }
  instance.snapshotTimer = setTimeout(() => {
    instance.snapshotTimer = null;
    instance.churnResets = 0;
    instance.dynamicSnapshotSent = false;
    const text = captureScreenText(instance.term);
    if (text === instance.lastSnapshotText) {
      return;
    }
    instance.lastSnapshotText = text;
    postHostMessage({ type: 'screenSnapshot', sessionId, text, stable: true });
  }, instance.snapshotDebounceMs);
}

const rootElement = document.getElementById('terminal');
// sessionId -> terminal instance state
const terminals = new Map();
let activeSessionId = null;
let hostVisible = true;
const performanceCounters = {
  bridgeWrites: 0,
  bridgeCharacters: 0,
  xtermWrites: 0,
  xtermCharacters: 0,
  maximumPendingCharacters: 0
};
let busyOverlayElement = null;
let busyOverlayPanelElement = null;
let busyOverlayTextElement = null;
let busyOverlayActionButton = null;

function getActiveInstance() {
  return activeSessionId === null ? undefined : terminals.get(activeSessionId);
}

function getBusyOverlay() {
  if (busyOverlayElement !== null) {
    rootElement.appendChild(busyOverlayElement);
    return busyOverlayElement;
  }

  const overlay = document.createElement('div');
  overlay.style.position = 'absolute';
  overlay.style.inset = '0';
  overlay.style.zIndex = '1000';
  overlay.style.display = 'none';
  overlay.style.alignItems = 'center';
  overlay.style.justifyContent = 'center';
  overlay.style.pointerEvents = 'auto';

  const panel = document.createElement('div');
  panel.style.width = '280px';
  panel.style.padding = '18px';
  panel.style.borderRadius = '6px';
  panel.style.boxSizing = 'border-box';

  const progress = document.createElement('progress');
  progress.style.width = '100%';
  progress.style.height = '6px';
  progress.removeAttribute('value');

  const text = document.createElement('div');
  text.style.marginTop = '12px';
  text.style.font = '600 14px system-ui, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif';
  text.textContent = 'Working...';

  const action = document.createElement('button');
  action.style.marginTop = '12px';
  action.style.padding = '6px 12px';
  action.style.font = '600 13px system-ui, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif';
  action.style.borderRadius = '4px';
  action.style.cursor = 'pointer';
  action.style.display = 'none';
  action.addEventListener('click', function () {
    postHostMessage({ type: 'busyOverlayAction' });
  });

  panel.appendChild(progress);
  panel.appendChild(text);
  panel.appendChild(action);
  overlay.appendChild(panel);
  rootElement.appendChild(overlay);

  busyOverlayElement = overlay;
  busyOverlayPanelElement = panel;
  busyOverlayTextElement = text;
  busyOverlayActionButton = action;
  applyTerminalTheme(currentThemeName);
  return overlay;
}

function applyTerminalTheme(name) {
  currentThemeName = name === 'light' ? 'light' : 'dark';
  const theme = terminalThemes[currentThemeName];
  terminalOptions.theme = theme;

  document.documentElement.style.background = theme.background;
  document.body.style.background = theme.background;
  rootElement.style.background = theme.background;

  for (const instance of terminals.values()) {
    instance.term.options.theme = theme;
  }

  if (busyOverlayPanelElement) {
    busyOverlayPanelElement.style.background = theme.overlayPanel;
    busyOverlayPanelElement.style.border = `1px solid ${theme.overlayBorder}`;
  }
  if (busyOverlayTextElement) {
    busyOverlayTextElement.style.color = theme.foreground;
  }
  if (busyOverlayActionButton) {
    busyOverlayActionButton.style.color = theme.overlayAction;
    busyOverlayActionButton.style.background = theme.overlayPanel;
    busyOverlayActionButton.style.border = `1px solid ${theme.overlayBorder}`;
  }
}

function setBusyOverlay(message, isVisible, dimBackground, actionLabel) {
  const overlay = getBusyOverlay();
  if (busyOverlayTextElement) {
    busyOverlayTextElement.textContent = message || 'Working...';
  }

  if (busyOverlayActionButton) {
    busyOverlayActionButton.textContent = actionLabel || '';
    busyOverlayActionButton.style.display = actionLabel ? 'inline-block' : 'none';
  }

  overlay.style.background = dimBackground ? 'rgba(0, 0, 0, 0.6)' : 'transparent';
  overlay.style.display = isVisible ? 'flex' : 'none';
}

function stripTerminalColorQueryResponses(data) {
  return data.replace(/\x1b\](?:10|11);[^\x07\x1b]*(?:\x07|\x1b\\)/g, '');
}

function createTerminalOptions(sessionId) {
  return {
    ...terminalOptions,
    linkHandler: {
      activate: (_event, url) => {
        postHostMessage({ type: 'linkRequested', sessionId, url });
      }
    }
  };
}

function createInstance(sessionId, options) {
  if (terminals.has(sessionId)) {
    return terminals.get(sessionId);
  }

  const container = document.createElement('div');
  container.style.position = 'absolute';
  container.style.inset = '0';
  container.style.visibility = 'hidden';
  rootElement.appendChild(container);

  const term = new Terminal(createTerminalOptions(sessionId));
  const fitAddon = new FitAddon.FitAddon();
  term.loadAddon(fitAddon);
  term.open(container);

  const instance = {
    sessionId, term, fitAddon, container, lastSelectedText: '', hasSelection: false,
    selectionRevision: 0,
    lastPointerAnchor: null,
    pendingSelectionAnchor: null,
    pendingOutput: '',
    outputTimer: null,
    writeInProgress: false,
    snapshotTimer: null,
    lastSnapshotText: null,
    churnResets: 0,
    dynamicSnapshotSent: false,
    snapshotDebounceMs: options?.snapshotDebounceMs ?? defaultSnapshotDebounceMs
  };
  term.options.cursorBlink = false;
  terminals.set(sessionId, instance);

  container.addEventListener('pointerdown', () => {
    instance.pendingSelectionAnchor = null;
    instance.lastPointerAnchor = null;
    // Agents that own the mouse keep their selection outside xterm, so a press
    // inside the terminal is the only evidence their selection is gone.
    postHostMessage({ type: 'selectionDismissed', sessionId });
  });

  container.addEventListener('pointerup', event => {
    const rectangle = container.getBoundingClientRect();
    const anchor = {
      x: event.clientX - rectangle.left,
      y: event.clientY - rectangle.top
    };
    // xterm publishes onSelectionChange from its own document-level mouseup
    // handler, which runs after this release; the completion is posted there.
    instance.lastPointerAnchor = anchor;
    instance.pendingSelectionAnchor = anchor;
  });

  // Shift+Enter / Ctrl+Enter must insert a newline in the agent's input box,
  // not submit. xterm emits '\r' for every Enter chord, so agents cannot
  // distinguish them. Send '\n' instead; plain Enter keeps sending '\r'.
  term.attachCustomKeyEventHandler(event => {
    if (event.type === 'keydown') {
      instance.lastPointerAnchor = null;
      instance.pendingSelectionAnchor = null;
      postHostMessage({ type: 'selectionDismissed', sessionId });
    }
    if (event.key === 'Enter' && (event.shiftKey || event.ctrlKey)) {
      if (event.type === 'keydown') {
        postHostMessage({ type: 'input', sessionId, data: '\n' });
      }
      return false;
    }
    return true;
  });

  term.onData(data => {
    // Mouse reports travel through this channel, so agent-owned selections must
    // keep their pointer anchor here; typing clears it in the key handler.
    const filteredData = stripTerminalColorQueryResponses(data);
    if (filteredData.length === 0) {
      return;
    }

    if (filteredData.includes('\r')) {
      resetSnapshotBaseline(instance);
    }

    postHostMessage({ type: 'input', sessionId, data: filteredData });
  });

  term.onResize(size => {
    postHostMessage({
      type: 'resize', sessionId, cols: size.cols, rows: size.rows
    });
  });

  term.onSelectionChange(() => {
    instance.selectionRevision += 1;
    const selectedText = term.getSelection();
    const hasSelection = selectedText.length > 0;

    if (hasSelection) {
      instance.lastSelectedText = selectedText;
    }

    if (instance.hasSelection !== hasSelection) {
      instance.hasSelection = hasSelection;

      postHostMessage({
        type: 'selectionChanged',
        sessionId,
        hasSelection
      });
    }

    const anchor = instance.pendingSelectionAnchor;
    instance.pendingSelectionAnchor = null;
    if (anchor !== null && hasSelection) {
      postHostMessage({
        type: 'selectionCompleted',
        sessionId,
        x: anchor.x,
        y: anchor.y,
        revision: instance.selectionRevision
      });
      // The xterm selection owns this gesture; a later OSC 52 copy must not
      // reuse its release point.
      instance.lastPointerAnchor = null;
    }
  });

  // OSC 52: apps (claude) copy to the clipboard through the terminal.
  // xterm.js ignores it by default, which silently drops the copy; decode
  // the base64 payload and forward it to the host clipboard.
  term.parser.registerOscHandler(52, payload => {
    const anchor = instance.lastPointerAnchor;
    instance.lastPointerAnchor = null;
    const separatorIndex = payload.indexOf(';');
    if (separatorIndex < 0) {
      return true;
    }

    const base64Text = payload.slice(separatorIndex + 1);
    if (base64Text === '' || base64Text === '?') {
      return true; // clear/query — nothing to copy
    }

    try {
      const bytes = Uint8Array.from(atob(base64Text), ch => ch.charCodeAt(0));
      const text = new TextDecoder().decode(bytes);
      if (text.length > 0) {
        const message = { type: 'copySelection', sessionId, data: text };
        if (anchor !== null) {
          message.x = anchor.x;
          message.y = anchor.y;
          message.revision = instance.selectionRevision;
        }
        postHostMessage(message);
      }
    } catch {
      // malformed base64 — swallow, the sequence is still consumed
    }

    return true;
  });

  return instance;
}

function scheduleOutput(instance) {
  if (instance.outputTimer !== null || instance.writeInProgress || instance.pendingOutput.length === 0) {
    return;
  }

  const delay = instance.sessionId === activeSessionId
    ? activeOutputBatchDelayMs
    : hiddenOutputBatchDelayMs;
  instance.outputTimer = setTimeout(() => {
    instance.outputTimer = null;
    flushOutput(instance);
  }, delay);
}

function flushOutput(instance) {
  if (instance.outputTimer !== null) {
    clearTimeout(instance.outputTimer);
    instance.outputTimer = null;
  }
  if (instance.writeInProgress || instance.pendingOutput.length === 0) {
    return;
  }

  const chunk = instance.pendingOutput.slice(0, maximumOutputChunkLength);
  instance.pendingOutput = instance.pendingOutput.slice(chunk.length);
  instance.writeInProgress = true;
  performanceCounters.xtermWrites++;
  performanceCounters.xtermCharacters += chunk.length;
  instance.term.write(chunk, () => {
    instance.writeInProgress = false;
    scheduleSnapshot(instance.sessionId, instance);
    scheduleOutput(instance);
  });
}

function updateCursorBlink() {
  for (const instance of terminals.values()) {
    instance.term.options.cursorBlink = hostVisible && instance.sessionId === activeSessionId;
  }
}

function enqueueOutput(instance, text, flushImmediately) {
  performanceCounters.bridgeWrites++;
  performanceCounters.bridgeCharacters += text.length;
  instance.pendingOutput += text;
  performanceCounters.maximumPendingCharacters = Math.max(
    performanceCounters.maximumPendingCharacters,
    instance.pendingOutput.length);
  if (flushImmediately) {
    flushOutput(instance);
  } else {
    scheduleOutput(instance);
  }
}

function resizeAllInstances() {
  const active = getActiveInstance();
  if (!active) {
    return;
  }

  // Measure once on the visible instance; identical font and stacked
  // containers mean every instance shares the same dimensions.
  active.fitAddon.fit();
  const cols = active.term.cols;
  const rows = active.term.rows;
  for (const instance of terminals.values()) {
    if (instance !== active) {
      instance.term.resize(cols, rows);
    }
  }
}

let resizeTimer = null;
window.addEventListener('resize', () => {
  if (resizeTimer !== null) {
    clearTimeout(resizeTimer);
  }
  resizeTimer = setTimeout(() => {
    resizeTimer = null;
    resizeAllInstances();
  }, 100);
});

// The host owns right-click copy/paste. Stop the button press in capture phase
// so a mouse-tracking TUI (notably Claude) cannot also handle the same gesture.
document.addEventListener('mousedown', event => {
  if (event.button === 2) {
    event.stopPropagation();
  }
}, true);

document.addEventListener('contextmenu', event => {
  event.preventDefault();

  const instance = getActiveInstance();
  if (!instance) {
    return;
  }
  instance.lastPointerAnchor = null;

  if (instance.term.hasSelection()) {
    postHostMessage({
      type: 'copySelection', sessionId: activeSessionId, data: instance.term.getSelection()
    });
    instance.lastSelectedText = '';
    instance.term.clearSelection();
    return;
  }

  postHostMessage({ type: 'pasteRequested', sessionId: activeSessionId });
});

window.agentTerminal = {
  setTheme: function (name) {
    applyTerminalTheme(name);
  },
  createTerminal: function (sessionId, options) {
    createInstance(sessionId, options);
  },
  showTerminal: function (sessionId, options) {
    const next = createInstance(sessionId, options);
    const current = getActiveInstance();
    if (current && current !== next) {
      current.container.style.visibility = 'hidden';
    }
    activeSessionId = sessionId;
    next.container.style.visibility = 'visible';
    updateCursorBlink();
    flushOutput(next);
    resizeAllInstances();
    next.term.focus();
  },
  write: function (sessionId, text) {
    const instance = terminals.get(sessionId);
    if (instance) {
      enqueueOutput(instance, text, false);
    }
  },
  writeBatch: function (sessionId, text) {
    const instance = terminals.get(sessionId);
    if (instance) {
      enqueueOutput(instance, text, true);
    }
  },
  resetSnapshotBaseline: function (sessionId) {
    const instance = terminals.get(sessionId);
    if (instance) {
      resetSnapshotBaseline(instance);
    }
  },
  disposeTerminal: function (sessionId) {
    const instance = terminals.get(sessionId);
    if (!instance) {
      return;
    }
    terminals.delete(sessionId);
    if (instance.outputTimer !== null) {
      clearTimeout(instance.outputTimer);
    }
    if (activeSessionId === sessionId) {
      activeSessionId = null;
    }
    if (instance.snapshotTimer !== null) {
      clearTimeout(instance.snapshotTimer);
    }
    instance.term.dispose();
    instance.container.remove();
  },
  getSelectedText: function () {
    const instance = getActiveInstance();
    if (!instance) {
      return '';
    }
    return instance.term.getSelection() || instance.lastSelectedText;
  },
  fit: function () {
    resizeAllInstances();
  },
  focus: function () {
    const instance = getActiveInstance();
    if (instance) {
      instance.term.focus();
    }
  },
  setBusyOverlay: function (message, isVisible, dimBackground, actionLabel) {
    setBusyOverlay(message, isVisible, dimBackground, actionLabel);
  },
  setHostVisible: function (isVisible) {
    hostVisible = isVisible;
    updateCursorBlink();
  },
  getPerformanceSnapshot: function () {
    return { ...performanceCounters };
  }
};

postHostMessage({ type: 'ready' });
