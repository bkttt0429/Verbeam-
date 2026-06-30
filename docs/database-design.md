# Database Design

Verbeam uses one local SQLite database as a runtime memory store. The
current API path should stay simple:

```text
POST /translate
-> normalize request
-> preset + glossary
-> exact user memory
-> memory/recent/scene context
-> generated translation cache
-> provider
-> write cache/event/memory data
-> update bounded scene summary
-> queue durable translation-memory candidate extraction
-> queue optional durable embedding prewarm
-> MORT response and broadcast
```

The schema keeps the existing `translations` table compatible while adding the
tables needed for profiles, sessions, glossary editing, RAG memory, embeddings,
and viewer/history features.

The detailed memory/RAG behavior is described in
[Headroom-style memory design](headroom-style-memory-design.md). Remaining
implementation work is tracked in
[Memory System TODO](memory-system-todo.md).

## Design Principles

- Keep SQLite as the only runtime database for the local app.
- Keep `translations.key` as the generated-cache primary key so current cache
  behavior remains unchanged.
- Store user-trusted knowledge separately from generated cache output.
- Store event/history rows separately from cache rows so cache eviction does not
  erase the user timeline.
- Store vectors as `float32` little-endian BLOBs first; the current bounded
  candidate scan uses in-memory cosine, and a future `sqlite-vec` integration
  can index the same logical data if the candidate cap or latency target grows.
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

Maintenance indexes:

- `idx_translation_events_session_success` supports bounded lookup of recent
  successful events by profile, session, language pair, mode, and timestamp for
  automatic scene summary maintenance and translation-memory candidate
  extraction.

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
| `tags_json`, `metadata_json` | Flexible local metadata, including review status and source event ids. |
| `trust_level` | `user_verified`, `trusted_import`, `local_generated`, `untrusted_import`, or `quarantined`. |
| `source_uri`, `source_hash` | Provenance and integrity fields. |
| `created_by`, `approved_by` | Local audit fields. |
| `security_flags_json`, `classification`, `visibility` | RAG safety and scope controls. |
| `is_active`, `last_used_at`, `use_count` | Lifecycle and usage signals. |

Runtime behavior:

- User corrections and manual rows use trusted levels such as `user_verified`.
- Auto-extracted translation, retained-term, and OCR-correction candidates are
  stored as `trust_level='local_generated'`, `origin='auto-extracted'`, and
  `metadata_json.review_status='candidate'`.
- `POST /memories/{id}/review` approves candidates by promoting them to
  `user_verified`, or rejects candidates by deactivating them and setting
  `metadata_json.review_status='rejected'`.
- `POST /memories/{id}/resolve-conflict` keeps the selected row as winner,
  optionally promotes it, and deactivates competing active rows with the same
  normalized source.
- `POST /memories/{id}/merge-conflict` applies reviewer-edited target, note,
  priority, and confidence fields to the selected row, promotes it by default,
  and deactivates competing active rows with the same normalized source.
- Candidate metadata includes `created_from` values such as
  `auto-translation-memory`, `auto-term-memory`, or
  `auto-ocr-correction-memory`, plus a provenance `source_table`
  (`translation_events` or `ocr_events`), `source_event_ids`, `extractor`, and
  `observation_count`.
- Exact memory override only accepts trusted rows, so generated candidates stay
  reviewable until the user promotes them.
- Rows with `visibility='shared'` are reviewable through management APIs but are
  excluded from realtime exact-memory override, prompt context, and embedding
  prewarm unless `Memory.SharedMemoryEnabled=true`. API realtime retrieval first
  resolves the request principal from `X-Verbeam-Session` when present, then
  falls back to `X-Verbeam-Principal` or implicit `local`. It checks
  `memory_principal_permissions` for that principal and profile. If no DB
  permission row exists, it falls back to
  `Memory.SharedMemoryAuthorizedPrincipals`. Even when enabled, trusted levels,
  profile id, language pair, active state, and security flag checks still apply.

### `memory_principal_permissions`

Durable profile-scoped memory ACL.

| Column | Purpose |
| --- | --- |
| `principal_id` | Request principal, usually from `X-Verbeam-Principal`. |
| `profile_id` | Profile whose memory operations are controlled. |
| `role` | Profile-scoped role preset: `blocked`, `reader`, `contributor`, `reviewer`, `admin`, or `custom`. |
| `can_read_shared_memory` | Whether realtime exact/prompt retrieval can include `visibility='shared'` rows for this profile. |
| `can_write_memory` | Whether memory create/import/update/deactivate operations are allowed. |
| `can_approve_memory` | Whether trust updates, candidate review, conflict resolve/merge, and trusted correction promotion are allowed. |
| `created_at`, `updated_at` | Local ACL audit timestamps. |

