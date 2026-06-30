"""Preview experimental full-frame block detection on selected timestamps."""
import argparse
import csv
import json
import sys
import time
from pathlib import Path

import cv2

from block_detector import detect_text_blocks
from columnizer import columnize
from reader_routes import JapaneseMangaOcrReader, route_japanese_roi
from realtime_preview import draw_grid, make_contact_sheet, put_label

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

TOOL_DIR = Path(__file__).resolve().parent
SRC = TOOL_DIR / "inputs" / "source_1080p.mp4"
OUT = TOOL_DIR / "outputs" / "detector_preview"


def draw_proposal(frame, index, candidate):
    x0, y0, x1, y1 = candidate.bbox
    color = (0, 210, 255)
    cv2.rectangle(frame, (x0, y0), (x1, y1), color, 2)
    label = (
        f"proposal#{index} vote={candidate.vote} rank={candidate.rank:.2f} "
        f"{candidate.layout} source={candidate.source}"
    )
    put_label(frame, label, x0 + 4, y0 - 8, color=color, scale=0.45)


def draw_detection(frame, index, candidate, result, route):
    x0, y0, x1, y1 = candidate.bbox
    color = (0, 255, 255) if result["layout"] == "vertical_rl" else (255, 180, 0)
    cv2.rectangle(frame, (x0, y0), (x1, y1), color, 3)
    label = (
        f"auto#{index} vote={candidate.vote} rank={candidate.rank:.2f} "
        f"{result['layout']} {result['status']} cols={len(result['columns'])} "
        f"reader={route['reader'] or '-'} calls={route['ocr_calls']}"
    )
    put_label(frame, label, x0 + 4, y0 - 8, color=color, scale=0.45)

    for col in result["columns"]:
        bx0, by0, bx1, by1 = col["bbox"]
        ax0, ay0, ax1, ay1 = x0 + bx0, y0 + by0, x0 + bx1, y0 + by1
        cv2.rectangle(frame, (ax0, ay0), (ax1, ay1), (255, 0, 255), 2)
        put_label(frame, f"c{col['order']}", ax0 + 2, ay0 + 16, color=(255, 0, 255), scale=0.4)

    if route["reads"]:
        text = " | ".join(f"c{r['order']}:{r['text']}" for r in route["reads"])
        put_label(frame, text, x0 + 4, y1 + 24, color=(220, 255, 220), scale=0.48)


def event_rows(t, index, candidate, result, route):
    rows = []
    base = {
        "time_s": f"{t:.3f}",
        "det_index": index,
        "det_bbox": list(candidate.bbox),
        "det_vote": candidate.vote,
        "det_rank": round(candidate.rank, 4),
        "det_layout_hint": candidate.layout,
        "det_cols_hint": candidate.columns,
        "det_occ": candidate.occ,
        "det_tl": candidate.tl,
        "det_n": candidate.n,
        "det_line_frac": candidate.line_frac,
        "det_edge_frac": candidate.edge_frac,
        "det_max_dom": candidate.max_dom,
        "layout": result["layout"],
        "status": result["status"],
        "mask_source": result["mask_dbg"].get("source"),
        "mask_occ": result["mask_dbg"].get("occ"),
        "mask_tl": result["mask_dbg"].get("tl"),
        "mask_n": result["mask_dbg"].get("n"),
        "reader": route["reader"],
        "reader_status": route["reader_status"],
        "route_reason": route["route_reason"],
        "ocr_calls": route["ocr_calls"],
        "joined": route["joined"],
    }
    reads = {read["order"]: read for read in route["reads"]}
    if result["columns"]:
        x0, y0, _x1, _y1 = candidate.bbox
        for col in result["columns"]:
            bbox = col["bbox"]
            abs_bbox = [x0 + bbox[0], y0 + bbox[1], x0 + bbox[2], y0 + bbox[3]]
            read = reads.get(col["order"], {})
            rows.append({
                **base,
                "column_order": col["order"],
                "column_bbox_abs": abs_bbox,
                "ocr_text": read.get("text", ""),
                "ocr_ms": read.get("ms", ""),
                "ocr_jp_ratio": read.get("jp_ratio", ""),
            })
    else:
        rows.append({
            **base,
            "column_order": "",
            "column_bbox_abs": "",
            "ocr_text": "",
            "ocr_ms": "",
            "ocr_jp_ratio": "",
        })
    return rows


