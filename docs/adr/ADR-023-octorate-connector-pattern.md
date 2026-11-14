# ADR-023: Octorate Channel Manager Connector Pattern

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-27 (channel connection), NF-28 (iCal migration)  
**Amended:** 2026-11-05 (R7 webhook authentication and R11 no Mock adapter: PO decision of 2026-11-04 and the facts proven in the Octorate sandbox, see `kb/plan/octorate-integration-facts.md`)

## Context

CasaZen integrates with Octorate channel manager to enable two-way sync of listings, bookings, rates, and OTA messages. This ADR establishes the connector foundation: OAuth token management, API client abstraction, rate limiting, and error handling. Approximately 60% of in-scope features (56 NFs) depend on this connector.

**Existing code:**
- No Octorate integration exists (`kb/gap/scout-be.md`: NF-27 MISSING, NF-28 MISSING)
- OTA adapter pattern at `Casazen.Infrastructure/OTA/IChannelAdapter.cs` (Octorate is a channel manager, not an OTA; separate adapter)
- Stripe OAuth pattern at `StripeConnectGateway.cs` with ASP.NET Data Protection encryption
- External HTTP client pattern at `AlloggiatiWebService.cs` with retries

**Repo scope:** Backend (connector, OAuth, rate limiter), Frontend (connection wizard, error display), Mobile (none)

## Decision

### Adapter Interface

```csharp
public interface IOctorateApiClient
{
    Task<OctorateProperty[]> GetPropertiesAsync(CancellationToken ct);
    Task<OctorateProperty> GetPropertyAsync(string octoratePropertyId, CancellationToken ct);
    Task UpdatePropertyAsync(string octoratePropertyId, OctoratePropertyUpdate update, CancellationToken ct);
    Task<OctorateReservation[]> GetReservationsAsync(DateTime modifiedAfter, CancellationToken ct);
    Task UpdateReservationStatusAsync(string reservationId, OctorateReservationStatus status, CancellationToken ct);
    Task<OctorateMessageThread[]> GetMessagesAsync(DateTime since, CancellationToken ct);
    Task SendMessageAsync(string threadId, string messageText, CancellationToken ct);
}
```

### OAuth Token Storage

```csharp
public class OctorateConnection : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public OctorateEnvironment Environment { get; set; }  // Test | Production
    public string OctorateAccountId { get; set; }         // Network ID for agencies
    public string EncryptedAccessToken { get; set; }      // ASP.NET Data Protection
    public string EncryptedRefreshToken { get; set; }
    public DateTime TokenExpiresAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public OctorateConnectionStatus Status { get; set; }  // Connected | Disconnected | Error
}

public enum OctorateEnvironment { Test, Production }
public enum OctorateConnectionStatus { Connected, Disconnected, Error }
```

Per Q-A02: one token per tenant + environment + Octorate account (network-level for agencies). Token encrypted with separate ASP.NET Data Protection purpose `Casazen.Octorate.OAuth` (never shared with other secrets).

### OAuth Refresh Flow

```csharp
public async Task<string> GetAccessTokenAsync(Guid orgId, CancellationToken ct)
{
    var connection = await _db.OctorateConnections
        .Where(c => c.OrgId == orgId && c.Environment == _currentEnvironment)
        .SingleOrDefaultAsync(ct);

    if (connection == null)
        throw new OctorateNotConnectedException();

    if (connection.TokenExpiresAt <= DateTime.UtcNow.AddMinutes(5))
    {
        var newToken = await RefreshTokenAsync(connection, ct);
        connection.EncryptedAccessToken = _encryption.Encrypt(newToken.AccessToken);
        connection.EncryptedRefreshToken = _encryption.Encrypt(newToken.RefreshToken);
        connection.TokenExpiresAt = DateTime.UtcNow.AddSeconds(newToken.ExpiresIn);
        await _db.SaveChangesAsync(ct);
        
        _auditLog.Log(new OctorateTokenRefreshed(orgId, connection.Id));
    }

    return _encryption.Decrypt(connection.EncryptedAccessToken);
}
```

### Rate Limiting

Per Q-A03: per-org token bucket (100 requests/minute, 20 burst). Uses `AspNetCoreRateLimit` with org key.

```csharp
public class OctorateRateLimiter
{
    private readonly ConcurrentDictionary<Guid, TokenBucket> _buckets = new();
    
    public async Task<bool> TryAcquireAsync(Guid orgId, CancellationToken ct)
    {
        var bucket = _buckets.GetOrAdd(orgId, _ => new TokenBucket(100, TimeSpan.FromMinutes(1), 20));
        return bucket.TryConsume();
    }
}
```

