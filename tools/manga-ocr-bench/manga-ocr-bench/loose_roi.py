"""Judge OUR problem: does manga-ocr need a TIGHT single-column crop, or does a LOOSE/coarse ROI
(both columns + background, = what a cheap/garble-cluster detector would give) also read?"""
import sys, time
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
from manga_ocr import MangaOcr
from PIL import Image

ocr = MangaOcr(force_cpu=True)
R = r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois"

def rd(path, box, tag):
    img = Image.open(path).crop(box)  # box = (l,t,r,b) full-frame coords
    t=time.time(); txt=ocr(img); print(f"{tag:32s} {img.size[0]}x{img.size[1]} -> '{txt}'  {(time.time()-t)*1000:.0f}ms")

for t in [6.0, 6.5]:
    f = f"{R}\\frame_t{t}.png"
    rd(f, (1330,120,1760,820), f"band both-cols loose t{t}")     # both columns + bg
    rd(f, (1361,377,1608,770), f"ppocr-cluster-union t{t}")       # garble-cluster union (both cols)
    rd(f, (1300,150,1800,850), f"sloppy big ROI t{t}")           # deliberately sloppy
cap=None
