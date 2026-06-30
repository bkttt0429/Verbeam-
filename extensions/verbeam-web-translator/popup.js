const DEFAULT_SETTINGS = {
  settingsVersion: 5,
  backendUrl: "http://127.0.0.1:5768",
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

const BACKEND_DETECT_TIMEOUT_MS = 4000;
const BACKEND_DETECT_CONCURRENCY = 8;
const PAGE_TRANSLATION_SESSION_KEY = "pageTranslationSession";
const PAGE_TRANSLATION_SESSION_TTL_MS = 5 * 60 * 1000;
const EXPECTED_CONTENT_SCRIPT_BUILD = "2026-06-22-context-invalidated";
const BACKEND_CANDIDATE_URLS = [
  "http://127.0.0.1:5768",
  "http://localhost:5768",
  "http://127.0.0.1:5758",
  "http://localhost:5758",
  "http://127.0.0.1:5757",
  "http://localhost:5757",
  "http://127.0.0.1:5069",
  "http://localhost:5069"
];
const BACKEND_SCAN_PORT_RANGES = [
  [5750, 5799],
  [5060, 5075],
  [5000, 5010]
];

const fields = {
  backendUrl: document.getElementById("backendUrl"),
  source: document.getElementById("source"),
  target: document.getElementById("target"),
  provider: document.getElementById("provider"),
  model: document.getElementById("model"),
  mode: document.getElementById("mode"),
  contextMode: document.getElementById("contextMode"),
  displayMode: document.getElementById("displayMode"),
  watchAjax: document.getElementById("watchAjax"),
  concurrency: document.getElementById("concurrency"),
  floatingButtonEnabled: document.getElementById("floatingButtonEnabled"),
  floatingButtonSide: document.getElementById("floatingButtonSide"),
  floatingButtonLocked: document.getElementById("floatingButtonLocked")
};

const status = document.getElementById("status");
const translateButton = document.getElementById("translateButton");
const restoreButton = document.getElementById("restoreButton");
const refreshCatalogButton = document.getElementById("refreshCatalogButton");
const detectBackendButton = document.getElementById("detectBackendButton");
const resetDisabledSitesButton = document.getElementById("resetDisabledSitesButton");

const catalogState = {
  health: null,
  providers: [],
  models: [],
  busy: false
};
let currentSettings = { ...DEFAULT_SETTINGS };

document.addEventListener("DOMContentLoaded", async () => {
  const settings = await loadSettings();
  currentSettings = settings;
  fillForm(settings);
  await refreshCatalog(settings, { quiet: true });
  await refreshStatus();
});

translateButton.addEventListener("click", async () => {
  await runAction("Starting page translation...", async () => {
    const settings = await ensureBackendOnline(readForm(), { quiet: true });
    await saveSettings(settings);
    await sendToActiveTab({ type: "verbeam:translatePage", settings });
    setStatus("Page translation queued.");
    setTimeout(refreshStatus, 800);
  });
});

restoreButton.addEventListener("click", async () => {
  await runAction("Restoring original text...", async () => {
    await sendToActiveTab({ type: "verbeam:restorePage" });
    setStatus("Original text restored.");
  });
});

refreshCatalogButton.addEventListener("click", async () => {
  await runAction("Loading local models...", async () => {
    await refreshCatalog(readForm(), { quiet: false });
  });
});

detectBackendButton.addEventListener("click", async () => {
  await runAction("Detecting backend...", async () => {
    const settings = await detectAndApplyBackend(readForm(), { quiet: false });
    await refreshCatalog(settings, { quiet: false, autoDetect: false });
  });
});

fields.backendUrl.addEventListener("change", async () => {
  const settings = readForm();
  await saveAndPushSettings(settings);
  await refreshCatalog(settings, { quiet: false });
});

fields.provider.addEventListener("change", async () => {
  const settings = { ...readForm(), model: "" };
  fields.model.value = "";
  await saveAndPushSettings(settings);
  await refreshModels(settings);
});

for (const field of [
  fields.source,
  fields.target,
  fields.model,
  fields.mode,
  fields.contextMode,
  fields.displayMode,
  fields.watchAjax,
  fields.concurrency,
  fields.floatingButtonEnabled,
  fields.floatingButtonSide,
  fields.floatingButtonLocked
]) {
  field.addEventListener("change", async () => {
    await saveAndPushSettings(readForm());
  });
}

resetDisabledSitesButton.addEventListener("click", async () => {
  await runAction("Enabling floating button on all sites...", async () => {
    const settings = { ...readForm(), disabledSitePatterns: [] };
    await saveAndPushSettings(settings);
    fillForm(settings);
    setStatus("Floating button enabled for all sites.");
  });
});

async function saveAndPushSettings(settings) {
  const normalized = normalizeSettings(settings);
  currentSettings = normalized;
  await saveSettings(normalized);
  await sendToActiveTab({ type: "verbeam:updateSettings", settings: normalized }).catch(() => {});
}

async function runAction(workingText, action) {
  setBusy(true);
  setStatus(workingText);
  try {
    await action();
  }
  catch (error) {
    setStatus(error instanceof Error ? error.message : String(error), true);
  }
  finally {
    setBusy(false);
  }
}

async function refreshCatalog(settings, options = {}) {
  catalogState.busy = true;
  setBusy(true);
  try {
    const resolvedSettings = options.autoDetect === false
      ? normalizeSettings(settings)
      : await ensureBackendOnline(settings, { quiet: true });

    catalogState.health = await fetchBackendJson(resolvedSettings.backendUrl, "/health").catch(() => null);
    catalogState.providers = normalizeCollection(await fetchBackendJson(resolvedSettings.backendUrl, "/providers"));
    populateProviders(resolvedSettings.provider);
    await refreshModels(resolvedSettings);
    if (!options.quiet) {
      const localCount = catalogState.providers.filter((provider) => provider.isLocal).length;
      setStatus(`Loaded ${localCount} local provider(s), ${catalogState.models.length} model(s).`);
    }
  }
  catch (error) {
    populateProviders(settings.provider);
    populateModels([], settings.model);
    if (!options.quiet) {
      setStatus(error instanceof Error ? error.message : String(error), true);
    }
  }
  finally {
    catalogState.busy = false;
    setBusy(false);
  }
}

async function ensureBackendOnline(settings, options = {}) {
  const normalized = normalizeSettings(settings);
  const current = await probeBackendUrl(normalized.backendUrl);
  if (current) {
    catalogState.health = current.health;
    const updated = normalizeSettings({ ...normalized, backendUrl: current.url });
    if (updated.backendUrl !== normalized.backendUrl) {
      fillForm(updated);
      await saveAndPushSettings(updated);
    }
    return updated;
  }

  return detectAndApplyBackend(normalized, options);
}

async function detectAndApplyBackend(settings, options = {}) {
  const normalized = normalizeSettings(settings);
  const detected = await detectBackendUrl(normalized);
  if (!detected) {
    if (!options.quiet) {
      setStatus("No Verbeam backend found on localhost ports 5750-5799, 5060-5075, or 5000-5010.", true);
    }
    return normalized;
  }

  catalogState.health = detected.health;
  const updated = normalizeSettings({ ...normalized, backendUrl: detected.url });
  if (updated.backendUrl !== normalized.backendUrl) {
    fillForm(updated);
    await saveAndPushSettings(updated);
  }
  if (!options.quiet) {
    setStatus(`Detected backend at ${updated.backendUrl}.`);
  }
  return updated;
}

async function detectBackendUrl(settings) {
  const candidates = await buildBackendCandidates(settings);
  const results = await probeBackendCandidates(candidates);
  return results
    .filter(Boolean)
    .sort((a, b) => a.index - b.index)[0] || null;
}

async function buildBackendCandidates(settings) {
  const candidates = [];
  const addCandidate = (rawUrl) => {
    const normalized = normalizeBackendBaseUrl(rawUrl);
    if (normalized && !candidates.includes(normalized)) {
      candidates.push(normalized);
    }
  };

  addCandidate(settings?.backendUrl);
  for (const origin of await localOriginsFromActiveTab()) {
    addCandidate(origin);
  }
  for (const url of BACKEND_CANDIDATE_URLS) {
    addCandidate(url);
  }
  for (const [start, end] of BACKEND_SCAN_PORT_RANGES) {
    for (let port = start; port <= end; port += 1) {
      addCandidate(`http://127.0.0.1:${port}`);
      addCandidate(`http://localhost:${port}`);
    }
  }
  addCandidate(DEFAULT_SETTINGS.backendUrl);
  return candidates;
}

async function probeBackendCandidates(candidates) {
  const results = [];
  let cursor = 0;
  const workerCount = Math.min(BACKEND_DETECT_CONCURRENCY, candidates.length);

  async function worker() {
    while (cursor < candidates.length) {
      const index = cursor;
      cursor += 1;
      const result = await probeBackendUrl(candidates[index]);
      if (result) {
        results.push({ ...result, index });
      }
    }
  }

  await Promise.all(Array.from({ length: workerCount }, worker));
  return results;
}

async function localOriginsFromActiveTab() {
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab?.url) {
      return [];
    }
    const url = new URL(tab.url);
    if (url.protocol !== "http:" || !["localhost", "127.0.0.1"].includes(url.hostname)) {
      return [];
    }
    return [url.origin];
  }
  catch {
    return [];
  }
}

