# Memory System TODO

Last updated: 2026-06-11

This document tracks the remaining work for the local memory/RAG system after
Phase 1 and Phase 2. The detailed architecture is described in
[Headroom-style memory design](headroom-style-memory-design.md), while database
tables and runtime storage behavior are described in
[Database Design](database-design.md). Backup/export/import safety guidance is
tracked in [Memory Data Operations](memory-data-operations.md).

## Current Status

Implemented:

- [x] User corrections can be promoted into `memory_items`.
- [x] Exact `memory_items(memory_kind='translation')` overrides
  provider/cache for the same profile and language pair.
- [x] Active trusted memory can be rendered into provider prompt context.
- [x] Selected memory context hash is included in the generated translation
  cache key.
- [x] Memory usage counters update without changing `updated_at`, so usage
  statistics do not invalidate stable generated-cache keys.
- [x] `GET /memory/search` exposes selected memory, scores, reasons, rendered
  context, and context hash for debugging.
- [x] Recent session context from `translation_events` is included when
  `sessionId` is present, bounded by count and character budgets.
- [x] Scene summaries can be stored in `scene_summaries`, inspected through
  API/debug output, rendered into prompt context, and included in context hash.
- [x] Scene summaries are automatically maintained from successful same-session
  translation events after a conservative event threshold.
- [x] Output policy validation blocks provider/cache/memory responses that leak
  RAG block markers or prompt-control phrases.
- [x] `memory_items.metadata_json.review_status` reflects trust state:
  approved, pending review, candidate, or quarantined.
- [x] Workbench Settings includes a Memory panel for inspecting, filtering,
  creating, editing, deactivating, reactivating, and approving local memory rows.
- [x] `POST /memories/{id}/review` supports dedicated candidate approve/reject
  actions and keeps `metadata_json.review_status` in sync.
- [x] `GET /memories/conflicts` and Workbench Conflicts view expose active rows
  whose normalized source matches but target text differs.
- [x] `POST /memories/{id}/resolve-conflict` and Workbench Resolve can keep a
  selected winner and deactivate competing active rows.
- [x] `POST /memories/{id}/merge-conflict` and Workbench Merge can apply the
  reviewer-edited target/note/score to the selected row, promote it, and
  deactivate competing active rows.
- [x] Repeated stable translation outputs can create low-confidence
  `local_generated` memory candidates with event provenance.
- [x] Repeated retained Title Case terms can create low-confidence
  `local_generated` term candidates with event provenance.
- [x] Repeated applied OCR corrections from `/ocr/translate` can create
  low-confidence `local_generated` OCR correction candidates with event
  provenance.

Not implemented yet:

- No memory-system implementation blockers currently tracked. Future work below
  remains optional or scale-driven.

Recently implemented:

- [x] Optional advanced conflict diff/merge workflow.
- [x] Optional semantic retrieval through embeddings.
- [x] Background embedding generation for trusted memory items.
- [x] In-memory cosine benchmark for the bounded semantic path.
- [x] Local shared-memory runtime gate with `Memory.SharedMemoryEnabled`.
- [x] Header-based shared-memory principal allow list through
  `Memory.SharedMemoryAuthorizedPrincipals` and `X-Verbeam-Principal`.
- [x] DB-backed profile shared-memory read ACL with Workbench management.
- [x] DB-backed profile memory write/approve ACL enforcement.
- [x] DB-backed profile role presets (`blocked`, `reader`, `contributor`,
  `reviewer`, `admin`, `custom`) layered over read/write/approve ACL fields.
- [x] Trusted-proxy external identity bridge with shared-secret verification
  and external group-to-profile-role mapping.
- [x] First-party HS256/RS256 bearer JWT validation with
  issuer/audience/lifetime checks, static JWKS, remote JWKS URL/OIDC discovery
  support, and group-to-profile-role mapping.
- [x] OIDC authorization-code + PKCE login/callback endpoints and refresh-token
  exchange that validate returned JWTs before issuing DB-backed Verbeam session
  tokens.
- [x] Workbench OIDC controls for browser login, callback completion,
  refresh-token exchange, and applying issued session tokens to Runtime
  identity.
- [x] OIDC refresh-token storage policy is explicitly client-only:
  `/health` reports `refreshTokenStorage=client_only`, Workbench keeps the
  token in a password field, and tests assert the raw refresh token is not
  persisted to SQLite.
