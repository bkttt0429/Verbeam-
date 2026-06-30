"""Reader routing helpers for the Columnizer bench.

Contract:
  - CV masks choose geometry only.
  - OCR readers receive raw BGR crops sliced from the original ROI.
  - Japanese vertical text is the only implemented reader route here.
"""
import time

import cv2
from PIL import Image


VERTICAL_JA_READER = "ja_mangaocr"


def jp_ratio(text):
    if not text.strip():
        return 0.0
    jp = sum(("\u3040" <= c <= "\u30ff") or ("\u4e00" <= c <= "\u9fff") for c in text)
    return jp / max(len(text), 1)


class JapaneseMangaOcrReader:
    def __init__(self, force_cpu=True):
        from manga_ocr import MangaOcr

        self._ocr = MangaOcr(force_cpu=force_cpu)

    def read_bgr(self, crop_bgr):
        rgb = cv2.cvtColor(crop_bgr, cv2.COLOR_BGR2RGB)
        return self._ocr(Image.fromarray(rgb))


def _raw_crop(roi_bgr, bbox):
    x0, y0, x1, y1 = bbox
    return roi_bgr[y0:y1, x0:x1]


def route_japanese_roi(roi_bgr, geometry, read_bgr=None):
    """Route one ROI using the current Japanese-only reader contract.

    `geometry` is the dict returned by columnizer.columnize().
    `read_bgr` should be JapaneseMangaOcrReader.read_bgr in full OCR mode.
    If read_bgr is None, this returns the planned route without OCR.
    """
    layout = geometry["layout"]
    status = geometry["status"]

    base = {
        "script": "ja",
        "layout": layout,
        "geometry_status": status,
        "reader": None,
        "reader_status": "skipped",
        "route_reason": None,
        "ocr_calls": 0,
        "reads": [],
        "joined": "",
    }

    if status == "reject" or layout == "no_text":
        base["route_reason"] = geometry.get("reject_reason") or "no_text"
        return base

    if layout == "horizontal_ltr":
        base["route_reason"] = "horizontal_reader_not_implemented_in_ja_bench"
        return base

    if layout != "vertical_rl":
        base["route_reason"] = "hold_unknown_layout"
        return base

    base["reader"] = VERTICAL_JA_READER
    base["reader_status"] = "planned" if read_bgr is None else "ok"
    if read_bgr is None:
        base["ocr_calls"] = len(geometry["columns"])
        return base

    reads = []
    for col in geometry["columns"]:
        crop = _raw_crop(roi_bgr, col["bbox"])
        ts = time.perf_counter()
        text = read_bgr(crop)
        ms = round((time.perf_counter() - ts) * 1000)
        reads.append({
            "order": col["order"],
            "bbox": col["bbox"],
            "reader": VERTICAL_JA_READER,
            "image": "raw_column_crop",
            "text": text,
            "ms": ms,
            "jp_ratio": round(jp_ratio(text), 2),
        })

    base["ocr_calls"] = len(reads)
    base["reads"] = reads
    base["joined"] = "\u3000".join(r["text"] for r in reads)
    return base
