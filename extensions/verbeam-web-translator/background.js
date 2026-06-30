const DEFAULT_BACKEND_URL = "http://127.0.0.1:5768";
const EXTENSION_BUILD = "2026-06-21-fetch-recover";
const BATCH_SEPARATOR = "%%";
const BATCH_SEPARATOR_PATTERN = /\r?\n[ \t]*%%[ \t]*\r?\n/;
const BATCH_DELAY_MS = 100;
const MAX_BATCH_ITEMS = 2;
const MAX_BATCH_CHARS = 1600;
const SETTINGS_VERSION = 5;
const BACKEND_PROBE_TIMEOUT_MS = 4000;
const BACKEND_RECOVERY_CONCURRENCY = 8;
const BACKEND_RECOVERY_CANDIDATE_URLS = [
  "http://127.0.0.1:5768",
  "http://localhost:5768",
  "http://127.0.0.1:5758",
  "http://localhost:5758",
  "http://127.0.0.1:5757",
  "http://localhost:5757",
  "http://127.0.0.1:5069",
  "http://localhost:5069"
];
const BACKEND_RECOVERY_PORT_RANGES = [[5757, 5772]];
const LOCAL_PROVIDER_NAMES = new Set(["", "llama-cpp", "ollama", "hybrid"]);
const BATCH_CONTEXT = [
  "Batch translation instructions:",
  "The text may contain a standalone line with only %%.",
  "Translate each segment independently.",
  "Return exactly one translated segment for each input segment.",
  "Put a standalone %% line between translated segments.",
  "Do not add explanations, numbering, labels, or markdown fences."
].join("\n");

const pendingBatches = new Map();
const duplicateRequests = new Map();
const activeRequestControllers = new Map();
let batchTimer = null;

chrome.runtime.onInstalled.addListener(() => {
  chrome.storage.local.get("settings", (items) => {
    if (items.settings) {
      const settings = migrateSettings(items.settings);
      if (settings !== items.settings) {
        chrome.storage.local.set({ settings });
      }
      return;
    }

    chrome.storage.local.set({
      settings: {
        settingsVersion: SETTINGS_VERSION,
        backendUrl: DEFAULT_BACKEND_URL,
        source: "auto",
        target: "zh-TW",
        provider: "",
        model: "",
        mode: "web_article",
        profile: "web-page",
        displayMode: "bilingual",
        contextMode: "fast",
        watchAjax: true,
        concurrency: 4,
        floatingButtonEnabled: true,
        floatingButtonSide: "right",
        floatingButtonPosition: 0.45,
        floatingButtonLocked: false,
        disabledSitePatterns: []
      }
    });
  });
});

function migrateSettings(settings) {
  const previousVersion = Number(settings.settingsVersion) || 1;
  if (previousVersion >= SETTINGS_VERSION) {
    return settings;
  }

  return {
    ...settings,
    settingsVersion: SETTINGS_VERSION,
    concurrency: previousVersion < 5 && Number(settings.concurrency) === 2 ? 4 : settings.concurrency,
    floatingButtonEnabled: settings.floatingButtonEnabled !== false,
    floatingButtonSide: settings.floatingButtonSide === "left" ? "left" : "right",
    floatingButtonPosition: Number.isFinite(Number(settings.floatingButtonPosition))
      ? Math.max(0.08, Math.min(0.86, Number(settings.floatingButtonPosition)))
      : 0.45,
    floatingButtonLocked: Boolean(settings.floatingButtonLocked),
    disabledSitePatterns: Array.isArray(settings.disabledSitePatterns)
      ? settings.disabledSitePatterns.map((item) => String(item || "").trim()).filter(Boolean)
      : []
  };
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || typeof message.type !== "string") {
    return false;
  }

  if (message.type === "verbeam:cancelTranslateText") {
    cancelTranslateText(message.requestId || message.payload?.requestId);
    sendResponse({ ok: true });
    return false;
  }

  if (message.type === "verbeam:probeBackend") {
    probeBackendUrl(message.payload?.backendUrl)
      .then((result) => sendResponse({ ok: true, data: result || { ok: false } }))
      .catch((error) => sendResponse({
        ok: false,
        error: error instanceof Error ? error.message : String(error)
      }));

    return true;
  }

  if (message.type === "verbeam:fetchBackendJson") {
    fetchBackendJson(message.payload?.backendUrl, message.payload?.path)
      .then((data) => sendResponse({ ok: true, data }))
      .catch((error) => sendResponse({
        ok: false,
        error: error instanceof Error ? error.message : String(error)
      }));

    return true;
  }

  if (message.type === "verbeam:rememberBackend") {
    rememberBackend(message.payload)
      .then((settings) => sendResponse({ ok: true, data: settings }))
      .catch((error) => sendResponse({
        ok: false,
        error: error instanceof Error ? error.message : String(error)
      }));

    return true;
  }

  if (message.type === "verbeam:translateText") {
    enqueueTranslateText(message.payload)
      .then((data) => sendResponse({ ok: true, data }))
      .catch((error) => sendResponse({
        ok: false,
        error: error instanceof Error ? error.message : String(error),
        errorName: error instanceof Error ? error.name : undefined
      }));

    return true;
  }

  return false;
});

