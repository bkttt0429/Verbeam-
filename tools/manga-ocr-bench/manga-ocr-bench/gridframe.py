"""Save frames with a labelled pixel-coordinate grid so ROIs can be read off accurately.
Usage: venv\\Scripts\\python gridframe.py 37 21 42 132 153   ->  rois\\_grid_<t>.png
Grid lines every 160 px (x) / 120 px (y), labelled in ORIGINAL 1920x1080 pixel coords."""
import sys
import cv2
SRC = r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
cap = cv2.VideoCapture(SRC); fps = cap.get(cv2.CAP_PROP_FPS) or 24.0
ts = [float(a) for a in sys.argv[1:]] or [37, 21, 42, 132, 153]
for t in ts:
    cap.set(cv2.CAP_PROP_POS_FRAMES, int(t * fps)); ok, fr = cap.read()
    if not ok: continue
    h, w = fr.shape[:2]
    for x in range(0, w, 160):
        cv2.line(fr, (x, 0), (x, h), (0, 255, 255), 1)
        cv2.putText(fr, str(x), (x + 2, 24), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)
    for y in range(0, h, 120):
        cv2.line(fr, (0, y), (w, y), (0, 255, 255), 1)
        cv2.putText(fr, str(y), (2, y + 18), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)
    cv2.imwrite(rf"rois\_grid_{int(t)}.png", fr)
cap.release(); print("ok", ts)
