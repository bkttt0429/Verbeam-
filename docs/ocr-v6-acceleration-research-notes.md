# OCR v6 加速研究交接筆記（2026-06-23）

> 給 Claude 研究用。本輪已驗證的數據 + 黑科技嘗試結果 + 已知瓶頸 + 未走的路。

---

## 背景

Verbeam realtime 遊戲字幕場景。問題：**首幀 cold full detect 太慢**，快速略過的短句（<5s）偵測完字幕已消失。穩態已 11-21ms（incremental cache），瓶頸在 cold full detect。

測試幀：
- `composite_1920x1080.png` = 1280×720 字幕幀置中進 1920×1080 黑畫布（模擬「框整螢幕」）
- `0001_00-00-03-940.png` = 原始 1280×720 YouTube 字幕幀
- 期望文字：`未熟 無ジョウ`（日文垂直字幕）

硬體：
- NVIDIA RTX 3050 Ti Laptop 4GB（dGPU，同時跑 ollama/llama-cpp CUDA）
- Intel Iris Xe Graphics 1GB 專屬 + 共享系統 RAM（iGPU）
- DML adapter log 回報 iGPU 128MB 是**內部 segment，不是總量**（別再誤判）

服務：`POST http://localhost:5768/ocr/bytes?provider=rapidocr-net-v6&realtime=true&language=ja&sessionId=X&autoSuppressRecurringText=false`，body=raw PNG bytes，content-type image/png。

---

## 已驗證數據（Release single-file dist，iGPU DML）

### DET model A/B（composite 1920×1080 tiling）

| det model | 大小 | composite 1920 | native 1280 | 備註 |
|---|---|---|---|---|
| v5 server `ch_PP-OCRv5_server_det` | 84 MB | 10283ms | 7480ms | 原預設，慢元兇 |
| v6 medium `PP-OCRv6_medium_det` | 59 MB | 3264ms | — | 3x 快 |
| **v6 small `PP-OCRv6_small_det`** | **9.4 MB** | **2386ms** | 3732ms | **最優，4.3x 快** |
| v6 tiny `PP-OCRv6_tiny_det` | 1.7 MB | 3593ms | 1051ms | DML 反而更慢（架構不友善）|

**結論：v6 small det (9.4MB) 最優。** tiny 在 DML 下反而慢（DML 對 tiny 架構 launch overhead 蓋過 compute 省下來的）。

### Execution Provider A/B（composite 1920×1080）

| EP | tiling 1920 | 原因 |
|---|---|---|
| **iGPU DML（device 1）** | **2386ms** ✅ | 共享記憶體免 PCIe copy + 不搶 dGPU |
| dGPU DML（device 0）| 37780ms ❌ | 被 llama-cpp/ollama CUDA 搶 + 每 tile PCIe 上傳 |
| CPU | 71808ms ❌ | 無 GPU 加速，8 tiles 災難 |
| CUDA | N/A | dist build 沒包 CUDA native DLL，靜默 fallback CPU |

**結論：iGPU DML 已是最優。** 原本 `PreferIntegratedGpu=true` 刻意避開 dGPU 是對的。

### 穩態 incremental

| 場景 | 延遲 |
|---|---|
| 同 session 同圖（cache hit）| 3-6ms |
| 同 session 3s 後微改幀（interval 伸縮 fix）| 11-21ms |

---

## 嘗試過的黑科技結果

### 1. ORT_ENABLE_ALL（DET 路徑）— ❌ 反效果
- REC 路徑（OnnxRecognizer）已設 `ORT_ENABLE_ALL`，DET 路徑用 `RapidOcr.GetDefaultSessionOptions` 沒設。
- 加上去後：composite 2386ms → 4331ms（**慢 2x**），native det 309ms → 615ms。
- 原因：DML EP 有自己的圖優化，ORT 的 layout transformation 干擾它。
- **已 revert。DET 路徑不要 ORT_ENABLE_ALL。**