- [x] Optional encrypted server-side OIDC refresh-token vault:
  `Memory.Oidc.RefreshTokenStorage=encrypted_db` plus
  `RefreshTokenProtectionKey` stores AES-GCM encrypted refresh tokens in
  `memory_oidc_refresh_tokens`, returns a refresh handle instead of raw token,
  supports handle-based refresh, exposes admin list/revoke endpoints for handle
  lifecycle, fails closed when the key is missing, and is revoked by principal
  deprovision.
- [x] DB-backed principal session tokens with revoke support.
- [x] DB-backed local principal credentials with hashed secrets and
  `POST /memory/principal-login` session issuance.
- [x] Workbench Memory panel principal-credential manager for creating,
  listing, revoking, and logging in with DB-backed local credentials.
- [x] Principal-aware `rag_context_audit` rows and
  `GET /memory/context-audit` diagnostics for prompt-context and exact-memory
  usage.
- [x] Workbench Memory panel audit viewer for profile/principal context usage.
- [x] Optional `Memory.AdminToken` gate for permission, credential, session,
  and context audit management endpoints.
- [x] Admin-gated `POST /memory/principals/deprovision` endpoint and Workbench
  lifecycle control for revoking all sessions, revoking all credentials, and
  optionally deleting ACL rows for a principal.
- [x] Workbench Memory panel principal-session manager for creating, listing,
  and revoking DB-backed session tokens.
- [x] Workbench Runtime identity controls that send `X-Verbeam-Session` or
  `X-Verbeam-Principal` on synchronous text, OCR, region, and ASR translation
  requests.
- [x] Long-running video ASR sessions persist the resolved principal and shared
  memory decision when session-level subtitle translation is enabled.
- [x] Durable SQLite-backed `memory_maintenance_jobs` queue for translation
  candidate extraction and embedding prewarm, with `pending`, `running`,
  `completed`, and `failed` states, stale running-job retry, and admin
  list/drain endpoints.

## Recommended Order

1. Revisit `sqlite-vec` only if candidate limits or latency targets outgrow the
   current bounded in-memory scan
2. Add server-retained OIDC refresh tokens only by enabling the encrypted vault
   with operator-managed key material

The recommended order now builds on the debug endpoint, so future memory layers
can be inspected as they are added.

## 1. Debug API

Implemented.

Goal: make memory retrieval observable before more RAG behavior is added.

- [x] Add `GET /memory/search`.
- [x] Accept `profile`, `source`, `target`, `mode`, `sessionId`, `q`, and
  optional `limit`.
- [x] Return selected memory rows with `id`, `memoryKind`, `sourceText`,
  `targetText`, `trustLevel`, `priority`, `confidence`, `score`, and reason.
- [x] Return rendered context preview.
- [x] Return `contextHash`.
- [x] Return `retrievalElapsedMs` for retrieval/build latency diagnostics.
- [x] Return whether prompt memory context is enabled.
- [x] Ensure inactive, quarantined, and untrusted memory are excluded by
  default.
- [x] Add tests for profile isolation.
- [x] Add tests for trust-level filtering.
- [x] Add tests that the debug `contextHash` matches the hash used by
  `/translate`.

Acceptance criteria:

- A developer can explain why a translation used a term or example by calling
  one endpoint.
- Different profiles never see each other's private memory.
- The endpoint does not expose untrusted/quarantined entries unless a future
  explicit debug-only flag is added.

## 2. Phase 3: Recent Context

Implemented.

Goal: use recent translation history without sending the full history to the
provider.

- [x] Add a recent-event retrieval method to `ITranslationEventStore`.
- [x] Scope lookup by `profileId` and `sessionId`.
- [x] Exclude failed events unless explicitly useful for debugging.
- [x] Exclude the current source text to avoid echoing the same request.
- [x] Limit by count and character budget.
- [x] Add recent context to the same memory prompt package after high-priority
  memory snippets.
- [x] Include selected recent event ids and timestamps in context hash.
- [x] Add options for `MaxRecentLines` and `MaxRecentContextCharacters`.
- [x] Add tests that recent context respects session/profile boundaries.
- [x] Add tests that long sessions do not grow prompt size linearly.

Acceptance criteria:

- Recent lines appear in prompt only for the same profile/session.
- Prompt size stays bounded.
- Generated cache key changes only when selected recent context changes.

## 3. Phase 3: Scene Summaries

Manual scene summary storage, prompt integration, and automatic bounded
maintenance from successful session events are implemented.

Goal: keep long sessions useful without growing prompt context linearly.

