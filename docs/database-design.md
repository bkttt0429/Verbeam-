# Database Design

Verbeam uses one local SQLite database as a runtime memory store. The
current API path should stay simple:

```text
POST /translate
-> normalize request
-> exact generated translation cache
-> preset + glossary
-> provider
-> write cache/event/memory data
-> MORT response and broadcast
```

The schema keeps the existing `translations` table compatible while adding the
tables needed for profiles, sessions, glossary editing, RAG memory, embeddings,
and viewer/history features.

The detailed memory/RAG behavior is described in
[Headroom-style memory design](headroom-style-memory-design.md).

## Design Principles

- Keep SQLite as the only runtime database for the local app.
- Keep `translations.key` as the generated-cache primary key so current cache
  behavior remains unchanged.
- Store user-trusted knowledge separately from generated cache output.
- Store event/history rows separately from cache rows so cache eviction does not
  erase the user timeline.
- Store vectors as `float32` little-endian BLOBs first; a future `sqlite-vec`
  integration can index the same logical data.
- Use ISO-8601 UTC text timestamps and integer booleans for portable SQLite
  behavior.

## Tables

### `schema_migrations`

Tracks applied schema versions.

| Column | Purpose |
| --- | --- |
| `version` | Integer schema version. |
| `name` | Human-readable migration name. |
| `applied_at` | UTC timestamp. |

### `profiles`

Represents a game, book, reading project, or default MORT profile.

| Column | Purpose |
| --- | --- |
| `id` | Stable profile id, such as `default` or a game slug. |
| `display_name` | User-facing name. |
| `source_language`, `target_language` | Default language pair. |
| `default_mode`, `default_provider` | Default preset/provider choices. |
| `notes`, `metadata_json` | Flexible local metadata. |
| `is_active` | Soft-disable flag. |

### `translation_sessions`

Groups translation events for a play/read session.

| Column | Purpose |
| --- | --- |
| `id` | Session id supplied by app/MORT or generated locally. |
| `profile_id` | Parent profile. |
| `display_name` | Optional user label. |
| `source_language`, `target_language`, `mode`, `provider` | Session defaults. |
| `started_at`, `last_seen_at`, `ended_at` | Timeline fields. |
| `metadata_json` | UI/OCR/app metadata. |

### `translations`

Generated translation cache. This table intentionally matches the current
`SqliteTranslationCache` record shape.

| Column | Purpose |
| --- | --- |
| `key` | SHA-256 cache key from text, language pair, mode, provider, model, preset version, and glossary hash. |
| `source_text`, `translated_text` | Input and generated output. |
| `source_language`, `target_language`, `mode` | Request context. |
| `provider`, `engine`, `model` | Provider/runtime identity. |
| `preset_version`, `glossary_hash` | Prompt inputs included in the cache key. |
| `latency_ms`, `created_at` | Performance and timeline data. |

### `translation_events`

Append-only-ish request history for viewer, debugging, and future session
context. It can point at a generated cache row but does not depend on one.

| Column | Purpose |
| --- | --- |
| `id` | Event id. |
| `session_id`, `profile_id` | Timeline grouping. |
| `translation_key` | Optional pointer to generated cache. |
| `request_name` | Existing MORT request `name` field. |
| `source_text`, `translated_text` | Request/result text. |
| `source_language`, `target_language`, `mode`, `provider` | Request context. |
| `glossary_id`, `glossary_hash` | Glossary identity at translation time. |
| `engine`, `model`, `latency_ms`, `cache_hit` | Runtime diagnostics. |
| `error_code`, `error_message` | MORT-compatible failure record. |
| `created_at` | Event timestamp. |

### `glossary_sets` and `glossary_terms`

Database-backed glossary storage for future editing/import flows. Existing JSON
glossaries can remain the read-only seed/source format.

| Table | Purpose |
| --- | --- |
| `glossary_sets` | Glossary header, profile scope, hash, and metadata. |
| `glossary_terms` | Source term, target term, note, priority, active flag. |

### `memory_items`

User-trusted or system-maintained memory for RAG.

| Column | Purpose |
| --- | --- |
| `id` | Memory item id. |
| `profile_id` | Scope boundary. |
| `memory_kind` | `term`, `translation`, `ocr_correction`, `style`, or `scene_summary`. |
| `source_language`, `target_language` | Language pair. |
| `source_text`, `source_text_normalized` | Matchable source content. |
| `target_text` | Translation/correction/style content. |
| `note`, `priority`, `confidence` | Retrieval ranking and explanation fields. |
| `tags_json`, `metadata_json` | Flexible local metadata. |
| `is_active`, `last_used_at`, `use_count` | Lifecycle and usage signals. |

### `memory_embeddings`

Optional vector data for semantic retrieval.

| Column | Purpose |
| --- | --- |
| `memory_id` | Parent memory item. |
| `embedding_model` | Model that produced the vector. |
| `dims` | Vector dimensionality. |
| `vector` | `float32` little-endian BLOB. |
| `content_hash` | Detects stale vectors after text changes. |

### `scene_summaries`

Compact session summaries used to keep prompts small.

| Column | Purpose |
| --- | --- |
| `id` | Summary id. |
| `session_id`, `profile_id` | Scope. |
| `summary_text` | Prompt-ready summary. |
| `start_event_id`, `end_event_id` | Event range covered by this summary. |
| `created_at`, `updated_at` | Timeline fields. |

## Retrieval Path

The future RAG path should resolve context in this order:

1. Exact user memory from `memory_items` where `memory_kind = 'translation'`.
2. Glossary terms from JSON or `glossary_terms`.
3. OCR corrections and terms from `memory_items`.
4. Recent rows from `translation_events`.
5. Session summary from `scene_summaries`.
6. Optional semantic matches via `memory_embeddings`.

Generated translations remain in `translations`; user-approved corrections live
in `memory_items` so they can override generated cache instead of competing with
it.

## Current Implementation Status

Schema version `1` is initialized by `SqliteSchema` through the shared
`SqliteDatabase` helper. The runtime enables WAL mode for better concurrent
read/write behavior, applies `foreign_keys = ON`, `busy_timeout = 5000`, and
`synchronous = NORMAL` on opened connections, and caches schema initialization
per database path so ordinary cache/event operations do not rerun the full
schema setup.

Translation requests now write success and failure rows through
`SqliteTranslationEventStore`, and recent rows are exposed through
`GET /translation/events`. Generated-cache behavior remains unchanged, while
the runtime database now has a concrete history source for
viewer/debug/session/RAG features.

User-approved translation memory is implemented through `SqliteMemoryStore`.
`GET /memories` and `POST /memories` manage active memory rows, while
`POST /translation/corrections` promotes a translation event into
`memory_items(memory_kind='translation')`. `TranslationService` checks exact
profile-scoped translation memory before calling the provider.

Prompt memory context is now implemented through `MemoryContextBuilder`.
Translation requests retrieve active trusted memory rows for the same
profile/language pair, render only selected terms, OCR corrections, style notes,
and approved examples into the provider prompt, and include the selected memory
context hash in the generated cache key. Usage counters update `last_used_at`
and `use_count` without changing `updated_at`, so memory statistics do not
invalidate stable generated-cache keys.
