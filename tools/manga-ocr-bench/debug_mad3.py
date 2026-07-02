"""Crop-diff take 3: thumb at the TRACKED bbox (detected x-range × track's union y-extent), which follows
the drifting caption, instead of a fixed screen box. Same-content noise should collapse to detection
jitter; text-swap (KATA vs SAN) should stay high. Also try ±2px shift-search min-MAD for extra robustness."""
import sys
import cv2
import numpy as np
from block_detector import detect_text_blocks

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"

cap = cv2.VideoCapture(SRC)
fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
start = int(48.0 * fps)
frames = []
for i in range(15):
    cap.set(cv2.CAP_PROP_POS_FRAMES, start + i)
    ok, f = cap.read()
    if ok:
        frames.append(f)
cap.release()

# per-frame DETECTED bboxes for the two same-diamond columns (KATA drifts x 843->872)
kata_det, san_det = {}, {}
for i, f in enumerate(frames):
    for b in detect_text_blocks(f, scorer="cc", group="graph", emit_seeds=True, confirm_raw=True):
        x0, y0 = b.bbox[0], b.bbox[1]
        if 830 <= x0 <= 920 and 500 <= y0 <= 600:
            kata_det[i] = b.bbox
        if 900 <= x0 <= 1010 and 440 <= y0 <= 520:
            san_det[i] = b.bbox

print("KATA detected on frames:", sorted(kata_det), " SAN on:", sorted(san_det))

KATA_Y = (545, 797)   # track union y-extent (what the cache's y_top/y_bot would hold)
SAN_Y = (471, 880)


def thumb(frame, det_bbox, y_union):
    x0, _, x1, _ = [int(v) for v in det_bbox]
    y0, y1 = y_union
    h, w = frame.shape[:2]
    c = frame[max(0, y0):min(h, y1), max(0, x0):min(w, x1)]
    g = cv2.cvtColor(c, cv2.COLOR_BGR2GRAY)
    return cv2.resize(g, (24, 96)).astype(np.float32)


def mad(a, b):
    return float(np.mean(np.abs(a - b)))


def mad_shift(a, b, r=2):
    """min MAD over ±r px 2D shifts of b (crop the overlap so sizes match)."""
    best = 1e9
    H, W = a.shape
    for dy in range(-r, r + 1):
        for dx in range(-r, r + 1):
            ya0, yb0 = max(0, dy), max(0, -dy)
            xa0, xb0 = max(0, dx), max(0, -dx)
            hh, ww = H - abs(dy), W - abs(dx)
            best = min(best, mad(a[ya0:ya0 + hh, xa0:xa0 + ww], b[yb0:yb0 + hh, xb0:xb0 + ww]))
    return best


f0 = sorted(kata_det)[0]
ref = thumb(frames[f0], kata_det[f0], KATA_Y)
print("\n=== same-content (tracked bbox): KATA frame", f0, "vs later detected frames ===")
same_plain, same_shift = [], []
for i in sorted(kata_det):
    if i == f0:
        continue
    t = thumb(frames[i], kata_det[i], KATA_Y)
    same_plain.append(mad(ref, t)); same_shift.append(mad_shift(ref, t))
    print(f"  f{i}: plain={same_plain[-1]:.1f}  shift±2={same_shift[-1]:.1f}")

print("\n=== different-content: KATA vs SAN (each at its own tracked bbox, same frame) ===")
diff_plain, diff_shift = [], []
for i in sorted(set(kata_det) & set(san_det)):
    a = thumb(frames[i], kata_det[i], KATA_Y)
    b = thumb(frames[i], san_det[i], SAN_Y)
    diff_plain.append(mad(a, b)); diff_shift.append(mad_shift(a, b))
    print(f"  f{i}: plain={diff_plain[-1]:.1f}  shift±2={diff_shift[-1]:.1f}")

if same_plain and diff_plain:
    print(f"\nplain   : max_same={max(same_plain):.1f}  min_diff={min(diff_plain):.1f}  "
          f"sep={min(diff_plain)/max(same_plain):.2f}")
    print(f"shift±2 : max_same={max(same_shift):.1f}  min_diff={min(diff_shift):.1f}  "
          f"sep={min(diff_shift)/max(same_shift):.2f}")
