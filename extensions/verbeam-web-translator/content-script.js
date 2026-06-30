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

const VERBEAM_EXTENSION_BUILD = "2026-06-22-context-invalidated";

const SKIP_TAGS = new Set([
  "SCRIPT",
  "STYLE",
  "NOSCRIPT",
  "TEXTAREA",
  "INPUT",
  "SELECT",
  "OPTION",
  "CODE",
  "PRE",
  "KBD",
  "SAMP",
  "SVG",
  "CANVAS",
  "IFRAME",
  "OBJECT",
  "EMBED",
  "MATH"
]);

const BLOCK_TAGS = new Set([
  "P",
  "H1", "H2", "H3", "H4", "H5", "H6",
  "LI", "TD", "TH", "DT", "DD",
  "BLOCKQUOTE",
  "ARTICLE", "SECTION", "HEADER", "FOOTER", "MAIN", "ASIDE", "NAV",
  "FIGCAPTION", "SUMMARY", "DETAILS", "FIELDSET", "LEGEND"
]);

const PAGE_CHROME_SELECTOR = [
  "footer",
  "[role='contentinfo']",
  "[aria-label*='breadcrumb' i]",
  "[data-ad]",
  "[data-ad-slot]",
  "ins.adsbygoogle",
  "[id*='ad-slot' i]",
  "[class*='ad-slot' i]",
  "[id*='advert' i]",
  "[class*='advert' i]"
].join(",");

const PAGE_TOOLBAR_SELECTOR = [
  "header",
  "[role='banner']",
  "[role='toolbar']",
  "[aria-label*='toolbar' i]",
  "[data-testid*='toolbar' i]",
  "[data-test*='toolbar' i]",
  "[class*='toolbar' i]",
  "[class*='Toolbar']",
  "[class*='actions' i]",
  "[class*='Actions']"
].join(",");

const PAGE_ACTION_LABEL_PATTERN = /^(copy\s*&\s*edit|copy|download|edit|run|save|share|fork|more|menu|open|close|expand|collapse)$/i;
const SIDE_RAIL_SELECTOR = [
  "aside",
  "[role='complementary']",
  "[aria-label*='table of contents' i]",
  "[aria-label*='contents' i]",
  "[aria-label*='sidebar' i]",
  "[data-testid*='table-of-contents' i]",
  "[data-testid*='sidebar' i]",
  "[data-test*='table-of-contents' i]",
  "[data-test*='sidebar' i]",
  "[class*='table-of-contents' i]",
  "[class*='TableOfContents']",
  "[class*='sidebar' i]",
  "[class*='Sidebar']"
].join(",");

const ICON_GLYPH_SELECTOR = [
  ".material-icons",
  ".material-icons-outlined",
  ".material-icons-round",
  ".material-icons-sharp",
  ".material-icons-two-tone",
  ".material-symbols-outlined",
  ".material-symbols-rounded",
  ".material-symbols-sharp",
  "[class*='material-icons']",
  "[class*='material-symbols']"
].join(",");
const ICON_LIGATURE_PATTERN = /^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$/i;

const TRANSLATED_ATTR = "data-verbeam-translated";
const IGNORE_ATTR = "data-verbeam-ignore";
const BILINGUAL_CLASS = "verbeam-bilingual";
const TRANSLATION_ONLY_CLASS = "verbeam-translation-only";
const MARKER_CLASS = "verbeam-bilingual-marker";
const OVERLAY_TRANSLATION_CLASS = "verbeam-overlay-translation";
const COMPACT_INLINE_CLASS = "verbeam-compact-inline";
const COMPACT_STACKED_CLASS = "verbeam-compact-stacked";
const TRANSLATION_LAYOUT_ATTR = "data-verbeam-layout";
const STATUS_BADGE_ID = "verbeam-web-translator-status";
const OVERLAY_ROOT_ID = "verbeam-overlay-root";
const FLOATING_BUTTON_ID = "verbeam-floating-button";
const FLOATING_DRAG_START_DISTANCE_PX = 6;
const FLOATING_LONG_PRESS_MS = 300;
const FLOATING_TOP_MIN = 0.08;
const FLOATING_TOP_MAX = 0.86;
const PAGE_TRANSLATION_ROOT_MARGIN = "600px 0px";
const MUTATION_SCAN_DELAY_MS = 120;
const AJAX_GLOBAL_RESCAN_THROTTLE_MS = 2000;
const AJAX_SETTLE_RESCAN_DELAYS_MS = [800, 2500, 6000];
const ROUTE_CHECK_INTERVAL_MS = 1000;
const HYDRATION_RESCAN_DELAYS_MS = [250, 750, 1500, 3000, 6000, 10000, 15000, 30000, 60000, 120000];
const PAGE_TRANSLATION_SESSION_KEY = "pageTranslationSession";

const state = {
  settings: { ...DEFAULT_SETTINGS },
  enabled: false,
  runId: 0,
  pageContext: null,
  intersectionObserver: null,
  mutationObserver: null,
  shadowMutationObservers: new Map(),
  mutationScanTimer: null,
  hydrationScanTimers: new Set(),
  ajaxSettleScanTimers: new Set(),
  ajaxGlobalScanTimer: null,
  lastAjaxGlobalScanAt: 0,
  pendingMutationRoots: new Set(),
  routeCheckTimer: null,
  routeSignature: "",
  queue: [],
  queuedBlocks: new WeakSet(),
  observedBlocks: new WeakSet(),
  queueSequence: 0,
  requestSequence: 0,
  traceSequence: 0,
  currentTraceId: "",
  activeRequestIds: new Set(),
  translationCache: new Map(),
  running: 0,
  blockStates: new WeakMap(),
  overlayBlocks: new Set(),
  activeOverlayBlock: null,
  overlayPositionFrame: 0,
  overlayRoot: null,
  floatingButton: null,
  floatingPointer: null,
  runtimeInvalidated: false,
  stats: {
    queued: 0,
    running: 0,
    translated: 0,
    failed: 0
  }
};

try {
  console.info("[Verbeam content-script] loaded", VERBEAM_EXTENSION_BUILD);
  loadSettings().then((settings) => {
    applySettings(settings);
    cleanupDisallowedPageShellTranslations();
    maybeStartActiveTranslationSession().catch((error) => {
      console.debug("[Verbeam] active translation session skipped:", error?.message || error);
    });
    discoverBackendFromCurrentPage().catch((error) => {
      console.debug("[Verbeam] backend page discovery skipped:", error?.message || error);
    });
  });
}
catch (error) {
  console.error("[Verbeam content-script] failed to load settings:", error);
}

window.addEventListener("popstate", handlePotentialRouteChange);
window.addEventListener("hashchange", handlePotentialRouteChange);
window.addEventListener("load", handlePageLifecycleRescan, true);
window.addEventListener("scroll", scheduleOverlayPositionUpdate, true);
window.addEventListener("resize", scheduleOverlayPositionUpdate);
document.addEventListener("readystatechange", handlePageLifecycleRescan, true);
document.addEventListener("pointerover", handleOverlayPointerOver, true);
document.addEventListener("pointerout", handleOverlayPointerOut, true);
document.addEventListener("focusin", handleOverlayFocusIn, true);
document.addEventListener("focusout", handleOverlayFocusOut, true);

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || typeof message.type !== "string") {
    return false;
  }

  switch (message.type) {
    case "verbeam:getStatus":
      sendResponse({ ok: true, data: getStatus() });
      return false;

    case "verbeam:updateSettings":
      applySettings(message.settings);
      sendResponse({ ok: true, data: getStatus() });
      return false;

    case "verbeam:translatePage":
      translatePage(message.settings)
        .then(() => sendResponse({ ok: true, data: getStatus() }))
        .catch((error) => sendResponse({
          ok: false,
          error: error instanceof Error ? error.message : String(error)
        }));
      return true;

    case "verbeam:restorePage":
      restorePage();
      sendResponse({ ok: true, data: getStatus() });
      return false;

    default:
      return false;
  }
});

async function translatePage(settings) {
  applySettings(settings || state.settings);
  state.enabled = true;
  state.runId += 1;
  state.currentTraceId = createTraceId();
  abortActiveRequests();
  state.pageContext = collectPageContext(state.settings);
  state.queue = [];
  state.queuedBlocks = new WeakSet();
  state.observedBlocks = new WeakSet();
  state.pendingMutationRoots.clear();
  state.running = 0;
  clearMutationScanTimer();
  clearHydrationScans();
  clearAjaxSettleScans();
  resetStats();
  ensureStatusBadge();
  ensurePageStyles();
  cleanupLegacyCompactInlineTranslations();
  cleanupDisallowedPageShellTranslations();
  stopIntersectionObserver();
  startIntersectionObserver();

  if (state.settings.watchAjax) {
    startMutationObserver();
  }
  else {
    stopMutationObserver();
  }

  scanTranslatableRoot(document.body || document.documentElement);
  scheduleHydrationScans();
  scheduleAjaxActivityRescans();
  startRouteWatcher();
  updateFloatingButtonState();
}

function restorePage() {
  state.runId += 1;
  state.enabled = false;
  state.currentTraceId = "";
  abortActiveRequests();
  stopRouteWatcher();
  stopMutationObserver();
  stopIntersectionObserver();
  clearMutationScanTimer();
  clearHydrationScans();
  clearAjaxSettleScans();

  const translatedBlocks = document.querySelectorAll(`[${TRANSLATED_ATTR}]`);
  for (const block of Array.from(translatedBlocks)) {
    restoreBlock(block);
  }
  for (const block of Array.from(state.overlayBlocks)) {
    restoreBlock(block);
  }

  state.blockStates = new WeakMap();
  state.overlayBlocks.clear();
  state.activeOverlayBlock = null;
  if (state.overlayPositionFrame) {
    window.cancelAnimationFrame(state.overlayPositionFrame);
    state.overlayPositionFrame = 0;
  }
  state.queuedBlocks = new WeakSet();
  state.observedBlocks = new WeakSet();
  state.pendingMutationRoots.clear();
  state.queue = [];
  state.running = 0;
  resetStats();
  updateStatusBadge();
  updateFloatingButtonState();
}

function restoreBlock(block) {
  const blockState = state.blockStates.get(block);
  const mode = block.getAttribute(TRANSLATED_ATTR);

  if (blockState?.overlayNode) {
    blockState.overlayNode.remove();
  }
  if (state.activeOverlayBlock === block) {
    state.activeOverlayBlock = null;
  }
  if (blockState?.titleWasSetByVerbeam) {
    if (blockState.hadOriginalTitle) {
      block.setAttribute("title", blockState.originalTitle || "");
    }
    else {
      block.removeAttribute("title");
    }
  }

  if (mode === "translationOnly") {
    if (blockState?.originalHtml) {
      block.innerHTML = blockState.originalHtml;
    }
    else {
      const originalHtml = block.dataset.verbeamOriginalHtml;
      if (originalHtml) {
        block.innerHTML = originalHtml;
      }
    }
  }
  else if (mode === "bilingual" || mode === "compact") {
    const markers = block.querySelectorAll(`.${MARKER_CLASS}, .${BILINGUAL_CLASS}, .${COMPACT_INLINE_CLASS}, .${COMPACT_STACKED_CLASS}`);
    for (const marker of Array.from(markers)) {
      marker.remove();
    }
  }

  block.removeAttribute(TRANSLATED_ATTR);
  block.removeAttribute(IGNORE_ATTR);
  block.removeAttribute(TRANSLATION_LAYOUT_ATTR);
  delete block.dataset.verbeamOriginalHtml;
  delete block.dataset.verbeamOriginal;
  state.overlayBlocks.delete(block);
  state.blockStates.delete(block);
}

