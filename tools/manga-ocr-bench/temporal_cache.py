"""Temporal block cache (Phase 2 / Patch #3) — cut OCR calls on a video stream.

The cheap-CV detector emits confirmed blocks every frame, but manga-ocr has no KV-cache so re-reading a
stable caption every frame is the cost. This tracker matches a frame's blocks to existing tracklets by
bbox IoU and only fires OCR once a block has been STABLE for a few frames; thereafter it HOLDs (reuses the
cached text, 0 OCR). A block that disappears EXPIREs after a few missed frames.

State machine per tracklet:
    NEW       just appeared (age < stable_frames)        -> no OCR
    OCR_DONE  reached stable_frames this frame           -> OCR fires exactly once
    HOLD      already OCR'd, still present               -> reuse cached text, 0 OCR
    EXPIRE    missed > expire_frames                     -> dropped

hard_mixed_art_text never reaches here (it is a confirm reject, not a kept block), so it is skipped for
free. ponytail: matching is position-IoU only; a same-position SCENE CHANGE would reuse stale text — add a
crop-diff re-OCR trigger if that shows up on real clips (cheap mean-abs-diff on the cached crop).

Seed admission (measured, SEED-UTILITY-MEASURED.md): a real sparse caption (これ, detected only 3/15
frames) recurs near the same CENTER, but its recall-source bbox EXTENT varies between hits enough that
pairwise IoU < match_iou — so IoU-only matching fragments its 3 hits into separate age-1 stubs that never
reach a consecutive-age gate. Flicker seeds, by contrast, appear at a genuinely different center each time.
So for column_seed only, `_best_center` links a new detection to an existing track by CENTER distance
(within `center_r`) when IoU fails, and `stable_by_kind={"column_seed": 3}` gates on the resulting
count-of-hits rather than consecutive age. `center_r` must stay below the minimum real inter-column gap
(~54px on the measured clip) or it will fuse two adjacent columns into one track.
"""

import copy

NEW, OCR_DONE, HOLD, EXPIRE = "new", "ocr_done", "hold", "expire"


def _extend_seed_y(b, y_top, y_bot):
    """Return a COPY of a column_seed block whose Y-extent covers [y_top, y_bot] (keeping its CURRENT x —
    correct for a horizontally-drifting subtitle), mirrored into its single column_boxes_abs. Returned to
    ocr_fn so the reader gets the track's MAX observed column height: the per-frame CLAHE tail-probe
    (_extend_column_tails) is frame-inconsistent (finds て on some frames, misses on others), so without
    this the OCR-firing frame may drop a faint column-end glyph another frame already proved is there.
    Never mutates the caller's block (callers reuse block lists across cache runs). ponytail: y-union
    memory only; full cross-frame smoothing (Kalman) is the P1 roadmap item."""
    x0, y0, x1, y1 = b.bbox
    ny0, ny1 = min(y0, y_top), max(y1, y_bot)
    if (ny0, ny1) == (y0, y1):
        return b
    nb = copy.copy(b)
    nb.bbox = (x0, ny0, x1, ny1)
    cols = getattr(b, "column_boxes_abs", None)
    if cols and len(cols) == 1:
        nb.column_boxes_abs = ((x0, ny0, x1, ny1),)
    return nb


def _iou(a, b):
    ix0, iy0 = max(a[0], b[0]), max(a[1], b[1])
    ix1, iy1 = min(a[2], b[2]), min(a[3], b[3])
    inter = max(0, ix1 - ix0) * max(0, iy1 - iy0)
    if inter == 0:
        return 0.0
    aa = (a[2] - a[0]) * (a[3] - a[1])
    bb = (b[2] - b[0]) * (b[3] - b[1])
    return inter / float(aa + bb - inter)


