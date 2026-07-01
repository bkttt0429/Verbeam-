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

## UPDATE 5 — 2026-06-30 — Patch #1 SHIPPED: column_seed → confirm + parent/child NMS. 語っといて recovered.

Wired column_seed proposals into the real confirm/kept path (UPDATE 3 step 4). 語っといて now confirms; これ
still absent (needs #2); art false-positives bounded by a trust-path guard. Core gate unchanged.

- **Seeds → confirm.** `detect_text_blocks` now confirms ALL proposals (seeds + blocks) then suppresses,
  instead of early-breaking. Seeds route through `_confirm_seed`, blocks through `_confirm_candidate_on_raw_many`.
- **Seeds never nearby-merge.** `_merge_proposal` applies `_nearby_block` only to block_merged; a column_seed
  dedups by IoU/containment only — else two adjacent seeds glue into a multi-col box no parent column can
  represent → duplicate boxes. (Removed the bogus `cols=2` "seeds".)
- **Parent/child NMS** (`_suppress_confirmed` + `_representing_parent`): parents accepted first (normal NMS),
  then a seed is dropped only if a confirmed parent already has a column AT the seed's position — strong
  x-overlap AND y-overlap, each normalised by the *narrower* box (the seed's comp-derived bbox is wider/taller
  than the columnize column, so a full-IoU test wrongly missed the match). A seed no parent column represents
  (standalone 語っといて) is KEPT. Collapsed 4 duplicate 48s boxes.
- **Trust-confirm for faint columns** (`_confirm_seed`). A seed whose raw crop re-columnizes as `no_text`
  (faint purple-on-purple 語っといて: columnize's local mask sees n=0 though `component_filter_global` saw 4
  comps at frame scale) is trusted as one `vertical_rl` column from its validated seed geometry — the same
  trust trick UPDATE 2 uses for broad-split children. Art guard: reject if `occ > 0.48`, line-dominated, OR
  `h/w < SEED_TRUST_MIN_ASPECT` (2.5) — squat 2-blob art pairs (h/w ~1.5) are rejected. Cut 4 of 5 art
  trust-seeds. Seeds that columnize cleanly still go through the normal text gate (not trusted blindly).

**Measured 48s confirm = 8 blocks:** 他に何がある/以上 · 何がそんな/不満なんだ · teal 3-col · 散々ワガママ ·
**語っといて `[843,545,888,744]` (NEW — was missing)** · 視感/char region (broad_split, deferred). **これ
STILL no box** (1 surviving comp; needs #2 morph-close). **42s:** 既視感 still withheld; one thin-line
straggler `[1894]` in the deferred far-right art region (h/w 6.6 slips the aspect guard — not chased, per
the art-region deferral). Core gate `raw 16/20 | core 15/15` throughout.

**Acceptance 48s:** B 他に何がある ✅ · C 何がそんな/不満 ✅ · D 散々ワガママ + 語っといて ✅ (語っといて
recovered) · E teal 3-col ✅ · A これ ❌ (pending #2) · F 視感 deferred. **Next = #2:** これ component recall
(morph-close before `component_filter_global`, proposal-source only, low rank), then temporal cache (#3).
New tunable: `SEED_TRUST_MIN_ASPECT`.

---

## UPDATE 6 — 2026-06-30 — #2 SHIPPED: fragment-recall source. これ recovered. 48s acceptance A–E all pass.

Added a fragment-recall proposal source so faint glyphs that fragment below `component_filter_global`'s
`min_area=80` can still form a seed. これ now confirms; no new art false-positives; core gate unchanged.

- **Recall source** (`blackhat_close_full` in `_frame_masks`): a vertical morph-close
  (`RECALL_CLOSE_KERNEL=(5,19)`) merges sub-threshold collinear glyph fragments. 48s こ was blackhat areas
  21/10/8, 12px apart → closed into ONE area-153 comp that clears `min_area=80` (no threshold lowering, so
  no extra noise — still 56 comps frame-wide). The kernel bridges intra-glyph gaps (~12px) but NOT the 35px
  こ-れ gap, so stacked glyphs stay separate comps. Feeds `column_seed` ONLY (never block_merged), at score
  −`RECALL_SEED_SCORE_PENALTY`(0.5) so a normal-mask parent/seed always wins NMS.
- **Aspect guard relaxed 2.5 → 2.25.** これ is a 2-glyph column = h/w 2.3; the 2.5 trust gate (UPDATE 5) cut
  it together with art. Measured: 42s art trust-seeds are h/w ≤ 2.2 and 48s art 1.5/1.6, so 2.25 admits これ
  (2.3) and re-admits NO art on either frame. **FRAGILE 0.1 margin** — exactly the cheap-CV text/art limit
  (UPDATE 1); robust separation is the learned-detector/VLM path, not this knob.

**Measured 48s confirm:** これ `[756,161,797,256]` (NEW) · 語っといて · 他に何がある/以上 · 何がそんな/不満なんだ ·
teal 3-col · 散々ワガママ · 視感/char (broad_split, deferred). **42s unchanged** (既視感 withheld, no new art).
Core gate `raw 16/20 | core 15/15`.

**48s acceptance: A これ ✅ · B ✅ · C ✅ · D 散々ワガママ + 語っといて ✅ · E teal 3-col ✅ · F 視感 deferred.**
All column-level targets met. **Next = #3 temporal cache** (NEW→STABLE→OCR_DONE→HOLD→EXPIRE; OCR only on
blocks stable 2–3 frames; skip `hard_mixed_art_text`). Cost note: the recall source adds 1 CC pass/frame —
gate it in #3. New tunables: `RECALL_CLOSE_KERNEL`, `RECALL_SEED_SCORE_PENALTY`, `SEED_TRUST_MIN_ASPECT`(2.25).

---

## UPDATE 7 — 2026-06-30 — #3 SHIPPED: temporal block cache. 78% fewer OCR calls on the 48s stream.

Phase 2 cache built (`temporal_cache.py` + `temporal_stream.py`). The detector emits blocks every frame and
manga-ocr has no KV-cache, so re-reading a stable caption every frame is the cost. The cache matches a
frame's blocks to tracklets by bbox IoU and fires OCR only once a block is STABLE (≥ `stable_frames`), then
HOLDs (reuse cached text, 0 OCR); a vanished block EXPIREs after `expire_frames` misses.

- **`TemporalBlockCache`** (`temporal_cache.py`): NEW → OCR_DONE (OCR fires once) → HOLD (0 OCR) → EXPIRE.
  Defaults `stable_frames=2, expire_frames=3, match_iou=0.5`. Dependency-free (inline IoU); `python
  temporal_cache.py` runs a self-check (static block OCRs exactly once then HOLDs; expires when gone; a
  10-frame caption = 1 OCR not 10).
- **`hard_mixed_art_text` needs no special-casing** — it is a confirm *reject*, never a kept block, so it
  never enters the cache.
- **`temporal_stream.py`**: runs `detect_text_blocks` over consecutive frames, feeds the cache, reports OCR
  calls cache-vs-naive (`--start 48.0 --frames 15`).

**Measured (48.0s, 15 consecutive native frames):** naive = **108** OCR calls (every block every frame),
cache = **24 → 78% fewer**; several frames hit 0 OCR. The residual calls are detector **jitter**:
seed/broad_split bboxes wobble frame-to-frame and drop below `match_iou` → new tracklet → re-OCR. The stable
`block_merged` captions (これ/語っといて/teal/white) hold cleanly. Pushing savings higher is the detector
*stabiliser's* job (separate — see `realtime-region-stabilizer`), not the cache.

**ponytail caveat:** matching is position-IoU only — a same-position scene change would reuse stale text;
add a crop mean-abs-diff re-OCR trigger if real clips show it. **Cost:** the #2 recall source adds 1 CC/frame
— gate it (cold-frame only) when this goes realtime. New tunables: `stable_frames`, `expire_frames`, `match_iou`.

**#1–#3 complete.** Grouping/lifecycle work (UPDATE 3–7) done; 48s acceptance A–E pass; the temporal cache
cuts realtime OCR ~78%. Remaining: detector temporal stability, and the learned-detector/VLM path for the
`hard_mixed_art_text` 既視感/視感 region (cheap-CV can't separate it — UPDATE 1).

---

## UPDATE 8 — 2026-06-30 — Phase 4 (4A) done. Finding: a matching stabilizer will NOT reach the ≤12 target.

Built 4A observation first (as planned: measure before tuning), plus a small redundant-seed cleanup. The
measurement **redirects Phase 4** — the residual OCR is not what the stabilizer (4B/4C) would fix.

- **Instrumentation:** `TemporalBlockCache.update` returns `spawned` + `best_iou` per block;
  `temporal_stream.py` breaks OCR/spawns down by proposal kind and best-IoU bucket.
- **Redundant-seed cleanup:** `_representing_parent` dropped its y-overlap requirement (it made a seed
  duplicating a parent column flicker in/out); now drops a seed contained in a parent bbox AND x-aligned
  with one of its columns. Removed a duplicate teal column from 48s. Core gate unchanged (`16/20`).

**4A measured (48.0s, 15 frames, cache = 23 OCR):**
- Spawns (= jitter re-OCR source): block_merged 4, **column_seed 16**, broad_split 5. OCR by kind:
  block_merged 7, broad_split 5, column_seed 11.
- **The stable captions are already stable**: これ spawns once (f3) then HOLDs, 語っといて never re-spawns,
  block_merged captions barely churn.
- **The residual is STANDALONE marginal/recall column_seed FLICKER**: 9 of 16 seed spawns have
  `best_iou < 0.1` — they form at DIFFERENT positions each frame (teal-region x214/233/305, art-region,
  white-adjacent x1229/1271), not the same box wobbling. Suppression tests don't touch them (they aren't
  inside a parent).

**Conclusion — 4B (kind-aware IoU) / 4C (canonical bbox) cannot reach ≤12.** Those target position WOBBLE
(`best_iou 0.35–0.5`) = only ~5 spawns here. The dominant jitter is APPEARANCE flicker (`best_iou<0.1`) of
low-value marginal/recall seeds — a detection-precision problem, not track-matching. The high-value text
(block_merged + これ + 語っといて) is already stable and cached.

**Real levers (a decision, not a stabilizer module):**
1. **Marginal-seed precision** — the flickering seeds are the h/w≈2.2–2.5 recall/aspect-threshold ones near
   art; tightening cuts flicker but trades against これ recall (これ is itself h/w 2.3).
2. **Defer the art region** (視感/dress broad_split + its seeds ≈ 7 of 23 OCR) — the hard_mixed zone already
   slated for deferral; removing it drops that share with no stabilizer.
3. **Caveat:** 48.0–48.6s is animation — some "flicker" may be real content change (correct re-OCR), not jitter.

4B–4F (stabilizer module, canonical bbox, crop-diff, recall gating) are **NOT built** — the data says they
won't pay off before the precision/deferral decision above. Recommend deciding (1)/(2) first.

---

## UPDATE 9 — 2026-06-30 — Patch 5 (seed admission) = ONE lever: kind-tiered stable_frames. Mode A/B shipped.

4A said the residual is column_seed *precision*, not matching. The fix is a single lever, not a
SeedAdmissionController: a flickering seed lands at a NEW position each frame, so its track never reaches a
higher stable-age gate, while a real caption persists and does. `TemporalBlockCache(stable_by_kind=
{"column_seed": N})`; block_merged/broad_split keep `stable_frames=2`.

**Measured (48.0s × 15 frames):**

| mode | `seed_stable` | OCR calls | captions read |
|---|---|---|---|
| **Accuracy (A)** | 2 | 23 | これ + 語っといて |
| **Realtime (B)** | 4 | **15** ✅ | 語っといて (これ dropped) |

(seed_stable=3 → 16, これ still dropped.) column_seed OCR 11 → 3 in realtime; block_merged/broad_split
unchanged. **Hits the ≤15 realtime target.** These is itself a flickery recall micro-caption — it never
persists 3+ frames (the recall source detects it intermittently), so persistence-gating sacrifices it. That
IS the intended Mode-B tradeoff (realtime drops low-contrast micro-captions for stability + cost); Mode A
keeps it.

**Patch 5's other parts (edge/trusted/recall classification, seed OCR budget, admission-reason taxonomy) are
NOT needed to hit the target** — the one lever does it (YAGNI). Recall-source CC gating (4F) remains a minor
CPU optimisation (extra morphology/frame, NOT OCR), low priority. Cache default = no gating (accuracy);
`temporal_stream.py --seed-stable` selects the mode. Self-check extended (column_seed OCRs at age N,
block_merged at 2). Core gate unaffected (no detector change this step).

**Phase 4/5 done.** Realtime mainline: stable captions HOLD, flickery seeds gated, OCR ~15/15-frames (86%
fewer). Accuracy mode retains full recall (23, keeps これ). Remaining is only the learned-detector/VLM path
for the hard_mixed 既視感/視感 region (unchanged — cheap-CV can't separate it).

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