function cleanupLegacyCompactInlineTranslations() {
  const markers = document.querySelectorAll(`.${COMPACT_INLINE_CLASS}, .${COMPACT_STACKED_CLASS}`);
  for (const marker of Array.from(markers)) {
    const owner = marker.closest?.(`[${TRANSLATED_ATTR}]`) || marker.parentElement;
    const markerText = String(marker.textContent || "").trim();
    marker.remove();

    if (!owner || owner.nodeType !== Node.ELEMENT_NODE) {
      continue;
    }

    if (owner.getAttribute(TRANSLATED_ATTR) === "compact") {
      if (markerText && owner.getAttribute("title") === markerText) {
        owner.removeAttribute("title");
      }
      owner.removeAttribute(TRANSLATED_ATTR);
      owner.removeAttribute(IGNORE_ATTR);
      owner.removeAttribute(TRANSLATION_LAYOUT_ATTR);
      state.overlayBlocks.delete(owner);
      state.blockStates.delete(owner);
    }
  }
}

function cleanupDisallowedPageShellTranslations() {
  const selectors = [PAGE_CHROME_SELECTOR, PAGE_TOOLBAR_SELECTOR]
    .filter(Boolean)
    .join(",");
  let containers = [];
  try {
    containers = Array.from(document.querySelectorAll(selectors));
  }
  catch {
    return;
  }

  for (const container of containers) {
    cleanupTranslationArtifactsWithin(container);
  }
}

function cleanupTranslationArtifactsWithin(root) {
  if (!root?.querySelectorAll) {
    return;
  }

  const artifacts = root.querySelectorAll(`.${MARKER_CLASS}, .${BILINGUAL_CLASS}, .${COMPACT_INLINE_CLASS}, .${COMPACT_STACKED_CLASS}, .${TRANSLATION_ONLY_CLASS}`);
  for (const artifact of Array.from(artifacts)) {
    artifact.remove();
  }

  const translatedBlocks = [];
  if (root.matches?.(`[${TRANSLATED_ATTR}]`)) {
    translatedBlocks.push(root);
  }
  translatedBlocks.push(...Array.from(root.querySelectorAll(`[${TRANSLATED_ATTR}]`)));

  for (const block of translatedBlocks) {
    const mode = block.getAttribute(TRANSLATED_ATTR);
    restoreBlock(block);
    if ((mode === "overlay" || mode === "compact") && block.hasAttribute("title")) {
      block.removeAttribute("title");
    }
  }
}

function startMutationObserver() {
  stopMutationObserver();

  const root = document.documentElement || document.body;
  if (!root) {
    return;
  }

  state.mutationObserver = new MutationObserver(handleMutationRecords);

  state.mutationObserver.observe(root, {
    childList: true,
    subtree: true,
    characterData: true,
    attributes: true,
    attributeFilter: [
      "class", "style", "hidden", "aria-hidden", "aria-expanded", "aria-selected",
      "data-state", "data-testid", "data-test", "data-test-id"
    ]
  });
  observeOpenShadowRoots(root);
}

function stopMutationObserver() {
  if (state.mutationObserver) {
    state.mutationObserver.disconnect();
    state.mutationObserver = null;
  }
  for (const observer of state.shadowMutationObservers.values()) {
    observer.disconnect();
  }
  state.shadowMutationObservers.clear();
}

function handleMutationRecords(records) {
  if (!state.enabled) {
    return;
  }

  let sawPageMutation = false;
  for (const record of records) {
    if (isVerbeamMutationRecord(record)) {
      continue;
    }

    const translatedBlock = getClosestTranslatedBlock(record.target);
    if (translatedBlock) {
      handleTranslatedBlockMutation(translatedBlock);
      continue;
    }

    if (record.type === "childList") {
      for (const node of record.addedNodes) {
        if (isVerbeamInjectedNode(node)) {
          continue;
        }

        const addedTranslatedBlock = getClosestTranslatedBlock(node);
        if (addedTranslatedBlock) {
          handleTranslatedBlockMutation(addedTranslatedBlock);
          sawPageMutation = true;
          continue;
        }

        if (!isObservableRoot(node)) {
          continue;
        }
        observeOpenShadowRoots(node);
        scheduleMutationScan(node);
        sawPageMutation = true;
      }

      if (record.addedNodes?.length > 0) {
        scheduleMutationScan(record.target);
        sawPageMutation = true;
      }

      if (record.removedNodes?.length > 0) {
        scheduleMutationScan(record.target);
        sawPageMutation = true;
      }
    }
    else if (record.type === "characterData") {
      scheduleMutationScan(record.target.parentElement || record.target.getRootNode?.());
      sawPageMutation = true;
    }
    else if (record.type === "attributes") {
      scheduleMutationScan(record.target);
      sawPageMutation = true;
    }
  }

  if (sawPageMutation) {
    scheduleAjaxActivityRescans();
  }
  scheduleOverlayPositionUpdate();
}

function observeOpenShadowRoots(root) {
  if (!isObservableRoot(root)) {
    return;
  }

  const observeElementShadow = (element) => {
    if (element?.shadowRoot) {
      startShadowMutationObserver(element.shadowRoot);
      observeOpenShadowRoots(element.shadowRoot);
    }
  };

  if (root.nodeType === Node.ELEMENT_NODE) {
    observeElementShadow(root);
  }

  const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
  let node = walker.nextNode();
  while (node) {
    observeElementShadow(node);
    node = walker.nextNode();
  }
}

function startShadowMutationObserver(shadowRoot) {
  if (!shadowRoot || state.shadowMutationObservers.has(shadowRoot)) {
    return;
  }

  const observer = new MutationObserver(handleMutationRecords);
  observer.observe(shadowRoot, {
    childList: true,
    subtree: true,
    characterData: true,
    attributes: true,
    attributeFilter: [
      "class", "style", "hidden", "aria-hidden", "aria-expanded", "aria-selected",
      "data-state", "data-testid", "data-test", "data-test-id"
    ]
  });
  state.shadowMutationObservers.set(shadowRoot, observer);
  scheduleMutationScan(shadowRoot);
}

function scheduleMutationScan(root) {
  if (!state.enabled || !root) {
    return;
  }

  const element = getMutationScanElement(root);
  if (!element || element.closest?.(`#${OVERLAY_ROOT_ID}`)) {
    return;
  }

  const ignored = element.closest?.(`[${IGNORE_ATTR}='true']`);
  if (ignored && !ignored.hasAttribute?.(TRANSLATED_ATTR)) {
    return;
  }

  state.pendingMutationRoots.add(root);
  if (state.mutationScanTimer) {
    return;
  }

  state.mutationScanTimer = setTimeout(() => {
    state.mutationScanTimer = null;
    flushMutationScans();
  }, MUTATION_SCAN_DELAY_MS);
}

function getClosestTranslatedBlock(node) {
  const element = getMutationScanElement(node);
  if (!element?.closest) {
    return null;
  }
  return element.closest(`[${TRANSLATED_ATTR}]`);
}

function isVerbeamInjectedNode(node) {
  const element = getMutationScanElement(node);
  if (!element?.closest) {
    return false;
  }
  return Boolean(element.closest(`#${OVERLAY_ROOT_ID},.${MARKER_CLASS},.${BILINGUAL_CLASS},.${TRANSLATION_ONLY_CLASS},.${OVERLAY_TRANSLATION_CLASS},.${COMPACT_INLINE_CLASS},.${COMPACT_STACKED_CLASS}`));
}

function isVerbeamMutationRecord(record) {
  if (record.type === "attributes") {
    const target = getMutationScanElement(record.target);
    if (!target) {
      return false;
    }
    if (target.closest?.(`#${OVERLAY_ROOT_ID},.${MARKER_CLASS},.${BILINGUAL_CLASS},.${TRANSLATION_ONLY_CLASS},.${OVERLAY_TRANSLATION_CLASS},.${COMPACT_INLINE_CLASS},.${COMPACT_STACKED_CLASS}`)) {
      return true;
    }
    if (
      target.hasAttribute?.(TRANSLATED_ATTR) &&
      [TRANSLATED_ATTR, IGNORE_ATTR, TRANSLATION_LAYOUT_ATTR, "data-verbeam-original-html", "data-verbeam-original"].includes(record.attributeName)
    ) {
      return true;
    }
    return false;
  }

  if (record.type !== "childList") {
    return isVerbeamInjectedNode(record.target);
  }

  const changedNodes = [...record.addedNodes, ...record.removedNodes];
  return changedNodes.length > 0 && changedNodes.every(isVerbeamInjectedNode);
}

function handleTranslatedBlockMutation(block) {
  if (!block || !block.isConnected) {
    return;
  }

  const blockState = state.blockStates.get(block);
  if (!blockState || blockState.inFlight || !hasTranslatedBlockSourceChanged(block, blockState)) {
    return;
  }

  restoreBlock(block);
  if (isElementTranslatableBlock(block)) {
    enqueueBlock(block);
  }
  else {
    scheduleMutationScan(block.parentElement || document.body);
  }
}

function hasTranslatedBlockSourceChanged(block, blockState) {
  const sourceText = extractTranslatedBlockSourceText(block);
  if (!sourceText && block.getAttribute(TRANSLATED_ATTR) === "translationOnly") {
    return false;
  }
  return sourceText !== blockState.originalText;
}

function extractTranslatedBlockSourceText(block) {
  const values = [];
  const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT, {
    acceptNode(node) {
      const parent = node.parentElement;
      if (!parent) {
        return NodeFilter.FILTER_REJECT;
      }
      const text = String(node.nodeValue || "").replace(/\s+/g, " ").trim();
      if (!text) {
        return NodeFilter.FILTER_REJECT;
      }
      if (parent.closest(`#${OVERLAY_ROOT_ID},.${MARKER_CLASS},.${BILINGUAL_CLASS},.${TRANSLATION_ONLY_CLASS},.${OVERLAY_TRANSLATION_CLASS},.${COMPACT_INLINE_CLASS},.${COMPACT_STACKED_CLASS}`)) {
        return NodeFilter.FILTER_REJECT;
      }
      if (SKIP_TAGS.has(parent.tagName)) {
        return NodeFilter.FILTER_REJECT;
      }
      return NodeFilter.FILTER_ACCEPT;
    }
  });

  let node = walker.nextNode();
  while (node) {
    values.push(node.nodeValue || "");
    node = walker.nextNode();
  }

  return values.join("").replace(/\s+/g, " ").trim();
}

function clearMutationScanTimer() {
  if (state.mutationScanTimer) {
    clearTimeout(state.mutationScanTimer);
    state.mutationScanTimer = null;
  }
}

