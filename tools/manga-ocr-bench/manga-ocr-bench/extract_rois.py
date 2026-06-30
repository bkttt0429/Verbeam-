"""Extract the real failing vertical-subtitle frames + save full frames so we can pick exact ROI crops."""
import cv2, os
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
OUT = r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois"
os.makedirs(OUT, exist_ok=True)
cap = cv2.VideoCapture(SRC)
fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)); H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
print(f"fps={fps:.2f} size={W}x{H}")
for t in [2.0, 5.0, 6.0, 6.5]:
    cap.set(cv2.CAP_PROP_POS_FRAMES, int(t*fps)); ok, fr = cap.read()
    if not ok: print(f"t={t} read fail"); continue
    p = os.path.join(OUT, f"frame_t{t}.png"); cv2.imwrite(p, fr)
    # right-side subtitle band where the vertical columns live (from trace: x~1350-1750)
    crop = fr[120:820, 1330:1760]
    cv2.imwrite(os.path.join(OUT, f"band_t{t}.png"), crop)
    print(f"t={t} saved full + band {crop.shape[1]}x{crop.shape[0]}")
cap.release()
