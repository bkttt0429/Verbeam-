"""Side-by-side Mode A (accuracy) vs Mode B (realtime) render.

Runs the 15-frame sequence in both modes, records which block POSITIONS ever fired OCR, then paints the
clean 48.0s reference frame twice: green = caption actually read (OCR'd) in that mode, red = detected but
gated (never OCR'd). These flips green->red between A and B.

    python mode_compare.py --start 48.0 --frames 15
"""
import argparse
import sys

import cv2

from block_detector import detect_text_blocks, _iou
from temporal_cache import TemporalBlockCache
from realtime_preview import put_label

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
OUT = r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\mode_compare.png"


def run_mode(cap, fps, start, frames, seed_stable):
    """Return (total_ocr, set of bboxes that ever fired OCR) for one mode."""
    cache = TemporalBlockCache(stable_frames=2, stable_by_kind={"column_seed": seed_stable})
    total, ocrd = 0, []
    for i in range(frames):
        cap.set(cv2.CAP_PROP_POS_FRAMES, start + i)
        ok, frame = cap.read()
        if not ok:
            break
        blocks = detect_text_blocks(frame, scorer="cc", group="graph", emit_seeds=True, confirm_raw=True)
        for b, r in zip(blocks, cache.update(blocks)):
            if r["ocr_called"]:
                total += 1
                ocrd.append(b.bbox)
    return total, ocrd


def was_read(bbox, ocrd):
    # a flickery caption (これ) is OCR'd at a slightly different box on a later frame than the reference
    # frame-0 box, so IoU alone misses it — also accept a read whose CENTER lands in the same region.
    cx, cy = (bbox[0] + bbox[2]) / 2, (bbox[1] + bbox[3]) / 2
    for o in ocrd:
        ox, oy = (o[0] + o[2]) / 2, (o[1] + o[3]) / 2
        if _iou(bbox, o) > 0.3 or (abs(cx - ox) < 70 and abs(cy - oy) < 90):
            return True
    return False


def panel(frame, ref_blocks, ocrd, title):
    img = frame.copy()
    for b in ref_blocks:
        x0, y0, x1, y1 = b.bbox
        read = was_read(b.bbox, ocrd)
        color = (0, 210, 0) if read else (0, 0, 255)
        cv2.rectangle(img, (x0, y0), (x1, y1), color, 3)
        put_label(img, f"{b.kind} {'OCR' if read else 'GATED'}", x0 + 3, y0 - 6, color=color, scale=0.5)
    put_label(img, title, 12, 40, color=(255, 255, 255), scale=0.9, thickness=2)
    return img


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--start", type=float, default=48.0)
    ap.add_argument("--frames", type=int, default=15)
    ap.add_argument("--out", default=OUT)
    args = ap.parse_args()

    cap = cv2.VideoCapture(SRC)
    fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
    start = int(args.start * fps)

    a_total, a_ocrd = run_mode(cap, fps, start, args.frames, seed_stable=2)
    b_total, b_ocrd = run_mode(cap, fps, start, args.frames, seed_stable=4)

    cap.set(cv2.CAP_PROP_POS_FRAMES, start)
    _ok, ref = cap.read()
    ref_blocks = detect_text_blocks(ref, scorer="cc", group="graph", emit_seeds=True, confirm_raw=True)
    cap.release()

    left = panel(ref, ref_blocks, a_ocrd, f"Mode A (accuracy) seed_stable=2  OCR={a_total}/{args.frames}f")
    right = panel(ref, ref_blocks, b_ocrd, f"Mode B (realtime) seed_stable=4  OCR={b_total}/{args.frames}f")
    combo = cv2.vconcat([left, right])
    cv2.imwrite(args.out, combo)
    print(f"A OCR={a_total}  B OCR={b_total}")
    print(f"out={args.out}")


if __name__ == "__main__":
    main()
