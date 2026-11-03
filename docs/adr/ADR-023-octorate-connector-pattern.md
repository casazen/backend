# ADR-023: Octorate Channel Manager Connector Pattern

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-27 (channel connection), NF-28 (iCal migration)

## Context

CasaZen integrates with Octorate channel manager to enable two-way sync of listings, bookings, rates, and OTA messages. This ADR establishes the connector foundation: OAuth token management, API client abstraction, rate limiting, and error handling. Approximately 60% of in-scope features (56 NFs) depend on this connector.

**Existing code:**
- No Octorate integration exists (`kb/gap/scout-be.md`: NF-27 MISSING, NF-28 MISSING)
- OTA adapter pattern at `Casazen.Infrastructure/OTA/IChannelAdapter.cs` (Octorate is a channel manager, not an OTA; separate adapter)
- Stripe OAuth pattern at `StripeConnectGateway.cs` with ASP.NET Data Protection encryption
- External HTTP client pattern at `AlloggiatiWebService.cs` with retries

**Repo scope:** Backend (connector, OAuth, rate limiter, mock adapter), Frontend (connection wizard, error display), Mobile (none)

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

### Webhook Signature Verification

```csharp
[AllowAnonymous]
[HttpPost("/api/webhooks/octorate")]
public async Task<IActionResult> HandleWebhook()
{
    var signature = Request.Headers["X-Octorate-Signature"].FirstOrDefault();
    var payload = await new StreamReader(Request.Body).ReadToEndAsync();
    
    if (!_signatureVerifier.Verify(payload, signature, _config.WebhookSecret))
    {
        _logger.LogWarning("Invalid Octorate webhook signature from {IP}", HttpContext.Connection.RemoteIpAddress);
        return Unauthorized();
    }
    
    // Write to outbox, return 200 within 3s (ADR-022-R19)
    await _events.RecordAsync(DomainEventType.OctorateWebhookReceived, Guid.Empty, payload);
    await _db.SaveChangesAsync();
    
    return Ok();
}
```

### Error Mapping

| Octorate Status | Exception | .resx Key |
|---|---|---|
| 401 / 403 | `OctorateAuthException` | `octorate.error.unauthorized` |
| 404 | `OctorateNotFoundException` | `octorate.error.notFound` |
| 409 | `OctorateConflictException` | `octorate.error.conflict` |
| 429 | `OctorateRateLimitException` | `octorate.error.rateLimitExceeded` |
| 5xx | `OctorateServerException` | `octorate.error.serverError` |

### Mock Adapter (H0-H2)

```csharp
public class MockOctorateApiClient : IOctorateApiClient
{
    private static readonly OctorateProperty[] _properties = LoadFixture("octorate-properties.json");
    
    public Task<OctorateProperty[]> GetPropertiesAsync(CancellationToken ct)
        => Task.FromResult(_properties);
    // ... other methods with fixture data
}
```

Registered via config `Octorate:UseMockAdapter=true`. Mock enforces same auth patterns (signature verification, rate limits) so security controls are tested before Octorate production access.

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
| ADR-023-R7 | P0 | Webhook endpoint `POST /api/webhooks/octorate` MUST verify HMAC-SHA256 signature with secret from config `Octorate:WebhookSecret`; invalid signature returns 401, logs security event, does NOT process payload |
| ADR-023-R8 | P0 | Rate limiter (per-org token bucket: 100 requests/minute, 20 burst) MUST gate all `IOctorateApiClient` calls; 429 from Octorate triggers exponential backoff (1s, 2s, 4s, max 3 retries) |
| ADR-023-R9 | P0 | Rate limit exceeded (local or Octorate 429 after retries) MUST throw `OctorateRateLimitException`, log to `ListingChannelState.errorCode = "rate_limit_exceeded"`, retry via Hangfire delayed job (not inline) |
| ADR-023-R10 | P0 | Authorization: channel connection endpoint `POST /api/octorate/connect` MUST require `CasazenPolicies.ManageIntegrations`; listing/booking sync endpoints MUST check property access via `IAuthorizationService` with `HostScope.PropertyIds` |
| ADR-023-R11 | P0 | Mock adapter `MockOctorateApiClient` with in-memory fixture data (5 properties, 20 bookings, 10 messages), registered via config `Octorate:UseMockAdapter=true`; production MUST set false |
| ADR-023-R12 | P0 | Token refresh failure (invalid_grant) MUST set `OctorateConnection.Status = Disconnected`, create "To do" item "Reconnect your channel manager" with urgency=high, MUST NOT retry automatically |
| ADR-023-R13 | P0 | OAuth token access (decrypt + use) MUST log to audit: `OctorateTokenAccessed` event (UserId, OrgId, ConnectionId, OccurredAt); MUST NOT log token value or API responses containing PII |
| ADR-023-R14 | P1 | Proof test `OctorateConnector_RateLimit_EnforcedPerOrg` (unit with mocked time): issue 100 calls for OrgId A, verify 101st throws with retry-after; parallel OrgId B calls unaffected |
| ADR-023-R15 | P1 | Proof test `OctorateConnection_TokenEncrypted_NotPlaintext`: verify stored token cannot be read without decryption; proof test `OctorateClient_OrgScope_Never403ForOtherOrg` (tenant isolation) |

## Consequences

**Follow-on work:**
- Backend: `OctorateConnection` entity and migration, `IOctorateApiClient` implementation, OAuth flow, rate limiter, webhook handler, mock adapter
- Frontend: Channel connection wizard (consent step, OAuth redirect), connection status display, 429 error handling with retry button
- Mobile: None (Octorate sync is backend concern)
- Tests: Proof tests for encryption, tenant isolation, rate limiting

**Migration order:** ADR-022 (outbox) MUST exist before webhook handling

**Risks:**
- Octorate API unknown until H2 sandbox: mock adapter allows H0-H1 development
- OAuth token lifetime assumptions (1-hour access, 90-day refresh): verify in sandbox
- Rate limit unknown: Q-A03 proposes 100 req/min; adjust via config after sandbox validation
- Conflict resolution UX (Keep/Take per LST-01:8.2): user may not understand; include help text

**Assumptions pending confirmation:**
- Octorate webhook signature format (assumed HMAC-SHA256; verify in sandbox)
- Octorate API error codes (mapped from typical REST patterns; verify in sandbox)

**Feature flag:** `octorate-connector` default OFF until H2 sandbox validated

**CI cost:** +15s for proof tests
