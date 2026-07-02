# What "track lost" means today, and why it can't drive recall-source gating yet

Date: 2026-07-02. Prompted by a question after P3 (`P3-DETECTOR-LATENCY.md`): the report named
"gate the recall source on track-loss" as a future lever (REALTIME-STATUS open problem 4) but explicitly
left it undone. This doc answers *why* — the current system has no clean "lost" signal to trigger it on,
and the hard cases already measured in this project (contrast dropout, drift, random relocation) are each
solved by **routing around** track-loss, not by refining what "lost" means. Code: `temporal_cache.py`.

---

## The one definition that exists, and what it deliberately doesn't know

`TemporalBlockCache` has exactly one notion of "lost" — a pure position-matching timeout:

```python
self.tracks[tid]["missed"] += 1
if self.tracks[tid]["missed"] > self.expire_frames:   # default 3
    del self.tracks[tid]
```

A track's `missed` counter increments whenever nothing in the current frame matches it (by IoU, or for
`column_seed`, by the alpha-beta predicted center — see `P1-KALMAN-CROPDIFF.md`). After more than
`expire_frames` consecutive misses, the track is deleted. That is the entire definition: **N consecutive
frames with no positional match.**

It is a blunt timeout because it cannot distinguish *why* nothing matched. Four causes look identical at
this layer:

| real cause | what SHOULD happen | what this project actually does about it |
|---|---|---|
| the caption genuinely ended | expire, free the track | this — and it's correct here |
| **same position, content changed** (dialogue swap) | re-OCR, don't expire | **routed around**: P1's crop-diff stale guard (MAD vs a per-kind threshold, 2-frame confirm) fires on a *matched* track — it never touches `missed`/expiry at all |
| **detection just failed this frame** (faint text, contrast dip) | keep the track alive across the gap, don't discard accumulated evidence | **routed around**: center-linked hit-count admission (this vs. a consecutive-age gate) |
| **position moved beyond the match radius** | keep tracking if the motion is predictable | **routed around**: alpha-beta prediction extends the effective linking radius along the caption's estimated velocity |

The pattern across all three "routed around" rows is the same: **this project has consistently found
that the hard cases are not fixed by making expiry smarter — they're fixed by not needing expiry to
answer the question in the first place.**

---

## The three named hard cases, mapped to what was actually measured

**High-contrast / intermittent faint text (これ).** Measured (`SEED-UTILITY-MEASURED.md`): これ is
detected in only 3 of 15 frames — it is *not* a track that gets lost and re-found, because under a
consecutive-age gate (`seed_stable=4`, the old design) it can never accumulate 4 in a row and is
correctly described as "never stably tracked" rather than "repeatedly lost." The fix
(`_best_center` + `stable_by_kind={"column_seed": 3}`) sidesteps expiry entirely: hits are counted
across arbitrarily large gaps (bounded only by `expire_frames` before the *track itself* is discarded),
not required to be consecutive. If track-loss timing were the discriminator here, これ would need
`expire_frames` large enough to survive its longest real gap and small enough not to conflate with
genuinely-ended captions — a single number can't satisfy both without per-caption knowledge this system
doesn't have.

**Drifting subtitle (語っといて, x: 843→872 over 13 frames).** Measured
(`P1-KALMAN-CROPDIFF.md`): a static `center_r=20` alone cannot always bridge a multi-frame gap during
drift — the self-check constructs exactly this failure (8px/frame drift + a 3-frame gap = 32px
displacement, which exceeds `center_r` with a static last-known-center). The fix is prediction
(`kf.predict(missed + 1)`), not a change to expiry. Genuinely fast motion that outruns the predictor's
confidence *should* still expire and re-spawn — the self-check's control case (a moving block with no
IoU overlap) documents this as correct, not a limitation to patch.

**Randomly-relocating detections (art-region flicker seeds).** Measured (`CONCLUSION-GROUPING-FINDING.md`
Phase 4A): 9 of 16 column_seed spawns land at `best_iou < 0.1` — a genuinely different position each
frame, not the same box wobbling. The correct behavior here is that these *never link into one track* —
`center_r` naturally filters them by radius at match time, before track-loss ever becomes relevant. There
is no "was this the same caption that got lost" question to answer, because it wasn't the same caption.

---

## Why this blocks recall-source gating specifically

The idea flagged in P3: run the expensive `blackhat_close_full` recall mask (the one that finds これ-class
faint text) only "on cold-start, periodically, or when a track is lost" instead of every frame — saving
roughly a third of the mask+CC budget (`P3-DETECTOR-LATENCY.md`).

The problem: at the moment a plausible trigger condition would fire — "no column_seed track currently
alive," or "a column_seed track just expired" — **that state is indistinguishable between "a real faint
caption just ended" and "the faint caption is still there but this project's own evidence shows it
routinely goes 2+ frames without being detected at all."** これ's own measured profile (3/15 frames, no
guaranteed consecutive detection) is a direct demonstration that "currently undetected" is not evidence
of "gone." A gate built on this signal would, on the available evidence, plausibly suppress the very
recall pass a real これ-class caption needs mid-caption — which is the opposite of what problem 4 wants.

**What would actually be needed before shipping this** — not done, flagged as its own follow-up:
a cheap, separate presence signal that is NOT "some track expired" (e.g., a coarse full-frame edge/ink
density check, independent of the column_seed track lifecycle) to distinguish "screen plausibly still has
faint text worth another expensive pass" from "screen is genuinely text-free." Until that signal is
measured, gating the recall source on track-loss risks trading a ~⅓ mask+CC saving for a real recall
regression on exactly the caption class (これ) three separate patches in this project were built to keep.

---

## Bottom line

Every hard case this project has hit so far was solved by **not asking track-loss to answer a question
it structurally cannot answer** — sparse detection, drift, and random relocation are each handled by a
mechanism that operates independently of the expiry timer. Track-loss today has exactly one job (free a
truly-gone track's memory) and is reliable at it. Overloading it as a trigger for a detection-cost
decision (recall-source gating) would be reusing a coarse timeout as a presence classifier — precisely
the kind of unmeasured semantic change P3 declined to bundle in. It stays a separate, unshipped item
(REALTIME-STATUS problem 4) pending its own presence-signal measurement.
