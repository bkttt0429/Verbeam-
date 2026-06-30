"""Bench manga-ocr-torchless under one EP/device: warm latency per column + accuracy + peak RAM.
Reuses MangaOcr's processor/tokenizer/__call__, swaps in EP-specific ONNX sessions."""
import sys, os, time, json, glob, argparse, statistics, ctypes
from ctypes import wintypes
from pathlib import Path
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
import onnxruntime as ort
from PIL import Image
from huggingface_hub import hf_hub_download
from manga_ocr import MangaOcr

class PMC(ctypes.Structure):
    _fields_=[("cb",wintypes.DWORD),("pf",wintypes.DWORD),("PeakWS",ctypes.c_size_t),("WS",ctypes.c_size_t),
      ("a",ctypes.c_size_t),("b",ctypes.c_size_t),("c",ctypes.c_size_t),("d",ctypes.c_size_t),("e",ctypes.c_size_t),("f",ctypes.c_size_t)]
def ws_mb():
    k=ctypes.windll.kernel32; ps=ctypes.windll.psapi
    k.GetCurrentProcess.restype=wintypes.HANDLE
    ps.GetProcessMemoryInfo.argtypes=[wintypes.HANDLE, ctypes.POINTER(PMC), wintypes.DWORD]
    c=PMC(); c.cb=ctypes.sizeof(c)
    ok=ps.GetProcessMemoryInfo(k.GetCurrentProcess(), ctypes.byref(c), c.cb)
    return round(c.PeakWS/1048576,1), round(c.WS/1048576,1)

TOOL_DIR = Path(__file__).resolve().parent

ap=argparse.ArgumentParser(); ap.add_argument("--ep",required=True); ap.add_argument("--device",type=int,default=0)
ap.add_argument("--crops", default=str(TOOL_DIR / "outputs" / "columns" / "col_*.png"))
ap.add_argument("--hold",type=float,default=4.0); a=ap.parse_args()

repo="mayocream/manga-ocr-onnx"
enc=hf_hub_download(repo,"encoder_model.onnx"); dec=hf_hub_download(repo,"decoder_model.onnx")
ocr=MangaOcr(force_cpu=True)  # processor + tokenizer + ids + __call__
if a.ep=="cpu": providers=["CPUExecutionProvider"]
else: providers=[("DmlExecutionProvider",{"device_id":a.device}),"CPUExecutionProvider"]
t0=time.time()
ocr.encoder_session=ort.InferenceSession(enc,providers=providers)
ocr.decoder_session=ort.InferenceSession(dec,providers=providers)
load_ms=round((time.time()-t0)*1000)
real=[p.get_providers() if (p:=ocr.encoder_session) else None][0]

crops=sorted(glob.glob(a.crops))
imgs=[(os.path.basename(p), Image.open(p)) for p in crops]
# warmup (DML kernel init) on first crop
for _ in range(2): ocr(imgs[0][1])
results=[]
for name,img in imgs:
    ts=[]
    for _ in range(6):
        t=time.time(); txt=ocr(img); ts.append((time.time()-t)*1000)
    results.append({"name":name,"text":txt,"median_ms":round(statistics.median(ts)),"min_ms":round(min(ts))})
pk,cur=ws_mb()
print("BENCHJSON"+json.dumps({"ep":a.ep,"device":a.device,"providers":real,"load_ms":load_ms,
    "peak_ws_mb":pk,"ws_mb":cur,"results":results},ensure_ascii=False))
sys.stdout.flush()
time.sleep(a.hold)  # let parent sample nvidia-smi during a memory-resident window
