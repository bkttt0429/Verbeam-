# Grouping Finding — over-merge vs under-detect are the SAME root cause

Date: 2026-06-30
For: fresh web session pickup. Read this first, then `BLOCK-DETECTOR-CC-PROPOSAL-SPEC.md`.
Bench dir: `.codex-run/manga-ocr-bench/` (source files in this folder).

---

## TL;DR

The cheap-CV full-frame detector (`block_detector.py`, `scorer="cc"`) is fast and correct in
structure, but its **block grouping is a dumb uniform dilation** that cannot tell
"text + neighbouring text" apart from "text + character art". That single weakness produces
**two opposite-looking failures** on the test clip:

- **42s 「既視感」 (red title): OVER-MERGE.** Text gets glued to the red-umbrella character art
  into one big mixed block → confirms as non-vertical → rejected → no box at all.
- **48s 「これ」 (small white caption): UNDER-DETECT.** A weak 2-glyph column gets swallowed into
  the adjacent text block / is too weak to stand → no clean column box of its own.

These pull in **opposite directions**, so a global "tighten / loosen the dilation kernel" knob
**cannot fix both**. The correct fix is **content-aware grouping (component graph)**: connect
components by *size-similarity + alignment*, which (a) refuses to bond a 40px text glyph to a
200px art blob → fixes 既視感, and (b) bonds collinear small text into a proper multi-column
block and never drops a vote-2 column that is collinear with neighbours → fixes これ.

**Do NOT** spend another round tuning `GROUP_KERNEL_*` / `_nearby_block` thresholds.
**Do NOT** add a Lab proposal source to fix 既視感 (see §3 — it is already proposed by gray).

---

## UPDATE 2026-06-30 — component-graph grouping IMPLEMENTED + A/B'd. Result: HALF the fix.

The §5 grouper is now built (`group_components_into_blocks_graph` in `block_detector.py`, selectable
via `detector_preview.py --group graph|dilate`, default `graph`). Measured A/B on 42s + 48s. **The
"one root cause, graph fixes both" thesis is HALF RIGHT — and the over-merge half is DISPROVEN here:**

- ✅ **48s 「これ」 (under-detect) — FIXED.** これ is now its own clean column
  `col1 abs_x[559:590] y[164:253]` inside the multi-column block `[547,43,715,447]` — exactly the §5
  acceptance. (Required `STACK_GAP ≈ 2.5·glyph_h`, not 1.3 — これ's line gap is 1.8·glyph_h; a tight
  gap drops it.) 48s recall tuned back to **4/5** vs dilate's 5 (graph also adds the これ column
  dilate never produced); needed `GRAPH_BLOCK_PAD=18` (graph boxes hug comps → occ trips the
  confirm `occ≤0.45` gate) and `ADJ_OVERLAP=0.5`.
- ❌ **42s 「既視感」 (over-merge) — NOT FIXED, and the premise above is wrong for this frame.** §TL;DR
  assumed "40px glyph won't bond to a 200px art blob." **Measured: there is no 200px blob and no size
  gap.** The filtered comps in the 既視感 region are a *continuous size ladder 13→127px* (blackhat
  13.5…80.5, tophat 11.5…127.5); the large red title glyphs (~60–90px square, fill 0.43–0.60) and the
  umbrella/character art genuinely **overlap in size**. Single-linkage size-similarity (ratio<2
  pairwise) therefore **chains straight through** the intermediate sizes. Even when a box lands on the
  region it columnizes `no_text` at **occ=0.61** — text+art are co-located at high ink density, not a
  carve-out-able clean column.
- **Cost:** graph also loses recall elsewhere (before tuning 48s was 3/5) and is slightly slower
  end-to-end (group_ms itself is faster: 6–9ms vs 23–25ms) — the adjacency edge over-chains
  horizontally too (48s `[1508,…]` grew to 7 columns → killed by the confirm `cols≤4` gate). Same
  chaining disease as 既視感, sideways.

**Corrected takeaway:** content-aware grouping fixes the *recall* side (これ) but **cannot** fix the
*over-merge* side (既視感) — when text and art share color, size, position, and density, cheap-CV
size/alignment grouping has no separating signal. 既視感 needs a **different** signal (stroke-width,
connected-structure area, or actual recognition / VLM on `occ>0.55` mixed blocks), NOT a smarter
grouper. Grouping work on 既視感 is shelved. Current direction: keep graph (it earns これ), tune edge
rules to hold dilate's recall, then move to temporal cache (§7). See `mangaocr-vertical-bench` memo.