async function probeBackendUrl(rawUrl) {
  const url = normalizeBackendBaseUrl(rawUrl);
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), BACKEND_PROBE_TIMEOUT_MS);
  try {
    const endpoint = new URL("/health", url);
    const response = await fetch(endpoint.toString(), {
      cache: "no-store",
      signal: controller.signal
    });
    if (!response.ok) {
      return null;
    }

    const text = await response.text();
    const health = text ? JSON.parse(text) : null;
    if (!isVerbeamHealth(health)) {
      return null;
    }

    return { ok: true, url, health };
  }
  catch {
    return null;
  }
  finally {
    clearTimeout(timer);
  }
}

async function fetchBackendJson(rawUrl, path) {
  const endpoint = buildBackendEndpoint(rawUrl, path);
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), BACKEND_PROBE_TIMEOUT_MS);
  try {
    const response = await fetch(endpoint, {
      cache: "no-store",
      signal: controller.signal
    });
    const text = await response.text();
    if (!response.ok) {
      throw new Error(`Backend ${response.status}: ${trimForError(text || response.statusText)}`);
    }
    return text ? JSON.parse(text) : null;
  }
  finally {
    clearTimeout(timer);
  }
}

function buildBackendEndpoint(rawUrl, path) {
  const base = new URL(normalizeBackendBaseUrl(rawUrl));
  const [pathname, search = ""] = String(path || "/").split("?", 2);
  base.pathname = pathname && pathname.startsWith("/") ? pathname : "/";
  base.search = search ? `?${search}` : "";
  base.hash = "";
  return base.toString();
}

function isVerbeamHealth(health) {
  if (!health || (health.status !== "ok" && !health.defaultProvider)) {
    return false;
  }

  return Boolean(
    health.llamaCpp ||
    health.ollama ||
    health.ocr ||
    health.speech ||
    health.paths?.presets ||
    health.counts?.providers
  );
}

function rememberBackend(payload) {
  const backendUrl = normalizeBackendBaseUrl(payload?.backendUrl);
  return new Promise((resolve, reject) => {
    chrome.storage.local.get("settings", (items) => {
      const runtimeError = chrome.runtime.lastError;
      if (runtimeError) {
        reject(new Error(runtimeError.message));
        return;
      }

      const settings = migrateSettings({
        ...DEFAULT_EXTENSION_SETTINGS(),
        ...(items.settings || {}),
        settingsVersion: SETTINGS_VERSION,
        backendUrl
      });
      chrome.storage.local.set({ settings }, () => {
        const setError = chrome.runtime.lastError;
        if (setError) {
          reject(new Error(setError.message));
          return;
        }
        resolve(settings);
      });
    });
  });
}

function DEFAULT_EXTENSION_SETTINGS() {
  return {
    settingsVersion: SETTINGS_VERSION,
    backendUrl: DEFAULT_BACKEND_URL,
    source: "auto",
    target: "zh-TW",
    provider: "",
    model: "",
    mode: "web_article",
    profile: "web-page",
    displayMode: "bilingual",
    contextMode: "fast",
    watchAjax: true,
    concurrency: 4,
    floatingButtonEnabled: true,
    floatingButtonSide: "right",
    floatingButtonPosition: 0.45,
    floatingButtonLocked: false,
    disabledSitePatterns: []
  };
}

