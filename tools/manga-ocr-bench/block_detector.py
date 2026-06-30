"""Experimental full-frame text-block candidate detector for preview only.

This is intentionally separate from columnizer.py. It uses the current
Columnizer as a cheap-CV confirmation step after proposing full-frame blocks.
The older sliding-window scorer remains available as a debug fallback.

It is not production realtime code yet:
  - it is slower than a real detector,
  - it can produce false positives on character/background line art,
  - selected OCR crops must still be raw crops from the original frame.
"""
from dataclasses import dataclass
import time

import cv2
import numpy as np

from columnizer import (
    _branch_mask,
    _columnize_vertical,
    _evaluate_mask,
    component_filter_global,
    component_filter,
    columnize,
    layout_gate,
    layout_gate_scored,
)


WINDOW_SIZES = [
    (140, 360),
    (180, 420),
    (240, 420),
    (340, 430),
    (440, 520),
    (600, 560),
    (620, 260),
    (900, 300),
]

MIN_CLUSTER_VOTE = 5
GROUP_KERNEL_VERTICAL = (15, 65)
GROUP_KERNEL_V_BRIDGE = (45, 11)
GROUP_KERNEL_HORIZONTAL = (65, 15)
PROPOSAL_PAD = 10
MIN_BLOCK_AREA = 1800
MAX_BLOCK_W = 720
MAX_BLOCK_H = 720

# Component-graph grouping (content-aware; replaces uniform dilation). Starting values from
# CONCLUSION-GROUPING-FINDING.md §5 — these are the calibration knobs, tune on real frames.
GRAPH_SIZE_RATIO = 2.0       # |size_a/size_b| must stay within this band, else refuse edge (text<->art)
GRAPH_ALIGN_FRAC = 0.8       # cross-axis center delta < this * avg cross-axis extent = same column/row
GRAPH_STACK_GAP = 2.5        # along-axis edge gap < this * avg along-axis extent = stacked/inline glyphs
GRAPH_ADJ_OVERLAP = 0.50     # along-axis band overlap fraction to treat two comps as one block
GRAPH_ADJ_GAP = 1.2          # adjacent column/row cross-axis gap < this * avg cross-axis extent
GRAPH_BLOCK_PAD = 18         # graph boxes hug the comps; extra pad lowers occ at the confirm gate


@dataclass
class BlockCandidate:
    bbox: tuple
    score: float
    rank: float
    vote: int
    layout: str
    columns: int
    occ: float
    tl: float
    n: int
    source: str
    line_frac: float = 0.0
    edge_frac: float = 0.0
    max_dom: float = 0.0
    columnizer_result: dict | None = None


def _inter(a, b):
    ax0, ay0, ax1, ay1 = a
    bx0, by0, bx1, by1 = b
    ix0 = max(ax0, bx0)
    iy0 = max(ay0, by0)
    ix1 = min(ax1, bx1)
    iy1 = min(ay1, by1)
    return max(0, ix1 - ix0) * max(0, iy1 - iy0)


def _iou(a, b):
    inter = _inter(a, b)
    if inter == 0:
        return 0.0
    aa = (a[2] - a[0]) * (a[3] - a[1])
    bb = (b[2] - b[0]) * (b[3] - b[1])
    return inter / float(aa + bb - inter)


def _contained_frac(a, b):
    aa = (a[2] - a[0]) * (a[3] - a[1])
    return _inter(a, b) / float(max(aa, 1))


def _overlap_1d(a0, a1, b0, b1):
    return max(0, min(a1, b1) - max(a0, b0))


def _center_delta(a, b):
    ac = ((a[0] + a[2]) / 2.0, (a[1] + a[3]) / 2.0)
    bc = ((b[0] + b[2]) / 2.0, (b[1] + b[3]) / 2.0)
    return abs(ac[0] - bc[0]), abs(ac[1] - bc[1])


