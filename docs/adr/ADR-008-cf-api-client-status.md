# ADR-008: ConcordatoFacile API Client and Status Display

**Status:** Superseded come riferimento del processo ConcordatoFacile (25 ottobre 2026; era Accepted)  
**Date:** 2026-10-23  
**Issue:** none (run R20261023-1107)  
**Informs:** NF-06, NF-08 (ConcordatoFacile practice lookup and status polling)

> **SUPERATO come riferimento del processo ConcordatoFacile (25 ottobre 2026).** Superato dal documento `docs/integrations/concordatofacile/README.md`: non usare come riferimento. Il file resta nel repository perché i commenti del codice e dei test citano gli identificativi delle sue regole (`ADR-008-Rn`): sono etichette, non requisiti validi. I fatti sul processo sono in quel documento, i requisiti in `Sessions/business-analysis-canone-concordato.md`.
>
> Punti che non valgono più o sono sbagliati:
>
> - R2: la variabile è `ConcordatoFacile__StatusApiKey` (non `CONCORDATOFACILE_API_KEY`, che nessun codice legge) e la sua assenza non ferma l'avvio: l'API di stato resta spenta con un avviso nel log.
> - R6: la risposta dell'elenco di CF è l'oggetto `{ leaseId, practices }`, non un array; anche `comuneDiffersFromLink` può essere `null` (specifica CF v0.2). Il codice seguiva la regola sbagliata fino al 25 ottobre; ora legge l'oggetto (task CF-FIX2, backend #611).
> - R11: il controllo che il contratto sia dell'organizzazione del chiamante mancava; c'è dal 25 ottobre (CF-FIX2): un contratto inesistente o altrui è 404 `lease_not_found`.
> - R22: senza chiave ogni lettura fallisce (una 401 è registrata come errore nel log); non si restituisce un elenco vuoto (un errore non è mai «nessuna pratica»).
> - Dal 25 ottobre (CF-FIX2) il lookup per `practiceId` è in cache (15 minuti) e dopo un errore lo snapshot non aggiornato si serve per 5 minuti. Il resto (client con Polly, cache di 15 minuti con rilettura breve di 5, stati, badge) descrive ciò che il codice fa.

## Context

CasaZen calls the ConcordatoFacile (CF) API to retrieve practice status for display to landlords. The API requires resilience patterns for transient errors and rate limiting.

**Requirements** (from `kb/intake/requirements.md` NF-06, NF-08):
- Practice lookup by leaseId
- 15-minute cache, 5-minute brief refresh window after link emission
- Honor 429 rate limit with Retry-After header
- Retain last valid snapshot as stale on error (do not show empty/final)
- 11 practice status values plus unknown fallback

**HITL answers:**
- Q-A07: Polly with retry and timeout, no circuit-breaker
- Q-A08 (default): Honor Retry-After + retry once

**Existing code** (from `kb/gap/gap-analysis.md`):
- HTTP client patterns in `DeepSeekWebSearchClient.cs:16-60` (IHttpClientFactory, timeout, error handling)
- IMemoryCache for status caching

**Repo scope:** Backend (HTTP client, cache service), Frontend (status display UI)

**Mobile:** NOT-APPLICABLE - mobile has no contract detail screens

## Decision

### Typed HTTP Client with Polly Resilience

Create `ConcordatoFacileApiClient` registered with `AddHttpClient<>().AddPolicyHandler()`:

- **Retry policy:** `WaitAndRetryAsync` for transient errors (5xx, TaskCanceledException, TimeoutException, HttpRequestException) with exponential backoff (1s, 2s, 4s, max 3 retries)
- **Timeout policy:** `TimeoutAsync(30 seconds)` per request attempt
- **No circuit-breaker:** CF is partner-controlled API, not commodity; circuit-break would prevent all CF requests for all orgs if one org triggers failures

### 429 Rate Limit Handling

