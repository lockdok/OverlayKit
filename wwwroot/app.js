(() => {
  "use strict";

  const isOverlay = new URLSearchParams(window.location.search).get("mode") === "overlay";
  const controlPage = document.getElementById("controlPage");
  const overlayPage = document.getElementById("overlayPage");
  const overlayContent = document.getElementById("overlayContent");
  const connectionStatus = document.getElementById("connectionStatus");
  const overlayConnection = document.getElementById("overlayConnection");
  const controlError = document.getElementById("controlError");
  const heldKeys = new Map();
  const recentTimers = new Set();
  const keyboardTimers = new Map();

  let settings = {
    displayMode: "text",
    showKeyboard: true,
    showMouse: true,
    overlayVisible: true,
    fadeMilliseconds: 1200
  };
  let socket;
  let reconnectTimer;
  let mouseTimer;
  let scrollTimer;

  controlPage.hidden = isOverlay;
  overlayPage.hidden = !isOverlay;
  document.body.classList.toggle("overlay-mode", isOverlay);
  buildKeyboard();
  renderSettings();
  connect();

  function connect() {
    clearTimeout(reconnectTimer);
    socket = new WebSocket(`ws://${window.location.host}/ws`);

    socket.addEventListener("open", () => {
      setConnectionState(true);
    });
    socket.addEventListener("message", (event) => {
      try {
        handleMessage(JSON.parse(event.data));
      } catch (error) {
        showError(`Could not read a message from the companion app: ${error.message}`);
      }
    });
    socket.addEventListener("close", () => {
      setConnectionState(false);
      clearInputState();
      reconnectTimer = setTimeout(connect, 1500);
    });
    socket.addEventListener("error", () => {
      socket.close();
    });
  }

  function setConnectionState(connected) {
    if (connectionStatus) {
      connectionStatus.textContent = connected ? "Connected" : "Disconnected — reconnecting…";
      connectionStatus.classList.toggle("connected", connected);
      connectionStatus.classList.toggle("disconnected", !connected);
    }
    if (overlayConnection) {
      overlayConnection.hidden = connected;
    }
  }

  function handleMessage(message) {
    if (message.type === "settings") {
      settings = { ...settings, ...message.settings };
      renderSettings();
      return;
    }

    if (message.type === "key") {
      handleKey(message);
      return;
    }

    if (message.type === "mouse") {
      handleMouse(message);
      return;
    }

    if (message.type === "scroll") {
      handleScroll(message);
      return;
    }

    if (message.type === "error") {
      if (message.settings) {
        settings = { ...settings, ...message.settings };
        renderSettings();
      }
      showError(message.message);
    }
  }

  function renderSettings() {
    if (!isOverlay) {
      document.getElementById("displayMode").value = settings.displayMode;
      document.getElementById("showKeyboard").checked = settings.showKeyboard;
      document.getElementById("showMouse").checked = settings.showMouse;
      document.getElementById("fadeMilliseconds").value = settings.fadeMilliseconds;
      document.getElementById("fadeValue").textContent = `${settings.fadeMilliseconds} ms`;
      document.getElementById("toggleOverlay").textContent = settings.overlayVisible ? "Hide overlay" : "Show overlay";
    }

    overlayContent.hidden = !settings.overlayVisible;
    if (!settings.showKeyboard || !settings.overlayVisible) {
      clearKeyboardState();
    }
    if (!settings.showMouse || !settings.overlayVisible) {
      clearMouseState();
    }
    document.getElementById("keyboardPanel").hidden = !settings.showKeyboard;
    document.getElementById("mousePanel").hidden = !settings.showMouse;
    document.getElementById("textView").hidden = settings.displayMode !== "text";
    document.getElementById("keyboardView").hidden = settings.displayMode !== "keyboard";
    overlayPage.classList.toggle("text-mode", settings.displayMode === "text");
  }

  function sendSettings(update) {
    settings = { ...settings, ...update };
    renderSettings();
    showError("");
    if (socket && socket.readyState === WebSocket.OPEN) {
      socket.send(JSON.stringify({ type: "settings", settings: update }));
    } else {
      showError("The companion app is disconnected; settings were not saved.");
    }
  }

  function showError(message) {
    if (controlError) {
      controlError.textContent = message || "";
    }
  }

  if (!isOverlay) {
    document.getElementById("displayMode").addEventListener("change", (event) => {
      sendSettings({ displayMode: event.target.value });
    });
    document.getElementById("showKeyboard").addEventListener("change", (event) => {
      sendSettings({ showKeyboard: event.target.checked });
    });
    document.getElementById("showMouse").addEventListener("change", (event) => {
      sendSettings({ showMouse: event.target.checked });
    });
    document.getElementById("fadeMilliseconds").addEventListener("input", (event) => {
      const milliseconds = Number(event.target.value);
      document.getElementById("fadeValue").textContent = `${milliseconds} ms`;
      sendSettings({ fadeMilliseconds: milliseconds });
    });
    document.getElementById("toggleOverlay").addEventListener("click", () => {
      sendSettings({ overlayVisible: !settings.overlayVisible });
    });
    document.getElementById("copyUrl").addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(document.getElementById("overlayUrl").textContent);
        document.getElementById("copyUrl").textContent = "Copied";
        setTimeout(() => { document.getElementById("copyUrl").textContent = "Copy"; }, 1200);
      } catch {
        showError("Could not copy the URL. Select and copy it manually.");
      }
    });
  }

  function handleKey(message) {
    if (!isOverlay || !settings.showKeyboard || !settings.overlayVisible) {
      return;
    }

    const key = { vk: Number(message.vk), label: message.label };
    if (message.phase === "down") {
      heldKeys.set(key.vk, key);
      addRecentKey(key);
    } else {
      heldKeys.delete(key.vk);
    }

    renderHeldKeys();
    for (const keyElement of findKeyboardKeys(key.vk)) {
      keyElement.classList.toggle("pressed", message.phase === "down");
    }
  }

  function findKeyboardKeys(vk) {
    const aliases = {
      0x10: [0xA0, 0xA1],
      0x11: [0xA2, 0xA3],
      0x12: [0xA4, 0xA5]
    };
    const codes = aliases[vk] || [vk];
    return codes.flatMap((code) => Array.from(document.querySelectorAll(`[data-vk="${code}"]`)));
  }

  function renderHeldKeys() {
    const container = document.getElementById("heldKeys");
    container.replaceChildren();
    for (const key of heldKeys.values()) {
      const chip = document.createElement("span");
      chip.className = "key-chip";
      chip.textContent = key.label;
      container.append(chip);
    }
  }

  function addRecentKey(key) {
    highlightKeyboardKey(key.vk);
    const container = document.getElementById("recentKeys");
    const chip = document.createElement("span");
    chip.className = "recent-key";
    chip.textContent = key.label;
    container.append(chip);

    const timer = setTimeout(() => {
      chip.remove();
      recentTimers.delete(timer);
    }, settings.fadeMilliseconds);
    recentTimers.add(timer);
  }

  function highlightKeyboardKey(vk) {
    const keys = findKeyboardKeys(vk);
    for (const key of keys) {
      key.classList.add("recent");
    }

    clearTimeout(keyboardTimers.get(vk));
    const timer = setTimeout(() => {
      for (const key of keys) {
        key.classList.remove("recent");
      }
      keyboardTimers.delete(vk);
    }, settings.fadeMilliseconds);
    keyboardTimers.set(vk, timer);
  }

  function clearKeyboardState() {
    heldKeys.clear();
    for (const timer of recentTimers) {
      clearTimeout(timer);
    }
    recentTimers.clear();
    for (const timer of keyboardTimers.values()) {
      clearTimeout(timer);
    }
    keyboardTimers.clear();
    document.getElementById("heldKeys").replaceChildren();
    document.getElementById("recentKeys").replaceChildren();
    document.querySelectorAll(".keyboard-key.pressed, .keyboard-key.recent")
      .forEach((key) => key.classList.remove("pressed", "recent"));
  }

  function clearMouseState() {
    clearTimeout(mouseTimer);
    clearTimeout(scrollTimer);
    document.getElementById("mouseLeft").classList.remove("active");
    document.getElementById("mouseRight").classList.remove("active");
    document.getElementById("mouseWheel").classList.remove("active");
    document.getElementById("mouseClickLabel").textContent = "";
    document.getElementById("scrollLabel").textContent = "";
  }

  function clearInputState() {
    clearKeyboardState();
    clearMouseState();
  }

  function buildKeyboard() {
    const rows = [
      [
        ["Esc", 0x1B, "wide"], ["F1", 0x70], ["F2", 0x71], ["F3", 0x72], ["F4", 0x73],
        ["F5", 0x74], ["F6", 0x75], ["F7", 0x76], ["F8", 0x77],
        ["F9", 0x78], ["F10", 0x79], ["F11", 0x7A], ["F12", 0x7B]
      ],
      [
        ["1", 0x31], ["2", 0x32], ["3", 0x33], ["4", 0x34], ["5", 0x35], ["6", 0x36],
        ["7", 0x37], ["8", 0x38], ["9", 0x39], ["0", 0x30], ["-", 0xBD], ["=", 0xBB],
        ["Back", 0x08, "wide"]
      ],
      [
        ["Tab", 0x09, "wide"], ["Q", 0x51], ["W", 0x57], ["E", 0x45], ["R", 0x52],
        ["T", 0x54], ["Y", 0x59], ["U", 0x55], ["I", 0x49], ["O", 0x4F], ["P", 0x50],
        ["[", 0xDB], ["]", 0xDD], ["Enter", 0x0D, "wide"]
      ],
      [
        ["Caps", 0x14, "wide"], ["A", 0x41], ["S", 0x53], ["D", 0x44], ["F", 0x46],
        ["G", 0x47], ["H", 0x48], ["J", 0x4A], ["K", 0x4B], ["L", 0x4C],
        [";", 0xBA], ["'", 0xDE], ["\\", 0xDC]
      ],
      [
        ["Shift", 0xA0, "wide"], ["Z", 0x5A], ["X", 0x58], ["C", 0x43], ["V", 0x56],
        ["B", 0x42], ["N", 0x4E], ["M", 0x4D], [",", 0xBC], [".", 0xBE], ["/", 0xBF],
        ["Shift", 0xA1, "wide"]
      ],
      [
        ["Ctrl", 0xA2, "wide"], ["Win", 0x5B], ["Alt", 0xA4], ["Space", 0x20, "space"],
        ["Alt", 0xA5], ["Win", 0x5C], ["Ctrl", 0xA3, "wide"], ["←", 0x25], ["↑", 0x26], ["↓", 0x28], ["→", 0x27]
      ]
    ];

    const layout = document.getElementById("keyboardLayout");
    for (const row of rows) {
      const rowElement = document.createElement("div");
      rowElement.className = "keyboard-row";
      for (const [label, vk, size = ""] of row) {
        const key = document.createElement("span");
        key.className = `keyboard-key ${size}`.trim();
        key.dataset.vk = String(vk);
        key.textContent = label;
        rowElement.append(key);
      }
      layout.append(rowElement);
    }
  }

  function handleMouse(message) {
    if (!isOverlay || !settings.showMouse || !settings.overlayVisible) {
      return;
    }
    const button = message.button;
    const element = document.getElementById(button === "left" ? "mouseLeft" : button === "right" ? "mouseRight" : "mouseWheel");
    const label = button === "left" ? "LMB" : button === "middle" ? "MMB" : "RMB";

    if (message.phase === "down") {
      element.classList.add("active");
      document.getElementById("mouseClickLabel").textContent = label;
      clearTimeout(mouseTimer);
    } else {
      element.classList.remove("active");
      mouseTimer = setTimeout(() => {
        document.getElementById("mouseClickLabel").textContent = "";
      }, settings.fadeMilliseconds);
    }
  }

  function handleScroll(message) {
    if (!isOverlay || !settings.showMouse || !settings.overlayVisible) {
      return;
    }
    const directions = { up: "↑", down: "↓", left: "←", right: "→" };
    const direction = directions[message.direction] || "•";
    const axis = message.horizontal ? " horizontal" : "";
    const label = document.getElementById("scrollLabel");
    label.textContent = `${direction} Scroll${axis}`;
    label.classList.remove("scroll-pulse");
    void label.offsetWidth;
    label.classList.add("scroll-pulse");
    clearTimeout(scrollTimer);
    scrollTimer = setTimeout(() => { label.textContent = ""; }, settings.fadeMilliseconds);
  }
})();