### 2. DetMaxSideLen（det 內部 resize cap）— ❌ 對 tiling 無效
- `DetMaxSideLen` 控制 det 模型內部 resize，compute 隨它平方縮放，rec 讀原圖不受影響。
- 設 480：composite 2610ms（vs 2386ms default，噪音範圍）。
- 設 320：composite 2403ms（噪音範圍）。
- 原因：**瓶頸不是 compute，是每塊 tile 的 DML launch overhead**（8 tiles × ~290ms）。縮 MaxSideLen 省的是 tensor compute，但 DML per-shape compile + launch 佔主導。
- **已 revert 到 0（default）。**

### 3. Tile-shape warmup（DML 預編譯 600×600）— ✅ 已實作，無害
- 原本 warmup 只跑 960×2762（跟 tile 600×600 完全不同 shape），DML 按 shape 編譯 → 首次 tile 偵測付編譯費。
- 在 `WarmUp()` 加第二段：用 600×600 blank bitmap 跑一次 `_engine.Detect`，預編譯 DML 600×600 shape。
- log 確認：`warmup tile 600x600 in 138-202ms`（啟動時付一次）。
- 對 tiling 首幀延遲的實際改善在噪音範圍內（DML shape cache 機制可能本來就會 cache），但**無害且語義正確**，保留。

### 4. v6 模型 featuremap_cond 檢查 — ✅ v6 沒這問題
- `featuremap_cond appears in graph inputs` warning 會擋 const folding。
- 用 onnx 1.21.0（`.ocr-set/venv`）檢查：**v6 三個模型 input 都只有 `x`，沒有 featuremap_cond**。
- warning 來自 v5 warmup，跟 v6 無關。v6 的 const folding 沒被擋。

---

## 目前 source 狀態（vs 本輪起點）

### 淨變更（都在 `RapidOcrNetProvider.cs` + `RapidOcrRealtimeCache.cs` + appsettings）

1. **tiling + 自癒**（上一輪已寫，本輪驗證）：
   - `TryDetectTiledLines`（short side >760 → 8 tiles 600×600 overlap 96，IoU>0.45 去重）
   - `TryRecreateDetectEngine`（DML 崩潰自動重建 `_engine`）

2. **tiling 成功診斷 log**：`[ocr] tiled detect: N tiles -> M lines over WxH in X ms`

3. **redetect interval 伸縮**（本輪加，關鍵 fix）：
   - `RapidOcrRealtimeLayout` 加 `LastFullDetectMs` 欄位
   - store 時戳 `LastFullDetectMs = stopwatch.ElapsedMilliseconds`
   - interval 檢查改成 `max(RealtimeRedetectIntervalMs, lastFullDetectMs × 4)`
   - 效果：1080p tiled 從每 2s 重偵測 → ~59s，穩態 14-16s → 11-21ms

4. **v6 small det 預設**：appsettings `RapidOcrNetV6.DetModelPath` 改 `PP-OCRv6_small_det_inference.onnx`

5. **tile-shape warmup**：`WarmUp()` 加第二段 600×600 blank detect

6. **side-rescue gate 試誤已 revert**：曾嘗試大幀跳過 side-rescue（14s→5s），但 native 對比發現 `無ジョウ` 靠 side-rescue 的 `ApplyRealtimeIlluminationFlatten` 抓微弱 kana —— gate 會丟質量，已移除。

### 已知限制

- **微弱低對比 kana**（`無ジョウ`）仍靠 side-rescue illumination flatten，tiling 取代不了。未來若要安全跳 side-rescue 需做 per-tile illumination flattening。
- **語言偵測**在 sparse composite 上誤判 ja→zh-Hant-TW（`OcrLanguageScorer` 問題，與 tiling 無關，未動）。
- **首幀** 1080p tiling 仍 ~2.4s（v6 small det），是 iGPU 算力 + DML per-tile launch overhead 硬底。

---

## 瓶頸拆解（v6 small det, composite 1920×1080, 2386ms）

```
8 tiles × ~290ms/tile = ~2320ms  ← DML launch + iGPU 算力
side-rescue: ~0-2ms（v6 rec 直接讀到 kana，japaneseChars>=4 跳過）
rec: 含在 fullDetectMs 內
```

