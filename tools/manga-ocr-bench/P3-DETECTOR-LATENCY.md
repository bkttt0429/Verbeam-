# P3 — Detector latency: profile-driven cuts, measured plateau

Date: 2026-07-02. Implements NEXT-STEPS-ROADMAP.md **P3**. Constraint honoured throughout: **identical
detector output** — every stage counter (proposals 861/338, confirmed 108, every reject bucket,
seed_trust 72) is unchanged, `robustness.py --no-ocr` stays raw 16/20 | core 15/15, and the clip's OCR
counts stay 19 (Full Recall) / 5 (Realtime) with both captions. Reproducer: `debug_prof.py`.

## What profiling refuted before any code was written

1. **The roadmap named "group (~191ms)" as a co-equal lever. Wrong.** cProfile: the group stage is only
   ~13–28 ms/frame; the #1 hotspot was `columnize` — **471 calls / 15 frames (44% of runtime), 235 of
   them from `_confirm_seed`**, which ran columnize on the SAME crop twice (once inside
   `_confirm_candidate_on_raw`, once again for the trust path) even though the inner function already
   accepts `result=`/`roi=` parameters.
2. **A cross-frame confirm memo (the roadmap's "skip confirm on stable tracks") is not viable at the
   detector level.** Measured: proposal bboxes repeat across frames **0% exactly, 20% at 8px
   quantization** — they jitter too much for a bbox-keyed cache to hit. Idea killed by measurement
   before implementation.

## The two cuts (both provably output-identical)

1. **`_confirm_seed` columnize dedup** — compute the crop + columnize once, share it with
   `_confirm_candidate_on_raw` via its existing `result=`/`roi=` params, reuse for the trust path.
   Same inputs, same function, same outputs by construction. columnize calls: **471 → 338**;
   confirm stage: **1394 → 1019 ms** per 15 frames (−27%).
2. **Vectorized `_graph_edges_vec`** — the O(N²) pure-Python `_graph_edge` pair scan (49K calls / 15
   frames) rewritten as numpy pairwise matrices, formula-for-formula identical (same expressions,
   strict/non-strict comparisons preserved). **Equivalence asserted, not assumed: 70,829 real pairs
   across all 15 frames × 3 mask sources, zero mismatches** against the scalar version (which stays in
   the file as the reference). group stage: **151 → 16 ms** per 15 frames (−89%).

## Measured result

| metric | before | after |
|---|---|---|
| like-for-like cProfile, 15 frames | 3.60 s | **2.76 s (−23%)** |
| clean per-frame median (`debug_perf.py`) | 540 ms* | **209 ms** |
| group stage / 15 frames | 151 ms | 16 ms |
| confirm stage / 15 frames | 1394 ms | 1019 ms |
| stage counters / gate / OCR counts | — | **all identical** |

*the 540 ms figure was measured in an earlier session and likely includes system load; the trustworthy
comparison is the same-session cProfile pair (−23%) and the per-stage counters. Current honest number:
**~209 ms/frame ≈ 4.8 fps** for the full-frame Python detector.

## Where the remaining 209 ms lives — and why this is the identical-output plateau

- **~77 ms** frame-level masks + CC: three full-1080p morphology chains (blackhat / tophat /
  blackhat_close) + three full-frame connectedComponents. Intrinsic to "3 mask sources on the full
  frame every frame".
- **~70–90 ms** confirm: 338 columnize calls (≈22/frame — every merged proposal, dominated by seeds).
  The cross-frame memo that would cut this was refuted (see above); a cheaper pre-columnize seed gate
  would change semantics.
- rest: proposal bookkeeping, NMS, tail-extension probes.

Further real gains require **semantic** changes, which are deliberately out of P3's scope and already
have owners:
- **gate the recall source** (blackhat_close every frame → cold-start / periodic / on-lost-track) —
  REALTIME-STATUS open problem 4, saves ~⅓ of mask+CC;
- **ROI-scoped detection with periodic full rescan** — the RoiBands pattern already proven in the C#
  live engine (realtime-roi-lock), belongs to the P5 integration;
- the **C# port** itself (this Python bench proves structure, not production speed).