function cancelTranslateText(requestId) {
  const id = String(requestId || "");
  if (!id) {
    return;
  }

  const controller = activeRequestControllers.get(id);
  if (controller) {
    controller.abort();
    activeRequestControllers.delete(id);
  }

  for (const [key, batch] of Array.from(pendingBatches.entries())) {
    const remainingTasks = [];
    let removedCharacters = 0;
    for (const task of batch.tasks) {
      if (task.payload.requestId === id) {
        removedCharacters += task.characters;
        task.reject(createAbortError());
      }
      else {
        remainingTasks.push(task);
      }
    }

    if (remainingTasks.length === batch.tasks.length) {
      continue;
    }

    if (remainingTasks.length === 0) {
      pendingBatches.delete(key);
      continue;
    }

    batch.tasks = remainingTasks;
    batch.characters = Math.max(0, batch.characters - removedCharacters);
  }
}

function enqueueTranslateText(payload) {
  const normalized = normalizePayload(payload);
  const requestKey = translationRequestKey(normalized);
  const duplicate = duplicateRequests.get(requestKey);
  if (duplicate) {
    return duplicate;
  }

  const promise = shouldBatchPayload(normalized)
    ? enqueueBatchablePayload(normalized)
    : translateText(normalized);

  duplicateRequests.set(requestKey, promise);
  promise.then(
    () => duplicateRequests.delete(requestKey),
    () => duplicateRequests.delete(requestKey)
  );
  return promise;
}

function enqueueBatchablePayload(payload) {
  return new Promise((resolve, reject) => {
    const task = {
      payload,
      resolve,
      reject,
      characters: String(payload.text || "").length
    };
    const key = batchKey(payload);
    let batch = pendingBatches.get(key);
    if (!batch) {
      batch = {
        key,
        tasks: [],
        characters: 0,
        createdAt: Date.now()
      };
      pendingBatches.set(key, batch);
    }

    if (batch.tasks.length > 0 && batch.characters + task.characters > MAX_BATCH_CHARS) {
      flushBatch(key);
      batch = {
        key,
        tasks: [],
        characters: 0,
        createdAt: Date.now()
      };
      pendingBatches.set(key, batch);
    }

    batch.tasks.push(task);
    batch.characters += task.characters;

    if (batch.tasks.length >= MAX_BATCH_ITEMS || batch.characters >= MAX_BATCH_CHARS) {
      flushBatch(key);
    }
    else {
      scheduleBatchFlush();
    }
  });
}

function scheduleBatchFlush() {
  if (batchTimer) {
    return;
  }

  batchTimer = setTimeout(() => {
    batchTimer = null;
    const now = Date.now();
    for (const [key, batch] of Array.from(pendingBatches.entries())) {
      if (now - batch.createdAt >= BATCH_DELAY_MS) {
        flushBatch(key);
      }
    }
    if (pendingBatches.size > 0) {
      scheduleBatchFlush();
    }
  }, BATCH_DELAY_MS);
}

function flushBatch(key) {
  const batch = pendingBatches.get(key);
  if (!batch) {
    return;
  }

  pendingBatches.delete(key);
  const tasks = batch.tasks;
  if (tasks.length === 0) {
    return;
  }

  if (tasks.length === 1) {
    translateText(tasks[0].payload)
      .then(tasks[0].resolve, tasks[0].reject);
    return;
  }

  executeBatch(tasks)
    .then((responses) => {
      responses.forEach((response, index) => tasks[index].resolve(response));
    })
    .catch((error) => {
      console.warn("[Verbeam] batch failed, falling back to individual requests:", error);
      Promise.allSettled(tasks.map(async (task) => {
        try {
          task.resolve(await translateText(task.payload));
        }
        catch (individualError) {
          task.reject(individualError);
        }
      }));
    });
}

