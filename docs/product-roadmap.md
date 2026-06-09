# 產品路線：即時螢幕語境翻譯器

YomiBridge 的長期定位不是單純的「OCR 翻譯 API」，而是「即時螢幕語境翻譯器」：在不打斷玩家沉浸感的前提下，記住術語、角色、劇情脈絡與使用者修正，讓翻譯越用越一致、越用越貼近作品語氣。

這份文件描述產品方向與分期路線。它不是目前功能清單；凡是標在未來階段的能力，都代表尚未實作。

## 目前狀態

目前 YomiBridge 已經是一個可用的本地翻譯 gateway，重點放在 MORT Custom API 相容與本地模型翻譯。

已具備：

- MORT 相容的 `POST /translate`，回傳 `result`、`errorCode`、`errorMessage`。
- `ollama` 與 `mock` provider。
- prompt presets，用來針對遊戲對話、漫畫、字幕、技術文件等場景調整翻譯提示。
- glossary 檔案，讓使用者手動維持固定術語。
- SQLite translation cache，避免同一組輸入重複呼叫模型。
- 手機/平板 viewer 與 WebSocket broadcast，讓玩家可以不用在主螢幕掛 overlay。

尚未具備：

- 桌面截圖、框選區域、overlay 視窗。
- RapidOCR / ONNX Runtime 即時 OCR pipeline、PP-OCRv5 辨識核心、VLM-OCR fallback。
- frame diff、文字框快取、detection 降頻等逐幀最佳化。
- RAG 記憶庫、向量檢索、使用者修正回寫。
- 歷史對話壓縮、場景摘要、角色語氣記憶。
- DeepSeek 或其他雲端模型 provider。
- 自動模型路由、困難句升級、成本監控。

## 核心產品想法

傳統即時 OCR 翻譯常見問題不是只有 OCR 錯字，而是翻譯缺少記憶：

| 問題 | 影響 |
| --- | --- |
| 同一角色名稱前後翻譯不同 | 劇情閱讀割裂 |
| 專有名詞亂翻 | 世界觀不穩定 |
| 同一句重複翻譯 | 延遲與成本上升 |
| 上下文斷裂 | 對話語氣不自然 |
| 使用者修正不能被學習 | 每次都要重新手動修 |

YomiBridge 的護城河應該是應用層記憶，而不是單一模型：

```text
OCR 新文字
-> 相似度去重
-> Translation Cache
-> 術語/記憶檢索
-> 最近對話與場景摘要
-> 模型翻譯
-> 回寫快取與記憶
-> Overlay 或手機/平板顯示
```

## 第一階段：強化後端翻譯 Gateway

目標是在不引入桌面 OCR app 的前提下，把目前的 MORT API 後端整理成穩定、可擴充的翻譯核心。

建議能力：

- 保持 `POST /translate` 與 MORT 相容，不破壞既有使用方式。
- 強化 translation cache 的可觀測性，例如 cache hit、latency、provider、model。
- 讓 glossary 與 presets 更容易被遊戲 profile 使用。
- 保持 provider/model config-driven，避免把模型名稱寫死在程式流程裡。
- 文件化本地模型、未來雲端模型與成本/隱私取捨。

第一階段完成後，YomiBridge 應該是可靠的「本地翻譯 gateway」，可以接 MORT、OCR 工具或其他上游文字來源。

## 第二階段：語境記憶與 RAG

目標是讓翻譯不再只看單句，而是能取用「查得到的記憶」與「當前劇情脈絡」。

詳細工程設計見 [RAG 實作計畫](rag-implementation-plan.md)。

建議記憶類型：

| 記憶類型 | 用途 | 範例 |
| --- | --- | --- |
| 術語記憶 | 統一角色、地名、技能、道具譯名 | `魔導炉` -> `魔導爐` |
| 翻譯記憶 | 保存已確認的原文與譯文 | 同句命中直接回傳 |
| 場景記憶 | 保存目前章節或場景摘要 | 主角正在王都尋找妹妹 |
| 角色語氣 | 提供說話風格提示 | 莉娜：直接、偶爾諷刺 |
| 使用者修正 | 把手動修正寫回記憶 | 這個名字不要翻譯 |
| OCR 修正記憶 | 修正常見 OCR 變體 | `グランぺル` -> `グランベル` |

RAG 負責「查得到的記憶」：

- 這個角色以前怎麼翻？
- 這個地名是不是固定譯名？
- 這句話以前有沒有出現過？
- 使用者是否修正過這個詞？

歷史壓縮負責「當前語境」：

- 現在劇情在哪個場景？
- 目前有哪些活躍角色？
- 上一句對話在吵什麼？
- 這句話應該是諷刺、命令還是安慰？

翻譯 prompt 應該只送必要上下文，而不是把全部歷史丟給模型。

```text
【最近對話】
A: ...
B: ...

【場景摘要】
主角剛進入王都，正在尋找失蹤的妹妹。

【檢索到的術語】
王都グランベル = 格蘭貝爾王都
魔導炉 = 魔導爐

【本次 OCR 原文】
もういい、勝手にしなさい。
```

第二階段完成後，YomiBridge 應該能提供「一致、有記憶、可修正」的語境翻譯。

## 第三階段：OCR 與 App 層體驗

目標是從翻譯 gateway 往完整即時螢幕翻譯工具前進。

建議能力：

- 桌面截圖與框選 OCR 區域。
- frame diff / change detection，畫面沒變就不 OCR。
- OCR stabilizer，多幀投票降低錯字。
- duplicate detector，相似文字不重翻。
- overlay 顯示與無干擾手機/平板 viewer 並存。
- 多遊戲 profile，保存不同作品的 glossary、記憶與顯示設定。

