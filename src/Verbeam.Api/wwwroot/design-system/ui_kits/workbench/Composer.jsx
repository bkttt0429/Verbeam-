/* global React */
const { Message, Tab, Button, Field, Input, Pill } = window.LocalTranslateHubDesignSystem_32566a;

function Terminal({ messages }) {
  const ref = React.useRef(null);
  React.useEffect(() => {
    if (ref.current) ref.current.scrollTop = ref.current.scrollHeight;
  }, [messages]);
  return (
    <section ref={ref} style={tStyles.terminal} aria-live="polite">
      {messages.map((m) => (
        <Message key={m.id} kind={m.kind} title={m.title} meta={m.meta}>
          {m.body}
        </Message>
      ))}
    </section>
  );
}

const tStyles = {
  terminal: {
    overflow: "auto",
    padding: "16px",
    display: "grid",
    gap: "12px",
    alignContent: "start",
    background: "var(--vb-bg)",
  },
};

const MODES = [
  { id: "text", label: "text" },
  { id: "ocr", label: "ocr" },
  { id: "pipe", label: "pipe" },
  { id: "audio", label: "audio" },
  { id: "audioPipe", label: "audio pipe" },
  { id: "region", label: "region" },
];

function TextPane({ source, onSource, result }) {
  return (
    <div style={cStyles.grid}>
      <Field label="source" htmlFor="src">
        <Input id="src" multiline value={source} onChange={(e) => onSource(e.target.value)} rows={3} />
      </Field>
      <Field label="result" htmlFor="res">
        <Input id="res" multiline readOnly value={result} rows={3} placeholder="" />
      </Field>
    </div>
  );
}

function OcrOverlayBlock({ block, maskEnabled, scaleX = 1, scaleY = 1 }) {
  const { bbox, translatedText, bgColor, fontSize, color } = block;
  const fontScale = Math.max(0.7, Math.min(1.4, Math.min(scaleX || 1, scaleY || 1)));
  return (
    <div style={{
      position: "absolute",
      left: `${bbox.x * scaleX}px`,
      top: `${bbox.y * scaleY}px`,
      width: `${bbox.w * scaleX}px`,
      height: `${bbox.h * scaleY}px`,
      transform: bbox.angle ? `rotate(${bbox.angle}deg)` : undefined,
      transformOrigin: "top left",
      backgroundColor: maskEnabled ? (bgColor || "rgba(245,240,232,0.92)") : "transparent",
      display: "grid",
      placeItems: "center",
      borderRadius: "2px",
      overflow: "hidden",
      transition: "background-color 0.2s ease",
      padding: "2px 4px",
    }}>
      <span style={{
        fontSize: `${Math.max(10, Math.round((fontSize || 14) * fontScale))}px`,
        color: color || "#2d2d2d",
        fontWeight: 600,
        lineHeight: 1.1,
        textAlign: "center",
        whiteSpace: "normal",
        overflowWrap: "anywhere",
        textShadow: maskEnabled ? "none" : "0 1px 3px rgba(0,0,0,0.7)",
      }}>
        {translatedText}
      </span>
    </div>
  );
}