def _similar(a, b):
    dx, dy = _center_delta(a, b)
    aw = a[2] - a[0]
    ah = a[3] - a[1]
    bw = b[2] - b[0]
    bh = b[3] - b[1]
    return (
        _iou(a, b) > 0.22
        or (dx < max(70, min(aw, bw) * 0.45) and dy < max(120, min(ah, bh) * 0.45))
        or _contained_frac(a, b) > 0.72
        or _contained_frac(b, a) > 0.88
    )


def _nearby_block(a, b):
    ax0, ay0, ax1, ay1 = a
    bx0, by0, bx1, by1 = b
    aw = ax1 - ax0
    ah = ay1 - ay0
    bw = bx1 - bx0
    bh = by1 - by0
    x_gap = max(0, max(ax0, bx0) - min(ax1, bx1))
    y_gap = max(0, max(ay0, by0) - min(ay1, by1))
    y_overlap = _overlap_1d(ay0, ay1, by0, by1) / float(max(1, min(ah, bh)))
    x_overlap = _overlap_1d(ax0, ax1, bx0, bx1) / float(max(1, min(aw, bw)))
    union = (min(ax0, bx0), min(ay0, by0), max(ax1, bx1), max(ay1, by1))
    uw = union[2] - union[0]
    uh = union[3] - union[1]
    if uw > MAX_BLOCK_W or uh > MAX_BLOCK_H:
        return False
    side_by_side = y_overlap > 0.55 and x_gap <= 120 and max(aw, bw) <= 260 and uw <= 360
    stacked = x_overlap > 0.45 and y_gap <= 90 and max(ah, bh) <= 500
    return side_by_side or stacked


def _columns_bbox(columns, offset_x, offset_y, frame_w, frame_h):
    x0 = min(c["bbox"][0] for c in columns) + offset_x
    y0 = min(c["bbox"][1] for c in columns) + offset_y
    x1 = max(c["bbox"][2] for c in columns) + offset_x
    y1 = max(c["bbox"][3] for c in columns) + offset_y
    pad = 12
    return (
        max(0, x0 - pad),
        max(0, y0 - pad),
        min(frame_w, x1 + pad),
        min(frame_h, y1 + pad),
    )


def _add_ms(stats, key, start):
    if stats is not None:
        stats[key] = stats.get(key, 0.0) + (time.perf_counter() - start) * 1000.0


def _new_stats(scorer):
    return {
        "scorer": scorer,
        "mask_ms": 0.0,
        "cc_ms": 0.0,
        "group_ms": 0.0,
        "window_ms": 0.0,
        "confirm_ms": 0.0,
        "raw_proposal_count": 0,
        "merged_proposal_count": 0,
        "proposal_count": 0,
        "confirmed_count": 0,
    }


def _frame_masks(frame, stats=None):
    ts = time.perf_counter()
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    masks = [
        ("blackhat_full", _branch_mask(gray, cv2.MORPH_BLACKHAT)),
        ("tophat_full", _branch_mask(gray, cv2.MORPH_TOPHAT)),
    ]
    _add_ms(stats, "mask_ms", ts)
    return masks


def full_frame_components(frame, stats=None):
    results = []
    for source, mask in _frame_masks(frame, stats):
        ts = time.perf_counter()
        comps = component_filter_global(mask, frame.shape)
        _add_ms(stats, "cc_ms", ts)
        results.append((source, comps, mask))
    return results


def _block_comp_count(comps, bbox):
    x0, y0, x1, y1 = bbox
    return sum(1 for c in comps if x0 <= c.cx <= x1 and y0 <= c.cy <= y1)


def _pad_bbox(x, y, w, h, frame_w, frame_h):
    return (
        max(0, int(x) - PROPOSAL_PAD),
        max(0, int(y) - PROPOSAL_PAD),
        min(frame_w, int(x + w) + PROPOSAL_PAD),
        min(frame_h, int(y + h) + PROPOSAL_PAD),
    )


