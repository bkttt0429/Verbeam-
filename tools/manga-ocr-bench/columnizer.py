"""Cheap-CV Columnizer for vertical CJK: text-region ROI -> ordered single-column crops.

Pipeline (handoff §4 step 1+2):
  polarity from luminance  (Otsu majority class = background -> build ONE black-hat or top-hat mask)
  -> component filter       (drop noise / background blobs / border lines / tall-thin streaks)
  -> occupancy reject       (ink spread across the ROI = rain/texture -> no_text)
  -> layout gate            (no_text / horizontal / vertical / unknown) BEFORE splitting
  -> per-column x-cluster + right-to-left order   (only on the vertical branch)

columnize() returns a structured dict (handoff §4 schema). The manga-ocr validation harness only
runs under __main__, so robustness.py / colz_debug.py can import the CV functions (single source)."""
import sys, statistics
from collections import namedtuple
import cv2
import numpy as np

Comp = namedtuple("Comp", "cx cy x y w h area fill")

_K15 = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (15, 15))
_K5 = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5))
_K3 = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))

# ponytail: starting thresholds, tuned by eye via probe/colz_debug. occ margin is tight
# (real text <=0.44 vs rain 0.53 on the bench) — widen OCC_MAX if real text false-rejects.
OCC_MAX = 0.50           # ink in >50% of an 8x8 grid = spread (rain/texture), not concentrated text
MIN_ALIGN = 0.25         # below this row/col alignment = structureless mask -> no_text
GATE_MARGIN = 1.35       # horizontal/vertical must beat the other axis by this ratio, else "unknown"
COLOR_OCC_MAX = 0.45     # lab-delta fallback is stricter; colored panel edges are easy to over-capture
COLOR_MIN_SCORE_GAIN = 0.10
COLOR_MIN_COMPS = 2

# GAP-A: drop tall-thin vertical scratches/rain, keep wide-thin horizontal CJK strokes.
TALL_STREAK_AR = 8.0          # h/w >= this = candidate streak
TALL_STREAK_MIN_H_FRAC = 0.18 # AND height >= 18% of ROI height (only long streaks)
TALL_STREAK_MAX_W_PX = 10     # AND width is narrow ...
TALL_STREAK_MAX_W_FRAC = 0.06 # ... <= max(10px, 6% ROI width)


# ---------- masks ----------
def _branch_mask(gray, op):
    """op = MORPH_BLACKHAT (dark text on light) or MORPH_TOPHAT (light text on dark)."""
    m = cv2.morphologyEx(gray, op, _K15)
    _, m = cv2.threshold(m, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, _K5)
    m = cv2.morphologyEx(m, cv2.MORPH_OPEN, _K3)
    return m


def _odd_kernel_for_roi(h, w, frac=0.06, min_k=21, max_k=51):
    k = int(min(h, w) * frac)
    k = max(min_k, min(max_k, k))
    return k + 1 if k % 2 == 0 else k


def _lab_delta_mask(bgr):
    """Local Lab color-delta mask for text that disappears after grayscale conversion."""
    h, w = bgr.shape[:2]
    lab_u8 = cv2.cvtColor(bgr, cv2.COLOR_BGR2LAB)
    lab = lab_u8.astype(np.float32)

    k = _odd_kernel_for_roi(h, w)
    bg = cv2.medianBlur(lab_u8, k).astype(np.float32)

    d_l = lab[:, :, 0] - bg[:, :, 0]
    d_a = lab[:, :, 1] - bg[:, :, 1]
    d_b = lab[:, :, 2] - bg[:, :, 2]
    delta = np.sqrt((0.35 * d_l * d_l) + (d_a * d_a) + (d_b * d_b))
    delta = np.clip(delta, 0, 255).astype(np.uint8)

    _, mask = cv2.threshold(delta, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, _K5)
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, _K3)
    return mask


def _is_tall_streak(cw, ch, roi_w, roi_h):
    """Drop vertical rain/scratch strokes, but keep wide horizontal CJK strokes."""
    if cw <= 0:
        return True
    tall_ar = ch / float(cw)
    return (
        tall_ar >= TALL_STREAK_AR
        and ch >= max(60, TALL_STREAK_MIN_H_FRAC * roi_h)
        and cw <= max(TALL_STREAK_MAX_W_PX, TALL_STREAK_MAX_W_FRAC * roi_w)
    )