Runtime behavior:

- `GET /memory/principal-permissions` lists ACL rows by profile/principal.
- `POST /memory/principal-permissions` upserts a principal/profile permission,
  including a role preset and/or explicit read/write/approve booleans.
- Role presets map to booleans as follows: `blocked` = none, `reader` =
  read shared, `contributor` = read shared + write, `reviewer` = read shared +
  approve, and `admin` = read shared + write + approve. `custom` preserves a
  non-preset boolean combination.
- `DELETE /memory/principal-permissions?profile=...&principal=...` removes the
  DB row so runtime falls back to configuration defaults.
- `POST /memory/principals/deprovision` can optionally delete all ACL rows for a
  principal as part of offboarding.
- If `Memory.AdminToken` is configured, permission management endpoints require
  `X-Verbeam-Admin-Token` or `Authorization: Bearer ...`.
- A present DB row is authoritative for shared-memory read access. A row with
  `can_read_shared_memory=false` denies access even if the config allow list
  would otherwise allow the principal.
- For memory mutation routes, a missing DB row allows only the implicit `local`
  principal. Non-local principals require explicit DB permission. Present DB
  rows are authoritative for `can_write_memory` and `can_approve_memory`.

### External identity bridge

`Memory.ExternalIdentity`, `Memory.BearerJwt`, and `Memory.Oidc` are optional
gateway integrations for deployments that already terminate authentication in a
reverse proxy, SSO sidecar, IdP gateway, or internal OAuth/OIDC issuer.

Runtime behavior:

- The bridge is disabled by default. When enabled, Verbeam reads
  `X-Verbeam-External-Principal`, `X-Verbeam-External-Groups`, and
  `X-Verbeam-External-Token` unless the header names are overridden in config.
- `X-Verbeam-External-Token` must match `Memory.ExternalIdentity.SharedSecret`.
  If an external identity header is present but the token is missing/wrong,
  Verbeam treats the request as an empty principal and does not fall back to
  `local`.
- If `Memory.BearerJwt.Enabled=true`, `Authorization: Bearer ...` can carry a
  HS256 JWT signed by `HmacSecret` or an RS256 JWT signed by a key in
  `JwksJson`, `JwksPath`, remote `JwksUrl`, or the JWKS endpoint discovered
  from `OidcDiscoveryUrl`. Verbeam caches remote JWKS by
  `JwksRefreshSeconds` and validates signature, issuer, audience, expiration,
  and not-before before extracting principal/groups from configured claims.
- If `Memory.Oidc.Enabled=true`, `GET /memory/oidc/login` starts an
  authorization-code + PKCE browser flow, `GET /memory/oidc/callback`
  exchanges the code, validates the returned ID/access token through the same
  bearer-JWT verifier, and creates a normal DB-backed session token.
  `POST /memory/oidc/refresh` exchanges a refresh token and issues a
  replacement Verbeam session after token validation.
- OIDC refresh-token storage is `client_only` by default. If
  `Memory.Oidc.RefreshTokenStorage=encrypted_db` and
  `RefreshTokenProtectionKey` are configured, refresh tokens are stored in
  `memory_oidc_refresh_tokens` as AES-GCM encrypted payloads and API responses
  return an opaque `refreshTokenHandle` instead of the raw token. If
  `encrypted_db` is requested without a protection key, OIDC token exchange
  fails closed.
- `RoleMappings` map external groups to profile-scoped role presets. DB rows in
  `memory_principal_permissions` remain authoritative for an explicit
  principal/profile override; group mappings are used only when no DB row exists
  for that principal/profile.
- `X-Verbeam-Session` still has first priority. A valid session token resolves
  the principal without consulting bearer JWTs or external identity headers.

### `memory_principal_sessions`

Durable session tokens for resolving request principals without trusting a raw
principal header.

| Column | Purpose |
| --- | --- |
| `id` | Session id used for listing and revocation. |
| `principal_id` | Principal resolved from the session token. |
| `token_hash` | SHA-256 hash of the bearer token. The plain token is returned only when the session is created. |
| `created_at`, `expires_at`, `revoked_at`, `last_seen_at` | Session lifecycle and audit timestamps. |

Runtime behavior:

- `POST /memory/principal-sessions` creates a session and returns a one-time
  `sessionToken`.
