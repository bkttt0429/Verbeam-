# Block Detector — CC-Proposal 重寫規格 (implementation spec)

Date: 2026-06-30
Status: APPROVED — 給下一個 session 照做
Scope file: `.codex-run/manga-ocr-bench/block_detector.py` (主), `columnizer.py` (加幾個 sibling),
`detector_preview.py` (改用新路徑 + 分段 bench)

---

## 0. 一句話

把 `block_detector.py` 的 proposal head 從「sliding window × CC-per-window」換成
「full-frame mask → CC 一次 → 聚 component 成 block」。**confirm → route → OCR 的尾段全部沿用,不重寫。**
只換管線的前段。

現況瓶頸已量化:fast scorer 把 morphology 提到全圖算一次,但 `connectedComponentsWithStats`
仍每個 window 重跑 ≈ 2830 次 → 6.75s(42s)/ 9.58s(48s)。proposal 反轉後這段塌成 2–3 次 CC。

---

## 1. 不准破的契約 (GUARDRAILS — 違反就是亂跑)

1. **mask = 幾何證據;raw crop = OCR 輸入。** 永遠不准把 mask / 前處理過的影像餵 OCR。
   selected OCR crop 必須是從原始 frame 切的 raw BGR crop。(沿用 `reader_routes._raw_crop`)
2. **職責分離:detector = frame→blocks;Columnizer = block→columns。**
   detector **不准**輸出「第 0 欄/第 1 欄」。欄是 `columnize()` 的事。輸出只到 block bbox + layout_hint。
3. **不准改 `columnize()` / `component_filter()` / `layout_gate()` 既有簽章與行為。**
   它們撐著 core 驗收。要新行為就**加 sibling 函式**或**加 result dict 的新 key**(additive),
   不要原地改既有 caller 看得到的東西。
4. **core 驗收不准退步:`python robustness.py` 必須維持 `raw 16/20 | core 15/15`,
   `python columnizer.py` 3 個 case 仍 OK。** 任何動到 columnizer.py 的項目都要跑這兩個再 commit。
5. **保留舊 sliding-window 路徑當 `scorer="window"` debug mode,先別刪。**
   新路徑 `scorer="cc"` 做出來、能在 42s/48s 對拍贏過 window 之後,才在後續 patch 刪 window。

---

## 2. 分期 (照順序,不准跳)

- **Phase 1(本規格,Patch A→B→C):** CC-proposal + 重複 `columnize()`/CC 消除 + 分段 bench。
  仍是 2 張靜態幀(42s/48s)的 preview,**不碰時間軸**。
- **Phase 2(本規格 §7,Patch D):** temporal tracker / OCR cache。**必須 Phase 1 驗收過才開始**,
  因為它需要 frame loop,是另一個 surface,別塞進靜態 preview。
- **DEFERRED(§6,本次明確不做):** component-graph proposal、downscale proposal、多種 bbox 型別、
  small learned detector。除非 Phase 1 在 42s/48s 上證明 dilation grouping 不夠,否則不准提前做。

---

## 3. 現況 touch map(省得重找)

```
block_detector.py
  WINDOW_SIZES / MIN_CLUSTER_VOTE        L26-37   ← Phase1 後變 debug-only,最終刪
  _frame_masks(frame)                    L115     ← 加 lab source (Patch B)
  _candidate_from_mask_crop / _exact     L123/168 ← window scorer,歸 debug-only
  _line_noise_features                   L207     ← 改吃 columnize 已有 comps (Patch B-reuse)
  _confirm_candidate_on_raw              L250     ← 沿用;回傳時把 result 塞進 candidate
  detect_text_blocks                     L298     ← 加 scorer="cc" 分支 (Patch A)
  _similar / _iou / _contained_frac      L57-98   ← cc 路徑用得到 _iou 去重,_similar 歸 window

columnizer.py
  component_filter(mask, roi_shape)      L88      ← 不改;新增 component_filter_global sibling
  _occupancy(mask)                       L165     ← vectorize (Patch B,需 robustness gate)
  layout_gate(comps, roi_shape)          L230     ← 不改;新增 layout_gate_scored sibling
  columnize(roi) result dict             L270-289 ← 加 "components" key (additive)
  _is_tall_streak / _branch_mask / _lab_delta_mask  ← cc/global 重用

detector_preview.py
  main loop columnize(roi) 重算          L147     ← 改吃 candidate.columnizer_result
  detect_text_blocks(...) 呼叫            L129     ← 加 --scorer / --stage
```

