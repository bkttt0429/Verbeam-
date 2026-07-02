# P1 — Cross-frame track state: alpha-beta (Kalman) prediction + measured crop-diff stale guard

Date: 2026-07-02. Implements NEXT-STEPS-ROADMAP.md **P1** (owner priority: "字幕會移動 → 卡爾曼濾波" +
stale guard, one workstream). All in `temporal_cache.py`; reproducers `debug_mad.py` / `debug_mad2.py` /
`debug_mad3.py` / `debug_stale.py`. Every constant below is measured on the 48.0s clip, not guessed.

## TL;DR

- Each `column_seed` track now carries an **alpha-beta filter** (steady-state Kalman, constant-velocity,
  hand-rolled ~10 lines, no deps) on its center; `_best_center` matches against the **predicted** center,
  so a drifting caption re-links across detection gaps where its static last-position would fall outside
  `center_r`. Self-check proves it: an 8px/frame drifting caption with a 3-frame gap (32px of drift)
  links and OCRs with the filter, and provably cannot without it (control: strip the filter → the
  re-appearance spawns fresh, the old track expires, zero OCR).
- **Stale guard**: pass `frame=` to `update()`; every OCR'd track stores a thumbnail of its crop, and a
  mean-abs-diff above a per-kind threshold for **2 consecutive frames** re-arms OCR (dialogue changed at
  the same position). Center-linking makes tracks longer-lived, so this guard is what keeps them honest.
- **The roadmap's guessed design failed measurement twice and was corrected both times** (see below) —
  this is why the thresholds are trustworthy.
- **P2b wiring** (left by the P2 session): `quality_fn=` on the cache — a garbage read never becomes the
  track's text (previous good text is HELD), and the track still counts as OCR'd (no per-frame retry loop).

## What measurement refuted (and the fixes)

1. **"Fixed screen box + 32×32 thumb + MAD 18" does not work.** Measured: at a fixed box, same-content
   noise reaches 13.8 (caption drifts out of the box; rain) while the hardest genuine change — a
   same-style vertical-column swap (語っといて→散々ワガママ proxy) — measures only **10.4**. The
   distributions **overlap inverted**; no threshold exists. Resolution/normalization/Sobel variants don't
   fix it (`debug_mad2.py`: all sep-ratios < 0.5).
2. **Fix #1: thumb the TRACKED bbox** (detected x-range × the track's union y-extent, 24×96 column-shaped
   thumb). Drift misalignment vanishes: same-content ≤ 2.7, hardest swap ≥ 9.3 — **3.4× separation**
   (`debug_mad3.py`). Shift-search adds nothing (the tracked bbox already absorbs the drift).
3. **A single threshold then failed live** (`debug_stale.py` on the real clip): `block_merged` bboxes are
   LOOSE (padding + multi-column span), and the animated background inside them (the pink diamond slides
   under 散々ワガママ) raises the same-TEXT floor to **~8.5** → false re-OCRs at threshold 5.
4. **Fix #2: per-kind thresholds** (`stale_mad_by_kind`, mirroring `stable_by_kind`): `column_seed` 7.5
   (live floor 6.2 / swap 9.0), others 12.0 (floor 8.5 / real swap 45+). One-frame spikes (a measured
   15.5 transient on a clean track) are absorbed by the 2-frame confirm.

## Verified results (real clip, real manga-ocr)

| point | OCR | これ | 語っといて | whitebox | garbage cached |
|---|---|---|---|---|---|
| **Realtime** (production point) | **5 — unchanged** | ✓ | ✓ full | deferred | 0 |
| Realtime+Allowlist | 11 | ✓ | ✓ | ✓ | 0 |
| Full Recall | 19 (was 15) | ✓ | ✓ | ✓ | filtered by quality gate |

The Full-Recall +4 re-OCRs are **all in the animating art region** (MAD 14–47 = genuine pixel change —
the guard is doing its contract; the garbage text they produce is rejected by the quality gate, and the
whole region is deferred in the production Realtime point). **Zero false re-fires on real text tracks**
(他に何が… 1–3, そんなところも… ≤5.4, 語っといて ≤6.2 — all held). Gate `robustness.py --no-ocr`
unchanged at raw 16/20 | core 15/15; `temporal_cache.py` self-check extended (KF drift-link + control,
stale-guard content-swap, quality-gate HOLD-previous) and passing.

## Caveats (honest)

- **One-clip calibration, thin `column_seed` margin** (floor 6.2 vs swap 9.0 → 7.5). Must re-check on more
  footage before calling the numbers defaults; `stale_log` diagnostics hook + `debug_stale.py` exist for
  exactly that.
- The clip has **no natural same-position dialogue change**; the swap distribution is a proxy
  (cross-caption same-style columns) plus a synthetic self-check. Live validation needs a clip where the
  dialogue actually changes in place.
- The filter smooths the **center only**; extent still comes from the per-frame detection + y-union
  (geometry contract: OCR always reads a raw crop, never a hallucinated box).
