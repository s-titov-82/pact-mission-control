'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');

const scriptPath = process.argv[2];
const behavior = process.argv[3];
const script = fs.readFileSync(scriptPath, 'utf8');

function createPage({ hidden }) {
  const state = { hidden };
  const timers = new Map();
  const frames = new Map();
  const windowCaptureListeners = [];
  const documentListeners = [];
  let nextTimer = 1;
  let nextFrame = 1;

  const dispatch = event => {
    let stopped = false;
    event.target = document;
    event.stopImmediatePropagation = () => {
      stopped = true;
    };
    for (const entry of windowCaptureListeners.filter(item => item.type === event.type)) {
      entry.listener(event);
      if (stopped) {
        return;
      }
    }
    for (const entry of documentListeners.filter(item => item.type === event.type)) {
      entry.listener(event);
    }
  };

  class Document {
    addEventListener(type, listener) {
      documentListeners.push({ type, listener });
    }

    dispatchEvent(event) {
      dispatch(event);
      return true;
    }
  }
  Object.defineProperty(Document.prototype, 'hidden', {
    configurable: true,
    enumerable: true,
    get() {
      return state.hidden;
    }
  });
  Object.defineProperty(Document.prototype, 'visibilityState', {
    configurable: true,
    enumerable: true,
    get() {
      return state.hidden ? 'hidden' : 'visible';
    }
  });

  const document = new Document();
  const context = {
    Document,
    document,
    Event: class {
      constructor(type) {
        this.type = type;
      }
    },
    performance: { now: () => 1234 },
    setTimeout(callback, delay) {
      const id = nextTimer++;
      timers.set(id, { callback, delay });
      return id;
    },
    clearTimeout(id) {
      timers.delete(id);
    },
    requestAnimationFrame(callback) {
      const id = nextFrame++;
      frames.set(id, callback);
      return id;
    },
    cancelAnimationFrame(id) {
      frames.delete(id);
    },
    addEventListener(type, listener, capture) {
      assert.equal(capture, true, 'the page script listens in the capture phase');
      windowCaptureListeners.push({ type, listener });
    }
  };
  context.globalThis = context;
  vm.createContext(context);

  return {
    context,
    state,
    timers,
    document,
    run() {
      vm.runInContext(script, context, { filename: scriptPath });
    },
    setHidden(value) {
      state.hidden = value;
      dispatch({ type: 'visibilitychange' });
    },
    setActive(active) {
      vm.runInContext(
        `globalThis[Symbol.for('pact.backgroundVisibility')].setActive(${active})`,
        context);
    },
    flushTimers() {
      const due = [...timers.entries()];
      timers.clear();
      for (const [, timer] of due) {
        timer.callback();
      }
    },
    flushFrames() {
      const due = [...frames.values()];
      frames.clear();
      for (const callback of due) {
        callback(99);
      }
    }
  };
}

const behaviors = {
  'reports-visible-while-hidden'() {
    const page = createPage({ hidden: true });
    page.run();

    assert.equal(page.document.hidden, false);
    assert.equal(page.document.visibilityState, 'visible');
  },

  'withholds-only-hide-transitions'() {
    const page = createPage({ hidden: false });
    page.run();
    const seen = [];
    page.document.addEventListener('visibilitychange', () => seen.push(page.document.visibilityState));

    page.setHidden(true);
    assert.deepEqual(seen, [], 'the page must not learn that it was hidden');

    page.setHidden(false);
    assert.deepEqual(seen, ['visible'], 'the page still learns that it became visible');
  },

  'hidden-frames-run-on-a-timer-once'() {
    const page = createPage({ hidden: true });
    page.run();
    const calls = [];
    page.context.requestAnimationFrame(time => calls.push(time));

    page.flushTimers();
    page.flushFrames();

    assert.deepEqual(calls, [1234]);
  },

  'pending-frame-runs-after-the-tab-is-hidden'() {
    const page = createPage({ hidden: false });
    page.run();
    const calls = [];
    page.context.requestAnimationFrame(time => calls.push(time));
    assert.equal(page.timers.size, 0, 'a visible tab uses only native frames');

    page.setHidden(true);
    page.flushTimers();

    assert.deepEqual(calls, [1234]);
  },

  'visible-frames-use-native-frames-and-cancel'() {
    const page = createPage({ hidden: false });
    page.run();
    const calls = [];
    page.context.requestAnimationFrame(time => calls.push(`first:${time}`));
    const cancelled = page.context.requestAnimationFrame(() => calls.push('cancelled'));
    page.context.cancelAnimationFrame(cancelled);

    page.flushFrames();
    page.setHidden(true);
    page.flushTimers();

    assert.deepEqual(calls, ['first:99']);
  },

  'deactivation-gives-a-hidden-page-its-real-visibility'() {
    const page = createPage({ hidden: false });
    page.run();
    const seen = [];
    page.document.addEventListener('visibilitychange', () => seen.push(page.document.visibilityState));
    page.setHidden(true);
    const calls = [];
    page.context.requestAnimationFrame(() => calls.push('frame'));
    assert.equal(page.timers.size, 1);

    page.setActive(false);

    assert.equal(page.document.hidden, true);
    assert.deepEqual(seen, ['hidden'], 'the page learns that it is hidden');
    assert.equal(page.timers.size, 0, 'hidden frames stop running on a timer');
    page.setHidden(false);
    page.setHidden(true);
    assert.deepEqual(seen, ['hidden', 'visible', 'hidden'], 'later transitions reach the page');
    page.flushFrames();
    assert.deepEqual(calls, ['frame'], 'a pending frame still runs natively');
  },

  'reinjection-reactivates-a-deactivated-page'() {
    const page = createPage({ hidden: true });
    page.run();
    page.setActive(false);

    page.run();

    assert.equal(page.document.hidden, false);
    page.context.requestAnimationFrame(() => {});
    assert.equal(page.timers.size, 1, 'hidden frames run on a timer again');
  },

  'repeated-injection-is-idempotent'() {
    const page = createPage({ hidden: true });
    page.run();
    page.run();
    const calls = [];
    page.context.requestAnimationFrame(() => calls.push('frame'));

    assert.equal(page.timers.size, 1, 'the frame wrapper is installed once');
    page.flushTimers();
    assert.deepEqual(calls, ['frame']);
  }
};

const run = behaviors[behavior];
if (!run) {
  throw new Error(`Unknown behavior '${behavior}'.`);
}

run();
