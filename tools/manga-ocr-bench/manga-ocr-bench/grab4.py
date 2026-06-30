import cv2
SRC=r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
O=r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\rois"
cap=cv2.VideoCapture(SRC); fps=cap.get(cv2.CAP_PROP_FPS)
for t in [248.0, 127.0, 69.0, 48.0]:
    cap.set(cv2.CAP_PROP_POS_FRAMES,int(t*fps)); ok,fr=cap.read()
    if ok: cv2.imwrite(f"{O}\\full_t{t}.png",fr); print("saved",t,fr.shape)
cap.release()