- [x] Implement scene summary read path from `scene_summaries`.
- [x] Add summary selection to memory context.
- [x] Include `scene_summary_id` and `updated_at` in context hash.
- [x] Add an automatic maintenance path that creates or updates scene summaries
  from event ranges.
- [x] Preserve `start_event_id` and `end_event_id` for traceability.
- [x] Start with deterministic synchronous in-process maintenance after
  successful event writes.
- [x] Add tests that summary updates change cache key.
- [x] Add tests that summaries link back to the covered event range.
- [x] Add tests that automatic maintenance creates summaries only after the
  configured event threshold.

Acceptance criteria:

- Long sessions can provide compact story/context state.
- Summary provenance can be inspected through event ids.
- Summary maintenance is bounded by event count and character budget; high-volume
  async jobs can be added later if latency requires it.

## 4. Memory Management UI

Implemented for Workbench management. Auto-extracted candidates now
appear as ordinary `local_generated` memory rows that can be filtered, edited,
deactivated, promoted, approved, or rejected. Richer conflict resolution is
implemented through conflict discovery, winner-based resolve, and reviewer
edited merge.

Goal: let users inspect, edit, disable, and approve memory without touching
SQLite directly.

- [x] Add memory list view filtered by `profile` and `memoryKind`.
- [x] Add API filters for trust level, active state, source/target language, and
  search text.
- [x] Add API edit flow for `sourceText`, `targetText`, `note`, `priority`,
  `confidence`, and `trustLevel`.
- [x] Add API deactivate/reactivate actions.
- [x] Add Workbench UI for create/edit/filter/trust/active memory operations.
- [x] Add Workbench deactivate/reactivate actions.
- [x] Support first-pass candidate review through existing filters/edit/trust
  controls.
- [x] Add dedicated candidate approve/reject API and Workbench controls.
- [x] Add conflict discovery API and Workbench Conflicts view for competing
  source/target rows.
- [x] Add one-click winner-based resolve action for competing candidate/trusted
  rows.
- [x] Add optional advanced diff/merge workflow for complex conflicts.
- [x] Add profile import/export API for memory rows.
- [x] Add API import conflict indicators for duplicate source terms with different
  targets.
- [x] Add API-level tests for edit/deactivate behavior.

Acceptance criteria:

- A user can correct bad memory without deleting the database.
- Auto-extracted candidates can be reviewed through the general memory panel and
  one-click approve/reject actions.
- Competing active rows can be discovered without direct SQLite queries.
- A selected conflict winner can be promoted and competing rows deactivated
  without direct SQLite edits.
- Profile-scoped memory is easy to inspect and export.

## 5. Phase 4: Auto Extraction

Goal: produce low-confidence memory candidates from real usage while keeping
user-verified memory authoritative.

- [x] Add a memory maintenance service.
- [x] Extract repeated retained terms from successful translation events.
- [x] Extract candidate translation memories from repeated stable outputs.
- [x] Extract OCR correction candidates from OCR correction patterns.
- [x] Store candidates with lower confidence and non-user trust level.
- [x] Merge active duplicate translation candidates by normalized source when
  the generated target is stable.
- [x] Add conflict detection against user-verified memory.
- [x] Add review status in `metadata_json` for approved, imported, generated,
  and quarantined memory.
- [x] Queue bounded candidate maintenance after successful event writes.
- [x] Move translation candidate and embedding maintenance to a durable
  SQLite-backed queue with retryable pending/running/completed/failed states.
- [x] Add tests that auto memory does not override user-verified memory.
- [x] Add tests for duplicate merge and confidence decay.

Acceptance criteria:

- Repeated stable translations, retained terms, and repeatedly applied OCR
  corrections produce reviewable candidates.
- Auto memory stays `local_generated` and cannot override exact user memory;
  approval promotes it into the trusted exact-memory path.
- Conflicting generated candidates are skipped or kept safe by default; richer
  conflict UX remains future work.

## 6. Phase 5: Optional Semantic Retrieval

Goal: improve recall for fuzzy or paraphrased memory while preserving the
deterministic low-latency path.

- [x] Add `IEmbeddingProvider`.
- [x] Add background embedding generation for `memory_items`.
- [x] Store vectors in `memory_embeddings`.
- [x] Add lexical + semantic hybrid ranking.
- [x] Add retrieval timeout and fallback to deterministic lexical retrieval.
- [x] Ensure embeddings are scoped by profile/language pair.
- [x] Benchmark in-memory cosine scan before adopting `sqlite-vec`.
- [x] Add tests that semantic results rank below exact user memory.
- [x] Add tests that timeout returns lexical results.

Current implementation:

