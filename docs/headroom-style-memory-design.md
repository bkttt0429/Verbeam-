# Headroom-Style Memory Design

This document defines the Verbeam memory system inspired by
Headroom-style ideas: keep full raw history, compress it into small reversible
context, retrieve only the highest-value snippets, and keep memory scoped by
profile/session so unrelated games or reading projects do not bleed into each
other.

The remaining implementation checklist is tracked in
[Memory System TODO](memory-system-todo.md).

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
- `trust_level`: `local_generated`.
- `is_active`: `1`, but rank below user-verified rows.
- Keep out of the trusted exact-memory path until edited or accepted by the
  user.

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
The current implementation supports manual upsert through `/scene-summaries`
and deterministic automatic maintenance from recent successful translation
events once a session reaches the configured event threshold.

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
  "observation_count": 3,
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

### `memory_maintenance_jobs`

Used for durable async maintenance without blocking `/translate`.

```sql
CREATE TABLE memory_maintenance_jobs (
    id TEXT PRIMARY KEY,
    job_kind TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending',
    profile_id TEXT NOT NULL,
    session_id TEXT NOT NULL DEFAULT '',
    source_language TEXT NOT NULL,
    target_language TEXT NOT NULL,
    mode TEXT NOT NULL,
    attempts INTEGER NOT NULL DEFAULT 0,
    error_message TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    started_at TEXT,
    completed_at TEXT
);

CREATE INDEX idx_memory_maintenance_jobs_status
ON memory_maintenance_jobs(status, updated_at, created_at);
```

Implemented job kinds:

- `translation_candidates`
- `embedding_prewarm`

Jobs move through `pending`, `running`, `completed`, and `failed`. Stale
`running` rows can be claimed again, and repeated failures are retried up to the
maintenance attempt limit before staying `failed`.

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
- Use trusted memory for realtime exact/prompt paths; `local_generated`
  auto-extracted candidates remain reviewable until promoted.

### `MemoryMaintenanceService`

Runs after events are written.

Responsibilities:

- Extract repeated term candidates.
- Create candidate translation memories from repeated successful translations.
- Update scene summaries every N events.
- Merge duplicate memory candidates.
- Lower confidence for conflicting auto-extracted items.

The current implementation has two bounded maintenance steps after successful
translation events:

- `SceneSummaryMaintenanceService` updates one deterministic
  `scene_summaries` row per profile/session/language pair/mode in the request
  flow.
- `MemoryMaintenanceService` creates or updates
  `memory_items(memory_kind='translation', trust_level='local_generated')`
  candidates when the same source has a repeated stable target in recent
  same-session success events. It also creates
  `memory_items(memory_kind='term', trust_level='local_generated')` candidates
  for repeated retained Title Case terms such as `Star Key`. `TranslationService`
  enqueues this as durable `memory_maintenance_jobs` work, then drains a small
  batch opportunistically so local use remains low-latency while retry state is
  preserved in SQLite.
- `/ocr/translate` can create
  `memory_items(memory_kind='ocr_correction', trust_level='local_generated')`
  candidates after the same OCR correction has been applied repeatedly.

Auto candidates include event provenance in `metadata_json`, use lower
confidence and priority, and are skipped when a user-verified or trusted-import
memory already owns the same normalized source. If a new generated candidate
conflicts with an existing `local_generated` row, the existing row is kept but
its confidence is lowered for review.

## API Design

### List memories

```http
GET /memories?profile=default&type=term&trust=user_verified&q=star&limit=100
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

### Update memory

```http
PATCH /memories/{id}
Content-Type: application/json