Per Q-A08 default:
- Read `Retry-After` header (seconds or HTTP-date)
- If present and <= 30s: wait, then retry once (not counted against retry policy max 3)
- If missing or > 30s: log warning, propagate error to cache service

### Status Cache Service

`ConcordatoFacileStatusCacheService.GetPracticesAsync(leaseId)`:

- In-memory cache (IMemoryCache) with key `{env}:{orgId}:{leaseId}`
- Sliding expiration 15 minutes (normal) or 5 minutes (brief refresh after link emission)
- Concurrent request coalescing: lock per leaseId, first request fetches, others await result
- On CF API error: retain last valid snapshot as stale (`IsStale=true, LastError=message`), do NOT transform to empty array or final status

### Practice Status Enum

11 values: Received, InProgress, AwaitingUser, DraftSent, SignedReceived, SentToAssociation, Attested, Registered, ClosedWithoutAttestation, Suspended, Unknown

- Unknown status values from CF API MUST be stored as-is and displayed neutrally (raw text)
- MUST NOT default to "completed" or trigger automation for unknown values

### Frontend Status Display

- TanStack Query with `refetchOnMount: true` and `refetchInterval` driven by backend-computed `nextUpdateAt`
- Status badge colors: Received=blue, InProgress=yellow, Attested=yellow (intermediate), Registered=green (final positive), ClosedWithoutAttestation=red (final negative)
- Stale snapshot: show warning + last valid data (not empty state)
- Practice list expanded by default (spec says "lists all")

### Rejected Alternatives