async function probeBackendUrl(rawUrl) {
  const url = normalizeBackendBaseUrl(rawUrl);
  if (!url) {
    return null;
  }

  const backgroundResult = await probeBackendViaBackground(url);
  if (backgroundResult !== undefined) {
    return backgroundResult;
  }

  return probeBackendUrlDirect(url);
}

async function probeBackendViaBackground(url) {
  try {
    const response = await sendRuntimeMessage({
      type: "verbeam:probeBackend",
      payload: { backendUrl: url }
    });
    const data = response?.data;
    if (response?.ok && data?.ok && data?.health) {
      return { url: normalizeBackendBaseUrl(data.url || url), health: data.health };
    }
    if (response?.ok) {
      return null;
    }
  }
  catch {
    // Fall through to direct fetch for older installed background scripts.
  }
  return undefined;
}

async function probeBackendUrlDirect(rawUrl) {
  const url = normalizeBackendBaseUrl(rawUrl);
  if (!url) {
    return null;
  }

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), BACKEND_DETECT_TIMEOUT_MS);
  try {
    const endpoint = buildBackendEndpoint(url, "/health");
    const response = await fetch(endpoint, {
      cache: "no-store",
      signal: controller.signal
    });
    const text = await response.text();
    if (!response.ok) {
      return null;
    }
    const health = text ? JSON.parse(text) : null;
    if (health?.status !== "ok" && !health?.defaultProvider) {
      return null;
    }
    return { url, health };
  }
  catch {
    return null;
  }
  finally {
    clearTimeout(timer);
  }
}

