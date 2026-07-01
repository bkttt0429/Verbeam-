"""DEBUG (trace): why is 散 excluded from the 散々ワガママ column?
Test the two grouping paths on the real comps: block_merged graph edge (has size gate) and
column_seed edge (no size gate), plus whether a full seed is formed then NMS-suppressed."""
import sys
import statistics
import cv2
from block_detector import (full_frame_components, _column_seeds, _vertical_seed_edge,
                            _size_similar, _graph_edge, _representing_parent,
                            SEED_STACK_GAP, SEED_ALIGN_FRAC, GRAPH_SIZE_RATIO,
                            detect_text_blocks)

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"

cap = cv2.VideoCapture(SRC)
fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
cap.set(cv2.CAP_PROP_POS_FRAMES, int(48.0 * fps))
ok, frame = cap.read()
cap.release()

# blackhat_full comps (the source that carries 散々ワガママ)
source, comps, _m = full_frame_components(frame)[0]
col = [c for c in comps if 900 <= c.cx <= 1030 and 440 <= c.cy <= 900]
col.sort(key=lambda c: c.cy)
san = col[0]          # 散  (cy~512)
maru = col[1]         # 々  (cy~581)
print(f"散 : cx={san.cx:.0f} cy={san.cy:.0f} w={san.w} h={san.h}  size={0.5*(san.w+san.h):.1f}")
print(f"々 : cx={maru.cx:.0f} cy={maru.cy:.0f} w={maru.w} h={maru.h}  size={0.5*(maru.w+maru.h):.1f}")

# --- block_merged path: graph edge has a size gate ---
ratio = (0.5*(san.w+san.h)) / (0.5*(maru.w+maru.h))
print(f"\n[block_merged] size ratio 散/々 = {ratio:.2f}  (GRAPH_SIZE_RATIO={GRAPH_SIZE_RATIO})"
      f"  -> size_similar={_size_similar(san, maru)}  graph_edge={_graph_edge(san, maru, 'vertical')}")

# --- column_seed path: vertical seed edge has NO size gate, but a gap gate ---
med_w = statistics.median([c.w for c in comps])
med_h = statistics.median([c.h for c in comps])
gap = maru.y - (san.y + san.h)
print(f"\n[column_seed] med_w={med_w} med_h={med_h}  gap(散->々)={gap}px  "
      f"SEED_STACK_GAP*med_h={SEED_STACK_GAP*med_h:.0f}  cx_delta={abs(san.cx-maru.cx):.0f}"
      f"  SEED_ALIGN_FRAC*med_w={SEED_ALIGN_FRAC*med_w:.0f}")
print(f"  vertical_seed_edge(散,々) = {_vertical_seed_edge(san, maru, med_w, med_h)}")

# is 散 actually inside a generated seed?
seeds = _column_seeds(comps, frame.shape)
for s in seeds:
    x0, y0, x1, y1 = s["bbox"]
    if x0 <= san.cx <= x1 and y0 <= san.cy <= y1:
        print(f"  -> 散 IS in a seed: bbox={s['bbox']} comps={s['vote']}")
        break
else:
    print(f"  -> 散 is in NO seed (never bonded). {len(seeds)} seeds total.")
    for s in seeds:
        x0, y0, x1, y1 = s["bbox"]
        if 900 <= (x0+x1)//2 <= 1030 and 440 <= (y0+y1)//2 <= 900:
            print(f"     nearby seed bbox={s['bbox']} comps={s['vote']} (top y={y0})")