def proposal_event_row(t, index, candidate):
    return {
        "time_s": f"{t:.3f}",
        "det_index": index,
        "det_bbox": list(candidate.bbox),
        "det_vote": candidate.vote,
        "det_rank": round(candidate.rank, 4),
        "det_layout_hint": candidate.layout,
        "det_cols_hint": candidate.columns,
        "det_occ": candidate.occ,
        "det_tl": candidate.tl,
        "det_n": candidate.n,
        "det_line_frac": candidate.line_frac,
        "det_edge_frac": candidate.edge_frac,
        "det_max_dom": candidate.max_dom,
        "layout": "",
        "status": "proposal",
        "mask_source": candidate.source,
        "mask_occ": "",
        "mask_tl": "",
        "mask_n": "",
        "reader": "",
        "reader_status": "skipped",
        "route_reason": "proposal_stage",
        "ocr_calls": 0,
        "joined": "",
        "column_order": "",
        "column_bbox_abs": "",
        "ocr_text": "",
        "ocr_ms": "",
        "ocr_jp_ratio": "",
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--src", default=str(SRC))
    parser.add_argument("--out", default=str(OUT))
    parser.add_argument("--times", nargs="*", type=float, default=[42.0, 48.0])
    parser.add_argument("--max-blocks", type=int, default=14)
    parser.add_argument("--scorer", choices=["cc", "window", "fast", "exact"], default="cc")
    parser.add_argument("--stage", choices=["proposal", "confirm", "ocr"], default="confirm")
    parser.add_argument("--min-vote", type=int, default=5)
    parser.add_argument("--ocr", action="store_true")
    args = parser.parse_args()
    if args.ocr:
        args.stage = "ocr"

    out_dir = Path(args.out)
    frames_dir = out_dir / "frames"
    frames_dir.mkdir(parents=True, exist_ok=True)

    reader = JapaneseMangaOcrReader(force_cpu=True) if args.stage == "ocr" else None
    cap = cv2.VideoCapture(args.src)
    fps = cap.get(cv2.CAP_PROP_FPS) or 24.0

    annotated = []
    labels = []
    rows = []
    metrics = []
    try:
        for t in args.times:
            cap.set(cv2.CAP_PROP_POS_FRAMES, int(t * fps))
            ok, frame = cap.read()
            if not ok:
                continue
            raw = frame
            canvas = raw.copy()
            draw_grid(canvas)
            ts_detect = time.perf_counter()
            candidates, stats = detect_text_blocks(
                raw,
                max_blocks=args.max_blocks,
                scorer=args.scorer,
                min_vote=args.min_vote,
                confirm_raw=args.stage in ("confirm", "ocr"),
                return_stats=True,
            )
            detector_ms = (time.perf_counter() - ts_detect) * 1000.0
            put_label(
                canvas,
                f"t={t:.1f}s experimental auto block detector scorer={args.scorer} "
                f"stage={args.stage} min_vote={args.min_vote} candidates={len(candidates)}",
                10,
                55,
                scale=0.75,
                thickness=2,
            )
            ocr_calls = 0
            ocr_ms = 0
            for index, candidate in enumerate(candidates):
                x0, y0, x1, y1 = candidate.bbox
                if args.stage == "proposal":
                    draw_proposal(canvas, index, candidate)
                    rows.append(proposal_event_row(t, index, candidate))
                    continue

                roi = raw[y0:y1, x0:x1].copy()
                result = candidate.columnizer_result or columnize(roi)
                route = route_japanese_roi(
                    roi,
                    result,
                    read_bgr=reader.read_bgr if reader is not None else None,
                )
                ocr_calls += route["ocr_calls"] if args.stage == "ocr" else 0
                ocr_ms += sum(int(read.get("ms", 0)) for read in route["reads"])
                draw_detection(canvas, index, candidate, result, route)
                rows.extend(event_rows(t, index, candidate, result, route))
            metrics.append({
                "time_s": f"{t:.3f}",
                "stage": args.stage,
                "scorer": args.scorer,
                "detector_ms": round(detector_ms, 2),
                "mask_ms": round(stats.get("mask_ms", 0.0), 2),
                "cc_ms": round(stats.get("cc_ms", 0.0), 2),
                "group_ms": round(stats.get("group_ms", 0.0), 2),
                "window_ms": round(stats.get("window_ms", 0.0), 2),
                "confirm_ms": round(stats.get("confirm_ms", 0.0), 2),
                "ocr_ms": ocr_ms,
                "proposal_count": stats.get("proposal_count", 0),
                "confirmed_count": stats.get("confirmed_count", len(candidates)),
                "candidate_count": len(candidates),
                "ocr_calls": ocr_calls,
            })
            out_frame = frames_dir / f"detector_{int(t)}.png"
            cv2.imwrite(str(out_frame), canvas)
            annotated.append(canvas)
            labels.append(f"{t:.1f}s")
    finally:
        cap.release()

    fieldnames = [
        "time_s", "det_index", "det_bbox", "det_vote", "det_rank", "det_layout_hint",
        "det_cols_hint", "det_occ", "det_tl", "det_n", "det_line_frac",
        "det_edge_frac", "det_max_dom", "layout", "status",
        "mask_source", "mask_occ", "mask_tl", "mask_n", "reader", "reader_status",
        "route_reason", "ocr_calls", "joined", "column_order", "column_bbox_abs",
        "ocr_text", "ocr_ms", "ocr_jp_ratio",
    ]
    tsv_path = out_dir / "events.tsv"
    with tsv_path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames, delimiter="\t")
        writer.writeheader()
        writer.writerows(rows)
    json_path = out_dir / "events.json"
    with json_path.open("w", encoding="utf-8") as f:
        json.dump(rows, f, ensure_ascii=False, indent=2)
    metrics_fields = [
        "time_s", "stage", "scorer", "detector_ms", "mask_ms", "cc_ms",
        "group_ms", "window_ms", "confirm_ms", "ocr_ms", "proposal_count",
        "confirmed_count", "candidate_count", "ocr_calls",
    ]
    metrics_tsv = out_dir / "metrics.tsv"
    with metrics_tsv.open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=metrics_fields, delimiter="\t")
        writer.writeheader()
        writer.writerows(metrics)
    metrics_json = out_dir / "metrics.json"
    with metrics_json.open("w", encoding="utf-8") as f:
        json.dump(metrics, f, ensure_ascii=False, indent=2)
    sheet_path = out_dir / "contact_sheet.png"
    make_contact_sheet(annotated, labels, sheet_path, cols=1)

    print(f"frames={len(annotated)} rows={len(rows)}")
    print(f"contact_sheet={sheet_path}")
    print(f"events_tsv={tsv_path}")
    print(f"events_json={json_path}")
    print(f"metrics_tsv={metrics_tsv}")
    print(f"metrics_json={metrics_json}")


if __name__ == "__main__":
    main()