async function refreshModels(settings) {
  const provider = effectiveProvider(settings);
  if (!provider) {
    populateModels([], settings.model);
    return;
  }

  try {
    catalogState.models = normalizeCollection(await fetchBackendJson(
      settings.backendUrl,
      `/translation/models?provider=${encodeURIComponent(provider)}`
    ));
  }
  catch {
    catalogState.models = [];
  }
  populateModels(catalogState.models, settings.model);
}

function populateProviders(selectedProvider) {
  const providerSelect = fields.provider;
  providerSelect.innerHTML = "";
  providerSelect.appendChild(optionElement(
    "",
    `Backend default${catalogState.health?.defaultProvider ? ` (${catalogState.health.defaultProvider})` : ""}`
  ));

  const providers = [...catalogState.providers].sort((a, b) => {
    if (Boolean(a.isLocal) !== Boolean(b.isLocal)) {
      return a.isLocal ? -1 : 1;
    }
    return providerLabel(a).localeCompare(providerLabel(b));
  });

  for (const provider of providers) {
    const tags = [
      provider.isLocal ? "local" : "remote",
      provider.requiresNetwork ? "network" : "offline"
    ];
    providerSelect.appendChild(optionElement(
      provider.name,
      `${providerLabel(provider)} - ${tags.join(", ")}`
    ));
  }

  if (selectedProvider && !providers.some((provider) => provider.name === selectedProvider)) {
    providerSelect.appendChild(optionElement(selectedProvider, `${selectedProvider} - custom`));
  }
  providerSelect.value = selectedProvider || "";
}