- Semantic retrieval is opt-in through `Memory.SemanticRetrievalEnabled`.
- The implementation uses `IEmbeddingProvider`, a deterministic local
  `HashEmbeddingProvider`, lazy per-item generation, background prewarm through
  `MemoryMaintenanceService.MaintainEmbeddingsAsync`, and durable vectors in
  `memory_embeddings`.
- `POST /memories/embeddings/maintain` can precompute or refresh vectors for a
  profile/language pair. Successful translations also queue this maintenance
  when semantic retrieval is enabled.
- `MemoryContextBuilder` keeps the original lexical path as the fallback. If
  embedding lookup/generation exceeds `Memory.SemanticTimeoutMs` or the
  provider/store fails, prompt context is built from deterministic lexical
  scores only.
- Search candidates are still loaded through `IMemoryStore.SearchAsync`, so
  profile, source language, target language, trust, visibility, active state,
  and confidence filters apply before semantic ranking.
- `MemoryRetrievalPerformanceTests` covers a prewarmed 400-candidate semantic
  path and verifies retrieval stays within the bounded latency guard. Keep the
  current in-memory cosine scan until the candidate cap rises materially above
  500 or production telemetry shows the timeout/fallback path is too frequent.

Acceptance criteria:

- Translation still works when embeddings are disabled.
- Semantic retrieval cannot cross profile boundaries.
- Slow embedding retrieval never blocks realtime translation beyond the
  configured timeout.

## 7. Security And Quality Hardening

Goal: make memory safe before shared/imported RAG grows.

- [x] Add tests for `quarantined` memory exclusion.
- [x] Add tests for `untrusted_import` exclusion from realtime prompt context.
- [x] Add cross-profile/shared-memory authorization rules before shared memory
  is enabled.
- [x] Add `Memory.SharedMemoryEnabled` so `visibility=shared` rows remain
  fail-closed for realtime exact/prompt retrieval unless a local runtime
  explicitly opts in.
- [x] Add a request-level shared-memory principal gate:
  `X-Verbeam-Principal` must match
  `Memory.SharedMemoryAuthorizedPrincipals` when an allow list is configured.
- [x] Add `memory_principal_permissions` so a DB row can grant or deny
  profile-scoped shared-memory read access before config fallback is used.
- [x] Extend `memory_principal_permissions` with `can_write_memory` and
  `can_approve_memory`, and enforce them on memory mutation/review routes for
  non-local principals.
- [x] Add `memory_principal_sessions` so requests can use revocable
  `X-Verbeam-Session` tokens instead of trusting raw principal headers.
- [x] Add `memory_principal_credentials` so a local principal secret can be
  validated by `POST /memory/principal-login` and exchanged for a revocable
  session token.
- [x] Add profile role presets to `memory_principal_permissions` so operators
  can manage reader/contributor/reviewer/admin ACLs without hand-tuning every
  boolean.
- [x] Add `Memory.ExternalIdentity` so a trusted reverse proxy / SSO gateway
  can pass external principal/groups through protected headers and map groups
  to profile role presets.
- [x] Add `Memory.BearerJwt` so an internal OAuth/JWT issuer can pass HS256 or
  RS256 bearer tokens that Verbeam validates before applying group role
  mappings.
- [x] Add `rag_context_audit` diagnostics so selected prompt snippets and exact
  memory overrides can be reviewed by profile/principal with context hashes.
- [x] Surface context-audit rows in Workbench for operator review without direct
  SQLite access.
- [x] Add an optional admin token requirement for ACL/session/audit management
  APIs, with Workbench support for `X-Verbeam-Admin-Token`.
- [x] Surface `memory_principal_sessions` lifecycle in Workbench so operators
  can issue one-time tokens and revoke sessions without direct SQLite/API work.
- [x] Surface `memory_principal_credentials` lifecycle in Workbench so
  operators can issue/revoke local secrets and exchange them for session tokens
  without direct SQLite/API work.
- [x] Add principal deprovisioning so operators can revoke all sessions,
  revoke all local credentials, and optionally remove profile ACL rows for a
  departing or compromised principal.
- [x] Apply Workbench Runtime identity to synchronous translation routes that
  read memory: `/translate`, `/ocr/translate`, and `/asr/translate`.
- [x] Persist resolved principal/shared-memory authorization in
  `VideoSpeechSessionRequest` so background video subtitle translation uses the
  same memory scope as the session creation request.
- [x] Add output checks to prevent provider responses from leaking RAG block
  markers or prompt instructions.
