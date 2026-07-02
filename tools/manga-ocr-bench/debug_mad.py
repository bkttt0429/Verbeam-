"""Measure crop-diff MAD distributions to pick the stale-guard threshold (disposable analysis).

same-content:      the SAME caption region sampled across frames (includes rain animation + drift noise)
different-content: a caption thumb vs OTHER captions / art / blank regions (what a dialogue change looks like)

The threshold must sit between the two distributions or the guard doesn't work.
"""
import sys
import cv2
import numpy as np

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


def thumb(frame, bbox):
    x0, y0, x1, y1 = [int(v) for v in bbox]
    h, w = frame.shape[:2]
    x0, y0, x1, y1 = max(0, x0), max(0, y0), min(w, x1), min(h, y1)
    crop = frame[y0:y1, x0:x1]
    if crop.size == 0:
        return None
    return cv2.resize(cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY), (32, 32)).astype(np.int16)


def mad(a, b):
    return float(np.mean(np.abs(a - b)))


KATA = (859, 545, 903, 792)     # 語っといて (OCR'd bbox, incl て)
KORE = (784, 161, 822, 256)     # これ
SAN = (925, 471, 1004, 880)     # 散々ワガママ
ART = (1463, 418, 1703, 776)    # 視感 art region
BLANK = (600, 620, 645, 700)    # blank pink diamond + rain
WHITE = (1215, 112, 1435, 506)  # 何がそんな不満なんだ white card

print("=== same-content MAD (KATATTOITE region, frame0 thumb vs frames 1..14 — rain/drift noise floor) ===")
ref = thumb(frames[0], KATA)
same = [mad(ref, thumb(frames[i], KATA)) for i in range(1, len(frames))]
print("  " + "  ".join(f"{v:.1f}" for v in same))
print(f"  min={min(same):.1f} median={sorted(same)[len(same)//2]:.1f} max={max(same):.1f}")

print("\n=== same-content MAD for other regions (frame0 vs frame7/14) ===")
for name, bb in (("KORE", KORE), ("SAN", SAN), ("WHITE", WHITE), ("ART", ART), ("BLANK", BLANK)):
    r = thumb(frames[0], bb)
    vals = [mad(r, thumb(frames[i], bb)) for i in (7, 14)]
    print(f"  {name:<6} {vals[0]:.1f} / {vals[1]:.1f}")

print("\n=== different-content MAD (dialogue-change proxy: caption thumb vs OTHER content, frame 0) ===")
ref_k = thumb(frames[0], KATA)
for name, bb in (("KATA vs KORE", KORE), ("KATA vs SAN", SAN), ("KATA vs WHITE", WHITE),
                 ("KATA vs ART", ART), ("KATA vs BLANK(text gone)", BLANK)):
    print(f"  {name:<26} {mad(ref_k, thumb(frames[0], bb)):.1f}")
ref_w = thumb(frames[0], WHITE)
print(f"  {'WHITE vs SAN':<26} {mad(ref_w, thumb(frames[0], SAN)):.1f}")
print(f"  {'WHITE vs BLANK':<26} {mad(ref_w, thumb(frames[0], BLANK)):.1f}")