function OcrOverlay({ image, blocks, overlayEnabled, maskEnabled, onImageChange, showActions = true }) {
  const fileRef = React.useRef(null);
  const imageRef = React.useRef(null);
  const [dragOver, setDragOver] = React.useState(false);
  const [imageScale, setImageScale] = React.useState({ x: 1, y: 1 });

  function updateImageScale() {
    const node = imageRef.current;
    if (!node || !node.naturalWidth || !node.naturalHeight) {
      setImageScale({ x: 1, y: 1 });
      return;
    }

    setImageScale({
      x: node.clientWidth / node.naturalWidth,
      y: node.clientHeight / node.naturalHeight,
    });
  }

  React.useEffect(() => {
    updateImageScale();
    const node = imageRef.current;
    if (!node || typeof ResizeObserver === "undefined") {
      window.addEventListener("resize", updateImageScale);
      return () => window.removeEventListener("resize", updateImageScale);
    }

    const observer = new ResizeObserver(updateImageScale);
    observer.observe(node);
    return () => observer.disconnect();
  }, [image]);

  function handleFile(file) {
    if (!file || !file.type.startsWith("image/")) return;
    const reader = new FileReader();
    reader.onload = (e) => onImageChange(e.target.result);
    reader.readAsDataURL(file);
  }

  function handleDrop(e) {
    e.preventDefault();
    setDragOver(false);
    const file = e.dataTransfer.files?.[0];
    if (file) handleFile(file);
  }

  function handlePaste(e) {
    const file = e.clipboardData?.files?.[0];
    if (file) handleFile(file);
  }

  if (!image) {
    return (
      <div
        style={{...cStyles.dropZone, minHeight: "260px"}}
        tabIndex={0}
        role="button"
        onClick={() => fileRef.current?.click()}
        onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
        onDragLeave={() => setDragOver(false)}
        onDrop={handleDrop}
        onPaste={handlePaste}
      >
        <input
          ref={fileRef}
          type="file"
          accept="image/*"
          style={{ display: "none" }}
          onChange={(e) => handleFile(e.target.files?.[0])}
        />
        <i className="ri-image-add-line" style={cStyles.dropIcon} />
        <div style={cStyles.dropTitle}>Drop, paste, or choose an image</div>
        <div style={cStyles.dropHint}>Image input auto-runs OCR. Translated text renders in-place on the image.</div>
      </div>
    );
  }

  return (
    <div style={ovStyles.stage}>
      <div style={ovStyles.imageWrap}>
        <img ref={imageRef} src={image} alt="OCR source" style={ovStyles.image} draggable={false} onLoad={updateImageScale} />
        {overlayEnabled && blocks.map((block) => (
          <OcrOverlayBlock key={block.id} block={block} maskEnabled={maskEnabled} scaleX={imageScale.x} scaleY={imageScale.y} />
        ))}
      </div>
      {showActions && (
        <div style={ovStyles.imageActions}>
          <button type="button" style={ovStyles.imageBtn} onClick={() => { fileRef.current?.click(); }}>
            <i className="ri-refresh-line" /> Change image
          </button>
          <input
            ref={fileRef}
            type="file"
            accept="image/*"
            style={{ display: "none" }}
            onChange={(e) => handleFile(e.target.files?.[0])}
          />
        </div>
      )}
    </div>
  );
}

const ovStyles = {
  stage: {
    display: "grid",
    gap: "8px",
  },
  imageWrap: {
    position: "relative",
    display: "inline-block",
    maxWidth: "100%",
    border: "1px solid var(--vb-line)",
    borderRadius: "6px",
    overflow: "hidden",
    background: "#0a0a0a",
  },
  image: {
    display: "block",
    maxWidth: "100%",
    maxHeight: "340px",
    userSelect: "none",
  },
  imageActions: {
    display: "flex",
    gap: "8px",
    alignItems: "center",
  },
  imageBtn: {
    display: "inline-flex",
    alignItems: "center",
    gap: "6px",
    minHeight: "28px",
    padding: "0 10px",
    border: "1px solid var(--vb-line)",
    borderRadius: "5px",
    background: "transparent",
    color: "var(--vb-muted)",
    fontSize: "11px",
    fontFamily: "var(--font-sans)",
    cursor: "pointer",
    transition: "all 0.15s ease",
  },
};