class TemporalBlockCache:
    def __init__(self, stable_frames=2, expire_frames=3, match_iou=0.5, stable_by_kind=None,
                 center_r=20.0):
        self.stable_frames = stable_frames
        # seed admission: a flickering column_seed lands at a new position each frame so its track
        # never reaches this age -> never OCR'd; a real caption (これ/語っといて) persists and does.
        # e.g. {"column_seed": 3}. block_merged/broad_split fall back to stable_frames.
        self.stable_by_kind = stable_by_kind or {}
        self.expire_frames = expire_frames
        self.match_iou = match_iou
        self.center_r = center_r   # column_seed center-link radius; see _best_center
        self.tracks = {}      # id -> {bbox, kind, age, missed, state, text, ocr_done}
        self._next_id = 0

    def _best(self, bbox):
        """Best-overlapping track and its IoU, regardless of threshold (for 4A jitter diagnostics)."""
        best_id, best_iou = None, 0.0
        for tid, t in self.tracks.items():
            i = _iou(bbox, t["bbox"])
            if i > best_iou:
                best_id, best_iou = tid, i
        return best_id, best_iou

    def _best_center(self, bbox):
        """Nearest COLUMN_SEED track whose center is within center_r (see module docstring). Only
        used as a fallback when IoU matching fails, and only for column_seed candidates/tracks — block_
        merged/broad_split never use center-linking."""
        cx, cy = (bbox[0] + bbox[2]) * 0.5, (bbox[1] + bbox[3]) * 0.5
        best_id, best_d = None, self.center_r
        for tid, t in self.tracks.items():
            if t.get("kind") != "column_seed":
                continue
            tcx = (t["bbox"][0] + t["bbox"][2]) * 0.5
            tcy = (t["bbox"][1] + t["bbox"][3]) * 0.5
            d = ((cx - tcx) ** 2 + (cy - tcy) ** 2) ** 0.5
            if d < best_d:
                best_id, best_d = tid, d
        return best_id

    def update(self, blocks, ocr_fn=None):
        """blocks: objects with a .bbox tuple (and whatever ocr_fn needs). ocr_fn(block)->text is called
        AT MOST once per tracklet, the frame it stabilises. Returns one dict per input block:
        {id, bbox, state, ocr_called, text, spawned, best_iou}."""
        seen = set()
        out = []
        for b in blocks:
            cand_id, best_iou = self._best(b.bbox)
            matched = cand_id is not None and best_iou >= self.match_iou
            if not matched and getattr(b, "kind", None) == "column_seed":
                cid = self._best_center(b.bbox)   # IoU failed -> try center-link (column_seed only)
                if cid is not None:
                    cand_id, matched = cid, True
            spawned = not matched
            tid = None if spawned else cand_id
            if tid is None:
                tid = self._next_id
                self._next_id += 1
                self.tracks[tid] = {"bbox": b.bbox, "kind": getattr(b, "kind", None), "age": 1,
                                    "missed": 0, "state": NEW, "text": None, "ocr_done": False}
            else:
                t = self.tracks[tid]
                t["bbox"] = b.bbox
                t["age"] += 1
                t["missed"] = 0
            t = self.tracks[tid]
            seen.add(tid)
            if t["kind"] == "column_seed":   # remember the track's max Y-extent for tail recovery
                t["y_top"] = min(t.get("y_top", b.bbox[1]), b.bbox[1])
                t["y_bot"] = max(t.get("y_bot", b.bbox[3]), b.bbox[3])

            ocr_called = False
            need = self.stable_by_kind.get(t["kind"], self.stable_frames)
            if not t["ocr_done"] and t["age"] >= need:
                if t["kind"] == "column_seed":
                    b = _extend_seed_y(b, t["y_top"], t["y_bot"])   # OCR the max observed column height
                if ocr_fn is not None:
                    t["text"] = ocr_fn(b)
                t["ocr_done"] = True
                t["state"] = OCR_DONE
                ocr_called = True
            elif t["ocr_done"]:
                t["state"] = HOLD
            else:
                t["state"] = NEW
            out.append({"id": tid, "bbox": b.bbox, "state": t["state"], "ocr_called": ocr_called,
                        "text": t["text"], "spawned": spawned, "best_iou": round(best_iou, 3)})

        for tid in list(self.tracks):
            if tid not in seen:
                self.tracks[tid]["missed"] += 1
                if self.tracks[tid]["missed"] > self.expire_frames:
                    del self.tracks[tid]
        return out