function flushMutationScans() {
  if (!state.enabled || state.pendingMutationRoots.size === 0) {
    state.pendingMutationRoots.clear();
    return;
  }

  const roots = Array.from(state.pendingMutationRoots);
  state.pendingMutationRoots.clear();

  for (const root of roots) {
    scanTranslatableRoot(root);
  }
  updateStatusBadge();
}

function scanTranslatableRoot(root) {
  if (!state.enabled || !isObservableRoot(root) || !isRootConnected(root)) {
    return;
  }

  observeOpenShadowRoots(root);
  const blocks = collectTranslatableBlocks(root);
  for (const block of blocks) {
    observeBlock(block);
  }
  updateStatusBadge();
}

function scheduleHydrationScans() {
  clearHydrationScans();
  for (const delay of HYDRATION_RESCAN_DELAYS_MS) {
    const timer = window.setTimeout(() => {
      state.hydrationScanTimers.delete(timer);
      scanCurrentDocument();
    }, delay);
    state.hydrationScanTimers.add(timer);
  }
}

function clearHydrationScans() {
  for (const timer of state.hydrationScanTimers) {
    window.clearTimeout(timer);
  }
  state.hydrationScanTimers.clear();
}

function scheduleAjaxActivityRescans() {
  if (!state.enabled || !state.settings.watchAjax) {
    return;
  }

  const now = Date.now();
  if (!state.ajaxGlobalScanTimer && now - state.lastAjaxGlobalScanAt >= AJAX_GLOBAL_RESCAN_THROTTLE_MS) {
    state.lastAjaxGlobalScanAt = now;
    state.ajaxGlobalScanTimer = window.setTimeout(() => {
      state.ajaxGlobalScanTimer = null;
      scanCurrentDocument();
    }, MUTATION_SCAN_DELAY_MS);
  }

  clearAjaxSettleTimersOnly();
  for (const delay of AJAX_SETTLE_RESCAN_DELAYS_MS) {
    const timer = window.setTimeout(() => {
      state.ajaxSettleScanTimers.delete(timer);
      scanCurrentDocument();
    }, delay);
    state.ajaxSettleScanTimers.add(timer);
  }
}

function clearAjaxSettleScans() {
  clearAjaxSettleTimersOnly();
  if (state.ajaxGlobalScanTimer) {
    window.clearTimeout(state.ajaxGlobalScanTimer);
    state.ajaxGlobalScanTimer = null;
  }
  state.lastAjaxGlobalScanAt = 0;
}

function clearAjaxSettleTimersOnly() {
  for (const timer of state.ajaxSettleScanTimers) {
    window.clearTimeout(timer);
  }
  state.ajaxSettleScanTimers.clear();
}

function scanCurrentDocument() {
  scanTranslatableRoot(document.body || document.documentElement);
}

function getMutationScanElement(root) {
  if (root.nodeType === Node.ELEMENT_NODE) {
    return root;
  }
  if (root.nodeType === Node.DOCUMENT_FRAGMENT_NODE) {
    return root.host || null;
  }
  return root.parentElement || null;
}

function isObservableRoot(root) {
  return Boolean(root) &&
    (root.nodeType === Node.ELEMENT_NODE ||
      root.nodeType === Node.DOCUMENT_NODE ||
      root.nodeType === Node.DOCUMENT_FRAGMENT_NODE);
}

function isRootConnected(root) {
  if (root.nodeType === Node.DOCUMENT_NODE) {
    return true;
  }
  if (root.nodeType === Node.DOCUMENT_FRAGMENT_NODE) {
    return Boolean(root.host?.isConnected || root.isConnected);
  }
  return Boolean(root.isConnected);
}

function startIntersectionObserver() {
  stopIntersectionObserver();

  state.intersectionObserver = new IntersectionObserver((entries) => {
    if (!state.enabled) {
      return;
    }

    for (const entry of entries) {
      if (entry.isIntersecting) {
        const block = entry.target;
        state.observedBlocks.delete(block);
        state.intersectionObserver.unobserve(block);
        enqueueBlock(block);
      }
    }
  }, {
    root: null,
    rootMargin: PAGE_TRANSLATION_ROOT_MARGIN,
    threshold: 0.01
  });
}

function stopIntersectionObserver() {
  if (state.intersectionObserver) {
    state.intersectionObserver.disconnect();
    state.intersectionObserver = null;
  }
  state.observedBlocks = new WeakSet();
}

function observeBlock(block) {
  if (!state.intersectionObserver) {
    startIntersectionObserver();
  }

  if (block.getAttribute(TRANSLATED_ATTR) || isInsideTranslatedBlock(block)) {
    return;
  }

  if (!block.isConnected || state.observedBlocks.has(block)) {
    return;
  }

  if (!isElementTranslatableBlock(block)) {
    return;
  }

  if (isBlockNearViewport(block)) {
    enqueueBlock(block);
    return;
  }

  state.observedBlocks.add(block);
  state.intersectionObserver.observe(block);
}

function isBlockNearViewport(block) {
  const rect = block.getBoundingClientRect();
  if (rect.width <= 0 || rect.height <= 0) {
    return false;
  }

  const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 1024;
  const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 768;
  const margin = 600;
  return rect.bottom >= -margin &&
    rect.top <= viewportHeight + margin &&
    rect.right >= -margin &&
    rect.left <= viewportWidth + margin;
}

function isInsideTranslatedBlock(element) {
  const closest = element.closest(`[${TRANSLATED_ATTR}]`);
  return closest !== null && closest !== element;
}

function enqueueBlock(block) {
  if (!state.enabled) {
    return;
  }

  if (state.queuedBlocks.has(block)) {
    return;
  }

  if (!block.isConnected || block.getAttribute(TRANSLATED_ATTR) || !isElementTranslatableBlock(block)) {
    return;
  }

  const entry = {
    block,
    priority: getBlockQueuePriority(block),
    sequence: state.queueSequence += 1,
    itemId: createTraceItemId("block"),
    queuedAtUnixMs: Date.now()
  };
  const insertAt = state.queue.findIndex((item) =>
    item.priority > entry.priority ||
    (item.priority === entry.priority && item.sequence > entry.sequence));
  if (insertAt === -1) {
    state.queue.push(entry);
  }
  else {
    state.queue.splice(insertAt, 0, entry);
  }
  state.queuedBlocks.add(block);
  state.stats.queued = state.queue.length;
  drainQueue();
  updateStatusBadge();
}

function drainQueue() {
  const maxRunning = Math.max(1, Math.min(6, Number(state.settings.concurrency) || 2));
  while (state.running < maxRunning && state.queue.length > 0) {
    const entry = state.queue.shift();
    const block = entry?.block || entry;
    state.queuedBlocks.delete(block);
    state.stats.queued = state.queue.length;
    if (!block || !block.isConnected || block.getAttribute(TRANSLATED_ATTR) || !isElementTranslatableBlock(block)) {
      updateStatusBadge();
      continue;
    }

    state.running += 1;
    state.stats.running = state.running;
    state.stats.queued = state.queue.length;
    updateStatusBadge();

    const startedRunId = state.runId;
    translateBlock(block, entry)
      .catch((err) => {
        if (isExtensionContextInvalidated(err)) {
          handleExtensionContextInvalidated(err);
          return;
        }
        if (state.runId !== startedRunId || isAbortError(err)) {
          return;
        }
        console.error("[Verbeam] translateBlock failed:", err.message);
        state.stats.failed += 1;
      })
      .finally(() => {
        if (state.runId !== startedRunId) {
          return;
        }
        state.running = Math.max(0, state.running - 1);
        state.stats.running = state.running;
        state.stats.queued = state.queue.length;
        updateStatusBadge();
        drainQueue();
      });
  }
}

function getBlockQueuePriority(block) {
  const rect = block.getBoundingClientRect();
  if (rect.width <= 0 || rect.height <= 0) {
    return Number.MAX_SAFE_INTEGER;
  }

  const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 800;
  const visible = rect.bottom >= 0 && rect.top <= viewportHeight;
  if (visible) {
    return Math.max(0, rect.top);
  }

  const distance = rect.top > viewportHeight
    ? rect.top - viewportHeight
    : Math.abs(rect.bottom);
  return 100000 + distance;
}

async function translateBlock(block, queueEntry = null) {
  const runId = state.runId;
  if (!state.enabled || runId !== state.runId) {
    return;
  }

  if (block.getAttribute(TRANSLATED_ATTR)) {
    return;
  }

  const originalText = extractBlockText(block);
  if (!isTranslatableText(originalText)) {
    return;
  }
  const sourceFingerprint = getBlockFingerprint(block);

  const blockState = state.blockStates.get(block) || {};
  if (blockState.inFlight) {
    return;
  }

  blockState.originalText = originalText;
  blockState.sourceFingerprint = sourceFingerprint;
  blockState.inFlight = true;
  state.blockStates.set(block, blockState);

  let response;
  const requestId = createRequestId();
  const traceInfo = {
    traceId: state.currentTraceId,
    itemId: queueEntry?.itemId || createTraceItemId("block"),
    clientQueuedAtUnixMs: queueEntry?.queuedAtUnixMs || Date.now()
  };
  blockState.requestId = requestId;
  state.activeRequestIds.add(requestId);
  try {
    response = await requestTranslation(originalText, requestId, traceInfo);
    logTranslationTrace(response, traceInfo);
  }
  catch (error) {
    if (isExtensionContextInvalidated(error)) {
      handleExtensionContextInvalidated(error);
      return;
    }
    if (isAbortError(error)) {
      return;
    }
    throw error;
  }
  finally {
    state.activeRequestIds.delete(requestId);
    if (blockState.requestId === requestId) {
      delete blockState.requestId;
    }
    blockState.inFlight = false;
  }
  const translatedText = normalizeTranslatedText(response?.result);

  if (!state.enabled || runId !== state.runId) {
    return;
  }

  if (!block.isConnected) {
    return;
  }

  if (getBlockFingerprint(block) !== sourceFingerprint) {
    observeBlock(block);
    return;
  }

  if (!translatedText) {
    return;
  }

  if (block.getAttribute(TRANSLATED_ATTR)) {
    return;
  }

  blockState.translatedText = translatedText;
  renderTranslation(block, originalText, translatedText);
  state.stats.translated += 1;
}

function getBlockFingerprint(block) {
  return [
    extractBlockText(block),
    block.childElementCount,
    block.textContent?.length || 0
  ].join("\u001f");
}

function renderTranslation(block, originalText, translatedText) {
  if (shouldUseChromeOverlayTranslation(block)) {
    renderOverlayTranslation(block, translatedText);
    block.setAttribute(TRANSLATION_LAYOUT_ATTR, "chrome");
    return;
  }

  const displayMode = state.settings.displayMode === "translationOnly" ? "translationOnly" : "bilingual";

  if (displayMode === "bilingual") {
    renderBilingual(block, originalText, translatedText);
  }
  else {
    renderTranslationOnly(block, originalText, translatedText);
  }
}

