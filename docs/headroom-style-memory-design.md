# Headroom-Style Memory Design

This document defines the YomiBridge memory system inspired by
Headroom-style ideas: keep full raw history, compress it into small reversible
context, retrieve only the highest-value snippets, and keep memory scoped by
profile/session so unrelated games or reading projects do not bleed into each
other.

This is not a dependency on Headroom. The goal is to borrow the useful pattern:

```text
raw local events
-> extracted memory candidates
-> compressed summaries
-> source links
-> deterministic retrieval
-> optional semantic retrieval
-> prompt context
```

## Goals

- Keep translation quality stable across long sessions.
- Preserve user corrections as higher-priority memory than generated output.
- Keep prompts small, usually 400-800 tokens of memory context.
- Make compressed context reversible by linking summaries back to source events.
- Keep all memory local in SQLite.
- Work without embeddings first; add semantic retrieval later.
- Keep `/translate` fast enough for MORT/OCR overlay usage.

## Non-Goals

- Do not add a separate vector database in the first version.
- Do not make memory extraction block the translation response.
- Do not trust auto-extracted memory as much as user-verified memory.
- Do not mix memories across profiles unless the user explicitly imports or
  links them.
- Do not send full history to the model by default.

## Memory Layers

### Layer 0: Raw Events

Raw events are the source of truth.

Existing tables:

- `translation_events`
- `ocr_events`
- `speech_events`

These rows keep exact source text, translated text, provider/model, profile,
session, timestamps, latency, cache hits, and errors.

Rules:

- Never delete raw events as part of normal memory compression.
- Cache cleanup must not delete raw timeline rows.
- Summaries and memory items must keep enough metadata to trace back to raw
  event ids.

### Layer 1: User-Verified Memory

User-verified memory has the highest priority.

Stored in:

- `memory_items`

Kinds:

- `term`: a term/name/title mapping.
- `translation`: an approved source-to-target translation.
- `ocr_correction`: a known OCR mistake and its correction.
- `style`: tone, character voice, naming convention, or translation rule.
- `scene_summary`: a stable summary promoted by the user or summarizer.

Priority:

```text
user-verified > imported > auto-extracted > generated-cache
```

Suggested `metadata_json` fields:

```json
{
  "origin": "user-verified",
  "source_event_ids": ["..."],
  "created_from": "viewer-correction",
  "review_status": "approved"
}
```

### Layer 2: Auto-Extracted Memory Candidates

Auto-extracted memory is useful but should stay lower-confidence until the user
confirms it.

Examples:

- Repeated untranslated names.
- Consistent source-target pairs from successful translations.
- OCR correction patterns.
- Speaker style hints.
- Repeated location or faction names.

Recommended defaults:

- `confidence`: `0.3` to `0.7`.
- `origin`: `auto-extracted`.
- `is_active`: `1`, but rank below user-verified rows.
- Promote to `origin=user-verified` when edited or accepted by the user.

### Layer 3: Session Summaries

Session summaries keep recent story/context compact.

Stored in:

- `scene_summaries`
- optionally mirrored as `memory_items(memory_kind='scene_summary')`

Each summary must include:

- `profile_id`
- `session_id`
- `summary_text`
- `start_event_id`
- `end_event_id`

Summaries are prompt-ready, but source events remain available for inspection.

## Source Linking

To make compressed context reversible, memory rows should link back to source
events through `metadata_json`.

Suggested shape:

```json
{
  "origin": "auto-extracted",
  "source_table": "translation_events",
  "source_event_ids": ["event-a", "event-b"],
  "extractor": "memory-maintenance-v1",
  "review_status": "candidate"
}
```

When a memory item comes from multiple modalities, use:

```json
{
  "sources": [
    { "table": "ocr_events", "id": "ocr-event-a" },
    { "table": "translation_events", "id": "translation-event-a" }
  ]
}
```

## Retrieval Order

The retrieval path should be deterministic first:

```text
1. user-verified exact translation memory
2. user-verified term memory
3. OCR correction memory
4. active glossary terms
5. recent session lines
6. scene summary
7. imported/auto-extracted examples
8. optional semantic matches
```

The first implementation should not require embeddings.

## Ranking

Each candidate gets a score. Keep the formula simple at first:

```text
score =
  origin_weight
  + kind_weight
  + priority
  + confidence_weight
  + recency_weight
  + usage_weight
  + match_weight
```

Suggested weights:

| Signal | Suggested value |
| --- | --- |
| `origin=user-verified` | +1000 |
| `origin=imported` | +600 |
| `origin=auto-extracted` | +200 |
| exact source match | +500 |
| substring match | +200 |
| same session | +100 |
| same profile | required |
| inactive memory | excluded |

Tie-breakers:

```text
priority DESC,
confidence DESC,
last_used_at DESC,
use_count DESC,
updated_at DESC
```

## Prompt Context Budget

The prompt should receive a compressed memory package, not raw history.

Default budget:

| Block | Budget |
| --- | --- |
| Terms | 5-10 rows |
| OCR corrections | 3-5 rows |
| Approved examples | 1-3 rows |
| Recent lines | 3-5 rows |
| Scene summary | 300-500 characters |

Recommended rendered shape:

```text
Memory:
Terms:
- SOURCE => TARGET

OCR corrections:
- WRONG => CORRECT

Approved examples:
- SOURCE: ...
  TARGET: ...

Recent context:
- SOURCE -> TARGET

Scene:
...
```

## Context Hash

Generated translation cache must include a memory context hash once RAG is
enabled.

Recommended hash inputs:

```text
profile_id
session_id or stable session scope
preset_version
glossary_hash
memory_item_ids + updated_at
scene_summary_id + updated_at
retrieval_policy_version
```

Do not hash raw event history directly. Hash the selected memory snippets and
their versions.

## Data Model Additions

The current schema is enough for the first implementation, but these additions
would make the system cleaner.

### Add `origin` to `memory_items`

Current workaround: store in `metadata_json`.

Future column:

```sql
origin TEXT NOT NULL DEFAULT 'user-verified'
```

Allowed values:

```text
user-verified
imported
auto-extracted
system
```

### Add `review_status` to `memory_items`

Current workaround: store in `metadata_json`.

Future column:

```sql
review_status TEXT NOT NULL DEFAULT 'approved'
```

Allowed values:

```text
candidate
approved
rejected
archived
```

### Add `source_links_json` to `memory_items`

Current workaround: store in `metadata_json`.

Future column:

```sql
source_links_json TEXT NOT NULL DEFAULT '[]'
```

Shape:

```json
[
  { "table": "translation_events", "id": "..." },
  { "table": "ocr_events", "id": "..." }
]
```

### Add `memory_extraction_jobs`

Used for async maintenance without blocking `/translate`.

```sql
CREATE TABLE memory_extraction_jobs (
    id TEXT PRIMARY KEY,
    profile_id TEXT NOT NULL,
    session_id TEXT NOT NULL DEFAULT '',
    source_event_id TEXT NOT NULL,
    source_table TEXT NOT NULL,
    job_kind TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    attempts INTEGER NOT NULL DEFAULT 0,
    error_message TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE INDEX idx_memory_extraction_jobs_pending
ON memory_extraction_jobs(status, created_at);
```

First version can skip this table and perform maintenance on demand. Add it when
translation/event volume grows.

## Services

### `IMemoryStore`

Responsible for CRUD and deterministic lookup.

```csharp
public interface IMemoryStore
{
    Task AddOrUpdateAsync(MemoryItem item, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryItem>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken);
    Task RecordUseAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken);
}
```

### `MemoryRetriever`

Builds the selected memory set for one translation request.

Input:

```text
text
source
target
mode
profile_id
session_id
glossary_id
```

Output:

```csharp
public sealed record MemoryContext(
    IReadOnlyList<MemorySnippet> Terms,
    IReadOnlyList<MemorySnippet> OcrCorrections,
    IReadOnlyList<MemorySnippet> Examples,
    IReadOnlyList<RecentLine> RecentLines,
    string SceneSummary,
    string ContextHash);
```

### `MemoryContextBuilder`

Renders selected memory into prompt-safe text.

Rules:

- Keep deterministic ordering.
- Truncate each block independently.
- Do not include inactive/rejected memory.
- Include only target-profile memories.
- Prefer user-verified over auto-extracted.

### `MemoryMaintenanceService`

Runs after events are written.

Responsibilities:

- Extract repeated term candidates.
- Create candidate translation memories from repeated successful translations.
- Update scene summaries every N events.
- Merge duplicate memory candidates.
- Lower confidence for conflicting auto-extracted items.

This service should not block `/translate` in the long run.

## API Design

### List memories

```http
GET /memories?profile=default&type=term&limit=100
```