- `GET /memory/principal-sessions` lists sessions without token hashes.
- `DELETE /memory/principal-sessions/{id}` revokes a session.
- `POST /memory/principals/deprovision` revokes all active sessions for the
  requested principal.
- If `Memory.AdminToken` is configured, session create/list/revoke endpoints
  require the admin token.
- `X-Verbeam-Session` takes precedence over `X-Verbeam-Principal`. If a session
  header is present but expired, revoked, or unknown, the request does not fall
  back to `local`.
- The Workbench Runtime identity controls send `X-Verbeam-Session` for
  synchronous text, OCR-translation, region-translation, and ASR-translation
  requests. If no session token is set, Workbench can send
  `X-Verbeam-Principal` as the local/simple-deployment fallback.
- Long-running video ASR sessions store the resolved principal id and
  shared-memory authorization result in `speech_video_sessions.request_json`
  when session-level subtitle translation is enabled. Raw bearer/session tokens
  are not written to SQLite.

### `memory_principal_credentials`

Local principal secrets used to exchange a principal/password-style credential
for a revocable `memory_principal_sessions` token.

| Column | Purpose |
| --- | --- |
| `id` | Credential id used for listing and revocation. |
| `principal_id` | Principal that can log in with this credential. |
| `label` | Operator-facing label such as device, user, or bootstrap note. |
| `secret_hash` | SHA-256 hash of the local secret. The plain secret is returned only when the credential is created. |
| `created_at`, `expires_at`, `revoked_at`, `last_used_at` | Credential lifecycle and audit timestamps. |

Runtime behavior:

- `POST /memory/principal-credentials` creates a local credential and returns a
  one-time `secret`.
- `GET /memory/principal-credentials` lists credentials without secret hashes.
- `DELETE /memory/principal-credentials/{id}` revokes future logins for that
  credential. Existing sessions remain independently revocable through
  `/memory/principal-sessions/{id}`.
- `POST /memory/principals/deprovision` revokes all active local credentials for
  the requested principal, blocking future `POST /memory/principal-login`
  exchanges for those credentials.
- If `Memory.AdminToken` is configured, credential create/list/revoke endpoints
  require the admin token.
- `POST /memory/principal-login` validates `principal` + `secret` and returns a
  normal one-time `sessionToken` from `memory_principal_sessions`.
- The Workbench Settings > Memory panel can create/list/revoke credentials,
  paste or display the one-time secret, call principal login, and copy the
  issued session token into Runtime identity.

### `memory_oidc_refresh_tokens`

Optional encrypted vault for server-retained OIDC refresh tokens.

| Column | Purpose |
| --- | --- |
| `id` | Opaque refresh handle returned to the caller. |
| `principal_id` | Principal validated from the OIDC ID/access token. |
| `nonce`, `tag`, `ciphertext` | AES-GCM encrypted refresh-token payload; raw tokens are not stored. |
| `created_at`, `updated_at`, `expires_at`, `revoked_at`, `last_used_at` | Vault lifecycle and audit timestamps. |

Runtime behavior:

- Disabled by default. `client_only` keeps refresh tokens in the Workbench
  browser field and does not write them to SQLite.
- `encrypted_db` requires `Memory.Oidc.RefreshTokenProtectionKey`. Missing key
  configuration fails closed.
- `GET /memory/oidc/callback` stores a returned refresh token and returns
  `refreshTokenHandle` when encrypted storage is enabled.
- `POST /memory/oidc/refresh` accepts either a raw `refreshToken` or a
  `refreshTokenHandle`; handle refresh validates that the refreshed JWT resolves
  to the same principal before issuing a new Verbeam session.
- `GET /memory/oidc/refresh-tokens` lists handle metadata by principal without
  exposing nonce, tag, ciphertext, or raw tokens.
- `DELETE /memory/oidc/refresh-tokens/{id}` revokes a single active handle.
- `POST /memory/principals/deprovision` revokes active refresh handles for that
  principal.

### `rag_context_audit`

Append-only audit trail for memory snippets that affect realtime translation.

| Column | Purpose |
| --- | --- |
| `id` | Audit row id. |
| `request_id` | Translation event id that used or attempted to use the memory. |
| `profile_id`, `principal_id`, `session_id` | Runtime scope and caller identity. |
| `translation_key` | Generated cache key when one exists; exact-memory overrides usually leave this null. |
| `memory_id`, `memory_kind` | Selected memory row. |
| `snippet_hash`, `context_hash` | Stable hashes for the selected snippet and full memory context. |
| `trust_level`, `source_hash`, `policy_version` | Trust and policy inputs used at selection time. |
| `context_character_count`, `selected_memory_count`, `selected_recent_event_count` | Prompt/context diagnostics. |
| `decision`, `reason` | Whether the memory was used or blocked, and the runtime reason. |
| `created_at` | Audit timestamp. |