function OcrPane({
  overlayEnabled,
  onOverlayToggle,
  maskEnabled,
  onMaskToggle,
  ocrImage,
  onOcrImage,
  ocrBlocks,
  ocrText,
  onOcrText,
  ocrTranslation,
  audioSourceUrl,
  onAudioSourceUrl,
  audioFileName,
  onAudioFile,
  asrText,
  speechTranslation,
  onCopySpeech,
  regionVideoRef,
  regionStatus,
  regionSelection,
  onRegionSelection,
  regionImage,
  regionBlocks,
  regionOcrText,
  regionTranslation,
  regionLoopEnabled,
  regionInterval,
  onRegionInterval,
  onStartRegionCapture,
  onStopRegionCapture,
  onToggleRegionLoop,
}) {
  const [textCollapsed, setTextCollapsed] = React.useState(true);

  return (
    <div style={cStyles.stack}>
      <div style={cStyles.overlayToolbar}>
        <button
          type="button"
          onClick={onOverlayToggle}
          style={{
            ...cStyles.toggleBtn,
            borderColor: overlayEnabled ? "var(--vb-blue)" : "var(--vb-line)",
            color: overlayEnabled ? "var(--vb-blue)" : "var(--vb-muted)",
            background: overlayEnabled ? "rgba(59,130,246,0.1)" : "transparent",
          }}
        >
          <i className={overlayEnabled ? "ri-eye-line" : "ri-eye-off-line"} />
          Overlay {overlayEnabled ? "ON" : "OFF"}
        </button>
        <button
          type="button"
          onClick={onMaskToggle}
          style={{
            ...cStyles.toggleBtn,
            borderColor: maskEnabled ? "var(--vb-blue)" : "var(--vb-line)",
            color: maskEnabled ? "var(--vb-blue)" : "var(--vb-muted)",
            background: maskEnabled ? "rgba(59,130,246,0.1)" : "transparent",
          }}
        >
          <i className={maskEnabled ? "ri-paint-fill" : "ri-paint-line"} />
          Mask {maskEnabled ? "ON" : "OFF"}
        </button>
        <span style={cStyles.overlayHint}>
          {overlayEnabled ? "Translated text renders in-place on the image" : "Overlay disabled — saving resources"}
        </span>
      </div>

      <OcrOverlay
        image={ocrImage}
        blocks={ocrBlocks}
        overlayEnabled={overlayEnabled}
        maskEnabled={maskEnabled}
        onImageChange={onOcrImage}
      />

      <div style={cStyles.collapseSection}>
        <button
          type="button"
          onClick={() => setTextCollapsed(v => !v)}
          style={cStyles.collapseToggle}
        >
          <i className={textCollapsed ? "ri-arrow-down-s-line" : "ri-arrow-up-s-line"} />
          <span>OCR text &amp; translation</span>
          <span style={cStyles.collapseMeta}>{textCollapsed ? "collapsed" : "expanded"}</span>
        </button>
        {!textCollapsed && (
          <div style={cStyles.grid}>
            <Field label="ocr text" htmlFor="ocrOut">
              <Input id="ocrOut" multiline rows={3} value={ocrText || ""} onChange={(e) => onOcrText && onOcrText(e.target.value)} placeholder="" />
            </Field>
            <Field label="translation" htmlFor="ocrTr">
              <Input id="ocrTr" multiline readOnly rows={3} value={ocrTranslation || ""} placeholder="" />
            </Field>
          </div>
        )}
      </div>
    </div>
  );
}

function AudioPane() {
  return (
    <div style={cStyles.grid}>
      <Field label="audio source" htmlFor="audUrl">
        <div style={cStyles.stack}>
          <div style={cStyles.fileRow}>
            <Input id="audUrl" mono placeholder="https:// … or choose a file" style={{ flex: 1 }} />
            <Button>Choose file</Button>
          </div>
          <span style={cStyles.hint}>audio / video · auto-runs ASR</span>
        </div>
      </Field>
      <Field label="asr text" htmlFor="asrOut">
        <div style={cStyles.stack}>
          <Input id="asrOut" multiline rows={2} placeholder="" />
          <div style={cStyles.fileRow}>
            <Button>Copy SRT</Button>
            <Button>Copy VTT</Button>
          </div>
        </div>
      </Field>
    </div>
  );
}

function RegionPane() {
  return (
    <div style={cStyles.stack}>
      <div style={cStyles.toolbar}>
        <Button>Capture Screen</Button>
        <Button variant="primary">Snapshot Translate</Button>
        <Button>Loop off</Button>
        <div style={cStyles.interval}>
          <span className="vb-label">interval ms</span>
          <Input mono type="number" defaultValue="1500" style={{ width: "90px" }} />
        </div>
      </div>
      <div style={cStyles.regionStage} tabIndex={0}>
        Capture a screen or window, then drag a box over the dialogue area.
      </div>
      <div style={cStyles.grid}>
        <Field label="region ocr" htmlFor="regOcr">
          <Input id="regOcr" multiline rows={2} placeholder="" />
        </Field>
        <Field label="region translation" htmlFor="regTr">
          <Input id="regTr" multiline readOnly rows={2} placeholder="" />
        </Field>
      </div>
    </div>
  );
}

