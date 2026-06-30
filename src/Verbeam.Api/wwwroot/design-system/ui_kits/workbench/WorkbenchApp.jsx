/* global React */
const { useState: useWorkbenchState } = React;

function now() {
  return new Date().toLocaleTimeString("en-GB", { hour12: false });
}

const WORKSPACE_TO_MODE = {
  translate: "text",
  ocr: "ocr",
  pipeline: "pipe",
  audio: "audio",
  audioPipeline: "audioPipe",
  region: "region",
};

const MODE_TO_WORKSPACE = {
  text: "translate",
  ocr: "ocr",
  pipe: "pipeline",
  audio: "audio",
  audioPipe: "audioPipeline",
  region: "region",
};

const DEFAULT_RUNTIME = {
  provider: "ollama",
  model: "verbeam-mort-qwen2.5-0.5b:latest",
  ocr: "auto",
  asr: "funasr-http",
  cache: "warm",
};

const OCR_SETTINGS_KEY = "verbeam.ocr.settings";
const REGION_BACKEND_KEY = "verbeam.region.backend";
const NATIVE_POLL_MS = 1500;

function readRegionBackend() {
  try {
    const saved = window.localStorage.getItem(REGION_BACKEND_KEY);
    if (saved === "native" || saved === "browser") {
      return saved;
    }
  } catch {
    // localStorage unavailable / blocked — fall through to default.
  }
  return "native";
}

function persistRegionBackend(value) {
  try {
    window.localStorage.setItem(REGION_BACKEND_KEY, value);
  } catch {
    // Ignore persistence failure; the in-memory state still works.
  }
}

function readOcrSettings() {
  const defaults = {
    provider: "auto",
    contentType: "screenshot_text",
    preference: "balanced",
    language: "ja",
  };

  try {
    const saved = JSON.parse(window.localStorage.getItem(OCR_SETTINGS_KEY) || "null");
    return {
      provider: saved?.provider || defaults.provider,
      contentType: saved?.contentType || defaults.contentType,
      preference: saved?.preference || defaults.preference,
      language: saved?.language || defaults.language,
    };
  } catch {
    return defaults;
  }
}

async function postJson(path, payload) {
  const response = await fetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
  const text = await response.text();
  let data = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = { errorMessage: text };
    }
  }
  if (!response.ok) {
    throw new Error(data?.errorMessage || data?.title || response.statusText || `HTTP ${response.status}`);
  }
  return data;
}

function imagePayloadFromDataUrl(dataUrl) {
  const value = String(dataUrl || "").trim();
  if (!value) {
    throw new Error("image is required");
  }

  const match = /^data:([^;,]+);base64,(.+)$/i.exec(value);
  if (match) {
    return {
      imageMimeType: match[1],
      imageBase64: match[2],
    };
  }

  return {
    imageMimeType: "image/png",
    imageBase64: value,
  };
}

function readFileAsDataUrl(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = (event) => resolve(String(event.target?.result || ""));
    reader.onerror = () => reject(reader.error || new Error("file read failed"));
    reader.readAsDataURL(file);
  });
}

function audioPayloadFromDataUrl(dataUrl) {
  const value = String(dataUrl || "").trim();
  if (!value) {
    return { audioBase64: "", audioMimeType: "" };
  }

  const match = /^data:([^;,]+);base64,(.+)$/i.exec(value);
  return match
    ? { audioMimeType: match[1], audioBase64: match[2] }
    : { audioMimeType: "application/octet-stream", audioBase64: value };
}