def group_components_into_blocks(comps, frame_shape, mode="vertical", stats=None):
    ts = time.perf_counter()
    frame_h, frame_w = frame_shape[:2]
    if not comps:
        _add_ms(stats, "group_ms", ts)
        return []

    canvas = np.zeros((frame_h, frame_w), dtype=np.uint8)
    for c in comps:
        cv2.rectangle(canvas, (c.x, c.y), (c.x + c.w, c.y + c.h), 255, -1)

    if mode == "horizontal":
        grouped = cv2.dilate(canvas, cv2.getStructuringElement(cv2.MORPH_RECT, GROUP_KERNEL_HORIZONTAL))
    else:
        grouped = cv2.dilate(canvas, cv2.getStructuringElement(cv2.MORPH_RECT, GROUP_KERNEL_VERTICAL))
        grouped = cv2.dilate(grouped, cv2.getStructuringElement(cv2.MORPH_RECT, GROUP_KERNEL_V_BRIDGE))

    n, _lab, stats_cc, _cent = cv2.connectedComponentsWithStats(grouped, 8)
    blocks = []
    for i in range(1, n):
        x, y, bw, bh, area = stats_cc[i]
        if area < MIN_BLOCK_AREA:
            continue
        if bw < 24 or bh < 24 or bw > MAX_BLOCK_W or bh > MAX_BLOCK_H:
            continue
        bbox = _pad_bbox(x, y, bw, bh, frame_w, frame_h)
        comp_count = _block_comp_count(comps, bbox)
        if comp_count < 2:
            continue
        box_area = max(1, (bbox[2] - bbox[0]) * (bbox[3] - bbox[1]))
        score = 1.0 + 0.035 * min(comp_count, 24) - 0.00000008 * box_area
        blocks.append({"bbox": bbox, "score": score, "vote": comp_count})

    _add_ms(stats, "group_ms", ts)
    return blocks


def _comp_size(c):
    return 0.5 * (c.w + c.h)


def _size_similar(a, b):
    sa, sb = _comp_size(a), _comp_size(b)
    if sa <= 0 or sb <= 0:
        return False
    r = sa / sb
    return (1.0 / GRAPH_SIZE_RATIO) <= r <= GRAPH_SIZE_RATIO


def _graph_edge(a, b, mode):
    """Content-aware edge: bond two comps iff size-similar AND (same column/row OR adjacent
    column/row of one block). Size gate refuses text<->art; geometry gate bonds collinear text."""
    if not _size_similar(a, b):
        return False
    # vertical text stacks along Y inside a column (columns vary in X); horizontal runs along X.
    if mode == "horizontal":
        a_lo0, a_lo1, a_loc = a.x, a.x + a.w, a.cx        # along-axis = X
        a_cr0, a_cr1, a_crc = a.y, a.y + a.h, a.cy        # cross-axis = Y
        b_lo0, b_lo1, b_loc = b.x, b.x + b.w, b.cx
        b_cr0, b_cr1, b_crc = b.y, b.y + b.h, b.cy
        along_ext = 0.5 * (a.w + b.w)
        cross_ext = 0.5 * (a.h + b.h)
    else:
        a_lo0, a_lo1, a_loc = a.y, a.y + a.h, a.cy        # along-axis = Y
        a_cr0, a_cr1, a_crc = a.x, a.x + a.w, a.cx        # cross-axis = X
        b_lo0, b_lo1, b_loc = b.y, b.y + b.h, b.cy
        b_cr0, b_cr1, b_crc = b.x, b.x + b.w, b.cx
        along_ext = 0.5 * (a.h + b.h)
        cross_ext = 0.5 * (a.w + b.w)

    # same column/row: shared cross-axis center, small along-axis gap (stacked glyphs)
    cross_center_delta = abs(a_crc - b_crc)
    along_gap = max(0, max(a_lo0, b_lo0) - min(a_lo1, b_lo1))
    if (cross_center_delta < GRAPH_ALIGN_FRAC * cross_ext
            and along_gap < GRAPH_STACK_GAP * along_ext):
        return True

    # adjacent column/row of the same block: high along-axis overlap, small cross-axis gap
    along_overlap = _overlap_1d(a_lo0, a_lo1, b_lo0, b_lo1)
    along_min = max(1, min(a_lo1 - a_lo0, b_lo1 - b_lo0))
    cross_gap = max(0, max(a_cr0, b_cr0) - min(a_cr1, b_cr1))
    if (along_overlap / float(along_min) > GRAPH_ADJ_OVERLAP
            and cross_gap < GRAPH_ADJ_GAP * cross_ext):
        return True
    return False


