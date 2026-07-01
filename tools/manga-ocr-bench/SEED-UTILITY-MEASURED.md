# Seed admission → optimal A/B — MEASURED, and what survives of the utility-scorer proposal

Date: 2026-07-01. Bench dir: `tools/manga-ocr-bench/`. Reproducer: `measure_seed_spread.py`
(48.0s × 15 frames, the same clip/window as UPDATE 8/9). All numbers below are from one run of it.

The starting point was a design proposal to replace the single `seed_stable=2/4` gate with a weighted
**SeedUtility = P(text) × P(new_info) − OCR_cost − false_positive_risk** scorer (per-seed features, log-odds
temporal, budgeted top-K, correlation-clustering / set-packing framing), with **Accuracy/Realtime = one
scorer at two cost/budget operating points.** This report measures the problem atomically, then keeps the
parts of that proposal the data supports and cuts the parts it doesn't.

---

## TL;DR

- **The A/B "keep これ vs cut cost" tradeoff was NOT fundamental — it was an artifact of gating on
  _consecutive age_.** これ is detected in only **3/15 frames**; a gate that needs age≥4 is unsatisfiable for
  it, no matter how good the matcher is (measured: center-match at `seed_stable=4` changes nothing).
- **Fix = link これ's 3 sparse hits into one track by CENTER, then gate on a count of 3, not 4.**
  Measured: `center-match (R≥20px) + seed_stable=3` → **17 OCR keeping これ AND 語っといて**. This strictly
  dominates old Accuracy (23 OCR, same full recall) and, unlike old Realtime (15), does not drop これ.
- **The proposal's high-level framing was right (A/B = points on one cost-recall curve); its
  implementation was 12× too big.** Of ~10 proposed features, exactly **one** separates keep-これ from
  drop-flicker in the data (`temporal_persistence`), and it only works reframed as **windowed count at a
  center-linked location**. The geometry/aspect features measurably *cannot* separate the one hard case.
- **Art-region deferral is the big cost lever (15→~7) but cheap-CV can't earn it** — the art blocks pass
  confirm (Problem 3). It needs a configured per-game deferral ROI or the learned detector, not a CV gate.

---

## Method

Capture the detector output once over 48.0s × 15 frames, then replay it through different admission
policies (cheap; the detector is the only slow part). `where()` tags the two rescued captions by bbox so we
can assert a policy never silently drops them: **KORE = これ**, **KATATTOITE = 語っといて**.

---

## Finding 1 — the real characteristic of これ (my first hypothesis was WRONG)

I predicted これ would have a *stable center* and flicker would *scatter*, so a center-match cache would
separate them. **Measured, it's the opposite:**

| column_seed cluster | #frames / 15 | center x-spread |
|---|---|---|
| KATATTOITE (語っといて) | 6 | 29 px |
| **KORE (これ)** | **3** | **26 px** |
| art-persistent (1456,336) | 6 | 3 px |
| art-persistent (1248,308) | 4 | 25 px |
| 7 more untagged multi-frame | 2 each | 3–19 px |
| 6 untagged singletons | 1 each | — |

- これ's center is **not** more stable than flicker (26 px spread, and several flicker clusters are *tighter*
  at ~3 px). The center-stability signal I hypothesised **does not exist**.
- The real discriminator is **detection count at a location**: これ = **3 hits**, pure flicker = **1 hit**
  (6 singletons). "Keep vs drop" is a *count-over-a-window* question, not a *geometry* question.

**Why center-match alone fails (measured):** at `seed_stable=4`, sweeping center-radius R = 8→25 px changes
nothing — OCR stays 15, これ stays dropped. You cannot reach age 4 with only 3 detections. The binding
constraint is **count (3), not match quality.** This is the fact that refutes the "just track it better"
intuition (and refutes Phase-4B/4C, consistently with UPDATE 8).

---

## Finding 2 — the fix that works (measured)

これ has exactly 3 hits, ~26 px apart. Two things are needed together:
1. **Center-link** those 3 hits into one track (their bbox *extent* varies enough that IoU>0.5 fails between
   them, so IoU-matching fragments them into age-1 stubs — that is why plain `seed_stable=3` still drops it).
2. **Threshold = 3**, because that is これ's ceiling.

| policy | OCR | col_seed OCR | これ | 語っといて |
|---|---|---|---|---|
| `seed_stable=4`, IoU (old Realtime B) | 15 | 3 | ✗ | ✓ |
| `seed_stable=3`, IoU only | 16 | 3 | ✗ | ✓ |
| **`seed_stable=3`, center-match R=20** | **17** | 4 | **✓** | ✓ |
| `seed_stable=3`, center-match R=30 / 40 | 17 | 4 | ✓ | ✓ |
| `seed_stable=2`, IoU (old Accuracy A) | 23 | 11 | ✓ | ✓ |

So **`center-match R=20 + seed_stable=3` = 17 OCR at full caption recall.** Keeping これ costs **+2 OCR** over
old B (15→17) and **−6** vs old A (23→17). Use R=20 (not 30/40): R must stay below the nearest inter-column
gap (nearest distinct columns here are ~54 px apart) or center-match will glue two real columns into one
track. This is the single unconditional win — no scene-specific config.

---

## Finding 3 — art deferral is the big lever, but cheap-CV can't earn it

The 視感/dress art region is a spatially-stable right-side band (all broad_split clusters at
center-x ∈ [1290, 1582]; the persistent art column_seeds at x = 1248/1456/1311; art block_merged at
x = 1264/1607/1657). Dropping everything with center-x > 1200 px:

