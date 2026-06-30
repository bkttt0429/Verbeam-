"""Run the Columnizer bench over the ROIs in cases.py.

Geometry scoring is always cheap CV only. Full mode additionally exercises the
implemented Japanese reader route:

  vertical_ja -> raw column crops -> manga-ocr

Other layouts are intentionally skipped by the reader route until their readers
exist. This keeps OCR out of routing decisions.
"""
import sys
from collections import defaultdict

import cv2

from cases import CASES
from columnizer import columnize
from reader_routes import JapaneseMangaOcrReader, route_japanese_roi

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

NO_OCR = "--no-ocr" in sys.argv
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"

if not NO_OCR:
    _ja_reader = JapaneseMangaOcrReader(force_cpu=True)

cap = cv2.VideoCapture(SRC)
fps = cap.get(cv2.CAP_PROP_FPS) or 24.0


def grab(t):
    cap.set(cv2.CAP_PROP_POS_FRAMES, int(t * fps))
    _ok, frame = cap.read()
    return frame


def layout_ok(exp, got):
    want = "horizontal" if "horiz" in exp else "vertical" if "vert" in exp else "no_text"
    return got == "no_text" if want == "no_text" else got.startswith(want)


def pol_ok(exp, got):
    return True if exp == "none" else got in (exp.replace("-", "_"), "mixed")


def check(case, result):
    """Return (ok, list of '<field>:exp!=got') over present expectations."""
    bad = []
    if "layout" in case and not layout_ok(case["layout"], result["layout"]):
        bad.append(f"layout:{case['layout']}!={result['layout']}")
    if "polarity" in case and not pol_ok(case["polarity"], result["polarity"]):
        bad.append(f"polarity:{case['polarity']}!={result['polarity']}")
    if "status" in case and case["status"] != result["status"]:
        bad.append(f"status:{case['status']}!={result['status']}")
    if "reason" in case and case["reason"] != result["reject_reason"]:
        bad.append(f"reason:{case['reason']}!={result['reject_reason']}")
    if "cols" in case and result["status"] != "reject" and len(result["columns"]) != case["cols"]:
        bad.append(f"cols:{case['cols']}!={len(result['columns'])}")
    if "mask_source" in case and case["mask_source"] != result["mask_dbg"].get("source"):
        bad.append(f"mask_source:{case['mask_source']}!={result['mask_dbg'].get('source')}")
    if "mask_source_in" in case and result["mask_dbg"].get("source") not in case["mask_source_in"]:
        bad.append(f"mask_source_in:{case['mask_source_in']}!={result['mask_dbg'].get('source')}")
    return (not bad), bad


raw_p = raw_t = core_p = core_t = 0
cat = defaultdict(lambda: [0, 0])

for case in CASES:
    x0, y0, x1, y1 = case["roi"]
    roi = grab(case["t"])[y0:y1, x0:x1]
    result = columnize(roi)
    ok, bad = check(case, result)
    deferred = case.get("deferred", False)

    raw_t += 1
    raw_p += int(ok)
    if not deferred:
        core_t += 1
        core_p += int(ok)

    category = case.get("category", "?")
    cat[category][1] += 1
    cat[category][0] += int(ok)

    tag = "PASS" if ok else ("FAIL*(deferred)" if deferred else "FAIL")
    print(f"\n## {case['name']}  [{category}]  {case.get('notes', '')}")
    print(
        f"   polarity={result['polarity']:<14} "
        f"source={result['mask_dbg'].get('source', '?'):<9} {result['mask_dbg']}"
    )
    print(
        f"   layout={result['layout']:<14} status={result['status']:<18} "
        f"cols={len(result['columns'])} conf={result['split_confidence']} "
        f"mask_q={result['mask_quality']} reason={result['reject_reason']}"
    )
    print(f"   => {tag}" + (f"   MISMATCH {bad}" if bad else ""))
    if deferred:
        print(f"      (deferred: {case.get('deferred_reason', '')})")

    if not NO_OCR:
        route = route_japanese_roi(roi, result, read_bgr=_ja_reader.read_bgr)
        print(
            f"   route script={route['script']} reader={route['reader']} "
            f"reader_status={route['reader_status']} ocr_calls={route['ocr_calls']} "
            f"reason={route['route_reason']}"
        )
        if route["reads"]:
            texts = [read["text"] for read in route["reads"]]
            ratios = [read["jp_ratio"] for read in route["reads"]]
            times = [read["ms"] for read in route["reads"]]
            print(f"   reads={texts}  jp_ratio={ratios}  ms={times}  joined R->L: '{route['joined']}'")

print(f"\n==== raw {raw_p}/{raw_t}  |  core(non-deferred) {core_p}/{core_t} ====")
print("     by category: " + "  ".join(f"{k} {v[0]}/{v[1]}" for k, v in sorted(cat.items())))
cap.release()
