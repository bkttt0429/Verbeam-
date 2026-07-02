"""Render an annotated mp4: detector -> deferral(+allowlist) -> TemporalBlockCache (Kalman + stale-guard +
quality gate) with real manga-ocr, drawing boxes + the cached OCR text on every frame. The cache holds text
across frames, so captions stay stable while the scene animates. This is the Realtime+Allowlist production
point (NEXT-STEPS-ROADMAP.md P1/P2/P2b): right-side art band deferred, white text panel allow-listed back.

    venv/Scripts/python.exe render_video.py                    # 4s preview window (default)
    venv/Scripts/python.exe render_video.py --full              # whole source video (slow: detector-bound)
    venv/Scripts/python.exe render_video.py --start 40 --duration 30
"""
import argparse
import sys, time
import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont

from block_detector import detect_text_blocks
from temporal_cache import TemporalBlockCache
from deferral import apply_deferral_regions
from reader_routes import JapaneseMangaOcrReader
from ocr_quality import ocr_quality

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
DEFER = [(1200, 0, 1920, 1080)]           # Realtime deferral ROI (right-side art band)
ALLOW = [(1200, 90, 1440, 520)]           # P2: rescue the white text panel from the band above

ap = argparse.ArgumentParser()
ap.add_argument("--start", type=float, default=48.0, help="start time in seconds")
ap.add_argument("--duration", type=float, default=4.0, help="seconds to render")
ap.add_argument("--full", action="store_true", help="render the entire source video (ignores --start/--duration)")
ap.add_argument("--out", default=None)
args = ap.parse_args()


def _font(sz):
    for p in (r"C:\Windows\Fonts\meiryo.ttc", r"C:\Windows\Fonts\msgothic.ttc"):
        try:
            return ImageFont.truetype(p, sz)
        except OSError:
            continue
    return ImageFont.load_default()


def ocr_block(frame, b, reader):
    cols = list(b.column_boxes_abs) if b.column_boxes_abs else [b.bbox]
    parts = []
    for (x0, y0, x1, y1) in cols:
        crop = frame[int(y0):int(y1), int(x0):int(x1)]
        if crop.size:
            parts.append(reader.read_bgr(crop))
    return "　".join(parts)


print("loading manga-ocr (force_cpu)...")
reader = JapaneseMangaOcrReader(force_cpu=True)
font = _font(28)
font_bar = _font(34)

cap = cv2.VideoCapture(SRC)
fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

if args.full:
    START, FRAMES = 0.0, total_frames
else:
    START, FRAMES = args.start, int(args.duration * fps)
OUT = args.out or (r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\full_video_realtimeallow.mp4"
                   if args.full else
                   r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\real_run_realtime.mp4")

start = int(START * fps)
vw = cv2.VideoWriter(OUT, cv2.VideoWriter_fourcc(*"mp4v"), fps, (W, H))

cache = TemporalBlockCache(stable_frames=2, expire_frames=3, center_r=20.0,
                           stable_by_kind={"column_seed": 3}, quality_fn=ocr_quality)
t_start = time.time()
ocr_total = 0
cap.set(cv2.CAP_PROP_POS_FRAMES, start)   # seek ONCE, then read sequentially (~15x faster than per-frame seek)
for i in range(FRAMES):
    ok, frame = cap.read()
    if not ok:
        break
    blocks = detect_text_blocks(frame, scorer="cc", group="graph", emit_seeds=True, confirm_raw=True)
    blocks, _ = apply_deferral_regions(blocks, DEFER, allow_regions=ALLOW)
    res = cache.update(blocks, ocr_fn=lambda b: ocr_block(frame, b, reader), frame=frame)
    ocr_total += sum(r["ocr_called"] for r in res)

    img = frame.copy()
    for r in res:
        x0, y0, x1, y1 = [int(v) for v in r["bbox"]]
        cv2.rectangle(img, (x0, y0), (x1, y1), (0, 230, 0), 2)
    pil = Image.fromarray(cv2.cvtColor(img, cv2.COLOR_BGR2RGB))
    draw = ImageDraw.Draw(pil)
    captions = []
    for r in res:
        if r["text"]:
            x0, y0 = int(r["bbox"][0]), int(r["bbox"][1])
            draw.text((x0, max(0, y0 - 32)), r["text"], fill=(255, 255, 0), font=font)
            captions.append(r["text"])
    # bottom subtitle bar (joined current captions)
    if captions:
        bar = "　".join(captions)
        draw.rectangle([0, H - 60, W, H], fill=(0, 0, 0))
        draw.text((20, H - 52), bar[:80], fill=(255, 255, 255), font=font_bar)
    draw.text((20, 16), f"Realtime+Allowlist  t={START + i / fps:5.2f}s  OCR so far={ocr_total}",
              fill=(0, 255, 128), font=font)
    vw.write(cv2.cvtColor(np.array(pil), cv2.COLOR_RGB2BGR))
    if i % 50 == 0:
        elapsed = time.time() - t_start
        eta = (elapsed / (i + 1)) * (FRAMES - i - 1)
        print(f"  frame {i}/{FRAMES}  ocr_total={ocr_total}  elapsed={elapsed:.0f}s  eta={eta:.0f}s")

cap.release()
vw.release()
print(f"\nDONE  {OUT}")
print(f"frames={FRAMES}  total OCR calls={ocr_total}  render_time={time.time()-t_start:.0f}s")
