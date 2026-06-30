# 實作計畫 — GAP-A：非對稱 tall-streak filter（擋直向雨絲/刮痕被當欄）

> 給接手 session：這份是自足的。你不需要先前對話。先讀「冷啟動 context」，再照「Patch」改 `columnizer.py` 一個函式，然後照「驗收」跑 `robustness.py`。**只動 `component_filter()`，別碰其他層。**

---

## 0. 冷啟動 context（你接手時的現況）

工作區：`D:\LocalTranslateHub\.codex-run\manga-ocr-bench\`（隔離 venv，**用 `venv\Scripts\python` 跑**，不要用系統 python）。

這是一個**直書日文 OCR 的 Columnizer（分欄+欄序）bench**。管線（在 `columnizer.py`）：
```
ROI(BGR) → Otsu 判背景極性 → 只建一張 black-hat 或 top-hat mask
         → component_filter()（去雜訊）→ occupancy reject（雨/紋理 spread）
         → layout_gate（no_text/horizontal/vertical/unknown）→ 直書才分欄（cx 群聚 R→L）
```
reader 是 manga-ocr ONNX（讀單欄），不在這次範圍。詳見同目錄 `HANDOFF-COLUMNIZER.md`。

測試集是**資料驅動**：case 在 `cases.py`（dict list），runner 是 `robustness.py`。
- 跑法：`venv\Scripts\python robustness.py --no-ocr`（快，只看幾何，不載模型）／不加 `--no-ocr` 才會載 manga-ocr 跑讀出。
- `robustness.py` 只 assert case 裡有給的欄位（layout/polarity/cols/status/reason），reject 只要 reason 對也算 PASS。summary = `raw / core(非deferred) / 分類別`。
- **現況：20 cases，core 10/10、raw 11/20**（9 個 deferred = gap backlog）。

工具 `gridframe.py`：把影格疊像素座標格線存出（`venv\Scripts\python gridframe.py 21 132`），**新增/調 ROI 一律先用它讀座標**（肉眼從縮圖猜會偏很多）。本計畫不需新增 ROI，但若要驗證 ROI 用得上。

---

## 1. 問題與目標

`component_filter()`（`columnizer.py`）目前**刻意沒有 aspect-ratio 過濾**——因為之前的「對稱」aspect filter（`if aspect>8 or aspect<1/8: continue`）會殺掉「重」這種**橫向寬扁筆畫**，害 69s 白字黑底橫排判錯極性。那個移除是對的。

但副作用：**直向細長的雨絲/刮痕**重新混進 components → 被 cx 群聚當成「欄」。

**要補的是非對稱過濾：只丟「高瘦長線」(tall-thin)，保留「寬扁橫畫」(wide-thin)。**

目標把這兩個 case 從 deferred 拉回 core：
```
21s カワキヲアメク：  現 5 cols → 目標 vertical_rl, 1 col
132s ほっといてくれ： 現 6 cols → 目標 vertical_rl, 1 col   ⚠️ 見 §5 注意
```
**不可回歸**：
```
69s 重い → 必須仍 horizontal_ltr（不能因 aspect filter 又刪掉「重」的橫畫變 no_text）
5s 未熟/無ジョウ → 仍 2 cols
48s teal → 仍 3 cols
rain/no-text(69s) → 仍 reject
```

---

## 2. Patch（只改 `columnizer.py` 的 `component_filter`）

### 2a. 新增模組常數（放在檔案上方既有門檻常數附近，例如 OCC_MAX/MIN_ALIGN 那段）
```python
# GAP-A: drop tall-thin vertical scratches/rain, keep wide-thin horizontal CJK strokes.
TALL_STREAK_AR = 8.0          # h/w >= this = candidate streak
TALL_STREAK_MIN_H_FRAC = 0.18 # AND height >= 18% of ROI height (only long streaks)
TALL_STREAK_MAX_W_PX = 10     # AND width is narrow ...
TALL_STREAK_MAX_W_FRAC = 0.06 # ... <= max(10px, 6% ROI width)
```

### 2b. 新增 helper（放在 `component_filter` 之前）
```python
def _is_tall_streak(cw, ch, roi_w, roi_h):
    """Drop vertical rain/scratch strokes, but keep wide horizontal CJK strokes.
    Only checks h/w (tall-thin), never w/h — so 重's wide strokes survive."""
    if cw <= 0:
        return True
    tall_ar = ch / float(cw)
    return (
        tall_ar >= TALL_STREAK_AR
        and ch >= max(60, TALL_STREAK_MIN_H_FRAC * roi_h)
        and cw <= max(TALL_STREAK_MAX_W_PX, TALL_STREAK_MAX_W_FRAC * roi_w)
    )