function renderBilingual(block, _originalText, translatedText) {
  if (shouldUseCompactInlineTranslation(block)) {
    renderCompactInlineTranslation(block, translatedText);
    return;
  }

  if (shouldUseOverlayTranslation(block)) {
    renderOverlayTranslation(block, translatedText);
    return;
  }

  const marker = document.createElement("span");
  marker.className = MARKER_CLASS;
  marker.setAttribute(IGNORE_ATTR, "true");
  marker.setAttribute("aria-hidden", "true");
  marker.style.cssText = "display:block;height:0.5em;";

  const translationNode = document.createElement("span");
  translationNode.className = BILINGUAL_CLASS;
  translationNode.setAttribute(IGNORE_ATTR, "true");
  translationNode.setAttribute("dir", "auto");
  translationNode.textContent = translatedText;

  block.appendChild(marker);
  block.appendChild(translationNode);
  block.setAttribute(TRANSLATED_ATTR, "bilingual");
  block.setAttribute(TRANSLATION_LAYOUT_ATTR, "inline");
  block.setAttribute(IGNORE_ATTR, "true");
}

function renderCompactInlineTranslation(block, translatedText) {
  if (shouldUseStackedCompactTranslation(block)) {
    renderStackedCompactTranslation(block, translatedText);
    return;
  }

  renderOverlayTranslation(block, translatedText);
  block.setAttribute(TRANSLATION_LAYOUT_ATTR, "chrome");
}

function renderStackedCompactTranslation(block, translatedText) {
  const translationNode = document.createElement("span");
  translationNode.className = COMPACT_STACKED_CLASS;
  translationNode.setAttribute(IGNORE_ATTR, "true");
  translationNode.setAttribute("dir", "auto");
  translationNode.textContent = translatedText;

  const blockState = state.blockStates.get(block) || {};
  blockState.hadOriginalTitle = block.hasAttribute("title");
  blockState.originalTitle = block.getAttribute("title") || "";
  blockState.titleWasSetByVerbeam = true;
  state.blockStates.set(block, blockState);

  block.setAttribute("title", translatedText);
  block.appendChild(translationNode);
  block.setAttribute(TRANSLATED_ATTR, "compact");
  block.setAttribute(TRANSLATION_LAYOUT_ATTR, "stacked");
  block.setAttribute(IGNORE_ATTR, "true");
}

function renderOverlayTranslation(block, translatedText) {
  const overlayNode = document.createElement("span");
  overlayNode.className = OVERLAY_TRANSLATION_CLASS;
  overlayNode.setAttribute(IGNORE_ATTR, "true");
  overlayNode.setAttribute("dir", "auto");
  overlayNode.hidden = true;
  overlayNode.textContent = translatedText;

  const blockState = state.blockStates.get(block) || {};
  blockState.overlayNode = overlayNode;
  blockState.hadOriginalTitle = block.hasAttribute("title");
  blockState.originalTitle = block.getAttribute("title") || "";
  blockState.titleWasSetByVerbeam = true;
  state.blockStates.set(block, blockState);

  block.setAttribute("title", translatedText);
  block.setAttribute(TRANSLATED_ATTR, "overlay");
  block.setAttribute(TRANSLATION_LAYOUT_ATTR, "overlay");
  block.setAttribute(IGNORE_ATTR, "true");
  ensureOverlayRoot().appendChild(overlayNode);
  state.overlayBlocks.add(block);
}

function shouldUseOverlayTranslation(block) {
  const rect = block.getBoundingClientRect();
  const style = getComputedStyle(block);
  const display = style.display || "";
  const tag = block.tagName;
  const fontSize = Number.parseFloat(style.fontSize) || 16;

  if (["A", "BUTTON", "LABEL", "SPAN", "TD", "TH", "DT", "DD", "SUMMARY"].includes(tag)) {
    return true;
  }

  if (block.closest("button,[role='button'],[role='tab'],[role='menuitem'],[role='link'],[role='option'],[role='switch']")) {
    return true;
  }

  if (fontSize <= 20 && rect.height > 0 && rect.height <= getCompactLineHeightThreshold(style)) {
    return true;
  }

  if ((display.includes("inline") || display.includes("flex") || display.includes("grid")) && rect.height < 56) {
    return true;
  }

  return hasCompactClippingAncestor(block, rect);
}

function shouldUseCompactInlineTranslation(block) {
  if (!isCompactUiTranslationCandidate(block) && !isInsideSideRail(block)) {
    return false;
  }

  const text = extractBlockText(block);
  if (!isTranslatableText(text) || text.length > 120) {
    return false;
  }

  const rect = block.getBoundingClientRect();
  return rect.width > 0 && rect.height > 0 && rect.height <= 72;
}

function shouldUseStackedCompactTranslation(block) {
  if (!block || block.nodeType !== Node.ELEMENT_NODE) {
    return false;
  }

  if (shouldUseChromeOverlayTranslation(block)) {
    return false;
  }

  const rect = block.getBoundingClientRect();
  const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 1024;
  if (rect.width <= 0 || rect.height <= 0 || rect.top < 120) {
    return false;
  }

  const isLeftRail = rect.left < Math.min(360, viewportWidth * 0.25);
  const isRightRail = rect.left > viewportWidth * 0.62;
  const isNarrowRail = rect.width <= Math.min(420, viewportWidth * 0.34);
  return isNarrowRail && (isLeftRail || isRightRail);
}

function shouldUseChromeOverlayTranslation(block) {
  if (!shouldUseCompactInlineTranslation(block)) {
    return false;
  }

  return isInsideSideRail(block) ||
    isInsidePageChrome(block) ||
    isInsidePageToolbar(block) ||
    Boolean(block.closest("nav,[role='navigation'],[role='tablist']"));
}

function isCompactUiTranslationCandidate(element) {
  if (!element || element.nodeType !== Node.ELEMENT_NODE) {
    return false;
  }

  if (isPageActionControl(element)) {
    return false;
  }

  const tag = element.tagName;
  const role = (element.getAttribute("role") || "").toLowerCase();
  if (["A", "BUTTON"].includes(tag) || ["tab", "menuitem", "link", "button", "option"].includes(role)) {
    return true;
  }

  if (isInsideSideRail(element)) {
    return ["A", "SPAN", "DIV", "LI", "P", "H1", "H2", "H3", "H4", "H5", "H6"].includes(tag);
  }

  if (element.closest("nav,[role='navigation'],[role='tablist'],[aria-label*='table of contents' i],[data-testid*='table-of-contents' i]")) {
    return ["SPAN", "DIV", "LI", "P", "H1", "H2", "H3", "H4", "H5", "H6"].includes(tag);
  }

  return false;
}

function isPageActionControl(element) {
  if (!element || element.nodeType !== Node.ELEMENT_NODE) {
    return false;
  }

  const tag = element.tagName;
  const role = (element.getAttribute("role") || "").toLowerCase();
  const isControl = ["BUTTON", "SELECT", "INPUT"].includes(tag) ||
    ["button", "switch", "menuitem"].includes(role);
  if (!isControl) {
    return false;
  }

  const label = normalizeUiLabel(
    element.getAttribute("aria-label") ||
    element.getAttribute("title") ||
    extractBlockText(element)
  );
  if (label && PAGE_ACTION_LABEL_PATTERN.test(label)) {
    return true;
  }

  if (element.closest(PAGE_TOOLBAR_SELECTOR)) {
    return true;
  }

  const rect = element.getBoundingClientRect();
  const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 1024;
  return Boolean(
    label &&
    label.length <= 48 &&
    rect.top >= 0 &&
    rect.top < 180 &&
    rect.left > viewportWidth * 0.45
  );
}

function normalizeUiLabel(value) {
  return String(value || "")
    .replace(/\s+/g, " ")
    .trim()
    .toLowerCase();
}

function getCompactLineHeightThreshold(style) {
  const fontSize = Number.parseFloat(style.fontSize) || 16;
  const lineHeight = style.lineHeight === "normal"
    ? fontSize * 1.25
    : Number.parseFloat(style.lineHeight) || fontSize * 1.25;
  return Math.max(28, lineHeight * 1.65);
}

function hasCompactClippingAncestor(block, blockRect) {
  let node = block;
  while (node && node !== document.body && node !== document.documentElement) {
    const style = getComputedStyle(node);
    const overflow = `${style.overflow} ${style.overflowX} ${style.overflowY}`;
    if (/(hidden|clip|auto|scroll)/.test(overflow)) {
      const rect = node.getBoundingClientRect();
      if (node === block || rect.height < 72 || rect.height <= blockRect.height + 24) {
        return true;
      }
    }
    node = node.parentElement;
  }
  return false;
}

function scheduleOverlayPositionUpdate() {
  if (!state.activeOverlayBlock || state.overlayPositionFrame) {
    return;
  }

  state.overlayPositionFrame = window.requestAnimationFrame(() => {
    state.overlayPositionFrame = 0;
    updateOverlayTranslationPositions();
  });
}

function updateOverlayTranslationPositions() {
  const block = state.activeOverlayBlock;
  if (!block) {
    return;
  }

  const blockState = state.blockStates.get(block);
  if (!blockState?.overlayNode || !block.isConnected || !block.getAttribute(TRANSLATED_ATTR)) {
    hideOverlayTranslation(block);
    return;
  }

  updateOverlayTranslationPosition(block, blockState.overlayNode);
}

function updateOverlayTranslationPosition(block, overlayNode) {
  if (overlayNode.hidden) {
    return;
  }

  const rect = block.getBoundingClientRect();
  const viewportWidth = window.innerWidth || document.documentElement.clientWidth || 1024;
  const viewportHeight = window.innerHeight || document.documentElement.clientHeight || 768;

  if (rect.width <= 0 || rect.height <= 0 || rect.bottom < 0 || rect.top > viewportHeight) {
    overlayNode.hidden = true;
    return;
  }

  const minLeft = 8;
  const minTop = 8;
  const maxWidth = Math.min(420, Math.max(140, viewportWidth - minLeft * 2));
  const left = clamp(rect.left, minLeft, Math.max(minLeft, viewportWidth - minLeft - 140));
  const availableWidth = Math.max(96, Math.min(maxWidth, viewportWidth - left - minLeft));

  overlayNode.style.visibility = "hidden";
  overlayNode.style.left = `${left}px`;
  overlayNode.style.top = "0px";
  overlayNode.style.maxWidth = `${availableWidth}px`;

  const overlayHeight = overlayNode.offsetHeight || 24;
  const overlayWidth = overlayNode.offsetWidth || Math.min(availableWidth, maxWidth);
  if (block.getAttribute(TRANSLATION_LAYOUT_ATTR) === "chrome") {
    const gap = 8;
    const roomRight = viewportWidth - rect.right - minLeft;
    const roomLeft = rect.left - minLeft;
    const preferRight = rect.left < viewportWidth / 2;
    const canUseRight = roomRight >= Math.min(overlayWidth, 160);
    const canUseLeft = roomLeft >= Math.min(overlayWidth, 160);
    let sideLeft = null;
    let placement = "";

    if ((preferRight && canUseRight) || (!canUseLeft && canUseRight)) {
      sideLeft = Math.min(rect.right + gap, viewportWidth - minLeft - overlayWidth);
      placement = "right";
    }
    else if (canUseLeft) {
      sideLeft = Math.max(minLeft, rect.left - overlayWidth - gap);
      placement = "left";
    }

    if (sideLeft !== null) {
      const sideTop = clamp(
        rect.top + (rect.height - overlayHeight) / 2,
        minTop,
        Math.max(minTop, viewportHeight - overlayHeight - minTop)
      );
      overlayNode.dataset.placement = placement;
      overlayNode.style.left = `${sideLeft}px`;
      overlayNode.style.top = `${sideTop}px`;
      overlayNode.style.visibility = "visible";
      return;
    }
  }

  let top = rect.bottom + 3;
  let placement = "below";
  if (top + overlayHeight > viewportHeight - minTop && rect.top - overlayHeight - 3 >= minTop) {
    top = rect.top - overlayHeight - 3;
    placement = "above";
  }

  overlayNode.dataset.placement = placement;
  overlayNode.style.top = `${clamp(top, minTop, Math.max(minTop, viewportHeight - overlayHeight - minTop))}px`;
  overlayNode.style.visibility = "visible";
}