**每 tile 290ms 拆解**：DML per-shape compile（首塊）+ DML launch overhead + iGPU MatMul。算力只佔一小部分，這就是為什麼 DetMaxSideLen（縮 compute）幫不上。

---

## 未走的路（給 Claude 研究）

### A. 減少 tile 數量（最直接）
- 現在 8 tiles（600×600, step 504）。若 iGPU 能吃 800×800 不崩潰 → 4-6 tiles → 1.3-2x 快。
- 風險：大 tile 撞 iGPU MatMul crash（當初 tiling 就是為避這個）。需 A/B 測 `RealtimeDetectTileSide` 上限。
- `RealtimeDetectTileOverlap`（96）也可調小 → step 變大 → 少幾塊，但 seam 切字風險。

### B. 字幕帶優先偵測（最快首幀，但需 region 偵測）
- 遊戲字幕通常在底部 20-30% 區域。先只 det 底部一塊（1920×324 = 1 tile）→ ~290ms 出文字，背景 full tiling 之後補。
-犧牲：字幕出現在頂部/側邊時首幀漏（但 realtime 場景可接受，下幀 full detect 補）。
- 需要新邏輯：先 det bottom band → 有文字先回 → async full tiling 補完整 layout。
- 這是「首幀 <500ms」的唯一路。

### C. DML EP 改用 onnxruntime-genai 或 DirectML 1.13+
- 現用 `onnxruntime` DirectML EP。新一代 DirectML 對小模型 launch overhead 有改善。
- 需升級 onnxruntime-native + 重 publish。風險：相容性。

### D. iGPU DML session 複用（減 launch overhead）
- 現在每塊 tile 可能重建 DML resource。若能讓 8 tiles 共用同一 compiled session（shape 相同 600×600）→ launch overhead 降。
- 需查 RapidOcrNet 是否每 Detect 呼叫都重 compile，還是 shape cache。

### E. CPU DET for small frames + DML DET for large（混合）
- native 1280×720：CPU det 3238ms vs DML det 309ms（v6 small）→ DML 快 10x。
- 但 CPU 對 8-tile 災難（71s）。**單塊小幀 CPU 可能贏 DML launch overhead**。
- 門檻：short side <760 走 CPU single-pass？需 A/B。目前 DML 全場景已最快，這條價值低。

### F. 向量偵測 + ROI 鎖定（真正解決 realtime）
- 首幀 full tiling 找到字幕 ROI → 後續幀只 det ROI 區（1 tile）→ ~290ms/幀。
- 現有 incremental cache 已做到「同位置文字變 → rec-only 6ms」，但**背景動畫觸發 outside-cell 變化 → full detect**。ROI 鎖定可讓背景變化不觸發 full detect（只監 ROI 區）。
- 這是 Kalman ROI 的真正價值（不是預測移動，是**隔離背景變化**）。
- 需新邏輯：full detect 後選定 ROI box → 後續幀只 hash ROI 區 + det ROI 區，outside-cell 改成 ROI 外才觸發 full。

---

## Claude 研究建議優先序

1. **B（字幕帶優先偵測）** — 最直接解決「首幀太慢字幕消失」，~290ms 出字。改動小、風險低。
2. **F（ROI 鎖定隔離背景）** — 解決背景動畫觸發 full detect，讓穩態真正穩。價值最高但改動較大。
3. **A（減 tile 數）** — 線性加速首幀，但需實測 iGPU crash 上限。
4. **D（DML session 複用）** — 需深查 onnxruntime DirectML EP 內部，不確定能否改。

---

## 實作結果（Claude, 2026-06-23）：兩段式 coarse-locate → ROI-tile（合併 B+F）

採用研究建議的方向，把「盲目 8-12 tile 全格掃描」改成**先定位、再只 tile 字幕區**。已實作 + 驗證。

### 改了什麼（全在 `RapidOcrNetProvider.cs`）

