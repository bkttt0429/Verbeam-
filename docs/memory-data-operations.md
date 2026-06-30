# Memory Data Operations

Verbeam stores runtime cache, translation events, OCR/ASR events, scene
summaries, and RAG memory in the local SQLite database. Treat this file as
sensitive application data: it can contain source text, translations, imported
terms, user corrections, and prompt-context provenance.

## Backup

Preferred backup path:

1. Stop the local Verbeam API process.
2. Copy the database file and its SQLite sidecar files together:
   `translations.sqlite`, `translations.sqlite-wal`, and
   `translations.sqlite-shm`.
3. Store the backup in a user-controlled location with OS file permissions.

If `sqlite3` is available, an online backup is safer while the app is running:

```powershell
sqlite3 "$env:VB_Verbeam__CachePath" ".backup 'D:\Backups\verbeam-translations.sqlite'"
```

Do not copy only the main `.sqlite` file while Verbeam is running in WAL mode;
that can produce an incomplete backup.

## Export

For memory review or migration, export per profile with the dedicated export
endpoint. It includes inactive rows by default so audit and rollback state is
not lost:

```powershell
Invoke-RestMethod "http://localhost:5138/memories/export?profile=default&limit=500" |
  ConvertTo-Json -Depth 20 |
  Set-Content .\verbeam-memory-default.json -Encoding UTF8
```

Keep the trust and provenance fields with every row:

- `trustLevel`
- `sourceUri`
- `sourceHash`
- `createdBy`
- `approvedBy`
- `securityFlagsJson`
- `classification`
- `visibility`
- `isActive`

These fields are part of the safety boundary. Dropping them makes imported data
harder to audit and easier to accidentally trust.

## Import Review

Treat all external exports as untrusted until reviewed. New or migrated rows
should normally enter as `untrusted_import`; Verbeam will quarantine rows with
prompt-injection signals such as hidden Unicode, role markers, or prompt-control
phrases.

Use the import endpoint to apply those safety defaults. It ignores trust and
approval claims in the imported payload, imports rows as `untrusted_import`, and
recomputes security flags locally:

```powershell
$package = Get-Content .\verbeam-memory-default.json -Raw | ConvertFrom-Json
$payload = @{
  profile = "default"
  sourceUri = "import://manual-review/verbeam-memory-default.json"
  importedBy = $env:USERNAME
  items = $package.items
}

Invoke-RestMethod "http://localhost:5138/memories/import" `
  -Method Post `
  -ContentType "application/json" `
  -Body ($payload | ConvertTo-Json -Depth 20)
```

If an imported row has the same profile, memory kind, language pair, and source
text as an existing active row but a different target text, the import response
marks it as a conflict and rejects that row instead of overwriting local memory.

Only use `trusted_import` or `user_verified` after a person has reviewed the
source and approved the content. Exports keep `securityFlagsJson` for audit, but
imports should recompute it locally and avoid automatically clearing
`quarantined`.

Rows with non-empty `securityFlagsJson` require an explicit
`acknowledgeSecurityFlags=true` update before they can be promoted to
`trusted_import` or `user_verified`. Treat that acknowledgement as a record that
the reviewer intentionally inspected the flagged content.

`metadataJson.review_status` follows the effective trust state:

- `approved` for `user_verified` and `trusted_import`
- `pending_review` for `untrusted_import`
- `candidate` for `local_generated`
- `rejected` for inactive auto-extracted candidates rejected by review
- `quarantined` for `quarantined`

## Auto-Extracted Candidates

When `Verbeam:Memory:AutoExtractionEnabled` is enabled, successful repeated
same-session translations can create `memory_kind=translation` candidates, and
repeated retained Title Case source terms can create `memory_kind=term`
candidates. Repeatedly applied OCR corrections in `/ocr/translate` can create
`memory_kind=ocr_correction` candidates. Auto-extracted rows use
`trustLevel=local_generated`, low confidence, and
`metadataJson.review_status=candidate`. They are intentionally excluded from
exact-memory overrides and trusted prompt context until a reviewer promotes
them.

Auto-extraction skips a candidate when the same profile/language/source already
has a trusted memory row with a different target text. If a new stable generated
candidate conflicts with an existing `local_generated` row for the same
normalized source, Verbeam keeps the existing row, skips the overwrite, and
lowers that generated row's confidence so review filters can surface it as less
reliable.

Maintenance work is stored durably in `memory_maintenance_jobs`. Successful
translations enqueue `translation_candidates` jobs when auto extraction is
enabled, and `embedding_prewarm` jobs when semantic retrieval is enabled.
Verbeam drains a small batch after enqueue, but operators can inspect or recover
the queue through admin endpoints:

```powershell
$adminHeaders = @{ "X-Verbeam-Admin-Token" = $env:VB_Verbeam__Memory__AdminToken }

