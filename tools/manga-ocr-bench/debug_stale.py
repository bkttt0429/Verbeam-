"""Which tracks does the stale guard fire on, and are those genuine content changes or thumb-geometry
false positives? Replays the 48.0s window and logs every HOLD-frame MAD per track (disposable)."""
import sys
from collections import defaultdict

import cv2

from block_detector import detect_text_blocks
from temporal_cache import TemporalBlockCache

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

cache = TemporalBlockCache(stable_frames=2, expire_frames=3, center_r=20.0,
                           stable_by_kind={"column_seed": 3})
cache.stale_log = []
per_frame_ocr = []
for i, f in enumerate(frames):
    blocks = detect_text_blocks(f, scorer="cc", group="graph", emit_seeds=True, confirm_raw=True)
    res = cache.update(blocks, frame=f)
    fired = [(r["id"], next(b.kind for b, rr in zip(blocks, res) if rr is r)) for r in res if r["ocr_called"]]
    if fired:
        per_frame_ocr.append((i, fired))

by_track = defaultdict(list)
for tid, kind, mad, bbox in cache.stale_log:
    by_track[(tid, kind)].append((mad, bbox))

print("per-track HOLD-frame MADs (threshold 5.0):")
for (tid, kind), entries in sorted(by_track.items()):
    mads = [m for m, _ in entries]
    over = sum(m > 5.0 for m in mads)
    print(f"  track {tid:<3} {kind:<12} n={len(mads):<3} mads={mads}  over-thr={over}  "
          f"bbox~{entries[-1][1]}")
print("\nframes where OCR fired:", per_frame_ocr)