---

## UPDATE 2 — 2026-06-30 — Patch 1+2 SHIPPED: broad-block split + hard_mixed tag. [1508] fixed, 既視感 shelved.

Decision (user): do NOT cheap-CV-solve 既視感; broad-split the *controllable* over-chain; then go temporal.

- **Patch 1 — broad-block split** (`_confirm_candidate_on_raw_many` + `_split_broad_columns` +
  `_build_split_child`): a confirmed `vertical_rl` block with `cols > MAX_COLS_PER_BLOCK`(4) AND
  `occ ≤ CONFIRM_OCC_MAX`(0.48) AND `tl ≥ 0.5` AND not line_dominated is **split at gutters** (cut where an
  inter-column gap > `1.25·median_col_w`, then chunk to ≤4) into sub-blocks, instead of being killed by the
  confirm `cols≤4` gate. **GOTCHA (measured): do NOT re-columnize the fragments** — a 1–2-column fragment
  re-reads as `unknown`/`horizontal_ltr` (too few comps to score an axis) → dies on require_vertical
  (kids=0). Fix: emit children **directly from the parent's already-validated vertical columns** (rebased to
  child-crop coords), trusting the parent's occ/tl/line gates. detector still outputs *blocks*; columns
  stay `columnize()`'s product — guardrail intact.
- **Patch 2 — hard_mixed_art_text tag**: a `require_vertical/status/weak_mask` reject with `occ > 0.55` is
  re-tagged `hard_mixed_art_text`. 既視感 (no_text, occ 0.61) lands here — a metrics tag for an offline/VLM
  fallback, **NOT emitted, does NOT block realtime**.
- **occ cap 0.45→0.48** in confirm (graph block pad reverted to 10). Measured: pad≥14 mega-merges [1508]
  into an occ-0.55 no_text block (unsplittable); the occ bump recovers the [201] left block (occ 0.47)
  without any geometry change; 既視感 (0.61) stays blocked.

**Measured 42/48 (graph = default):** 48s [1508] recovered as **2 clean split children → 48s 6 boxes**
(parity with dilate's 7, cleaner); 42s **4 boxes + 既視感 correctly tagged & withheld** (dilate wrongly
emits the mixed box `[1299,247,1557,827]`); これ still its own column; core gate `raw 16/20 | core 15/15`;
det_ms 228/84 (<400). New stats: `broad_split_attempts / broad_split_children / reject_cols_over_limit`.

**Status:** grouping recall recovered; 既視感 = `hard_mixed_art_text` (offline-only, not realtime). **Next =
Phase 2 temporal cache (§7):** NEW→STABLE→OCR_DONE→HOLD→EXPIRE, OCR only on blocks stable 2–3 frames, skip
`hard_mixed_art_text`.

> ⚠️ **SUPERSEDED by UPDATE 3** for the "48s これ FIXED / 48s 6 boxes" claim — a 48s overlay shows
> これ has NO box and the purple ワガママ block is missing its 語っといて column. Re-measured below.

---

## UPDATE 3 — 2026-06-30 — 48s is NOT fixed; both misses are UPSTREAM of the proposal lifecycle.

A fresh 48s overlay (`frames/detector_48.png`) shows **これ has no box** and the purple block keeps only
the **散々ワガママ** column, not **語っといて**. The proposed fix (a `column_seed` proposal_kind preserved
through merge/confirm/NMS) assumes the weak columns are *proposed then swallowed*. **Measured: they are
never proposed at all** — both die before merge. `column_seed` preservation operates at merge→confirm→NMS
and so cannot fix either. The two misses have *different* root causes, both upstream of grouping output:

