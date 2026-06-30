namespace Verbeam.Api.Broadcast;

public static class ProjectorPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Verbeam Projector</title>
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link href="https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700;800;900&family=JetBrains+Mono:wght@400;500;700&display=swap" rel="stylesheet">
  <style>
    :root {
      color-scheme: dark;
      --bg: #000000;
      --ink: #f7f7f2;
      --muted: #7a7a7a;
      --line: rgba(255, 255, 255, 0.1);
      --panel: rgba(20, 20, 20, 0.65);
      --accent: #22c55e;
      --warn: #d6b86a;
      --shadow: rgba(0, 0, 0, 0.86);
      --caption-max: 104px;
      --caption-min: 34px;
      --source-size: 24px;
      --font: "Outfit", system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
      --mono: "JetBrains Mono", "SF Mono", Consolas, monospace;
      --weight: 700;
    }

    ::-webkit-scrollbar {
      display: none;
    }
    * {
      scrollbar-width: none;
      -ms-overflow-style: none;
      box-sizing: border-box;
    }

    html,
    body {
      width: 100%;
      min-height: 100%;
      margin: 0;
      overflow: hidden;
      background: var(--bg);
      color: var(--ink);
      font-family: var(--font);
      letter-spacing: 0;
    }

    /* Idle fading for UI */
    body.idle .hud,
    body.idle .controls {
      opacity: 0 !important;
      pointer-events: none;
      transform: translateY(10px);
    }

    .hud {
      transition: opacity 0.5s cubic-bezier(0.16, 1, 0.3, 1), transform 0.5s cubic-bezier(0.16, 1, 0.3, 1);
    }

    button,
    a {
      font: inherit;
    }

    a {
      color: var(--ink);
      text-decoration: none;
    }

    .projector-stage {
      min-height: 100vh;
      display: flex;
      justify-content: center;
      padding: 6vh 5vw 8vh;
      background: #000000;
    }

    .projector-stage[data-position="top"] {
      align-items: flex-start;
      padding-top: 9vh;
    }

    .projector-stage[data-position="center"] {
      align-items: center;
      padding-bottom: 6vh;
    }

    .projector-stage[data-position="bottom"] {
      align-items: flex-end;
      padding-bottom: 8vh;
    }

    .caption-window {
      width: min(92vw, 1760px);
      max-height: 44vh;
      display: grid;
      align-content: end;
      justify-items: center;
      gap: 0.75rem;
      opacity: 0.28;
      transform: translateY(0.35rem);
      transition: opacity 250ms cubic-bezier(0.16, 1, 0.3, 1), transform 250ms cubic-bezier(0.16, 1, 0.3, 1);
    }

    .projector-stage.active .caption-window {
      opacity: 1;
      transform: translateY(0);
    }

    /* Subtitle Animations */
    @keyframes fadeInUp {
      from {
        opacity: 0;
        transform: translateY(8px);
      }
      to {
        opacity: 1;
        transform: translateY(0);
      }
    }

    .animate-text {
      animation: fadeInUp 0.4s cubic-bezier(0.16, 1, 0.3, 1) forwards;
    }

    .source-line {
      max-width: 100%;
      color: rgba(247, 247, 242, 0.72);
      font-size: var(--source-size);
      font-weight: calc(var(--weight) - 200);
      line-height: 1.28;
      text-align: center;
      text-shadow:
        0 2px 16px var(--shadow),
        0 0 3px #000000;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }

    .source-line.hidden {
      display: none;
    }

    .translation-line {
      max-width: 100%;
      color: var(--ink);
      font-weight: var(--weight);
      line-height: 1.16;
      text-align: center;
      text-wrap: balance;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      text-shadow:
        0 3px 28px var(--shadow),
        0 0 6px #000000,
        0 0 2px #000000;
      -webkit-text-stroke: var(--stroke-width, 0px) #000000;
    }

    .caption-backdrop .translation-line,
    .caption-backdrop .source-line {
      padding: 0.16em 0.32em;
      background: var(--backdrop-bg, rgba(0, 0, 0, 0.48));
      border-radius: 6px;
      box-decoration-break: clone;
      -webkit-box-decoration-break: clone;
    }

    .hud {
      position: fixed;
      top: 14px;
      left: 14px;
      right: 14px;
      z-index: 5;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      color: rgba(255, 255, 255, 0.85);
      font-family: var(--mono);
      font-size: 12px;
      pointer-events: none;
    }

    .status,
    .meta {
      min-height: 30px;
      display: inline-flex;
      align-items: center;
      gap: 8px;
      padding: 0 12px;
      border: 1px solid var(--line);
      border-radius: 8px;
      background: var(--panel);
      backdrop-filter: blur(16px);
      -webkit-backdrop-filter: blur(16px);
      box-shadow: 0 4px 24px rgba(0,0,0,0.2);
    }

    .dot {
      width: 8px;
      height: 8px;
      border-radius: 999px;
      background: var(--warn);
      box-shadow: 0 0 8px var(--warn);
      transition: background 0.3s, box-shadow 0.3s;
    }

    .status.live .dot {
      background: var(--accent);
      box-shadow: 0 0 8px var(--accent);
    }

    .controls {
      position: fixed;
      left: 50%;
      bottom: 20px;
      z-index: 10;
      display: flex;
      align-items: center;
      gap: 8px;
      max-width: calc(100vw - 28px);
      padding: 6px 12px;
      border: 1px solid var(--line);
      border-radius: 12px;
      background: var(--panel);
      opacity: 0;
      transform: translate(-50%, 12px);
      transition: opacity 0.4s cubic-bezier(0.16, 1, 0.3, 1), transform 0.4s cubic-bezier(0.16, 1, 0.3, 1);
      backdrop-filter: blur(24px);
      -webkit-backdrop-filter: blur(24px);
      box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
    }

    body:hover .controls,
    .controls:focus-within {
      opacity: 1;
      transform: translate(-50%, 0);
    }

    .controls button,
    .controls a {
      min-width: 38px;
      min-height: 30px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      padding: 0 14px;
      border: 1px solid transparent;
      border-radius: 6px;
      background: transparent;
      color: rgba(255, 255, 255, 0.75);
      cursor: pointer;
      white-space: nowrap;
      font-weight: 500;
      font-size: 12px;
      transition: all 0.2s ease;
    }

    .controls button:hover,
    .controls a:hover {
      border-color: rgba(255, 255, 255, 0.15);
      background: rgba(255, 255, 255, 0.1);
      color: #ffffff;
    }

    .controls button.active {
      background: rgba(255, 255, 255, 0.15);
      color: #ffffff;
    }

    .divider {
      width: 1px;
      height: 20px;
      background: var(--line);
      margin: 0 4px;
    }

    @media (max-width: 720px) {
      .projector-stage {
        padding-left: 4vw;
        padding-right: 4vw;
      }

      .hud {
        font-size: 11px;
      }

      .meta {
        display: none;
      }

      .caption-window {
        width: 94vw;
        max-height: 52vh;
      }

      .controls {
        overflow-x: auto;
        justify-content: flex-start;
      }
    }
  </style>
</head>
<body>
  <main id="projectorStage" class="projector-stage active" data-position="bottom">
    <section id="captionWindow" class="caption-window caption-backdrop" aria-live="polite">
      <div id="sourceLine" class="source-line hidden animate-text"></div>
      <div id="translationLine" class="translation-line animate-text">Waiting for translation</div>
    </section>
  </main>

  <div class="hud" aria-hidden="true">
    <div id="status" class="status"><span class="dot"></span><span id="statusText">Connecting</span></div>
    <div id="meta" class="meta">Verbeam Projector</div>
  </div>

  <nav class="controls" aria-label="Projector controls">
    <button id="decreaseSize" type="button" title="Smaller captions">A-</button>
    <button id="increaseSize" type="button" title="Larger captions">A+</button>
    <div class="divider"></div>
    <button id="weightToggle" type="button" title="Toggle font weight">weight</button>
    <button id="outlineToggle" type="button" title="Toggle text outline">outline</button>
    <div class="divider"></div>
    <button id="positionButton" type="button" title="Move captions">bottom</button>
    <button id="sourceToggle" type="button" title="Toggle source text">source off</button>
    <button id="backdropToggle" type="button" title="Toggle caption backdrop">backdrop on</button>
    <button id="clearButton" type="button" title="Clear caption">clear</button>
    <a href="/app" title="Open workbench">app</a>
  </nav>

  <script>
    const $ = id => document.getElementById(id);
    const stage = $("projectorStage");
    const captionWindow = $("captionWindow");
    const sourceLine = $("sourceLine");
    const translationLine = $("translationLine");
    const status = $("status");
    const statusText = $("statusText");
    const meta = $("meta");
    const positions = ["bottom", "center", "top"];
    const weights = [400, 500, 700, 900];
    let fadeTimer = null;
    let reconnectTimer = null;
    let uiIdleTimer = null;

    const settings = {
      scale: readNumber("scale", 1),
      position: readChoice("position", positions, "bottom"),
      showSource: readBool("source", false),
      backdrop: readBool("backdrop", true),
      outline: readBool("outline", false),
      weight: readNumber("weight", 700),
      fadeSeconds: readNumber("fade", 12),
      font: readChoice("font", ["outfit", "jetbrains mono", "sans-serif", "serif", "monospace"], "outfit"),
      color: paramValue("color") ?? readStorage("color") ?? "#f7f7f2",
      bgColor: paramValue("bgcolor") ?? readStorage("bgcolor") ?? "rgba(0, 0, 0, 0.48)",
      align: readChoice("align", ["left", "center", "right"], "center"),
      shadow: readBool("shadow", true)
    };

    const state = {
      lastKey: ""
    };

    function resetIdleTimer() {
      document.body.classList.remove("idle");
      clearTimeout(uiIdleTimer);
      uiIdleTimer = setTimeout(() => {
        document.body.classList.add("idle");
      }, 3500);
    }

    window.addEventListener("mousemove", resetIdleTimer);
    window.addEventListener("keydown", resetIdleTimer);
    window.addEventListener("touchstart", resetIdleTimer);
    resetIdleTimer();

    function readStorage(key) {
      try {
        return window.localStorage.getItem(`verbeam.projector.${key}`);
      } catch {
        return null;
      }
    }

    function writeStorage(key, value) {
      try {
        window.localStorage.setItem(`verbeam.projector.${key}`, String(value));
      } catch {
      }
    }

    function paramValue(name) {
      return new URLSearchParams(location.search).get(name);
    }

    function readNumber(name, fallback) {
      const value = Number.parseFloat(paramValue(name) ?? readStorage(name) ?? "");
      return Number.isFinite(value) ? value : fallback;
    }

    function readChoice(name, choices, fallback) {
      const value = (paramValue(name) ?? readStorage(name) ?? fallback).toString().toLowerCase();
      return choices.includes(value) || choices.includes(Number(value)) ? (isNaN(value) ? value : Number(value)) : fallback;
    }

    function readBool(name, fallback) {
      const value = (paramValue(name) ?? readStorage(name) ?? "").toLowerCase();
      if (["1", "true", "yes", "on"].includes(value)) {
        return true;
      }

      if (["0", "false", "no", "off"].includes(value)) {
        return false;
      }

      return fallback;
    }

    function setStatus(label, isLive) {
      statusText.textContent = label;
      status.classList.toggle("live", isLive);
    }

    function applySettings() {
      settings.scale = Math.max(0.65, Math.min(1.7, settings.scale));
      settings.fadeSeconds = Math.max(2, Math.min(60, settings.fadeSeconds));
      if(!weights.includes(settings.weight)) settings.weight = 700;

      stage.dataset.position = settings.position;
      captionWindow.classList.toggle("caption-backdrop", settings.backdrop);
      sourceLine.classList.toggle("hidden", !settings.showSource || !sourceLine.textContent.trim());

      $("positionButton").textContent = settings.position;
      $("sourceToggle").textContent = settings.showSource ? "source on" : "source off";
      $("sourceToggle").classList.toggle("active", settings.showSource);
      $("backdropToggle").textContent = settings.backdrop ? "backdrop on" : "backdrop off";
      $("backdropToggle").classList.toggle("active", settings.backdrop);
      $("outlineToggle").textContent = settings.outline ? "outline on" : "outline off";
      $("outlineToggle").classList.toggle("active", settings.outline);
      $("weightToggle").textContent = `w${settings.weight}`;

      // Dynamic styles from parameters
      let fontStack = "var(--font)";
      if (settings.font === "jetbrains mono") fontStack = "var(--mono)";
      else if (settings.font === "sans-serif") fontStack = "sans-serif";
      else if (settings.font === "monospace") fontStack = "monospace";

      translationLine.style.fontFamily = fontStack;
      sourceLine.style.fontFamily = fontStack;

      document.documentElement.style.setProperty("--weight", settings.weight);
      document.documentElement.style.setProperty("--stroke-width", settings.outline ? "2.5px" : "0px");

      translationLine.style.color = settings.color;
      document.documentElement.style.setProperty("--backdrop-bg", settings.bgColor);

      captionWindow.style.justifyItems = settings.align === "left" ? "flex-start" : (settings.align === "right" ? "flex-end" : "center");
      translationLine.style.textAlign = settings.align;
      sourceLine.style.textAlign = settings.align;

      const shadowStyle = settings.shadow
        ? "0 3px 28px var(--shadow), 0 0 6px #000000, 0 0 2px #000000"
        : "none";
      translationLine.style.textShadow = shadowStyle;
      sourceLine.style.textShadow = shadowStyle;

      fitCaption();
    }

    function captionKey(message) {
      return message.id || message.stableKey || [
        message.createdAt || "",
        message.sourceKind || "",
        message.sourceText || "",
        message.translatedText || ""
      ].join("|");
    }

    function displayMessage(message) {
      if (!message || message.type !== "translation") {
        return;
      }

      const text = (message.translatedText || message.sourceText || "").trim();
      if (!text) {
        return;
      }

      const key = captionKey(message);
      if (key && key === state.lastKey) {
        return;
      }

      state.lastKey = key;
      translationLine.textContent = text;
      sourceLine.textContent = message.sourceText || "";
      sourceLine.classList.toggle("hidden", !settings.showSource || !sourceLine.textContent.trim());
      meta.textContent = `${message.source || "-"} to ${message.target || "-"} / ${message.sourceKind || "text"}`;
      stage.classList.add("active");
      window.clearTimeout(fadeTimer);

      // Re-trigger animation
      translationLine.classList.remove("animate-text");
      sourceLine.classList.remove("animate-text");
      void translationLine.offsetWidth; // Force reflow
      translationLine.classList.add("animate-text");
      sourceLine.classList.add("animate-text");

      let delay = settings.fadeSeconds * 1000;
      if (message.displayUntil) {
        const displayUntil = Date.parse(message.displayUntil);
        if (Number.isFinite(displayUntil)) {
          delay = Math.max(1500, displayUntil - Date.now());
        }
      }

      fadeTimer = window.setTimeout(() => stage.classList.remove("active"), delay);
      window.requestAnimationFrame(fitCaption);
    }

    function clearCaption() {
      state.lastKey = "";
      sourceLine.textContent = "";
      translationLine.textContent = "Waiting for translation";
      sourceLine.classList.add("hidden");
      meta.textContent = "Verbeam Projector";
      stage.classList.remove("active");
      window.clearTimeout(fadeTimer);
    }

    function fitCaption() {
      const maxSize = Math.max(32, Math.min(window.innerWidth * 0.072, 112) * settings.scale);
      const minSize = Math.max(24, Math.min(window.innerWidth * 0.038, 44));
      let size = maxSize;
      translationLine.style.fontSize = `${size}px`;
      sourceLine.style.fontSize = `${Math.max(18, size * 0.34)}px`;

      for (let step = 0; step < 36; step += 1) {
        const overflowY = captionWindow.scrollHeight > captionWindow.clientHeight + 1;
        const overflowX = captionWindow.scrollWidth > captionWindow.clientWidth + 1;
        if ((!overflowY && !overflowX) || size <= minSize) {
          break;
        }

        size -= 2;
        translationLine.style.fontSize = `${size}px`;
        sourceLine.style.fontSize = `${Math.max(18, size * 0.34)}px`;
      }
    }

    async function fetchLatest() {
      try {
        const response = await fetch("/broadcast/latest", { cache: "no-store" });
        if (response.status === 204) {
          return;
        }

        if (response.ok) {
          displayMessage(await response.json());
        }
      } catch {
      }
    }

    function connect() {
      window.clearTimeout(reconnectTimer);
      const protocol = location.protocol === "https:" ? "wss:" : "ws:";
      const socket = new WebSocket(`${protocol}//${location.host}/broadcast`);
      setStatus("Connecting", false);

      socket.addEventListener("open", () => {
        setStatus("Live", true);
        fetchLatest();
      });

      socket.addEventListener("message", event => {
        try {
          displayMessage(JSON.parse(event.data));
        } catch {
        }
      });

      socket.addEventListener("close", () => {
        setStatus("Reconnecting", false);
        reconnectTimer = window.setTimeout(connect, 1500);
      });

      socket.addEventListener("error", () => socket.close());
    }

    $("increaseSize").addEventListener("click", () => {
      settings.scale += 0.1;
      writeStorage("scale", settings.scale.toFixed(2));
      applySettings();
    });

    $("decreaseSize").addEventListener("click", () => {
      settings.scale -= 0.1;
      writeStorage("scale", settings.scale.toFixed(2));
      applySettings();
    });

    $("weightToggle").addEventListener("click", () => {
      const idx = weights.indexOf(settings.weight);
      settings.weight = weights[(idx + 1) % weights.length];
      writeStorage("weight", settings.weight);
      applySettings();
    });

    $("outlineToggle").addEventListener("click", () => {
      settings.outline = !settings.outline;
      writeStorage("outline", settings.outline ? "1" : "0");
      applySettings();
    });

    $("positionButton").addEventListener("click", () => {
      const index = positions.indexOf(settings.position);
      settings.position = positions[(index + 1) % positions.length];
      writeStorage("position", settings.position);
      applySettings();
    });

    $("sourceToggle").addEventListener("click", () => {
      settings.showSource = !settings.showSource;
      writeStorage("source", settings.showSource ? "1" : "0");
      applySettings();
    });

    $("backdropToggle").addEventListener("click", () => {
      settings.backdrop = !settings.backdrop;
      writeStorage("backdrop", settings.backdrop ? "1" : "0");
      applySettings();
    });

    $("clearButton").addEventListener("click", clearCaption);
    window.addEventListener("resize", fitCaption);
    applySettings();
    fetchLatest();
    connect();
  </script>
</body>
</html>
""";
}