async function executeBatch(tasks) {
  const basePayload = tasks[0].payload;
  const batchText = tasks
    .map((task) => String(task.payload.text || "").trim())
    .join(`\n\n${BATCH_SEPARATOR}\n\n`);
  const batchPayload = {
    ...basePayload,
    requestId: "",
    name: "verbeam-web-batch",
    text: batchText,
    context: joinContext(BATCH_CONTEXT, basePayload.context),
    contextItems: basePayload.contextItems
  };

  console.log("[Verbeam] batch translate request:", tasks.length, "items", batchText.length, "chars");
  const response = await translateText(batchPayload);
  const parts = parseBatchResult(response?.result);
  if (parts.length !== tasks.length) {
    throw new Error(`Batch result count mismatch: expected ${tasks.length}, got ${parts.length}.`);
  }

  return parts.map((result, index) => ({
    ...response,
    result,
    batchCount: tasks.length,
    batchIndex: index
  }));
}

async function translateText(payload) {
  const normalized = normalizePayload(payload);
  const backgroundReceivedAtUnixMs = Date.now();
  let endpoint = buildTranslateEndpoint(normalized.backendUrl);
  console.log("[Verbeam] translate request:", EXTENSION_BUILD, endpoint, normalized.text?.slice(0, 80));

  const request = {
    name: normalized.name || "verbeam-web",
    text: normalized.text || "",
    source: normalized.source || undefined,
    target: normalized.target || undefined,
    provider: normalized.provider || undefined,
    model: normalized.model || undefined,
    mode: normalized.mode || "web_article",
    profile: normalized.profile || "web-page",
    sessionId: normalized.sessionId || undefined,
    context: normalized.context || undefined,
    contextItems: normalized.contextItems || undefined,
    skipMemoryContext: normalized.skipMemoryContext || undefined,
    langConfig: {
      sourceCode: normalized.source || "auto",
      targetCode: normalized.target || "zh-TW",
      level: normalized.level || undefined
    },
    webTitle: normalized.webTitle || undefined,
    webSummary: normalized.webSummary || undefined,
    webContent: normalized.webContent || undefined,
    traceId: normalized.traceId || undefined,
    itemId: normalized.itemId || undefined,
    chunkId: normalized.chunkId || undefined,
    clientQueuedAtUnixMs: normalized.clientQueuedAtUnixMs || undefined,
    clientRequestStartedAtUnixMs: normalized.clientRequestStartedAtUnixMs || undefined,
    backgroundReceivedAtUnixMs,
    backgroundFetchStartedAtUnixMs: undefined
  };

  let response;
  const controller = normalized.requestId ? new AbortController() : null;
  if (controller) {
    activeRequestControllers.set(normalized.requestId, controller);
  }
  let fetchStartedAtUnixMs = Date.now();
  request.backgroundFetchStartedAtUnixMs = fetchStartedAtUnixMs;
  let fetchStartedAtMs = performance.now();
  try {
    response = await postTranslateRequest(endpoint, request, controller?.signal);
  }
  catch (fetchError) {
    if (fetchError?.name === "AbortError") {
      throw createAbortError();
    }
    console.error("[Verbeam] fetch failed:", endpoint, fetchError);

    const recoveredBackend = await recoverBackendAfterFetchFailure(normalized.backendUrl);
    if (!recoveredBackend) {
      throw createFetchError(endpoint, fetchError);
    }

    normalized.backendUrl = recoveredBackend;
    endpoint = buildTranslateEndpoint(normalized.backendUrl);
    fetchStartedAtUnixMs = Date.now();
    request.backgroundFetchStartedAtUnixMs = fetchStartedAtUnixMs;
    fetchStartedAtMs = performance.now();
    console.warn("[Verbeam] retrying translate with recovered backend:", endpoint);

    try {
      response = await postTranslateRequest(endpoint, request, controller?.signal);
    }
    catch (retryError) {
      if (retryError?.name === "AbortError") {
        throw createAbortError();
      }
      console.error("[Verbeam] fetch retry failed:", endpoint, retryError);
      throw createFetchError(endpoint, retryError);
    }
  }
  finally {
    if (controller) {
      activeRequestControllers.delete(normalized.requestId);
    }
  }

  const body = await response.text();
  const fetchDurationMs = Math.round(performance.now() - fetchStartedAtMs);
  if (!response.ok) {
    console.error("[Verbeam] HTTP error:", response.status, body);
    throw new Error(`Verbeam returned HTTP ${response.status} from ${endpoint}: ${trimForError(body)}`);
  }

  let json;
  try {
    json = JSON.parse(body);
  }
  catch (parseError) {
    console.error("[Verbeam] non-JSON response:", body);
    throw new Error(`Verbeam returned non-JSON response: ${trimForError(body)}`);
  }

  if (json.errorCode && json.errorCode !== "0") {
    console.error("[Verbeam] API error:", json.errorCode, json.errorMessage);
    throw new Error(json.errorMessage || `Verbeam error ${json.errorCode}`);
  }

  json.extensionTrace = {
    traceId: normalized.traceId || "",
    itemId: normalized.itemId || "",
    requestId: normalized.requestId || "",
    backgroundReceivedAtUnixMs,
    backgroundFetchStartedAtUnixMs: fetchStartedAtUnixMs,
    backgroundTotalMs: Math.max(0, Date.now() - backgroundReceivedAtUnixMs),
    fetchMs: fetchDurationMs,
    responseBytes: body.length
  };

  console.log("[Verbeam] translate success:", json.engine, json.result?.slice(0, 80));
  return json;
}