429 from Octorate triggers exponential backoff (1s, 2s, 4s, max 3 retries). After retries exhausted, throws `OctorateRateLimitException` and queues retry via Hangfire delayed job.

### Webhook Authentication (amended 2026-11-05)

Octorate documents NO signature for reservation, chat or content webhooks (requirements OCT-08 and S24), and the sandbox run of 2026-11-04 confirmed it: the call is a `POST` with the body `{"ping": "<string>"}` and no signature header. The first version of this ADR assumed an HMAC-SHA256 header (`X-Octorate-Signature`); that is not part of the contract. The subscription (`POST /rest/v1/subscription/{event}`, form field `url`, https only) carries no secret, so CasaZen puts the secret in the address it registers and treats the body as a notification only: the object is re-read through the API, and the echo of CasaZen's own writes is ignored.

```csharp
[AllowAnonymous]
[HttpPost("/api/webhooks/octorate/{secret}")]
public async Task<IActionResult> HandleWebhook(string secret)
{
    if (!_verifier.IsValid(secret))   // constant-time comparison with config Octorate:WebhookSecret
    {
        _logger.LogWarning("Invalid Octorate webhook secret from {IP}", HttpContext.Connection.RemoteIpAddress);
        return Unauthorized();        // never log the secret or the body
    }

    // Read the body ONLY for its routing keys (event name, accommodation code, object id),
    // record the event in the outbox with ids only, return 200 within 3s (ADR-022-R19)
    ...
    return Ok();
}
```

**Pending:** the PO has not yet confirmed the secret-in-the-address choice. If the sandbox or Octorate later shows signed requests, the verifier is the only place that changes.

### Error Mapping

| Octorate Status | Exception | .resx Key |
|---|---|---|
| 401 / 403 | `OctorateAuthException` | `octorate.error.unauthorized` |
| 404 | `OctorateNotFoundException` | `octorate.error.notFound` |
| 409 | `OctorateConflictException` | `octorate.error.conflict` |
| 429 | `OctorateRateLimitException` | `octorate.error.rateLimitExceeded` |
| 5xx | `OctorateServerException` | `octorate.error.serverError` |

### No Mock adapter: the client works against the Octorate sandbox (PO decision 2026-11-04)