- **これ — component recall (mask).** `--stage proposal` at 48s = 8 proposals, **none** covers これ
  (x≈745–790 y≈175–255, in the gap between proposal#0 ending x705 and proposal#3 at x941). Probing the
  これ column: only **1** comp survives per mask — the 「れ」 glyph (blackhat area 91 / tophat 213, cy≈236).
  The 「こ」 glyph (cy≈185) is in the raw mask only as **sub-threshold fragments** (blackhat areas 21/10/8),
  which `component_filter_global` correctly discards. 1 comp → graph needs `len(group) ≥ 2` → no group →
  no proposal. **Not a lifecycle bug; faint-gray micro-caption under-segments at the glyph level.**
- **語っといて — `_graph_edge` gates too strict.** proposal#3 `[941,558,992,868]` is only the *right*
  column (51px wide = 1 col). The left column has **4 real comps** (cx≈864, cy 569/622/679/726) but
  **zero edges form** between them: 語→っ fails the size gate (`size_ratio 2.35 > GRAPH_SIZE_RATIO 2.0`,
  kanji-vs-kana); っ→と (`ygap 43 > stack_thresh 32`) and と→い (`ygap 35 ≮ 35`) fail `GRAPH_STACK_GAP`.
  4 isolated comps → no group → no proposal. **The size-ratio gate (meant to refuse text↔art) wrongly
  refuses kanji+kana in one column; the stack-gap, computed from the two glyphs' own heights, is too tight
  for small sparse kana.**

**Corrected fix (smaller, right layer — do NOT build the column_seed/NMS machinery for this):**
fix `_graph_edge`, not the lifecycle.
- Loosen/skip the **size-ratio gate on the stacked (same-column) branch** — within a vertical column a big
  kanji next to a small kana is normal text; the size gate's text↔art job belongs only to the
  *adjacent-column* branch.
- Give `GRAPH_STACK_GAP` an **absolute pixel floor** (or derive `along_ext` from a column-median glyph
  height, not the two endpoints) so small kana don't fall out on gap-relative-to-tiny-height.
- これ specifically additionally needs **component recall** (morphological close before
  `component_filter_global`, or a lower min-area rescue for collinear fragments) — or accept it as a
  deferred low-contrast micro-caption. Lower value than 語っといて; weigh against added noise.

**Broad-block split stays as-is (already shipped, UPDATE 2) — it is orthogonal** (handles confirmed
`vertical_rl` with cols>4). Neither 48s miss is a broad-split case. **視感 + character art** stays
`hard_mixed_art_text` / deferred (UPDATE 1 conclusion unchanged).

**Acceptance for "48s fixed" (column-level, from the overlay):** これ has its own clean box · 語っといて
restored as a column · 他に何がある？ / 以上 · 何がそんな / 不満なんだ · teal 3-col · all still present ·
視感 mixed stays withheld · `robustness.py --no-ocr` still `raw 16/20 | core 15/15`.

---

## UPDATE 4 — 2026-06-30 — column_seed gen SHIPPED (proposal-stage); same-column size gate dropped.

Built Patch A (debug metadata) + the column_seed generator and ran it on 48s to settle the UPDATE 3
caveats empirically. Both confirmed; the tractable one (語っといて) is fixed.

- **Patch A:** `BlockCandidate.kind / parent_id / component_ids`; proposals tagged `column_seed` vs
  `block_merged`; overlay legend (cyan = seed, orange = block); `events.tsv` adds `proposal_kind` +
  `component_count`. `_merge_proposal` now refuses to merge across kinds (UPDATE 3 step 3), so a seed is
  never absorbed by a block_merged.
- **column_seed generator** (`_column_seeds`, `detect_text_blocks(emit_seeds=True)`): one block per
  same-column comp chain (≥2 comps). **Wired to the proposal stage ONLY — NOT fed to confirm yet**, so the
  core gate stays byte-identical. `detector_preview.py --stage proposal` now emits + colours seeds.
- **Same-column size gate DROPPED** (`_vertical_seed_edge` no longer calls `_seed_size_similar`).
  Measured: with the spec's `size_similar(2.0)` the 語っといて seed started at っ (語/っ max-dim = 29/12 =
  2.42 > 2.0 → 語 excluded). After dropping it the seed = `(843,545,888,744)` vote=4, cy 569/622/679/726 —
  the full 語+っ+と+い column. `_seed_size_similar` is kept for the (future) adjacent-column branch, where
  the text↔art signal actually belongs. Separately, the vertical gap now uses frame-median `med_h` (not the
  two glyphs' own heights) — that is what lets the small kana っ→と→い bond at all (`_graph_edge` could not).
- **これ unchanged — still NO seed.** At x≈768 only **1** comp survives `component_filter_global` (こ is
  sub-threshold fragments); a 2-comp seed is impossible. This is mask/component recall, NOT a lifecycle
  bug — column_seed cannot fix it. Needs a separate morph-close-before-filter (or defer as a faint caption).
  NOTE: the doc's older §3 "これ" probe at x≈564 was actually 以上 (the c1 column inside the red diamond),
  not the gray これ at x≈768 the overlay circles — that mislabel is why これ was once thought fixed.

Core gate after the change: `raw 16/20 | core 15/15` (unchanged). 48s proposal stage = 4 column_seed +
4 block_merged.

**Still open:** (1) feed seeds into confirm + parent/child NMS (UPDATE 3 step 4) — seeds are debug-only
today; (2) これ component recall; (3) 視感 stays `hard_mixed_art_text`. Broad-split (Patch C) + hard_mixed
(Patch D) already shipped (UPDATE 2). Temporal cache (Patch E) last.

---

## 1. Current pipeline (what already works)

```
full frame
  → _frame_masks            blackhat_full + tophat_full (gray, morphology, Otsu)   ~50–75ms
  → full_frame_components   component_filter_global() per mask, CC once            ~25–30ms
  → group_components_into_blocks   paint comp bboxes → directional cv2.dilate → CC ~37–45ms
  → _merge_proposal         union/nearby merge across sources
  → _confirm_candidate_on_raw   columnize() each raw crop, reject reasons counted  rest
```

Speed (42s/48s): old sliding-window **5688 / 4989 ms** → cc **339 / 158 ms**. ~15–30×.
Core gate intact: `python robustness.py --no-ocr` → `raw 16/20 | core 15/15`.

Patch-1 (this round) added **reject-reason metrics** so we stop guessing:
`detector_preview.py --stage proposal|confirm|ocr` → `metrics.tsv` has
`raw_proposal_count / merged_proposal_count / proposal_count / confirmed_count` and a
`reject_breakdown` column (`right_edge / status / require_vertical / unknown / weak_mask /
wide_col_low_tl / line_dominated / size`).

---

## 2. Failure A — 42s 「既視感」 OVER-MERGE (measured)

`detector_preview.py --scorer cc --times 42 --stage proposal` shows:

```
proposal [1270,220,1577,815] vote=32   ← red text 既視感 GLUED TO the red-umbrella character
proposal [1340, 94,1560,227] vote=2    ← clean top fragment of 既視感, too weak
```

`--stage confirm` → 42s confirmed = only `[794,123,897,405]` and `[510,474,676,872]`.
The big mixed box is **rejected** (`reject_breakdown` 42s = `{require_vertical:1, right_edge:1,
status:1, weak_mask:1}`; the big mixed block columnizes as non-vertical → `require_vertical`).
`right_edge` is a *different* box `[1682,409,1920,792]`, not 既視感.

**Conclusion:** 既視感 IS proposed by the gray masks; it dies because the umbrella-character art
is merged into its block. This is a grouping precision problem, not a mask-recall problem.

## 3. Failure B — 48s 「これ」 UNDER-DETECT (measured)

Probe of the これ region (frame 48s, ROI x[470–600] y[80–260]):

```
[blackhat_full] comps-in-ROI = 1   (just one noise comp)
[tophat_full]   comps-in-ROI = 2   (the これ glyphs)
                  (564,169, 18x23) area=274 fill=0.66
                  (569,228, 13x17) area=116 fill=0.52
[tophat_full]   group block = (525,122,622,293) vote=2   ← これ IS grouped, but vote=2 (minimum)
```

これ is white text on the red diamond → only **tophat** sees it (blackhat is wrong polarity,
sees 1 noise comp). The vote-2 これ block then gets **absorbed by `_merge_proposal` into the
adjacent `[525,16,735,477]` 「他に何がある？」 proposal** → これ never gets its own clean column.

**Conclusion:** これ IS detected (by tophat) but is too weak / mis-merged into a neighbour.
This is a grouping recall problem — the *opposite* of 既視感.

## 4. Why both are one root cause

`group_components_into_blocks` paints every kept component's bbox onto a canvas and applies a
**uniform directional dilation**, then takes connected components of the dilated canvas. Uniform
dilation bonds **anything within kernel reach**, so it cannot distinguish:

- text glyph ↔ character-art blob  (should NOT bond → 既視感)
- text glyph ↔ collinear text glyph (SHOULD bond into multi-column block → これ)

Tightening the kernel helps 既視感 but kills the weak これ block. Loosening does the reverse.
**The knob is the wrong tool.** The grouping must be *content-aware*.

---

## 5. Recommended fix (next implementation)

Replace `group_components_into_blocks` dilation with a **component-graph grouper** (keep dilation
as `--group dilate` for A/B):

```
build graph over component_filter_global() comps:
  edge(c1, c2) iff
      size_similar:  0.5 <= median(c1.h,c1.w)/median(c2.h,c2.w) <= 2.0   (text↔art refused)
      AND aligned & near (vertical mode):
          |c1.cx - c2.cx| < 0.8 * median_glyph_w        (same column)
          0 < gap(c1,c2)  < ~2.5 * median_glyph_h       (stacked, small gap)
      OR adjacent-column (same block):
          |c1.cy band overlap| high AND column gap < ~1.2 * median_glyph_w
connected components of graph = block proposals
do NOT drop a 2-comp column if it is collinear with a neighbour column (multi-column block)
```

Expected: 既視感 art blobs (large, size-dissimilar to glyphs) won't join the text → text block
survives as vertical → confirms. これ (collinear small text) joins 以上/他に何がある？ as one
R→L multi-column sentence-block → boxed, read in order.

Acceptance (A/B on 42s + 48s):
- 42s: a `vertical_rl` block on 既視感 survives confirm (currently 0).
- 48s: これ is inside a clean column box (its own column within the sentence block).
- `python robustness.py --no-ocr` still `raw 16/20 | core 15/15` (grouping change must not
  touch columnizer core).
- detector_ms stays < ~400ms/frame.

Also pending (unchanged): right-edge / wide-col hard rejects in `_confirm_candidate_on_raw`
should become edge+line-feature conditional (they are content-blind today). Temporal cache is the
realtime unlock after grouping is stable (OCR only blocks stable 2–3 frames; see
`HANDOFF-COLUMNIZER.md` / `BLOCK-DETECTOR-CC-PROPOSAL-SPEC.md` §7).

---

## 6. How to run / verify

```bash
# in .codex-run/manga-ocr-bench/venv
python robustness.py --no-ocr                                   # core gate: raw 16/20 | core 15/15
python detector_preview.py --scorer cc --times 42 48 --stage proposal   # all proposal boxes
python detector_preview.py --scorer cc --times 42 48 --stage confirm    # survivors + reject_breakdown
# outputs: rois/<out>/contact_sheet.png, frames/detector_42.png, frames/detector_48.png,
#          metrics.tsv (reject_breakdown), events.tsv
python detector_preview.py --scorer window --times 42 48 --stage proposal   # old baseline for A/B
```

## 7. File map (in this folder)

```
columnizer.py        block→columns cheap-CV core (mask/polarity/component_filter/layout_gate/columnize)
                     + component_filter_global() (frame-scale CC for proposal)
block_detector.py    full-frame detector. scorer="cc" (new) | "window" (old, debug).
                     group_components_into_blocks() = THE function to replace (§5).
detector_preview.py  bench harness: --stage, metrics.tsv reject_breakdown, contact sheets.
reader_routes.py     raw-crop OCR routing (manga-ocr, vertical-JP only). mask=geometry, raw crop=OCR.
robustness.py        core regression gate (raw 16/20 | core 15/15).
cases.py             hand-labelled ROIs.
CONCLUSION-GROUPING-FINDING.md   <- this file
BLOCK-DETECTOR-CC-PROPOSAL-SPEC.md   the CC-proposal rewrite spec (guardrails, phases)
HANDOFF-COLUMNIZER.md   columnizer judgment chain + numbers + dead-ends
DETECTOR-PREVIEW-NOTES.md   detector history/notes
```

**Invariants (do not break):** mask = geometry only; raw crop = OCR input; detector outputs
blocks (not columns — columns are `columnize()`'s job); never regress `raw 16/20 | core 15/15`.
