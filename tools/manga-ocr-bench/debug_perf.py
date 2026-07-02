"""Measure per-frame latency + the new seed-admission/deferral overhead on the 48.0s window.
Detector is CPU OpenCV; OCR (manga-ocr) is NOT run here (ocr_fn=None) — this isolates the CV pipeline."""
import sys, time, statistics, tracemalloc
import cv2
from block_detector import detect_text_blocks
from temporal_cache import TemporalBlockCache
from deferral import apply_deferral_regions

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
cap = cv2.VideoCapture(SRC); fps = cap.get(cv2.CAP_PROP_FPS) or 24.0; start = int(48.0 * fps)

# preload frames so disk decode isn't in the timing
frames = []
for i in range(15):
    cap.set(cv2.CAP_PROP_POS_FRAMES, start + i); ok, f = cap.read()
    if ok: frames.append(f)
cap.release()

# warm up (first call pays lazy allocations)
detect_text_blocks(frames[0], scorer="cc", group="graph", emit_seeds=True, confirm_raw=True)

cache = TemporalBlockCache(stable_frames=2, expire_frames=3, center_r=20.0,
                           stable_by_kind={"column_seed": 3})
det_ms, cache_ms, defer_ms = [], [], []
agg = {}
tracemalloc.start()
for f in frames:
    t = time.perf_counter()
    blocks, stats = detect_text_blocks(f, scorer="cc", group="graph", emit_seeds=True,
                                       confirm_raw=True, return_stats=True)
    det_ms.append((time.perf_counter() - t) * 1000)
    for k in ("mask_ms", "cc_ms", "group_ms", "confirm_ms"):
        agg[k] = agg.get(k, 0.0) + stats.get(k, 0.0)
    t = time.perf_counter()
    blocks, deferred = apply_deferral_regions(blocks, [(1200, 0, 1920, 1080)])
    defer_ms.append((time.perf_counter() - t) * 1000)
    t = time.perf_counter()
    cache.update(blocks)
    cache_ms.append((time.perf_counter() - t) * 1000)
cur, peak = tracemalloc.get_traced_memory(); tracemalloc.stop()


def line(name, xs):
    print(f"  {name:<28} median={statistics.median(xs):7.2f} ms   mean={statistics.mean(xs):7.2f} ms   "
          f"max={max(xs):7.2f} ms")

print(f"frame {frames[0].shape[1]}x{frames[0].shape[0]}, {len(frames)} frames\n")
line("detect_text_blocks (total)", det_ms)
print(f"    breakdown (sum over {len(frames)} frames): " +
      "  ".join(f"{k}={agg[k]:.0f}" for k in ("mask_ms", "cc_ms", "group_ms", "confirm_ms")))
line("apply_deferral_regions", defer_ms)
line("TemporalBlockCache.update", cache_ms)
print(f"\n  python-heap peak during loop (tracemalloc) = {peak/1e6:.1f} MB  "
      f"(excludes OpenCV/native buffers)")