```

### 2c. 在 `component_filter` 插入呼叫 + debug 計數
目前 `component_filter(mask, roi_shape)`。改成可選 `dbg`，在 `fill` 判斷之後、`touches` 判斷之前插入 streak drop：
```python
def component_filter(mask, roi_shape, dbg=None):
    """CC on a binary mask, drop noise / background / rain. Returns kept Comp list."""
    h, w = roi_shape[:2]
    long_side = max(h, w)
    if dbg is not None:
        dbg.setdefault("drop_tall", 0)
    n, _lab, stats, cent = cv2.connectedComponentsWithStats(mask, 8)
    comps = []
    for i in range(1, n):
        x, y, cw, ch, area = stats[i]
        if area < 80:                       continue
        if ch > 0.9 * h or cw > 0.55 * w:   continue
        if cw < 6 or ch < 6:                continue
        fill = area / float(cw * ch)
        if fill < 0.12:                     continue
        # GAP-A asymmetric aspect filter: drop tall-thin streaks, keep wide-thin strokes.
        if _is_tall_streak(cw, ch, w, h):
            if dbg is not None:
                dbg["drop_tall"] += 1
            continue
        touches = (x <= 1 or y <= 1 or x + cw >= w - 1 or y + ch >= h - 1)
        if touches and max(cw, ch) > 0.45 * long_side:
            continue
        comps.append(Comp(float(cent[i][0]), float(cent[i][1]),
                          int(x), int(y), int(cw), int(ch), int(area), float(fill)))
    return comps
```
（`dbg` 為可選參數 → 向後相容；`colz_debug.py` 也 import `component_filter`，不傳 dbg 仍可用。）

### 2d. 在 `make_text_mask_dual` 把 drop_tall + n 帶進 mask_dbg（方便調門檻）
目前：
```python
    comps = component_filter(mask, shape)
    occ = _occupancy(mask)
    score = text_likeness(comps, shape)
    dbg = {"occ": round(occ, 2), "tl": score}
```
改成：
```python
    filter_dbg = {}
    comps = component_filter(mask, shape, filter_dbg)
    occ = _occupancy(mask)
    score = text_likeness(comps, shape)
    dbg = {"occ": round(occ, 2), "tl": score, **filter_dbg, "n": len(comps)}