- [x] Add ingestion tests for hidden Unicode and role-marker prompt injection.
- [x] Require explicit `acknowledgeSecurityFlags` before flagged memory can be
  approved for realtime use.
- [x] Add performance benchmarks for memory retrieval latency.
- [x] Track prompt context size and selected snippet count in diagnostics.
- [x] Add operational docs for backup/export of SQLite memory data.

Acceptance criteria:

- Prompt context contains only marked data blocks.
- RAG content cannot change output format or access scope.
- Memory retrieval latency and prompt size remain measurable.

## Backlog Decisions

- [ ] Decide whether `origin`, `review_status`, and `source_links_json` should
  become first-class columns instead of living in `metadata_json`; current
  implementation keeps `origin` and `review_status` in `metadata_json`.
- [ ] Decide whether exact user memory should always return directly or
  optionally pass through the provider for style smoothing.
- [ ] Decide whether scene summaries should live only in `scene_summaries` or
  also mirror into `memory_items(memory_kind='scene_summary')`.
- [x] Decide whether memory maintenance should start as an in-process
  background service or a SQLite-backed job table: Verbeam now stores queued
  translation-candidate and embedding-prewarm jobs durably in SQLite, drains
  them opportunistically after successful translations, and exposes admin drain
  as a recovery path.
- [x] Decide first video-session authorization layer:
  `VideoSpeechSessionRequest` persists resolved principal id and shared-memory
  authorization for session-level subtitle translation; raw bearer tokens are
  not stored.
- [x] Decide first shared profile memory gate: local runtime use requires
  `Memory.SharedMemoryEnabled=true`; deployments can additionally require
  `X-Verbeam-Principal` to match `Memory.SharedMemoryAuthorizedPrincipals`.
- [x] Decide first durable ACL layer: `memory_principal_permissions` controls
  profile-scoped shared-memory read, memory write, and memory approve access;
- [x] Decide first profile role layer: `memory_principal_permissions.role`
  stores local role presets over the same read/write/approve flags; external
  proxy group-to-role mapping can feed the same roles.
- [x] Decide first external identity layer: require a shared-secret header for
  proxy-provided principal/groups; bearer JWT validation is handled by
  `Memory.BearerJwt`.
- [x] Decide first JWT validation layer: support HS256 and RS256 bearer JWT
  issuer/audience/exp/nbf validation with static JWKS, remote JWKS URL, and
  OIDC discovery without external package dependencies.
- [x] Decide first OIDC browser-login layer: support authorization-code + PKCE
  login/callback, refresh-token exchange, JWT validation, and DB-backed session
  issuance; refresh-token persistence is either client-only by default or
  encrypted in `memory_oidc_refresh_tokens` when explicitly enabled.
- [x] Decide first durable session layer: `memory_principal_sessions` stores
  hashed, revocable session tokens; `memory_principal_credentials` stores
  hashed local login secrets that issue those sessions.
- [x] Decide first local management gate: `Memory.AdminToken` can protect
  principal permission/session/credential/audit/deprovision endpoints before a
  full identity provider exists.
- [x] Decide first principal deprovision layer: lifecycle offboarding revokes
  DB-backed sessions and credentials by principal id, with optional ACL row
  deletion.
- [x] Decide first OIDC refresh-token vault layer: default to client-only; when
  `encrypted_db` is enabled with a protection key, store only encrypted token
  payloads and expose opaque handles.

## Test Checklist

- [x] Corrected translation is returned before provider call.
- [x] Memory from one profile is not used in another profile.
- [x] Inactive/rejected memory is ignored.
- [x] User-verified memory outranks auto-extracted memory.
- [x] Context hash changes when selected memory item updates.
- [x] Generated cache differs when memory context differs.
- [x] Recent context respects count and character budgets.
- [x] Scene summary updates change generated cache key.
- [x] Memory search returns score/debug metadata.
- [x] Provider failure writes a translation event but not a memory item.
- [x] RAG-disabled requests keep current cache behavior.
- [x] Embedding timeout falls back to lexical retrieval.
- [x] Principal deprovision revokes sessions, revokes credentials, deletes ACL
  rows when requested, and blocks old session/credential reuse.
- [x] Encrypted OIDC refresh-token vault returns handles, refreshes by handle,
  supports admin list/revoke, rejects missing key configuration, avoids
  plaintext SQLite persistence, and is revoked by deprovision.
- [x] Durable memory maintenance jobs can be enqueued, drained, marked
  completed, and produce `local_generated` candidates without entering the
  trusted exact-memory path.