---

## 4. PHASE 1 工作項目

### Patch A — CC proposal(最大刀)

**A1. `columnizer.py`: 新增 global filter(不動既有 `component_filter`)**

```python
def component_filter_global(mask, frame_shape, min_area=80, min_fill=0.12):
    """Frame-scale CC for block PROPOSAL only.
    只做 absolute / 極端門檻 —— 不做 ROI-relative size 與 border-touch
    (那些在全圖尺度沒意義)。ROI-relative 的判斷留給 local confirm。
    Drop: area<min_area, fill<min_fill, 極端整幀長線(cw 或 ch > 0.85*對應 frame 邊),
          tall streak(重用 _is_tall_streak(cw,ch, frame_w, frame_h))。
    Return: list[Comp](同 namedtuple 格式)。
    """
```

理由:現有 `component_filter` 的 `ch>0.9*h / cw>0.55*w / touches border` 是 ROI 相對,
直接套全圖會誤殺。global 只濾「絕對是垃圾」的,其餘交給每個 block 的 local confirm。

**A2. `block_detector.py`: proposal 三件套**

```python
def _frame_masks(frame, scale=1.0):
    # 回傳 [(source, mask), ...]
    # blackhat_full, tophat_full  (現有)
    # + lab_delta_full            (Patch B 加;見 §4-B 成本警告)
    # scale<1 時在縮圖上算 mask(Patch B/§6 downscale,v1 預設 scale=1.0)

def full_frame_components(frame, scale=1.0):
    # for source, mask in _frame_masks(frame, scale):
    #     comps = component_filter_global(mask, frame.shape)
    # 回傳 [(source, comps, mask), ...]   # 每個 source 一次 CC,共 2~3 次

def group_components_into_blocks(comps, frame_shape, mode):
    # v1 = dilation grouping(lazy-correct 版,不是裸 dilate 整張 mask):
    #   1. 開一張黑底 canvas,把『過了 global filter 的 comp bbox』畫白
    #      (裸 cv2.dilate(mask) 會把被濾掉的線條黏回來 —— 不要那樣)
    #   2. 依 mode 用方向性 kernel 膨脹:
    #        vertical   : 直向 kernel 連欄內字 + 小橫向連同塊鄰欄,別跨對白框
    #        horizontal : 橫向 kernel
    #   3. CC 這張膨脹後 canvas,每個連通塊 = 一個 block bbox
    # 回傳 list[bbox]  (~10–50 個,不是 1415)

def propose_blocks_from_frame_masks(frame, scale=1.0, mode="vertical"):
    # full_frame_components → group_components_into_blocks(per source)
    # 跨 source 的 bbox 用 _iou>0.5 合併去重(只去重,不投票)
    # 回傳 list[{ "bbox":(x0,y0,x1,y1), "source":..., "layout_hint":"vertical_candidate" }]
```

膨脹 kernel 尺寸放具名常數,**在 42s/48s 上用眼睛調**(ponytail: 留校正旋鈕,別寫死):

```python
GROUP_KERNEL_VERTICAL   = (15, 65)   # (w,h) 起點,連欄內字
GROUP_KERNEL_V_BRIDGE   = (45, 11)   # 連同一 block 的鄰欄,太大會黏對白
GROUP_KERNEL_HORIZONTAL = (65, 15)
```

**A3. `detect_text_blocks(frame, ..., scorer="cc", mode="vertical")`**

```
scorer="cc"(新預設,A/B 對拍贏 window 後):
    proposals = propose_blocks_from_frame_masks(frame, mode=mode)
    for each proposal:
        candidate = _confirm_candidate_on_raw(frame, <proposal→BlockCandidate>, require_vertical)
        # ↑ 沿用!confirm/line-reject/refine bbox 完全不動
    最後用既有 _iou / _contained_frac 去重 → kept
scorer="window"(舊路徑原封不動,debug-only)
```

**confirm → route → OCR 尾段一律沿用。** `_confirm_candidate_on_raw`、`route_japanese_roi`、
raw-crop OCR 契約都不改。Phase 1 只換 proposal head。

去掉的東西(cc 路徑不需要,但先留著給 window):`WINDOW_SIZES`、stride、`_similar` 投票、
`min_vote`、cluster voting(L324-360)。這些只為了收拾 window 重疊;CC proposal 不重疊。

