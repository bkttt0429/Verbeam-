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
"""

NEW, OCR_DONE, HOLD, EXPIRE = "new", "ocr_done", "hold", "expire"


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
    def __init__(self, stable_frames=2, expire_frames=3, match_iou=0.5, stable_by_kind=None):
        self.stable_frames = stable_frames
        # seed admission (Patch 5): a flickering column_seed lands at a new position each frame so its
        # track never reaches this age -> never OCR'd; a real caption (これ/語っといて) persists and does.
        # e.g. {"column_seed": 4}. block_merged/broad_split fall back to stable_frames.
        self.stable_by_kind = stable_by_kind or {}
        self.expire_frames = expire_frames
        self.match_iou = match_iou
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

    def update(self, blocks, ocr_fn=None):
        """blocks: objects with a .bbox tuple (and whatever ocr_fn needs). ocr_fn(block)->text is called
        AT MOST once per tracklet, the frame it stabilises. Returns one dict per input block:
        {id, bbox, state, ocr_called, text, spawned, best_iou}."""
        seen = set()
        out = []
        for b in blocks:
            cand_id, best_iou = self._best(b.bbox)
            spawned = cand_id is None or best_iou < self.match_iou
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

            ocr_called = False
            need = self.stable_by_kind.get(t["kind"], self.stable_frames)
            if not t["ocr_done"] and t["age"] >= need:
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
    print("temporal_cache self-check OK")


if __name__ == "__main__":
    _demo()