function postTranslateRequest(endpoint, request, signal) {
  return fetch(endpoint, {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    signal,
    body: JSON.stringify(request)
  });
}

async function recoverBackendAfterFetchFailure(failedBackendUrl) {
  const detected = await detectBackendUrlForRecovery(failedBackendUrl);
  if (!detected?.url) {
    return "";
  }

  await rememberBackend({ backendUrl: detected.url }).catch((error) => {
    console.warn("[Verbeam] failed to persist recovered backend:", error?.message || error);
  });
  return detected.url;
}

async function detectBackendUrlForRecovery(preferredUrl) {
  const candidates = await buildBackendRecoveryCandidates(preferredUrl);
  let cursor = 0;
  let remaining = Math.min(BACKEND_RECOVERY_CONCURRENCY, candidates.length);

  return new Promise((resolve) => {
    if (remaining <= 0) {
      resolve(null);
      return;
    }

    let resolved = false;
    async function worker() {
      while (!resolved && cursor < candidates.length) {
        const index = cursor;
        cursor += 1;
        const result = await probeBackendUrl(candidates[index]);
        if (result && !resolved) {
          resolved = true;
          resolve({ ...result, index });
          return;
        }
      }

      remaining -= 1;
      if (!resolved && remaining <= 0) {
        resolve(null);
      }
    }

    for (let index = 0; index < remaining; index += 1) {
      worker();
    }
  });
}

async function buildBackendRecoveryCandidates(preferredUrl) {
  const candidates = [];
  const addCandidate = (rawUrl) => {
    const normalized = normalizeBackendBaseUrl(rawUrl);
    if (normalized && !candidates.includes(normalized)) {
      candidates.push(normalized);
    }
  };

  addCandidate(preferredUrl);
  addSamePortHostAlternates(preferredUrl, addCandidate);
  addCandidate(await readStoredBackendUrl());
  for (const url of BACKEND_RECOVERY_CANDIDATE_URLS) {
    addCandidate(url);
  }
  for (const [start, end] of BACKEND_RECOVERY_PORT_RANGES) {
    for (let port = start; port <= end; port += 1) {
      addCandidate(`http://127.0.0.1:${port}`);
      addCandidate(`http://localhost:${port}`);
    }
  }
  addCandidate(DEFAULT_BACKEND_URL);
  return candidates;
}

function addSamePortHostAlternates(rawUrl, addCandidate) {
  try {
    const base = new URL(normalizeBackendBaseUrl(rawUrl));
    const port = base.port || "80";
    addCandidate(`http://127.0.0.1:${port}`);
    addCandidate(`http://localhost:${port}`);
  }
  catch {
    // Ignore malformed user settings; the standard candidate list follows.
  }
}

function readStoredBackendUrl() {
  return new Promise((resolve) => {
    chrome.storage.local.get("settings", (items) => {
      if (chrome.runtime.lastError) {
        resolve("");
        return;
      }
      resolve(items.settings?.backendUrl || "");
    });
  });
}