def component_filter(mask, roi_shape, dbg=None):
    """CC on a binary mask, drop noise / background / rain. Returns kept Comp list."""
    h, w = roi_shape[:2]
    long_side = max(h, w)
    if dbg is not None:
        dbg.setdefault("drop_tall", 0)
    n, _lab, stats, cent = cv2.connectedComponentsWithStats(mask, 8)
    comps = []
    for i in range(1, n):
        x, y, cw, ch, area = stats[i]
        if area < 80:                       continue   # speck
        if ch > 0.9 * h or cw > 0.55 * w:   continue   # background / cross-column blob
        if cw < 6 or ch < 6:                continue   # too small
        fill = area / float(cw * ch)
        if fill < 0.12:                     continue   # thin line (hair / outline)
        # GAP-A asymmetric aspect filter: drop tall-thin streaks, keep wide-thin strokes.
        if _is_tall_streak(cw, ch, w, h):
            if dbg is not None:
                dbg["drop_tall"] += 1
            continue
        touches = (x <= 1 or y <= 1 or x + cw >= w - 1 or y + ch >= h - 1)
        if touches and max(cw, ch) > 0.45 * long_side:
            continue                                    # long border line
        comps.append(Comp(float(cent[i][0]), float(cent[i][1]),
                          int(x), int(y), int(cw), int(ch), int(area), float(fill)))
    return comps


def component_filter_global(mask, frame_shape, min_area=80, min_fill=0.12):
    """Frame-scale CC filter for block proposal only.

    This intentionally avoids ROI-relative border/touch rejects. Local block
    validation still happens through columnize() after a proposal is cropped.
    """
    h, w = frame_shape[:2]
    n, _lab, stats, cent = cv2.connectedComponentsWithStats(mask, 8)
    comps = []
    for i in range(1, n):
        x, y, cw, ch, area = stats[i]
        if area < min_area:
            continue
        if cw < 4 or ch < 4:
            continue
        if cw > 0.85 * w or ch > 0.85 * h:
            continue
        fill = area / float(cw * ch)
        if fill < min_fill:
            continue
        if _is_tall_streak(cw, ch, w, h):
            continue
        comps.append(Comp(float(cent[i][0]), float(cent[i][1]),
                          int(x), int(y), int(cw), int(ch), int(area), float(fill)))
    return comps


# ---------- clustering helpers ----------
def _cluster_vals(vals, eps):
    vs = sorted(vals)
    groups = [[vs[0]]]
    for v in vs[1:]:
        if v - groups[-1][-1] <= eps: groups[-1].append(v)
        else: groups.append([v])
    return groups


def _cluster_objs(comps, key, eps):
    cs = sorted(comps, key=key)
    groups = [[cs[0]]]
    for c in cs[1:]:
        if key(c) - key(groups[-1][-1]) <= eps: groups[-1].append(c)
        else: groups.append([c])
    return groups


# ---------- text-likeness (NOT raw pixel count, or rain wins) ----------
def _alignment_score(coords, scale):
    """Fraction of comps whose 1D centers line up into a row/column (cluster of >=2)."""
    if len(coords) < 2:
        return 0.0
    eps = max(scale * 0.6, 6)
    clusters = _cluster_vals(coords, eps)
    aligned = sum(len(cl) for cl in clusters if len(cl) >= 2)
    return aligned / len(coords)


def text_likeness(comps, roi_shape):
    """0..1, higher = more text-like: components line up into a row OR column and there are a few
    of them. (Rain also aligns, so rain is rejected by occupancy in make_text_mask_dual, not here.)"""
    if len(comps) < 2:
        return 0.0
    mw = statistics.median([c.w for c in comps]); mh = statistics.median([c.h for c in comps])
    align = max(_alignment_score([c.cy for c in comps], mh),   # rows
                _alignment_score([c.cx for c in comps], mw))   # cols
    count_score = min(1.0, len(comps) / 4.0)
    return round(align * count_score, 4)


def _otsu_polarity(gray):
    """Background = Otsu majority class. Light background -> dark text, and vice versa."""
    ret, _ = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    dark = int((gray <= ret).sum()); light = int((gray > ret).sum())
    return "dark_on_light" if light >= dark else "light_on_dark"


def _occupancy(mask, grid=8):
    """Fraction of an 8x8 grid that holds ink. Concentrated text is low; sprinkled rain is high."""
    small = cv2.resize((mask > 0).astype(np.uint8) * 255, (grid, grid), interpolation=cv2.INTER_AREA)
    return float((small > int(255 * 0.02)).mean())


