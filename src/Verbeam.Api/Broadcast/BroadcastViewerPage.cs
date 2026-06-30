namespace Verbeam.Api.Broadcast;

public static class BroadcastViewerPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Verbeam Viewer</title>
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link href="https://fonts.googleapis.com/css2?family=Outfit:wght@300;400;500;600;700&family=JetBrains+Mono:wght@400;500;700&display=swap" rel="stylesheet">
  <link href="https://cdn.jsdelivr.net/npm/remixicon@4.2.0/fonts/remixicon.css" rel="stylesheet" />
  <style>
    :root {
      color-scheme: dark;
      --bg: #090909;
      --layer: #121212;
      --layer-2: #1c1c1c;
      --line: #1f1f1f;
      --line-strong: #2d2d2d;
      --text: #e8e8e8;
      --muted: #7a7a7a;
      --blue: #3b82f6;
      --green: #22c55e;
      --amber: #d6b86a;
      --sans: "Outfit", system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
      --mono: "JetBrains Mono", "SF Mono", Consolas, monospace;
    }

    ::-webkit-scrollbar {
      display: none;
    }
    * {
      scrollbar-width: none;
      -ms-overflow-style: none;
      box-sizing: border-box;
    }

    body {
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font-family: var(--sans);
      font-size: 13.5px;
      line-height: 1.5;
    }

    main {
      min-height: 100vh;
      display: grid;
      grid-template-rows: 44px minmax(0, 1fr) auto;
      gap: 0;
      padding: 0;
      background: var(--bg);
    }

    header {
      min-height: 44px;
      padding: 0 16px;
      border-bottom: 1px solid var(--line);
      background: var(--layer);
      color: var(--muted);
      font-size: 13px;
      font-weight: 500;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 1rem;
    }

    footer {
      display: flex;
      align-items: stretch;
      gap: 16px;
      padding: 16px;
      border-top: 1px solid var(--line);
      background: var(--layer);
    }

    .brand {
      color: #ffffff;
      font-size: 14px;
      font-weight: 700;
      letter-spacing: 0.02em;
    }

    .status {
      min-height: 26px;
      padding: 0 10px;
      border: 1px solid var(--line);
      border-radius: 99px;
      background: rgba(255, 255, 255, 0.03);
      color: var(--muted);
      font-size: 12px;
      font-weight: 500;
      transition: all 0.3s ease;
      display: inline-flex;
      align-items: center;
      gap: 0.5rem;
    }

    .dot {
      width: 7px;
      height: 7px;
      border-radius: 999px;
      background: var(--amber);
    }

    .status.live .dot {
      background: var(--green);
    }

    .translation {
      place-self: center;
      width: min(90vw, 1200px);
      margin: 40px auto;
      min-height: 22rem;
      border: 1px solid var(--line);
      border-radius: 12px;
      background: var(--layer);
      color: var(--text);
      font-size: clamp(1.8rem, 5vw, 3.4rem);
      font-weight: 600;
      box-shadow: none;
      transition: all 0.3s ease;
      display: grid;
      place-items: center;
      padding: 40px 30px;
      text-shadow: none;
      text-align: center;
      white-space: pre-wrap;
      word-break: break-word;
    }

    .source {
      flex: 1;
      min-height: 4.5rem;
      border: 1px solid var(--line);
      border-left: 4px solid var(--blue);
      border-radius: 6px;
      background: var(--layer-2);
      color: #94a3b8;
      font-size: 14px;
      padding: 12px 16px;
      line-height: 1.5;
      white-space: pre-wrap;
      word-break: break-word;
    }

    #meta {
      align-self: stretch;
      display: grid;
      place-items: center;
      min-width: 180px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: var(--layer-2);
      color: #94a3b8;
      font-family: var(--mono);
      font-size: 12px;
      font-weight: 500;
    }

    @media (max-width: 720px) {
      .translation {
        margin: 20px;
        min-height: 14rem;
        padding: 24px;
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
      <div class="brand">Verbeam Viewer</div>
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

    let lastCaptionKey = "";

    function captionKey(message) {
      return message.id || message.stableKey || [
        message.createdAt || "",
        message.sourceKind || "",
        message.sourceText || "",
        message.translatedText || ""
      ].join("|");
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

        const key = captionKey(message);
        if (key && key === lastCaptionKey) {
          return;
        }

        lastCaptionKey = key;
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
