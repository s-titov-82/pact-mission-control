// Runs at document creation in every monitored Pact browser tab. A hidden tab's WebView2
// controller is not visible, so the page sees document.hidden, gets no animation frames, and many
// web apps (TeamCity, GitLab) pause their live updates until the tab is shown again. Web
// monitoring reads the DOM of loaded hidden tabs, so the page must keep updating as if it were on
// screen. globalThis[Symbol.for('pact.backgroundVisibility')].setActive(false) gives the current
// page its real visibility back when its tab stops being monitored; running the script again
// turns the override back on.
(() => {
  'use strict';

  const marker = Symbol.for('pact.backgroundVisibility');
  if (globalThis[marker]) {
    globalThis[marker].setActive(true);
    return;
  }

  const documentPrototype = globalThis.Document && globalThis.Document.prototype;
  const hiddenDescriptor = documentPrototype
    && Object.getOwnPropertyDescriptor(documentPrototype, 'hidden');
  if (!hiddenDescriptor || typeof hiddenDescriptor.get !== 'function') {
    return;
  }

  let active = true;
  const isActuallyHidden = () => hiddenDescriptor.get.call(document) === true;

  const pins = [
    ['hidden', false],
    ['webkitHidden', false],
    ['visibilityState', 'visible'],
    ['webkitVisibilityState', 'visible']
  ].flatMap(([name, value]) => {
    const original = Object.getOwnPropertyDescriptor(documentPrototype, name);
    return original && original.configurable
      ? [{
        name,
        original,
        pinned: { configurable: true, enumerable: original.enumerable, get: () => value }
      }]
      : [];
  });
  const applyPins = () => {
    for (const pin of pins) {
      Object.defineProperty(documentPrototype, pin.name, active ? pin.pinned : pin.original);
    }
  };

  // Hidden WebView2 content produces no animation frames; a frame callback still runs on a
  // timer so rendering that waits for one is not deferred until the tab is shown. The wrappers
  // stay installed when the override is off, because the page may still hold their handles.
  const hiddenFrameDelayMs = 100;
  const nativeRequestFrame = typeof globalThis.requestAnimationFrame === 'function'
    ? globalThis.requestAnimationFrame.bind(globalThis)
    : null;
  const nativeCancelFrame = typeof globalThis.cancelAnimationFrame === 'function'
    ? globalThis.cancelAnimationFrame.bind(globalThis)
    : null;
  const pendingFrames = new Map();

  const armHiddenFrame = entry => {
    if (active && entry.timer === 0) {
      entry.timer = setTimeout(() => entry.run(performance.now()), hiddenFrameDelayMs);
    }
  };

  if (nativeRequestFrame && nativeCancelFrame) {
    let nextHandle = 0;
    globalThis.requestAnimationFrame = callback => {
      const handle = ++nextHandle;
      const entry = { frame: 0, timer: 0, run: null };
      entry.run = time => {
        if (!pendingFrames.delete(handle)) {
          return;
        }
        nativeCancelFrame(entry.frame);
        clearTimeout(entry.timer);
        callback(time);
      };
      pendingFrames.set(handle, entry);
      entry.frame = nativeRequestFrame(entry.run);
      if (isActuallyHidden()) {
        armHiddenFrame(entry);
      }
      return handle;
    };
    globalThis.cancelAnimationFrame = handle => {
      const entry = pendingFrames.get(handle);
      if (!entry) {
        return;
      }
      pendingFrames.delete(handle);
      nativeCancelFrame(entry.frame);
      clearTimeout(entry.timer);
    };
  }

  // Only the transition to hidden is withheld from the page; the transition back to visible
  // still reaches it, so a page that loaded while hidden starts normally once shown.
  const withholdHide = event => {
    if (!active || event.target !== document || !isActuallyHidden()) {
      return;
    }
    event.stopImmediatePropagation();
    for (const entry of pendingFrames.values()) {
      armHiddenFrame(entry);
    }
  };
  globalThis.addEventListener('visibilitychange', withholdHide, true);
  globalThis.addEventListener('webkitvisibilitychange', withholdHide, true);

  const setActive = enabled => {
    if (active === enabled) {
      return;
    }
    active = enabled;
    applyPins();
    if (!active) {
      for (const entry of pendingFrames.values()) {
        clearTimeout(entry.timer);
        entry.timer = 0;
      }
    }
    if (isActuallyHidden()) {
      if (active) {
        for (const entry of pendingFrames.values()) {
          armHiddenFrame(entry);
        }
      } else {
        // The page was told it stayed visible, so it learns now that it is hidden.
        document.dispatchEvent(new Event('visibilitychange'));
      }
    }
  };

  applyPins();
  Object.defineProperty(globalThis, marker, { value: Object.freeze({ setActive }) });
})();
