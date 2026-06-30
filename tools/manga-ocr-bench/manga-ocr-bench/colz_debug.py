"""Diagnose the polarity-aware mask: dump the chosen-polarity mask + a column-box overlay,
so occupancy / alignment thresholds can be tuned by eye."""
import sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
import cv2
from columnizer import (
    _branch_mask,
    _lab_delta_mask,
    _otsu_polarity,
    columnize,
)

OUT = r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois"
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
cap = cv2.VideoCapture(SRC); fps = cap.get(cv2.CAP_PROP_FPS) or 24.0

# (tag, t, roi)
CASES = [
 ("5s_dark",  5.0,  (1330, 120, 1760, 820)),
 ("69s_rev",  69.0, (690, 400, 1230, 690)),    # white-on-dark horizontal 重い
 ("48s_ruby", 48.0, (1520, 360, 1810, 770)),   # light-on-color + furigana
 ("69s_rain", 69.0, (100, 100, 460, 460)),     # background rain (should be no_text)
 ("42s_purple", 42.0, (500, 460, 700, 850)),
 ("153s_blue", 153.0, (520, 80, 660, 500)),
 ("201s_pink", 201.0, (300, 90, 440, 480)),
]

def analyze(tag, t, box):
    cap.set(cv2.CAP_PROP_POS_FRAMES, int(t * fps)); _ok, fr = cap.read()
    x0, y0, x1, y1 = box; roi = fr[y0:y1, x0:x1]
    gray = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
    gray_pol = _otsu_polarity(gray)
    gray_op = cv2.MORPH_BLACKHAT if gray_pol == "dark_on_light" else cv2.MORPH_TOPHAT
    gray_mask = _branch_mask(gray, gray_op)
    lab_mask = _lab_delta_mask(roi)
    r = columnize(roi)
    overlay = roi.copy()
    color = (0, 0, 255) if r["status"] == "ok" else (0, 165, 255)
    for c in r["columns"]:
        bx0, by0, bx1, by1 = c["bbox"]
        cv2.rectangle(overlay, (bx0, by0), (bx1, by1), color, 2)
    cv2.imwrite(f"{OUT}\\dbg_{tag}_graymask.png", gray_mask)
    cv2.imwrite(f"{OUT}\\dbg_{tag}_labmask.png", lab_mask)
    if r["mask"] is not None:
        cv2.imwrite(f"{OUT}\\dbg_{tag}_chosenmask.png", r["mask"])
    cv2.imwrite(f"{OUT}\\dbg_{tag}_overlay.png", overlay)
    print(f"{tag}: polarity={r['polarity']} source={r['mask_dbg'].get('source')} "
          f"{r['mask_dbg']}  layout={r['layout']}  status={r['status']}  cols={len(r['columns'])}")

for tag, t, box in CASES:
    analyze(tag, t, box)
cap.release()
