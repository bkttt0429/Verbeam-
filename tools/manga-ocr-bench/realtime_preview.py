"""Build annotated realtime-style previews from the current bench cases.

This is not a detector. It visualizes the known ROIs in cases.py on top of the
source video, then runs the current cheap-CV geometry and optional Japanese
reader route.
"""
import argparse
import csv
import json
import math
import re
import sys
from collections import defaultdict
from pathlib import Path

import cv2
import numpy as np

from cases import CASES
from columnizer import columnize
from reader_routes import JapaneseMangaOcrReader, route_japanese_roi

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

DEFAULT_SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
DEFAULT_OUT = Path(r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\realtime_preview")

COLORS = {
    "vertical_rl": (0, 220, 0),
    "horizontal_ltr": (255, 160, 0),
    "unknown": (0, 180, 255),
    "no_text": (0, 0, 255),
    "reject": (0, 0, 255),
}


def safe_tag(text):
    return re.sub(r"[^0-9A-Za-z._-]+", "_", text).strip("_")


def put_label(img, text, x, y, color=(255, 255, 255), scale=0.55, thickness=1):
    font = cv2.FONT_HERSHEY_SIMPLEX
    (tw, th), baseline = cv2.getTextSize(text, font, scale, thickness)
    x = max(0, min(x, img.shape[1] - tw - 2))
    y = max(th + 2, min(y, img.shape[0] - baseline - 2))
    cv2.rectangle(img, (x - 2, y - th - 4), (x + tw + 2, y + baseline + 2), (0, 0, 0), -1)
    cv2.putText(img, text, (x, y), font, scale, color, thickness, cv2.LINE_AA)


def draw_grid(img, x_step=160, y_step=120):
    overlay = img.copy()
    h, w = img.shape[:2]
    for x in range(0, w, x_step):
        cv2.line(overlay, (x, 0), (x, h), (0, 255, 255), 1)
        cv2.putText(overlay, str(x), (x + 3, 23), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 255, 255), 2)
    for y in range(0, h, y_step):
        cv2.line(overlay, (0, y), (w, y), (0, 255, 255), 1)
        cv2.putText(overlay, str(y), (3, y + 18), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 255, 255), 2)
    cv2.addWeighted(overlay, 0.45, img, 0.55, 0, img)


def draw_case(frame, case, result, route):
    x0, y0, x1, y1 = case["roi"]
    color = COLORS.get(result["layout"], (255, 255, 255))
    if result["status"] == "reject":
        color = COLORS["reject"]
    cv2.rectangle(frame, (x0, y0), (x1, y1), color, 3)

    label = (
        f"{case['t']:.1f}s {case['name']} | {result['layout']} {result['status']} "
        f"src={result['mask_dbg'].get('source')} cols={len(result['columns'])} "
        f"reader={route['reader'] or '-'} calls={route['ocr_calls']}"
    )
    put_label(frame, label, x0 + 4, y0 - 8, color=color, scale=0.48)

    for col in result["columns"]:
        bx0, by0, bx1, by1 = col["bbox"]
        ax0, ay0, ax1, ay1 = x0 + bx0, y0 + by0, x0 + bx1, y0 + by1
        cv2.rectangle(frame, (ax0, ay0), (ax1, ay1), (255, 0, 255), 2)
        put_label(frame, f"c{col['order']} [{ax0},{ay0},{ax1},{ay1}]", ax0 + 3, ay0 + 18, color=(255, 0, 255), scale=0.42)

    if route["reads"]:
        text = " | ".join(f"c{r['order']}:{r['text']}" for r in route["reads"])
        put_label(frame, text, x0 + 4, y1 + 24, color=(220, 255, 220), scale=0.5)


def make_contact_sheet(images, labels, out_path, cell_w=480, cols=3):
    cells = []
    for img, label in zip(images, labels):
        h, w = img.shape[:2]
        cell_h = int(h * cell_w / w)
        small = cv2.resize(img, (cell_w, cell_h), interpolation=cv2.INTER_AREA)
        put_label(small, label, 8, 26, color=(255, 255, 255), scale=0.65, thickness=2)
        cells.append(small)
    if not cells:
        return
    rows = math.ceil(len(cells) / cols)
    cell_h = max(c.shape[0] for c in cells)
    sheet = np.zeros((rows * cell_h, cols * cell_w, 3), np.uint8)
    for i, cell in enumerate(cells):
        r, c = divmod(i, cols)
        y = r * cell_h
        x = c * cell_w
        sheet[y:y + cell.shape[0], x:x + cell.shape[1]] = cell
    cv2.imwrite(str(out_path), sheet)