def _demo():
    """Self-check: a static block present 5 frames must OCR exactly once (frame 2), then HOLD; after it
    leaves it must EXPIRE; a moving block (no IoU overlap) re-OCRs each frame."""
    class B:
        def __init__(self, bbox): self.bbox = bbox

    cache = TemporalBlockCache(stable_frames=2, expire_frames=3)
    static = (100, 100, 150, 300)
    calls = []
    for f in range(5):
        res = cache.update([B(static)], ocr_fn=lambda b: "これ")
        calls.append(res[0]["ocr_called"])
    assert calls == [False, True, False, False, False], calls          # OCR once, at stabilise
    assert cache.tracks[0]["state"] == HOLD and cache.tracks[0]["text"] == "これ"

    for f in range(4):  # block gone -> EXPIRE after >expire_frames missed
        cache.update([])
    assert cache.tracks == {}, cache.tracks

    # a moving block never matches a prior tracklet -> NEW every frame, never reaches stable -> 0 OCR,
    # but each frame makes a fresh track (real moving subtitles need per-frame OCR, which is correct).
    cache2 = TemporalBlockCache(stable_frames=2, expire_frames=3)
    moved = [cache2.update([B((10 + 200 * f, 10, 60 + 200 * f, 120))])[0]["state"] for f in range(3)]
    assert moved == [NEW, NEW, NEW], moved

    # total OCR over a 10-frame static caption = 1 (vs naive 10)
    cache3 = TemporalBlockCache(stable_frames=2)
    total = sum(cache3.update([B(static)], ocr_fn=lambda b: "x")[0]["ocr_called"] for _ in range(10))
    assert total == 1, total

    # kind-tiered admission: a column_seed needs 4 frames, a block_merged only 2.
    class BK:
        def __init__(self, bbox, kind): self.bbox, self.kind = bbox, kind
    cache4 = TemporalBlockCache(stable_frames=2, stable_by_kind={"column_seed": 4})
    seed_calls = [cache4.update([BK(static, "column_seed")])[0]["ocr_called"] for _ in range(5)]
    assert seed_calls == [False, False, False, True, False], seed_calls   # OCR at age 4, not 2
    cache5 = TemporalBlockCache(stable_frames=2, stable_by_kind={"column_seed": 4})
    blk_calls = [cache5.update([BK(static, "block_merged")])[0]["ocr_called"] for _ in range(5)]
    assert blk_calls == [False, True, False, False, False], blk_calls     # block still age 2

    # Center-linked count (measured fix for これ, SEED-UTILITY-MEASURED.md): three SAME-CENTER but
    # different-SIZE column_seed boxes (mimics a faint recall-source caption whose bbox extent varies
    # between hits) have pairwise IoU < match_iou=0.5, so IoU-only matching would spawn 3 separate
    # age-1 tracks. With center_r=20 they center-link into ONE track and OCR exactly once at the 3rd hit.
    sparse = [
        BK((100, 100, 140, 140), "column_seed"),  # 40x40, center (120,120)
        BK((115, 115, 125, 125), "column_seed"),  # 10x10 concentric -> IoU vs prior = 0.0625
        BK((108, 108, 132, 132), "column_seed"),  # 24x24 concentric -> IoU vs prior = 0.174
    ]
    cache6 = TemporalBlockCache(stable_frames=2, expire_frames=3, center_r=20.0,
                                stable_by_kind={"column_seed": 3})
    sparse_calls = [cache6.update([b])[0]["ocr_called"] for b in sparse]
    assert sparse_calls == [False, False, True], sparse_calls   # center-link -> 1 track -> OCR at hit 3
    assert len(cache6.tracks) == 1, cache6.tracks                # proves it's ONE track, not 3

    # Control: center_r=0 disables the fallback (a distance >= 0 can never be < 0), so the SAME 3 boxes
    # spawn 3 separate age-1 tracks under IoU-only matching -> never reach the count-3 gate -> zero OCR.
    # Proves CENTER-LINK, not the count threshold alone, is what admits the sparse caption.
    cache7 = TemporalBlockCache(stable_frames=2, expire_frames=3, center_r=0.0,
                                stable_by_kind={"column_seed": 3})
    control_calls = [cache7.update([b])[0]["ocr_called"] for b in sparse]
    assert control_calls == [False, False, False], control_calls
    assert len(cache7.tracks) == 3, cache7.tracks                 # 3 distinct spawns, never linked
    print("temporal_cache self-check OK")


if __name__ == "__main__":
    _demo()