- **Stage 1 `TryLocateTextRegions`**：在已下採樣的 detect bitmap（16:9→1138×640，跟小幀 single-pass 同等安全尺寸）跑**一次** det 定位文字。框可不準，只取範圍。輸入若仍過大會自降到 `RealtimeCoarseLocateShortSide`(640) 再 det，所以 `DetTargetShortSide=0/auto` 也安全。
- **`ClusterBoxesIntoRegions`**：每個粗框膨脹 0.6×較長邊+8，重疊者 union，取最大 3 個 region。雜訊多 → 退回全格。
- **Stage 2 `DetectLinesInRegions` + `DetectTilesInBounds`**：只在 region 內 tile（共用原全格的 per-tile detect/offset/dedup/自癒）。全格 tiler 也重構成呼叫 `DetectTilesInBounds(整幀)`，行為不變。
- **`TryDetectLargeFrameLines`** 編排：coarse→region tile→有字就回；coarse 空 → **退回全格 tiling**（保住微弱 kana 的準確度底線）。`FullDetectFrame` 大幀分支改呼叫它（一行替換）。
- **coarse-shape warmup**：`WarmUp()` 加 1138×640 預編譯，消掉首幀 DML kernel compile（cold 1774ms→warm ~300-500ms）。
- **`VB_OCR_FORCE_FULLGRID=1`** debug toggle：強制走舊全格路徑做 A/B。

### A/B 實測（src Debug、iGPU DML device 1、同一張 `composite_1920x1080.png`、warm engine）

| 路徑 | warm fullDetectMs | tiles | text（抓到的字） |
|---|---|---|---|
| 舊全格 tiling | 2019ms | 8 tiles | `ジ ウ 未熟` |
| **兩段式 region** | **803-979ms** | 1 coarse + **1 tile** | `ジ 未熟 ウ` |

- **延遲 ~2.1-2.5x 快**（Debug 比 Release 慢，Release 全格基準 2386ms → 預估 region ~1000ms）。
- **無文字回退**：兩路抓到的字集合相同 `{未熟, ジ, ウ}`，只是排序不同；`無`/`ョ` 兩路都漏 → 是 warm v6 對微弱直書 kana 的辨識極限，**不是 tiling 路徑造成**（native single-pass 同幀更糟，`未熟 ヨ之`）。
- **首幀**：加 coarse warmup 後，第一張大幀 803ms（cold-compile cliff 消失，16:9）。
- **零崩潰**：log 無 `detector recreated`／`MatMul`／`E_INVALIDARG`。

### 已知限制 / 待辦

- **非 16:9** 擷取首幀仍付一次 coarse-shape DML compile（~1.5s），之後 warm。可加常見比例 warmup。
- **微弱 kana 準確度**是獨立問題（引擎層，side-rescue 範疇），本次只動延遲，未碰。
- **排序**：跨 tile/region 的 reading-order 仍非純上到下（舊全格也一樣），翻譯前若要正確語序需另解。
- **F 的「背景動畫不觸發 full detect」**（trigger gating，非 re-detect 加速）尚未做——本次只交付 F 的「re-detect 只掃 ROI」那半。ROI 跨幀 cache 也還沒做（coarse 每次 full detect 重跑 ~300-500ms，在非熱路徑可接受）。
- **下一步**：republish 進 Release dist（`scripts\publish-exe.ps1`，會備份還原 `dist\Verbeam\data`）→ 真實 getDisplayMedia 連續擷取驗證（短句不再消失）。

---

## 實作結果 2（Claude, 2026-06-23）：F — ROI 鎖定 + 空幀不重掃整螢幕

### 為什麼「框整螢幕 + 上 model 就無法 realtime」

根因不是背景 motion（`RealtimeSuppressLuminanceRedetect=true` 早就擋掉了），是：
- realtime 迴圈 `layout.Lines.Count==0`（畫面上沒鎖到字）→ **立刻 full detect,不受任何 interval 節流**（[RapidOcrNetProvider.cs](app/src/Verbeam.Core/Providers/RapidOcrNetProvider.cs) 的 empty-layout 分支）。
- 影片有大量「沒字幕的瞬間」(場景切換、句間) → **每張空幀都重掃整螢幕**。
- 而兩段式在空幀最慘:coarse 找不到字(~540ms)→ 退回 full-grid 8 tiles(~2019ms)= **每張空幀 ~2.5s**。
- 上 translation model 後 rec(CPU)+ llama.cpp(CPU 執行緒)互搶 → 徹底卡死。

