"""Variant shoot-out for the crop-diff signal: which thumb representation separates
same-content (max over frames, incl. rain/drift) from different-content (min, incl. the hard
same-style text swap KATA<->SAN)? Pick the variant with the largest separation ratio."""
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

KATA = (859, 545, 903, 792)
KORE = (784, 161, 822, 256)
SAN = (925, 471, 1004, 880)
WHITE = (1215, 112, 1435, 506)


def crop_gray(frame, bbox):
    x0, y0, x1, y1 = [int(v) for v in bbox]
    h, w = frame.shape[:2]
    c = frame[max(0, y0):min(h, y1), max(0, x0):min(w, x1)]
    return cv2.cvtColor(c, cv2.COLOR_BGR2GRAY)


def v_raw32(g):    return cv2.resize(g, (32, 32)).astype(np.int16)
def v_col(g):      return cv2.resize(g, (24, 96)).astype(np.int16)          # column aspect, glyphs survive
def v_colnorm(g):  t = cv2.resize(g, (24, 96)).astype(np.float32); return t - t.mean()
def v_sobel(g):
    t = cv2.resize(g, (24, 96)).astype(np.float32)
    gx = cv2.Sobel(t, cv2.CV_32F, 1, 0); gy = cv2.Sobel(t, cv2.CV_32F, 0, 1)
    return np.sqrt(gx * gx + gy * gy)


def mad(a, b): return float(np.mean(np.abs(a - b)))


VARIANTS = [("raw32", v_raw32), ("col24x96", v_col), ("colnorm", v_colnorm), ("sobel", v_sobel)]

print(f"{'variant':<10} {'same:KATA max':<14} {'same:WHITE max':<15} {'diff:KATAvsSAN':<15} "
      f"{'diff:KATAvsKORE':<16} {'diff:WHITEvsSAN':<16} sep-ratio(minDiff/maxSame)")
for name, fn in VARIANTS:
    same_kata = max(mad(fn(crop_gray(frames[0], KATA)), fn(crop_gray(frames[i], KATA)))
                    for i in range(1, 15))
    same_white = max(mad(fn(crop_gray(frames[0], WHITE)), fn(crop_gray(frames[i], WHITE)))
                     for i in (7, 14))
    d_ks = mad(fn(crop_gray(frames[0], KATA)), fn(crop_gray(frames[0], SAN)))
    d_kk = mad(fn(crop_gray(frames[0], KATA)), fn(crop_gray(frames[0], KORE)))
    d_ws = mad(fn(crop_gray(frames[0], WHITE)), fn(crop_gray(frames[0], SAN)))
    max_same = max(same_kata, same_white)
    min_diff = min(d_ks, d_kk, d_ws)
    print(f"{name:<10} {same_kata:<14.1f} {same_white:<15.1f} {d_ks:<15.1f} "
          f"{d_kk:<16.1f} {d_ws:<16.1f} {min_diff/max_same:.2f}")