def _evaluate_mask(mask, roi_shape, source, occ_max=OCC_MAX, min_comps=2):
    filter_dbg = {}
    comps = component_filter(mask, roi_shape, filter_dbg)
    occ = _occupancy(mask)
    score = text_likeness(comps, roi_shape)
    dbg = {"source": source, "occ": round(occ, 2), "tl": score, **filter_dbg, "n": len(comps)}
    ok = len(comps) >= min_comps and occ <= occ_max and score >= MIN_ALIGN
    return ok, comps, score, dbg


def make_text_mask_dual(gray, roi_shape=None, bgr=None):
    """Polarity from luminance (Otsu majority = background), then build that one mask.
    Returns (mask, comps, polarity, score, dbg). polarity in dark_on_light / light_on_dark / no_text.
    Rejects spread (rain/texture) via occupancy and structureless masks via low alignment.
    If grayscale fails, tries a conservative Lab local-color-contrast fallback."""
    shape = roi_shape if roi_shape is not None else gray.shape
    pol = _otsu_polarity(gray)
    op = cv2.MORPH_BLACKHAT if pol == "dark_on_light" else cv2.MORPH_TOPHAT
    gray_mask = _branch_mask(gray, op)
    ok, comps, score, dbg = _evaluate_mask(gray_mask, shape, source="gray_otsu")
    if ok:
        return gray_mask, comps, pol, score, dbg

    gray_reject_dbg = dbg
    if bgr is not None:
        color_mask = _lab_delta_mask(bgr)
        cok, ccomps, cscore, cdbg = _evaluate_mask(
            color_mask, shape, source="lab_delta", occ_max=COLOR_OCC_MAX, min_comps=COLOR_MIN_COMPS)
        gray_score_floor = score if len(comps) >= 2 and dbg["occ"] <= OCC_MAX else 0.0
        if cok and cscore >= max(MIN_ALIGN, gray_score_floor + COLOR_MIN_SCORE_GAIN):
            cdbg["gray_reject"] = gray_reject_dbg
            return color_mask, ccomps, "color_contrast", cscore, cdbg
        gray_reject_dbg["color_tried"] = True
        gray_reject_dbg["color_dbg"] = cdbg

    return None, [], "no_text", score, gray_reject_dbg


# ---------- layout gate (before splitting) ----------
def _axis_score(comps, axis):
    """axis='h': group by cy into rows, reward wide-flat rows.
       axis='v': group by cx into cols, reward tall-narrow cols.
    Returns (score, group_count)."""
    mw = statistics.median([c.w for c in comps]); mh = statistics.median([c.h for c in comps])
    if axis == "h":
        groups = _cluster_objs(comps, lambda c: c.cy, max(mh * 0.6, 6))
        span = lambda g: max(c.x + c.w for c in g) - min(c.x for c in g)
        spread = lambda g: max(c.cy for c in g) - min(c.cy for c in g)
    else:
        groups = _cluster_objs(comps, lambda c: c.cx, max(mw * 0.6, 6))
        span = lambda g: max(c.y + c.h for c in g) - min(c.y for c in g)
        spread = lambda g: max(c.cx for c in g) - min(c.cx for c in g)
    total = sum((span(g) / (spread(g) + 1)) * len(g) for g in groups if len(g) >= 2)
    return total, len(groups)


def layout_gate(comps, roi_shape):
    """no_text / horizontal / vertical / unknown. (multi_block reject is step 5, out of scope —
    this round never emits it; ambiguous blocks fall to 'unknown')."""
    if len(comps) < 2:
        return "no_text"
    hs, _ = _axis_score(comps, "h")
    vs, _ = _axis_score(comps, "v")
    if hs > vs * GATE_MARGIN:
        return "horizontal"
    if vs > hs * GATE_MARGIN:
        return "vertical"
    return "unknown"


def layout_gate_scored(comps, roi_shape):
    """Return layout plus raw horizontal/vertical scores for detector ranking."""
    if len(comps) < 2:
        return "no_text", 0.0, 0.0, 0.0
    hs, _ = _axis_score(comps, "h")
    vs, _ = _axis_score(comps, "v")
    denom = max(hs, vs, 1e-6)
    margin = abs(vs - hs) / denom
    if hs > vs * GATE_MARGIN:
        layout = "horizontal"
    elif vs > hs * GATE_MARGIN:
        layout = "vertical"
    else:
        layout = "unknown"
    return layout, hs, vs, margin