def _graph_groups(comps, mode):
    """Union-find over component edges -> list of comp-groups (each = one block proposal)."""
    n = len(comps)
    parent = list(range(n))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    # ponytail: O(N^2) pair scan. N is the filtered frame-scale comp count (tens-low hundreds);
    # add a grid spatial index only if a frame's comp count makes this measurably slow.
    for i in range(n):
        for j in range(i + 1, n):
            if _graph_edge(comps[i], comps[j], mode):
                ri, rj = find(i), find(j)
                if ri != rj:
                    parent[ri] = rj

    groups = {}
    for i in range(n):
        groups.setdefault(find(i), []).append(comps[i])
    return list(groups.values())


def group_components_into_blocks_graph(comps, frame_shape, mode="vertical", stats=None):
    """Content-aware grouping: connect components by size-similarity + alignment/proximity, then
    take graph connected-components as block proposals. A vote-2 column that is collinear with a
    neighbour stays its own group (never dropped); art blobs are size-dissimilar so they never
    bond to glyphs. See CONCLUSION-GROUPING-FINDING.md §4-5."""
    ts = time.perf_counter()
    frame_h, frame_w = frame_shape[:2]
    if not comps:
        _add_ms(stats, "group_ms", ts)
        return []

    blocks = []
    for group in _graph_groups(comps, mode):
        if len(group) < 2:
            continue
        x0 = min(c.x for c in group)
        y0 = min(c.y for c in group)
        x1 = max(c.x + c.w for c in group)
        y1 = max(c.y + c.h for c in group)
        bbox = (
            max(0, x0 - GRAPH_BLOCK_PAD),
            max(0, y0 - GRAPH_BLOCK_PAD),
            min(frame_w, x1 + GRAPH_BLOCK_PAD),
            min(frame_h, y1 + GRAPH_BLOCK_PAD),
        )
        bw = bbox[2] - bbox[0]
        bh = bbox[3] - bbox[1]
        if bw < 24 or bh < 24 or bw > MAX_BLOCK_W or bh > MAX_BLOCK_H:
            continue
        box_area = max(1, bw * bh)
        if box_area < MIN_BLOCK_AREA:
            continue
        comp_count = len(group)
        score = 1.0 + 0.035 * min(comp_count, 24) - 0.00000008 * box_area
        blocks.append({"bbox": bbox, "score": score, "vote": comp_count})

    _add_ms(stats, "group_ms", ts)
    return blocks


def _merge_proposal(items):
    merged = []
    for item in sorted(items, key=lambda p: p["score"], reverse=True):
        found = None
        for existing in merged:
            if (
                _iou(item["bbox"], existing["bbox"]) > 0.50
                or _contained_frac(item["bbox"], existing["bbox"]) > 0.82
                or _contained_frac(existing["bbox"], item["bbox"]) > 0.82
                or _nearby_block(item["bbox"], existing["bbox"])
            ):
                found = existing
                break
        if found is None:
            merged.append(dict(item))
            continue
        found["vote"] += item["vote"]
        found["sources"].append(item["source"])
        found["bbox"] = (
            min(found["bbox"][0], item["bbox"][0]),
            min(found["bbox"][1], item["bbox"][1]),
            max(found["bbox"][2], item["bbox"][2]),
            max(found["bbox"][3], item["bbox"][3]),
        )
        found["score"] = max(found["score"], item["score"]) + 0.01
    return merged