function populateModels(models, selectedModel) {
  const modelSelect = fields.model;
  modelSelect.innerHTML = "";
  const defaultModel = defaultModelForEffectiveProvider();
  modelSelect.appendChild(optionElement(
    "",
    `Provider default${defaultModel ? ` (${defaultModel})` : ""}`
  ));

  const sorted = [...models].sort((a, b) => {
    if (Boolean(a.isDefault) !== Boolean(b.isDefault)) {
      return a.isDefault ? -1 : 1;
    }
    if (Boolean(a.isInstalled) !== Boolean(b.isInstalled)) {
      return a.isInstalled ? -1 : 1;
    }
    return modelLabel(a).localeCompare(modelLabel(b));
  });

  for (const model of sorted) {
    const tags = [];
    if (model.isDefault) tags.push("default");
    tags.push(model.isInstalled ? "installed" : "not installed");
    if (model.recommendedUse && !model.isDefault) tags.push(model.recommendedUse);
    modelSelect.appendChild(optionElement(
      model.name,
      `${modelLabel(model)} - ${tags.join(", ")}`
    ));
  }

  if (selectedModel && !sorted.some((model) => model.name === selectedModel)) {
    modelSelect.appendChild(optionElement(selectedModel, `${selectedModel} - custom`));
  }
  modelSelect.value = selectedModel || "";
}

async function refreshStatus() {
  try {
    const response = await sendToActiveTab({ type: "verbeam:getStatus" });
    if (response.staleBuild) {
      setStatus("This tab still runs an old Verbeam script. Refresh the page.", true);
      return;
    }

    const stats = response.stats || {};
    if (response.enabled) {
      const pending = Number(stats.queued || 0) + Number(stats.running || 0);
      setStatus(`Translated ${stats.translated || 0}; pending ${pending}; failed ${stats.failed || 0}.`);
    }
    else {
      setStatus("Ready.");
    }
  }
  catch {
    setStatus("Open an http or https page, then translate.", true);
  }
}

function readForm() {
  return {
    ...DEFAULT_SETTINGS,
    backendUrl: fields.backendUrl.value.trim() || DEFAULT_SETTINGS.backendUrl,
    source: fields.source.value.trim() || DEFAULT_SETTINGS.source,
    target: fields.target.value.trim() || DEFAULT_SETTINGS.target,
    provider: fields.provider.value.trim(),
    model: fields.model.value.trim(),
    mode: fields.mode.value.trim() || DEFAULT_SETTINGS.mode,
    contextMode: normalizeContextMode(fields.contextMode.value),
    displayMode: fields.displayMode.value === "translationOnly" ? "translationOnly" : "bilingual",
    watchAjax: fields.watchAjax.checked,
    concurrency: Math.max(1, Math.min(6, Number(fields.concurrency.value) || DEFAULT_SETTINGS.concurrency)),
    floatingButtonEnabled: fields.floatingButtonEnabled.checked,
    floatingButtonSide: fields.floatingButtonSide.value === "left" ? "left" : "right",
    floatingButtonPosition: Number(currentSettings.floatingButtonPosition || DEFAULT_SETTINGS.floatingButtonPosition),
    floatingButtonLocked: fields.floatingButtonLocked.checked,
    disabledSitePatterns: normalizeStringList(currentSettings.disabledSitePatterns)
  };
}

