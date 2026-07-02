"""Verify the temporal cache on a real frame sequence: run the detector on consecutive frames, apply
deferral regions, feed confirmed blocks to TemporalBlockCache, and report OCR calls cache-vs-naive.

Naive = OCR every confirmed block every frame. Cache = OCR each tracklet once (when it stabilises),
then HOLD. The win: most frames do 0 OCR once captions are stable.

Two product operating points (SEED-UTILITY-MEASURED.md / SEED-ADMISSION-IMPL-SPEC.md) share the SAME
admission knobs (center_r=20, column_seed count=3) and differ only in whether a deferral ROI is applied —
both are full-recall, neither sacrifices これ the way the old seed_stable=4 "realtime" mode did:

    Full Recall:  python temporal_stream.py --start 48.0 --frames 15
    Realtime:     python temporal_stream.py --start 48.0 --frames 15 --defer-region 1200,0,1920,1080
"""
import argparse
import sys
from collections import Counter

import cv2

from block_detector import detect_text_blocks
from temporal_cache import TemporalBlockCache
from deferral import apply_deferral_regions

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"


def _parse_rect(s):
    x0, y0, x1, y1 = (float(v) for v in s.split(","))
    return (x0, y0, x1, y1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=SRC)
    ap.add_argument("--start", type=float, default=48.0)
    ap.add_argument("--frames", type=int, default=15)
    ap.add_argument("--step", type=int, default=1, help="frame stride")
    ap.add_argument("--stable", type=int, default=2, help="block_merged/broad_split admission age")
    ap.add_argument("--expire", type=int, default=3)
    ap.add_argument("--seed-stable", type=int, default=3,
                    help="column_seed admission: hit-count at a center-linked location (NOT necessarily "
                         "consecutive frames — see --center-r). Default 3 = the measured ceiling for a "
                         "sparse recall-only caption (これ). The old default of 4 is retired: it is "
                         "strictly dominated (see SEED-UTILITY-MEASURED.md) because it gates on "
                         "consecutive age with no center-link, which no sparse caption can satisfy.")
    ap.add_argument("--center-r", type=float, default=20.0,
                    help="column_seed center-link radius in px (0 disables -> old IoU-only behaviour). "
                         "Must stay below the smallest real inter-column gap or adjacent columns fuse.")
    ap.add_argument("--defer-region", action="append", default=[], metavar="X0,Y0,X1,Y1",
                    help="repeatable; a block >50%% inside any region is held back from OCR (per-profile "
                         "'ignore this screen area', e.g. a character-art panel cheap-CV can't separate "
                         "from text). Passing one or more turns Full Recall into the Realtime point.")
    args = ap.parse_args()
    defer_rects = [_parse_rect(s) for s in args.defer_region]

    cap = cv2.VideoCapture(args.src)
    fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
    start = int(args.start * fps)
    cache = TemporalBlockCache(stable_frames=args.stable, expire_frames=args.expire,
                               center_r=args.center_r, stable_by_kind={"column_seed": args.seed_stable})

    def where(b):  # tag the two rescued captions so we can confirm gating never kills them
        x0, y0 = b[0], b[1]
        if 740 <= x0 <= 800 and y0 < 300: return "KORE"
        if 830 <= x0 <= 860 and y0 < 600: return "KATATTOITE"
        return None
    captions_ocrd = set()

    naive_total = cache_total = deferred_total = 0
    spawns_by_kind = Counter()      # 4A: which proposal kinds spawn new tracks (-> re-OCR)
    ocr_by_kind = Counter()
    blocks_by_kind = Counter()
    nearmiss = Counter()            # best_iou bucket of SPAWNS: rescuable by a better matcher?
    print(f"frame   t     blocks  deferred  ocr(cache)  states")
    for i in range(args.frames):
        cap.set(cv2.CAP_PROP_POS_FRAMES, start + i * args.step)
        ok, frame = cap.read()
        if not ok:
            break
        blocks = detect_text_blocks(frame, scorer="cc", group="graph",
                                    emit_seeds=True, confirm_raw=True)
        blocks, deferred = apply_deferral_regions(blocks, defer_rects)
        deferred_total += len(deferred)
        res = cache.update(blocks, frame=frame)  # ocr_fn=None: we only COUNT would-be OCR calls
        ocr_now = sum(r["ocr_called"] for r in res)
        naive_total += len(blocks)
        cache_total += ocr_now
        for b, r in zip(blocks, res):
            blocks_by_kind[b.kind] += 1
            if r["ocr_called"]:
                ocr_by_kind[b.kind] += 1
                w = where(b.bbox)
                if w:
                    captions_ocrd.add(w)
            if r["spawned"] and i > 0:  # frame 0 spawns are unavoidable cold start
                spawns_by_kind[b.kind] += 1
                iou = r["best_iou"]
                bucket = ("iou<0.1" if iou < 0.1 else "0.1-0.35" if iou < 0.35
                          else "0.35-0.5(rescuable)" if iou < 0.5 else ">=0.5")
                nearmiss[f"{b.kind}:{bucket}"] += 1
        states = Counter(r["state"] for r in res)
        t = (start + i * args.step) / fps
        print(f"{i:<5d} {t:5.2f}  {len(blocks):<6d}  {len(deferred):<8d}  {ocr_now:<10d}  {dict(states)}")
    cap.release()

    saved = (1 - cache_total / naive_total) * 100 if naive_total else 0.0
    point = "Realtime" if defer_rects else "Full Recall"
    print(f"\noperating point: {point}  (seed_stable={args.seed_stable} center_r={args.center_r} "
          f"defer_regions={defer_rects})")
    print(f"OCR calls: naive={naive_total}  cache={cache_total}  saved={saved:.0f}%  "
          f"deferred={deferred_total}")
    print(f"captions OCR'd (must be both): {sorted(captions_ocrd)}")
    print(f"blocks_by_kind:  {dict(blocks_by_kind)}")
    print(f"ocr_by_kind:     {dict(ocr_by_kind)}")
    print(f"spawns_by_kind (frame>0, = jitter re-OCR source): {dict(spawns_by_kind)}")
    print(f"spawn best_iou buckets: {dict(sorted(nearmiss.items()))}")


if __name__ == "__main__":
    main()