function normalizePayload(payload) {
  return {
    ...(payload || {}),
    backendUrl: normalizeBackendBaseUrl(payload?.backendUrl),
    requestId: String(payload?.requestId || ""),
    text: String(payload?.text || ""),
    source: payload?.source || "auto",
    target: payload?.target || "zh-TW",
    provider: payload?.provider || "",
    model: payload?.model || "",
    mode: payload?.mode || "web_article",
    profile: payload?.profile || "web-page",
    context: payload?.context || "",
    contextItems: payload?.contextItems || undefined,
    skipMemoryContext: Boolean(payload?.skipMemoryContext),
    webTitle: payload?.webTitle || "",
    webSummary: payload?.webSummary || "",
    webContent: payload?.webContent || "",
    traceId: String(payload?.traceId || ""),
    itemId: String(payload?.itemId || ""),
    chunkId: String(payload?.chunkId || ""),
    clientQueuedAtUnixMs: normalizeUnixMs(payload?.clientQueuedAtUnixMs),
    clientRequestStartedAtUnixMs: normalizeUnixMs(payload?.clientRequestStartedAtUnixMs)
  };
}

function normalizeUnixMs(value) {
  const number = Number(value);
  return Number.isFinite(number) && number > 0 ? Math.round(number) : undefined;
}

function shouldBatchPayload(payload) {
  if (!String(payload.text || "").trim()) {
    return false;
  }

  if (String(payload.text || "").includes(BATCH_SEPARATOR)) {
    return false;
  }

  const provider = String(payload.provider || "").toLowerCase();
  if (provider === "mock" || provider === "deepl") {
    return false;
  }
  if (LOCAL_PROVIDER_NAMES.has(provider)) {
    return false;
  }

  return String(payload.text || "").length <= MAX_BATCH_CHARS;
}

function batchKey(payload) {
  return JSON.stringify({
    endpoint: buildTranslateEndpoint(payload.backendUrl),
    source: payload.source,
    target: payload.target,
    provider: payload.provider,
    model: payload.model,
    mode: payload.mode,
    profile: payload.profile,
    sessionId: payload.sessionId,
    context: payload.context,
    contextItems: payload.contextItems,
    skipMemoryContext: payload.skipMemoryContext,
    webTitle: payload.webTitle,
    webSummary: payload.webSummary,
    webContent: payload.webContent
  });
}

function translationRequestKey(payload) {
  return JSON.stringify({
    ...JSON.parse(batchKey(payload)),
    text: payload.text
  });
}

function parseBatchResult(result) {
  return String(result || "")
    .trim()
    .split(BATCH_SEPARATOR_PATTERN)
    .map((part) => part.trim());
}

function joinContext(...parts) {
  return parts
    .map((part) => String(part || "").trim())
    .filter(Boolean)
    .join("\n\n");
}

function buildTranslateEndpoint(rawUrl) {
  const base = new URL(normalizeBackendBaseUrl(rawUrl));
  if (base.protocol !== "http:") {
    throw new Error("Verbeam backend URL must use http.");
  }

  if (!["localhost", "127.0.0.1"].includes(base.hostname)) {
    throw new Error("Verbeam backend URL must point to localhost or 127.0.0.1.");
  }

  const cleanPath = base.pathname.replace(/\/+$/, "");
  base.pathname = cleanPath.endsWith("/translate/web")
    ? cleanPath
    : "/translate/web";
  base.search = "";
  base.hash = "";
  return base.toString();
}

function normalizeBackendBaseUrl(rawUrl) {
  try {
    const base = new URL(rawUrl || DEFAULT_BACKEND_URL);
    if (base.protocol !== "http:" || !["localhost", "127.0.0.1"].includes(base.hostname)) {
      return DEFAULT_BACKEND_URL;
    }
    base.pathname = "";
    base.search = "";
    base.hash = "";
    return base.toString().replace(/\/$/, "");
  }
  catch {
    return DEFAULT_BACKEND_URL;
  }
}

function trimForError(value) {
  const text = String(value || "").replace(/\s+/g, " ").trim();
  return text.length <= 300 ? text : `${text.slice(0, 300)}...`;
}

function createFetchError(endpoint, cause) {
  const causeMessage = cause instanceof Error ? cause.message : String(cause || "unknown error");
  return new Error(`Failed to fetch Verbeam backend at ${endpoint}. Check that Verbeam is running and reload Detect if the port changed. Cause: ${causeMessage}`);
}

function createAbortError(message = "Verbeam request canceled.") {
  const error = new Error(message);
  error.name = "AbortError";
  return error;
}
