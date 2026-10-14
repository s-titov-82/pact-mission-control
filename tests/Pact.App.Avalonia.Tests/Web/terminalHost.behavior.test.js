'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const terminalHostPath = process.argv[2];
const behavior = process.argv[3];
const hostMessages = [];
const timers = new Map();
const scheduledDelays = [];
const terminalInstances = [];
const documentListeners = new Map();
const windowListeners = new Map();
let nextTimerId = 1;

function createElement() {
  const listeners = new Map();
  return {
    style: {},
    listeners,
    appendChild() {},
    remove() {},
    addEventListener(type, handler) { listeners.set(type, handler); },
    removeAttribute() {},
    focus() {},
    textContent: '',
    getBoundingClientRect: () => ({ left: 10, top: 20 })
  };
}

const rootElement = createElement();
global.document = {
  documentElement: createElement(),
  body: createElement(),
  getElementById: () => rootElement,
  createElement,
  addEventListener(type, handler, options) {
    documentListeners.set(type, { handler, options });
  }
};
global.window = {
  chrome: { webview: { postMessage: message => hostMessages.push(message) } },
  addEventListener(type, handler) { windowListeners.set(type, handler); }
};
global.setTimeout = (callback, delay) => {
  const id = nextTimerId++;
  timers.set(id, callback);
  scheduledDelays.push(delay);
  return id;
};
global.clearTimeout = id => timers.delete(id);
class FakeBufferLine {
  constructor(text, isWrapped = false, dim = [], cells = null) {
    this.text = text;
    this.isWrapped = isWrapped;
    this.dim = dim;
    this.cells = cells;
    this.cellReads = 0;
  }

  getCell(index, reused = {}) {
    this.cellReads++;
    const cell = this.cells?.[index] ?? { text: this.text[index] ?? '', width: 1, dim: this.dim.includes(index) };
    reused.getChars = () => cell.text;
    reused.getWidth = () => cell.width;
    reused.isDim = () => cell.dim;
    return reused;
  }

  translateToString(trimRight) {
    return trimRight ? this.text.trimEnd() : this.text;
  }
}

class FakeTerminal {
  constructor(options) {
    this.options = { ...options };
    this.writes = [];
    this.rows = 3;
    this.cols = 80;
    this.lines = [];
    this.selectedText = '';
    this.oscHandlers = new Map();
    this.modes = { mouseTrackingMode: 'none' };
    this.buffer = {
      active: {
        baseY: 0,
        getNullCell: () => ({}),
        getLine: index => this.lines[index]
      }
    };
    this.parser = {
      registerOscHandler: (code, handler) => {
        this.oscHandlers.set(code, handler);
        return { dispose() {} };
      }
    };
    terminalInstances.push(this);
  }

  loadAddon(addon) { addon.activate(this); }
  open(container) { this.container = container; }
  attachCustomKeyEventHandler(handler) { this.keyHandler = handler; }
  onData(handler) { this.dataHandler = handler; }
  onResize(handler) { this.resizeHandler = handler; }
  onSelectionChange(handler) {
    this.selectionChangedHandler = handler;
    return { dispose() {} };
  }
  write(text, callback) {
    this.writes.push(text);
    callback?.();
  }
  focus() {}
  dispose() {}
  hasSelection() { return this.selectedText.length > 0; }
  getSelection() { return this.selectedText; }
  clearSelection() { this.selectedText = ''; }
  paste() {}
  resize(columns, rows) {
    if (this.cols === columns && this.rows === rows) {
      return;
    }
    this.cols = columns;
    this.rows = rows;
    this.resizeHandler?.({ cols: columns, rows });
  }
  triggerData(data) { this.dataHandler(data); }
}

global.Terminal = FakeTerminal;
global.FitAddon = {
  FitAddon: class {
    activate(terminal) { this.terminal = terminal; }
    fit() { this.terminal.resize(101, 37); }
  }
};

vm.runInThisContext(fs.readFileSync(terminalHostPath, 'utf8'), { filename: terminalHostPath });
hostMessages.length = 0; // discard the ready handshake