function WiredAudioPane({
  sourceUrl,
  onSourceUrl,
  audioFileName,
  onAudioFile,
  asrText,
  speechTranslation,
  onCopySpeech,
  isPipeline,
  busy,
}) {
  const fileRef = React.useRef(null);

  return (
    <div style={cStyles.grid}>
      <Field label="audio source" htmlFor="audUrl">
        <div style={cStyles.stack}>
          <div style={cStyles.fileRow}>
            <Input
              id="audUrl"
              mono
              value={sourceUrl || ""}
              onChange={(e) => onSourceUrl(e.target.value)}
              placeholder="https:// ... or choose a file"
              style={{ flex: 1 }}
            />
            <Button onClick={() => fileRef.current?.click()} disabled={busy}>Choose file</Button>
            <input
              ref={fileRef}
              type="file"
              accept="audio/*,video/*"
              style={{ display: "none" }}
              onChange={(e) => onAudioFile(e.target.files?.[0])}
            />
          </div>
          <span style={cStyles.hint}>
            {audioFileName ? `file: ${audioFileName}` : "audio / video source, then Run"}
          </span>
        </div>
      </Field>
      <Field label="asr text" htmlFor="asrOut">
        <div style={cStyles.stack}>
          <Input id="asrOut" multiline rows={2} readOnly value={asrText || ""} placeholder="" />
          <div style={cStyles.fileRow}>
            <Button onClick={() => onCopySpeech("srt")} disabled={busy || !asrText}>Copy SRT</Button>
            <Button onClick={() => onCopySpeech("vtt")} disabled={busy || !asrText}>Copy VTT</Button>
          </div>
        </div>
      </Field>
      {isPipeline && (
        <Field label="translation" htmlFor="audTr">
          <Input id="audTr" multiline rows={2} readOnly value={speechTranslation || ""} placeholder="" />
        </Field>
      )}
    </div>
  );
}

function clampUnit(value) {
  return Math.max(0, Math.min(1, Number(value) || 0));
}

function normalizeSelection(start, end) {
  const left = Math.min(start.x, end.x);
  const top = Math.min(start.y, end.y);
  const right = Math.max(start.x, end.x);
  const bottom = Math.max(start.y, end.y);
  return {
    x: left,
    y: top,
    w: Math.max(0.01, right - left),
    h: Math.max(0.01, bottom - top),
  };
}

function RegionCaptureStage({ videoRef, status, selection, onSelection }) {
  const stageRef = React.useRef(null);
  const [dragStart, setDragStart] = React.useState(null);

  function pointFromEvent(e) {
    const rect = stageRef.current?.getBoundingClientRect();
    if (!rect || rect.width <= 0 || rect.height <= 0) {
      return { x: 0, y: 0 };
    }

    return {
      x: clampUnit((e.clientX - rect.left) / rect.width),
      y: clampUnit((e.clientY - rect.top) / rect.height),
    };
  }

  function handlePointerDown(e) {
    if (status !== "capturing") {
      return;
    }

    const point = pointFromEvent(e);
    setDragStart(point);
    onSelection({ x: point.x, y: point.y, w: 0.01, h: 0.01 });
    e.currentTarget.setPointerCapture?.(e.pointerId);
  }

  function handlePointerMove(e) {
    if (!dragStart) {
      return;
    }

    onSelection(normalizeSelection(dragStart, pointFromEvent(e)));
  }

  function handlePointerUp(e) {
    if (dragStart) {
      onSelection(normalizeSelection(dragStart, pointFromEvent(e)));
    }
    setDragStart(null);
    e.currentTarget.releasePointerCapture?.(e.pointerId);
  }

  return (
    <div
      ref={stageRef}
      style={cStyles.regionStage}
      tabIndex={0}
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={handlePointerUp}
    >
      {status === "capturing" ? (
        <>
          <video ref={videoRef} style={cStyles.regionVideo} muted playsInline autoPlay />
          {selection && (
            <div style={{
              ...cStyles.regionSelection,
              left: `${selection.x * 100}%`,
              top: `${selection.y * 100}%`,
              width: `${selection.w * 100}%`,
              height: `${selection.h * 100}%`,
            }} />
          )}
        </>
      ) : (
        <div style={cStyles.regionPlaceholder}>
          Capture a screen or window, then drag a box over the dialogue area.
        </div>
      )}
    </div>
  );
}

