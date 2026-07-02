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
free.

P1 cross-frame state (measured, debug_mad3.py / NEXT-STEPS-ROADMAP.md P1):
- Captions MOVE (語っといて drifts x 843->872 over 13 frames). Each column_seed track carries an
  alpha-beta filter (steady-state Kalman) on its center; `_best_center` matches against the PREDICTED
  center, so a drifting caption re-links across detection gaps where its static last-position would
  fall outside `center_r`. Falls back to the raw bbox center when no filter state exists.
- Stale guard: pass `frame=` to update() and each OCR'd track stores a thumbnail of its crop; on later
  matched frames a mean-abs-diff (MAD) above `stale_mad` for `stale_confirm` consecutive frames re-arms
  OCR (dialogue changed at the same position). MEASURED design, not guessed: thumbs must be taken at the
  TRACKED bbox (detected x-range × track union-y) — a fixed screen box accumulates drift misalignment and
  same-content noise (max 13.8) then OVERLAPS the hardest change case (same-style column swap, 10.4).
  At the tracked bbox: same-content <= 2.7, hardest swap >= 9.3 (3.4x separation) -> stale_mad=5.0.

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

import cv2
import numpy as np

NEW, OCR_DONE, HOLD, EXPIRE = "new", "ocr_done", "hold", "expire"

THUMB_SIZE = (24, 96)   # column-shaped (w, h): squashing a tall crop to a square destroys the glyphs