def propose_blocks_from_frame_masks(frame, mode="vertical", stats=None, group="graph"):
    grouper = (
        group_components_into_blocks_graph if group == "graph"
        else group_components_into_blocks
    )
    proposals = []
    for source, comps, _mask in full_frame_components(frame, stats):
        for block in grouper(comps, frame.shape, mode=mode, stats=stats):
            proposals.append({
                **block,
                "source": source,
                "sources": [source],
                "layout": f"{mode}_candidate",
            })

    merged = _merge_proposal(proposals)
    ranked = []
    for proposal in merged:
        ranked.append(BlockCandidate(
            bbox=proposal["bbox"],
            score=proposal["score"],
            rank=round(proposal["score"], 4),
            vote=proposal["vote"],
            layout=proposal["layout"],
            columns=0,
            occ=0.0,
            tl=0.0,
            n=proposal["vote"],
            source="+".join(dict.fromkeys(proposal["sources"])),
        ))
    ranked.sort(key=lambda c: c.rank, reverse=True)
    if stats is not None:
        stats["raw_proposal_count"] = len(proposals)
        stats["merged_proposal_count"] = len(merged)
        stats["proposal_count"] = len(ranked)
    return ranked


def _candidate_from_mask_crop(masks, x, y, w, h, frame_w, frame_h):
    best = None
    for source, mask in masks:
        crop = mask[y:y + h, x:x + w]
        ok, comps, _score, dbg = _evaluate_mask(crop, (h, w, 3), source)
        if not ok:
            continue

        layout = layout_gate(comps, (h, w, 3))
        if layout == "vertical":
            columns, conf = _columnize_vertical(comps, w, h)
            layout_name = "vertical_rl"
            if not (1 <= len(columns) <= 4 and dbg["occ"] <= 0.40 and dbg["tl"] >= 0.75 and dbg["n"] >= 3):
                continue
        elif layout == "horizontal":
            columns = [{"order": 0, "bbox": [0, 0, w, h]}]
            conf = 1.0
            layout_name = "horizontal_ltr"
            if not (dbg["occ"] <= 0.38 and dbg["tl"] >= 0.75 and dbg["n"] >= 3):
                continue
        else:
            continue

        box = _columns_bbox(columns, x, y, frame_w, frame_h)
        bw = box[2] - box[0]
        bh = box[3] - box[1]
        if bw * bh < 1800 or bw > 620 or bh > 620:
            continue

        score = dbg["tl"] + 0.08 * min(len(columns), 3) + 0.03 * conf - 0.00000025 * (bw * bh)
        candidate = {
            "bbox": box,
            "score": score,
            "layout": layout_name,
            "columns": len(columns),
            "occ": dbg["occ"],
            "tl": dbg["tl"],
            "n": dbg["n"],
            "source": source,
        }
        if best is None or candidate["score"] > best["score"]:
            best = candidate
    return best


def _candidate_from_window_exact(frame, x, y, w, h):
    result = columnize(frame[y:y + h, x:x + w])
    if result["status"] == "reject":
        return None

    occ = result["mask_dbg"].get("occ", 1.0)
    tl = result["mask_dbg"].get("tl", 0.0)
    n = result["mask_dbg"].get("n", 0)
    cols = len(result["columns"])

    if result["layout"] == "vertical_rl":
        if not (1 <= cols <= 4 and occ <= 0.40 and tl >= 0.75 and n >= 3):
            return None
    elif result["layout"] == "horizontal_ltr":
        if not (occ <= 0.38 and tl >= 0.75 and n >= 3):
            return None
    else:
        return None

    frame_h, frame_w = frame.shape[:2]
    box = _columns_bbox(result["columns"], x, y, frame_w, frame_h)
    bw = box[2] - box[0]
    bh = box[3] - box[1]
    if bw * bh < 1800 or bw > 620 or bh > 620:
        return None

    score = tl + 0.08 * min(cols, 3) + 0.03 * result["split_confidence"] - 0.00000025 * (bw * bh)
    return {
        "bbox": box,
        "score": score,
        "layout": result["layout"],
        "columns": cols,
        "occ": occ,
        "tl": tl,
        "n": n,
        "source": result["mask_dbg"].get("source", "?"),
    }