function handleOverlayPointerOver(event) {
  const block = getClosestOverlayBlock(event.target);
  if (block) {
    showOverlayTranslation(block);
  }
}

function handleOverlayPointerOut(event) {
  const block = getClosestOverlayBlock(event.target);
  if (!block) {
    return;
  }
  const related = event.relatedTarget;
  if (!related || !block.contains(related)) {
    hideOverlayTranslation(block);
  }
}

function handleOverlayFocusIn(event) {
  const block = getClosestOverlayBlock(event.target);
  if (block) {
    showOverlayTranslation(block);
  }
}

function handleOverlayFocusOut(event) {
  const block = getClosestOverlayBlock(event.target);
  if (block) {
    hideOverlayTranslation(block);
  }
}

function getClosestOverlayBlock(node) {
  const element = getMutationScanElement(node);
  if (!element?.closest) {
    return null;
  }
  const block = element.closest(`[${TRANSLATED_ATTR}="overlay"]`);
  return block && state.overlayBlocks.has(block) ? block : null;
}

function showOverlayTranslation(block) {
  const blockState = state.blockStates.get(block);
  if (!blockState?.overlayNode || !block.isConnected) {
    return;
  }

  hideOverlayTranslationsExcept(block);
  state.activeOverlayBlock = block;
  blockState.overlayNode.hidden = false;
  blockState.overlayNode.dataset.visible = "true";
  updateOverlayTranslationPosition(block, blockState.overlayNode);
}

function hideOverlayTranslation(block) {
  const blockState = state.blockStates.get(block);
  if (blockState?.overlayNode) {
    blockState.overlayNode.hidden = true;
    delete blockState.overlayNode.dataset.visible;
    blockState.overlayNode.style.visibility = "hidden";
  }
  if (state.activeOverlayBlock === block) {
    state.activeOverlayBlock = null;
  }
}

function hideOverlayTranslationsExcept(activeBlock) {
  for (const block of Array.from(state.overlayBlocks)) {
    if (block !== activeBlock) {
      hideOverlayTranslation(block);
    }
  }
}

function renderTranslationOnly(block, _originalText, translatedText) {
  const blockState = state.blockStates.get(block) || {};
  blockState.originalHtml = block.innerHTML;
  state.blockStates.set(block, blockState);

  block.dataset.verbeamOriginalHtml = block.innerHTML;
  block.innerHTML = "";

  const translationNode = document.createElement("span");
  translationNode.className = TRANSLATION_ONLY_CLASS;
  translationNode.setAttribute(IGNORE_ATTR, "true");
  translationNode.setAttribute("dir", "auto");
  translationNode.textContent = translatedText;

  block.appendChild(translationNode);
  block.setAttribute(TRANSLATED_ATTR, "translationOnly");
  block.setAttribute(IGNORE_ATTR, "true");
}

function requestTranslation(text, requestId, traceInfo = null) {
  const cacheKey = translationCacheKey(text);
  const cached = state.translationCache.get(cacheKey);
  if (cached) {
    return Promise.resolve({ result: cached, cacheHit: true });
  }

  const context = state.pageContext || collectPageContext(state.settings);
  const skipMemoryContext = (state.settings.contextMode || "fast") === "fast";
  return sendRuntimeMessage({
    type: "verbeam:translateText",
    payload: {
      backendUrl: state.settings.backendUrl,
      requestId,
      text,
      source: state.settings.source,
      target: state.settings.target,
      provider: state.settings.provider,
      model: state.settings.model,
      mode: state.settings.mode,
      profile: state.settings.profile,
      sessionId: skipMemoryContext ? "" : location.href,
      skipMemoryContext,
      traceId: traceInfo?.traceId || "",
      itemId: traceInfo?.itemId || "",
      clientQueuedAtUnixMs: traceInfo?.clientQueuedAtUnixMs || undefined,
      clientRequestStartedAtUnixMs: Date.now(),
      webTitle: context.webTitle,
      webSummary: context.webSummary,
      webContent: context.webContent
    }
  }).then((response) => {
    const translated = normalizeTranslatedText(response?.result);
    if (translated) {
      state.translationCache.set(cacheKey, translated);
      while (state.translationCache.size > 200) {
        state.translationCache.delete(state.translationCache.keys().next().value);
      }
    }
    return response;
  });
}

function createRequestId() {
  state.requestSequence += 1;
  return `${Date.now().toString(36)}-${state.requestSequence}`;
}

function createTraceId() {
  state.traceSequence += 1;
  const host = location.hostname || "page";
  return `web-${Date.now().toString(36)}-${state.traceSequence}-${host}`;
}

function createTraceItemId(prefix) {
  state.traceSequence += 1;
  return `${prefix}-${Date.now().toString(36)}-${state.traceSequence}`;
}

function logTranslationTrace(response, traceInfo) {
  const backendTrace = response?.performanceTrace;
  const extensionTrace = response?.extensionTrace;
  if (!backendTrace && !extensionTrace) {
    return;
  }

  console.debug("[Verbeam] translation trace:", {
    traceId: traceInfo?.traceId || backendTrace?.traceId,
    itemId: traceInfo?.itemId || backendTrace?.itemId,
    extensionTrace,
    backendTrace
  });
}

function abortActiveRequests() {
  const requestIds = Array.from(state.activeRequestIds);
  state.activeRequestIds.clear();
  if (state.runtimeInvalidated) {
    return;
  }
  for (const requestId of requestIds) {
    sendRuntimeMessage({
      type: "verbeam:cancelTranslateText",
      requestId
    }).catch(() => {
      // The page may be navigating or the extension may be unloading; stale
      // responses are still guarded by runId and source fingerprints.
    });
  }
}

function isAbortError(error) {
  const message = String(error?.message || error || "").toLowerCase();
  return state.runtimeInvalidated ||
    error?.name === "AbortError" ||
    message.includes("aborted") ||
    message.includes("canceled") ||
    message.includes("cancelled");
}

function isExtensionContextInvalidated(error) {
  const message = String(error?.message || error || "").toLowerCase();
  return message.includes("extension context invalidated") ||
    message.includes("context invalidated") ||
    message.includes("extension context") ||
    message.includes("invalid extension context");
}

function handleExtensionContextInvalidated(error) {
  if (state.runtimeInvalidated) {
    return;
  }

  state.runtimeInvalidated = true;
  state.enabled = false;
  state.runId += 1;
  state.currentTraceId = "";
  state.activeRequestIds.clear();
  state.queue = [];
  state.queuedBlocks = new WeakSet();
  state.observedBlocks = new WeakSet();
  state.pendingMutationRoots.clear();
  state.running = 0;
  state.stats.queued = 0;
  state.stats.running = 0;

  stopRouteWatcher();
  stopMutationObserver();
  stopIntersectionObserver();
  clearMutationScanTimer();
  clearHydrationScans();
  clearAjaxSettleScans();
  updateStatusBadge();

  console.warn("[Verbeam] extension context invalidated; refresh this page to continue translating.", error?.message || error);
}

function translationCacheKey(text) {
  return [
    state.settings.source,
    state.settings.target,
    state.settings.provider,
    state.settings.model,
    state.settings.mode,
    state.settings.contextMode,
    text
  ].join("\u001f");
}

function collectTranslatableBlocks(root) {
  const blocks = [];
  collectTranslatableBlocksFromRoot(root, blocks, new WeakSet());
  return blocks;
}

function collectTranslatableBlocksFromRoot(root, blocks, seen) {
  if (!isObservableRoot(root)) {
    return;
  }

  maybePushTranslatableBlock(root, blocks, seen);

  if (root.nodeType === Node.ELEMENT_NODE && root.shadowRoot) {
    collectTranslatableBlocksFromRoot(root.shadowRoot, blocks, seen);
  }

  const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
  let node = walker.nextNode();
  while (node) {
    maybePushTranslatableBlock(node, blocks, seen);
    if (node.shadowRoot) {
      collectTranslatableBlocksFromRoot(node.shadowRoot, blocks, seen);
    }
    node = walker.nextNode();
  }
}

function maybePushTranslatableBlock(node, blocks, seen) {
  if (node.nodeType !== Node.ELEMENT_NODE || seen.has(node)) {
    return;
  }
  seen.add(node);
  if (isElementTranslatableBlock(node)) {
    blocks.push(node);
  }
}

function isElementTranslatableBlock(element) {
  if (!element || element.nodeType !== Node.ELEMENT_NODE) {
    return false;
  }

  if (element.hasAttribute(TRANSLATED_ATTR)) {
    return false;
  }

  if (element.closest(`[${IGNORE_ATTR}='true'],[${TRANSLATED_ATTR}]`)) {
    return false;
  }

  if (element.isContentEditable) {
    return false;
  }

  if (isIconGlyphElement(element)) {
    return false;
  }

  const isCompactCandidate = isCompactUiTranslationCandidate(element);
  if (isInsidePageShell(element) && !isCompactCandidate) {
    return false;
  }

  if (isPageActionControl(element)) {
    return false;
  }

  if (SKIP_TAGS.has(element.tagName)) {
    return false;
  }

  const style = getComputedStyle(element);
  if (style.display === "none" || style.visibility === "hidden" || Number(style.opacity) === 0) {
    return false;
  }

  if (isCompactCandidate) {
    const text = extractBlockText(element);
    return isTranslatableText(text) && text.length <= 120;
  }

  const isBlockTag = BLOCK_TAGS.has(element.tagName);
  const hasBlockChildren = Array.from(element.children).some(child => isBlockLevel(child));

  if (isBlockTag && !hasBlockChildren) {
    const text = extractBlockText(element);
    return isTranslatableText(text);
  }

  if ((element.tagName === "DIV" || element.tagName === "SPAN") && !hasBlockChildren) {
    const text = extractBlockText(element);
    if (!isTranslatableText(text)) {
      return false;
    }

    const display = style.display;
    if (display.includes("block") || display.includes("flex") || display.includes("grid") || display.includes("list-item")) {
      return true;
    }
  }

  return false;
}

function isBlockLevel(element) {
  if (!element || element.nodeType !== Node.ELEMENT_NODE) {
    return false;
  }

  if (BLOCK_TAGS.has(element.tagName)) {
    return true;
  }

  const display = getComputedStyle(element).display;
  return display.includes("block") || display.includes("flex") || display.includes("grid") || display.includes("list-item") || display.includes("table");
}