- **Circuit-breaker (Option B)**: Would block all CF requests for all orgs on localized failure
- **No retry, stale cache only (Option C)**: Poor UX for transient network errors

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-008-R1 | P0 | `ConcordatoFacileApiClient` typed HTTP client MUST be registered with `AddHttpClient<>().AddPolicyHandler(retryPolicy).AddPolicyHandler(timeoutPolicy)`, baseAddress from `ConcordatoFacile:StatusApiUrl`, default headers `Authorization: Bearer {ConcordatoFacile:StatusApiKey}` |
| ADR-008-R2 | P0 | CF API Bearer token MUST be loaded from `CONCORDATOFACILE_API_KEY` environment variable; missing in Production MUST fail startup; token value MUST NOT be logged |
| ADR-008-R3 | P0 | Retry policy MUST use Polly `WaitAndRetryAsync` for transient errors (HTTP 5xx, TaskCanceledException, TimeoutException, HttpRequestException) with exponential backoff delays: 1s, 2s, 4s (max 3 retries) |
| ADR-008-R4 | P0 | Timeout policy MUST use Polly `TimeoutAsync(30 seconds)` per request attempt, separate from retry cumulative timeout (total request may take up to 90s for 3 attempts) |
| ADR-008-R5 | P0 | HttpStatusCode.TooManyRequests (429) MUST read `Retry-After` header (seconds or HTTP-date); if present and <= 30s: wait + retry once (not counted against retry policy); if missing or > 30s: log warning and fail |
| ADR-008-R6 | P0 | `GET /v1/leases/{leaseId}/practices` MUST parse JSON array response, deserialize to `List<PracticeDto>`, return empty list for 200 with `[]` body |
| ADR-008-R7 | P0 | `ConcordatoFacileStatusCacheService.GetPracticesAsync(leaseId)` MUST maintain in-memory cache (IMemoryCache) with key `{env}:{orgId}:{leaseId}`, sliding expiration 15 minutes, concurrent request coalescing (lock per leaseId) |
| ADR-008-R8 | P0 | Cache MUST retain last valid snapshot as stale on CF API error: set `IsStale=true`, `LastAttemptAt=UtcNow`, `LastError=exception.Message`; MUST NOT transform error to empty array or final status |
| ADR-008-R9 | P0 | Brief refresh window (5-min for 60 min after link emission) MUST query LeaseEvent WHERE EventType=ConcordatoFacileLinkGenerated AND LeaseContractId=leaseId AND OccurredAt > UtcNow.AddHours(-1); if count > 0 AND practices.Count = 0, reduce cache expiration to 5 minutes |
| ADR-008-R10 | P0 | Status cache MUST key by (environment, orgId, leaseId); MUST NOT return cached snapshot if snapshot.OrgId != User.OrgId (tenant isolation) |
| ADR-008-R11 | P0 | `GET /api/leases/{leaseId}/concordatofacile/status` MUST authorize User with LeaseRead policy and verify leaseId ownership BEFORE calling CF API or reading cached snapshot |
| ADR-008-R12 | P0 | Status endpoint response MUST use `Cache-Control: private, no-store` header |
| ADR-008-R13 | P0 | Unknown practice status values from CF API MUST be stored as-is (ADR-008-R8); MUST NOT default to "completed" or trigger automation |
| ADR-008-R14 | P0 | CF API client MUST validate TLS 1.2 or higher; HTTP (non-TLS) connection MUST be rejected |
| ADR-008-R15 | P0 | Frontend status response MUST include `{practices: [...], lastCheckedAt: ISO8601, nextUpdateAt: ISO8601, stale: boolean}` |
| ADR-008-R16 | P0 | Frontend status section MUST use TanStack Query with `refetchOnMount: true` and `refetchInterval` driven by `nextUpdateAt` |
| ADR-008-R17 | P0 | Frontend stale snapshot (stale=true) MUST show `role="alert"` warning with existing practice list visible |
| ADR-008-R18 | P0 | Frontend status badge MUST map status to colors: Received=blue, InProgress=yellow, AwaitingUser=yellow, DraftSent=blue, SignedReceived=blue, SentToAssociation=blue, Attested=yellow, Registered=green, ClosedWithoutAttestation=red, Suspended=gray; unknown statuses displayed neutrally as raw text |
| ADR-008-R19 | P0 | Frontend MUST display two distinct dates: "Ultimo cambio stato CF: {statusChangedAt}" and "Ultimo controllo CasaZen: {lastCheckedAt}" |
| ADR-008-R20 | P0 | Frontend comuneDiffersFromLink=true MUST show warning "Il comune associato dal partner differisce dalla casa del contratto" |
| ADR-008-R21 | P1 | PracticeStatus enum MUST include 11 values plus Unknown; deserialize unknown string values to Unknown without throwing |
| ADR-008-R22 | P1 | Configuration `ConcordatoFacile:StatusApiKey` missing MUST log error "CF status API disabled" and return empty practices list (graceful degradation) |
| ADR-008-R23 | P1 | i18n keys: `concordatofacile.status.{title, lastCheck, nextUpdate, practiceId, statusLabels.*, closeReasons.*, comuneMismatch, staleWarning, errorMessage, unknownStatus}` |
| ADR-008-R24 | P1 | data-testid: `cf-status-section`, `cf-practice-item`, `cf-status-badge`, `cf-last-check`, `cf-status-error`, `cf-comune-warning` |

## Consequences

**Follow-on work:**
- Backend: `ConcordatoFacileApiClient`, `ConcordatoFacileStatusCacheService`, HTTP client registration with Polly
- Backend: Add `Microsoft.Extensions.Http.Polly` NuGet dependency
- Frontend: Status display component, TanStack Query with refetchInterval
- Tests: Unit test with mock HTTP client for 429/Retry-After handling
- Tests: Unit test for exponential backoff retry

**Risks:**
- Bearer token per environment delivered via Bitwarden Send (never commit in code)
- Rate limit budget: 4 req/hour per contract (15-min) up to 12 req/hour (5-min brief)
- Retry-After > 30s: request takes up to 90s total, acceptable for user page load

**CI cost:** +5s for Polly retry tests (mocked delays)

**Cross-repo contract:**
- Backend: `GET /api/leases/{leaseId}/concordatofacile/status` returns `{practices, lastCheckedAt, nextUpdateAt, stale}`
- Frontend: Polls based on nextUpdateAt, displays stale warning when stale=true