**A3 驗收門檻:**
- `python detector_preview.py --scorer cc --times 42 48`(no OCR)proposal+confirm 兩幀合計
  **比 window 快 ≥10×**(window 現況 ~16.5s;目標明顯進入亞秒~1s 級,不訂死 ms,因硬體而異)。
- contact sheet:cc 框到的真文字塊,要**涵蓋** DETECTOR-PREVIEW-NOTES 裡 42s/48s 那兩組
  已知 block(gray/purple/teal/red/視感 等),且 line-art false positive **不多於** window 版。

---

### Patch B — 消除重複 CC / 重算 + 安全小修

**B1. `BlockCandidate` 帶 `columnize` 結果(#4)**

```python
@dataclass
class BlockCandidate:
    ...
    columnizer_result: dict | None = None   # 新增,default None
```

`_confirm_candidate_on_raw` 內已經 `result = columnize(roi)` —— 回傳時塞進 `columnizer_result`。
`detector_preview.py` 改:

```python
result = candidate.columnizer_result or columnize(roi)   # 不再無條件重算
```

**B2. `_line_noise_features` 吃既有 comps,不再重跑 CC(#5)**
`columnize()` result 加 additive key `"components": comps`(internal,非穩定 schema)。
`_line_noise_features` 改成讀 `result["components"]`,不再對同一 mask 重新 `component_filter`。
若不想曝完整 comps,退而求其次:把 `line_frac/edge_frac/max_dom` 在 `columnize()` 內算完放進 `mask_dbg`。

**B3. `_frame_masks` 加 Lab source(#1)**

```python
("lab_delta_full", _lab_delta_mask(frame))
```

⚠️ **成本警告(務必量):** `_lab_delta_mask` 內 `medianBlur` 在全 1080p 幀、kernel 會被 clamp 到 51,
**很慢(可能數百 ms),會把省下的時間吃回去。** 對策(擇一,先量再決定):
- Lab source **只在 scale=0.5 縮圖**上算(與 §6 downscale 綁),或
- 若全 res Lab 量出來 >150ms,本 patch 先**不加 Lab**,標記待 downscale 落地再加。

Lab 只當 proposal/fallback source,rank 要保守(stricter occ、加 penalty),否則彩色背景邊界會混進來。

**B4. `_occupancy` vectorize(#6,**需 gate**)**

```python
def _occupancy(mask, grid=8):
    small = cv2.resize((mask > 0).astype(np.uint8) * 255, (grid, grid), interpolation=cv2.INTER_AREA)
    return float((small > int(255 * 0.02)).mean())
```

⚠️ occ margin 很緊(`OCC_MAX=0.50`,真文字 ≤0.44 vs rain 0.53)。**改完必須跑 `python robustness.py`
確認仍 `raw 16/20 | core 15/15`。** 退步就還原。收益小,純順手,不過就 gate 它。

**B5. `layout_gate_scored` + margin 進 rank(#9)**

```python
def layout_gate_scored(comps, roi_shape):
    """Return (layout, hs, vs, margin). margin = abs(vs-hs)/max(vs,hs,1e-6)."""
# 既有 layout_gate() 改成: return layout_gate_scored(comps, roi_shape)[0]   (行為不變)
```

`detect_text_blocks` rank 加入 `layout_margin`:勉強過線(1.36:1)的 vertical 要比 10:1 的低分。

**B6. detector 層 `unknown` 一律 reject(#7,#8)**
`_confirm_candidate_on_raw` 已經 `unknown → None`,維持。**CC proposal 變稀疏後也不准放鬆。**
`mode="vertical"` 時 proposal/confirm/rank/OCR 都不碰 horizontal candidate(早分流,別全算完才丟)。

---

### Patch C — 分段 benchmark(沒這個會繼續靠感覺調)

`detector_preview.py` 加 `--stage {proposal,confirm,ocr}` 與計時輸出:

```
mask_ms  cc_ms  group_ms  confirm_ms  ocr_ms
candidate_count  confirmed_count  ocr_calls
```

- `--stage proposal`:只跑到 block bbox,看 detector latency。
- `--stage confirm` :加 columnize,不 OCR。
- `--stage ocr`     :才載 manga-ocr。

用途:一眼分清「detector 慢 / columnize 慢 / OCR 慢」。

---

## 5. 實作順序(Patch 級)

```
Patch A: component_filter_global + full_frame_components + group_components_into_blocks
         + propose_blocks_from_frame_masks + detect_text_blocks(scorer="cc")
         ── 保留 scorer="window";--scorer 兩版可對拍 ──
Patch B: BlockCandidate.columnizer_result / columnize result["components"] /
         _line_noise_features 重用 / lab source(看成本)/ _occupancy vectorize(gate)/
         layout_gate_scored / mode 早分流
Patch C: --stage proposal|confirm|ocr + 分段計時
（A/B/C 全綠後)── 刪 window scorer 與其專屬 helper(WINDOW_SIZES/_similar/vote)──
Patch D: 見 §7,Phase 2,另開
```

每個 patch commit 前必跑 §8 驗收。

---

## 6. DEFERRED — 本次明確不做(防亂跑)

- **component-graph proposal(原 #3 的 B 路)。** v1 grouping 用 dilation。
  **觸發條件才做:** 若 Patch A 的 42s/48s contact sheet 出現 dilation 把鄰塊黏成一框、
  或把一欄拆兩塊,才加 graph grouping(近鄰 + 尺寸相近 + alignment 連邊)當 `mode` 內的替代 grouper。
  沒看到這種錯就**不要寫**。
- **downscale proposal(原 #11)。** `scale` 參數先留著、預設 1.0。除非 Lab 成本(B3)或整體 latency
  逼著要,才開 0.5。開的時候:proposal 用縮圖,confirm/OCR 一律回 full-res raw crop。
- **多種 bbox 型別 proposal/confirm/ocr_bbox(原 #13)。** v1 沿用既有單一 refined bbox + `_columns_bbox`。
  只有當看到「越 confirm 越 shrink / 切掉 furigana」才拆 bbox。
- **small learned detector(原 #15)。** 規則修完仍被線條/白框/furigana 騙,才考慮。不是現在。

---

## 7. PHASE 2 — temporal tracker(Patch D,Phase 1 驗收過才開始)

目的:讓**大多數幀 0 次 OCR**。manga-ocr greedy decode 無 KV-cache,長欄延遲隨欄長增加,
不能每幀跑(見 mangaocr-vertical-bench / latency 筆記)。

需要靜態 preview 之外的 frame loop(連續取幀),所以是獨立 surface,不塞進 42s/48s 靜態 preview。

**穩定 track id(別只存 bbox→text,box 抖一下就 miss):**

```python
track_key = { "layout", "bbox", "columns", "mask_source", "phash(roi)" }
match: IoU>0.6  AND  column_count 相同  AND  layout 相同  AND  pHash 距離小
```

**狀態機:**

```
NEW       第一次看到,不 OCR
STABLE    連續 2–3 幀 IoU/cols/layout 穩定 → 才 OCR
OCR_DONE  cache text
HOLD      box 短暫消失/motion blur → 沿用舊 text
EXPIRE    消失 N 幀後清掉
```

OCR 只吃 STABLE 的 `vertical_rl` raw crop。

---

## 8. 驗收 / 驗證指令(在 `.codex-run/manga-ocr-bench/venv` 內跑)

```bash
# core 不准退步(動到 columnizer.py 的每個 patch 都要跑)
python columnizer.py            # 3 個 case 仍 OK
python robustness.py            # 必須 raw 16/20 | core 15/15

# proposal 對拍 + latency
python detector_preview.py --scorer window --times 42 48 --stage proposal   # 基準
python detector_preview.py --scorer cc     --times 42 48 --stage proposal   # 目標 ≥10× faster
python detector_preview.py --scorer cc     --times 42 48 --stage confirm
python detector_preview.py --scorer cc     --times 42 48 --stage ocr        # 載 manga-ocr

# 看圖:rois/detector_preview/contact_sheet.png
```

## 9. Definition of Done(Phase 1)

```
[ ] scorer="cc" 兩幀 proposal+confirm 比 window ≥10× 快
[ ] cc 框涵蓋 42s/48s 已知 block,line-art FP 不多於 window 版
[ ] python robustness.py 仍 raw 16/20 | core 15/15
[ ] python columnizer.py 3 case 仍 OK
[ ] 每個 block 只跑一次 columnize(無重算)、_line_noise_features 不再重跑 CC
[ ] --stage proposal|confirm|ocr 三段計時可輸出
[ ] window scorer 與其 helper 已刪(A/B/C 全綠後)
[ ] mask=幾何 / raw crop=OCR 契約未破;detector 不輸出欄
```