function WiredRegionPane({
  videoRef,
  status,
  selection,
  onSelection,
  image,
  blocks,
  ocrText,
  translation,
  loopEnabled,
  interval,
  onInterval,
  onStartCapture,
  onSnapshot,
  onStopCapture,
  onToggleLoop,
  busy,
}) {
  return (
    <div style={cStyles.stack}>
      <div style={cStyles.toolbar}>
        <Button onClick={onStartCapture} disabled={busy}>Capture Screen</Button>
        <Button variant="primary" onClick={onSnapshot} disabled={busy}>Snapshot Translate</Button>
        <Button onClick={onToggleLoop} disabled={busy && !loopEnabled}>{loopEnabled ? "Loop on" : "Loop off"}</Button>
        <Button onClick={onStopCapture} disabled={busy || status !== "capturing"}>Stop</Button>
        <div style={cStyles.interval}>
          <span className="vb-label">interval ms</span>
          <Input
            mono
            type="number"
            min="500"
            step="100"
            value={interval}
            onChange={(e) => onInterval(Number(e.target.value) || 1500)}
            style={{ width: "90px" }}
          />
        </div>
      </div>
      <RegionCaptureStage videoRef={videoRef} status={status} selection={selection} onSelection={onSelection} />
      {image && (
        <OcrOverlay
          image={image}
          blocks={blocks || []}
          overlayEnabled
          maskEnabled
          onImageChange={() => {}}
          showActions={false}
        />
      )}
      <div style={cStyles.grid}>
        <Field label="region ocr" htmlFor="regOcr">
          <Input id="regOcr" multiline rows={2} readOnly value={ocrText || ""} placeholder="" />
        </Field>
        <Field label="region translation" htmlFor="regTr">
          <Input id="regTr" multiline readOnly rows={2} value={translation || ""} placeholder="" />
        </Field>
      </div>
    </div>
  );
}

function Composer({
  mode,
  onMode,
  source,
  onSource,
  result,
  latency,
  busy,
  onRun,
  onClear,
  onTranslateOcrText,
  overlayEnabled,
  onOverlayToggle,
  maskEnabled,
  onMaskToggle,
  ocrImage,
  onOcrImage,
  ocrBlocks,
  ocrText,
  onOcrText,
  ocrTranslation,
}) {
  return (
    <section style={cStyles.composer}>
      <div style={cStyles.tabs}>
        {MODES.map((m) => (
          <Tab key={m.id} active={mode === m.id} onClick={() => onMode(m.id)}>
            {m.label}
          </Tab>
        ))}
      </div>

      {mode === "text" && <TextPane source={source} onSource={onSource} result={result} />}
      {(mode === "ocr" || mode === "pipe") && (
        <OcrPane
          overlayEnabled={overlayEnabled}
          onOverlayToggle={onOverlayToggle}
          maskEnabled={maskEnabled}
          onMaskToggle={onMaskToggle}
          ocrImage={ocrImage}
          onOcrImage={onOcrImage}
          ocrBlocks={ocrBlocks}
          ocrText={ocrText}
          onOcrText={onOcrText}
          ocrTranslation={ocrTranslation}
        />
      )}
      {(mode === "audio" || mode === "audioPipe") && (
        <WiredAudioPane
          sourceUrl={audioSourceUrl}
          onSourceUrl={onAudioSourceUrl}
          audioFileName={audioFileName}
          onAudioFile={onAudioFile}
          asrText={asrText}
          speechTranslation={speechTranslation}
          onCopySpeech={onCopySpeech}
          isPipeline={mode === "audioPipe"}
          busy={busy}
        />
      )}
      {mode === "region" && (
        <WiredRegionPane
          videoRef={regionVideoRef}
          status={regionStatus}
          selection={regionSelection}
          onSelection={onRegionSelection}
          image={regionImage}
          blocks={regionBlocks}
          ocrText={regionOcrText}
          translation={regionTranslation}
          loopEnabled={regionLoopEnabled}
          interval={regionInterval}
          onInterval={onRegionInterval}
          onStartCapture={onStartRegionCapture}
          onSnapshot={onRun}
          onStopCapture={onStopRegionCapture}
          onToggleLoop={onToggleRegionLoop}
          busy={busy}
        />
      )}

      <div style={cStyles.actions}>
        <div style={cStyles.left}>
          <Button variant="primary" commandKey=">" onClick={onRun} disabled={busy}>Run</Button>
          {mode === "ocr" && <Button onClick={onTranslateOcrText} disabled={busy}>Translate OCR Text</Button>}
          <Button onClick={onClear} disabled={busy}>Clear</Button>
        </div>
        <div style={cStyles.right}>
          {latency > 0 && <Pill>{latency} ms</Pill>}
        </div>
      </div>
    </section>
  );
}