function fillForm(settings) {
  const value = normalizeSettings(settings);
  currentSettings = value;
  fields.backendUrl.value = value.backendUrl;
  fields.source.value = value.source;
  fields.target.value = value.target;
  fields.provider.value = value.provider;
  fields.model.value = value.model;
  fields.mode.value = value.mode;
  fields.contextMode.value = value.contextMode;
  fields.displayMode.value = value.displayMode === "translationOnly" ? "translationOnly" : "bilingual";
  fields.watchAjax.checked = value.watchAjax;
  fields.concurrency.value = value.concurrency;
  fields.floatingButtonEnabled.checked = value.floatingButtonEnabled;
  fields.floatingButtonSide.value = value.floatingButtonSide;
  fields.floatingButtonSide.dataset.position = String(value.floatingButtonPosition);
  fields.floatingButtonLocked.checked = value.floatingButtonLocked;
}

function loadSettings() {
  return new Promise((resolve) => {
    chrome.storage.local.get("settings", (items) => {
      resolve(normalizeSettings(items.settings));
    });
  });
}

function saveSettings(settings) {
  const normalized = normalizeSettings(settings);
  currentSettings = normalized;
  return chrome.storage.local.set({ settings: normalized });
}

function normalizeSettings(settings) {
  const value = { ...DEFAULT_SETTINGS, ...(settings || {}) };
  const previousVersion = Number(settings?.settingsVersion) || 1;
  const concurrency = previousVersion < 5 && Number(value.concurrency) === 2
    ? DEFAULT_SETTINGS.concurrency
    : value.concurrency;

  return {
    ...value,
    settingsVersion: DEFAULT_SETTINGS.settingsVersion,
    backendUrl: normalizeBackendBaseUrl(value.backendUrl),
    source: String(value.source || DEFAULT_SETTINGS.source).trim(),
    target: String(value.target || DEFAULT_SETTINGS.target).trim(),
    provider: String(value.provider || "").trim(),
    model: String(value.model || "").trim(),
    mode: String(value.mode || DEFAULT_SETTINGS.mode).trim(),
    contextMode: normalizeContextMode(value.contextMode),
    displayMode: value.displayMode === "translationOnly" ? "translationOnly" : "bilingual",
    watchAjax: value.watchAjax !== false,
    concurrency: Math.max(1, Math.min(6, Number(concurrency) || DEFAULT_SETTINGS.concurrency)),
    floatingButtonEnabled: value.floatingButtonEnabled !== false,
    floatingButtonSide: value.floatingButtonSide === "left" ? "left" : "right",
    floatingButtonPosition: clamp(Number(value.floatingButtonPosition), 0.08, 0.86, DEFAULT_SETTINGS.floatingButtonPosition),
    floatingButtonLocked: Boolean(value.floatingButtonLocked),
    disabledSitePatterns: normalizeStringList(value.disabledSitePatterns)
  };
}

function normalizeContextMode(value) {
  return value === "balanced" || value === "full" ? value : "fast";
}

function normalizeStringList(value) {
  return Array.isArray(value)
    ? value.map((item) => String(item || "").trim()).filter(Boolean)
    : [];
}

function clamp(value, min, max, fallback = min) {
  const number = Number(value);
  if (!Number.isFinite(number)) {
    return fallback;
  }
  return Math.max(min, Math.min(max, number));
}

async function sendToActiveTab(message) {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id) {
    throw new Error("No active tab found.");
  }

  if (!tab.url || (!tab.url.startsWith("http:") && !tab.url.startsWith("https:"))) {
    throw new Error("Open an http or https page first.");
  }

  try {
    const frames = await getTabFrames(tab.id);
    await rememberPageTranslationSession(tab, message, frames);
    const responses = await sendMessageToTabFrames(tab.id, message, frames);
    return aggregateFrameResponses(message, responses);
  }
  catch (error) {
    const text = error instanceof Error ? error.message : String(error);
    if (text.includes("Could not establish connection") || text.includes("Receiving end does not exist")) {
      throw new Error("Connection lost. Please refresh the page and try again.");
    }
    throw error;
  }
}

async function sendMessageToTabFrames(tabId, message, frames = null) {
  frames = frames || await getTabFrames(tabId);
  const targets = frames.length > 0 ? frames : [{ frameId: 0 }];
  const responses = await Promise.all(targets.map((frame) => sendMessageToFrame(tabId, frame.frameId, message)));
  if (!responses.some((item) => item.response?.ok) && !responses.some((item) => item.frameId === 0)) {
    responses.push(await sendMessageToFrame(tabId, 0, message));
  }
  return responses;
}