function extractBlockText(element) {
  if (!element) {
    return "";
  }

  const values = [];
  const walker = document.createTreeWalker(
    element,
    NodeFilter.SHOW_TEXT,
    {
      acceptNode: (node) => {
        const parent = node.parentElement;
        if (!parent) {
          return NodeFilter.FILTER_REJECT;
        }
        if (SKIP_TAGS.has(parent.tagName)) {
          return NodeFilter.FILTER_REJECT;
        }
        if (isIconGlyphElement(parent)) {
          return NodeFilter.FILTER_REJECT;
        }
        if (parent.closest("[aria-hidden='true']")) {
          return NodeFilter.FILTER_REJECT;
        }
        if (parent.closest(`[${IGNORE_ATTR}='true']`)) {
          return NodeFilter.FILTER_REJECT;
        }
        return NodeFilter.FILTER_ACCEPT;
      }
    }
  );

  let node = walker.nextNode();
  while (node) {
    values.push(node.nodeValue || "");
    node = walker.nextNode();
  }

  return values.join("").replace(/\s+/g, " ").trim();
}

function isTranslatableText(text) {
  const value = String(text || "").trim();
  if (value.length < 2) {
    return false;
  }

  if (/^[\d\s\-+.,:;()[\]{}"'`~!@#$%^&*=_/\\|<>]+$/.test(value)) {
    return false;
  }

  if (/^https?:\/\//i.test(value)) {
    return false;
  }

  return /[\p{L}\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Hangul}]/u.test(value);
}

function normalizeTranslatedText(value) {
  return String(value || "").replace(/\s+/g, " ").trim();
}

function collectPageContext(settings = state.settings) {
  const contextMode = settings.contextMode || "fast";
  if (contextMode === "fast") {
    return {
      webTitle: "",
      webSummary: "",
      webContent: ""
    };
  }

  const maxCharacters = contextMode === "full"
    ? 6000
    : contextMode === "balanced"
      ? 1200
      : 0;
  return {
    webTitle: document.title || location.hostname,
    webSummary: getMetaContent("description") || getMetaContent("og:description"),
    webContent: maxCharacters > 0 ? collectVisibleText(maxCharacters) : ""
  };
}

function collectVisibleText(maxCharacters) {
  if (!document.body) {
    return "";
  }

  const values = [];
  let total = 0;
  const walker = document.createTreeWalker(
    document.body,
    NodeFilter.SHOW_TEXT,
    {
      acceptNode: (node) => {
        const parent = node.parentElement;
        if (!parent) {
          return NodeFilter.FILTER_REJECT;
        }
        if (shouldSkipElement(parent)) {
          return NodeFilter.FILTER_REJECT;
        }
        const text = node.nodeValue || "";
        return isTranslatableText(text)
          ? NodeFilter.FILTER_ACCEPT
          : NodeFilter.FILTER_REJECT;
      }
    }
  );

  let node = walker.nextNode();
  while (node && total < maxCharacters) {
    const text = (node.nodeValue || "").trim();
    if (text) {
      const remaining = maxCharacters - total;
      values.push(text.slice(0, remaining));
      total += text.length + 1;
    }
    node = walker.nextNode();
  }

  return values.join("\n").slice(0, maxCharacters);
}

function shouldSkipElement(element) {
  if (!element) {
    return true;
  }

  if (element.closest(`#${STATUS_BADGE_ID},[${IGNORE_ATTR}='true']`)) {
    return true;
  }

  if (element.closest("[hidden],[aria-hidden='true']")) {
    return true;
  }

  if (element.isContentEditable) {
    return true;
  }

  if (isIconGlyphElement(element) || isInsidePageShell(element)) {
    return true;
  }

  if (SKIP_TAGS.has(element.tagName)) {
    return true;
  }

  const style = getComputedStyle(element);
  return style.display === "none" ||
    style.visibility === "hidden" ||
    Number(style.opacity) === 0;
}

function isInsidePageChrome(element) {
  if (!element?.closest) {
    return false;
  }

  try {
    return Boolean(element.closest(PAGE_CHROME_SELECTOR));
  }
  catch {
    return false;
  }
}

function isInsidePageShell(element) {
  if (!element?.closest) {
    return false;
  }

  return isInsidePageChrome(element) ||
    isInsidePageToolbar(element);
}

function isInsidePageToolbar(element) {
  if (!element?.closest) {
    return false;
  }

  let toolbar = null;
  try {
    toolbar = element.closest(PAGE_TOOLBAR_SELECTOR);
  }
  catch {
    return false;
  }
  if (!toolbar) {
    return false;
  }

  if (toolbar.closest("article,[role='article']")) {
    return false;
  }

  const rect = toolbar.getBoundingClientRect();
  return rect.top < 260 ||
    toolbar.matches?.("[role='banner'],[role='toolbar']") ||
    toolbar.getAttribute("aria-label")?.toLowerCase().includes("toolbar");
}

function isInsideSideRail(element) {
  if (!element?.closest) {
    return false;
  }

  try {
    return Boolean(element.closest(SIDE_RAIL_SELECTOR));
  }
  catch {
    return false;
  }
}

function isIconGlyphElement(element) {
  if (!element || element.nodeType !== Node.ELEMENT_NODE) {
    return false;
  }

  try {
    if (element.closest(ICON_GLYPH_SELECTOR)) {
      return true;
    }
  }
  catch {
    return false;
  }

  const text = String(element.textContent || "").replace(/\s+/g, " ").trim();
  if (!text || text.length > 48 || !ICON_LIGATURE_PATTERN.test(text)) {
    return false;
  }

  const className = getElementClassText(element);
  if (/material[-_ ]?(icon|symbol)s?/i.test(className)) {
    return true;
  }

  const fontFamily = getComputedStyle(element).fontFamily || "";
  return /Material Icons|Material Symbols/i.test(fontFamily);
}

function getElementClassText(element) {
  const className = element.className;
  if (typeof className === "string") {
    return className;
  }
  if (className?.baseVal) {
    return String(className.baseVal);
  }
  return "";
}

function getMetaContent(name) {
  const escaped = cssEscape(name);
  const meta = document.querySelector(`meta[name="${escaped}"],meta[property="${escaped}"]`);
  return meta?.getAttribute("content")?.trim() || "";
}

