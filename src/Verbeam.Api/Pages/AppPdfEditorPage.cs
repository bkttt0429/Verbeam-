namespace Verbeam.Api.Pages;

/// <summary>
/// Standalone PDF layout-preserving translation overlay editor, served at
/// <c>/pdf-editor?job={jobId}</c>. Renders the original PDF with PDF.js as the backdrop,
/// overlays the translation blocks from <c>/ocr/document-jobs/{job}/pages</c> (normalized
/// 0..1 geometry), and lets the user retype / move / resize / lock / ignore each block
/// (persisted to <c>/ocr/blocks/annotations</c> + <c>/ocr/blocks/layout</c>) and export a
/// layout-preserving PDF. Vanilla JS + vendored PDF.js; kept separate from the 20k-line
/// AppWorkbenchPage so it can be reviewed and run in isolation.
/// </summary>
public static class AppPdfEditorPage
{
    public const string Html = """
<!DOCTYPE html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Verbeam · PDF 疊加翻譯編輯器</title>
<style>
  :root { --bg:#1d2127; --panel:#272c34; --line:#3a414c; --fg:#e7ebf0; --muted:#9aa4b2; --accent:#4c8dff; --edited:#37c871; --ignored:#7a828e; --danger:#ff6b6b; }
  * { box-sizing: border-box; }
  body { margin:0; font-family:"Segoe UI",system-ui,"Microsoft JhengHei",sans-serif; background:var(--bg); color:var(--fg); height:100vh; display:flex; flex-direction:column; }
  header { display:flex; align-items:center; gap:12px; padding:8px 14px; border-bottom:1px solid var(--line); background:var(--panel); flex:0 0 auto; }
  header .title { font-weight:600; }
  header .spacer { flex:1; }
  button { background:#323845; color:var(--fg); border:1px solid var(--line); border-radius:6px; padding:6px 10px; cursor:pointer; font-size:13px; }
  button:hover { background:#3a414c; }
  button.primary { background:var(--accent); border-color:var(--accent); color:#fff; }
  button:disabled { opacity:.5; cursor:default; }
  .pagenav { display:flex; align-items:center; gap:6px; }
  #pageLabel { min-width:78px; text-align:center; color:var(--muted); font-size:13px; }
  main { flex:1; display:flex; min-height:0; }
  #stage { flex:1; overflow:auto; padding:18px; display:flex; justify-content:center; align-items:flex-start; }
  #canvasWrap { position:relative; box-shadow:0 2px 18px rgba(0,0,0,.5); background:#fff; }
  #pdfCanvas { display:block; }
  #overlay { position:absolute; inset:0; }
  .blk { position:absolute; border:1px solid rgba(76,141,255,.45); background:rgba(76,141,255,.08); color:#111; overflow:hidden; cursor:move; font-size:11px; line-height:1.15; padding:1px 2px; }
  .blk.edited { border-color:var(--edited); background:rgba(55,200,113,.12); }
  .blk.locked { border-color:#d9a441; }
  .blk.selected { outline:2px solid var(--accent); outline-offset:-1px; z-index:5; background:rgba(76,141,255,.16); }
  .blk .handle { position:absolute; width:9px; height:9px; background:var(--accent); border:1px solid #fff; border-radius:2px; }
  .blk .handle.se { right:-5px; bottom:-5px; cursor:se-resize; }
  .blk .handle.ne { right:-5px; top:-5px; cursor:ne-resize; }
  .blk .handle.sw { left:-5px; bottom:-5px; cursor:sw-resize; }
  .blk .handle.nw { left:-5px; top:-5px; cursor:nw-resize; }
  aside { flex:0 0 320px; border-left:1px solid var(--line); background:var(--panel); padding:14px; overflow:auto; display:flex; flex-direction:column; gap:10px; }
  aside h2 { font-size:13px; margin:0; color:var(--muted); text-transform:uppercase; letter-spacing:.04em; }
  aside label { font-size:12px; color:var(--muted); display:block; margin-bottom:3px; }
  aside textarea, aside input, aside select { width:100%; background:#1f242b; color:var(--fg); border:1px solid var(--line); border-radius:6px; padding:6px; font-size:13px; font-family:inherit; }
  aside textarea { resize:vertical; }
  .row { display:flex; gap:8px; }
  .row > * { flex:1; }
  .src { white-space:pre-wrap; background:#1f242b; border:1px solid var(--line); border-radius:6px; padding:6px; font-size:12px; color:var(--muted); max-height:120px; overflow:auto; }
  #status { font-size:12px; color:var(--muted); padding:0 14px 8px; }
  .empty { color:var(--muted); font-size:13px; }
  .badge { font-size:11px; padding:1px 6px; border-radius:10px; border:1px solid var(--line); color:var(--muted); }
</style>
</head>
<body>
<header>
  <span class="title">PDF 疊加翻譯編輯器</span>
  <span id="jobBadge" class="badge">no job</span>
  <span class="spacer"></span>
  <div class="pagenav">
    <button id="prevPage">◀</button>
    <span id="pageLabel">– / –</span>
    <button id="nextPage">▶</button>
  </div>
  <button id="exportOverlay" class="primary">匯出疊加 PDF</button>
</header>
<div id="status">載入中…</div>
<main>
  <div id="stage"><div id="canvasWrap"><canvas id="pdfCanvas"></canvas><div id="overlay"></div></div></div>
  <aside id="inspector"><div class="empty">點選一個區塊以編輯。</div></aside>
</main>

<script type="module">
import * as pdfjsLib from "/vendor/pdfjs/pdf.min.mjs";
pdfjsLib.GlobalWorkerOptions.workerSrc = "/vendor/pdfjs/pdf.worker.min.mjs";

const qs = new URLSearchParams(location.search);
const JOB = qs.get("job") || "";
const PROFILE = qs.get("profile") || "default";

const els = {
  status: document.getElementById("status"),
  jobBadge: document.getElementById("jobBadge"),
  wrap: document.getElementById("canvasWrap"),
  canvas: document.getElementById("pdfCanvas"),
  overlay: document.getElementById("overlay"),
  inspector: document.getElementById("inspector"),
  pageLabel: document.getElementById("pageLabel"),
  prev: document.getElementById("prevPage"),
  next: document.getElementById("nextPage"),
  exportOverlay: document.getElementById("exportOverlay"),
};

const state = { pdf:null, pagesMeta:[], pageIndex:0, target:"zh-TW", selectedId:null };

function setStatus(msg, isError) { els.status.textContent = msg; els.status.style.color = isError ? "var(--danger)" : "var(--muted)"; }

async function jget(url) { const r = await fetch(url); if (!r.ok) throw new Error(url + " → " + r.status); return r.json(); }
async function jpost(url, body) {
  const r = await fetch(url, { method:"POST", headers:{ "Content-Type":"application/json" }, body: JSON.stringify(body) });
  if (!r.ok) throw new Error(url + " → " + r.status);
  return r.json();
}

async function boot() {
  if (!JOB) { setStatus("缺少 ?job=<jobId> 參數。", true); return; }
  els.jobBadge.textContent = JOB.slice(0, 8);
  try {
    const job = await jget(`/ocr/document-jobs/${JOB}`);
    state.target = job.target || job.Target || "zh-TW";
    const artifacts = job.artifacts || job.Artifacts || [];
    const sourcePdf = artifacts.find(a => (a.kind||a.Kind) === "source-pdf");
    if (!sourcePdf) { setStatus("這個工作沒有 source-pdf 產物（需重新建立 PDF 工作後再開啟）。", true); return; }
    const pid = sourcePdf.id || sourcePdf.Id;
    const buf = await (await fetch(`/ocr/document-jobs/${JOB}/artifacts/${pid}`)).arrayBuffer();
    state.pdf = await pdfjsLib.getDocument({ data: buf }).promise;

    const pagesResp = await jget(`/ocr/document-jobs/${JOB}/pages?profile=${encodeURIComponent(PROFILE)}`);
    state.pagesMeta = pagesResp.pages || [];
    if (!state.pagesMeta.length) { setStatus("這個工作沒有可編輯的版面區塊。", true); return; }
    state.pageIndex = 0;
    await renderPage();
    setStatus(`已載入 ${state.pagesMeta.length} 頁。`);
  } catch (e) { setStatus("載入失敗：" + e.message, true); }
}

async function renderPage() {
  const meta = state.pagesMeta[state.pageIndex];
  els.pageLabel.textContent = `${state.pageIndex + 1} / ${state.pagesMeta.length}`;
  els.prev.disabled = state.pageIndex <= 0;
  els.next.disabled = state.pageIndex >= state.pagesMeta.length - 1;
  state.selectedId = null;

  const page = await state.pdf.getPage((meta.pageIndex ?? state.pageIndex) + 1);
  const stageWidth = Math.max(360, document.getElementById("stage").clientWidth - 36);
  const unscaled = page.getViewport({ scale: 1 });
  const scale = Math.min(1.8, stageWidth / unscaled.width);
  const viewport = page.getViewport({ scale });
  const ctx = els.canvas.getContext("2d");
  els.canvas.width = Math.floor(viewport.width);
  els.canvas.height = Math.floor(viewport.height);
  els.wrap.style.width = els.canvas.width + "px";
  els.wrap.style.height = els.canvas.height + "px";
  await page.render({ canvasContext: ctx, viewport }).promise;

  renderOverlay(meta);
  renderInspector(null);
}

function renderOverlay(meta) {
  els.overlay.innerHTML = "";
  for (const b of (meta.blocks || [])) {
    if ((b.status || "translated") === "ignored") continue;
    const div = document.createElement("div");
    div.className = "blk" + (b.status === "edited" ? " edited" : "") + (b.locked ? " locked" : "");
    div.dataset.id = b.id;
    const box = b.box || { nx:0, ny:0, nw:0, nh:0 };
    place(div, box);
    div.textContent = b.text || "";
    div.addEventListener("pointerdown", (ev) => onBlockPointerDown(ev, b, div, "move"));
    for (const corner of ["nw","ne","sw","se"]) {
      const h = document.createElement("div");
      h.className = "handle " + corner;
      h.addEventListener("pointerdown", (ev) => { ev.stopPropagation(); onBlockPointerDown(ev, b, div, corner); });
      div.appendChild(h);
    }
    els.overlay.appendChild(div);
  }
  highlightSelection();
}

function place(div, box) {
  div.style.left = (box.nx * 100).toFixed(3) + "%";
  div.style.top = (box.ny * 100).toFixed(3) + "%";
  div.style.width = (box.nw * 100).toFixed(3) + "%";
  div.style.height = (box.nh * 100).toFixed(3) + "%";
}

function currentBlock(id) {
  const meta = state.pagesMeta[state.pageIndex];
  return (meta.blocks || []).find(b => b.id === id);
}

function highlightSelection() {
  for (const el of els.overlay.children) el.classList.toggle("selected", el.dataset.id === state.selectedId);
}

let drag = null;
function onBlockPointerDown(ev, block, div, mode) {
  ev.preventDefault();
  state.selectedId = block.id;
  highlightSelection();
  renderInspector(block);
  if (block.locked) return; // locked blocks: select only, no move/resize
  const rect = els.overlay.getBoundingClientRect();
  drag = { block, div, mode, rect, startX: ev.clientX, startY: ev.clientY, box: { ...(block.box) } };
  div.setPointerCapture(ev.pointerId);
  div.addEventListener("pointermove", onBlockPointerMove);
  div.addEventListener("pointerup", onBlockPointerUp);
}

function onBlockPointerMove(ev) {
  if (!drag) return;
  const dx = (ev.clientX - drag.startX) / drag.rect.width;
  const dy = (ev.clientY - drag.startY) / drag.rect.height;
  let { nx, ny, nw, nh } = drag.box;
  if (drag.mode === "move") { nx += dx; ny += dy; }
  else {
    if (drag.mode.includes("e")) nw += dx;
    if (drag.mode.includes("s")) nh += dy;
    if (drag.mode.includes("w")) { nx += dx; nw -= dx; }
    if (drag.mode.includes("n")) { ny += dy; nh -= dy; }
  }
  nw = Math.max(0.01, nw); nh = Math.max(0.01, nh);
  nx = Math.min(Math.max(0, nx), 1 - nw); ny = Math.min(Math.max(0, ny), 1 - nh);
  drag.live = { nx, ny, nw, nh };
  place(drag.div, drag.live);
}

async function onBlockPointerUp(ev) {
  if (!drag) return;
  drag.div.removeEventListener("pointermove", onBlockPointerMove);
  drag.div.removeEventListener("pointerup", onBlockPointerUp);
  const moved = drag.live;
  const block = drag.block;
  drag = null;
  if (!moved) return;
  block.box = moved;
  try {
    await jpost("/ocr/blocks/layout", {
      profile: PROFILE, docKey: state.pagesMeta[state.pageIndex].docKey, blockId: block.id,
      nx: moved.nx, ny: moved.ny, nw: moved.nw, nh: moved.nh,
      overflow: block.overflow || "shrink"
    });
    setStatus("已儲存區塊位置。");
  } catch (e) { setStatus("位置儲存失敗：" + e.message, true); }
}

function renderInspector(block) {
  if (!block) { els.inspector.innerHTML = '<div class="empty">點選一個區塊以編輯。</div>'; return; }
  const esc = (s) => (s || "").replace(/[&<>]/g, c => ({ "&":"&amp;", "<":"&lt;", ">":"&gt;" }[c]));
  els.inspector.innerHTML = `
    <h2>區塊 ${esc(block.id)}</h2>
    <div><label>原文</label><div class="src">${esc(block.sourceText)}</div></div>
    <div><label>譯文</label><textarea id="inspText" rows="5">${esc(block.text)}</textarea></div>
    <div class="row">
      <div><label>對齊</label><select id="inspAlign">
        <option value="left">靠左</option><option value="center">置中</option><option value="right">靠右</option></select></div>
      <div><label>溢出</label><select id="inspOverflow">
        <option value="shrink">縮字</option><option value="wrap">換行</option></select></div>
    </div>
    <button id="inspSave" class="primary">儲存譯文</button>
    <div class="row">
      <button id="inspLock">${block.locked ? "解除鎖定" : "鎖定"}</button>
      <button id="inspIgnore">忽略</button>
    </div>`;
  document.getElementById("inspAlign").value = block.align || "left";
  document.getElementById("inspOverflow").value = block.overflow || "shrink";
  document.getElementById("inspSave").onclick = () => saveTranslation(block);
  document.getElementById("inspLock").onclick = () => toggleLock(block);
  document.getElementById("inspIgnore").onclick = () => ignoreBlock(block);
}

async function saveAnnotation(block, patch) {
  return jpost("/ocr/blocks/annotations", Object.assign({
    profile: PROFILE, imageHash: state.pagesMeta[state.pageIndex].docKey, blockId: block.id
  }, patch));
}

async function saveTranslation(block) {
  const text = document.getElementById("inspText").value;
  const align = document.getElementById("inspAlign").value;
  const overflow = document.getElementById("inspOverflow").value;
  try {
    await saveAnnotation(block, { editedTranslation: text, status: "edited" });
    await jpost("/ocr/blocks/layout", { profile: PROFILE, docKey: state.pagesMeta[state.pageIndex].docKey, blockId: block.id, textAlign: align, overflow });
    block.text = text; block.status = "edited"; block.align = align; block.overflow = overflow;
    renderOverlay(state.pagesMeta[state.pageIndex]);
    setStatus("已儲存譯文。");
  } catch (e) { setStatus("儲存失敗：" + e.message, true); }
}

async function toggleLock(block) {
  const next = !block.locked;
  try {
    await saveAnnotation(block, { locked: next, status: next ? "locked" : "edited", editedTranslation: block.text });
    block.locked = next; if (!next) block.status = "edited";
    renderOverlay(state.pagesMeta[state.pageIndex]); renderInspector(block);
    setStatus(next ? "已鎖定區塊。" : "已解除鎖定。");
  } catch (e) { setStatus("鎖定失敗：" + e.message, true); }
}

async function ignoreBlock(block) {
  try {
    await saveAnnotation(block, { status: "ignored" });
    block.status = "ignored"; state.selectedId = null;
    renderOverlay(state.pagesMeta[state.pageIndex]); renderInspector(null);
    setStatus("已忽略區塊（不會出現在匯出）。");
  } catch (e) { setStatus("忽略失敗：" + e.message, true); }
}

els.prev.onclick = async () => { if (state.pageIndex > 0) { state.pageIndex--; await renderPage(); } };
els.next.onclick = async () => { if (state.pageIndex < state.pagesMeta.length - 1) { state.pageIndex++; await renderPage(); } };

els.exportOverlay.onclick = async () => {
  els.exportOverlay.disabled = true;
  setStatus("正在匯出疊加 PDF…");
  try {
    const r = await jpost(`/ocr/document-jobs/${JOB}/export`, { engine: "overlay", variant: "mono", target: state.target, profile: PROFILE });
    const aid = r.artifactId || r.ArtifactId;
    setStatus("匯出完成，開始下載。");
    window.open(`/ocr/document-jobs/${JOB}/artifacts/${aid}`, "_blank");
  } catch (e) { setStatus("匯出失敗：" + e.message, true); }
  finally { els.exportOverlay.disabled = false; }
};

boot();
</script>
</body>
</html>
""";
}