Invoke-RestMethod "http://localhost:5138/memory/maintenance/jobs?profile=default&limit=50" `
  -Headers $adminHeaders

Invoke-RestMethod "http://localhost:5138/memory/maintenance/jobs/drain" `
  -Method Post `
  -ContentType "application/json" `
  -Headers $adminHeaders `
  -Body (@{ limit = 10 } | ConvertTo-Json)
```

Queue rows use `pending`, `running`, `completed`, and `failed` status values.
Stale `running` jobs can be claimed again, and failures retry until the
maintenance attempt limit is reached. A completed `translation_candidates` job
can create or update only reviewable `local_generated` memory; it does not move
that memory into the trusted exact-override path.

## Shared Database Notes

SQLite is best for a single local Verbeam runtime. Before using shared RAG or a
shared database, add an authorization layer for principal, profile, session, and
memory visibility checks. A network share with multiple writers is not a safe
substitute for multi-user database access.

Rows marked `visibility=shared` are stored for review/export purposes, but they
are fail-closed for realtime exact-memory, prompt-context retrieval, and
embedding prewarm unless the local runtime explicitly sets
`Verbeam:Memory:SharedMemoryEnabled=true`.

When shared memory is enabled, request-time retrieval is also gated by
principal. API callers can send `X-Verbeam-Session` to resolve a principal from
`memory_principal_sessions`, or `X-Verbeam-Principal` for local/simple
deployments. Verbeam first checks the durable `memory_principal_permissions`
row for that principal and profile. If a row exists,
`canReadSharedMemory=true` is required. If no DB row exists, Verbeam falls back
to configuration: when
`Verbeam:Memory:SharedMemoryAuthorizedPrincipals` is empty, only the implicit
`local` principal is allowed; otherwise the request principal must match that
allow list.

Example:

```powershell
$env:VB_Verbeam__Memory__SharedMemoryEnabled = "true"
$env:VB_Verbeam__Memory__AdminToken = "replace-with-local-admin-token"
$adminHeaders = @{ "X-Verbeam-Admin-Token" = $env:VB_Verbeam__Memory__AdminToken }

Invoke-RestMethod "http://localhost:5138/memory/principal-permissions" `
  -Method Post `
  -ContentType "application/json" `
  -Headers $adminHeaders `
  -Body (@{
    principal = "alice"
    profile = "default"
    role = "admin"
  } | ConvertTo-Json)

$credential = Invoke-RestMethod "http://localhost:5138/memory/principal-credentials" `
  -Method Post `
  -ContentType "application/json" `
  -Headers $adminHeaders `
  -Body (@{
    principal = "alice"
    label = "workbench-local"
  } | ConvertTo-Json)

$session = Invoke-RestMethod "http://localhost:5138/memory/principal-login" `
  -Method Post `
  -ContentType "application/json" `
  -Body (@{
    principal = "alice"
    secret = $credential.secret
  } | ConvertTo-Json)

