# Handoff — 直書日文 OCR：PP-OCR 不行 → manga-ocr + Columnizer 路線

> 目的：把這次對話的判斷鏈、實測數據、下一版設計、以及**所有用到的資源/檔案/指令**寫清楚,讓另一個 session 能直接接手,不用重推。

---

## STATUS (2026-06-29) — §4 步驟 1+2 DONE
dual-polarity mask + layout gate 已實作並驗證(`columnizer.py` 重構成單一來源、harness 可 import;`robustness.py` 擴欄+PASS/FAIL+負樣本+`--no-ocr`;`colz_debug.py` 改雙極性 dump)。**6/7 PASS**(唯一 FAIL=48s視感,屬 deferred furigana)。end-to-end manga-ocr 讀出:5s `未熟　無ジョウ`、69s重い `重い`(原本吐 `．．．`,已修)、teal `そんなところも　割と　嫌いじゃ無い`、rain→reject(原本當文字讀)。
**關鍵設計改動(實測逼出來,偏離原 plan)**:
- polarity **不是**比 black-hat vs top-hat 兩 mask 分數(會把乾淨 5s 評輸給垃圾 top-hat);改用 **Otsu 多數類=背景** 判 luminance(背景亮→dark_on_light、暗→light_on_dark),6/7 正確,只 build 一張 mask(更快)。`_otsu_polarity`。
- component filter **移除 aspect>8 硬丟**(會刪掉橫排 CJK 寬扁筆畫如 重 的橫畫 → polarity 反轉);雨絲改由 **occupancy** 擋。
- 雨/no-text reject **靠 occupancy**(8×8 grid 有墨格比例:真文字 ≤0.44、雨 0.53;`OCC_MAX=0.50`,margin 窄,壞了再放寬),**不靠 text_likeness**(雨也會 align,row_al 0.93)。
- 結果 dict 對齊 §4 schema(polarity/layout/status/columns/split_confidence/mask_quality/reject_reason)。
**§4 步驟 3(Columnizer v2)= 前提作廢,已撤。** 查證(看圖)**127s 其實是單欄**(`そのほうが魅力的でしょ` 直書一欄),handoff 原本的「白框 2 欄/併 1 欄欠分割」是**誤判**;columnizer 輸出 1 欄是對的、manga-ocr 讀對。幾何上也成立:兩不重疊直欄中心 x 距 ≥ 字寬 > eps(0.8字寬),cx-clustering 本來就分得開(5s→2、teal→3),valley-split 只對罕見 chaining 併欄有用、現有 case 全不觸發 → projection-valley 實作了又 revert(YAGNI,真有 chaining 幀再加)。**現況:core 5/5 PASS(5s/127s/69s/teal/rain),raw 6/7(唯一 fail=視感,deferred 且輸出其實乾淨 `視感が`)。step 1+2 已把現有測試集收乾淨。** 下一步見 §7。

## 0. 一句話現況
PP-OCRv6 在**移動直書日文**上結構性 garble(心浦/AE/ウ二A);換 reader = **manga-ocr ONNX**(跨 OS、iGPU 0 dGPU-VRAM、~240ms/欄、讀得對)。但 manga-ocr 是純 recognizer,中間缺一層 **Columnizer(分欄+欄序)**。Columnizer MVP 已驗證(black-hat mask)在「直書深字淺底」成立;robustness 找出破口(白字黑底/橫排/furigana/多區塊)。下一版要把 Columnizer 縮成「只吃單一 vertical-CJK block」,前後補 dual-polarity mask + layout gate + ruby filter,多區塊交回上游 detector。

---

## 1. 判斷鏈(這次對話怎麼走到這)