const cStyles = {
  composer: {
    borderTop: "1px solid var(--vb-line)",
    background: "var(--vb-panel)",
    padding: "12px 16px 16px",
    display: "grid",
    gap: "12px",
  },
  tabs: { display: "flex", flexWrap: "wrap", gap: "4px" },
  grid: { display: "grid", gridTemplateColumns: "1fr 1fr", gap: "12px" },
  stack: { display: "grid", gap: "10px", alignContent: "start" },
  actions: { display: "flex", alignItems: "center", justifyContent: "space-between", gap: "10px" },
  left: { display: "flex", gap: "8px" },
  right: { display: "flex", gap: "6px" },
  dropZone: {
    display: "grid",
    justifyItems: "center",
    alignContent: "center",
    gap: "4px",
    minHeight: "118px",
    padding: "14px",
    border: "1px dashed var(--vb-line-strong)",
    borderRadius: "6px",
    background: "var(--vb-layer-2)",
    cursor: "pointer",
    textAlign: "center",
  },
  dropIcon: { color: "var(--vb-muted)", fontSize: "20px" },
  dropTitle: { color: "var(--vb-text)", fontSize: "13px", fontWeight: 500 },
  dropHint: { color: "var(--vb-muted)", fontSize: "11px" },
  fileRow: { display: "flex", alignItems: "center", gap: "8px" },
  hint: { color: "var(--vb-muted)", fontSize: "11px", fontFamily: "var(--font-mono)" },
  toolbar: { display: "flex", flexWrap: "wrap", alignItems: "center", gap: "8px" },
  interval: { display: "flex", alignItems: "center", gap: "8px", marginLeft: "auto" },
  regionStage: {
    display: "grid",
    placeItems: "center",
    position: "relative",
    minHeight: "260px",
    padding: 0,
    border: "1px solid var(--vb-line)",
    borderRadius: "6px",
    background: "var(--vb-bg)",
    color: "var(--vb-muted)",
    fontSize: "12px",
    textAlign: "center",
    overflow: "hidden",
    touchAction: "none",
    userSelect: "none",
  },
  regionVideo: {
    width: "100%",
    maxHeight: "360px",
    display: "block",
    objectFit: "contain",
    background: "#050505",
  },
  regionSelection: {
    position: "absolute",
    border: "2px solid var(--vb-blue)",
    background: "rgba(59,130,246,0.14)",
    boxShadow: "0 0 0 9999px rgba(0,0,0,0.24)",
    pointerEvents: "none",
  },
  regionPlaceholder: {
    padding: "12px",
  },
  overlayToolbar: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    flexWrap: "wrap",
  },
  toggleBtn: {
    display: "inline-flex",
    alignItems: "center",
    gap: "6px",
    minHeight: "28px",
    padding: "0 10px",
    border: "1px solid var(--vb-line)",
    borderRadius: "5px",
    background: "transparent",
    fontSize: "11px",
    fontFamily: "var(--font-sans)",
    fontWeight: 500,
    cursor: "pointer",
    transition: "all 0.15s ease",
  },
  overlayHint: {
    marginLeft: "auto",
    color: "var(--vb-muted)",
    fontSize: "11px",
    fontFamily: "var(--font-mono)",
  },
  collapseSection: {
    border: "1px solid var(--vb-line)",
    borderRadius: "6px",
    background: "var(--vb-bg)",
    overflow: "hidden",
  },
  collapseToggle: {
    display: "flex",
    alignItems: "center",
    gap: "8px",
    width: "100%",
    minHeight: "32px",
    padding: "0 12px",
    border: "none",
    background: "transparent",
    color: "var(--vb-text)",
    fontSize: "12px",
    fontFamily: "var(--font-sans)",
    fontWeight: 500,
    cursor: "pointer",
    transition: "background 0.15s ease",
  },
  collapseMeta: {
    marginLeft: "auto",
    color: "var(--vb-muted)",
    fontSize: "10px",
    fontFamily: "var(--font-mono)",
  },
};

Object.assign(window, { VbTerminal: Terminal, VbComposer: Composer, VbOcrOverlay: OcrOverlay, VbOcrOverlayBlock: OcrOverlayBlock });