function getTabFrames(tabId) {
  return new Promise((resolve) => {
    if (!chrome.webNavigation?.getAllFrames) {
      resolve([{ frameId: 0 }]);
      return;
    }

    chrome.webNavigation.getAllFrames({ tabId }, (frames) => {
      const error = chrome.runtime.lastError;
      if (error || !Array.isArray(frames)) {
        resolve([{ frameId: 0 }]);
        return;
      }
      resolve(frames
        .map((frame) => ({ frameId: frame.frameId, url: frame.url || "" }))
        .filter((frame) => Number.isInteger(frame.frameId)));
    });
  });
}

async function rememberPageTranslationSession(tab, message, frames) {
  if (message.type === "verbeam:restorePage") {
    await chrome.storage.local.set({ [PAGE_TRANSLATION_SESSION_KEY]: { enabled: false } });
    return;
  }

  if (message.type !== "verbeam:translatePage") {
    return;
  }

  const now = Date.now();
  const topOrigin = safeOrigin(tab.url);
  const frameHosts = Array.from(new Set(
    (frames || [])
      .map((frame) => safeHostname(frame.url))
      .filter(Boolean)
  ));

  await chrome.storage.local.set({
    [PAGE_TRANSLATION_SESSION_KEY]: {
      enabled: true,
      settings: normalizeSettings(message.settings || currentSettings),
      startedAtUnixMs: now,
      expiresAtUnixMs: now + PAGE_TRANSLATION_SESSION_TTL_MS,
      topUrl: tab.url || "",
      topOrigin,
      frameHosts
    }
  });
}

function safeOrigin(url) {
  try {
    return new URL(url || "").origin;
  }
  catch {
    return "";
  }
}

function safeHostname(url) {
  try {
    return new URL(url || "").hostname.toLowerCase();
  }
  catch {
    return "";
  }
}

function sendMessageToFrame(tabId, frameId, message) {
  return new Promise((resolve) => {
    chrome.tabs.sendMessage(tabId, message, { frameId }, (response) => {
      const error = chrome.runtime.lastError?.message || "";
      resolve({ frameId, response, error });
    });
  });
}

function aggregateFrameResponses(message, responses) {
  const successful = responses.filter((item) => item.response?.ok);
  if (successful.length === 0) {
    const firstError = responses.find((item) => item.error)?.error;
    throw new Error(firstError || "The page translator is not available on this tab.");
  }

  const statuses = successful
    .map((item) => item.response?.data)
    .filter((data) => data && typeof data === "object" && data.stats);
  if (message.type === "verbeam:getStatus" || statuses.length > 0) {
    return mergeFrameStatuses(statuses, successful.length, responses.length);
  }

  return successful[0].response.data;
}

function mergeFrameStatuses(statuses, successfulFrames, totalFrames) {
  const first = statuses[0] || {};
  const builds = Array.from(new Set(
    statuses
      .map((status) => String(status.build || "").trim())
      .filter(Boolean)
  ));
  const missingBuildCount = statuses.filter((status) => !status.build).length;
  const totals = statuses.reduce((sum, status) => {
    const stats = status.stats || {};
    sum.queued += Number(stats.queued || 0);
    sum.running += Number(stats.running || 0);
    sum.translated += Number(stats.translated || 0);
    sum.failed += Number(stats.failed || 0);
    return sum;
  }, { queued: 0, running: 0, translated: 0, failed: 0 });

  return {
    ...first,
    enabled: statuses.some((status) => status.enabled),
    watchingAjax: statuses.some((status) => status.watchingAjax),
    build: builds[0] || "",
    builds,
    staleBuild: missingBuildCount > 0 || builds.some((build) => build !== EXPECTED_CONTENT_SCRIPT_BUILD),
    stats: totals,
    frames: {
      successful: successfulFrames,
      total: totalFrames
    }
  };
}

