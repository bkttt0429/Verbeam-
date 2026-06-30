"""Contact sheet: evenly sample the whole clip so we can spot which frames carry text (and what layout)."""
import cv2, numpy as np
SRC=r"D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4"
OUT=r"D:\LocalTranslateHub\.codex-run\manga-ocr-bench\contact.png"
cap=cv2.VideoCapture(SRC); fps=cap.get(cv2.CAP_PROP_FPS); n=int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
COLS,ROWS=6,8; N=COLS*ROWS; tw=300
times=[i*(n/ N)/fps for i in range(N)]
cells=[]
for t in times:
    cap.set(cv2.CAP_PROP_POS_FRAMES,int(t*fps)); ok,fr=cap.read()
    if not ok: fr=np.zeros((100,178,3),np.uint8)
    th=int(fr.shape[0]*tw/fr.shape[1]); fr=cv2.resize(fr,(tw,th))
    cv2.putText(fr,f"{t:.0f}s",(6,24),cv2.FONT_HERSHEY_SIMPLEX,0.8,(0,0,255),2)
    cells.append(fr)
ch=cells[0].shape[0]
grid=np.zeros((ch*ROWS,tw*COLS,3),np.uint8)
for i,c in enumerate(cells):
    r,cc=divmod(i,COLS); grid[r*ch:(r+1)*ch, cc*tw:(cc+1)*tw]=c[:ch,:tw]
cv2.imwrite(OUT,grid); print("saved",OUT,grid.shape)
cap.release()
