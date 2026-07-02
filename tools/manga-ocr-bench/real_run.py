"""REAL end-to-end run on the test clip: detector -> deferral -> TemporalBlockCache with a real manga-ocr
ocr_fn on raw column crops. Runs both operating points, writes a results .md + one annotated frame each.

    venv/Scripts/python.exe real_run.py
"""
import sys, time
import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

import json

from block_detector import detect_text_blocks
from temporal_cache import TemporalBlockCache
from deferral import apply_deferral_regions
from reader_routes import JapaneseMangaOcrReader
from eval_captions import evaluate, MANIFEST

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
OUT_MD = r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\real_run_results.md"
ROIS = r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois"
START, FRAMES = 48.0, 15


def _font(sz):
    for p in (r"C:\Windows\Fonts\meiryo.ttc", r"C:\Windows\Fonts\msgothic.ttc",
              r"C:\Windows\Fonts\YuGothM.ttc", r"C:\Windows\Fonts\YuGothR.ttc"):
        try:
            return ImageFont.truetype(p, sz)
        except OSError:
            continue
    return ImageFont.load_default()


def where(bbox):
    x0, y0 = bbox[0], bbox[1]
    if 740 <= x0 <= 800 and y0 < 300: return "KORE(これ)"
    if 830 <= x0 <= 860 and y0 < 600: return "KATATTOITE(語っといて)"
    if 1200 <= x0 <= 1440 and y0 < 520: return "WHITEBOX(何がそんな不満なんだ)"
    return None


def ocr_block(frame, b, reader):
    """Read each raw column crop of a block and join (manga-ocr wants one vertical column per call)."""
    cols = list(b.column_boxes_abs) if b.column_boxes_abs else [b.bbox]
    parts = []
    for (x0, y0, x1, y1) in cols:
        crop = frame[int(y0):int(y1), int(x0):int(x1)]
        if crop.size:
            parts.append(reader.read_bgr(crop))
    return "　".join(parts)


def run_mode(name, frames, reader, defer_rects, log, allow_rects=None):
    cache = TemporalBlockCache(stable_frames=2, expire_frames=3, center_r=20.0,
                               stable_by_kind={"column_seed": 3})
    total_ocr = 0
    deferred_total = 0
    captions = {}
    ocr_results = []  # for eval_captions.evaluate: {"bbox", "text"} per OCR call
    last = None  # (frame, res) of final frame for the annotated render
    log.append(f"\n## {name}  (center_r=20, column_seed count=3, defer={defer_rects or 'none'}, "
               f"allow={allow_rects or 'none'})\n")
    log.append("frame  t      kind          bbox                       ms    text")
    for i, frame in enumerate(frames):
        blocks, deferred = apply_deferral_regions(frame_blocks[i], defer_rects, allow_regions=allow_rects)
        deferred_total += len(deferred)
        t0 = time.perf_counter()
        res = cache.update(blocks, ocr_fn=lambda b: ocr_block(frame, b, reader), frame=frame)
        for b, r in zip(blocks, res):
            if r["ocr_called"]:
                total_ocr += 1
                ms = ""  # per-call ms captured inside reader; keep table compact
                t = (START * 24.0 + i) / 24.0
                # log r["bbox"] (the actual OCR'd geometry, incl. any cache tail-extension), not the raw block
                log.append(f"{i:<5d}  {i:<5d}  {b.kind:<12}  {str(r['bbox']):<26}  {'':<4}  {r['text']!r}")
                ocr_results.append({"bbox": r["bbox"], "text": r["text"]})
                w = where(b.bbox)
                if w:
                    captions[w] = r["text"]
        last = (frame, res)
    log.append(f"\n**{name}: total OCR = {total_ocr}**")
    for k in ("KORE(これ)", "KATATTOITE(語っといて)", "WHITEBOX(何がそんな不満なんだ)"):
        log.append(f"  - {k}: {captions.get(k, '<<NOT READ>>')!r}")

    with open(MANIFEST, encoding="utf-8") as fh:
        expected = json.load(fh)
    metrics = evaluate(expected, ocr_results, deferred_count=deferred_total)
    log.append(f"  - eval: {metrics}")

    # annotated render of the final frame (boxes + cached text)
    frame, res = last
    img = frame.copy()
    for r in res:
        x0, y0, x1, y1 = [int(v) for v in r["bbox"]]
        cv2.rectangle(img, (x0, y0), (x1, y1), (0, 230, 0), 2)
    pil = Image.fromarray(cv2.cvtColor(img, cv2.COLOR_BGR2RGB))
    draw = ImageDraw.Draw(pil)
    font = _font(26)
    for r in res:
        if r["text"]:
            x0, y0 = int(r["bbox"][0]), int(r["bbox"][1])
            draw.text((x0, max(0, y0 - 30)), r["text"], fill=(255, 255, 0), font=font)
    out_png = rf"{ROIS}\real_run_{name.split()[0].lower()}.png"
    pil.save(out_png)
    return total_ocr, out_png


print("loading manga-ocr (force_cpu)...")
reader = JapaneseMangaOcrReader(force_cpu=True)

cap = cv2.VideoCapture(SRC)
fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
start = int(START * fps)
frames = []
for i in range(FRAMES):
    cap.set(cv2.CAP_PROP_POS_FRAMES, start + i)
    ok, f = cap.read()
    if ok:
        frames.append(f)
cap.release()

# detect once per frame (reused by both modes; detector is deterministic)
print("detecting...")
frame_blocks = [detect_text_blocks(f, scorer="cc", group="graph", emit_seeds=True, confirm_raw=True)
                for f in frames]

log = [f"# Real manga-ocr run — {SRC}", f"window {START}s, {len(frames)} frames @ {fps:.0f}fps, "
       f"raw column crops -> manga-ocr (CPU)"]
print("Full Recall...");  fr_ocr, fr_png = run_mode("FullRecall", frames, reader, [], log)
print("Realtime...");     rt_ocr, rt_png = run_mode("Realtime", frames, reader,
                                                     [(1200, 0, 1920, 1080)], log)
print("Realtime+Allowlist...")
ra_ocr, ra_png = run_mode("RealtimeAllow", frames, reader, [(1200, 0, 1920, 1080)], log,
                          allow_rects=[(1200, 90, 1440, 520)])   # P2: rescue the whitebox caption

with open(OUT_MD, "w", encoding="utf-8") as fh:
    fh.write("\n".join(log) + "\n")
print(f"\nRESULTS  -> {OUT_MD}")
print(f"FullRecall render -> {fr_png}  (OCR={fr_ocr})")
print(f"RealtimeAllow render -> {ra_png}  (OCR={ra_ocr})")
print(f"Realtime  render -> {rt_png}  (OCR={rt_ocr})")