class _AlphaBeta:
    """Steady-state (fixed-gain) Kalman filter on a track center — constant-velocity model, position
    measurement, one independent filter per axis. gains 0.6/0.4 learn a caption's velocity within ~3
    hits (a sparse caption gives us no more), at the cost of some measurement-noise passthrough —
    detection-center jitter is a few px, well inside center_r, so responsiveness wins here.
    Hand-rolled (no cv2.KalmanFilter): 10 lines, trivially portable to the C# live engine later."""

    def __init__(self, cx, cy, alpha=0.6, beta=0.4):
        self.cx, self.cy, self.vx, self.vy = float(cx), float(cy), 0.0, 0.0
        self.alpha, self.beta = alpha, beta

    def predict(self, dt=1.0):
        return self.cx + self.vx * dt, self.cy + self.vy * dt

    def update(self, mx, my, dt=1.0):
        px, py = self.predict(dt)
        rx, ry = mx - px, my - py
        self.cx, self.cy = px + self.alpha * rx, py + self.alpha * ry
        self.vx += self.beta * rx / dt
        self.vy += self.beta * ry / dt


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
                 center_r=20.0, stale_mad=12.0, stale_mad_by_kind=None, stale_confirm=2,
                 quality_fn=None):
        self.stable_frames = stable_frames
        # seed admission: a flickering column_seed lands at a new position each frame so its track
        # never reaches this age -> never OCR'd; a real caption (これ/語っといて) persists and does.
        # e.g. {"column_seed": 3}. block_merged/broad_split fall back to stable_frames.
        self.stable_by_kind = stable_by_kind or {}
        self.expire_frames = expire_frames
        self.match_iou = match_iou
        self.center_r = center_r   # column_seed center-link radius; see _best_center
        # stale guard (only active when update() gets frame=). Per-kind thresholds, all measured
        # (debug_mad3.py / debug_stale.py): a column_seed bbox is TIGHT around its column — live floor
        # 6.2 (drifting caption + growing y-union), hardest same-style swap 9.0 -> 7.5 between; a
        # block_merged/broad_split bbox is LOOSE (padding + multi-column) so animated background inside
        # it raises the same-TEXT floor to ~8.5 on the moving-diamond clip, while a genuine text swap
        # measures 45+ -> 12.0. 2-frame confirm rides out one-frame spikes (a 15.5 transient on a clean
        # track was measured and held). ⚠ one-clip calibration, thin column_seed margin — re-check on
        # more footage (NEXT-STEPS-ROADMAP P1 note).
        self.stale_mad = stale_mad
        self.stale_mad_by_kind = stale_mad_by_kind or {"column_seed": 7.5}
        self.stale_confirm = stale_confirm
        # optional post-OCR quality gate (P2b, e.g. ocr_quality.ocr_quality): a read that isn't "ok"
        # never becomes the track's text — the previous good text (if any) is HELD instead, and the
        # track still counts as OCR'd (no per-frame retry loop on a garbage region).
        self.quality_fn = quality_fn
        self.tracks = {}      # id -> {bbox, kind, age, missed, state, text, ocr_done, kf, thumb, dirty}
        self._next_id = 0

    def _thumb(self, frame, bbox, t):
        """Gray column-shaped thumbnail of the TRACKED crop: current detected x-range × the track's
        union y-extent for a column_seed (stable vertical framing while the tail-probe flickers), raw
        bbox otherwise. Tracking the bbox (not a fixed screen box) is what makes same-content MAD
        collapse to detection jitter — see module docstring."""
        x0, y0, x1, y1 = bbox
        if t.get("kind") == "column_seed" and "y_top" in t:
            y0, y1 = t["y_top"], t["y_bot"]
        h, w = frame.shape[:2]
        x0, y0 = max(0, int(x0)), max(0, int(y0))
        x1, y1 = min(w, int(x1)), min(h, int(y1))
        if x1 - x0 < 2 or y1 - y0 < 2:
            return None
        gray = cv2.cvtColor(frame[y0:y1, x0:x1], cv2.COLOR_BGR2GRAY)
        return cv2.resize(gray, THUMB_SIZE).astype(np.float32)

    def _best(self, bbox):
        """Best-overlapping track and its IoU, regardless of threshold (for 4A jitter diagnostics)."""
        best_id, best_iou = None, 0.0
        for tid, t in self.tracks.items():
            i = _iou(bbox, t["bbox"])
            if i > best_iou:
                best_id, best_iou = tid, i
        return best_id, best_iou

    def _best_center(self, bbox):
        """Nearest COLUMN_SEED track whose PREDICTED center is within center_r (see module docstring).
        Only used as a fallback when IoU matching fails, and only for column_seed candidates/tracks —
        block_merged/broad_split never use center-linking. Prediction (alpha-beta, dt = frames since the
        track was last seen) is what lets a drifting caption re-link across a gap where drift × gap
        would push its LAST position outside center_r; falls back to the raw bbox center if the track
        has no filter state."""
        cx, cy = (bbox[0] + bbox[2]) * 0.5, (bbox[1] + bbox[3]) * 0.5
        best_id, best_d = None, self.center_r
        for tid, t in self.tracks.items():
            if t.get("kind") != "column_seed":
                continue
            kf = t.get("kf")
            if kf is not None:
                tcx, tcy = kf.predict(t["missed"] + 1)
            else:
                tcx = (t["bbox"][0] + t["bbox"][2]) * 0.5
                tcy = (t["bbox"][1] + t["bbox"][3]) * 0.5
            d = ((cx - tcx) ** 2 + (cy - tcy) ** 2) ** 0.5
            if d < best_d:
                best_id, best_d = tid, d
        return best_id

    def update(self, blocks, ocr_fn=None, frame=None):
        """blocks: objects with a .bbox tuple (and whatever ocr_fn needs). ocr_fn(block)->text is called
        once per tracklet when it stabilises — and again if the stale guard sees the crop content change
        (only when `frame`, the full BGR frame, is passed; without it the guard is off). Returns one dict
        per input block: {id, bbox, state, ocr_called, text, spawned, best_iou}."""
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
            cx, cy = (b.bbox[0] + b.bbox[2]) * 0.5, (b.bbox[1] + b.bbox[3]) * 0.5
            if tid is None:
                tid = self._next_id
                self._next_id += 1
                self.tracks[tid] = {"bbox": b.bbox, "kind": getattr(b, "kind", None), "age": 1,
                                    "missed": 0, "state": NEW, "text": None, "ocr_done": False,
                                    "dirty": 0}
                if self.tracks[tid]["kind"] == "column_seed":
                    self.tracks[tid]["kf"] = _AlphaBeta(cx, cy)
            else:
                t = self.tracks[tid]
                dt = t["missed"] + 1              # frames since this track was last seen
                t["bbox"] = b.bbox
                t["age"] += 1
                t["missed"] = 0
                if t.get("kf") is not None:
                    t["kf"].update(cx, cy, dt)
            t = self.tracks[tid]
            seen.add(tid)
            if t["kind"] == "column_seed":   # remember the track's max Y-extent for tail recovery
                t["y_top"] = min(t.get("y_top", b.bbox[1]), b.bbox[1])
                t["y_bot"] = max(t.get("y_bot", b.bbox[3]), b.bbox[3])

            # stale guard: same track, but the pixels changed (dialogue swap at the same position).
            # 2 consecutive dirty frames re-arm OCR; the admission block below then re-fires this frame.
            if frame is not None and not spawned and t["ocr_done"] and t.get("thumb") is not None:
                cur = self._thumb(frame, b.bbox, t)
                if cur is not None:
                    mad = float(np.mean(np.abs(cur - t["thumb"])))
                    if hasattr(self, "stale_log"):   # diagnostics only (debug scripts attach the list)
                        self.stale_log.append((tid, t["kind"], round(mad, 1), b.bbox))
                    if mad > self.stale_mad_by_kind.get(t["kind"], self.stale_mad):
                        t["dirty"] += 1
                    else:
                        t["dirty"] = 0
                    if t["dirty"] >= self.stale_confirm:
                        t["ocr_done"] = False
                        t["dirty"] = 0

            ocr_called = False
            ocr_rejected = False
            need = self.stable_by_kind.get(t["kind"], self.stable_frames)
            if not t["ocr_done"] and t["age"] >= need:
                if t["kind"] == "column_seed":
                    b = _extend_seed_y(b, t["y_top"], t["y_bot"])   # OCR the max observed column height
                if ocr_fn is not None:
                    text = ocr_fn(b)
                    if self.quality_fn is not None and self.quality_fn(text) != "ok":
                        ocr_rejected = True          # garbage read: HOLD previous good text (or None)
                    else:
                        t["text"] = text
                if frame is not None:
                    t["thumb"] = self._thumb(frame, b.bbox, t)   # baseline for the stale guard
                t["ocr_done"] = True
                t["state"] = OCR_DONE
                ocr_called = True
            elif t["ocr_done"]:
                t["state"] = HOLD
            else:
                t["state"] = NEW
            out.append({"id": tid, "bbox": b.bbox, "state": t["state"], "ocr_called": ocr_called,
                        "ocr_rejected": ocr_rejected, "text": t["text"], "spawned": spawned,
                        "best_iou": round(best_iou, 3)})

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

    # P1 Kalman drift-link: a caption drifting 8px/frame, hits f0-f3 then a 3-frame gap then f7.
    # Across the gap the drift is 32px: the STATIC last center (144) misses the f7 center (176) by
    # 32 > center_r=20, but the alpha-beta prediction (142.1 + 8.3*4 = 175.4) lands 0.6px away ->
    # linked, age reaches the count-5 gate at f7, OCR fires exactly once.
    def drift_box(f):
        return BK((100 + 8 * f, 100, 140 + 8 * f, 300), "column_seed")
    cache8 = TemporalBlockCache(stable_frames=2, expire_frames=3, center_r=20.0,
                                stable_by_kind={"column_seed": 5})
    drift_calls = []
    for f in (0, 1, 2, 3):
        drift_calls.append(cache8.update([drift_box(f)])[0]["ocr_called"])
    for _ in range(3):
        cache8.update([])                                        # gap: track coasts, missed grows
    drift_calls.append(cache8.update([drift_box(7)])[0]["ocr_called"])
    assert drift_calls == [False, False, False, False, True], drift_calls
    assert len(cache8.tracks) == 1, cache8.tracks                 # one track across the gap

    # Control: strip the filter after every update -> _best_center falls back to the static bbox
    # center -> the f7 hit (32px away) spawns a NEW track -> the count-5 gate is never reached.
    cache9 = TemporalBlockCache(stable_frames=2, expire_frames=3, center_r=20.0,
                                stable_by_kind={"column_seed": 5})
    total9 = 0
    for f in (0, 1, 2, 3):
        total9 += cache9.update([drift_box(f)])[0]["ocr_called"]
        for t in cache9.tracks.values():
            t.pop("kf", None)
    for _ in range(3):
        cache9.update([])
    total9 += cache9.update([drift_box(7)])[0]["ocr_called"]
    # the f7 hit spawned FRESH (age 1; the old track, unmatched at f7, hit missed=4 and expired) and
    # the count-5 gate was never reached -> zero OCR. With the filter (cache8) the same input OCR'd.
    assert total9 == 0, total9
    assert [t["age"] for t in cache9.tracks.values()] == [1], cache9.tracks

    # P1 stale guard: same position, content swaps -> re-OCR once after the 2-frame confirm.
    def synth_frame(pattern, shift=0):
        fr = np.full((200, 120, 3), 200, np.uint8)
        rows = ((40, 50), (90, 100)) if pattern == "A" else ((65, 75), (140, 150))
        for r0, r1 in rows:
            fr[r0:r1, 10:58] = 0
        return cv2.add(fr, shift) if shift else fr
    bbox = (10, 10, 58, 190)
    reads = []
    cache10 = TemporalBlockCache(stable_frames=2, expire_frames=3)   # block_merged stale_mad=12 default
    seqs = [("A", 0), ("A", 0), ("A", 2), ("B", 0), ("B", 0), ("B", 0)]
    calls10 = []
    for i, (pat, sh) in enumerate(seqs):
        fr = synth_frame(pat, sh)
        r = cache10.update([BK(bbox, "block_merged")], ocr_fn=lambda b: f"read{len(reads)}", frame=fr)[0]
        if r["ocr_called"]:
            reads.append(i)
        calls10.append(r["ocr_called"])
    # f1: stabilises (OCR #1, thumb stored). f2: +2 brightness = MAD 2 < 12, clean. f3: pattern swap
    # (MAD ~44 > 12), dirty 1 (no OCR yet). f4: dirty 2 -> re-armed -> OCR #2 same frame. f5: clean.
    assert calls10 == [False, True, False, False, True, False], calls10
    assert reads == [1, 4], reads

    # P2b quality gate wiring: a garbage read never becomes the track text, and the track still counts
    # as OCR'd (no per-frame retry loop); a later stale re-OCR that returns garbage HOLDs the good text.
    qf = lambda s: "garbage_dots" if s.startswith("．") else "ok"
    cache11 = TemporalBlockCache(stable_frames=2, quality_fn=qf)
    cache11.update([B(static)], ocr_fn=lambda b: "．．．")
    r = cache11.update([B(static)], ocr_fn=lambda b: "．．．")[0]
    assert r["ocr_called"] and r["ocr_rejected"] and r["text"] is None, r
    r = cache11.update([B(static)], ocr_fn=lambda b: "x")[0]
    assert not r["ocr_called"] and r["state"] == HOLD and r["text"] is None, r   # no retry loop

    cache12 = TemporalBlockCache(stable_frames=2, quality_fn=qf)   # stale re-OCR returns garbage
    texts = iter(["good", "．．．"])
    for i, (pat, sh) in enumerate([("A", 0), ("A", 0), ("B", 0), ("B", 0)]):
        r = cache12.update([BK(bbox, "block_merged")], ocr_fn=lambda b: next(texts),
                           frame=synth_frame(pat, sh))[0]
    assert r["ocr_called"] and r["ocr_rejected"] and r["text"] == "good", r      # previous text HELD
    print("temporal_cache self-check OK")


if __name__ == "__main__":
    _demo()
