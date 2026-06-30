import http from "node:http";

const CDP_HOST = process.env.CDP_HOST || "127.0.0.1";
const CDP_PORT = Number(process.env.CDP_PORT || 9222);
const BACKGROUND_URL_SUFFIX = "/background.js";

function httpJson(method, path) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: CDP_HOST, port: CDP_PORT, method, path }, (res) => {
      let body = "";
      res.setEncoding("utf8");
      res.on("data", (chunk) => (body += chunk));
      res.on("end", () => {
        try {
          resolve(JSON.parse(body));
        }
        catch {
          reject(new Error(`${method} ${path} returned non-JSON: ${body.slice(0, 160)}`));
        }
      });
    });
    req.on("error", reject);
    req.end();
  });
}

function connect(wsUrl) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    ws.onopen = () => resolve(ws);
    ws.onerror = reject;
  });
}

function send(ws, method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = Math.floor(Math.random() * 1e9);
    const onMessage = (event) => {
      const msg = JSON.parse(event.data);
      if (msg.id !== id) {
        return;
      }
      ws.removeEventListener("message", onMessage);
      if (msg.error) {
        reject(new Error(msg.error.message));
      }
      else {
        resolve(msg.result);
      }
    };
    ws.addEventListener("message", onMessage);
    ws.send(JSON.stringify({ id, method, params }));
  });
}

async function main() {
  const targets = await httpJson("GET", "/json");
  if (!Array.isArray(targets) || targets.length === 0) {
    throw new Error(`Cannot reach Chrome DevTools at http://${CDP_HOST}:${CDP_PORT}. Start Chrome with --remote-debugging-port=${CDP_PORT}.`);
  }

  const backgroundTarget = targets.find((target) =>
    typeof target.url === "string" &&
    target.url.startsWith("chrome-extension://") &&
    target.url.endsWith(BACKGROUND_URL_SUFFIX)
  );

  if (!backgroundTarget) {
    const extensions = targets
      .filter((target) => typeof target.url === "string" && target.url.startsWith("chrome-extension://"))
      .map((target) => target.url);
    throw new Error(`No Verbeam background target found. Loaded extension targets: ${extensions.length > 0 ? extensions.join(", ") : "none"}.`);
  }

  const ws = await connect(backgroundTarget.webSocketDebuggerUrl);
  await send(ws, "Runtime.evaluate", {
    expression: "chrome.runtime.reload()",
    awaitPromise: false
  });
  ws.close();

  const extensionId = backgroundTarget.url.split("/")[2];
  console.log(`Reloaded extension ${extensionId}`);
}

main().catch((error) => {
  console.error(error.message);
  process.exit(1);
});
