namespace LocalTranslateHub.Api.Broadcast;

public static class BroadcastViewerPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>LocalTranslateHub Viewer</title>
  <style>
    :root {
      color-scheme: dark;
      --background: #12120f;
      --surface: #1e1e18;
      --surface-strong: #2a2a21;
      --text: #f7f4ea;
      --muted: #b9b39f;
      --accent: #72d67c;
      --warning: #f0c95a;
    }

    * {
      box-sizing: border-box;
    }

    body {
      min-height: 100vh;
      margin: 0;
      background: var(--background);
      color: var(--text);
      font-family: "Segoe UI", system-ui, sans-serif;
      letter-spacing: 0;
    }

    main {
      min-height: 100vh;
      display: grid;
      grid-template-rows: auto 1fr auto;
      gap: 1rem;
      padding: 1rem;
    }

    header,
    footer {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 1rem;
      color: var(--muted);
      font-size: 0.9rem;
    }

    .brand {
      color: var(--text);
      font-weight: 700;
    }

    .status {
      display: inline-flex;
      align-items: center;
      gap: 0.5rem;
      min-height: 2rem;
      padding: 0 0.75rem;
      border: 1px solid var(--surface-strong);
      border-radius: 999px;
      background: var(--surface);
    }

    .dot {
      width: 0.65rem;
      height: 0.65rem;
      border-radius: 999px;
      background: var(--warning);
    }

    .status.live .dot {
      background: var(--accent);
    }

    .translation {
      display: grid;
      place-items: center;
      min-height: 18rem;
      padding: 1.25rem;
      border: 1px solid var(--surface-strong);
      border-radius: 8px;
      background: var(--surface);
      font-size: 2rem;
      line-height: 1.35;
      text-align: center;
      white-space: pre-wrap;
      word-break: break-word;
    }

    .source {
      min-height: 4rem;
      padding: 1rem;
      border-left: 3px solid var(--surface-strong);
      color: var(--muted);
      line-height: 1.5;
      white-space: pre-wrap;
      word-break: break-word;
    }

    @media (min-width: 720px) {
      main {
        padding: 1.5rem;
      }

      .translation {
        font-size: 2.8rem;
      }
    }
  </style>
  <style>
    :root {
      --oc-bg: #0f0f0f;
      --oc-titlebar: #242424;
      --oc-layer: #171717;
      --oc-layer-2: #202020;
      --oc-line: #2d2d2d;
      --oc-text: #dddddd;
      --oc-muted: #8b8b8b;
      --oc-blue: #2f6fff;
      --oc-green: #72d67c;
      --oc-amber: #d6b86a;
      --background: var(--oc-bg);
      --surface: var(--oc-layer);
      --surface-strong: var(--oc-line);
      --text: var(--oc-text);
      --muted: var(--oc-muted);
      --accent: var(--oc-green);
      --warning: var(--oc-amber);
    }

    body {
      background: var(--oc-bg);
      color: var(--oc-text);
      font-size: 13px;
    }

    main {
      grid-template-rows: 32px minmax(0, 1fr) auto;
      gap: 0;
      padding: 0;
      background: #111111;
    }

    header {
      min-height: 32px;
      padding: 0 12px;
      border-bottom: 1px solid #1f1f1f;
      background: var(--oc-titlebar);
      color: var(--oc-muted);
      font-family: "Cascadia Mono", Consolas, monospace;
      font-size: 11px;
    }

    footer {
      align-items: stretch;
      gap: 12px;
      padding: 12px;
      border-top: 1px solid var(--oc-line);
      background: #121212;
    }

    .brand {
      color: #f1f1f1;
      font-family: "Cascadia Mono", Consolas, monospace;
      font-size: 12px;
      font-weight: 650;
    }

    .status {
      min-height: 22px;
      padding: 0 8px;
      border-color: var(--oc-line);
      border-radius: 5px;
      background: var(--oc-layer);
      color: var(--oc-muted);
    }

    .dot {
      width: 7px;
      height: 7px;
    }

    .translation {
      place-self: stretch;
      margin: 30px;
      min-height: 18rem;
      border-color: var(--oc-line);
      border-radius: 8px;
      background: var(--oc-layer);
      color: var(--oc-text);
      font-size: clamp(1.6rem, 4vw, 3.2rem);
      box-shadow: none;
    }

    .source {
      flex: 1;
      min-height: 4rem;
      border: 1px solid var(--oc-line);
      border-left: 3px solid var(--oc-blue);
      border-radius: 8px;
      background: var(--oc-layer);
      color: var(--oc-muted);
    }

    #meta {
      align-self: stretch;
      display: grid;
      place-items: center;
      min-width: 150px;
      border: 1px solid var(--oc-line);
      border-radius: 8px;
      background: var(--oc-layer);
      color: var(--oc-muted);
      font-family: "Cascadia Mono", Consolas, monospace;
      font-size: 12px;
    }

    @media (max-width: 720px) {
      .translation {
        margin: 14px;
      }

      footer {
        display: grid;
      }

      #meta {
        min-height: 40px;
      }
    }
  </style>
</head>
<body>
  <main>
    <header>
      <div class="brand">LocalTranslateHub Viewer</div>
      <div id="status" class="status"><span class="dot"></span><span id="statusText">Connecting</span></div>
    </header>

    <section id="translation" class="translation">Waiting for translation</section>

    <footer>
      <div id="source" class="source">OCR text will appear here.</div>
      <div id="meta"></div>
    </footer>
  </main>

  <script>
    const status = document.getElementById("status");
    const statusText = document.getElementById("statusText");
    const translation = document.getElementById("translation");
    const source = document.getElementById("source");
    const meta = document.getElementById("meta");

    function setStatus(label, isLive) {
      statusText.textContent = label;
      status.classList.toggle("live", isLive);
    }

    function connect() {
      const protocol = location.protocol === "https:" ? "wss:" : "ws:";
      const socket = new WebSocket(`${protocol}//${location.host}/broadcast`);
      setStatus("Connecting", false);

      socket.addEventListener("open", () => setStatus("Live", true));
      socket.addEventListener("message", event => {
        const message = JSON.parse(event.data);
        if (message.type !== "translation") {
          return;
        }

        translation.textContent = message.translatedText || "";
        source.textContent = message.sourceText || "";
        meta.textContent = `${message.source} to ${message.target}`;
      });
      socket.addEventListener("close", () => {
        setStatus("Reconnecting", false);
        window.setTimeout(connect, 1500);
      });
      socket.addEventListener("error", () => socket.close());
    }

    connect();
  </script>
</body>
</html>
""";
}