Invoke-RestMethod "http://localhost:5138/memory/search?profile=default&source=en&target=zh-TW&q=Moon%20Key" `
  -Headers @{ "X-Verbeam-Session" = $session.sessionToken }

Invoke-RestMethod "http://localhost:5138/memory/context-audit?profile=default&principal=alice&limit=25" `
  -Headers $adminHeaders
```

This gate is a durable profile ACL for shared-memory read, memory write, and
memory approval operations. ACL rows can use role presets (`blocked`, `reader`,
`contributor`, `reviewer`, `admin`) or `custom` read/write/approve booleans.
Local principal credentials and session tokens are stored as hashes.
Credentials can be revoked through
`DELETE /memory/principal-credentials/{id}`, and issued sessions can be revoked
through `DELETE /memory/principal-sessions/{id}`. This is a local bootstrap
login and profile role model, not a full SSO implementation.
To offboard or quarantine a principal in one operation, use
`POST /memory/principals/deprovision` with an admin token. It revokes all active
sessions and local credentials for that principal and can optionally delete all
`memory_principal_permissions` ACL rows for the principal:

```powershell
Invoke-RestMethod "http://localhost:5138/memory/principals/deprovision" `
  -Method Post `
  -ContentType "application/json" `
  -Headers $adminHeaders `
  -Body (@{
    principal = "alice"
    deletePermissions = $true
  } | ConvertTo-Json)
```

`Memory.AdminToken` is an optional local admin gate for
permission/session/audit/credential/deprovision management endpoints. For team
or network deployments, add a real identity provider before treating shared
memory as a full multi-user authorization model.

For deployments behind a trusted reverse proxy or SSO gateway, Verbeam can
accept external identity headers after a shared-secret check:

```json
{
  "Verbeam": {
    "Memory": {
      "ExternalIdentity": {
        "Enabled": true,
        "SharedSecret": "replace-with-proxy-only-secret",
        "PrincipalHeader": "X-Verbeam-External-Principal",
        "GroupsHeader": "X-Verbeam-External-Groups",
        "SharedSecretHeader": "X-Verbeam-External-Token",
        "RoleMappings": [
          { "Group": "verbeam-reviewers", "Profile": "default", "Role": "reviewer" },
          { "Group": "verbeam-admins", "Profile": "*", "Role": "admin" }
        ]
      }
    }
  }
}
```

Only the trusted proxy should be able to set those headers. If an external
identity header is present but the shared secret is missing or wrong, Verbeam
fails closed for memory ACL checks instead of falling back to `local`.

For an internal OAuth/JWT issuer, Verbeam can also validate HS256 bearer tokens
directly:

```json
{
  "Verbeam": {
    "Memory": {
      "BearerJwt": {
        "Enabled": true,
        "Issuer": "https://issuer.example",
        "Audiences": [ "verbeam-memory" ],
        "HmacSecret": "replace-with-jwt-signing-secret",
        "JwksJson": "",
        "JwksPath": "",
        "JwksUrl": "",
        "OidcDiscoveryUrl": "",
        "JwksRefreshSeconds": 300,
        "PrincipalClaim": "sub",
        "GroupsClaim": "groups",
        "ClockSkewSeconds": 60
      },
      "Oidc": {
        "Enabled": true,
        "DiscoveryUrl": "https://issuer.example/.well-known/openid-configuration",
        "AuthorizationEndpoint": "",
        "TokenEndpoint": "",
        "ClientId": "verbeam-memory",
        "ClientSecret": "",
        "RedirectUri": "https://verbeam.example/app",
        "Scopes": [ "openid", "profile", "email" ],
        "StateTtlSeconds": 300,
        "SessionLifetimeMinutes": 480
      }
    }
  }
}
```

Valid bearer JWTs use the same `RoleMappings` group-to-role mapping as the
trusted-proxy bridge. This first-party validator checks HS256 HMAC signatures
or RS256 signatures from static `JwksJson` / `JwksPath`, remote `JwksUrl`, or
an OIDC discovery document at `OidcDiscoveryUrl`, plus issuer, audience, `exp`,
and `nbf`. Remote JWKS is cached by `JwksRefreshSeconds`.

When `Memory.Oidc.Enabled=true`, Verbeam can also start an authorization-code +
PKCE browser login through `GET /memory/oidc/login`, exchange callback
`code`/`state` through `GET /memory/oidc/callback`, validate the returned
ID/access token through the same bearer-JWT verifier, and issue a normal
DB-backed `sessionToken`. `POST /memory/oidc/refresh` can exchange a refresh
token for a new validated token and issue a replacement Verbeam session.
Refresh-token storage is `client_only` by default: `/health` reports that
policy, Workbench keeps the refresh token in a password field, and Verbeam does
not persist the raw refresh token to SQLite.

If a deployment needs refresh tokens retained across browser/app restarts,
enable the encrypted SQLite vault with operator-managed key material:

```json
{
  "Verbeam": {
    "Memory": {
      "Oidc": {
        "RefreshTokenStorage": "encrypted_db",
        "RefreshTokenProtectionKey": "replace-with-operator-managed-secret"
      }
    }
  }
}
```

In `encrypted_db` mode, callback and refresh responses return
`refreshTokenHandle` instead of the raw refresh token. `POST /memory/oidc/refresh`
can use that handle, and Verbeam stores only AES-GCM encrypted token payloads in
`memory_oidc_refresh_tokens`. If the storage mode is `encrypted_db` but the
protection key is missing, OIDC token exchange fails closed instead of returning
or storing the refresh token.

Admins can inspect and revoke handles without seeing token material:

```powershell
Invoke-RestMethod "http://localhost:5138/memory/oidc/refresh-tokens?principal=alice" `
  -Headers $adminHeaders

Invoke-RestMethod "http://localhost:5138/memory/oidc/refresh-tokens/<handle-id>" `
  -Method Delete `
  -Headers $adminHeaders
```