function cssEscape(value) {
  if (typeof CSS !== "undefined" && typeof CSS.escape === "function") {
    return CSS.escape(value);
  }
  return String(value).replace(/"/g, '\\"');
}

function ensurePageStyles() {
  const styleId = "verbeam-web-translator-styles";
  if (document.getElementById(styleId)) {
    return;
  }

  const style = document.createElement("style");
  style.id = styleId;
  style.setAttribute(IGNORE_ATTR, "true");
  style.textContent = `
    .verbeam-bilingual {
      display: block;
      box-sizing: border-box;
      width: fit-content;
      max-width: 100%;
      margin-top: 0.35em;
      padding: 0.35em 0.5em;
      background-color: rgba(17, 24, 39, 0.06);
      border-left: 3px solid rgba(17, 24, 39, 0.25);
      border-radius: 0 4px 4px 0;
      color: inherit;
      font-size: 0.95em;
      line-height: 1.5;
      overflow-wrap: anywhere;
      white-space: normal;
    }
    .verbeam-translation-only {
      display: inline;
      color: inherit;
    }
    .verbeam-bilingual-marker {
      display: block;
      height: 0.5em;
    }
    .verbeam-compact-inline {
      display: inline-block;
      box-sizing: border-box;
      max-width: min(12rem, 100%);
      margin-left: 0.35em;
      padding: 1px 4px;
      vertical-align: baseline;
      border-left: 2px solid rgba(23, 32, 51, 0.22);
      border-radius: 0 4px 4px 0;
      background: rgba(17, 24, 39, 0.06);
      color: #4b5563;
      font: 600 11px/1.25 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      pointer-events: none;
    }
    .verbeam-compact-stacked {
      display: block;
      box-sizing: border-box;
      max-width: 100%;
      margin-top: 2px;
      padding: 1px 0 1px 6px;
      border-left: 2px solid rgba(23, 32, 51, 0.22);
      color: #5b6472;
      font: 600 11px/1.3 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      white-space: normal;
      overflow-wrap: anywhere;
      pointer-events: none;
    }
    .verbeam-overlay-translation {
      position: absolute;
      display: block;
      box-sizing: border-box;
      z-index: 2147483647;
      pointer-events: none;
      padding: 3px 7px;
      background: rgba(249, 250, 251, 0.96);
      border-left: 3px solid rgba(23, 32, 51, 0.28);
      border-radius: 0 5px 5px 0;
      box-shadow: 0 6px 18px rgba(15, 23, 42, 0.16);
      color: #1f2937;
      font: 600 12px/1.35 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      overflow-wrap: anywhere;
      white-space: normal;
      opacity: 0.97;
    }
    .verbeam-overlay-translation[hidden] {
      display: none !important;
    }
    .verbeam-overlay-translation[data-placement="above"] {
      box-shadow: 0 -4px 16px rgba(15, 23, 42, 0.12);
    }
    #${OVERLAY_ROOT_ID} {
      position: fixed;
      inset: 0;
      z-index: 2147483647;
      pointer-events: none;
      font: 13px/1.4 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      color: #172033;
    }
    #${FLOATING_BUTTON_ID} {
      position: fixed;
      top: 45vh;
      width: 48px;
      height: 48px;
      pointer-events: auto;
      user-select: none;
      z-index: 2147483647;
    }
    #${FLOATING_BUTTON_ID}[data-side="right"] {
      right: 0;
    }
    #${FLOATING_BUTTON_ID}[data-side="left"] {
      left: 0;
    }
    #${FLOATING_BUTTON_ID}.verbeam-floating-dragging {
      right: auto;
      left: 0;
      cursor: grabbing;
    }
    .verbeam-floating-actions {
      position: absolute;
      bottom: 56px;
      display: flex;
      flex-direction: column;
      gap: 6px;
      opacity: 0;
      transform: translateY(6px);
      pointer-events: none;
      transition: opacity 0.16s ease, transform 0.16s ease;
    }
    #${FLOATING_BUTTON_ID}[data-side="right"] .verbeam-floating-actions {
      right: 6px;
      align-items: flex-end;
    }
    #${FLOATING_BUTTON_ID}[data-side="left"] .verbeam-floating-actions {
      left: 6px;
      align-items: flex-start;
    }
    #${FLOATING_BUTTON_ID}.verbeam-floating-expanded .verbeam-floating-actions,
    #${FLOATING_BUTTON_ID}.verbeam-floating-dragging .verbeam-floating-actions {
      opacity: 1;
      transform: translateY(0);
      pointer-events: auto;
    }
    #${FLOATING_BUTTON_ID}[data-side="right"]:not(.verbeam-floating-expanded):not(.verbeam-floating-dragging) {
      transform: translateX(29px);
    }
    #${FLOATING_BUTTON_ID}[data-side="left"]:not(.verbeam-floating-expanded):not(.verbeam-floating-dragging) {
      transform: translateX(-29px);
    }
    .verbeam-floating-main,
    .verbeam-floating-action {
      box-sizing: border-box;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      cursor: pointer;
      font: 700 12px/1 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      transition: opacity 0.16s ease, background 0.16s ease, transform 0.16s ease;
    }
    .verbeam-floating-main {
      position: relative;
      width: 48px;
      height: 48px;
      border: 1px solid rgba(255, 255, 255, 0.42);
      background: rgba(15, 23, 42, 0.92);
      box-shadow: 0 14px 34px rgba(15, 23, 42, 0.24);
      touch-action: none;
    }
    #${FLOATING_BUTTON_ID}[data-side="right"] .verbeam-floating-main {
      border-radius: 16px 0 0 16px;
      padding-right: 13px;
    }
    #${FLOATING_BUTTON_ID}[data-side="left"] .verbeam-floating-main {
      border-radius: 0 16px 16px 0;
      padding-left: 13px;
    }
    .verbeam-floating-logo {
      display: block;
      width: 30px;
      height: 30px;
      border-radius: 9px;
      object-fit: contain;
      pointer-events: none;
    }
    .verbeam-floating-state {
      position: absolute;
      bottom: 8px;
      width: 7px;
      height: 7px;
      border-radius: 999px;
      background: #94a3b8;
      box-shadow: 0 0 0 2px rgba(15, 23, 42, 0.92);
    }
    #${FLOATING_BUTTON_ID}[data-side="right"] .verbeam-floating-state {
      left: 9px;
    }
    #${FLOATING_BUTTON_ID}[data-side="left"] .verbeam-floating-state {
      right: 9px;
    }
    .verbeam-floating-action {
      min-width: 82px;
      height: 32px;
      justify-content: flex-start;
      border: 1px solid rgba(23, 32, 51, 0.12);
      border-radius: 8px;
      background: rgba(255, 255, 255, 0.96);
      color: #172033;
      box-shadow: 0 10px 26px rgba(15, 23, 42, 0.18);
      padding: 0 10px;
      white-space: nowrap;
    }
    .verbeam-floating-main:hover {
      background: rgba(30, 41, 59, 0.96);
      transform: translateY(-1px);
    }
    .verbeam-floating-action:hover {
      background: #f3f6fb;
      transform: translateY(-1px);
    }
    #${FLOATING_BUTTON_ID}.verbeam-floating-active .verbeam-floating-main {
      background: #2563eb;
      border-color: rgba(255, 255, 255, 0.54);
    }
    #${FLOATING_BUTTON_ID}.verbeam-floating-active .verbeam-floating-state {
      background: #22c55e;
    }
    [data-verbeam-translated="bilingual"] .verbeam-bilingual {
      animation: verbeam-fade-in 0.2s ease-out;
    }
    [data-verbeam-translated="translationOnly"] .verbeam-translation-only {
      animation: verbeam-fade-in 0.2s ease-out;
    }
    [data-verbeam-translated="compact"] .verbeam-compact-inline {
      animation: verbeam-fade-in 0.2s ease-out;
    }
    [data-verbeam-translated="compact"] .verbeam-compact-stacked {
      animation: verbeam-fade-in 0.2s ease-out;
    }
    @keyframes verbeam-fade-in {
      from { opacity: 0; }
      to { opacity: 1; }
    }
  `;

  const target = document.head || document.documentElement;
  target.appendChild(style);
}

function ensureOverlayRoot() {
  if (state.overlayRoot?.isConnected) {
    return state.overlayRoot;
  }

  let root = document.getElementById(OVERLAY_ROOT_ID);
  if (!root) {
    root = document.createElement("div");
    root.id = OVERLAY_ROOT_ID;
    root.setAttribute(IGNORE_ATTR, "true");
    root.setAttribute("aria-live", "polite");
    const target = document.documentElement || document.body;
    target.appendChild(root);
  }

  state.overlayRoot = root;
  return root;
}

function ensureStatusBadge() {
  if (!isTopFrame()) {
    return;
  }

  if (document.getElementById(STATUS_BADGE_ID)) {
    return;
  }

  const badge = document.createElement("div");
  badge.id = STATUS_BADGE_ID;
  badge.setAttribute(IGNORE_ATTR, "true");
  badge.style.cssText = [
    "position:fixed",
    "right:12px",
    "bottom:12px",
    "z-index:2147483647",
    "font:12px/1.4 system-ui,-apple-system,Segoe UI,sans-serif",
    "background:#111827",
    "color:#f9fafb",
    "border:1px solid rgba(255,255,255,.16)",
    "border-radius:6px",
    "box-shadow:0 8px 24px rgba(0,0,0,.22)",
    "padding:6px 8px",
    "max-width:260px",
    "pointer-events:none"
  ].join(";");
  ensureOverlayRoot().appendChild(badge);
}

function updateStatusBadge() {
  if (!isTopFrame()) {
    return;
  }

  const badge = document.getElementById(STATUS_BADGE_ID);
  if (!badge) {
    return;
  }

  if (state.runtimeInvalidated) {
    badge.textContent = "Verbeam updated. Refresh this page to continue.";
    return;
  }

  if (!state.enabled && state.stats.translated === 0) {
    badge.remove();
    return;
  }

  const pending = state.stats.queued + state.stats.running;
  badge.textContent = pending > 0
    ? `Verbeam translating: ${state.stats.translated} done, ${pending} pending`
    : `Verbeam translated: ${state.stats.translated} done${state.stats.failed ? `, ${state.stats.failed} failed` : ""}`;
}

function syncFloatingButton() {
  if (!isTopFrame() || !state.settings.floatingButtonEnabled || isCurrentSiteDisabled()) {
    removeFloatingButton();
    return;
  }

  ensurePageStyles();
  const root = ensureOverlayRoot();
  let button = document.getElementById(FLOATING_BUTTON_ID);
  if (!button) {
    button = createFloatingButton();
    root.appendChild(button);
  }

  state.floatingButton = button;
  positionFloatingButton(button);
  updateFloatingButtonState();
}

function createFloatingButton() {
  const container = document.createElement("div");
  container.id = FLOATING_BUTTON_ID;
  container.setAttribute(IGNORE_ATTR, "true");
  container.addEventListener("mouseenter", () => {
    container.classList.add("verbeam-floating-expanded");
  });
  container.addEventListener("mouseleave", () => {
    if (!state.floatingPointer) {
      container.classList.remove("verbeam-floating-expanded");
    }
  });

  const actions = document.createElement("div");
  actions.className = "verbeam-floating-actions";
  actions.setAttribute(IGNORE_ATTR, "true");

  actions.appendChild(createFloatingAction("Restore", "Restore original text", () => {
    restorePage();
  }));
  actions.appendChild(createFloatingAction("Lock", "Lock floating button", () => {
    persistSettingsPatch({ floatingButtonLocked: !state.settings.floatingButtonLocked });
  }, "lock"));
  actions.appendChild(createFloatingAction("Hide", "Hide floating button on this site", () => {
    disableFloatingButtonForCurrentSite();
  }));

  const main = document.createElement("button");
  main.type = "button";
  main.className = "verbeam-floating-main";
  main.setAttribute(IGNORE_ATTR, "true");
  main.appendChild(createFloatingBrandLogo());
  const stateDot = document.createElement("span");
  stateDot.className = "verbeam-floating-state";
  stateDot.setAttribute(IGNORE_ATTR, "true");
  main.appendChild(stateDot);
  main.addEventListener("pointerdown", handleFloatingPointerDown);
  main.addEventListener("pointermove", handleFloatingPointerMove);
  main.addEventListener("pointerup", (event) => finishFloatingPointer(event, true));
  main.addEventListener("pointercancel", (event) => finishFloatingPointer(event, false));

  container.appendChild(actions);
  container.appendChild(main);
  return container;
}

function createFloatingBrandLogo() {
  const logo = document.createElement("img");
  logo.className = "verbeam-floating-logo";
  logo.src = getExtensionAssetUrl("assets/verbeam-icon-32.png");
  logo.alt = "";
  logo.decoding = "async";
  logo.loading = "eager";
  logo.setAttribute("aria-hidden", "true");
  logo.setAttribute(IGNORE_ATTR, "true");
  return logo;
}

function getExtensionAssetUrl(path) {
  if (typeof chrome !== "undefined" && chrome.runtime && typeof chrome.runtime.getURL === "function") {
    return chrome.runtime.getURL(path);
  }
  return path;
}

function createFloatingAction(label, title, onClick, actionName = "") {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "verbeam-floating-action";
  if (actionName) {
    button.dataset.action = actionName;
  }
  button.textContent = label;
  button.title = title;
  button.setAttribute("aria-label", title);
  button.setAttribute(IGNORE_ATTR, "true");
  button.addEventListener("pointerdown", (event) => {
    event.stopPropagation();
  });
  button.addEventListener("click", (event) => {
    event.preventDefault();
    event.stopPropagation();
    onClick();
  });
  return button;
}

function handleFloatingPointerDown(event) {
  if (event.pointerType === "mouse" && event.button !== 0) {
    return;
  }

  const container = state.floatingButton;
  if (!container) {
    return;
  }

  const rect = container.getBoundingClientRect();
  state.floatingPointer = {
    pointerId: event.pointerId,
    startX: event.clientX,
    startY: event.clientY,
    offsetX: event.clientX - rect.left,
    offsetY: event.clientY - rect.top,
    width: rect.width || 48,
    height: rect.height || 48,
    activeDrag: false,
    timer: window.setTimeout(() => {
      startFloatingDrag(event.pointerId);
    }, FLOATING_LONG_PRESS_MS)
  };

  if (typeof event.currentTarget.setPointerCapture === "function") {
    event.currentTarget.setPointerCapture(event.pointerId);
  }
  event.preventDefault();
}

function handleFloatingPointerMove(event) {
  const pointer = state.floatingPointer;
  if (!pointer || pointer.pointerId !== event.pointerId) {
    return;
  }

  const distance = Math.hypot(event.clientX - pointer.startX, event.clientY - pointer.startY);
  if (!pointer.activeDrag && distance > FLOATING_DRAG_START_DISTANCE_PX) {
    startFloatingDrag(event.pointerId);
  }

  if (pointer.activeDrag) {
    moveFloatingDrag(event.clientX, event.clientY);
  }
}

function finishFloatingPointer(event, shouldToggle) {
  const pointer = state.floatingPointer;
  if (!pointer || pointer.pointerId !== event.pointerId) {
    return;
  }

  window.clearTimeout(pointer.timer);
  state.floatingPointer = null;

  if (typeof event.currentTarget.releasePointerCapture === "function") {
    event.currentTarget.releasePointerCapture(event.pointerId);
  }

  const container = state.floatingButton;
  if (!container) {
    return;
  }

  if (pointer.activeDrag) {
    container.classList.remove("verbeam-floating-dragging");
    const rect = container.getBoundingClientRect();
    const centerX = rect.left + rect.width / 2;
    const side = centerX < window.innerWidth / 2 ? "left" : "right";
    const position = clamp(rect.top / Math.max(1, window.innerHeight), FLOATING_TOP_MIN, FLOATING_TOP_MAX);
    persistSettingsPatch({
      floatingButtonSide: side,
      floatingButtonPosition: position
    });
    return;
  }

  if (shouldToggle) {
    togglePageTranslationFromFloatingButton();
  }
}

function startFloatingDrag(pointerId) {
  const pointer = state.floatingPointer;
  const container = state.floatingButton;
  if (!pointer || pointer.pointerId !== pointerId || !container || state.settings.floatingButtonLocked) {
    return;
  }

  pointer.activeDrag = true;
  container.classList.add("verbeam-floating-dragging", "verbeam-floating-expanded");
  const rect = container.getBoundingClientRect();
  container.style.left = `${rect.left}px`;
  container.style.right = "auto";
  container.style.top = `${rect.top}px`;
  container.style.transform = "none";
}

function moveFloatingDrag(clientX, clientY) {
  const pointer = state.floatingPointer;
  const container = state.floatingButton;
  if (!pointer || !container) {
    return;
  }

  const maxLeft = Math.max(0, window.innerWidth - pointer.width);
  const maxTop = Math.max(0, window.innerHeight - pointer.height);
  const left = clamp(clientX - pointer.offsetX, 0, maxLeft);
  const top = clamp(clientY - pointer.offsetY, 0, maxTop);
  container.style.left = `${left}px`;
  container.style.right = "auto";
  container.style.top = `${top}px`;
}

function togglePageTranslationFromFloatingButton() {
  if (state.enabled) {
    restorePage();
    return;
  }

  translatePage(state.settings).catch((error) => {
    console.error("[Verbeam] floating translate failed:", error);
  });
}

function disableFloatingButtonForCurrentSite() {
  const hostname = location.hostname;
  if (!hostname) {
    persistSettingsPatch({ floatingButtonEnabled: false });
    return;
  }

  const patterns = new Set(state.settings.disabledSitePatterns || []);
  patterns.add(hostname);
  persistSettingsPatch({ disabledSitePatterns: [...patterns] });
}

function removeFloatingButton() {
  const button = state.floatingButton || document.getElementById(FLOATING_BUTTON_ID);
  if (button) {
    button.remove();
  }
  state.floatingButton = null;
  state.floatingPointer = null;
}

function positionFloatingButton(button) {
  const side = state.settings.floatingButtonSide === "left" ? "left" : "right";
  button.dataset.side = side;
  button.style.left = side === "left" ? "0" : "auto";
  button.style.right = side === "right" ? "0" : "auto";
  button.style.top = `${clamp(state.settings.floatingButtonPosition, FLOATING_TOP_MIN, FLOATING_TOP_MAX) * 100}vh`;
  button.style.transform = "";
  button.classList.remove("verbeam-floating-dragging");
}

function updateFloatingButtonState() {
  const button = state.floatingButton;
  if (!button) {
    return;
  }

  button.classList.toggle("verbeam-floating-active", state.enabled);
  const main = button.querySelector(".verbeam-floating-main");
  if (main) {
    const title = state.enabled ? "Restore original text" : "Translate page";
    main.title = title;
    main.setAttribute("aria-label", title);
  }

  const lockButton = button.querySelector("[data-action='lock']");
  if (lockButton) {
    const locked = state.settings.floatingButtonLocked;
    lockButton.textContent = locked ? "Unlock" : "Lock";
    lockButton.title = locked ? "Unlock floating button" : "Lock floating button";
    lockButton.setAttribute("aria-label", lockButton.title);
  }
}

function isCurrentSiteDisabled() {
  const patterns = state.settings.disabledSitePatterns || [];
  return patterns.some((pattern) => matchesSitePattern(pattern, location.href, location.hostname));
}

function matchesSitePattern(pattern, href, hostname) {
  const value = String(pattern || "").trim().toLowerCase();
  if (!value) {
    return false;
  }

  const host = String(hostname || "").toLowerCase();
  const url = String(href || "").toLowerCase();
  if (value === host || value === url) {
    return true;
  }
  if (value.startsWith("*.")) {
    const suffix = value.slice(1);
    return host.endsWith(suffix);
  }
  return url.includes(value);
}

function isTopFrame() {
  try {
    return window.top === window;
  }
  catch {
    return false;
  }
}

function persistSettingsPatch(patch) {
  const settings = normalizeSettings({ ...state.settings, ...patch });
  state.settings = settings;
  chrome.storage.local.set({ settings }, () => {
    const runtimeError = chrome.runtime.lastError;
    if (runtimeError) {
      console.warn("[Verbeam] failed to persist settings:", runtimeError.message);
    }
  });
  syncFloatingButton();
}

function startRouteWatcher() {
  stopRouteWatcher();
  state.routeSignature = getRouteSignature();
  state.routeCheckTimer = window.setInterval(checkRouteChange, ROUTE_CHECK_INTERVAL_MS);
}

function stopRouteWatcher() {
  if (state.routeCheckTimer) {
    window.clearInterval(state.routeCheckTimer);
    state.routeCheckTimer = null;
  }
}

function handlePotentialRouteChange() {
  if (!state.enabled) {
    return;
  }
  window.setTimeout(checkRouteChange, 0);
}

function handlePageLifecycleRescan() {
  if (!state.enabled) {
    return;
  }

  scheduleMutationScan(document.body || document.documentElement);
  scheduleAjaxActivityRescans();
}

function checkRouteChange() {
  if (!state.enabled) {
    return;
  }

  const signature = getRouteSignature();
  if (signature === state.routeSignature) {
    return;
  }

  state.routeSignature = signature;
  translatePage(state.settings).catch((error) => {
    if (!isAbortError(error)) {
      console.error("[Verbeam] route restart failed:", error);
    }
  });
}

function getRouteSignature() {
  return `${location.href}\u001f${document.title || ""}`;
}

function getStatus() {
  return {
    build: VERBEAM_EXTENSION_BUILD,
    enabled: state.enabled,
    watchingAjax: Boolean(state.mutationObserver),
    stats: { ...state.stats },
    settings: { ...state.settings }
  };
}

function resetStats() {
  state.stats = {
    queued: 0,
    running: 0,
    translated: 0,
    failed: 0
  };
}

function loadSettings() {
  return new Promise((resolve) => {
    chrome.storage.local.get("settings", (items) => {
      resolve(normalizeSettings(items.settings));
    });
  });
}

function loadActiveTranslationSession() {
  return new Promise((resolve) => {
    chrome.storage.local.get(PAGE_TRANSLATION_SESSION_KEY, (items) => {
      resolve(items[PAGE_TRANSLATION_SESSION_KEY] || null);
    });
  });
}

async function maybeStartActiveTranslationSession() {
  const session = await loadActiveTranslationSession();
  if (!isActiveTranslationSessionForThisFrame(session)) {
    return;
  }

  await translatePage(session.settings);
}

function isActiveTranslationSessionForThisFrame(session) {
  if (!session?.enabled || !session.settings) {
    return false;
  }

  if (Number(session.expiresAtUnixMs || 0) <= Date.now()) {
    return false;
  }

  if (location.protocol !== "http:" && location.protocol !== "https:") {
    return false;
  }

  const topOrigin = String(session.topOrigin || "");
  if (topOrigin && location.origin === topOrigin) {
    return true;
  }

  const ancestorOrigins = Array.from(location.ancestorOrigins || []);
  if (topOrigin && ancestorOrigins.includes(topOrigin)) {
    return true;
  }

  const frameHosts = Array.isArray(session.frameHosts)
    ? session.frameHosts.map((host) => String(host || "").toLowerCase()).filter(Boolean)
    : [];
  return frameHosts.includes(location.hostname.toLowerCase());
}

function applySettings(settings) {
  state.settings = normalizeSettings(settings);
  syncFloatingButton();
}

async function discoverBackendFromCurrentPage() {
  const backendUrl = backendOriginFromCurrentPage();
  if (!backendUrl || state.settings.backendUrl === backendUrl) {
    return;
  }

  const probe = await sendRuntimeMessage({
    type: "verbeam:probeBackend",
    payload: {
      backendUrl
    }
  });
  if (!probe?.ok) {
    return;
  }

  const settings = normalizeSettings({
    ...state.settings,
    backendUrl
  });
  state.settings = settings;
  try {
    await sendRuntimeMessage({
      type: "verbeam:rememberBackend",
      payload: {
        backendUrl,
        discoveredFrom: location.href
      }
    });
  }
  catch (error) {
    chrome.storage.local.set({ settings }, () => {
      const runtimeError = chrome.runtime.lastError;
      if (runtimeError) {
        console.warn("[Verbeam] failed to persist discovered backend:", runtimeError.message);
      }
    });
    console.debug("[Verbeam] background backend memory failed, used local storage fallback:", error?.message || error);
  }
  console.info("[Verbeam] discovered backend from page:", backendUrl);
}

function backendOriginFromCurrentPage() {
  try {
    if (location.protocol !== "http:") {
      return "";
    }

    if (!["localhost", "127.0.0.1"].includes(location.hostname)) {
      return "";
    }

    return location.origin;
  }
  catch {
    return "";
  }
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
    profile: String(value.profile || DEFAULT_SETTINGS.profile).trim(),
    displayMode: value.displayMode === "translationOnly" ? "translationOnly" : "bilingual",
    contextMode: normalizeContextMode(value.contextMode),
    watchAjax: value.watchAjax !== false,
    concurrency: Math.max(1, Math.min(6, Number(concurrency) || DEFAULT_SETTINGS.concurrency)),
    floatingButtonEnabled: value.floatingButtonEnabled !== false,
    floatingButtonSide: value.floatingButtonSide === "left" ? "left" : "right",
    floatingButtonPosition: clamp(
      Number(value.floatingButtonPosition),
      FLOATING_TOP_MIN,
      FLOATING_TOP_MAX,
      DEFAULT_SETTINGS.floatingButtonPosition
    ),
    floatingButtonLocked: Boolean(value.floatingButtonLocked),
    disabledSitePatterns: normalizeStringList(value.disabledSitePatterns)
  };
}

