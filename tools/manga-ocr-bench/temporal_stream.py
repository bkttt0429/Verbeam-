"""Verify the temporal cache (#3) on a real frame sequence: run the detector on consecutive frames,
feed confirmed blocks to TemporalBlockCache, and report OCR calls cache-vs-naive.

Naive = OCR every confirmed block every frame. Cache = OCR each tracklet once (when it stabilises),
then HOLD. The win: most frames do 0 OCR once captions are stable.

    python temporal_stream.py --start 48.0 --frames 15
"""
import argparse
import sys
from collections import Counter

import cv2

from block_detector import detect_text_blocks
from temporal_cache import TemporalBlockCache

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=SRC)
    ap.add_argument("--start", type=float, default=48.0)
    ap.add_argument("--frames", type=int, default=15)
    ap.add_argument("--step", type=int, default=1, help="frame stride")
    ap.add_argument("--stable", type=int, default=2)
    ap.add_argument("--expire", type=int, default=3)
    args = ap.parse_args()

    cap = cv2.VideoCapture(args.src)
    fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
    start = int(args.start * fps)
    cache = TemporalBlockCache(stable_frames=args.stable, expire_frames=args.expire)

    naive_total = cache_total = 0
    print(f"frame   t     blocks  ocr(cache)  states")
    for i in range(args.frames):
        cap.set(cv2.CAP_PROP_POS_FRAMES, start + i * args.step)
        ok, frame = cap.read()
        if not ok:
            break
        blocks = detect_text_blocks(frame, scorer="cc", group="graph",
                                    emit_seeds=True, confirm_raw=True)
        res = cache.update(blocks)  # ocr_fn=None: we only COUNT would-be OCR calls
        ocr_now = sum(r["ocr_called"] for r in res)
        naive_total += len(blocks)
        cache_total += ocr_now
        states = Counter(r["state"] for r in res)
        t = (start + i * args.step) / fps
        print(f"{i:<5d} {t:5.2f}  {len(blocks):<6d}  {ocr_now:<10d}  {dict(states)}")
    cap.release()

    saved = (1 - cache_total / naive_total) * 100 if naive_total else 0.0
    print(f"\nOCR calls: naive={naive_total}  cache={cache_total}  saved={saved:.0f}%")


if __name__ == "__main__":
    main()