# ---------- vertical column split (x-center clustering; right-to-left order) ----------
# NOTE: a projection-valley split was tried for "merged adjacent columns" but reverted —
# non-overlapping columns always have a center-x gap >= glyph_w > eps, so cx-clustering already
# separates them (the supposed 127s "2-col merge" was actually a single column). Re-add a valley
# refinement only if a real chaining-merge frame turns up. See handoff §3.
def _columnize_vertical(comps, w, h):
    glyph_w = statistics.median([c.w for c in comps])
    eps = max(glyph_w * 0.8, 8)
    clusters = _cluster_objs(comps, lambda c: c.cx, eps)
    if len(clusters) <= 1:
        return [{"order": 0, "bbox": [0, 0, w, h]}], 0.8
    boxes = []
    for cl in clusters:
        x0 = max(0, min(c.x for c in cl) - 8); x1 = min(w, max(c.x + c.w for c in cl) + 8)
        y0 = max(0, min(c.y for c in cl) - 8); y1 = min(h, max(c.y + c.h for c in cl) + 8)
        boxes.append((statistics.mean(c.cx for c in cl), x0, y0, x1, y1, len(cl)))
    boxes.sort(key=lambda b: b[0], reverse=True)              # vertical_rl: right column first
    centers = sorted(b[0] for b in boxes)
    gaps = [centers[i + 1] - centers[i] for i in range(len(centers) - 1)]
    gap_score = min(1.0, min(gaps) / (glyph_w * 1.5)) if gaps else 0.5
    count_score = min(1.0, min(b[5] for b in boxes) / 2.0)
    conf = round(gap_score * count_score, 2)
    cols = [{"order": i, "bbox": [b[1], b[2], b[3], b[4]]} for i, b in enumerate(boxes)]
    return cols, conf


def columnize(roi):
    """ROI (BGR) -> structured result dict (handoff §4 schema)."""
    h, w = roi.shape[:2]
    gray = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
    mask, comps, polarity, mask_q, dbg = make_text_mask_dual(gray, roi.shape, bgr=roi)

    def result(layout, status, columns, conf, reason):
        return {"polarity": polarity, "layout": layout, "status": status, "columns": columns,
                "split_confidence": conf, "mask_quality": round(mask_q, 4),
                "mask_dbg": dbg, "reject_reason": reason, "mask": mask, "components": comps}

    if polarity == "no_text" or not comps:
        return result("no_text", "reject", [], 0.0, "no_text")
    layout = layout_gate(comps, roi.shape)
    if layout == "horizontal":
        return result("horizontal_ltr", "bypass_columnizer",
                      [{"order": 0, "bbox": [0, 0, w, h]}], 1.0, None)
    # vertical, or unknown -> attempt vertical (flagged via layout field)
    cols, conf = _columnize_vertical(comps, w, h)
    return result("vertical_rl" if layout == "vertical" else "unknown", "ok", cols, conf, None)


# ---------- validation harness (only runs when executed directly) ----------
if __name__ == "__main__":
    import argparse
    import time
    from pathlib import Path
    from PIL import Image
    from manga_ocr import MangaOcr
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--src",
        default=str(Path(__file__).resolve().parent / "inputs" / "source_1080p.mp4"),
        help="source video path",
    )
    args = parser.parse_args()
    ocr = MangaOcr(force_cpu=True)
    cap = cv2.VideoCapture(args.src); fps = cap.get(cv2.CAP_PROP_FPS) or 24.0

    def grab(t):
        cap.set(cv2.CAP_PROP_POS_FRAMES, int(t * fps)); _ok, fr = cap.read(); return fr

    def read(crop):
        return ocr(Image.fromarray(cv2.cvtColor(crop, cv2.COLOR_BGR2RGB)))

    CASES = [(6.0, (1330, 120, 1760, 820)), (6.5, (1330, 120, 1760, 820)), (2.0, (1330, 120, 1760, 820))]
    for t, (x0, y0, x1, y1) in CASES:
        roi = grab(t)[y0:y1, x0:x1]
        res = columnize(roi)
        print(f"\n===== t={t}  ROI {roi.shape[1]}x{roi.shape[0]} =====")
        print(f"  polarity={res['polarity']} ({res['mask_dbg']})  layout={res['layout']}  "
              f"status={res['status']}  conf={res['split_confidence']}  reason={res['reject_reason']}")
        if res["status"] == "reject":
            continue
        parts = []
        for col in res["columns"]:
            cx0, cy0, cx1, cy1 = col["bbox"]
            ts = time.time(); txt = read(roi[cy0:cy1, cx0:cx1]); dt = (time.time() - ts) * 1000
            parts.append(txt)
            print(f"     col{col['order']} x[{cx0}:{cx1}] -> '{txt}'  {dt:.0f}ms")
        print(f"  [joined R->L] -> '{'　'.join(parts)}'")
    cap.release()