The first version of this ADR had a `MockOctorateApiClient` with fixture data. The PO decided on 2026-11-04 that there is NO in-memory mock adapter and NO fixture client: `IOctorateApiClient` has one implementation, `OctorateApiClient`, that talks to the real Octorate SANDBOX from the first task, with the real test properties and test data of the Octorate test network. Sandbox and production share one host; the environment is decided by the credentials and reported by `GET /rest/v1/api/configuration` (`environment` = SANDBOX or PRODUCTION; credentials used on the wrong environment answer 403 `WrongEnviroment`, the spelling is Octorate's).

What the sandbox cannot do for CasaZen (the partner `license` is read-only, no OTA is connected to the test network, no `chat` or `invoice` scope) is covered by the OTA simulation in `kb/plan/ota-simulation.md`. Whether to add a CasaZen-side responder that plays Octorate for the push side (availability, content, chat, webhooks) is a separate PO decision, still open.

### Rejected Alternatives

- **IChannelAdapter reuse:** Octorate is a channel manager (aggregating OTAs), not an OTA. Separate interface is clearer.
- **Per-property tokens:** Q-A02 confirmed network-level token covers all properties. Per-property would require re-auth for each.
- **Global rate limit:** Q-A03 confirmed per-org is fairer (one org cannot exhaust quota for all).
- **Inline retry:** Rate limit retry via Hangfire delayed job prevents request timeout and provides observability.

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-023-R1 | P0 | `OctorateConnection` entity (OrgId, Environment enum, OctorateAccountId, EncryptedAccessToken, EncryptedRefreshToken, TokenExpiresAt, LastSyncedAt, Status, ITenantOwned) MUST encrypt tokens via ASP.NET Data Protection with purpose `Casazen.Octorate.OAuth` |
| ADR-023-R2 | P0 | OAuth refresh flow MUST use `IOctorateApiClient.RefreshTokenAsync()`, store new tokens atomically with expiry; MUST NOT log tokens in plaintext |
| ADR-023-R3 | P0 | `IOctorateApiClient` interface with methods: `GetPropertiesAsync`, `GetPropertyAsync`, `UpdatePropertyAsync`, `GetReservationsAsync`, `UpdateReservationStatusAsync`, `GetMessagesAsync`, `SendMessageAsync`; each accepts `CancellationToken` |
| ADR-023-R4 | P0 | All Octorate API calls MUST include tenant context via `orgId` parameter, verify `OctorateConnection.OrgId` matches current `ICurrentUser.OrgId` (tenant isolation); mismatch throws `UnauthorizedAccessException` |
| ADR-023-R5 | P0 | HTTP client MUST use 30s timeout (same as iCal per ADR-002:40), https only, Octorate base URL from config `Octorate:ApiBaseUrl`, User-Agent header `CasaZen/{version}` |
| ADR-023-R6 | P0 | Octorate API errors MUST map to typed exceptions: `OctorateNotFoundException` (404), `OctorateConflictException` (409), `OctorateAuthException` (401/403), `OctorateRateLimitException` (429), with `.resx` IT/EN messages |
| ADR-023-R7 | P0 | **Amended 2026-11-05.** Webhook endpoint `POST /api/webhooks/octorate/{secret}` MUST compare the secret carried by its address with the secret from config `Octorate:WebhookSecret` in constant time (Octorate sends no signature: the HMAC header assumed before is not part of the contract); a wrong secret returns 401, logs a security event (never the secret or the body) and does NOT process the call; the body is a notification only and the object is re-read through the API. Pending: PO confirmation of the secret-in-the-address choice |
| ADR-023-R8 | P0 | Rate limiter (per-org token bucket: 100 requests/minute, 20 burst) MUST gate all `IOctorateApiClient` calls; 429 from Octorate triggers exponential backoff (1s, 2s, 4s, max 3 retries) |
| ADR-023-R9 | P0 | Rate limit exceeded (local or Octorate 429 after retries) MUST throw `OctorateRateLimitException`, log to `ListingChannelState.errorCode = "rate_limit_exceeded"`, retry via Hangfire delayed job (not inline) |
| ADR-023-R10 | P0 | Authorization: channel connection endpoint `POST /api/octorate/connect` MUST require `CasazenPolicies.ManageIntegrations`; listing/booking sync endpoints MUST check property access via `IAuthorizationService` with `HostScope.PropertyIds` |
| ADR-023-R11 | P0 | **Amended 2026-11-05 (PO decision of 2026-11-04).** There is NO mock adapter and NO fixture client: `IOctorateApiClient` has the single implementation `OctorateApiClient`, which works against the real Octorate sandbox from the first task; the environment is decided by the credentials (`GET /rest/v1/api/configuration`; 403 `WrongEnviroment` on a mismatch). The flag `Octorate:UseMockAdapter` does not exist. What the sandbox cannot prove is handled as in `kb/plan/ota-simulation.md` |
| ADR-023-R12 | P0 | Token refresh failure (invalid_grant) MUST set `OctorateConnection.Status = Disconnected`, create "To do" item "Reconnect your channel manager" with urgency=high, MUST NOT retry automatically |
| ADR-023-R13 | P0 | OAuth token access (decrypt + use) MUST log to audit: `OctorateTokenAccessed` event (UserId, OrgId, ConnectionId, OccurredAt); MUST NOT log token value or API responses containing PII |
| ADR-023-R14 | P1 | Proof test `OctorateConnector_RateLimit_EnforcedPerOrg` (unit with mocked time): issue 100 calls for OrgId A, verify 101st throws with retry-after; parallel OrgId B calls unaffected |
| ADR-023-R15 | P1 | Proof test `OctorateConnection_TokenEncrypted_NotPlaintext`: verify stored token cannot be read without decryption; proof test `OctorateClient_OrgScope_Never403ForOtherOrg` (tenant isolation) |

## Consequences

**Follow-on work:**
- Backend: `OctorateConnection` entity and migration, `IOctorateApiClient` implementation (against the sandbox, no mock), OAuth flow, rate limiter, webhook handler (address secret)
- Frontend: Channel connection wizard (consent step, OAuth redirect), connection status display, 429 error handling with retry button
- Mobile: None (Octorate sync is backend concern)
- Tests: Proof tests for encryption, tenant isolation, rate limiting

**Migration order:** ADR-022 (outbox) MUST exist before webhook handling

**Risks:**
- Octorate API behaviour is verified against the sandbox from the first task; what is proven and what is only documented is in `kb/plan/octorate-integration-facts.md` and `kb/plan/capability-evidence.md`
- OAuth token lifetime assumptions (1-hour access, 90-day refresh): verify in sandbox
- Rate limit unknown: Q-A03 proposes 100 req/min; adjust via config after sandbox validation
- Conflict resolution UX (Keep/Take per LST-01:8.2): user may not understand; include help text

**Assumptions pending confirmation:**
- Octorate sends no webhook signature (proven in the sandbox on 2026-11-04); the secret-in-the-address choice awaits the PO's confirmation
- Octorate API error codes (mapped from typical REST patterns; verify in sandbox)

**Feature flag:** `octorate-connector` default OFF until H2 sandbox validated

**CI cost:** +15s for proof tests