async function fetchBackendJson(rawUrl, path) {
  const url = normalizeBackendBaseUrl(rawUrl);
  try {
    const response = await sendRuntimeMessage({
      type: "verbeam:fetchBackendJson",
      payload: { backendUrl: url, path }
    });
    if (response?.ok) {
      return response.data;
    }
  }
  catch {
    // Direct fetch keeps compatibility if the background worker was not reloaded yet.
  }

  return fetchBackendJsonDirect(url, path);
}

async function fetchBackendJsonDirect(rawUrl, path) {
  const endpoint = buildBackendEndpoint(rawUrl, path);
  const response = await fetch(endpoint);
  const text = await response.text();
  if (!response.ok) {
    throw new Error(`Backend ${response.status}: ${trimForError(text || response.statusText)}`);
  }
  return text ? JSON.parse(text) : null;
}

function sendRuntimeMessage(message) {
  return new Promise((resolve, reject) => {
    chrome.runtime.sendMessage(message, (response) => {
      const error = chrome.runtime.lastError;
      if (error) {
        reject(new Error(error.message));
        return;
      }
      resolve(response);
    });
  });
}

function buildBackendEndpoint(rawUrl, path) {
  const base = new URL(rawUrl || DEFAULT_SETTINGS.backendUrl);
  if (base.protocol !== "http:") {
    throw new Error("Verbeam backend URL must use http.");
  }
  if (!["localhost", "127.0.0.1"].includes(base.hostname)) {
    throw new Error("Verbeam backend URL must point to localhost or 127.0.0.1.");
  }
  const [pathname, search = ""] = String(path || "/").split("?", 2);
  base.pathname = pathname || "/";
  base.search = search ? `?${search}` : "";
  base.hash = "";
  return base.toString();
}

function normalizeBackendBaseUrl(rawUrl) {
  try {
    const base = new URL(rawUrl || DEFAULT_SETTINGS.backendUrl);
    if (base.protocol !== "http:" || !["localhost", "127.0.0.1"].includes(base.hostname)) {
      return "";
    }
    base.pathname = "";
    base.search = "";
    base.hash = "";
    return base.toString().replace(/\/$/, "");
  }
  catch {
    return "";
  }
}

function normalizeCollection(payload) {
  if (Array.isArray(payload)) {
    return payload;
  }
  if (Array.isArray(payload?.value)) {
    return payload.value;
  }
  return [];
}

function effectiveProvider(settings) {
  return settings.provider ||
    catalogState.health?.defaultProvider ||
    catalogState.providers.find((provider) => provider.isLocal)?.name ||
    "";
}

function defaultModelForEffectiveProvider() {
  const provider = effectiveProvider(readForm());
  if (!provider) {
    return "";
  }
  if (provider === catalogState.health?.defaultProvider) {
    const runtime = provider === "llama-cpp"
      ? catalogState.health?.llamaCpp
      : provider === "ollama"
        ? catalogState.health?.ollama
        : null;
    if (runtime?.model) {
      return runtime.model;
    }
  }
  return catalogState.providers.find((item) => item.name === provider)?.defaultModel || "";
}

function providerLabel(provider) {
  return provider.displayName || provider.name || "provider";
}

function modelLabel(model) {
  const supplier = model.supplierName ? `${model.supplierName}: ` : "";
  return `${supplier}${model.displayName || model.name || "model"}`;
}

function optionElement(value, text) {
  const option = document.createElement("option");
  option.value = value || "";
  option.textContent = text;
  return option;
}

function setBusy(isBusy) {
  translateButton.disabled = isBusy;
  restoreButton.disabled = isBusy;
  refreshCatalogButton.disabled = isBusy || catalogState.busy;
  detectBackendButton.disabled = isBusy || catalogState.busy;
}

function setStatus(text, isError = false) {
  status.textContent = text;
  status.classList.toggle("error", isError);
}

function trimForError(value) {
  const text = String(value || "").replace(/\s+/g, " ").trim();
  return text.length <= 180 ? text : `${text.slice(0, 180)}...`;
}
