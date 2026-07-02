"""Function-level profile of detect_text_blocks over the 15-frame window (disposable).
cProfile cumulative view + per-stage counters so the optimization targets facts, not guesses."""
import cProfile
import pstats
import sys

import cv2

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

detect_text_blocks(frames[0], scorer="cc", group="graph", emit_seeds=True, confirm_raw=True)  # warm

# aggregate stage stats
agg = {}
for f in frames:
    _, st = detect_text_blocks(f, scorer="cc", group="graph", emit_seeds=True, confirm_raw=True,
                               return_stats=True)
    for k, v in st.items():
        if isinstance(v, (int, float)):
            agg[k] = agg.get(k, 0) + v
print("stage totals over 15 frames:")
for k in sorted(agg):
    print(f"  {k:<28} {agg[k]:.0f}")

pr = cProfile.Profile()
pr.enable()
for f in frames:
    detect_text_blocks(f, scorer="cc", group="graph", emit_seeds=True, confirm_raw=True)
pr.disable()
ps = pstats.Stats(pr)
ps.sort_stats("cumulative")
print("\ntop functions (cumulative):")
ps.print_stats(22)