```
這樣 `robustness.py --no-ocr` 會印 `{'occ':0.20,'tl':0.75,'drop_tall':7,'n':4}`，調門檻時直接看丟了幾條。

---

## 3. ⛔ 不要做（避免踩回頭路 / 越界）
- **不要**恢復對稱 aspect filter（`if aspect>8 or aspect<1/8: continue`）→ 會再殺「重」的橫畫。只檢 `h/w`，不檢 `w/h`。
- **不要**動 `OCC_MAX` / `MIN_ALIGN` / occupancy 那層。已知真文字 occ≤0.44、紋理≥0.53，margin 窄但夠用；GAP-A 是「進欄前的噪線」問題，不應動 occupancy。
- **不要**做 color-aware mask（那是 GAP-B，動到 polarity/mask 主體，範圍大）。
- **不要**做 ruby / multi-block（後續層；Columnizer 職責限縮在單一 vertical-CJK block 的分欄+欄序）。
- **不要**新增/擴 ROI 測試集。本計畫只修這一刀。

---

## 4. 門檻調整（只在驗收沒過時動 `TALL_STREAK_MIN_H_FRAC`）
- 21s/132s 仍有殘留 scratch（cols 還是太多）→ `TALL_STREAK_MIN_H_FRAC` 往下調到 `0.14`。
- 正常直書筆畫被誤殺（5s/teal/48wb 的 cols 變少或變 no_text）→ 往上調到 `0.22`。
- 先用預設 `0.18` 跑一輪看 `drop_tall`/`n` 再決定。

---

## 5. ⚠️ 132s 的注意事項（A+B 混合，可能無法純靠 GAP-A 完全救）
132s「ほっといてくれ」是**白字壓亮綠**，灰階亮度對比極低（實測 ROI Laplacian 變異數 ≈13.6，極軟），原本的 6 欄多半是雨絲。GAP-A 把雨絲 streak 丟掉後，**真文字 component 可能本來就太弱/太少**，結果可能不是「1 col」而是 `no_text`（comps<2）。

判讀：
- **21s 是乾淨的 GAP-A 勝利**（白底深字，去雨絲後應穩定回 1 col）→ 必達。
- **132s 若去雨絲後變 `no_text`**，那是 **GAP-B（彩色低對比）** 的問題，不是 GAP-A 沒做好。此情況：**132s 留在 deferred，但把 deferred_reason 改成 GAP-B**（color-aware mask），別硬調 streak 門檻去湊（會傷到別的 case）。
- 若 132s 去雨絲後剛好回 1 col，那最好，移回 core。

也就是 GAP-A 的**硬性成功條件 = 21s 回 core 且零回歸**；132s 是 bonus。

---

## 6. 驗收

### 先跑幾何（快）
```bash
cd D:\LocalTranslateHub\.codex-run\manga-ocr-bench
venv\Scripts\python robustness.py --no-ocr
```
檢查：
| case | 期望 | 備註 |
|---|---|---|
| 21s カワキヲアメク | layout=vertical_rl, **cols=1**, drop_tall>0 | GAP-A 必達 |
| 132s ほっといてくれ | cols=1（理想）或 no_text（→ 轉 GAP-B） | 見 §5 |
| 69s 重い | layout=horizontal_ltr, 不可 no_text | 不回歸（最重要）|
| 5s | cols=2 | 不回歸 |
| 48s teal | cols=3 | 不回歸 |
| rain/no-text(69s) | reject | 不回歸 |

### 再跑完整 OCR（確認沒整體弄壞）
```bash
venv\Scripts\python robustness.py
```
GAP-A 成功條件是**幾何先對**（21s/132s 不再 5–6 欄、69s 不回歸），不要求 OCR 字字全對。

---

## 7. 成功後的善後（bookkeeping）
1. `cases.py`：把通過的 case 從 deferred 拉回 core——
   - `scratch-vert-1col(21s)`：移除 `deferred=True` 與 `deferred_reason`。
   - `green-1col(132s)`：若回 1 col → 同上移回 core；若變 no_text → 保留 deferred，`deferred_reason` 改成 `GAP-B color-aware mask`。
2. 重跑確認 summary：`core` 從 10/10 變 **11/11 或 12/12**（看 132s 是否回來）。
3. 更新 `HANDOFF-COLUMNIZER.md` §7 的 gap backlog：GAP-A 標 ✅ DONE（含實際門檻值與 21s/132s 結果），下一個指向 **GAP-B（color-aware mask）**。
4. 更新 memory `multilingual-vertical-ocr-router.md` 的 backlog 行（GAP-A done）。

---

## 8. 一句話
在 `component_filter` 加一個**只丟 tall-thin（h/w≥8 且夠高夠窄）**的過濾，保留 wide-thin 橫畫；目標 21s→1 欄（必達）、132s→1 欄（bonus，否則轉 GAP-B），69s/5s/teal/rain 零回歸；core 10/10 → 11–12/12。