### Create or update memory

```http
POST /memories
Content-Type: application/json

{
  "profile": "default",
  "memoryKind": "term",
  "source": "ja",
  "target": "zh-TW",
  "sourceText": "SOURCE",
  "targetText": "TARGET",
  "note": "",
  "priority": 0,
  "confidence": 1.0,
  "origin": "user-verified"
}
```

### Correct a translation

```http
POST /translation/corrections
Content-Type: application/json

{
  "profile": "default",
  "sessionId": "session-a",
  "eventId": "translation-event-id",
  "correctedText": "..."
}
```

Behavior:

- Load the source `translation_events` row.
- Create or update `memory_items(memory_kind='translation')`.
- Mark `origin=user-verified`.
- Link back to the source event id.

### Debug retrieval

```http
GET /memory/search?profile=default&source=ja&target=zh-TW&q=...
```

Returns selected memory snippets and scores. This is important for debugging
why a prompt used a term or example.

## Translation Flow With Memory

```text
POST /translate
-> normalize request
-> blank fast path
-> load preset/glossary
-> retrieve user-verified exact memory
-> if exact memory found, return it without provider
-> retrieve memory context
-> build context hash
-> check generated cache using context hash
-> call provider with rendered memory context
-> write generated cache
-> write translation event
-> queue memory maintenance
-> broadcast result
```

## Implementation Phases

### Current Implementation Status

Phase 1 is implemented:

- `IMemoryStore`
- `SqliteMemoryStore`
- `GET /memories`
- `POST /memories`
- `POST /translation/corrections`
- exact `memory_items(memory_kind='translation')` lookup before provider/cache
- profile-scoped exact memory
- usage counting through `last_used_at` and `use_count`

Not implemented yet:

- prompt memory context
- context hash based on selected memory snippets
- scene summary maintenance
- auto-extracted memory candidates
- semantic retrieval

### Phase 1: User Corrections Become Memory

Deliverables:

- `IMemoryStore`
- `SqliteMemoryStore`
- `POST /translation/corrections`
- `GET /memories`
- exact memory lookup before provider

Success criteria:

- Correcting a translation creates a `memory_items` row.
- Repeating the same source text returns the corrected translation without
  calling the provider.
- Memory is scoped by `profile_id`.

### Phase 2: Prompt Memory Context

Deliverables:

- `MemoryRetriever`
- `MemoryContextBuilder`
- `ProviderTranslationRequest.MemoryContext` or equivalent
- context hash in generated cache key

Success criteria:

- Relevant terms and OCR corrections appear in the provider prompt.
- RAG-disabled requests keep current cache behavior.
- Context hash changes when selected memory changes.

### Phase 3: Recent Context And Summaries

Deliverables:

- recent `translation_events` retrieval
- `scene_summaries` maintenance
- source event links in summary metadata

Success criteria:

- Recent session lines appear in prompt within budget.
- Summaries link back to event ranges.
- Long sessions do not grow prompt size linearly.

### Phase 4: Auto Extraction

Deliverables:

- candidate extraction from translation/OCR events
- duplicate merge
- candidate review API fields

Success criteria:

- Repeated terms produce low-confidence candidates.
- User approval promotes candidates.
- Conflicting auto memories do not override user-approved rows.

### Phase 5: Optional Semantic Retrieval

Deliverables:

- `IEmbeddingProvider`
- `memory_embeddings`
- optional lexical+dense ranking

Success criteria:

- The system works when embeddings are disabled.
- Dense retrieval has a timeout and falls back to deterministic retrieval.
- Semantic results are scoped by profile and ranked below exact user memory.

## Testing Plan

Required tests:

- Corrected translation is returned before provider call.
- Memory from one profile is not used in another profile.
- Inactive/rejected memory is ignored.
- User-verified memory outranks auto-extracted memory.
- Context hash changes when selected memory item updates.
- Generated cache differs when memory context differs.
- Recent context respects token/character budget.
- Memory search returns score/debug metadata.
- Provider failure still writes a translation event but not a memory item.

## Open Decisions

- Whether `origin` and `review_status` should become real columns immediately
  or stay in `metadata_json` for phase 1.
- Whether exact user memory should return directly or still pass through the
  provider for style smoothing.
- Whether scene summary should live only in `scene_summaries` or also mirror
  into `memory_items`.
- Whether memory maintenance should use an in-process background service first
  or a SQLite job table from the beginning.
