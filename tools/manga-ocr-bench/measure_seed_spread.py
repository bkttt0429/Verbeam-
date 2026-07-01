"""One-shot measurement for the seed-admission redesign (disposable analysis script).

Resolves the load-bearing unknown: does これ recur at a STABLE center while flicker seeds scatter?
If yes, a center-match (not IoU-match) cache admits これ in realtime without admitting flicker,
collapsing the A/B tradeoff. Also reproduces the A=23 / B=15 baseline and probes art-region stability.

    venv/Scripts/python.exe measure_seed_spread.py
"""
import sys
from collections import Counter, defaultdict

import cv2

from block_detector import detect_text_blocks
from temporal_cache import TemporalBlockCache

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
START, FRAMES = 48.0, 15


def where(bbox):
    x0, y0 = bbox[0], bbox[1]
    if 740 <= x0 <= 800 and y0 < 300:
        return "KORE"
    if 830 <= x0 <= 860 and y0 < 600:
        return "KATATTOITE"
    return None


def center(b):
    return ((b[0] + b[2]) / 2.0, (b[1] + b[3]) / 2.0)


class CenterMatchCache(TemporalBlockCache):
    """Rung-1 fix: match a block to an existing track by IoU>thresh OR center within center_r px.
    A small caption whose recall bbox EXTENT varies (IoU breaks) but whose CENTER is stable will
    re-link to one track -> age accumulates -> admitted; scattered flicker centers won't link."""
    def __init__(self, *a, center_r=15.0, **k):
        super().__init__(*a, **k)
        self.center_r = center_r

    def _best(self, bbox):
        bid, biou = super()._best(bbox)
        bcx, bcy = center(bbox)
        cid, cd = None, 1e9
        for tid, t in self.tracks.items():
            tcx, tcy = center(t["bbox"])
            d = ((bcx - tcx) ** 2 + (bcy - tcy) ** 2) ** 0.5
            if d < cd:
                cid, cd = tid, d
        if cid is not None and cd < self.center_r:
            return cid, max(biou, self.match_iou)  # force "matched" (not spawned)
        return bid, biou


def run_cache(frames_blocks, cache):
    """Feed a captured block sequence through a cache; return (total_ocr, by_kind, tags_ocrd)."""
    total = 0
    by_kind = Counter()
    tags = set()
    for blocks in frames_blocks:
        res = cache.update(blocks)
        for b, r in zip(blocks, res):
            if r["ocr_called"]:
                total += 1
                by_kind[b.kind] += 1
                w = where(b.bbox)
                if w:
                    tags.add(w)
    return total, dict(by_kind), sorted(tags)