1. **trace 定位**:未熟 不是被下游 filter 殺,是 **DET 階段**就 garble。t=5-6 raw det 只吐 心浦/AE/E端/ウ二A,從沒出現乾淨 未熟/無ジョウ;filter 正確擋 garble → track-hold 撐舊值;無ジョウ(新欄)被 garble 後 reject → 從沒進 track。
2. **甲/乙 實驗(scale 是不是兇手)**:把 `DetTargetShortSide` 640→1080(native)重跑 → **raw det 照樣 garble**,且 whole-screen relocate 變 0 框(更糟)。→ **乙(angle/decode)確認,scale 不是修法**。已把 config 改回 640(native 反而傷 faint vertical)。
3. **為何 PP-OCR 無法自適應轉向**:管線 = `DBNet 框 → rotate-90 拉平 → 橫向 CRNN REC`。縦書き 每字**直立堆疊**,rotate-90 會把每字**躺平** → REC 必錯;且 DBNet 對直書欄不是切碎就是糊成方塊,連「tall 直框」訊號都給錯 → rotate 啟發式根本不觸發;CLS 只有 0°/180°,沒有 90°/writing-mode/reading-order。**架構盲區,參數繞不過。**
4. **REC 換成 manga-ocr**:跨 OS(否決 oneocr,Win-only)。實測手動 crop 單欄 → 讀對(未熟 ✅/無ジョウ ✅)。按「手動 crop 測 REC」判準 → **我們落在「DET 是瓶頸」那側**。
5. **但 loose 多欄 ROI → manga-ocr 幻覺**:整塊兩欄餵它 → `未熟している無ジョウ`(幻覺接續詞黏接),或 cluster-union → 只讀一欄。→ 必須**一欄一餵**。
6. **缺的層 = Columnizer / Layout Adapter**:把「文字區」轉成「recognizer 能正確讀的 atomic ROI 序列」(這裡 atomic = 單一直書欄)。負責**分欄 + 欄序(右→左)**,不負責找字/判橫直/補 ROI。
7. **Columnizer MVP**:naive 多訊號 mask(adaptiveThreshold 雙極性+local-contrast)在真實動畫背景**爆**(抓皮膚/頭髮/描邊 → 5 個 phantom 欄 + 幻覺 + 假 split_conf=1.0)。換 **black-hat 形態學**(MORPH_BLACKHAT 15px ellipse + Otsu)→ 乾淨 2 欄,背景全忽略 → `未熟　無ジョウ`,零幻覺,R→L 正確,~240ms/欄(iGPU)。
8. **robustness(6 case)**:見 §3。核心 case 穩;破口 = 白字黑底/橫排/furigana/緊貼雙欄/ROI 不完整/多區塊。

---

## 2. 硬數字(實測)

**manga-ocr-torchless(`mayocream/manga-ocr-onnx`,ViT enc + GPT dec,greedy decode 無 KV-cache)**
- 權重 440MB fp32(decoder 327 + encoder 112);process RAM peak ~978MB / 穩態 ~711MB。
- 延遲(median ms / 2-4 字欄,warm):**CPU ~570 / iGPU-DML ~240 / dGPU-DML ~265**。
- VRAM(nvidia-smi delta 實測):**iGPU device0 = 0 MiB dGPU**(權重進共享系統 RAM)/ **dGPU device1 = ~240 MiB** dedicated / CPU = 0。
- **iGPU 跟 dGPU 一樣快、甚至更快**(模型小+autoregressive=dispatch-bound)→ **沒理由放 dGPU**(吃 VRAM 又跟 LLM 搶)。
- 準確度:讀對 rapidocr garble 的直書欄;唯一漏 = t=2 極淡 fade-in(→ええ,那是 temporal gating 的事)。
- **DML device 對應**(本機):device 0 = iGPU(Iris Xe),device 1 = NVIDIA dGPU(RTX 3050 Ti 4GB)。dGPU 實機常態已用 ~3672/4096(llama-server + Verbeam.Api OCR det),只剩 ~291 free。

---

## 3. Robustness 結果(6 個真實 ROI)

