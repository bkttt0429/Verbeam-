# RAG 實作計畫

LocalTranslateHub 的 RAG 目標不是讓模型讀完整歷史，而是在每次即時翻譯前，用很小、可控、可追蹤的上下文提醒模型：

- 固定術語怎麼翻。
- 角色、地名、技能、道具以前怎麼處理。
- 使用者修正過什麼。
- 最近幾句對話與目前場景是什麼。
- OCR 常見錯字要怎麼修。

RAG 必須服務即時翻譯，所以它的第一原則是低延遲。檢索資料要短，prompt 要短，任何慢任務都要放到背景處理。

若未來開放共用 RAG、共用資料庫、外部文件匯入或多人 profile，必須先落地 [RAG 與共用資料庫安全設計](rag-security-design.md)。RAG 檢索內容只能被視為資料，不能被視為指令；安全邊界要放在寫入、檢索、prompt 組裝、輸出驗證與稽核各層。

## RAG 用在哪裡

目前 `/translate` 流程是：

```text
MORT/OCR text
-> TranslationService
-> exact translation cache
-> preset + glossary
-> provider
-> SQLite cache
-> MORT response / viewer broadcast
```

加入 RAG 後，流程應改成：

```text
MORT/OCR text
-> normalize + profile/session
-> user-verified exact memory
-> static glossary
-> RAG retrieval
-> generated exact/fuzzy cache lookup with context hash
-> provider prompt
-> cache result
-> write conversation event
-> async memory maintenance
-> MORT response / viewer broadcast
```

RAG 不改 MORT contract。MORT 還是只呼叫：

```text
POST /translate
```

LocalTranslateHub 在內部把 OCR 文字補上記憶上下文，再送給 provider。MORT、viewer、未來桌面 OCR app 都共用同一個翻譯核心。

## 什麼不該用 RAG

不要把 RAG 當成萬能 prompt 歷史：

- 不要每次塞完整對話紀錄。
- 不要把整份 glossary 全部塞進 prompt。
- 不要讓向量 embedding 成為每次翻譯的硬依賴。
- 不要在即時路徑上做大型摘要、術語抽取或雲端模型分析。

即時翻譯需要的是「少量高信號記憶」，不是大段資料。

## DeepSeek V4 壓縮對本計畫的意義

DeepSeek V4 的 Compressed Sparse Attention (CSA) 與 Heavily Compressed Attention (HCA) 是模型層的長上下文架構：CSA 用較低壓縮率的長程池加 indexer 挑選相關 compressed entries，HCA 用更高壓縮率提供全域訊號。這類設計解的是 1M token 等級長上下文的 KV cache 與注意力成本，不是 LocalTranslateHub 這種刻意把即時 RAG context 壓在 400 到 800 tokens 的問題。

因此第一版不應把 DeepSeek V4 的 attention 壓縮當成實作方向，也不應為了它增加 prompt 或 runtime 複雜度。真正可借用的是概念上的記憶分層：

- 最近數句保留完整細節，對應 sliding window。
- 目前場景摘要保留短期全域訊號，對應較粗的 compressed view。
- 章節或劇情線摘要只在背景更新，作為更長期的低頻上下文。
- RAG retrieval 只挑本句需要的少量條目，不把長歷史交給模型自行注意。