def _line_noise_features(mask, roi_shape, comps=None):
    if mask is None and comps is None:
        return {"line_frac": 0.0, "edge_frac": 0.0, "edge_long_frac": 0.0, "max_dom": 0.0}

    h, w = roi_shape[:2]
    if comps is None:
        comps = component_filter(mask, roi_shape)
    total = sum(c.area for c in comps) or 1
    line_area = 0
    edge_area = 0
    edge_long_area = 0
    max_dom = 0.0

    for c in comps:
        ar = max(c.w / float(c.h), c.h / float(c.w))
        longish = (c.h >= 0.42 * h or c.w >= 0.42 * w) and ar >= 2.8
        touches = c.x <= 2 or c.y <= 2 or c.x + c.w >= w - 2 or c.y + c.h >= h - 2
        if longish:
            line_area += c.area
        if touches:
            edge_area += c.area
        if touches and longish:
            edge_long_area += c.area
        max_dom = max(max_dom, c.area / float(total))

    return {
        "line_frac": round(line_area / float(total), 4),
        "edge_frac": round(edge_area / float(total), 4),
        "edge_long_frac": round(edge_long_area / float(total), 4),
        "max_dom": round(max_dom, 4),
    }


def _is_line_dominated(features, occ):
    line_frac = features["line_frac"]
    edge_long_frac = features["edge_long_frac"]
    max_dom = features["max_dom"]
    return (
        (line_frac >= 0.45 and max_dom >= 0.45)
        or (line_frac >= 0.35 and occ >= 0.40)
        or (edge_long_frac >= 0.25 and line_frac >= 0.30)
    )


def _confirm_candidate_on_raw(frame, candidate, require_vertical, stats=None):
    def reject(reason):
        if stats is not None:
            stats[f"reject_{reason}"] = stats.get(f"reject_{reason}", 0) + 1
        return None

    x0, y0, x1, y1 = candidate.bbox
    frame_h, frame_w = frame.shape[:2]
    if x1 >= frame_w - 2 and x0 > frame_w * 0.85:
        return reject("right_edge")

    roi = frame[y0:y1, x0:x1]
    result = columnize(roi)
    if result["status"] == "reject":
        return reject("status")
    if require_vertical and result["layout"] != "vertical_rl":
        return reject("require_vertical")
    if result["layout"] == "unknown":
        return reject("unknown")

    cols = len(result["columns"])
    dbg = result["mask_dbg"]
    occ = float(dbg.get("occ", 1.0))
    tl = float(dbg.get("tl", 0.0))
    n = int(dbg.get("n", 0))
    if not (1 <= cols <= 4 and occ <= 0.45 and tl >= 0.50 and n >= 2):
        return reject("weak_mask")

    max_col_w = max((col["bbox"][2] - col["bbox"][0]) for col in result["columns"])
    if max_col_w > 120 and tl < 0.85:
        return reject("wide_col_low_tl")

    comps = result.get("components")
    _, _hs, _vs, margin = layout_gate_scored(comps, roi.shape) if comps else ("", 0.0, 0.0, 0.0)
    features = _line_noise_features(result.get("mask"), roi.shape, comps=comps)
    if _is_line_dominated(features, occ):
        return reject("line_dominated")

    refined_bbox = _columns_bbox(result["columns"], x0, y0, frame_w, frame_h)
    bw = refined_bbox[2] - refined_bbox[0]
    bh = refined_bbox[3] - refined_bbox[1]
    if bw * bh < 1800 or bw > 620 or bh > 620:
        return reject("size")

    adjusted_rank = (
        candidate.rank
        + 0.08
        + 0.05 * margin
        - 0.18 * features["line_frac"]
        - 0.10 * max(0.0, occ - 0.35)
    )
    return BlockCandidate(
        bbox=refined_bbox,
        score=candidate.score,
        rank=round(adjusted_rank, 4),
        vote=candidate.vote,
        layout=result["layout"],
        columns=cols,
        occ=round(occ, 4),
        tl=round(tl, 4),
        n=n,
        source=f"{candidate.source}+raw_confirm",
        line_frac=features["line_frac"],
        edge_frac=features["edge_frac"],
        max_dom=features["max_dom"],
        columnizer_result=result,
    )


