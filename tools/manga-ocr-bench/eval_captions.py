"""All-caption recall evaluator (NEXT-STEPS-ROADMAP.md P2c). The old acceptance check only tracked two
tags (KORE/KATATTOITE) — good for seed admission, but blind to a config silently killing other real text
(the x>1200 realtime deferral also ate 何がそんな不満なんだ, a legit white-panel caption). Score every
caption in expected_captions.json instead.

    venv/Scripts/python.exe eval_captions.py
"""
import json

from ocr_quality import ocr_quality

MANIFEST = r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\expected_captions.json"


def _center(bbox):
    return (bbox[0] + bbox[2]) * 0.5, (bbox[1] + bbox[3]) * 0.5


def _in_region(bbox, region):
    cx, cy = _center(bbox)
    x0, y0, x1, y1 = region
    return x0 <= cx <= x1 and y0 <= cy <= y1


def _matches(expected, bbox, text):
    if "region" in expected and not _in_region(bbox, expected["region"]):
        return False
    if "text" in expected and expected["text"] not in text:
        return False
    if "text_contains" in expected and not all(s in text for s in expected["text_contains"]):
        return False
    return True


def evaluate(expected_captions, ocr_results, deferred_count=0):
    """expected_captions: list from expected_captions.json. Entries with deferred=True are excluded from
    recall scoring (they're expected to be absent, not found). ocr_results: list of {"bbox", "text"}, one
    per OCR call (same shape TemporalBlockCache.update()/real_run.py already produce)."""
    scored = [e for e in expected_captions if not e.get("deferred")]
    found = set()
    garbage = 0
    for r in ocr_results:
        if ocr_quality(r["text"]) != "ok":
            garbage += 1
            continue
        for e in scored:
            if e["id"] not in found and _matches(e, r["bbox"], r["text"]):
                found.add(e["id"])

    must_have = [e for e in scored if e.get("must_have")]
    ocr_calls = len(ocr_results)
    return {
        "all_caption_recall": round(len(found) / len(scored), 3) if scored else 1.0,
        "must_have_recall": round(sum(e["id"] in found for e in must_have) / len(must_have), 3)
                             if must_have else 1.0,
        "garbage_output_count": garbage,
        "ocr_calls": ocr_calls,
        "useful_ocr_per_call": round(len(found) / ocr_calls, 3) if ocr_calls else 0.0,
        "dropped_by_deferral_count": deferred_count,
        "found": sorted(found),
        "missing_must_have": sorted(e["id"] for e in must_have if e["id"] not in found),
    }


def _demo():
    with open(MANIFEST, encoding="utf-8") as fh:
        expected = json.load(fh)

    # measured, real_run_results.md "FullRecall" (15 OCR calls, incl. the whitebox caption + art garbage)
    full_recall = [
        {"bbox": (551, 43, 720, 448), "text": "う　他に何がいる？　以上"},
        {"bbox": (1208, 123, 1311, 303), "text": "何がそ　不満"},
        {"bbox": (1463, 418, 1703, 776), "text": "視感　．．．　．．．．．．　えー"},
        {"bbox": (1341, 419, 1452, 774), "text": "．．．．．．．．．．．．　ぐ　（"},
        {"bbox": (840, 473, 1006, 749), "text": "散々ワガ　話"},
        {"bbox": (208, 337, 412, 786), "text": "そんなところも　割　嫌いじゃ無い"},
        {"bbox": (1425, 222, 1485, 478), "text": "うん．．．．．．"},
        {"bbox": (1215, 112, 1435, 506), "text": "．．．．．．。　何がそんな　不満なんだ"},
        {"bbox": (784, 161, 822, 256), "text": "これ"},
        {"bbox": (1225, 123, 1328, 303), "text": "何がそ　不満"},
        {"bbox": (859, 545, 903, 792), "text": "語っといて"},
        {"bbox": (1235, 198, 1299, 418), "text": "不満なんだ"},
        {"bbox": (1397, 55, 1537, 364), "text": "．．．．．．　．．．　で、　．．．"},
        {"bbox": (1243, 125, 1437, 506), "text": "«　何がそんな　不満なんだ"},
        {"bbox": (1632, 180, 1700, 313), "text": "．．．"},
    ]
    result = evaluate(expected, full_recall)
    assert result["ocr_calls"] == 15, result
    assert result["must_have_recall"] == 1.0, result
    assert result["found"] == ["katattoite", "kore", "whitebox"], result["found"]
    assert result["missing_must_have"] == [], result
    assert result["garbage_output_count"] == 5, result   # the 5 dot-noise art-region lines (measured)

    # measured, real_run_results.md "Realtime" (5 OCR calls) — the x>1200 deferral kills whitebox entirely,
    # exactly the collateral-damage bug P2's allow_regions exists to fix.
    realtime = [
        {"bbox": (551, 43, 720, 448), "text": "う　他に何がいる？　以上"},
        {"bbox": (840, 473, 1006, 749), "text": "散々ワガ　話"},
        {"bbox": (208, 337, 412, 786), "text": "そんなところも　割　嫌いじゃ無い"},
        {"bbox": (784, 161, 822, 256), "text": "これ"},
        {"bbox": (859, 545, 903, 792), "text": "語っといて"},
    ]
    result2 = evaluate(expected, realtime, deferred_count=10)
    assert result2["ocr_calls"] == 5, result2
    assert result2["must_have_recall"] < 1.0, result2
    assert result2["missing_must_have"] == ["whitebox"], result2
    assert result2["garbage_output_count"] == 0, result2
    assert result2["dropped_by_deferral_count"] == 10, result2

    print("eval_captions self-check OK")
    print(f"  FullRecall: {result}")
    print(f"  Realtime:   {result2}")


if __name__ == "__main__":
    _demo()
