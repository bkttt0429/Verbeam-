"""Can CLAHE contrast-rescue pull て out of the tail probe WITHOUT grabbing rain?
Test on the て tail vs a rain-only control patch (false-positive check)."""
import sys
import cv2
import numpy as np
from columnizer import component_filter

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"

cap = cv2.VideoCapture(SRC)
fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
cap.set(cv2.CAP_PROP_POS_FRAMES, int(48.0 * fps))
ok, frame = cap.read(); cap.release()

_K15 = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (15, 15))
clahe = cv2.createCLAHE(clipLimit=3.0, tileGridSize=(8, 8))


def rescue_mask(bgr, op):
    """CLAHE the gray, tophat/blackhat, then a PERCENTILE threshold (more sensitive than Otsu),
    minimal morphology (no OPEN, so thin cursive strokes survive)."""
    gray = clahe.apply(cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY))
    m = cv2.morphologyEx(gray, op, _K15)
    thr = np.percentile(m, 92)                       # top ~8% response
    _, mask = cv2.threshold(m, max(20, thr), 255, cv2.THRESH_BINARY)
    return cv2.morphologyEx(mask, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_RECT, (3, 5)))


def probe(name, box, expect_local_cy):
    x0, y0, x1, y1 = box
    crop = frame[y0:y1, x0:x1]
    cw = x1 - x0
    for op_name, op in (("tophat", cv2.MORPH_TOPHAT), ("blackhat", cv2.MORPH_BLACKHAT)):
        mask = rescue_mask(crop, op)
        comps = component_filter(mask, crop.shape)
        # a glyph-like blob: area range, roughly column-centered, not a tall rain streak
        glyphs = [c for c in comps if 40 <= c.area <= 2000 and abs(c.cx - cw / 2) < cw * 0.5
                  and c.h / max(1, c.w) < 2.5]
        print(f"  [{name}/{op_name}] {len(comps)} comps, {len(glyphs)} glyph-like: "
              + ", ".join(f"cy{c.cy:.0f}/a{c.area}/{c.w}x{c.h}" for c in sorted(glyphs, key=lambda c: c.cy)))
        cv2.imwrite(rf"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\rescue_{name}_{op_name}.png",
                    cv2.resize(mask, None, fx=4, fy=4, interpolation=cv2.INTER_NEAREST))


print("TE tail (expect a glyph ~local cy 71):")
probe("te", (843, 744, 888, 804), 71)
print("RAIN control (blank diamond patch, expect ZERO glyph-like):")
probe("rain", (600, 620, 645, 700), None)   # a same-size pink+rain patch with no text