function flushTimers() {
  while (timers.size > 0) {
    const pending = [...timers.entries()];
    timers.clear();
    for (const [, callback] of pending) {
      callback();
    }
  }
}

function flushNextTimerWithDelay(delay) {
  const entry = [...timers.entries()].find(([id]) => scheduledDelays[id - 1] === delay);
  assert.ok(entry, `expected a pending ${delay} ms timer`);
  const [id, callback] = entry;
  timers.delete(id);
  callback();
}

function snapshots() {
  return hostMessages.filter(message => message.type === 'screenSnapshot');
}

function lastHostMessage() {
  return hostMessages.at(-1);
}

function runSameFinalScreenBehavior() {
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 1 });
  const terminal = terminalInstances[0];
  terminal.rows = 1;
  terminal.lines = [new FakeBufferLine('PS D:\\Work> ')];

  window.agentTerminal.write('session-1', 'first prompt');
  flushTimers();
  assert.equal(snapshots().length, 1);

  terminal.triggerData('\r');
  window.agentTerminal.write('session-1', 'same prompt after Clear-Host');
  flushTimers();
  assert.equal(snapshots().length, 2, 'manual Enter must scope dedupe to the new activity');
  assert.equal(snapshots()[1].text, snapshots()[0].text);

  window.agentTerminal.resetSnapshotBaseline('session-1');
  window.agentTerminal.write('session-1', 'same prompt after controller submit');
  flushTimers();
  assert.equal(snapshots().length, 3, 'controller/scenario Enter must share the same reset contract');
}

function runWrappedBusyMarkerBehavior() {
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 1 });
  const terminal = terminalInstances[0];
  terminal.rows = 2;
  terminal.lines = [
    new FakeBufferLine('esc '),
    new FakeBufferLine('to interrupt', true)
  ];

  window.agentTerminal.write('session-1', 'wrapped busy marker');
  flushTimers();

  assert.equal(snapshots()[0].text, 'esc to interrupt');
}

function runWrappedClaudeComposerBehavior() {
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 1 });
  const terminal = terminalInstances[0];
  terminal.rows = 2;
  terminal.lines = [
    new FakeBufferLine('>         '),
    new FakeBufferLine('──────────────────────────────', true)
  ];

  window.agentTerminal.write('session-1', 'full-screen redraw');
  flushTimers();

  assert.equal(
    snapshots()[0].text,
    '>         ──────────────────────────────',
    'xterm logical-line reconstruction keeps padding and joins a stale wrapped row');
}

function runShowTerminalOptionsBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 7 });
  const terminal = terminalInstances[0];
  terminal.rows = 1;
  terminal.lines = [new FakeBufferLine('PS D:\\Work> ')];
  timers.clear();
  scheduledDelays.length = 0;

  window.agentTerminal.write('session-1', 'prompt');

  assert.equal(scheduledDelays.at(-1), 33);
  flushNextTimerWithDelay(33);
  assert.equal(scheduledDelays.at(-1), 7);
}

function runWrappedPwshPromptBehavior() {
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 1 });
  const terminal = terminalInstances[0];
  terminal.lines = [
    new FakeBufferLine('command output'),
    new FakeBufferLine('PS C:\\very\\long'),
    new FakeBufferLine('\\path> ', true)
  ];

  window.agentTerminal.write('session-1', 'wrapped prompt');
  flushTimers();

  assert.deepEqual(snapshots(), [{
    type: 'screenSnapshot',
    sessionId: 'session-1',
    text: 'command output\nPS C:\\very\\long\\path>',
    columns: terminal.cols,
    rows: [
      { text: 'command output', isWrapped: false, dim: [] },
      { text: 'PS C:\\very\\long', isWrapped: false, dim: [] },
      { text: '\\path>', isWrapped: true, dim: [] }
    ],
    stable: true
  }]);
}

function runDynamicChurnSnapshotBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 1 });
  const terminal = terminalInstances[0];
  terminal.rows = 1;
  terminal.lines = [new FakeBufferLine('* Thinking... (esc to interrupt)')];

  for (let i = 0; i < 5; i++) {
    window.agentTerminal.write('session-1', `spinner frame ${i}`);
    flushNextTimerWithDelay(33);
  }
  assert.equal(snapshots().length, 1, 'sustained churn must post one early dynamic snapshot');
  assert.equal(snapshots()[0].stable, false);
  assert.equal(snapshots()[0].text, '* Thinking... (esc to interrupt)');

  for (let i = 0; i < 5; i++) {
    window.agentTerminal.write('session-1', 'more spinner frames');
    flushNextTimerWithDelay(33);
  }
  assert.equal(snapshots().length, 1, 'dynamic snapshots are posted at most once per interval');

  const realNow = Date.now;
  const later = realNow() + 1000;
  Date.now = () => later;
  try {
    window.agentTerminal.write('session-1', 'frame a second later');
    flushNextTimerWithDelay(33);
  } finally {
    Date.now = realNow;
  }
  assert.equal(snapshots().length, 2, 'lasting churn posts another dynamic snapshot each interval');
  assert.equal(snapshots()[1].stable, false);

  flushTimers();
  assert.equal(snapshots().length, 3, 'screen settling must post the stable snapshot');
  assert.equal(snapshots()[2].stable, true);

  for (let i = 0; i < 5; i++) {
    window.agentTerminal.write('session-1', 'next activity frames');
    flushNextTimerWithDelay(33);
  }
  assert.equal(snapshots().length, 4, 'a stable snapshot must open a new churn episode');
  assert.equal(snapshots()[3].stable, false);
}

function runTypingProducesNoDynamicSnapshotBehavior() {
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 1 });
  const terminal = terminalInstances[0];
  terminal.rows = 1;
  terminal.lines = [new FakeBufferLine('> typed tex')];

  for (let i = 0; i < 3; i++) {
    window.agentTerminal.write('session-1', `keystroke ${i}`);
    flushTimers();
  }

  assert.equal(
    snapshots().filter(message => message.stable === false).length,
    0,
    'changes interleaved with quiet periods must never post dynamic snapshots');
}

function runRightClickOwnedByHostBehavior() {
  const mouseDown = documentListeners.get('mousedown');
  const contextMenu = documentListeners.get('contextmenu');
  assert.ok(mouseDown, 'host must intercept right mouse-down before xterm');
  assert.ok(contextMenu, 'host must handle the context menu action');
  assert.equal(mouseDown.options, true, 'right mouse interception must run in capture phase');

  let stopped = false;
  mouseDown.handler({ button: 2, stopPropagation: () => { stopped = true; } });
  assert.equal(stopped, true, 'right mouse-down must not reach a mouse-tracking TUI');

  stopped = false;
  mouseDown.handler({ button: 0, stopPropagation: () => { stopped = true; } });
  assert.equal(stopped, false, 'left mouse input must still reach the terminal');

  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });
  const terminal = terminalInstances[0];
  terminal.container.listeners.get('pointerup')({ clientX: 110, clientY: 70 });
  stopped = false;
  mouseDown.handler({ button: 2, stopPropagation: () => { stopped = true; } });
  assert.equal(stopped, true, 'without mouse tracking the host owns the right click');
  contextMenu.handler({ preventDefault() {} });
  assert.deepEqual(lastHostMessage(), { type: 'pasteRequested', sessionId: 'session-1' });

  hostMessages.length = 0;
  terminal.modes.mouseTrackingMode = 'any';
  stopped = false;
  mouseDown.handler({ button: 2, stopPropagation: () => { stopped = true; } });
  assert.equal(stopped, false, 'a mouse-tracking application must receive the right click');
  contextMenu.handler({ preventDefault() {} });
  assert.equal(hostMessages.length, 0, 'the host must not paste a right click the application received');

  contextMenu.handler({ preventDefault() {} });
  assert.deepEqual(
    lastHostMessage(),
    { type: 'pasteRequested', sessionId: 'session-1' },
    'a context menu without a forwarded press still pastes');

  hostMessages.length = 0;
  terminal.selectedText = 'selected';
  stopped = false;
  mouseDown.handler({ button: 2, stopPropagation: () => { stopped = true; } });
  assert.equal(stopped, true, 'a right click on a selection stays with the host');
  contextMenu.handler({ preventDefault() {} });
  assert.deepEqual(lastHostMessage(), {
    type: 'copySelection',
    sessionId: 'session-1',
    data: 'selected'
  });
  terminal.modes.mouseTrackingMode = 'none';

  hostMessages.length = 0;
  const osc52 = terminal.oscHandlers.get(52);
  osc52(`c;${Buffer.from('keyboard').toString('base64')}`);
  assert.deepEqual(lastHostMessage(), {
    type: 'copySelection',
    sessionId: 'session-1',
    data: 'keyboard'
  });
}