def detect_text_blocks(
    frame,
    max_blocks=18,
    stride=80,
    scorer="cc",
    mode="vertical",
    confirm_raw=True,
    require_vertical=True,
    min_vote=MIN_CLUSTER_VOTE,
    group="graph",
    return_stats=False,
):
    stats = _new_stats(scorer)
    if scorer == "cc":
        ranked = propose_blocks_from_frame_masks(frame, mode=mode, stats=stats, group=group)
        kept = []
        for candidate in ranked:
            if confirm_raw:
                ts = time.perf_counter()
                candidate = _confirm_candidate_on_raw(frame, candidate, require_vertical, stats)
                _add_ms(stats, "confirm_ms", ts)
                if candidate is None:
                    continue

            box = candidate.bbox
            if any(
                _iou(box, kept_candidate.bbox) > 0.18
                or _contained_frac(box, kept_candidate.bbox) > 0.70
                or _contained_frac(kept_candidate.bbox, box) > 0.88
                for kept_candidate in kept
            ):
                continue
            kept.append(candidate)
            if len(kept) >= max_blocks:
                break
        stats["confirmed_count"] = len(kept)
        return (kept, stats) if return_stats else kept

    if scorer == "window":
        scorer = "fast"

    candidates = []
    frame_h, frame_w = frame.shape[:2]
    masks = _frame_masks(frame, stats) if scorer == "fast" else None
    ts_window = time.perf_counter()
    for win_w, win_h in WINDOW_SIZES:
        step_x = max(60, min(stride, win_w // 2))
        step_y = max(60, min(stride, win_h // 3))
        for y in range(0, frame_h - win_h + 1, step_y):
            for x in range(0, frame_w - win_w + 1, step_x):
                if scorer == "fast":
                    candidate = _candidate_from_mask_crop(masks, x, y, win_w, win_h, frame_w, frame_h)
                elif scorer == "exact":
                    candidate = _candidate_from_window_exact(frame, x, y, win_w, win_h)
                else:
                    raise ValueError(f"unknown scorer: {scorer}")
                if candidate is not None:
                    candidates.append(candidate)
    _add_ms(stats, "window_ms", ts_window)

    clusters = []
    for candidate in sorted(candidates, key=lambda c: c["score"], reverse=True):
        found = None
        for cluster in clusters:
            if _similar(candidate["bbox"], cluster["best"]["bbox"]):
                found = cluster
                break
        if found is None:
            clusters.append({"best": candidate, "items": [candidate], "score_sum": candidate["score"]})
            continue
        found["items"].append(candidate)
        found["score_sum"] += candidate["score"]
        if candidate["score"] > found["best"]["score"]:
            found["best"] = candidate

    ranked = []
    for cluster in clusters:
        vote = len(cluster["items"])
        if vote < min_vote:
            continue
        best = cluster["best"]
        avg = cluster["score_sum"] / vote
        bbox = best["bbox"]
        area = (bbox[2] - bbox[0]) * (bbox[3] - bbox[1])
        rank = avg + 0.04 * min(vote, 12) - 0.0000001 * area
        ranked.append(BlockCandidate(
            bbox=bbox,
            score=best["score"],
            rank=rank,
            vote=vote,
            layout=best["layout"],
            columns=best["columns"],
            occ=best["occ"],
            tl=best["tl"],
            n=best["n"],
            source=best["source"],
        ))

    ranked.sort(key=lambda c: c.rank, reverse=True)
    stats["proposal_count"] = len(ranked)
    kept = []
    for candidate in ranked:
        if confirm_raw:
            ts = time.perf_counter()
            candidate = _confirm_candidate_on_raw(frame, candidate, require_vertical, stats)
            _add_ms(stats, "confirm_ms", ts)
            if candidate is None:
                continue

        box = candidate.bbox
        if any(
            _iou(box, kept_candidate.bbox) > 0.18
            or _contained_frac(box, kept_candidate.bbox) > 0.70
            or _contained_frac(kept_candidate.bbox, box) > 0.88
            for kept_candidate in kept
        ):
            continue
        kept.append(candidate)
        if len(kept) >= max_blocks:
            break
    stats["confirmed_count"] = len(kept)
    return (kept, stats) if return_stats else kept