參考：Hugging Face Transformers 的 [DeepSeek V4 文件](https://huggingface.co/docs/transformers/model_doc/deepseek_v4) 與 NVIDIA Megatron Bridge 的 [DeepSeek V4 架構摘要](https://docs.nvidia.com/nemo/megatron-bridge/nightly/models/deepseek/deepseek-v4.html)。

## 記憶分層

### 1. Static Glossary

現有 `glossaries/*.json` 保留，定位是使用者手動管理的固定字典。

用途：

- 確定性最高。
- 適合作品專名、角色名、地名。
- 可以在 cache key 中用 `glossary.Hash` 表示版本。

Static glossary 應該先於向量 RAG 使用，因為它便宜、穩定、可預期。

### 2. Dynamic Memory

動態記憶由 LocalTranslateHub 寫入 SQLite。

建議類型：

| 類型 | 用途 | 來源 |
| --- | --- | --- |
| `term` | 術語、角色、地名、技能、道具 | 使用者新增、匯入、未來自動抽取 |
| `translation` | 已確認原文與譯文 | 使用者修正、人工確認 |
| `ocr_correction` | OCR 錯字修正 | 使用者修正、穩定器累積 |
| `style` | 角色語氣、作品翻譯風格 | 使用者設定 |
| `scene_summary` | 目前章節/場景摘要 | 背景摘要任務 |

### 3. Recent Session Context

最近對話不一定需要向量化。它更像 session ring buffer，並且應明確拆成多層：

建議每次 prompt 最多放：

- 最近 3 到 5 條 OCR/翻譯，保留完整細節。
- 目前場景摘要 1 段，描述正在發生的事件與語氣。
- 章節或劇情線摘要 0 到 1 段，只在確實有助於消歧時放入。
- 活躍角色或 speaker hint，如果未來 OCR/app 能提供。

最近數句走即時 ring buffer；場景摘要與章節摘要由背景任務產生。prompt 預設只放最近數句與目前場景摘要，章節摘要要有 token budget 才加入。

## SQLite 資料結構

第一版先用 SQLite。向量檢索可以先用 in-memory cosine scan，之後再換 `sqlite-vec` 或其他向量擴充。

### `memory_items`

```sql
CREATE TABLE memory_items (
    id TEXT PRIMARY KEY,
    profile_id TEXT NOT NULL,
    type TEXT NOT NULL,
    source_language TEXT NOT NULL,
    target_language TEXT NOT NULL,
    source_text TEXT NOT NULL,
    target_text TEXT NOT NULL,
    note TEXT NOT NULL DEFAULT '',
    priority INTEGER NOT NULL DEFAULT 0,
    confidence REAL NOT NULL DEFAULT 1.0,
    tags_json TEXT NOT NULL DEFAULT '[]',
    metadata_json TEXT NOT NULL DEFAULT '{}',
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    last_used_at TEXT,
    use_count INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_memory_items_lookup
ON memory_items(profile_id, type, source_language, target_language, is_active);
```

`profile_id` 對應遊戲或作品。沒有 profile 時使用 `default`。

`metadata_json` 應保留來源資訊，例如 `origin = user-verified | imported | auto-extracted`。背景整併與衝突解決用這個欄位決定優先順序，不需要第一版新增欄位。

### `memory_embeddings`

```sql
CREATE TABLE memory_embeddings (
    memory_id TEXT NOT NULL,
    embedding_model TEXT NOT NULL,
    dims INTEGER NOT NULL,
    vector BLOB NOT NULL,
    content_hash TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (memory_id, embedding_model),
    FOREIGN KEY(memory_id) REFERENCES memory_items(id)
);
```

`vector` 可以用 float32 little-endian BLOB。第一版資料量小時，啟動時讀入記憶向量到記憶體做 cosine similarity 即可。

資料量成長後再加一層粗掃表示，不必一開始引入外部向量資料庫：

- 儲存 int8 或 binary quantized vector 做快速粗掃。
- 對粗掃 top-k 再用 float32 vector 重算 cosine。
- 若使用支援 Matryoshka / MRL 的 embedding model，可另存 256 維向量做熱路徑快掃。

### `conversation_events`

```sql
CREATE TABLE conversation_events (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    source_language TEXT NOT NULL,
    target_language TEXT NOT NULL,
    source_text TEXT NOT NULL,
    translated_text TEXT NOT NULL,
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    cache_hit INTEGER NOT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX idx_conversation_events_recent
ON conversation_events(session_id, created_at);
```

### `scene_summaries`

```sql
CREATE TABLE scene_summaries (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    summary_text TEXT NOT NULL,
    start_event_id TEXT NOT NULL,
    end_event_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
```

## Core 服務設計

### `IRagMemoryStore`

負責讀寫記憶。

```csharp
public interface IRagMemoryStore
{
    Task<IReadOnlyList<RagMemoryItem>> SearchTermsAsync(RagQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<RagMemoryItem>> SearchSimilarAsync(RagQuery query, CancellationToken cancellationToken);
    Task AddOrUpdateAsync(RagMemoryItem item, CancellationToken cancellationToken);
    Task RecordUseAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken);
}
```

第一版 `SearchTermsAsync` 用 normalized exact / substring / lexical FTS，作為 deterministic 熱路徑，不依賴 embedding。`SearchSimilarAsync` 才使用 dense embedding，作為可 timeout 的二線增強。

中日文專名召回不要只靠 dense。建議之後用 RRF (Reciprocal Rank Fusion) 把 lexical 與 dense 結果合併：

- lexical：SQLite FTS5，優先評估 trigram；若環境不支援，再退回 unicode61 / substring。
- dense：本地 embedding provider，超時或未啟用時直接回到 lexical 結果。
- 合併：同一 memory item 去重後，以 user priority、confidence、RRF score、recency 排序。

### `IEmbeddingProvider`

負責產生向量。

```csharp
public interface IEmbeddingProvider
{
    string Model { get; }
    int Dimensions { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
}
```

建議第一版提供：

- `DisabledEmbeddingProvider`：測試與低延遲模式。
- `OllamaEmbeddingProvider`：本地 embedding model。

不要讓 embedding model 與翻譯 model 綁死。它們應該分開設定。

本地 CJK embedding 選型建議：

- BGE-M3：同一模型支援 dense、sparse、multi-vector retrieval，支援 100+ 語言，適合之後做 hybrid retrieval。
- Qwen3-Embedding-0.6B：較小，支援 instruction-aware 與 Matryoshka / MRL 自訂維度，可用 256 維快掃降低記憶體與 CPU 成本。

4GB GPU 場景下，翻譯 LLM 與 embedding model 不應同時競爭 GPU。memory item 的 embedding 應背景預先計算；查詢端預設 lexical，dense 只在 CPU 足夠快或使用者啟用時加入。

參考：BGE 的 [BGE-M3 文件](https://bge-model.com/bge/bge_m3.html) 與 Qwen 的 [Qwen3 Embedding README](https://github.com/QwenLM/Qwen3-Embedding)。

### `RagRetriever`

負責把多種記憶來源合併、排序、裁切。

輸入：

```text
text, source, target, mode, profileId, sessionId
```

輸出：

```csharp
public sealed record RagContext(
    IReadOnlyList<RagTerm> Terms,
    IReadOnlyList<RagExample> Examples,
    IReadOnlyList<RagCorrection> OcrCorrections,
    IReadOnlyList<RecentLine> RecentLines,
    string SceneSummary,
    string ContextHash);
```

`ContextHash` 用於 generated translation cache。只要本次 prompt 依賴的 RAG snippets 或穩定前綴版本改了，cache key 就應該跟著變。

`ContextHash` 建議由兩部分組成：

```text
stablePrefixVersion + variableRagSnippetsHash
```

- `stablePrefixVersion`：system prompt、preset version、glossary hash、場景摘要版本、活躍角色名單版本。
- `variableRagSnippetsHash`：本句實際注入的 1 到 3 條範例、OCR correction、term snippets。

不要把整份 memory store 或未使用的檢索候選放進 hash。hash 只代表本次 prompt 真的依賴的內容。

### `PromptContextBuilder`

負責把 `RagContext` 壓成 prompt 片段，並維持 stable prefix / variable tail 的順序，讓 llama.cpp / Ollama 等 runtime 有機會重用固定前綴的 KV cache。

prompt 應分成：

```text
[固定前綴：session 級穩定內容]
  system / preset / glossary / 場景摘要 / 活躍角色名

[變動尾巴：本句專屬內容]
  本句相關的少量術語、OCR correction、1 到 3 條範例、INPUT_TEXT
```

固定前綴要放在前面，而且跨多句盡量不變。每句才會改的 RAG snippets 與 `INPUT_TEXT` 放在最後，避免 context hash 一變就破壞整段 prefix cache 的價值。

建議 token budget：

| 區塊 | 預設上限 |
| --- | --- |
| 術語 | 5 到 10 條 |
| OCR correction | 3 到 5 條 |
| 翻譯範例 | 1 到 3 條 |
| 最近對話 | 3 到 5 條 |
| 場景摘要 | 300 字以內 |

即時模式總 RAG context 建議控制在 400 到 800 tokens 內。

## `/translate` 接入方式

`MortTranslateRequest` 可逐步加上可選欄位，不破壞 MORT：

```csharp
public sealed record MortTranslateRequest
{
    public string? Text { get; init; }
    public string? Source { get; init; }
    public string? Target { get; init; }
    public string? Mode { get; init; }
    public string? Provider { get; init; }
    public string? Glossary { get; init; }

    public string? Profile { get; init; }
    public string? SessionId { get; init; }
    public bool? UseRag { get; init; }
}
```

MORT 不會送 `Profile` 或 `SessionId` 也沒關係：

- `Profile` 預設 `default`。
- `SessionId` 預設依來源建立，例如 `mort-default`。
- `UseRag` 預設由 appsettings 控制。

`TranslationService.TranslateAsync` 建議順序：

```text
1. Normalize request text/source/target/mode/provider/profile/session.
2. Blank text fast return.
3. Load preset and static glossary.
4. Check user-verified exact translation memory.
5. Retrieve RAG context if enabled.
6. Build context hash.
7. Check generated exact cache using glossary hash + context hash.
8. Check generated fuzzy cache using normalized text + profile + context hash.
9. Build provider request with glossary + RAG context.
10. Provider translate.
11. Store generated cache.
12. Store conversation event.
13. Queue async memory maintenance.
```

## Cache 策略

RAG 會影響模型輸出，所以 cache key 不能只看 source text。

目前 `TranslationCacheKey.Create(...)` 使用：

```text
text, source, target, mode, provider, model, presetVersion, glossaryHash
```

加入 RAG 後應加：

```text
profileId, ragContextHash
```

如果 `UseRag=false`，`ragContextHash` 使用空字串。

generated cache 分兩層：

```text
exact key:
  normalized original text + source + target + mode + provider + model
  + presetVersion + glossaryHash + profileId + ragContextHash

fuzzy key:
  fuzzy canonical text + source + target + mode + provider + model
  + presetVersion + glossaryHash + profileId + ragContextHash
```

fuzzy canonical text 只用在同一 `profileId`、provider/model、preset/glossary、`ragContextHash` 下。這個限制很重要，否則同一句 OCR 文字在不同場景或不同 RAG 記憶下可能回到錯的舊翻譯。

fuzzy cache 的 normalize 建議：

- Unicode NFKC。
- 全半形、連續空白、尾端標點正規化。
- 對最近命中的文字做短距離編輯距離比對。
- 若 dense embedding 已啟用，可只對最近一小段 cache 做便宜相似度輔助，不作為硬依賴。

另外建議把 cache 分成兩種概念：

| 類型 | 是否可直接返回 | 說明 |
| --- | --- | --- |
| user-verified translation memory | 可以 | 使用者確認過，優先於模型 |
| generated translation cache | 可以，但 key 必須含 context hash | 模型輸出快取，避免上下文變了還回舊翻譯 |
| generated fuzzy cache | 可以，但必須同 profile/provider/model/preset/glossary/context hash | OCR 抖動時提升命中率，避免同一行反覆重譯 |

## Prompt 設計

`PromptPreset.UserTemplate` 應增加 RAG placeholder：

```text
Task: translate INPUT_TEXT from {SOURCE} to {TARGET}.
Style: concise game dialogue for OCR overlay.

Memory:
{MEMORY_CONTEXT}

Glossary:
{GLOSSARY}

Recent context:
{RECENT_CONTEXT}

INPUT_TEXT:
{TEXT}

TRANSLATION:
```

實際渲染時，`Memory`、`Glossary`、`Recent context` 中穩定內容應盡量保持順序與文字穩定；本句專屬 examples / OCR corrections 可以放在靠近 `INPUT_TEXT` 的位置，減少每句變動對 prefix cache 的影響。

沒有 RAG 時：

```text
Memory:
(none)

Recent context:
(none)
```

RAG 片段格式要短：

```text
Terms:
- 魔導炉 => 魔導爐
- 王都グランベル => 格蘭貝爾王都

OCR corrections:
- グランぺル => グランベル

Examples:
- JP: もういい、勝手にしなさい。
  ZH: 算了，隨你高興。
```

避免長篇說明。模型只需要可套用的規則。

## 使用者怎麼用

### 第一版

使用者只需要：

1. 為遊戲建立 profile，例如 `granbell`.
2. 匯入或手動新增 glossary / memory。
3. MORT 繼續打 `POST /translate`。
4. LocalTranslateHub 自動檢索相關記憶並翻譯。

建議 API：

```http
GET /profiles
POST /profiles
GET /memories?profile=granbell
POST /memories
POST /memories/import
POST /corrections
GET /rag/search?profile=granbell&query=魔導炉
```

### Correction workflow

使用者看到翻譯錯誤時，未來 viewer 或桌面 UI 可以送：

```json
{
  "profile": "granbell",
  "source": "ja",
  "target": "zh-TW",
  "sourceText": "魔導炉",
  "correctedText": "魔導爐",
  "type": "term",
  "note": "作品固定術語"
}
```

下一次遇到相同或相似文字時，RAG 會把這條修正放進 prompt。

## 背景任務

即時路徑不能做重工作。這些應該放背景：

- 為新 memory item 生成 embedding。
- 對最近 20 到 50 條 conversation event 做 scene summary。
- 從已確認翻譯中抽取候選術語。
- 清理低使用率或過期 session context。
- 整併近似 memory items，降低重複檢索結果。
- 解決同一 source text 的衝突翻譯。

第一版可以用 `BackgroundService` + SQLite queue table。不要先引入外部 message broker。

衝突規則要 deterministic：

```text
user-verified > imported > auto-extracted
```

同一等級再用 recency、confidence、use_count tie-break。久未使用且低 confidence 的 auto-extracted 項目可以降權或停用，但不要自動刪除 user-verified 項目。

## 與 OCR 階段的關係

第三階段加入 OCR app 後，RAG 會多兩個用途：

1. OCR correction memory：在送翻譯前修正常見 OCR 變體。
2. Detection reuse：同一文字框反覆出現時，直接命中 translation memory 或 generated cache。

`ocr_correction` 不應只做 exact mapping。除了 `グランぺル => グランベル` 這類明確修正，也要能類推到同一系統性錯誤：

- 對已知術語做編輯距離 1 到 2 的候選比對。
- 維護小型字形混淆表，例如 `ぺ/べ`、`力/カ`、`ロ/口`。
- 混淆正規化後相同時套用修正，但仍要記錄 confidence。
- 低 confidence 修正只進 prompt，不直接覆寫 user-verified memory。

OCR pipeline 建議順序：

```text
frame diff
-> OCR
-> OCR stabilizer / correction memory
-> duplicate detector
-> /translate with profile/session
-> RAG
-> provider
```

## 第一版落地順序

### Step 1：資料模型與設定

- 新增 `RagOptions`。
- 新增 `ProfileId` / `SessionId` / `UseRag` request fields。
- 新增 `RagContext` model。
- 更新 cache key 支援 `profileId`、`ragContextHash`，並預留 fuzzy generated cache tier。

### Step 2：SQLite memory store

- 建立 `memory_items`、`conversation_events`、`scene_summaries`。
- 先實作 deterministic retrieval：exact / substring / priority sort。
- 預留 lexical FTS5 路徑，但第一版可以先用 substring。
- 暫時不要求 embedding。

### Step 3：Prompt integration

- `ProviderTranslationRequest` 加入 `RagContext`。
- `PromptRenderer` 支援 `{MEMORY_CONTEXT}` 與 `{RECENT_CONTEXT}`。
- preset 加入新 placeholder。
- 確保 stable prefix 在前、variable tail 在後。

### Step 4：API

- `GET /rag/search` 用於 debug。
- `POST /memories` 新增術語/修正。
- `POST /corrections` 把使用者修正寫回 memory。

### Step 5：Embedding retrieval

- 加 `IEmbeddingProvider`。
- 支援本地 embedding provider。
- 對 `translation`、`scene_summary` 類型做相似檢索。
- 用 RRF 合併 lexical 與 dense 結果。
- 先存 float32 vector；資料量變大後再加 int8/binary 粗掃與 top-k rescore。
- 加 retrieval timeout；超時時退回 deterministic RAG。

### Step 6：背景摘要

- 累積 session events。
- 每 N 條或每 N tokens 產生短摘要。
- 摘要只在未來翻譯中作為短上下文。
- 背景整併 memory items，套用衝突規則與 confidence 衰減。

## 測試計畫

- `UseRag=false` 時，行為與現在一致。
- exact user correction 優先於 provider。
- term memory 命中時，prompt 中出現固定譯名。
- RAG context hash 改變時，generated cache key 改變。
- stable prefix version 改變時，generated cache key 改變。
- fuzzy cache 只在同一 profile/provider/model/preset/glossary/context hash 下命中。
- retrieval 結果超過 token budget 時會裁切。
- dense retrieval timeout 時，RRF 合併流程仍回傳 lexical 結果。
- OCR correction 可套用編輯距離或字形混淆表，但不覆寫 user-verified memory。
- embedding provider 失敗時，仍可用 deterministic retrieval 翻譯。
- provider 失敗時，仍回傳原文，避免 MORT overlay 空白。
- conversation event 在成功翻譯後寫入。

## 成功標準

第一版 RAG 不追求「最聰明」，先追求可用與低延遲：

- 無 cache 的短句翻譯，RAG retrieval 額外延遲小於 30ms。
- prompt 中 RAG context 小於 800 tokens。
- 使用者新增術語後，下一次翻譯會套用。
- 同一作品 profile 的角色/地名譯名更一致。
- OCR 抖動造成的近似重複句可命中 fuzzy cache，不反覆重譯。
- stable prefix 不被每句 RAG snippets 破壞，未來 runtime prefix/KV cache 可受益。
- RAG 關閉時可完全回到目前 gateway 行為。