function formatTimestamp(seconds, separator) {
  const value = Math.max(0, Number(seconds) || 0);
  const hours = Math.floor(value / 3600);
  const minutes = Math.floor((value % 3600) / 60);
  const secs = Math.floor(value % 60);
  const ms = Math.floor((value - Math.floor(value)) * 1000);
  return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(secs).padStart(2, "0")}${separator}${String(ms).padStart(3, "0")}`;
}

function formatSpeechSegments(segments, kind) {
  const rows = (segments || []).filter((segment) => String(segment.text || "").trim());
  if (kind === "vtt") {
    return [
      "WEBVTT",
      "",
      ...rows.flatMap((segment) => [
        `${formatTimestamp(segment.startSeconds, ".")} --> ${formatTimestamp(segment.endSeconds, ".")}`,
        segment.text,
        "",
      ]),
    ].join("\n");
  }

  return rows.flatMap((segment, index) => [
    String(index + 1),
    `${formatTimestamp(segment.startSeconds, ",")} --> ${formatTimestamp(segment.endSeconds, ",")}`,
    segment.text,
    "",
  ]).join("\n");
}

function joinTranslatedSegments(translations) {
  return (translations || [])
    .map((segment) => segment.translatedText || segment.sourceText || "")
    .filter((text) => String(text).trim())
    .join("\n");
}

function clampRegionSelection(selection) {
  if (!selection) {
    return null;
  }

  const x1 = Math.max(0, Math.min(1, Number(selection.x) || 0));
  const y1 = Math.max(0, Math.min(1, Number(selection.y) || 0));
  const x2 = Math.max(0, Math.min(1, x1 + (Number(selection.w) || 0)));
  const y2 = Math.max(0, Math.min(1, y1 + (Number(selection.h) || 0)));
  const left = Math.min(x1, x2);
  const top = Math.min(y1, y2);
  const right = Math.max(x1, x2);
  const bottom = Math.max(y1, y2);
  return {
    x: left,
    y: top,
    w: Math.max(0.01, right - left),
    h: Math.max(0.01, bottom - top),
  };
}

function waitForVideoFrame(video) {
  if (!video || ((video.videoWidth || 0) > 0 && (video.videoHeight || 0) > 0)) {
    return Promise.resolve();
  }

  return new Promise((resolve) => {
    const done = () => resolve();
    video.addEventListener("loadedmetadata", done, { once: true });
    video.addEventListener("playing", done, { once: true });
    window.setTimeout(done, 1200);
  });
}

function baseOcrPayload(ocrImage) {
  const settings = readOcrSettings();
  return {
    ...imagePayloadFromDataUrl(ocrImage),
    provider: settings.provider,
    contentType: settings.contentType,
    preference: settings.preference,
    language: settings.language,
    profile: "default",
    preprocessingPreset: "none",
  };
}

function baseOcrTranslatePayload(ocrImage, runtime) {
  const ocr = baseOcrPayload(ocrImage);
  const { provider, ...rest } = ocr;
  return {
    ...rest,
    ocrProvider: provider,
    source: "ja",
    target: "zh-TW",
    mode: "game_dialogue",
    translationProvider: runtime.provider,
    model: runtime.model,
    glossary: null,
  };
}

function overlayBoxFromBoundingBox(box) {
  if (!box) {
    return null;
  }

  const x = Number(box.x ?? box.left ?? 0);
  const y = Number(box.y ?? box.top ?? 0);
  const w = Number(box.width ?? box.w ?? 0);
  const h = Number(box.height ?? box.h ?? 0);
  if (![x, y, w, h].every(Number.isFinite) || w <= 0 || h <= 0) {
    return null;
  }

  return { x, y, w, h, angle: 0 };
}

function makeOverlayBlock(id, box, sourceText, translatedText, type = "text") {
  return {
    id: id || `ocr-${Math.random().toString(36).slice(2)}`,
    bbox: box,
    sourceText: sourceText || "",
    translatedText: translatedText || sourceText || "",
    bgColor: type === "formula" ? "rgba(232,238,245,0.92)" : "rgba(245,240,232,0.92)",
    fontSize: type === "title" ? 18 : 14,
    color: "#1a1a1a",
  };
}

function imageSizeFromDataUrl(dataUrl) {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve({
      width: image.naturalWidth || image.width || 640,
      height: image.naturalHeight || image.height || 360,
    });
    image.onerror = () => reject(new Error("image dimensions unavailable"));
    image.src = dataUrl;
  });
}

function unionOverlayBox(blocks) {
  const boxes = (blocks || []).map((block) => block.bbox).filter(Boolean);
  if (boxes.length === 0) {
    return null;
  }

  const left = Math.min(...boxes.map((box) => box.x));
  const top = Math.min(...boxes.map((box) => box.y));
  const right = Math.max(...boxes.map((box) => box.x + box.w));
  const bottom = Math.max(...boxes.map((box) => box.y + box.h));
  return {
    x: left,
    y: top,
    w: Math.max(1, right - left),
    h: Math.max(1, bottom - top),
    angle: 0,
  };
}

function fallbackOverlayBox(size) {
  const width = Math.max(1, Number(size?.width) || 640);
  const height = Math.max(1, Number(size?.height) || 360);
  return {
    x: Math.round(width * 0.06),
    y: Math.round(height * 0.06),
    w: Math.round(width * 0.88),
    h: Math.max(40, Math.round(height * 0.18)),
    angle: 0,
  };
}

async function makeSingleTranslationOverlay(imageDataUrl, sourceText, translatedText, baseBlocks = []) {
  const box = unionOverlayBox(baseBlocks);
  if (box) {
    return [makeOverlayBlock("ocr-translated-union", box, sourceText, translatedText, "text")];
  }

  const size = await imageSizeFromDataUrl(imageDataUrl).catch(() => ({ width: 640, height: 360 }));
  return [makeOverlayBlock("ocr-translated-fallback", fallbackOverlayBox(size), sourceText, translatedText, "text")];
}

function flattenDocumentBlocks(document, segments = []) {
  const rows = [];
  const byId = new Map((segments || []).map((segment) => [segment.id, segment]));

  function visit(block) {
    if (!block) {
      return;
    }

    if (block.table && Array.isArray(block.table.cells)) {
      for (const cell of block.table.cells) {
        const cellId = `${block.id}:${cell.id || `r${cell.rowIndex}-c${cell.columnIndex}`}`;
        const segment = byId.get(cellId);
        const box = overlayBoxFromBoundingBox(cell.boundingBox);
        if (box) {
          rows.push(makeOverlayBlock(
            cellId,
            box,
            segment?.sourceText || cell.sourceText || cell.text || "",
            segment?.translatedText || cell.text || "",
            "table_cell"));
        }
      }
    }

    const segment = byId.get(block.id);
    const source = segment?.sourceText || block.sourceText || block.formula?.latex || block.text || "";
    const output = segment?.translatedText || block.text || source;
    const box = overlayBoxFromBoundingBox(block.boundingBox);
    if (box && output) {
      rows.push(makeOverlayBlock(block.id, box, source, output, block.type || "text"));
    }

    for (const child of block.children || []) {
      visit(child);
    }
  }

  for (const page of document?.pages || []) {
    for (const block of page.blocks || []) {
      visit(block);
    }
  }

  return rows;
}

function blocksFromOcrResponse(ocr, structured = null) {
  const documentBlocks = flattenDocumentBlocks(
    structured?.document || ocr?.document || null,
    structured?.segments || []);
  if (documentBlocks.length > 0) {
    return documentBlocks;
  }

  return (ocr?.blocks || [])
    .map((block, index) => {
      const box = overlayBoxFromBoundingBox(block.boundingBox);
      return box
        ? makeOverlayBlock(`block-${index}`, box, block.text || "", block.text || "")
        : null;
    })
    .filter(Boolean);
}

async function overlayBlocksForTranslatedOcr(imageDataUrl, ocr, structured, translatedText) {
  const blocks = blocksFromOcrResponse(ocr, structured);
  if (blocks.length > 0) {
    return blocks;
  }

  if (!String(translatedText || "").trim()) {
    return [];
  }

  return makeSingleTranslationOverlay(imageDataUrl, ocr?.text || "", translatedText, []);
}

function WorkbenchApp() {
  const [active, setActive] = useWorkbenchState("translate");
  const [mode, setModeState] = useWorkbenchState("text");
  const [source, setSource] = useWorkbenchState("");
  const [result, setResult] = useWorkbenchState("");
  const [latency, setLatency] = useWorkbenchState(0);
  const [live, setLive] = useWorkbenchState(false);
  const [footerLeft, setFooterLeft] = useWorkbenchState("ready");
  const [overlayEnabled, setOverlayEnabled] = useWorkbenchState(true);
  const [maskEnabled, setMaskEnabled] = useWorkbenchState(true);
  const [ocrImage, setOcrImageState] = useWorkbenchState(null);
  const [ocrBlocks, setOcrBlocks] = useWorkbenchState([]);
  const [ocrText, setOcrText] = useWorkbenchState("");
  const [ocrTranslation, setOcrTranslation] = useWorkbenchState("");
  const [audioSourceUrl, setAudioSourceUrl] = useWorkbenchState("");
  const [audioDataUrl, setAudioDataUrl] = useWorkbenchState("");
  const [audioFileName, setAudioFileName] = useWorkbenchState("");
  const [asrText, setAsrText] = useWorkbenchState("");
  const [speechSegments, setSpeechSegments] = useWorkbenchState([]);
  const [speechTranslation, setSpeechTranslation] = useWorkbenchState("");
  const [regionStatus, setRegionStatus] = useWorkbenchState("idle");
  const [regionSelection, setRegionSelection] = useWorkbenchState(null);
  const [regionImage, setRegionImage] = useWorkbenchState("");
  const [regionBlocks, setRegionBlocks] = useWorkbenchState([]);
  const [regionOcrText, setRegionOcrText] = useWorkbenchState("");
  const [regionTranslation, setRegionTranslation] = useWorkbenchState("");
  const [regionLoopEnabled, setRegionLoopEnabled] = useWorkbenchState(false);
  const [regionInterval, setRegionInterval] = useWorkbenchState(1500);
  const [regionBackend, setRegionBackend] = useWorkbenchState(readRegionBackend());
  const [regionNativeStatus, setRegionNativeStatus] = useWorkbenchState(null);
  const [regionNativeAvailable, setRegionNativeAvailable] = useWorkbenchState(true);
  const [busy, setBusy] = useWorkbenchState(false);
  const [runtime, setRuntime] = useWorkbenchState(DEFAULT_RUNTIME);
  const [messages, setMessages] = useWorkbenchState([
    { id: 0, kind: "system", title: "boot", meta: now(), body: "Verbeam app ready" },
  ]);
  const regionVideoRef = React.useRef(null);
  const regionStreamRef = React.useRef(null);
  const regionLoopRef = React.useRef(0);

  React.useEffect(() => {
    let canceled = false;
    fetch("/health")
      .then((response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response.json();
      })
      .then((health) => {
        if (canceled) {
          return;
        }

        const provider = health.defaultProvider || DEFAULT_RUNTIME.provider;
        const model =
          provider === "llama-cpp"
            ? health.llamaCpp?.model || DEFAULT_RUNTIME.model
            : health.ollama?.model || DEFAULT_RUNTIME.model;
        setRuntime({
          provider,
          model,
          ocr: health.ocr?.defaultProvider || DEFAULT_RUNTIME.ocr,
          asr: health.speech?.defaultProvider || DEFAULT_RUNTIME.asr,
          cache: "warm",
        });
      })
      .catch(() => {
        if (!canceled) {
          setRuntime(DEFAULT_RUNTIME);
        }
      });
    return () => { canceled = true; };
  }, []);

  // Native-engine status polling: only while the native backend is selected. Keeps the workbench UI
  // in sync with tray hotkeys / external changes to the shared engine. A 404 means we're in a pure
  // browser host with no /region/native/* endpoints → disable the native pill and fall back to
  // browser so the Run button never targets a dead endpoint.
  React.useEffect(() => {
    if (regionBackend !== "native") {
      setRegionNativeStatus(null);
      return;
    }

    let active = true;
    const poll = async () => {
      try {
        const response = await fetch("/region/native/status");
        if (!active) return;
        if (response.status === 404) {
          setRegionNativeAvailable(false);
          setRegionBackend("browser");
          persistRegionBackend("browser");
          return;
        }
        if (!response.ok) return;
        const data = await response.json();
        if (!active) return;
        setRegionNativeAvailable(true);
        setRegionNativeStatus(data);
      } catch {
        // Network hiccup / host gone — skip this tick silently.
      }
    };

    poll();
    const handle = window.setInterval(poll, NATIVE_POLL_MS);
    return () => {
      active = false;
      window.clearInterval(handle);
    };
  }, [regionBackend]);

  function append(msg) {
    setMessages((prev) => [...prev, { id: prev.length ? prev[prev.length - 1].id + 1 : 0, ...msg }]);
  }

  function setMode(nextMode) {
    setModeState(nextMode);
    const workspace = MODE_TO_WORKSPACE[nextMode];
    if (workspace) {
      setActive(workspace);
    }
  }

  function selectWorkspace(nextActive) {
    setActive(nextActive);
    const nextMode = WORKSPACE_TO_MODE[nextActive];
    if (nextMode) {
      setModeState(nextMode);
    }
  }

  function handleOcrImage(nextImage) {
    setOcrImageState(nextImage);
    setOcrBlocks([]);
    setOcrText("");
    setOcrTranslation("");
    setResult("");
    if (mode === "text") {
      setMode("ocr");
    }
    setFooterLeft(nextImage ? "image loaded" : "ready");
  }

  async function handleAudioFile(file) {
    if (!file) {
      return;
    }

    const dataUrl = await readFileAsDataUrl(file);
    setAudioDataUrl(dataUrl);
    setAudioFileName(file.name || "audio");
    setAudioSourceUrl("");
    setAsrText("");
    setSpeechSegments([]);
    setSpeechTranslation("");
    setFooterLeft(`audio loaded - ${file.name || "file"}`);
  }

  async function runTranslate() {
    const text = source.trim();
    if (!text) {
      throw new Error("source is empty");
    }

    append({ kind: "user", title: "source", meta: `${runtime.provider} / ${runtime.model}`, body: text });
    const started = performance.now();
    const data = await postJson("/translate", {
      text,
      source: "ja",
      target: "zh-TW",
      mode: "game_dialogue",
      provider: runtime.provider,
      model: runtime.model,
    });
    const ms = Math.round(performance.now() - started);
    setLatency(ms);

    if (data.errorCode && data.errorCode !== "0") {
      throw new Error(data.errorMessage || data.result || "translation failed");
    }

    setResult(data.result || "");
    append({ kind: "result", title: "translation", meta: `${ms} ms`, body: data.result || "" });
    setLive(true);
    setFooterLeft(`translated - ${ms} ms`);
  }

  async function runOcrOnly() {
    const started = performance.now();
    const data = await postJson("/ocr", baseOcrPayload(ocrImage));
    const ms = Math.round(performance.now() - started);
    const text = data?.text || "";

    setLatency(ms);
    setOcrText(text);
    setSource(text);
    setResult("");
    setOcrTranslation("");
    setOcrBlocks(blocksFromOcrResponse(data));
    append({ kind: "result", title: "ocr", meta: `${ms} ms`, body: text || "(no text)" });
    setFooterLeft(`ocr - ${ms} ms`);
    return text;
  }

  async function runOcrTranslate() {
    const started = performance.now();
    const data = await postJson("/ocr/translate", baseOcrTranslatePayload(ocrImage, runtime));
    const ms = Math.round(performance.now() - started);
    const ocr = data?.ocr || {};
    const translation = data?.translation || {};
    const text = ocr.text || "";
    const translated = translation.result || data?.structured?.text || "";

    setLatency(ms);
    setOcrText(text);
    setOcrTranslation(translated);
    setSource(text);
    setResult(translated);
    setOcrBlocks(await overlayBlocksForTranslatedOcr(ocrImage, ocr, data?.structured || null, translated));
    append({ kind: "user", title: "ocr", meta: ocr.provider || "ocr", body: text || "(no text)" });
    if (translation.errorCode && translation.errorCode !== "0") {
      append({ kind: "error", title: "translation", meta: `${ms} ms`, body: translation.errorMessage || translated || "translation failed" });
    } else {
      append({ kind: "result", title: "translation", meta: `${ms} ms`, body: translated || "" });
    }
    setLive(true);
    setFooterLeft(`ocr+translate - ${ms} ms`);
  }

  async function translateOcrText() {
    const text = ocrText.trim();
    if (!text) {
      throw new Error("ocr text is empty");
    }

    append({ kind: "user", title: "edited ocr", meta: `${runtime.provider} / ${runtime.model}`, body: text });
    const started = performance.now();
    const data = await postJson("/translate", {
      text,
      source: "ja",
      target: "zh-TW",
      mode: "game_dialogue",
      provider: runtime.provider,
      model: runtime.model,
    });
    const ms = Math.round(performance.now() - started);
    if (data.errorCode && data.errorCode !== "0") {
      throw new Error(data.errorMessage || data.result || "translation failed");
    }

    setLatency(ms);
    setOcrTranslation(data.result || "");
    setResult(data.result || "");
    setOcrBlocks(await makeSingleTranslationOverlay(ocrImage, text, data.result || "", ocrBlocks));
    append({ kind: "result", title: "translation", meta: `${ms} ms`, body: data.result || "" });
    setLive(true);
    setFooterLeft(`translated - ${ms} ms`);
  }

  function speechPayload() {
    const sourceUrl = audioSourceUrl.trim();
    const audio = audioPayloadFromDataUrl(audioDataUrl);
    if (!sourceUrl && !audio.audioBase64) {
      throw new Error("audio source is empty");
    }

    return {
      audioBase64: sourceUrl ? null : audio.audioBase64,
      audioMimeType: sourceUrl ? null : audio.audioMimeType,
      sourceUrl: sourceUrl || null,
      provider: runtime.asr,
      language: "ja",
      profile: "default",
      glossary: null,
      preferCaptions: true,
    };
  }

  async function runAsr() {
    const started = performance.now();
    const data = await postJson("/asr", speechPayload());
    const ms = Math.round(performance.now() - started);
    const text = data?.text || "";

    setLatency(ms);
    setAsrText(text);
    setSpeechSegments(data?.segments || []);
    setSpeechTranslation("");
    setSource(text);
    setResult("");
    append({ kind: "result", title: "asr", meta: `${data?.provider || runtime.asr} / ${ms} ms`, body: text || "(no speech)" });
    setFooterLeft(`asr - ${ms} ms`);
  }

  async function runAsrPipeline() {
    const payload = speechPayload();
    const started = performance.now();
    const data = await postJson("/asr/translate", {
      audioBase64: payload.audioBase64,
      audioMimeType: payload.audioMimeType,
      sourceUrl: payload.sourceUrl,
      speechProvider: runtime.asr,
      language: payload.language,
      profile: payload.profile,
      preferCaptions: payload.preferCaptions,
      source: "ja",
      target: "zh-TW",
      mode: "game_dialogue",
      glossary: null,
      translationProvider: runtime.provider,
      model: runtime.model,
    });
    const ms = Math.round(performance.now() - started);
    const speech = data?.speech || {};
    const translated = joinTranslatedSegments(data?.translations || []);

    setLatency(ms);
    setAsrText(speech.text || "");
    setSpeechSegments(speech.segments || []);
    setSpeechTranslation(translated);
    setSource(speech.text || "");
    setResult(translated);
    append({ kind: "user", title: "asr", meta: `${speech.provider || runtime.asr}`, body: speech.text || "(no speech)" });
    append({ kind: "result", title: "audio translation", meta: `${ms} ms`, body: translated || "" });
    setLive(true);
    setFooterLeft(`asr+translate - ${ms} ms`);
  }

  function stopRegionCapture() {
    if (regionLoopRef.current) {
      window.clearInterval(regionLoopRef.current);
      regionLoopRef.current = 0;
    }

    regionStreamRef.current?.getTracks?.().forEach((track) => track.stop());
    regionStreamRef.current = null;
    if (regionVideoRef.current) {
      regionVideoRef.current.srcObject = null;
    }
    setRegionLoopEnabled(false);
    setRegionStatus("idle");
  }

  async function startRegionCapture() {
    if (!navigator.mediaDevices?.getDisplayMedia) {
      throw new Error("screen capture is not available in this browser");
    }

    const stream = await navigator.mediaDevices.getDisplayMedia({
      video: { cursor: "always" },
      audio: false,
    });
    regionStreamRef.current = stream;
    setRegionStatus("capturing");
    const [track] = stream.getVideoTracks();
    if (track) {
      track.addEventListener("ended", stopRegionCapture, { once: true });
    }

    if (regionVideoRef.current) {
      regionVideoRef.current.srcObject = stream;
      await regionVideoRef.current.play().catch(() => {});
      await waitForVideoFrame(regionVideoRef.current);
    }
    setFooterLeft("region capture ready");
  }

  function captureRegionFrame() {
    const video = regionVideoRef.current;
    if (!video || !regionStreamRef.current) {
      throw new Error("capture screen first");
    }

    const width = video.videoWidth || 0;
    const height = video.videoHeight || 0;
    if (width <= 0 || height <= 0) {
      throw new Error("screen capture is not ready yet");
    }

    const box = clampRegionSelection(regionSelection) || { x: 0, y: 0, w: 1, h: 1 };
    const sx = Math.round(box.x * width);
    const sy = Math.round(box.y * height);
    const sw = Math.max(1, Math.round(box.w * width));
    const sh = Math.max(1, Math.round(box.h * height));
    const canvas = document.createElement("canvas");
    canvas.width = sw;
    canvas.height = sh;
    const context = canvas.getContext("2d");
    context.drawImage(video, sx, sy, sw, sh, 0, 0, sw, sh);
    return canvas.toDataURL("image/png");
  }

  async function runRegionSnapshot() {
    if (!regionStreamRef.current) {
      await startRegionCapture();
    }

    const image = captureRegionFrame();
    setRegionImage(image);
    const started = performance.now();
    const data = await postJson("/ocr/translate", {
      ...baseOcrTranslatePayload(image, runtime),
      realtime: true,
    });
    const ms = Math.round(performance.now() - started);
    const ocr = data?.ocr || {};
    const translation = data?.translation || {};
    const translated = translation.result || data?.structured?.text || "";

    setLatency(ms);
    setRegionOcrText(ocr.text || "");
    setRegionTranslation(translated);
    const blocks = await overlayBlocksForTranslatedOcr(image, ocr, data?.structured || null, translated);
    setRegionBlocks(blocks);
    setOcrImageState(image);
    setOcrBlocks(blocks);
    setOcrText(ocr.text || "");
    setOcrTranslation(translated);
    setSource(ocr.text || "");
    setResult(translated);
    append({ kind: "user", title: "region ocr", meta: ocr.provider || runtime.ocr, body: ocr.text || "(no text)" });
    append({ kind: "result", title: "region translation", meta: `${ms} ms`, body: translated || "" });
    setLive(true);
    setFooterLeft(`region - ${ms} ms`);
  }

  async function runNativeRegionSelect() {
    const status = await postJson("/region/native/select", {});
    const count = Number(status?.regionCount) || 0;
    const loopActive = Boolean(status?.loopActive);
    setRegionLoopEnabled(loopActive);
    setRegionStatus(count > 0 ? "capturing" : "idle");
    if (count > 0) {
      setFooterLeft(`region native · ${count} region${count === 1 ? "" : "s"}`);
      append({ kind: "result", title: "region", meta: "native", body: `${count} region${count === 1 ? "" : "s"} running` });
    } else {
      setFooterLeft("region native · no regions");
      append({ kind: "system", title: "region", meta: "native", body: "no regions selected" });
    }
    setLive(count > 0);
  }

  async function stopNativeLoop() {
    try {
      await postJson("/region/native/stop", {});
      setRegionLoopEnabled(false);
      setRegionStatus("idle");
      setFooterLeft("region native · loop stopped");
      append({ kind: "system", title: "region", meta: "native", body: "loop stopped" });
    } catch (error) {
      append({ kind: "error", title: "region", meta: "native", body: error.message || String(error) });
    }
  }

  async function handleRegionBackendChange(next) {
    if (next === regionBackend) {
      return;
    }

    // Mutual exclusion: stop the engine we're leaving so the two paths never run concurrently.
    if (regionBackend === "native") {
      try {
        await postJson("/region/native/stop", {});
      } catch {
        // Best-effort; switching anyway.
      }
      setRegionLoopEnabled(false);
      setRegionStatus("idle");
    } else if (regionBackend === "browser") {
      stopRegionCapture();
    }

    setRegionBackend(next);
    persistRegionBackend(next);
    setFooterLeft(`region · ${next}`);
  }

  function toggleRegionLoop() {
    if (regionLoopRef.current) {
      window.clearInterval(regionLoopRef.current);
      regionLoopRef.current = 0;
      setRegionLoopEnabled(false);
      setFooterLeft("region loop stopped");
      return;
    }

    const interval = Math.max(500, Number(regionInterval) || 1500);
    setRegionLoopEnabled(true);
    setFooterLeft(`region loop - ${interval} ms`);
    regionLoopRef.current = window.setInterval(() => {
      if (!busy) {
        runRegionSnapshot().catch((error) => {
          append({ kind: "error", title: "region", meta: "loop", body: error.message || String(error) });
          toggleRegionLoop();
        });
      }
    }, interval);
  }

  async function handleRun() {
    if (busy) {
      return;
    }

    setBusy(true);
    try {
      if (mode === "text") {
        await runTranslate();
      } else if (mode === "ocr") {
        await runOcrOnly();
      } else if (mode === "pipe") {
        await runOcrTranslate();
      } else if (mode === "audio") {
        await runAsr();
      } else if (mode === "audioPipe") {
        await runAsrPipeline();
      } else if (mode === "region") {
        if (regionBackend === "browser") {
          await runRegionSnapshot();
        } else {
          await runNativeRegionSelect();
        }
      } else {
        append({ kind: "system", title: mode, meta: "0 ms", body: `${mode} pipeline is not wired yet` });
        setFooterLeft(`${mode} pending`);
      }
    } catch (error) {
      append({ kind: "error", title: mode, meta: "error", body: error.message || String(error) });
      setFooterLeft("error");
    } finally {
      setBusy(false);
    }
  }

  async function handleTranslateOcrText() {
    if (busy) {
      return;
    }

    setBusy(true);
    try {
      await translateOcrText();
    } catch (error) {
      append({ kind: "error", title: "translation", meta: "error", body: error.message || String(error) });
      setFooterLeft("error");
    } finally {
      setBusy(false);
    }
  }

  function handleClear() {
    setMessages([]);
    setResult("");
    setLatency(0);
    setOcrBlocks([]);
    setOcrText("");
    setOcrTranslation("");
    setAsrText("");
    setSpeechSegments([]);
    setSpeechTranslation("");
    setRegionImage("");
    setRegionBlocks([]);
    setRegionOcrText("");
    setRegionTranslation("");
    setFooterLeft("cleared");
  }

  function copySpeech(kind) {
    const content = formatSpeechSegments(speechSegments, kind);
    navigator.clipboard?.writeText(content);
    append({ kind: "result", title: "subtitle", meta: kind.toUpperCase(), body: content || "(no segments)" });
  }

  const showSettings = active === "settings";

  return (
    <div style={appStyles.shell}>
      <VbTopbar health={busy ? "busy" : "ok"} broadcast={live ? "broadcast" : "idle"} live={live} />
      <VbActivityRail route="/app" />
      <VbSidebar active={active} onSelect={selectWorkspace} runtime={runtime} />

      <main style={appStyles.workspace}>
        {showSettings ? (
          <VbSettingsScreen />
        ) : (
          <>
            <VbTerminal messages={messages} />
            <VbComposer
              mode={mode}
              onMode={setMode}
              source={source}
              onSource={setSource}
              result={result}
              latency={latency}
              busy={busy}
              onRun={handleRun}
              onClear={handleClear}
              onTranslateOcrText={handleTranslateOcrText}
              overlayEnabled={overlayEnabled}
              onOverlayToggle={() => setOverlayEnabled((v) => !v)}
              maskEnabled={maskEnabled}
              onMaskToggle={() => setMaskEnabled((v) => !v)}
              ocrImage={ocrImage}
              onOcrImage={handleOcrImage}
              ocrBlocks={ocrBlocks}
              ocrText={ocrText}
              onOcrText={setOcrText}
              ocrTranslation={ocrTranslation}
              audioSourceUrl={audioSourceUrl}
              onAudioSourceUrl={setAudioSourceUrl}
              audioFileName={audioFileName}
              onAudioFile={handleAudioFile}
              asrText={asrText}
              speechTranslation={speechTranslation}
              onCopySpeech={copySpeech}
              regionVideoRef={regionVideoRef}
              regionStatus={regionStatus}
              regionSelection={regionSelection}
              onRegionSelection={setRegionSelection}
              regionImage={regionImage}
              regionBlocks={regionBlocks}
              regionOcrText={regionOcrText}
              regionTranslation={regionTranslation}
              regionLoopEnabled={regionLoopEnabled}
              regionInterval={regionInterval}
              onRegionInterval={setRegionInterval}
              onStartRegionCapture={startRegionCapture}
              onStopRegionCapture={stopRegionCapture}
              onToggleRegionLoop={toggleRegionLoop}
              regionBackend={regionBackend}
              regionNativeAvailable={regionNativeAvailable}
              onRegionBackend={handleRegionBackendChange}
              regionNativeStatus={regionNativeStatus}
              onStopNativeLoop={stopNativeLoop}
            />
          </>
        )}
      </main>

      <VbFooter left={footerLeft} right={showSettings ? "/app settings" : "/app"} />
    </div>
  );
}

const appStyles = {
  shell: {
    display: "grid",
    gridTemplateColumns: "53px 230px minmax(0,1fr)",
    gridTemplateRows: "44px minmax(0,1fr) 30px",
    height: "100vh",
    background: "var(--vb-bg)",
  },
  workspace: {
    gridColumn: 3,
    gridRow: "2 / 3",
    display: "grid",
    gridTemplateRows: "minmax(0,1fr) auto",
    minWidth: 0,
    overflow: "hidden",
  },
};

Object.assign(window, { VbWorkbenchApp: WorkbenchApp });