function runThemeSwitchBehavior() {
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 1 });
  const firstTerminal = terminalInstances[0];
  assert.equal(firstTerminal.options.theme.background, '#09090b');

  window.agentTerminal.setTheme('light');

  assert.equal(firstTerminal.options.theme.background, '#F8FAFC');
  assert.equal(document.documentElement.style.background, '#F8FAFC');
  assert.equal(document.body.style.background, '#F8FAFC');
  assert.equal(rootElement.style.background, '#F8FAFC');

  window.agentTerminal.createTerminal('session-2', { snapshotDebounceMs: 1 });
  assert.equal(terminalInstances[1].options.theme.background, '#F8FAFC');

  window.agentTerminal.setTheme('dark');
  assert.equal(firstTerminal.options.theme.background, '#09090b');
  assert.equal(terminalInstances[1].options.theme.background, '#09090b');
}

function runAdaptiveOutputBatchingBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });
  window.agentTerminal.createTerminal('session-2', { snapshotDebounceMs: 500 });
  const activeTerminal = terminalInstances[0];
  const hiddenTerminal = terminalInstances[1];
  timers.clear();
  scheduledDelays.length = 0;

  window.agentTerminal.write('session-1', 'active-a');
  window.agentTerminal.write('session-1', 'active-b');
  window.agentTerminal.write('session-2', 'hidden-a');
  window.agentTerminal.write('session-2', 'hidden-b');

  assert.deepEqual(activeTerminal.writes, [], 'active output must wait for its batch');
  assert.deepEqual(hiddenTerminal.writes, [], 'hidden output must wait for its larger batch');
  assert.equal(activeTerminal.options.cursorBlink, true);
  assert.equal(hiddenTerminal.options.cursorBlink, false);

  flushNextTimerWithDelay(33);
  assert.deepEqual(activeTerminal.writes, ['active-aactive-b']);
  assert.deepEqual(hiddenTerminal.writes, []);

  window.agentTerminal.showTerminal('session-2', { snapshotDebounceMs: 500 });
  assert.deepEqual(hiddenTerminal.writes, ['hidden-ahidden-b'], 'activation must flush pending output');
  assert.equal(activeTerminal.options.cursorBlink, false);
  assert.equal(hiddenTerminal.options.cursorBlink, true);

  window.agentTerminal.setHostVisible(false);
  assert.equal(hiddenTerminal.options.cursorBlink, false);
  window.agentTerminal.setHostVisible(true);
  assert.equal(hiddenTerminal.options.cursorBlink, true);

  assert.equal(activeTerminal.options.allowTransparency, false);
  assert.deepEqual(window.agentTerminal.getPerformanceSnapshot(), {
    bridgeWrites: 4,
    bridgeCharacters: 32,
    xtermWrites: 2,
    xtermCharacters: 32,
    maximumPendingCharacters: 16
  });
}

function runPrebatchedOutputBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });
  const terminal = terminalInstances[0];
  timers.clear();

  window.agentTerminal.writeBatch('session-1', 'already-batched');

  assert.deepEqual(terminal.writes, ['already-batched']);
  assert.equal(
    [...timers.values()].length,
    1,
    'a prebatched write should schedule only the screen snapshot debounce');
}

function runResizeBridgeBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });

  assert.deepEqual(hostMessages, [{
    type: 'resize',
    sessionId: 'session-1',
    cols: 101,
    rows: 37
  }]);

  hostMessages.length = 0;
  windowListeners.get('resize')();
  assert.equal(hostMessages.length, 0, 'browser resize should stay debounced');
  flushNextTimerWithDelay(100);
  assert.deepEqual(
    hostMessages,
    [],
    'an unchanged fitted size must not emit a duplicate resize');
}

function runModifiedEnterBehavior() {
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 500 });
  const terminal = terminalInstances[0];

  assert.equal(terminal.keyHandler({
    type: 'keydown',
    key: 'Enter',
    shiftKey: true,
    ctrlKey: false
  }), false);
  assert.deepEqual(lastHostMessage(), {
    type: 'input',
    sessionId: 'session-1',
    data: '\n'
  });

  hostMessages.length = 0;
  assert.equal(terminal.keyHandler({
    type: 'keyup',
    key: 'Enter',
    shiftKey: true,
    ctrlKey: false
  }), false);
  assert.deepEqual(hostMessages, [], 'keyup must not duplicate modified Enter input');
}

function runSelectionBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });
  const terminal = terminalInstances[0];
  hostMessages.length = 0;

  terminal.selectedText = 'chosen';
  terminal.selectionChangedHandler();
  assert.deepEqual(lastHostMessage(), {
    type: 'selectionChanged',
    sessionId: 'session-1',
    hasSelection: true
  });

  terminal.selectedText = '';
  terminal.selectionChangedHandler();
  assert.deepEqual(lastHostMessage(), {
    type: 'selectionChanged',
    sessionId: 'session-1',
    hasSelection: false
  });
}

function runSelectionCompletionBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });
  const terminal = terminalInstances[0];
  const pointerDown = terminal.container.listeners.get('pointerdown');
  const pointerUp = terminal.container.listeners.get('pointerup');
  assert.ok(pointerDown, 'production host must observe the start of a pointer gesture');
  assert.ok(pointerUp, 'production host must retain the release point of a pointer gesture');
  const completions = () => hostMessages.filter(message => message.type === 'selectionCompleted');

  // A selection published with no pointer release behind it (select-all, an
  // agent-owned selection) has no cursor to anchor to.
  terminal.selectedText = 'unanchored';
  terminal.selectionChangedHandler();
  assert.deepEqual(
    completions(),
    [],
    'a selection change outside a pointer gesture must not open the popover');
  terminal.selectedText = '';
  terminal.selectionChangedHandler();
  hostMessages.length = 0;

  // xterm publishes onSelectionChange from its document-level mouseup handler,
  // which runs after pointerup, so the release alone must not complete anything.
  pointerDown({ clientX: 110, clientY: 70 });
  terminal.selectedText = 'chosen';
  pointerUp({ clientX: 110, clientY: 70 });
  assert.deepEqual(
    completions(),
    [],
    'the pointer release alone must not complete a selection xterm has not published yet');

  terminal.selectionChangedHandler();
  assert.deepEqual(completions(), [{
    type: 'selectionCompleted',
    sessionId: 'session-1',
    x: 100,
    y: 50,
    revision: 3
  }], 'the selection xterm publishes after the release must open the popover at that release');

  hostMessages.length = 0;
  const osc52 = terminal.oscHandlers.get(52);
  osc52(`c;${Buffer.from('keyboard after selection').toString('base64')}`);
  assert.deepEqual(lastHostMessage(), {
    type: 'copySelection',
    sessionId: 'session-1',
    data: 'keyboard after selection'
  }, 'an ordinary xterm selection must not leave a pointer anchor for a later OSC 52 copy');

  hostMessages.length = 0;
  pointerDown({ clientX: 110, clientY: 70 });
  pointerUp({ clientX: 110, clientY: 70 });
  assert.deepEqual(
    completions(),
    [],
    'a plain click over an old selection must not reopen the popover');
}

function runSelectionDismissBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });
  const terminal = terminalInstances[0];
  hostMessages.length = 0;

  // Agents that own the mouse (Claude) never give xterm a selection, so pressing
  // or typing inside the terminal is the only signal that their internal
  // selection is gone.
  terminal.container.listeners.get('pointerdown')({ clientX: 110, clientY: 70 });
  assert.deepEqual(lastHostMessage(), {
    type: 'selectionDismissed',
    sessionId: 'session-1'
  });

  hostMessages.length = 0;
  assert.equal(terminal.keyHandler({ type: 'keydown', key: 'a' }), true);
  assert.deepEqual(lastHostMessage(), {
    type: 'selectionDismissed',
    sessionId: 'session-1'
  });
}

function runAgentMouseReleaseBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });
  const terminal = terminalInstances[0];
  const pointerUp = terminal.container.listeners.get('pointerup');
  const releases = () => hostMessages.filter(message => message.type === 'agentMouseReleased');
  hostMessages.length = 0;

  pointerUp({ clientX: 110, clientY: 70 });
  assert.deepEqual(releases(), [], 'xterm owns the selection while no application tracks the mouse');

  terminal.modes.mouseTrackingMode = 'drag';
  pointerUp({ clientX: 130, clientY: 90 });
  assert.deepEqual(releases(), [{
    type: 'agentMouseReleased',
    sessionId: 'session-1',
    x: 120,
    y: 70,
    revision: 0
  }], 'a release inside a mouse-tracking application must reach the host with its anchor');
}

function runOsc52Behavior() {
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 500 });
  const terminal = terminalInstances[0];
  const handler = terminal.oscHandlers.get(52);
  assert.ok(handler, 'production host must register OSC 52');

  terminal.container.listeners.get('pointerup')({ clientX: 110, clientY: 70 });

  assert.equal(handler(`c;${Buffer.from('copied').toString('base64')}`), true);
  assert.deepEqual(lastHostMessage(), {
    type: 'copySelection',
    sessionId: 'session-1',
    data: 'copied',
    x: 100,
    y: 50,
    revision: 0
  });

  hostMessages.length = 0;
  assert.equal(handler(`c;${Buffer.from('keyboard').toString('base64')}`), true);
  assert.deepEqual(lastHostMessage(), {
    type: 'copySelection',
    sessionId: 'session-1',
    data: 'keyboard'
  });

  hostMessages.length = 0;
  terminal.container.listeners.get('pointerup')({ clientX: 130, clientY: 90 });
  terminal.triggerData('[<0;5;3M');
  hostMessages.length = 0;
  assert.equal(handler(`c;${Buffer.from('mouse report').toString('base64')}`), true);
  assert.deepEqual(lastHostMessage(), {
    type: 'copySelection',
    sessionId: 'session-1',
    data: 'mouse report',
    x: 120,
    y: 70,
    revision: 0
  }, 'the mouse report an agent-owned selection sends must not drop its own anchor');

  hostMessages.length = 0;
  terminal.container.listeners.get('pointerup')({ clientX: 130, clientY: 90 });
  terminal.keyHandler({ type: 'keydown', key: 'y' });
  hostMessages.length = 0;
  assert.equal(handler(`c;${Buffer.from('keyboard input').toString('base64')}`), true);
  assert.deepEqual(lastHostMessage(), {
    type: 'copySelection',
    sessionId: 'session-1',
    data: 'keyboard input'
  }, 'typed input must invalidate a pointer anchor before OSC 52');

  hostMessages.length = 0;
  assert.doesNotThrow(() => handler('c;%%%'));
  assert.deepEqual(hostMessages, [], 'invalid base64 must not post clipboard data');
}

function runScreenCaptureBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });
  const terminal = terminalInstances[0];
  window.agentTerminal.write('session-1', 'queued output');
  hostMessages.length = 0;

  window.agentTerminal.captureScreen('session-1', 5);

  assert.ok(
    terminal.writes.some(write => write.includes('queued output')),
    'pending output is rendered before the capture');
  const capture = hostMessages.find(message => message.type === 'screenCapture');
  assert.equal(capture.requestId, 5);
  assert.equal(capture.sessionId, 'session-1');
  assert.equal(capture.stable, true);

  hostMessages.length = 0;
  window.agentTerminal.captureScreen('unknown', 6);
  assert.deepEqual(hostMessages, [{ type: 'screenCapture', sessionId: 'unknown', requestId: 6, missing: true }]);
}

function runSelectedTextRequestBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });
  const terminal = terminalInstances[0];

  terminal.selectedText = 'chosen';
  terminal.selectionChangedHandler();
  assert.equal(window.agentTerminal.getSelectedText(), 'chosen');

  terminal.selectedText = '';
  terminal.selectionChangedHandler();
  assert.equal(
    window.agentTerminal.getSelectedText(),
    'chosen',
    'the last app-owned selection must remain available after xterm clears it');
}

function runTerminalLinkBehavior() {
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 500 });
  window.agentTerminal.createTerminal('session-2', { snapshotDebounceMs: 500 });

  terminalInstances[1].options.linkHandler.activate(
    {},
    'https://example.test/review/42');

  assert.deepEqual(lastHostMessage(), {
    type: 'linkRequested',
    sessionId: 'session-2',
    url: 'https://example.test/review/42'
  });
}

function runHiddenTerminalsLeaveLayoutBehavior() {
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 500 });
  window.agentTerminal.createTerminal('session-2', { snapshotDebounceMs: 500 });
  const first = terminalInstances[0].container;
  const second = terminalInstances[1].container;

  // xterm pauses rendering only through an IntersectionObserver, which still
  // reports visibility:hidden elements as intersecting.
  assert.equal(first.style.display, 'none', 'a created terminal must not render until shown');
  assert.equal(second.style.display, 'none');

  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });
  assert.equal(first.style.display, '');
  assert.equal(second.style.display, 'none');

  window.agentTerminal.showTerminal('session-2', { snapshotDebounceMs: 500 });
  assert.equal(first.style.display, 'none', 'a switched-away terminal must stop rendering');
  assert.equal(second.style.display, '');
}

function runStyledSnapshotBehavior() {
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 1 });
  const terminal = terminalInstances[0];
  terminal.rows = behavior === 'styled-bottom-rows-only' ? 20 : 2;
  terminal.cols = 80;
  terminal.lines = Array.from({ length: terminal.rows }, () => new FakeBufferLine('history'));
  terminal.lines[terminal.rows - 2] = new FakeBufferLine('› hint', false, [2, 3, 4, 5]);
  terminal.lines[terminal.rows - 1] = new FakeBufferLine('  ');
  if (behavior === 'styled-wide-cells') {
    terminal.lines[0] = new FakeBufferLine('› 界e\u0301', false, [], [
      { text: '›', width: 1, dim: false }, { text: ' ', width: 1, dim: false },
      { text: '界', width: 2, dim: true }, { text: '', width: 0, dim: true },
      { text: 'e\u0301', width: 1, dim: true }
    ]);
  } else if (behavior === 'styled-wrapped-snapshot') {
    terminal.lines[0] = new FakeBufferLine('› hi', false, [2, 3]);
    terminal.lines[1] = new FakeBufferLine('nt', true, [0, 1]);
  }
  window.agentTerminal.write('session-1', 'capture');
  flushTimers();
  const first = snapshots()[0];
  assert.ok(first.rows, 'snapshot must carry physical rows');
  assert.equal(first.columns, 80);
  if (behavior === 'styled-wide-cells') {
    assert.equal(first.rows[0].text, '› 界e\u0301');
    assert.deepEqual(first.rows[0].dim, [[2, 3]]);
  } else if (behavior === 'styled-wrapped-snapshot') {
    assert.equal(first.text, '› hint');
    assert.equal(first.rows[1].isWrapped, true);
    assert.deepEqual(first.rows[1].dim, [[0, 2]]);
  } else {
    assert.deepEqual(first.rows[terminal.rows - 2].dim, [[2, 4]]);
    assert.deepEqual(first.rows[terminal.rows - 1].dim, []);
  }
  if (behavior === 'styled-bottom-rows-only') {
    assert.equal(terminal.lines[4].cellReads, 0, 'higher rows must not walk cells');
    assert.equal(first.rows[4].dim, undefined);
    assert.deepEqual(first.rows[5].dim, []);
  }
  if (behavior === 'style-only-snapshot') {
    terminal.lines[0].dim = [];
    window.agentTerminal.write('session-1', 'style changed');
    flushTimers();
    assert.equal(snapshots().length, 2);
    assert.equal(snapshots()[1].text, first.text);
    assert.deepEqual(snapshots()[1].rows[0].dim, []);
  }
}

