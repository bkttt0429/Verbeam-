"""Cut tight single-column crops (the unit manga-ocr should read) from the failing frames."""
import cv2, os
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
OUT = r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois"
cap = cv2.VideoCapture(SRC); fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
# full-frame crop boxes (x0,y0,x1,y1) per case, name, expected text
CASES = [
    (6.0, "unjuku", 1585, 275, 1730, 520, "未熟"),
    (6.0, "mujou",  1420, 380, 1580, 770, "無ジョウ"),
    (6.5, "unjuku", 1585, 300, 1730, 545, "未熟"),
    (6.5, "mujou",  1420, 410, 1580, 800, "無ジョウ"),
    (2.0, "unjuku_faint", 1585, 200, 1730, 440, "未熟"),
]
for t, name, x0, y0, x1, y1, exp in CASES:
    cap.set(cv2.CAP_PROP_POS_FRAMES, int(t*fps)); ok, fr = cap.read()
    if not ok: continue
    crop = fr[y0:y1, x0:x1]
    p = os.path.join(OUT, f"col_{name}_t{t}.png"); cv2.imwrite(p, crop)
    print(f"{name} t={t} -> {crop.shape[1]}x{crop.shape[0]} expect '{exp}'  {p}")
cap.release()