| case | 版面/底色 | Columnizer | manga-ocr | 判定 |
|---|---|---|---|---|
| 5s 未熟/無ジョウ | 直書/淺底 | 2 欄 ✅ | `未熟　無ジョウ` | ✅ PASS |
| 48s teal 3 欄 | 直書/彩色 | 3 欄 ✅ | `そんなところも　割と　嫌いじゃ無い` | ✅ PASS |
| 127s 白框 ~~2 欄~~ **單欄** | 直書/白框 | 1 欄 ✅(看圖確認:本來就單欄,非欠分割) | `そのほうが魅力的でしょ` | ✅ PASS(原判 ⚠️ 為誤診) |
| 48s 白框 2 欄 | 直書/白框 | 2 欄 ✅ | `何がそ　不満`(我 ROI 切短) | ⚠️ ROI 不完整 |
| 48s 視感+furigana | 直書/彩色 | 2 欄 | `視感　．．．`(ルビ變垃圾欄) | ❌ furigana |
| 69s 重い | **橫排/白字黑底** | 3 欄(雨點雜訊) | `．．．　．．．　．．．` | ❌❌ 全爆 |

**核心穩**:直書深字淺底(單/多欄/彩色底)。**破口** = 白字黑底(佔比高,top-hat 可補)、橫排、furigana、緊貼雙欄、ROI 不完整、多區塊。

---

## 4. 下一版設計(把 Columnizer 縮窄,前後補層)

```text
Block ROI
  ↓
make_text_mask_dual_polarity()      # black-hat + top-hat,選 text-likeness 高的極性
  ↓
component_filter()                  # 去雨絲(極端長寬比/低 fill/觸邊長線)
  ↓
layout_gate()                       # 直/橫/noise 分類 —— 必須在分欄之前
  ├─ no_text / noise → reject
  ├─ horizontal      → 直接 OCR(不分欄)
  └─ vertical_cjk    → columnize()
                          ↓ adaptive eps + projection-valley split(不只靠 eps)
                       ruby_filter()        # 小字附屬欄 → 丟棄/標 metadata
                          ↓
                       per_column_manga_ocr()
                          ↓
                       join_by_vertical_rl_order()
```

**Columnizer 職責縮窄**:只吃「單一 vertical-CJK text block」,只做分欄+欄序。**不**做:找整圖文字、判橫直、補 ROI、從雨點找字、跨區塊分欄。

### 失敗 → 工程層級 → 修法
| 問題 | 缺的層 | 修法 |
|---|---|---|
| 白字黑底爆 | polarity-aware mask | top-hat + black-hat 雙分支(選 text-likeness 高者,別無腦 OR) |
| 橫排被當直排 | layout gate | 直/橫分類先於 Columnizer |
| furigana 變垃圾欄 | ruby filter | 小字附屬欄偵測 → 丟棄/metadata |
| 緊貼雙欄併一欄 | adaptive split | eps 自適應 + projection valley veto(谷夠深就切,即使 cluster 併了) |
| ROI 切短 | upstream localizer | Columnizer 不補 ROI |
| 整幀多區塊亂切 | block detector | Columnizer 只吃單區,多區塊回 reject |

### 實作順序(CP 值)
1. ✅ **dual-polarity mask** DONE(改用 Otsu luminance polarity,非 dual-mask 分數比;見 STATUS)。
2. ✅ **layout gate**(直/橫/noise)DONE。重い→horizontal、未熟/teal/白框→vertical、rain→reject 全過。
3. ~~**Columnizer v2**~~ MOOT/REVERTED(127s 誤診為 2 欄,實為單欄;valley-split 現有 case 不觸發。真有 chaining 併欄幀再做)(adaptive eps + projection valley + split_confidence)。完成條件:127s 白框不再併 1 欄,5s/teal 維持正確,不過切。
4. **ruby filter**。完成條件:視感 主輸出不含 `．．．`,ルビ 標 ignored。
5. **multi-block reject**。完成條件:整張 48s → 回 `multi_block_roi`,不跨螢幕分欄。

### 結果資料結構(別只回 string)
```json
{
  "roi_id":"frame_48_teal","polarity":"dark_on_light","layout":"vertical_rl",
  "status":"ok","split_confidence":0.91,
  "columns":[
    {"order":0,"bbox":[...],"type":"main","text":"そんなところも","ocr_confidence":0.88},
    {"order":1,"bbox":[...],"type":"main","text":"割と","ocr_confidence":0.86},
    {"order":2,"bbox":[...],"type":"main","text":"嫌いじゃ無い","ocr_confidence":0.84}
  ],
  "joined_text":"そんなところも　割と　嫌いじゃ無い"
}
```
- furigana 欄:`{"type":"ruby","attached_to":0,"ignored":true}`
- 橫排:`{"layout":"horizontal_ltr","status":"bypass_columnizer","text":"重い"}`
- 多區塊:`{"status":"reject","reason":"multi_block_roi"}`(reject 正確 = PASS)

