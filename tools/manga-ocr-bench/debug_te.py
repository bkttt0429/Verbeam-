"""DEBUG (て): is て missing because the MASK doesn't fire (contrast) or because CC filtering drops it?
Dump raw blackhat/tophat mask response in て's expected box (below い@cy726), + any comps to y=900."""
import sys
import cv2
import numpy as np
from columnizer import _branch_mask, component_filter_global
from block_detector import full_frame_components

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
OUT = r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\debug_te_mask.png"

cap = cv2.VideoCapture(SRC)
fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
cap.set(cv2.CAP_PROP_POS_FRAMES, int(48.0 * fps))
ok, frame = cap.read()
cap.release()

# any component in the whole column to y=900?
print("all comps x∈[820,910] y∈[520,900]:")
for source, comps, _m in full_frame_components(frame):
    hits = [c for c in comps if 820 <= c.cx <= 910 and 520 <= c.cy <= 900]
    print(f"  {source}: " + ", ".join(f"cy{c.cy:.0f}/a{c.area}" for c in sorted(hits, key=lambda c: c.cy)))

gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
blackhat = _branch_mask(gray, cv2.MORPH_BLACKHAT)
tophat = _branch_mask(gray, cv2.MORPH_TOPHAT)

# て expected box: same x as the column (~853-878), one glyph-pitch (~50px) below い@cy726 -> cy~776
TE = (843, 750, 888, 815)   # x0,y0,x1,y1
x0, y0, x1, y1 = TE
for name, m in (("blackhat", blackhat), ("tophat", tophat)):
    sub = m[y0:y1, x0:x1]
    white = int((sub > 0).sum())
    print(f"\n{name} in て-box {TE}: white_px={white}/{sub.size}  max={int(sub.max())}  mean={sub.mean():.1f}")

# for contrast context: the same stat over い@cy726 (a DETECTED faint char) box
II = (843, 705, 888, 750)
for name, m in (("blackhat", blackhat),):
    sub = m[II[1]:II[3], II[0]:II[2]]
    print(f"{name} in い-box {II} (detected char, ref): white_px={int((sub>0).sum())}/{sub.size} max={int(sub.max())}")

# save a zoom of the raw frame + blackhat mask around て for eyeballing
crop = frame[700:830, 830:900]
bh = cv2.cvtColor(blackhat[700:830, 830:900], cv2.COLOR_GRAY2BGR)
combo = cv2.hconcat([crop, bh])
combo = cv2.resize(combo, None, fx=4, fy=4, interpolation=cv2.INTER_NEAREST)
cv2.imwrite(OUT, combo)
print(f"\nwrote {OUT} (left=frame, right=blackhat mask, around て)")