function runRealStyledCellsBehavior() {
  const path = require('node:path');
  const { Terminal: RealTerminal } = require(path.resolve(path.dirname(terminalHostPath), 'vendor/xterm/xterm.js'));
  const real = new RealTerminal({ cols: 200, rows: 50, allowProposedApi: true });
  const marker = 'PACT_DIM_😀e\u0301界';
  real.write('\x1b[?1049h\x1b[2J\x1b[999;1H\x1b[2m' + marker + '\x1b[0m');
  flushTimers();
  window.agentTerminal.createTerminal('session-1', { snapshotDebounceMs: 1 });
  const terminal = terminalInstances[0];
  terminal.buffer = real.buffer;
  terminal.cols = 200;
  terminal.rows = 50;
  window.agentTerminal.write('session-1', 'capture');
  flushTimers();
  const snapshot = snapshots()[0];
  assert.equal(snapshot.rows[49].text, marker);
  assert.deepEqual(snapshot.rows[49].dim, [[0, marker.length]]);
  assert.equal(snapshot.text, '\n'.repeat(49) + marker);
  for (const row of snapshot.rows) {
    for (const range of row.dim ?? []) assert.ok(range[0] + range[1] <= row.text.length);
  }
}

if (behavior === 'real-styled-cells') {
  runRealStyledCellsBehavior();
} else if (behavior.startsWith('styled-') || behavior === 'style-only-snapshot') {
  runStyledSnapshotBehavior();
} else if (behavior === 'same-final-screen') {
  runSameFinalScreenBehavior();
} else if (behavior === 'wrapped-pwsh-prompt') {
  runWrappedPwshPromptBehavior();
} else if (behavior === 'wrapped-busy-marker') {
  runWrappedBusyMarkerBehavior();
} else if (behavior === 'wrapped-claude-composer') {
  runWrappedClaudeComposerBehavior();
} else if (behavior === 'show-terminal-options') {
  runShowTerminalOptionsBehavior();
} else if (behavior === 'dynamic-churn-snapshot') {
  runDynamicChurnSnapshotBehavior();
} else if (behavior === 'typing-produces-no-dynamic-snapshot') {
  runTypingProducesNoDynamicSnapshotBehavior();
} else if (behavior === 'right-click-owned-by-host') {
  runRightClickOwnedByHostBehavior();
} else if (behavior === 'terminal-link-owned-by-session') {
  runTerminalLinkBehavior();
} else if (behavior === 'theme-switch-updates-existing-and-new-terminals') {
  runThemeSwitchBehavior();
} else if (behavior === 'adaptive-output-batching') {
  runAdaptiveOutputBatchingBehavior();
} else if (behavior === 'hidden-terminals-leave-layout') {
  runHiddenTerminalsLeaveLayoutBehavior();
} else if (behavior === 'prebatched-output') {
  runPrebatchedOutputBehavior();
} else if (behavior === 'resize-bridge') {
  runResizeBridgeBehavior();
} else if (behavior === 'modified-enter') {
  runModifiedEnterBehavior();
} else if (behavior === 'selection') {
  runSelectionBehavior();
} else if (behavior === 'selection-completion') {
  runSelectionCompletionBehavior();
} else if (behavior === 'selection-dismiss') {
  runSelectionDismissBehavior();
} else if (behavior === 'agent-mouse-release') {
  runAgentMouseReleaseBehavior();
} else if (behavior === 'osc52') {
  runOsc52Behavior();
} else if (behavior === 'screen-capture') {
  runScreenCaptureBehavior();
} else if (behavior === 'selected-text-request') {
  runSelectedTextRequestBehavior();
} else {
  throw new Error(`Unknown behavior: ${behavior}`);
}
