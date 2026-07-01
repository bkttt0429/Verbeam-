# Why columns lose characters — two dropped glyphs, two different root causes

Date: 2026-07-01. Frame: 48.0s reference. Debug tools: `debug_san.py`, `debug_te.py` (render + edge/mask
trace, reusable for any "char not boxed" case). Both cases below are on the pink-diamond captions.

## TL;DR

A column can lose a character for **two structurally different reasons**, and they need different fixes:

| case | glyph | is it detected as a component? | root cause | fix cost |
|---|---|---|---|---|
| **散** (top of 散々ワガママ) | big kanji | **YES** (clean comp, area 830) | **grouping** drops it (size-ratio gate) | cheap, precedented |
| **て** (bottom of 語っといて) | faint kana | **NO** (no comp in any mask) | **detection** (mask contrast too low) | hard, faint-text family |

The debug method that separates them: dump the comps in the column band. If the glyph **has a component**
→ grouping/NMS bug. If it **has no component** → look at the raw mask response → detection/contrast bug.

---

## Case 1 — 散: detected, then dropped by the block_merged size gate

`散` is a clean single component: `cx=961 cy=512 w=30 h=39 area=830`, x-aligned with the column
(々ワガママ at x≈962). It is **not** a mask problem. It is dropped in grouping:

```
[block_merged] size ratio 散/々 = 2.30   (GRAPH_SIZE_RATIO = 2.0)
               -> _size_similar = False  -> _graph_edge = False      # 散 refused into the block
[column_seed]  _vertical_seed_edge(散,々) = True  (no size gate)
               -> 散 IS in a seed: bbox=(937,484,987,598)
```

- `block_merged`'s graph grouping (`_graph_edge`) has a **size-ratio gate `GRAPH_SIZE_RATIO=2.0`**. A big
  kanji next to small kana exceeds it (34.5 / 15.0 = **2.30 > 2.0**), so 散 never bonds → the block =
  `々ワガママ`, structurally missing its leading kanji.
- This is the **exact bug UPDATE 4 already fixed for the column_seed path** (the `語→っ`, max-dim
  29/12 = 2.42 blocker). The size gate was removed from `_vertical_seed_edge` — but the **`_graph_edge`
  same-column branch still has it.**
- The column_seed path *does* include 散 (seed formed), but the truncated `block_merged`
  parent (`929,546,1004,880` = 々ワガママ) wins parent/child NMS and represents the column, so the fuller
  seed loses. Net: **the version missing 散 wins.**

**Fix — SHIPPED (NMS supersede), not the grouping route.** The full 散々ワガママ column_seed (with 散)
*already exists at the proposal stage*; it is only suppressed by the truncated 々ワガママ block_merged parent
in `_suppress_confirmed`. Fix = `_seed_supersedes()`: a column_seed that covers a **single-column** parent's
column AND extends beyond it by ~a glyph replaces that parent instead of being suppressed by it. Result:
散々ワガママ boxed in full (散 → last マ), 語っといて untouched, no dup, block count unchanged, gate stays
**raw 16/20 | core 15/15**.

> ⚠️ Rejected route: mirroring the UPDATE-4 seed size-gate fix into `_graph_edge` (same-column branch) DID
> box 散 and passed the gate, but it made 散々ワガママ + 語っといて merge into one 2-col block whose confirm
> `columnize` **trims both columns** (語っといて → 語っ, drops と・い; and clips the bottom マ) — a net
> **regression**, verified in the shipped path. Reverted. The confirm-trim on merged crops is the same
> faint-end family as Case 2.

---

## Case 2 — て: never detected; the mask barely responds

`語っといて` yields only **4 components** — `語@569, っ@622, と@679, い@726` — and they all bond cleanly
(every `_vertical_seed_edge = True`); the seed correctly ends at `い` (`843,545,888,744`). **て has no
component in any source** (blackhat / tophat / recall-close all empty below cy726). It is not a grouping
problem — the glyph never becomes a component.

Raw mask response in て's box vs its detected neighbour `い`:

| region | blackhat white-px | tophat white-px |
|---|---|---|
| て-box (843,750–815,888) | **14 / 2925** | 82 / 2925 (scattered) |
| い-box (detected, ref) | 142 / 2025 | — |

The mask render (`rois/debug_te_mask.png`) shows it plainly: `い` = a blob, `て` = a single speck. て is a
faint, thin, cursive light-grey glyph on the mid-pink diamond — **~10% of `い`'s contrast response**, far
below the `component_filter_global` `min_area=80`. This is the **same faint-text family as これ** (and the
CLAHE contrast-rescue line): a cheap-CV recall limit, not a bug.

**Fix — SHIPPED (`_extend_column_tails` column-tail extension).** Once a single-column vertical block is
confirmed, probe **one glyph-pitch beyond each end** along the column axis and rescue a faint glyph the
global mask dropped. Two things the measurement forced:
- A plain lower threshold is **not** enough — local Otsu/tophat/lab_delta all still miss て. It needs
  **CLAHE + a percentile (92nd) threshold + minimal morphology** (no OPEN, so thin cursive strokes survive).
- **Polarity matters for false positives:** polarity-matched **tophat** (light-on-dark) finds て (abs cy~776,
  area 179) and gives **0 hits on a rain-only control**; the wrong polarity (blackhat) grabs a rain streak.
  So the rescue picks the op from `_otsu_polarity` of the column crop.

Gated to glyph-sized / column-centered / aspect < 2.5 blobs. On the 48.0s reference frame 語っといて extends
y744 → **792** (includes て); これ and every other column unchanged; no junk blocks; block count stays 10;
gate **raw 16/20 | core 15/15**.

**Cross-frame caveat + cache fix (reconciles the real run).** The per-frame CLAHE probe is *frame-
inconsistent*: over the 15-frame window the 語っといて column is detected on frames 0/4/7/8/12/13, and the
tail-probe hits て only on **frames 0/4/13 (y≈792), misses on 7/8/12 (y=744)** — and the column x-drifts
843→872 (the subtitle is moving). The temporal cache fires OCR when the track reaches count 3 (frame 7), a
*non-extended* frame → the first real run read "語っとい". This is **not** a wiring miss (the extension does
reach the final bbox on the frames that hit) and **not** reader faintness. Fixed at the cache layer
(`temporal_cache._extend_seed_y`): a column_seed track remembers its **max observed Y-extent** and OCRs that
(current x), so a tail glyph proven on an earlier frame isn't lost on the firing frame. Real run now reads
**'語っといて'** in both modes (bbox 859,545,903,**792**). ponytail ceiling: y-union memory + single-column
one-pitch probe; full cross-frame smoothing (Kalman) for the drifting/animated caption is the P1 roadmap item.

---

## Recommended priority

1. **Case 1 (散) — DONE** via `_seed_supersedes()` in `_suppress_confirmed` (NMS). Fixes every column where a
   size-gated block_merged dropped its leading kanji but the seed still carried it. Gate held 16/20 | 15/15.
2. **Case 2 (て) — DONE** via `_extend_column_tails` (CLAHE + polarity-matched tophat, confirmed-column
   post-step). Kept spatially constrained + polarity-matched so it doesn't grab rain or trade away the
   art-suppression the pipeline is built on.

Both are recall (accuracy-mode) improvements; neither changes the realtime OCR-budget story in
`SEED-UTILITY-MEASURED.md`.
