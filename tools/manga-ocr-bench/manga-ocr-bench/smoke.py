import sys, time
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
from manga_ocr import MangaOcr
from PIL import Image
import glob, os
t0=time.time(); ocr = MangaOcr(force_cpu=True); print(f"load {time.time()-t0:.1f}s")
for p in sorted(glob.glob(r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois\col_*.png")):
    t=time.time(); txt = ocr(Image.open(p)); dt=time.time()-t
    print(f"{os.path.basename(p):28s} -> '{txt}'   {dt*1000:.0f}ms")