| policy (with art band dropped) | OCR | これ | 語っといて |
|---|---|---|---|
| `seed_stable=4` IoU | **6** | ✗ | ✓ |
| `seed_stable=3` center-match R=30 | **7** | ✓ | ✓ |

**15 → 7 while keeping BOTH captions.** But this is honest only about the *size* of the prize, not about how
to claim it: the art blocks **pass confirm** (they reach OCR as valid block_merged/broad_split — that is
exactly Problem 3, "cheap-CV has no separating signal"). So a durable `hard_mixed`-tag gate will **not**
catch them. The two real ways to earn this lever:
- **Configured per-game deferral ROI** ("ignore this screen region") — cheap, shippable now, and it already
  fits the per-game-profile line. The `x>1200` gate above is a stand-in for exactly this.
- **Learned detector / VLM** on the art region (Problem 3) — the only *automatic* separator. Not started.

---

## What survives of the utility-scorer proposal (mapped to measured facts)

The proposal's *framing* is adopted: **A/B is one cost-recall curve, not two rule sets.** The measurement
prunes the *implementation* hard, because most proposed features were measured not to separate the only case
that is actually hard (これ vs flicker).

| proposed piece | verdict | measured reason |
|---|---|---|
| `temporal_persistence` | **KEEP — load-bearing** | The ONLY signal that separates これ (3 hits) from singleton flicker (1 hit). |
| …but as *consecutive age* | **CHANGE** | age≥4 is unsatisfiable for a 3-hit caption; must be **windowed count at a center-linked location**. |
| `P_new_info` / `represented_by_parent` | **KEEP (already in code)** | Suppresses in-parent duplicate seeds; residual flicker is *standalone* so this isn't the これ lever, but it's correct and free. |
| `hard_mixed = −∞` (defer art) | **KEEP as intent** | Biggest cost lever (15→7). But needs a config ROI / learned detector, not a CV score (Finding 3). |
| `aspect_score`, `component_count`, `vertical_alignment`, `occ`, `line_dominated`, `isolated_edge` | **CUT as separators** | これ h/w 2.3 sits *inside* the flicker band; measured no separation. Already enforced at confirm anyway. |
| `recall_source_penalty` | **CUT — actively harmful** | これ exists ONLY via the recall source; penalising recall-source penalises これ. |
| 8-feature logistic + hand weights | **CUT** | Collapses to 1 useful feature; ~12 uncalibrated knobs fit to one 48s clip = overfit, strictly worse than one interpretable threshold. |
| log-odds decay (§8) | **DEFER** | Closer than seed_stable, but plain windowed-count(3) already hits the target; polish, not needed. |
| budgeted top-K (§9) | **CUT (for now)** | Total OCR is already 7–17; no frame emits enough seeds to need a budget. Revisit only if multi-clip overflows. |
| correlation-clustering / set-packing (§2,§10) | **CUT** | The existing graph-group + parent/child NMS + columnize *is* the greedy version; measured to already produce coherent columns. Reframing adds vocabulary, not capability. |

**Net:** the 12-knob utility function reduces to **two independent physical knobs** —
(1) windowed center-linked persistence threshold for column_seeds, (2) art deferral on/off — which is what
actually parameterises the A/B curve.

---

## The optimal A/B (measured)

| operating point | mechanism | OCR | これ | 語っといて |
|---|---|---|---|---|
| old Accuracy (A) | `seed_stable=2` | 23 | ✓ | ✓ |
| old Realtime (B) | `seed_stable=4` | 15 | ✗ | ✓ |
| **new A — full recall, no config** | center-match R20 + `seed_stable=3` | **17** | ✓ | ✓ |
| **new B — full recall, art-deferred** | new A + per-game deferral ROI | **7** | ✓ | ✓ |

The old A/B traded **recall** for cost (B loses これ). The new A/B trades only **cost** (art deferral),
at **full recall on both points**. That is the "optimal": the recall-sacrificing branch is gone.

---

## Minimal patch (ship this; skip the rest)

1. **`temporal_cache.py` — center fallback in `_best`** (~4 lines): match a block to a track if
   `IoU>match_iou` **OR** center-distance < `center_r` (default 20 px, must be < min inter-column gap). Set
   the new default `stable_by_kind={"column_seed": 3}`. → this alone is **new A (17, full recall)**, and it
   subsumes old A: strictly fewer OCR at the same recall. Extend the self-check: a 3-hit sparse caption at a
   stable center must OCR once under center-match+threshold-3, and must NOT under IoU+threshold-4.
2. **Art deferral = a config, not a CV gate**: add a per-game "ignore region" that removes blocks before the
   cache. → this is **new B (7)**. Do **not** try to earn it with a cheap-CV `hard_mixed` score — measured,
   the art passes confirm (Problem 3).

**Skip** (measured unnecessary): the per-feature logistic, log-odds, budgeted top-K, and the
correlation-clustering/set-packing layer. Add any of them only when a *measured* failure on real multi-clip
footage shows the two-knob version breaking.

---

## Caveats (do not overclaim)

- **One clip.** 48.0s × 15 frames, one scene. The two knobs are low-capacity so they should travel, but the
  `center_r=20` and any deferral ROI must be re-checked on real footage before they're called defaults.
- **Animation caveat (Problem 6).** Some block_merged / broad_split re-OCR over these 15 frames is genuine
  content change, not jitter; the seed/art changes here don't touch that share (block_merged stays at 7).
- **This is still bench-only** (Problem 5). Numbers are from `measure_seed_spread.py`, not the live engine.