### 下一輪 benchmark 表新增欄位
`polarity / layout_gate / mask_quality / split_confidence / ruby_detected / reject_reason`。reject 也算 PASS(只要 reason 正確)。

### text-likeness score(別只看像素數,否則雨點贏)
看:component size 一致性、對齊、thin-streak penalty、ink density range、text-like cluster count。雨絲過濾:極端長寬比(>8 或 <1/8)、面積小且孤立、觸邊長線、方向一致但不成字列的 streak。

---

## 5. 資源清單(移植用)

### 5.1 Bench 工作區
`D:\LocalTranslateHub\.codex-run\manga-ocr-bench\`
| 檔案 | 用途 |
|---|---|
| `venv\` | 隔離 venv(見 5.2) |
| `columnizer.py` | **Columnizer MVP**:black-hat mask → CC filter → x-center 分群 → R→L → 逐欄 manga-ocr 驗證。核心要移植的就是這支的 `make_ink_mask`/`columnize` |
| `robustness.py` | 6 case robustness(自帶 columnize 複本 + jp_ratio)。下一版照 §4 表擴欄 |
| `bench_mangaocr.py` + `run_bench.ps1` | VRAM/速度 bench(EP=cpu/dml、device 0/1)。RAM 用修好的 ctypes GetProcessMemoryInfo 自報;dGPU VRAM 用 nvidia-smi delta |
| `smoke.py` | CPU 準確度 smoke(逐 crop 印讀出) |
| `loose_roi.py` | loose 多欄 ROI 幻覺測試(證明要分欄) |
| `colz_debug.py` | 存 mask + 欄框 overlay 給肉眼看(調 mask 必用) |
| `contact_sheet.py` → `contact.png` | 全片 6×8 縮圖找文字幀 |
| `extract_rois.py`/`crop_columns.py`/`grab4.py` | 從影片切測試幀/欄/全幀 |
| `rois\` | 所有切好的幀、band、單欄 crop、dbg overlay/mask |

### 5.2 venv 重建
```bash
python -m venv venv                       # 用 miniconda python 3.12
venv\Scripts\python -m pip install manga-ocr-torchless      # 0.2.0(torchless,不裝 torch)
venv\Scripts\python -m pip uninstall onnxruntime -y         # 換成 DML 版
venv\Scripts\python -m pip install onnxruntime-directml     # 1.24.4(含 CPU + Dml EP)
venv\Scripts\python -m pip install opencv-python-headless   # 4.13.0
# 依賴帶進:transformers / huggingface_hub / jaconv / pillow / numpy
```
EP 選擇:`MangaOcr(force_cpu=True)` = CPU;要 DML 需自建 session 傳 `providers=[("DmlExecutionProvider",{"device_id":0|1}),"CPUExecutionProvider"]`(class 本身不暴露 device_id,bench_mangaocr.py 是換掉它的 encoder_session/decoder_session)。

### 5.3 模型
- HF repo `mayocream/manga-ocr-onnx`(encoder_model.onnx 112MB + decoder_model.onnx 327MB)。
- 快取:`C:\Users\ken\.cache\huggingface\hub\models--mayocream--manga-ocr-onnx`(Windows 無 symlink,blob 在 blobs\)。
- ⚠️ greedy decode **無 KV-cache**(`__call__` 每 token 重餵整個 input_ids)→ 延遲隨欄長線性。長欄(10-15 字)~0.5-0.9s/欄。日後換 `decoder_with_past` export 可砍半。**務必配 temporal cache,別每幀跑**。

### 5.4 測試影片(text-heavy 動畫 MV,絕佳測試集)
`D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4`(1920×1080,24fps,253s,6080 frames)
關鍵幀(full-frame ROI 座標 l,t,r,b):
- 5s 未熟/無ジョウ(直書淺底):band `(1330,120,1760,820)`,單欄 crop 在 `rois\col_*.png`
- 48s 散落多區塊(teal 3欄 `(30,300,590,850)`、白框2欄 `(1175,90,1335,470)`、視感+furigana `(1520,360,1810,770)`)
- 69s 重い(橫排白字黑底)`(690,400,1230,690)`
- 127s 白框2欄 そのほうが/魅力的でしょ `(845,140,1090,945)`
- 更多文字幀:contact.png(11/16/21/26/32/37/42/53/58/63/69/74/79/84/121/127/132/137/142/148/153/158/164/169/174/179/185/190/206/243/248s…多為直書,約半數白字黑底)

### 5.5 rapidocr 端 trace 工具(診斷 PP-OCR 用,已完成)
`D:\LocalTranslateHub\.codex-run\fp-validation\`
- `trace_probe.py`(t=0..7 連續 session POST /ocr;`VB_PROBE_SERVER` env 可改 port)
- `parse_trace.py`(讀 `trace_t6.jsonl`,印各 stage raw/filter/store + 標 未熟/無ジョウ)
- env `VB_OCR_REALTIME_TRACE_PATH` → trace 檔;server 端 `RapidOcrNetProvider.cs` 的 `TraceRealtimeFullDetect*` 寫入

### 5.6 dev server(若要重跑 trace)
```bash
$env:VB_OCR_REALTIME_TRACE_PATH="...\trace_t6.jsonl"
dotnet run --project D:\LocalTranslateHub\app\src\Verbeam.Api --launch-profile http
```
- ⚠️ 實際綁 **5768**(appsettings 頂層 `"Urls":"http://localhost:5768"` 蓋過 launchSettings 的 5069)。health = `/health`,warmup ~4s 才綠。
- ⚠️ `dotnet run` 可能撞 bin\Debug 的 stale incremental ghost(報不存在的 CS0120);先跑乾淨 `dotnet build ... -p:OutputPath=bin\verify\` 確認 source 能編。
- ⚠️ venv python.exe 是 launcher shim,外部 Get-Process by PID 量 RAM 會讀到 4MB(錯進程);RAM 要在 python 內用 ctypes 量。

### 5.7 主程式整合點(日後接進真 pipeline)
- OCR 即時路徑:`app\src\Verbeam.Core\Providers\RapidOcrNetProvider.cs`(realtime incremental + FullDetectFrame)。
- config:`app\src\Verbeam.Api\appsettings.json` → `Verbeam:Ocr:RapidOcrNetV6`(**DetTargetShortSide=640,別動**;native 更糟)。
- 規劃:manga-ocr 包成 ONNX provider 跑 **iGPU(device 0)**;Columnizer 接在 PP-OCR 定位之後、manga-ocr 之前;PP-OCR 降級成「橫排 reader / ROI 定位」;加 temporal cache(欄穩定 2-3 幀才讀)。
- ⚠️ oneocr routing fallback 仍在(`OneOcrProvider`,Win-only)——是目前唯一偶爾讀對移動幀的東西,但**不是跨 OS 答案**,別依賴。

---

## 6. 不要再走的死路(省下次的時間)
- ❌ 調 `DetTargetShortSide` / angle flag / filter 門檻 救 PP-OCR 直書(架構盲區,證實無效)。
- ❌ 把 manga-ocr 放 dGPU(沒更快,還吃 VRAM 跟 LLM 搶)。
- ❌ 整塊多欄/loose ROI 直接餵 manga-ocr(幻覺接續詞)。
- ❌ naive 多訊號 mask(adaptiveThreshold 雙極性)在動畫背景(抓背景)。用 **black-hat / top-hat 形態學**。
- ❌ 把 polarity/layout/noise/furigana/多區塊全塞進分欄邏輯(要拆成前後獨立層)。
- ❌ 急著 train detector(先把上面 4 個便宜層補完;多區塊才需要 detector)。

## 7. 下一步(接手就做)
~~(1) dual-polarity mask → (2) layout gate~~ ✅ DONE。~~(3) Columnizer v2~~ 作廢(127s 誤診,見 STATUS)。**現有 6+1 測試集 step 1+2 已全收乾淨(core 5/5)**,(4) ruby filter / (5) multi-block reject 在目前 case 也**沒有失敗輸出**(視感 bypass 後讀成乾淨 `視感が`、無多區塊幀)。
**進行中 = 擴測試集(不接主程式)**。基建:`robustness.py` 資料驅動(讀 `cases.py` 的 dict list,欄位 layout/polarity/cols/status/reason/deferred/category;只 assert 有給的欄位);summary = raw + core(非deferred) + 分類別。**新工具 `gridframe.py`**(影格疊像素座標格線)——肉眼從縮圖猜 ROI 不可靠(已踩坑:37s 猜偏 280px、132s 猜偏 160px、201s 把有字的當無字),**新增 case 一律先 gridframe 讀座標**。
**20 cases,core 15/15、raw 16/20**(5 deferred = 真實 gap backlog,非作弊)。分類別:dark-vert 5/6、color-vert 6/6、white-horiz 2/2、negative 3/3、furigana 0/1、multiblock 0/2。2026-06-29 GAP-A 後更新: `component_filter()` 已加 asymmetric tall-streak filter + `drop_tall`/`n` debug；21s 與 132s 的原 deferred ROI 也經 `gridframe.py` 校正(舊 21s 框只包到文字左側雨絲；舊 132s 框只切到文字邊緣)。2026-06-29 GAP-B 後更新: `make_text_mask_dual()` 已改成 gray/Otsu primary + conservative Lab local-delta fallback，`mask_dbg.source` 會標 `gray_otsu` / `lab_delta`，`colz_debug.py` 會輸出 `graymask` / `labmask` / `chosenmask` / `overlay`。purple/blue/pink 三個 color-vert case 已移回 core，但重要 caveat: 這三個的舊 ROI 本來就錯位/截斷；gridframe 校正後目前都由 `gray_otsu` 通過，不是現有測試集實際觸發 `lab_delta`。
**OCC 觀察(更多資料)**:真文字 occ ≤0.44(含大雨 137s=0.2)、紋理負樣本 ≥0.53,margin 守得住;且雨/雲紋理走 occ-high 被擋。

### Gap backlog(測試集驅動,依優先序;每個都有對應 deferred case)
- ✅ **GAP-A DONE 直向雨絲/刮痕被當欄**。已實作非對稱 aspect 過濾:丟 tall-thin(h/w>=8 且夠高夠窄),留 wide-thin(w/h 大的橫筆畫),並在 `mask_dbg` 輸出 `drop_tall`/`n`。驗收: `robustness.py --no-ocr` 與完整 `robustness.py` 皆為 raw 13/20、core 12/12；21s→1欄,132s→1欄；69s 重い、5s、teal、rain/no-text 零回歸。注意: 這輪也修正了 21s/132s 兩個錯位 ROI,所以後續不要用舊座標重判 GAP-A。
- ✅ **GAP-B 彩色菱形上的清楚文字被判 no_text**。已實作 Lab local color-delta fallback，且 purple `(500,460,700,850)`、blue `(520,80,660,500)`、pink `(300,90,440,480)` 經 gridframe 校正後皆 PASS 並移回 core。驗收: `robustness.py --no-ocr` 與完整 `robustness.py` 皆為 raw 16/20、core 15/15。注意: 這三個 corrected ROI 目前 source 都是 `gray_otsu`；舊 ROI 分別是截半截或背景，後續不要用舊座標重判 GAP-B。Lab fallback 留作真正灰階失敗時的保守備援，別為了強行觸發它去放寬 OCC/GATE。
- **GAP-C 同框混合字級**(37s 飽きた大+もう自己顕示小)→ layout=unknown + 過切成 3。修法=layout gate / split 對字級差容忍。
- **GAP furigana**(視感,step 4)、**GAP multi-block**(48s/42s 整幀,step 5)。

下一步:優先 GAP-C mixed glyph sizes(37s) 或 furigana / multi-block；不要再先擴測試集或接主程式。擴更多 ROI 時每個先用 gridframe 驗座標；本片白字多為**橫排**(69s/137s),白字**直書**疑似不存在於此源,別硬湊。