Runtime behavior:

- `TranslationService` writes rows for prompt-context snippets selected into
  provider requests.
- Exact translation-memory overrides also write audit rows, so the strongest
  memory path is traceable even when the provider and generated cache are
  bypassed.
- API callers can inspect recent rows with
  `GET /memory/context-audit?profile=...&principal=...&limit=...`.
- If `Memory.AdminToken` is configured, context-audit reads require the admin
  token because audit rows reveal memory ids, request ids, and principal usage.

### `memory_embeddings`

Optional vector data for semantic retrieval.

| Column | Purpose |
| --- | --- |
| `memory_id` | Parent memory item. |
| `embedding_model` | Model that produced the vector. |
| `dims` | Vector dimensionality. |
| `vector` | `float32` little-endian BLOB. |
| `content_hash` | Detects stale vectors after text changes. |

Runtime behavior:

- Semantic retrieval is opt-in through `Memory.SemanticRetrievalEnabled`; the
  deterministic lexical path remains the default.
- `MemoryContextBuilder` loads candidates through the existing
  profile/language/trust-scoped `IMemoryStore.SearchAsync` path, then applies
  an optional cosine-similarity score using vectors from `memory_embeddings`.
- Missing or stale vectors can be generated lazily during retrieval or
  prewarmed through `MemoryMaintenanceService.MaintainEmbeddingsAsync` and
  `POST /memories/embeddings/maintain`.
- Successful translations queue embedding maintenance for the active
  profile/language pair when semantic retrieval is enabled.
- Slow or failing embedding work during retrieval falls back to lexical ranking
  within `Memory.SemanticTimeoutMs`.
- The current performance guard covers a prewarmed 400-candidate semantic scan.
  With `SemanticCandidateLimit` capped at 500, this avoids adding `sqlite-vec`
  until evidence shows the bounded in-memory scan is no longer enough.

### `memory_maintenance_jobs`

Durable queue for bounded memory maintenance that should survive process
restarts.

| Column | Purpose |
| --- | --- |
| `id` | Job id. |
| `job_kind` | Maintenance kind, currently `translation_candidates` or `embedding_prewarm`. |
| `status` | `pending`, `running`, `completed`, or `failed`. |
| `profile_id`, `session_id` | Scope for event lookup and memory writes. |
| `source_language`, `target_language`, `mode` | Language/mode scope for candidate extraction or embedding prewarm. |
| `attempts` | Number of claim attempts. |
| `error_message` | Last failure message, if any. |
| `created_at`, `updated_at`, `started_at`, `completed_at` | Queue lifecycle timestamps. |

Runtime behavior:

- Successful translation events enqueue `translation_candidates` jobs when auto
  extraction is enabled and `embedding_prewarm` jobs when semantic retrieval is
  enabled.
- `TranslationService` drains a small batch opportunistically after enqueue so
  local use still behaves close to the old in-process path.
- `GET /memory/maintenance/jobs?profile=...&status=...&limit=...` lists recent
  queue rows for operator review.
- `POST /memory/maintenance/jobs/drain` claims and runs a bounded batch. Stale
  `running` jobs are claimable again, and failures are retried until the
  maintenance attempt limit is reached.

### `scene_summaries`

Compact session summaries used to keep prompts small.

| Column | Purpose |
| --- | --- |
| `id` | Summary id. |
| `session_id`, `profile_id` | Scope. |
| `summary_text` | Prompt-ready summary. |
| `start_event_id`, `end_event_id` | Event range covered by this summary. |
| `created_at`, `updated_at` | Timeline fields. |

Runtime behavior:

- `POST /scene-summaries` can create or update a summary manually and validates
  that the event range belongs to the same profile/session.
- `SceneSummaryMaintenanceService` automatically updates one deterministic
  summary row per profile/session/language pair/mode after enough successful
  same-session translations exist.
- `MemoryContextBuilder` reads the latest summary for the current
  profile/session, renders it into prompt context, and includes summary id,
  event range, `updated_at`, and text in the context hash.

Maintenance indexes:

- `idx_scene_summaries_profile_latest` supports latest-summary lookup by
  profile/session and `updated_at`.

## Retrieval Path

The future RAG path should resolve context in this order:

1. Exact trusted user/import memory from `memory_items` where
   `memory_kind = 'translation'`.
2. Glossary terms from JSON or `glossary_terms`.
3. OCR corrections and terms from `memory_items`.
4. Recent rows from `translation_events`.
5. Session summary from `scene_summaries`.
6. Optional semantic matches via `memory_embeddings`.

Generated translations remain in `translations`; user-approved corrections live
in `memory_items` so they can override generated cache instead of competing with
it. Auto-extracted `local_generated` candidates also live in `memory_items`, but
they do not participate in exact override until promoted.

## Current Implementation Status

Schema version `18` is initialized by `SqliteSchema` through the shared
`SqliteDatabase` helper. The runtime enables WAL mode for better concurrent
read/write behavior, applies `foreign_keys = ON`, `busy_timeout = 5000`, and
`synchronous = NORMAL` on opened connections, and caches schema initialization
per database path so ordinary cache/event operations do not rerun the full
schema setup. Initialization also applies additive migrations for existing
SQLite files, including security metadata, principal permissions, principal
roles, principal sessions, principal credentials, and context-audit diagnostics.
Version 17 adds the optional encrypted OIDC refresh-token vault table. Version
18 adds the durable memory maintenance queue.

Translation requests now write success and failure rows through
`SqliteTranslationEventStore`, and recent rows are exposed through
`GET /translation/events`. Generated-cache behavior remains unchanged, while
the runtime database now has a concrete history source for
viewer/debug/session/RAG features.

User-approved translation memory is implemented through `SqliteMemoryStore`.
`GET /memories`, `GET /memories/conflicts`, `POST /memories`,
`PATCH /memories/{id}`, `POST /memories/{id}/review`, and
`POST /memories/{id}/resolve-conflict`, and
`POST /memories/{id}/merge-conflict` manage memory rows, filters, conflict
discovery/resolution/merge, edit fields, trust, candidate review, visibility,
and active state, while
`POST /translation/corrections` promotes a translation event into
`memory_items(memory_kind='translation')`. The Workbench Settings Memory panel
uses those APIs for local inspection, conflict discovery/resolution, candidate
approve/reject, and maintenance. `TranslationService` checks exact
profile-scoped translation memory before calling the provider.

Prompt memory context is now implemented through `MemoryContextBuilder`.
Translation requests retrieve active trusted memory rows for the same
profile/language pair, render only selected terms, OCR corrections, style notes,
and approved examples into the provider prompt, and include the selected memory
context hash in the generated cache key. Usage counters update `last_used_at`
and `use_count` without changing `updated_at`, so memory statistics do not
invalidate stable generated-cache keys.

`rag_context_audit` records selected prompt-context snippets and exact
translation-memory overrides with profile, principal, session, snippet hash,
context hash, decision, and reason fields. `GET /memory/context-audit` exposes a
bounded profile/principal diagnostic view for review and rollout checks.
`POST /memory/principals/deprovision` provides principal-wide offboarding across
sessions, local credentials, OIDC refresh handles, and optional ACL deletion.

Recent session context is also selected from `translation_events` when a
request supplies `sessionId`. The query is scoped by profile, session, language
pair, and mode, excludes failed events and the current source text, and adds the
selected event ids/timestamps to the same context hash used by generated
translation cache keys.

Scene summaries are implemented through `SqliteSceneSummaryStore` and
`SceneSummaryMaintenanceService`. Manual summaries can be inspected and updated
through `/scene-summaries`; automatic summaries are built from a bounded set of
recent successful session events after the configured threshold. The latest
summary is selected into memory context for the same profile/session and changes
the generated translation cache key when its covered event range or text
changes.

Translation and term candidate extraction is implemented through
`MemoryMaintenanceService`. After successful event writes, `TranslationService`
enqueues durable `memory_maintenance_jobs` rows and drains a small batch
opportunistically. The admin API can list queue rows or drain a recovery batch
with `GET /memory/maintenance/jobs` and
`POST /memory/maintenance/jobs/drain`. The service
creates or updates a `local_generated` translation candidate only when the same
normalized source repeatedly produces one stable target, and creates
`local_generated` term candidates when a retained Title Case term repeats across
successful events. It skips exact conflicts with user-verified or
trusted-import rows and records event provenance in `metadata_json`. The same
queue also carries optional embedding prewarm work when semantic retrieval is
enabled.
