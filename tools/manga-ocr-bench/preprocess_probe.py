"""Offline OCR-input preprocessing probe.

This does not change the router/columnizer. It takes the crops that the current
cheap-CV columnizer already selected, then compares manga-ocr on raw vs a few
cheap normalized inputs.
"""
import argparse
import csv
import re
import sys
import time
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

from cases import CASES
from columnizer import columnize

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
OUT = Path(r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\preprocess_probe")

DEFAULT_CASES = [
    "purple-vert(42s)",
    "blue-vert(153s)",
    "pink-vert(201s)",
    "red-1col(42s既視感)",
    "light-on-color(48s視感)",
    "whitebox-2col(37s)",
]


def safe_tag(text):
    return re.sub(r"[^0-9A-Za-z._-]+", "_", text).strip("_")


def jp_ratio(s):
    if not s.strip():
        return 0.0
    jp = sum(("\u3040" <= c <= "\u30ff") or ("\u4e00" <= c <= "\u9fff") for c in s)
    return jp / max(len(s), 1)


def read_frame(cap, fps, t):
    cap.set(cv2.CAP_PROP_POS_FRAMES, int(t * fps))
    ok, frame = cap.read()
    if not ok:
        raise RuntimeError(f"failed to read frame at t={t}")
    return frame


def to_pil(bgr):
    return Image.fromarray(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))


def crop_mask(mask, bbox):
    if mask is None:
        return None
    x0, y0, x1, y1 = bbox
    return mask[y0:y1, x0:x1]


def variants(crop, mask):
    gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
    yield "raw", crop
    yield "gray3", cv2.cvtColor(gray, cv2.COLOR_GRAY2BGR)

    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8)).apply(gray)
    yield "clahe", cv2.cvtColor(clahe, cv2.COLOR_GRAY2BGR)

    if mask is None or not np.any(mask):
        return
    ink = mask > 0
    ink3 = ink[:, :, None]

    bw = np.full_like(crop, 255)
    bw[ink] = (0, 0, 0)
    yield "mask_bw", bw

    dilated = cv2.dilate(mask, cv2.getStructuringElement(cv2.MORPH_RECT, (2, 2)), iterations=1) > 0
    bw_dilated = np.full_like(crop, 255)
    bw_dilated[dilated] = (0, 0, 0)
    yield "mask_bw_dilate", bw_dilated

    orig_on_white = np.full_like(crop, 255)
    orig_on_white = np.where(ink3, crop, orig_on_white)
    yield "orig_on_white", orig_on_white


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cases", nargs="*", default=DEFAULT_CASES, help="case names to probe")
    ap.add_argument("--save-images", action="store_true", help="write every variant crop to rois/preprocess_probe")
    args = ap.parse_args()

    selected = {case["name"]: case for case in CASES if case["name"] in set(args.cases)}
    missing = [name for name in args.cases if name not in selected]
    if missing:
        raise SystemExit(f"unknown cases: {missing}")

    OUT.mkdir(parents=True, exist_ok=True)

    from manga_ocr import MangaOcr

    ocr = MangaOcr(force_cpu=True)
    cap = cv2.VideoCapture(SRC)
    fps = cap.get(cv2.CAP_PROP_FPS) or 24.0

    rows = []
    try:
        for name in args.cases:
            case = selected[name]
            frame = read_frame(cap, fps, case["t"])
            x0, y0, x1, y1 = case["roi"]
            roi = frame[y0:y1, x0:x1]
            result = columnize(roi)
            print(
                f"\n## {name} source={result['mask_dbg'].get('source')} "
                f"layout={result['layout']} status={result['status']} cols={len(result['columns'])} "
                f"dbg={result['mask_dbg']}"
            )
            if result["status"] == "reject":
                continue

            columns = result["columns"] or [{"order": 0, "bbox": [0, 0, roi.shape[1], roi.shape[0]]}]
            for col in columns:
                bx0, by0, bx1, by1 = col["bbox"]
                crop = roi[by0:by1, bx0:bx1]
                mask = crop_mask(result.get("mask"), col["bbox"])
                for variant_name, image in variants(crop, mask):
                    ts = time.perf_counter()
                    text = ocr(to_pil(image))
                    ms = round((time.perf_counter() - ts) * 1000)
                    ratio = round(jp_ratio(text), 2)
                    print(f"   col{col['order']} {variant_name:<14} {ms:>5}ms jp={ratio:<4} {text!r}")
                    rows.append({
                        "case": name,
                        "layout": result["layout"],
                        "source": result["mask_dbg"].get("source"),
                        "col": col["order"],
                        "variant": variant_name,
                        "ms": ms,
                        "jp_ratio": ratio,
                        "text": text,
                    })
                    if args.save_images:
                        tag = f"{safe_tag(name)}_c{col['order']}_{variant_name}.png"
                        cv2.imwrite(str(OUT / tag), image)
    finally:
        cap.release()

    out_csv = OUT / "results.tsv"
    with out_csv.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(
            f,
            fieldnames=["case", "layout", "source", "col", "variant", "ms", "jp_ratio", "text"],
            delimiter="\t",
        )
        writer.writeheader()
        writer.writerows(rows)
    print(f"\nwrote {out_csv}")


if __name__ == "__main__":
    main()