def main():
    cap = cv2.VideoCapture(SRC)
    fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
    start = int(START * fps)

    # --- capture the detector output ONCE (the slow part), reuse for every cache config ---
    frames_blocks = []
    for i in range(FRAMES):
        cap.set(cv2.CAP_PROP_POS_FRAMES, start + i)
        ok, frame = cap.read()
        if not ok:
            break
        blocks = detect_text_blocks(frame, scorer="cc", group="graph",
                                    emit_seeds=True, confirm_raw=True)
        frames_blocks.append(blocks)
    cap.release()
    n = len(frames_blocks)
    print(f"captured {n} frames @ {START}s\n")

    # --- SECTION 1: reproduce A=23 / B=15 baseline (IoU match) ---
    print("=== SECTION 1: baseline reproduction (IoU match) ===")
    for seed_stable in (2, 4):
        c = TemporalBlockCache(stable_frames=2, expire_frames=3,
                               stable_by_kind={"column_seed": seed_stable})
        total, by_kind, tags = run_cache(frames_blocks, c)
        print(f"  seed_stable={seed_stable}: OCR={total}  by_kind={by_kind}  tags_ocrd={tags}")

    # --- SECTION 2: UNKNOWN 1 — column_seed center spread per spatial cluster ---
    print("\n=== SECTION 2: column_seed center spread (KORE vs flicker) ===")
    clusters = []  # each: {"pts":[(frame,cx,cy,tag,bbox)], "cx":..,"cy":..}
    R_CLUSTER = 25.0
    for fi, blocks in enumerate(frames_blocks):
        for b in blocks:
            if b.kind != "column_seed":
                continue
            cx, cy = center(b.bbox)
            best, bd = None, 1e9
            for cl in clusters:
                d = ((cx - cl["cx"]) ** 2 + (cy - cl["cy"]) ** 2) ** 0.5
                if d < bd:
                    best, bd = cl, d
            if best is not None and bd < R_CLUSTER:
                best["pts"].append((fi, cx, cy, where(b.bbox), b.bbox))
                best["cx"] = sum(p[1] for p in best["pts"]) / len(best["pts"])
                best["cy"] = sum(p[2] for p in best["pts"]) / len(best["pts"])
            else:
                clusters.append({"pts": [(fi, cx, cy, where(b.bbox), b.bbox)], "cx": cx, "cy": cy})

    def spread(pts, k):
        vals = [p[k] for p in pts]
        return max(vals) - min(vals)

    clusters.sort(key=lambda cl: -len(cl["pts"]))
    print(f"  {len(clusters)} column_seed clusters (radius {R_CLUSTER:.0f}px), sorted by #frames present:")
    print(f"  {'tag':<12} {'#frames':<8} {'x-spread':<9} {'y-spread':<9} {'center(x,y)'}")
    kore_spread = flick_spreads = None
    flick_x, flick_y = [], []
    singletons = 0
    for cl in clusters:
        pts = cl["pts"]
        tag = next((p[3] for p in pts if p[3]), "-")
        xs, ys = spread(pts, 1), spread(pts, 2)
        if len(pts) == 1:
            singletons += 1
        if tag == "KORE":
            kore_spread = (len(pts), xs, ys)
        elif tag == "-":
            flick_x.append(xs); flick_y.append(ys)
        if len(pts) >= 2 or tag != "-":
            print(f"  {tag:<12} {len(pts):<8} {xs:<9.1f} {ys:<9.1f} ({cl['cx']:.0f},{cl['cy']:.0f})")
    print(f"  ... plus {singletons} singleton clusters (seen in exactly 1 frame = pure flicker)")
    if kore_spread:
        print(f"\n  KORE cluster: seen {kore_spread[0]}/{n} frames, "
              f"center x-spread={kore_spread[1]:.1f}px y-spread={kore_spread[2]:.1f}px")
    # center-to-center jump between consecutive flicker detections (untagged, multi-frame clusters)
    print(f"  untagged (flicker) multi-frame clusters: {len(flick_x)}  "
          f"median x-spread={sorted(flick_x)[len(flick_x)//2] if flick_x else 0:.1f}px")

    # --- SECTION 3: Rung-1 test — center-match cache at seed_stable=4, sweep R ---
    print("\n=== SECTION 3: Rung-1 center-match @ seed_stable=4 (does it keep KORE cheaply?) ===")
    for R in (0, 8, 12, 15, 20, 25):
        if R == 0:
            c = TemporalBlockCache(stable_frames=2, expire_frames=3, stable_by_kind={"column_seed": 4})
            label = "R=0 (IoU only, = baseline B)"
        else:
            c = CenterMatchCache(stable_frames=2, expire_frames=3,
                                 stable_by_kind={"column_seed": 4}, center_r=float(R))
            label = f"R={R}px"
        total, by_kind, tags = run_cache(frames_blocks, c)
        keep = "KORE✓" if "KORE" in tags else "KORE✗"
        print(f"  {label:<28} OCR={total:<4} col_seed={by_kind.get('column_seed',0):<3} {keep}  tags={tags}")

    # --- SECTION 3b: corrected test — center-match at seed_stable=3 (これ has exactly 3 hits) ---
    print("\n=== SECTION 3b: center-match @ seed_stable=3 (link これ's 3 sparse hits into one track) ===")
    for R in (0, 20, 30, 40):
        if R == 0:
            c = TemporalBlockCache(stable_frames=2, expire_frames=3, stable_by_kind={"column_seed": 3})
            label = "R=0 (IoU only)"
        else:
            c = CenterMatchCache(stable_frames=2, expire_frames=3,
                                 stable_by_kind={"column_seed": 3}, center_r=float(R))
            label = f"R={R}px"
        total, by_kind, tags = run_cache(frames_blocks, c)
        keep = "KORE✓" if "KORE" in tags else "KORE✗"
        print(f"  {label:<20} OCR={total:<4} col_seed={by_kind.get('column_seed',0):<3} {keep}  tags={tags}")

    # --- SECTION 2b: map real captions vs art spatially (block_merged + all kinds by x) ---
    print("\n=== SECTION 2b: block_merged cluster centers (map captions vs art) ===")
    bm_clusters = []
    for fi, blocks in enumerate(frames_blocks):
        for b in blocks:
            if b.kind != "block_merged":
                continue
            cx, cy = center(b.bbox)
            best, bd = None, 1e9
            for cl in bm_clusters:
                d = ((cx - cl["cx"]) ** 2 + (cy - cl["cy"]) ** 2) ** 0.5
                if d < bd:
                    best, bd = cl, d
            if best is not None and bd < 40:
                best["pts"].append((cx, cy)); best["cx"] = sum(p[0] for p in best["pts"]) / len(best["pts"]); best["cy"] = sum(p[1] for p in best["pts"]) / len(best["pts"])
            else:
                bm_clusters.append({"pts": [(cx, cy)], "cx": cx, "cy": cy})
    bm_clusters.sort(key=lambda cl: -len(cl["pts"]))
    for cl in bm_clusters:
        print(f"    block_merged #frames={len(cl['pts']):<3} center=({cl['cx']:.0f},{cl['cy']:.0f})")

    # --- SECTION 5: art spatial deferral — drop right-side art band, then rerun best configs ---
    print("\n=== SECTION 5: art deferral (drop blocks with center-x > X_DEFER) ===")
    for X_DEFER in (1200,):
        gated = [[b for b in blocks if center(b.bbox)[0] <= X_DEFER] for blocks in frames_blocks]
        # config B (baseline realtime): seed_stable=4 IoU
        cB = TemporalBlockCache(stable_frames=2, expire_frames=3, stable_by_kind={"column_seed": 4})
        tB, kB, tagB = run_cache(gated, cB)
        # config: seed_stable=3 + center-match R=30 (keeps これ)
        c3 = CenterMatchCache(stable_frames=2, expire_frames=3, stable_by_kind={"column_seed": 3}, center_r=30.0)
        t3, k3, tag3 = run_cache(gated, c3)
        print(f"  X_DEFER={X_DEFER}")
        print(f"    +seed_stable=4 IoU      : OCR={tB:<4} by_kind={kB} tags={tagB}")
        print(f"    +seed_stable=3 centerR30: OCR={t3:<4} by_kind={k3} tags={tag3}")

    # --- SECTION 4: UNKNOWN 2 — art-region broad_split spatial stability ---
    print("\n=== SECTION 4: broad_split center spread (art-region stability for deferral) ===")
    bs = defaultdict(list)
    R_BS = 40.0
    bs_clusters = []
    for fi, blocks in enumerate(frames_blocks):
        for b in blocks:
            if b.kind != "broad_split":
                continue
            cx, cy = center(b.bbox)
            best, bd = None, 1e9
            for cl in bs_clusters:
                d = ((cx - cl["cx"]) ** 2 + (cy - cl["cy"]) ** 2) ** 0.5
                if d < bd:
                    best, bd = cl, d
            if best is not None and bd < R_BS:
                best["pts"].append((fi, cx, cy, b.bbox))
                best["cx"] = sum(p[1] for p in best["pts"]) / len(best["pts"])
                best["cy"] = sum(p[2] for p in best["pts"]) / len(best["pts"])
            else:
                bs_clusters.append({"pts": [(fi, cx, cy, b.bbox)], "cx": cx, "cy": cy})
    bs_clusters.sort(key=lambda cl: -len(cl["pts"]))
    print(f"  {len(bs_clusters)} broad_split clusters (radius {R_BS:.0f}px):")
    for cl in bs_clusters:
        pts = cl["pts"]
        xs = max(p[1] for p in pts) - min(p[1] for p in pts)
        ys = max(p[2] for p in pts) - min(p[2] for p in pts)
        print(f"    #frames={len(pts):<3} center=({cl['cx']:.0f},{cl['cy']:.0f}) "
              f"x-spread={xs:.0f} y-spread={ys:.0f}")


if __name__ == "__main__":
    main()