### OCR 引擎策略

OCR 路線應該拆成兩層：即時主力 pipeline 與高精度 VLM-OCR 備援。逐幀翻譯不能把重型 VLM 當主力，否則延遲、顯存與成本都會先失控。

即時主力建議：

- 以 `RapidOCR + ONNX Runtime` 作為第一候選，因為它把 PaddleOCR 模型轉成 ONNX，適合跨平台部署，也比較容易接進 .NET / C#。
- 辨識核心優先評估 PP-OCRv5 / PP-OCRv5 mobile。PP-OCRv5 的語言覆蓋對繁中、簡中、日文、英文與拼音場景友善；mobile 變體可作為低延遲優先選項。
- Windows 加速路線先保證 ONNX Runtime CPU 可跑，再把 DirectML / WinML 作為可選 GPU 加速；不要讓 CUDA 成為基本門檻。

高精度 / 結構模式建議：

- 數學式與表格不是單純文字辨識，而是結構辨識。逐行 OCR 只會輸出文字流，不能可靠保留上下標、分數、矩陣、row/column、合併儲存格或閱讀順序。
- PaddleOCR-VL 應列為結構 OCR 首選候選，處理 formula / table / chart / document region；PP-StructureV3、Pix2Text 可作為較輕的本地候選；Mathpix、Google Cloud Vision、DeepSeek-OCR / VLM OCR 則先列為需要 API 或外部 runtime 的候選。
- dots.ocr 可列入文件 OCR 比較，但表格重的內容不應把它當首選。
- VLM-OCR 不應用於預設逐幀 OCR，除非未來有明確的本機延遲、顯存與成本測試證明可以穩定即時。
- 低信心 OCR、公式、複雜表格、特殊版面、手寫或嚴重扭曲畫面，可以作為升級到高精度 / 結構模式的觸發條件。
- 公式區塊應 bypass 翻譯並以 LaTeX / 結構表示原樣重建；表格應保留 grid 結構，只翻譯可翻譯 cell 文字，數字、符號與 layout 不應交給一般 `/translate` 整塊改寫。

| 場景 | 建議路線 | 理由 |
| --- | --- | --- |
| 即時逐幀 | RapidOCR + ONNX Runtime + PP-OCRv5 mobile 候選 | 延遲最低，最適合遊戲字幕與短句 |
| 低信心 OCR 修正 | 多幀投票、OCR correction memory，必要時升級 VLM-OCR | 先用便宜方法修正，再用高精度備援 |
| 複雜文件/表格/公式 | PaddleOCR-VL、PP-StructureV3、Pix2Text 候選 | 需要結構化中間表示，不能只輸出逐行文字 |
| 高精度單幀 | 使用者手動觸發結構 OCR / VLM-OCR | 可接受秒級延遲，換取更完整解析 |

逐幀工程策略：

- `frame diff`：目標區域像素沒有明顯變化時，不觸發 OCR。
- `detection cache`：文字框短時間快取，不必每幀重新偵測。
- `detection 降頻`：偵測階段低頻跑，辨識階段依畫面變化高頻跑。
- `recognition 優先逐幀`：文字框穩定時，只對裁切文字區做 recognition。
- `相似度去重`：OCR 結果與上一句高度相似時，沿用翻譯或只做局部更新。

第三階段完成後，YomiBridge 才會接近完整的「即時螢幕語境翻譯器」。

## 模型策略

模型不是產品護城河，模型路由與記憶結構才是。

建議原則：

- 本地模型優先，維持隱私與離線能力。
- 雲端模型作為可選 provider，不應成為預設依賴。
- 簡單短句走低延遲模型。
- 難句、劇情摘要、低信心 OCR 修正才升級到較強模型。
- provider、base URL、model name 都保持 config-driven。
- Context Caching 可降低固定前綴成本，但不能取代 RAG 記憶。

DeepSeek 可作為未來雲端 provider 選項之一。截至 2026-06-09，DeepSeek 官方文件列出 `deepseek-v4-flash` 與 `deepseek-v4-pro`，並標示 `deepseek-chat`、`deepseek-reasoner` 將於 2026-07-24 deprecated。實作時仍應以官方文件為準：

- [DeepSeek API: Your First API Call](https://api-docs.deepseek.com/)
- [DeepSeek API: Change Log](https://api-docs.deepseek.com/updates/)

## 分期摘要

| 階段 | 目標 | 不做什麼 |
| --- | --- | --- |
| 第一階段 | 穩定翻譯 gateway、cache、glossary、provider 設定 | 不做桌面 OCR app |
| 第二階段 | RAG 記憶、場景摘要、使用者修正回寫 | 不把全部歷史塞進 prompt |
| 第三階段 | OCR pipeline + VLM 高精度模式 + 桌面/app 體驗 | 不讓 VLM-OCR 成為逐幀主力 |

## 成功標準

長期來看，YomiBridge 應該同時做到：

- 低延遲：短句能快速回應，不拖慢遊戲節奏。
- 低成本：cache、去重與路由先行，不每句都呼叫昂貴模型。
- 一致性：角色、地名、技能、道具前後譯名一致。
- 可修正：使用者修正能回寫，下一次自動套用。
- 無干擾：主螢幕不必掛 overlay，也能用手機/平板看翻譯。
- 可開源：核心功能能用本地模型與本地資料庫跑起來。

一句話定位：

```text
YomiBridge 是一個會記得術語、劇情與修正的即時螢幕語境翻譯器。
```