{
  "sourceText": "SOURCE",
  "targetText": "TARGET",
  "note": "",
  "priority": 0,
  "confidence": 1.0,
  "trustLevel": "user_verified",
  "visibility": "profile",
  "isActive": true
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

### Maintenance queue

```http
GET /memory/maintenance/jobs?profile=default&status=pending&limit=50
```

Lists recent durable memory-maintenance jobs.

```http
POST /memory/maintenance/jobs/drain
Content-Type: application/json

{
  "limit": 10
}
```

Claims and runs a bounded maintenance batch. This is the operator recovery path
when a process exits before opportunistic queue draining finishes.

## Translation Flow With Memory

```text
POST /translate
-> normalize request
-> resolve request principal from X-Verbeam-Session / X-Verbeam-Principal
-> blank fast path
-> load preset/glossary
-> retrieve user-verified exact memory
-> if exact memory found, audit and return it without provider
-> retrieve memory context
-> build context hash
-> check generated cache using context hash
-> call provider with rendered memory context
-> write generated cache
-> write translation event
-> write selected memory/context audit rows
-> run bounded scene summary maintenance
-> queue durable translation-memory candidate maintenance
-> broadcast result
```

## Implementation Phases

### Current Implementation Status

Phase 1, Phase 2, Phase 3 recent context/scene summaries, Phase 4 auto
candidate extraction, and first-pass candidate review are implemented:

- `IMemoryStore`
- `SqliteMemoryStore`
- `MemoryContextBuilder`
- `GET /memories`
- `POST /memories`
- `PATCH /memories/{id}`
- `POST /memories/{id}/review`
- `POST /memories/{id}/resolve-conflict`
- `GET /memories/conflicts`
- `POST /translation/corrections`
- exact `memory_items(memory_kind='translation')` lookup before provider/cache
- profile-scoped exact memory
- prompt memory context for trusted terms, OCR corrections, style notes, and
  approved examples
- context hash based on selected memory snippets in the generated cache key
- recent session context from `translation_events` when `sessionId` is present
- recent event ids and timestamps included in the generated cache context hash
- `scene_summaries` storage and `/scene-summaries` inspection/upsert API
- scene summaries selected into prompt context and context hash
- deterministic automatic scene summary maintenance after successful
  same-session translation events
- automatic translation-memory candidate extraction from repeated stable
  same-session outputs
- automatic term candidate extraction from repeated retained Title Case terms
- automatic OCR-correction candidate extraction from repeated applied
  corrections in `/ocr/translate`
- durable `memory_maintenance_jobs` queue for translation candidate extraction
  and embedding prewarm, with admin list/drain endpoints and retryable stale
  running jobs
- Workbench Settings Memory panel for filtered inspection, create/edit,
  trust updates, candidate approve/reject, conflict discovery, conflict
  resolve, and deactivate/reactivate operations
- usage counting through `last_used_at` and `use_count`
- DB-backed shared-memory read, memory write, and memory approve permissions
  through `memory_principal_permissions`
- profile-scoped role presets (`blocked`, `reader`, `contributor`,
  `reviewer`, `admin`, `custom`) layered over the same read/write/approve ACL
- optional trusted-proxy external identity bridge that maps external groups to
  profile role presets after a shared-secret header check
- optional first-party HS256/RS256 bearer JWT validation that extracts
  principal and groups after issuer/audience/lifetime checks, with static
  JWKS, remote JWKS URL, and OIDC discovery support
- optional OIDC authorization-code + PKCE login endpoints that validate the
  returned ID/access token and issue the same DB-backed session tokens
- OIDC refresh-token storage policy is client-only: Workbench keeps the refresh
  token in a password field, `/health` reports the policy, and raw refresh
  tokens are not persisted to SQLite
- optional encrypted OIDC refresh-token vault through
  `memory_oidc_refresh_tokens`; when enabled with
  `Memory.Oidc.RefreshTokenStorage=encrypted_db` and a protection key, Verbeam
  returns refresh handles, refreshes by handle, and stores only AES-GCM
  encrypted token payloads
- admin refresh-handle management through
  `GET /memory/oidc/refresh-tokens` and
  `DELETE /memory/oidc/refresh-tokens/{id}`, exposing metadata without token
  material
- DB-backed revocable hashed session tokens through
  `memory_principal_sessions`
- DB-backed local principal credentials through
  `memory_principal_credentials` and `POST /memory/principal-login`
- principal-aware `rag_context_audit` rows and
  `GET /memory/context-audit` diagnostics for prompt-context snippets and
  exact-memory overrides
- optional `Memory.AdminToken` management gate for
  permission/session/credential/audit/deprovision endpoints until a full
  identity provider is added
- admin-gated `POST /memory/principals/deprovision` for revoking all sessions,
  revoking all local credentials, revoking OIDC refresh handles, and optionally
  deleting ACL rows for a principal
- Workbench Runtime identity controls that apply issued session tokens or raw
  principals to synchronous text, OCR, region, and ASR translation requests
- Workbench principal credential controls that create/revoke hashed local
  credentials and exchange a secret for a runtime session token
- Workbench OIDC controls that start browser login, complete callback exchange,
  refresh OIDC sessions, and apply issued tokens to Runtime identity
- long-running video ASR subtitle translation that persists the resolved
  principal and shared-memory decision in session metadata without storing raw
  session tokens

Not implemented yet:

- no memory-system implementation blockers currently tracked

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

Implemented.

Deliverables:

- deterministic memory retrieval through `IMemoryStore.SearchAsync`
- `MemoryContextBuilder`
- `ProviderTranslationRequest.MemoryContext` or equivalent
- context hash in generated cache key

Success criteria:

- Relevant terms and OCR corrections appear in the provider prompt.
- RAG-disabled requests keep current cache behavior.
- Context hash changes when selected memory changes.
- Shared rows stay out of exact and prompt retrieval unless
  `Memory.SharedMemoryEnabled` is explicitly enabled and the request principal
  resolved from `X-Verbeam-Session` / `X-Verbeam-Principal` passes the
  DB-backed `memory_principal_permissions` ACL or the fallback
  `Memory.SharedMemoryAuthorizedPrincipals` configuration.
- Non-local memory create/update/import/review/conflict routes require
  DB-backed `can_write_memory` and/or `can_approve_memory` permissions for the
  requested profile.
- `memory_principal_permissions.role` gives operators stable profile roles:
  `reader` can use shared memory, `contributor` can also write memory,
  `reviewer` can approve/reject/reconcile memory, and `admin` can do all three.
- `Memory.ExternalIdentity` can trust a reverse proxy/SSO gateway only when its
  shared-secret header matches, then map external groups to the same profile
  roles. Invalid external headers fail closed for memory ACL checks.
- `Memory.BearerJwt` can validate HS256 OAuth-style bearer JWTs directly and
  RS256 JWTs through static `JwksJson` / `JwksPath`, remote `JwksUrl`, or
  `OidcDiscoveryUrl`, including issuer, audience, `exp`, and `nbf`, then reuse
  group-to-role mappings for memory ACLs.
- `Memory.Oidc` can start a browser authorization-code + PKCE login, exchange
  callback codes and refresh tokens at the configured token endpoint, validate
  returned JWTs through `Memory.BearerJwt`, and issue `memory_principal_sessions`
  tokens for runtime requests.
- `X-Verbeam-Session` uses DB-backed hashed session tokens and takes precedence
  over raw principal headers.
- `POST /memory/principal-login` validates a DB-backed hashed local credential
  and issues the same revocable session token path used by admin-created
  sessions.
- `rag_context_audit` records which principal/profile/session selected each
  prompt snippet or exact-memory override, including snippet/context hashes,
  decision, and reason.
- When configured, `Memory.AdminToken` requires management callers to send
  `X-Verbeam-Admin-Token` or `Authorization: Bearer ...` before they can change
  permissions, create/revoke principal credentials and sessions, or inspect
  context audit rows.
- Workbench can apply the issued one-time session token to runtime translation
  calls, so operator-created ACL/session rows can be exercised without custom
  API scripts.
- Workbench can create/revoke local principal credentials and call
  `POST /memory/principal-login`, so the local bootstrap login path can be
  exercised without custom API scripts.
- Video ASR sessions with `Translate=true` use the principal/shared-memory
  decision resolved at session creation for background subtitle translation and
  context audit.

### Phase 3: Recent Context And Summaries

Recent context and scene summary maintenance are implemented.

Deliverables:

- recent `translation_events` retrieval
- `scene_summaries` manual upsert and automatic maintenance
- source event links through `start_event_id` and `end_event_id`

Success criteria:

- Recent session lines appear in prompt within budget.
- Summaries link back to event ranges.
- Long sessions do not grow prompt size linearly.

### Phase 4: Auto Extraction

Translation-memory candidate extraction from repeated stable outputs,
retained-term candidate extraction, and OCR-correction candidate extraction
from repeated applied corrections are implemented. Dedicated candidate
approve/reject review is implemented. Conflict discovery is implemented
through `/memories/conflicts`, winner-based resolution is implemented through
`/memories/{id}/resolve-conflict`, and reviewer-edited merge is implemented
through `/memories/{id}/merge-conflict` and the Workbench Conflicts view.

Deliverables:

- candidate extraction from repeated stable translation events
- candidate extraction from repeated retained terms
- candidate extraction from repeated applied OCR corrections
- duplicate merge for generated candidates
- candidate approve/reject API/UI controls
- conflict discovery API/UI
- winner-based conflict resolve API/UI
- optional advanced diff/merge actions

Success criteria:

- Repeated stable translations, retained terms, and repeated applied OCR
  corrections produce low-confidence candidates.
- User approval promotes candidates.
- User rejection deactivates candidates and marks them rejected.
- Conflicting active rows can be discovered by normalized source.
- Resolving a conflict keeps the selected winner and deactivates competing
  active rows.
- Conflicting auto memories do not override user-approved rows.

### Phase 5: Optional Semantic Retrieval

Deliverables:

- `IEmbeddingProvider` (implemented)
- `memory_embeddings` vector roundtrip (implemented)
- optional lexical+dense ranking (implemented, opt-in)
- background embedding generation (implemented through memory maintenance)
- bounded in-memory cosine benchmark before `sqlite-vec` (implemented)

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
- Scene summaries are generated after the configured successful-event
  threshold and included in debug retrieval.
- Repeated stable translations create `local_generated` candidates with source
  event provenance.
- Repeated retained terms create `local_generated` term candidates with source
  event provenance.
- Repeated applied OCR corrections create `local_generated` OCR correction
  candidates with source event provenance.
- Auto-extracted translation and term candidates do not override user-verified
  exact memory.
- Memory search returns score/debug metadata.
- Provider failure still writes a translation event but not a memory item.
- Durable memory maintenance jobs can be enqueued, drained, completed, and used
  to produce reviewable `local_generated` candidates.

## Open Decisions

- Whether `origin` and `review_status` should become real columns immediately
  or stay in `metadata_json` for phase 1.
- Whether exact user memory should return directly or still pass through the
  provider for style smoothing.
- Whether scene summary should live only in `scene_summaries` or also mirror
  into `memory_items`.
- Memory maintenance uses a SQLite-backed job table for translation candidate
  extraction and embedding prewarm. Successful translations still drain a small
  batch immediately, while admin drain remains available for recovery.