The Workbench Settings > Memory panel can manage the same flow: enter the admin
token, edit shared-memory ACL rows, create/revoke local principal credentials,
login with a credential secret, start/complete OIDC login, refresh an OIDC
session, create/revoke principal sessions, list/revoke refresh handles when the
encrypted vault is enabled, deprovision a principal, copy the one-time session
token into Runtime identity, and review context-audit rows.
Runtime identity sends `X-Verbeam-Session` on text, OCR-translation,
region-translation, and ASR-translation requests; if no session token is set, it
can send `X-Verbeam-Principal` for local/simple deployments. For long-running
video ASR sessions with subtitle translation enabled, Verbeam stores the
resolved principal and shared-memory authorization decision in the session
request metadata; it does not store the raw session token.

For shared deployments, require:

- explicit profile permissions before read/write/approve actions
- principal deprovision runbooks for offboarding and compromised credentials
- per-row `visibility` enforcement before retrieval
- audit review through `GET /memory/context-audit`, including selected memory
  ids, principal, snippet hash, context hash, decision, and reason
- encrypted backups or OS-level access controls
- review workflow before imported/shared memory can enter realtime prompts

## Retrieval Diagnostics

Use `GET /memory/search` while reviewing memory quality and shared-RAG rollout
risk. The response includes `retrievalElapsedMs`, `candidateCount`,
`contextCharacterCount`, and selected snippet counts so profile-level memory
growth is visible before it affects realtime translation latency.

Use `GET /memory/context-audit?profile=...&principal=...` after live traffic to
verify which memory rows actually affected translations. Prompt-context rows use
`reason=memory_context`; exact translation-memory overrides use
`reason=exact_memory_override` and usually have no generated `translationKey`.
The same rows are available in the Workbench Settings > Memory audit panel for
profile/principal review without opening SQLite directly.