「實作結果 1」只修快了「有字幕」那段,空幀/整螢幕重掃沒動 —— 正是上一輪 defer 掉的 F。

### 改了什麼

- **`RapidOcrRealtimeLayout`** 加 `RoiBands`(sticky 字幕區)+ `LastWholeScreenScanAt`。
- **scoped re-detect**:`FullDetectFrame` 加 `restrictToRoi` 參數 —— 有鎖定 ROI 時只 `DetectLinesInRegions(ROI)`,**不重掃整螢幕**;scoped 時跳過 side-rescue / sparse-rescue(那些會跑額外整幀 DET)。
- **`RecognizeRealtimeFrame`** 算一次 `scopedRoi`,傳給所有 full-detect 觸發點(empty-layout / interval / rec-empty / script-flip / …)。ROI 鎖定時走 scoped,只有(a)冷啟動沒 ROI、或(b)每 `RealtimeWholeScreenRescanIntervalMs`(5s)一次的 drift 重掃才走整螢幕。
- **full-grid fallback 只在冷啟動**(`allowFullGridFallback = 沒有 ROI`)才允許 —— 鎖定後永不付 2s 全格代價。
- **ROI carry-forward**:空幀把上一輪 ROI 帶下去(`previousLayout?.RoiBands`),瞬間空白不會把鎖丟回整螢幕。

### A/B 實測(同 session 連續幀,src Debug + Release dist 都驗)

| # | 幀 | reason | scope | det | 結果 |
|---|---|---|---|---|---|
| 1 | 字幕 | miss | **whole-screen** | ~1000ms | 鎖定 ROI |
| 2 | 空白 | rec-empty | **scoped-roi** | **285-371ms** | 0 lines |
| 3 | 字幕′ | empty-layout | **scoped-roi** | ~587ms | 抓到字幕 |
| 4 | 空白 | rec-empty | **scoped-roi** | ~340ms | 0 lines |
| 5 | 字幕″(+5.5s) | empty-layout | **whole-screen** | ~771ms | drift 重掃 ✓ |

- **空幀 ~2500ms → ~300ms(8x 便宜)** = 核心修復。
- 全程**無 `tiled detect: 8 tiles`**(昂貴 fallback 沒再觸發)。
- ROI 跨空幀存活;5s drift 重掃正常。
- 穩定字幕仍走 incremental(11-21ms);scoped full detect 只在「字幕變動/出現」的轉場觸發。
- 順帶:OCR 文字只在字幕真的變時才變 → translation 請求暴減 → 不再洗爆 LLM。

### 已知 / 待辦
- **字幕移到新位置**:最久 5s(drift 重掃)才會重新鎖到(固定位置字幕無影響)。`RealtimeWholeScreenRescanIntervalMs` 可調。
- 微弱 kana 辨識準確度仍是引擎層獨立問題(本次只動延遲/排程)。
- **真實 getDisplayMedia 連續擷取 + model 開著**的 live 驗證待使用者測。

---

## 環境注意

- dist 是 **single-file self-contained publish**（408MB），不能只換 DLL，要 `scripts\publish-exe.ps1` republish（會備份還原 `dist\Verbeam\data`）。
- src Debug build 比 Release 慢 1.5-2x，A/B 測試要用 Release 才準。
- PowerShell `Invoke-WebRequest` 對 binary body 有執行緒問題，用 `[System.Net.Http.HttpClient]` 發 POST。
- `Start-Process` 跑長期服務會撞 opencode bash tool 的 ChildProcess.kill 清理（誤報，detached 行程會存活）。
- dist appsettings 在 `D:\LocalTranslateHub\app\dist\Verbeam\appsettings.json`，src 在 `D:\LocalTranslateHub\app\src\Verbeam.Api\appsettings.json`，兩邊都要改。
- onnx Python 在 `D:\LocalTranslateHub\app\.ocr-set\venv\Scripts\python.exe`（onnx 1.21.0）。