def row_for_case(case, result, route):
    rows = []
    base = {
        "time_s": f"{case['t']:.3f}",
        "case": case["name"],
        "category": case.get("category", ""),
        "deferred": str(bool(case.get("deferred", False))).lower(),
        "roi_abs": list(case["roi"]),
        "layout": result["layout"],
        "status": result["status"],
        "reject_reason": result["reject_reason"],
        "mask_source": result["mask_dbg"].get("source"),
        "mask_occ": result["mask_dbg"].get("occ"),
        "mask_tl": result["mask_dbg"].get("tl"),
        "mask_n": result["mask_dbg"].get("n"),
        "split_confidence": result["split_confidence"],
        "reader": route["reader"],
        "reader_status": route["reader_status"],
        "route_reason": route["route_reason"],
        "ocr_calls": route["ocr_calls"],
        "joined": route["joined"],
    }
    read_by_order = {read["order"]: read for read in route["reads"]}
    if result["columns"]:
        for col in result["columns"]:
            bbox = col["bbox"]
            x0, y0, _x1, _y1 = case["roi"]
            abs_bbox = [x0 + bbox[0], y0 + bbox[1], x0 + bbox[2], y0 + bbox[3]]
            read = read_by_order.get(col["order"], {})
            rows.append({
                **base,
                "column_order": col["order"],
                "column_bbox_roi": bbox,
                "column_bbox_abs": abs_bbox,
                "ocr_text": read.get("text", ""),
                "ocr_ms": read.get("ms", ""),
                "ocr_jp_ratio": read.get("jp_ratio", ""),
            })
    else:
        rows.append({
            **base,
            "column_order": "",
            "column_bbox_roi": "",
            "column_bbox_abs": "",
            "ocr_text": "",
            "ocr_ms": "",
            "ocr_jp_ratio": "",
        })
    return rows


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--src", default=DEFAULT_SRC)
    parser.add_argument("--out", default=str(DEFAULT_OUT))
    parser.add_argument("--ocr", action="store_true", help="run Japanese manga-ocr route for vertical_rl cases")
    parser.add_argument("--include-deferred", action="store_true", help="include deferred cases in overlays")
    parser.add_argument("--video", action="store_true", help="write a short annotated mp4 slideshow")
    args = parser.parse_args()

    out_dir = Path(args.out)
    frames_dir = out_dir / "frames"
    frames_dir.mkdir(parents=True, exist_ok=True)

    selected = [case for case in CASES if args.include_deferred or not case.get("deferred", False)]
    grouped = defaultdict(list)
    for case in selected:
        grouped[case["t"]].append(case)

    reader = JapaneseMangaOcrReader(force_cpu=True) if args.ocr else None

    cap = cv2.VideoCapture(args.src)
    fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
    events = []
    annotated = []
    labels = []

    try:
        for t in sorted(grouped):
            cap.set(cv2.CAP_PROP_POS_FRAMES, int(t * fps))
            ok, frame = cap.read()
            if not ok:
                continue
            raw_frame = frame
            annotated_frame = raw_frame.copy()
            draw_grid(annotated_frame)
            put_label(annotated_frame, f"t={t:.1f}s  known ROI preview  source={Path(args.src).name}", 10, 55, scale=0.75, thickness=2)

            for case in grouped[t]:
                x0, y0, x1, y1 = case["roi"]
                roi = raw_frame[y0:y1, x0:x1].copy()
                result = columnize(roi)
                route = route_japanese_roi(
                    roi,
                    result,
                    read_bgr=reader.read_bgr if reader is not None else None,
                )
                draw_case(annotated_frame, case, result, route)
                events.extend(row_for_case(case, result, route))

            out_frame = frames_dir / f"preview_{safe_tag(f'{t:.1f}s')}.png"
            cv2.imwrite(str(out_frame), annotated_frame)
            annotated.append(annotated_frame.copy())
            labels.append(f"{t:.1f}s")
    finally:
        cap.release()

    tsv_path = out_dir / "events.tsv"
    json_path = out_dir / "events.json"
    fieldnames = [
        "time_s", "case", "category", "deferred", "roi_abs", "layout", "status",
        "reject_reason", "mask_source", "mask_occ", "mask_tl", "mask_n",
        "split_confidence", "reader", "reader_status", "route_reason", "ocr_calls",
        "joined", "column_order", "column_bbox_roi", "column_bbox_abs", "ocr_text",
        "ocr_ms", "ocr_jp_ratio",
    ]
    with tsv_path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames, delimiter="\t")
        writer.writeheader()
        writer.writerows(events)
    with json_path.open("w", encoding="utf-8") as f:
        json.dump(events, f, ensure_ascii=False, indent=2)

    sheet_path = out_dir / "contact_sheet.png"
    make_contact_sheet(annotated, labels, sheet_path)

    video_path = None
    if args.video and annotated:
        video_path = out_dir / "preview.mp4"
        h, w = annotated[0].shape[:2]
        writer = cv2.VideoWriter(str(video_path), cv2.VideoWriter_fourcc(*"mp4v"), 1.0, (w, h))
        for img in annotated:
            writer.write(img)
        writer.release()

    print(f"frames={len(annotated)} events={len(events)}")
    print(f"contact_sheet={sheet_path}")
    print(f"events_tsv={tsv_path}")
    print(f"events_json={json_path}")
    if video_path:
        print(f"video={video_path}")


if __name__ == "__main__":
    main()