function normalizeContextMode(value) {
  return value === "balanced" || value === "full" ? value : "fast";
}

function normalizeBackendBaseUrl(rawUrl) {
  try {
    const base = new URL(rawUrl || DEFAULT_SETTINGS.backendUrl);
    if (base.protocol !== "http:" || !["localhost", "127.0.0.1"].includes(base.hostname)) {
      return DEFAULT_SETTINGS.backendUrl;
    }
    base.pathname = "";
    base.search = "";
    base.hash = "";
    return base.toString().replace(/\/$/, "");
  }
  catch {
    return DEFAULT_SETTINGS.backendUrl;
  }
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

function sendRuntimeMessage(message) {
  return new Promise((resolve, reject) => {
    if (state.runtimeInvalidated ||
      typeof chrome === "undefined" ||
      !chrome.runtime ||
      typeof chrome.runtime.sendMessage !== "function") {
      const error = new Error("Extension context invalidated.");
      handleExtensionContextInvalidated(error);
      reject(error);
      return;
    }

    try {
      chrome.runtime.sendMessage(message, (response) => {
        const runtimeError = chrome.runtime.lastError;
        if (runtimeError) {
          const error = new Error(runtimeError.message);
          if (isExtensionContextInvalidated(error)) {
            handleExtensionContextInvalidated(error);
          }
          reject(error);
          return;
        }

        if (!response?.ok) {
          const error = new Error(response?.error || "Verbeam request failed.");
          if (response?.errorName) {
            error.name = response.errorName;
          }
          reject(error);
          return;
        }

        resolve(response.data);
      });
    }
    catch (error) {
      if (isExtensionContextInvalidated(error)) {
        handleExtensionContextInvalidated(error);
      }
      reject(error);
    }
  });
}
