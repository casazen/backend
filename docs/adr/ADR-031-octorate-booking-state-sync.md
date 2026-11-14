# ADR-031: Octorate Booking State Sync

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-45 (checkout wizard), NF-73 (booking sync)

## Context

Booking state (check-in, check-out, cancellation) must sync bidirectionally between CasaZen and Octorate. Lock codes must only release after biometric verification. Webhook reconciliation catches missed events.

**Existing code:**
- Checkout wizard exists (`CheckoutWizardController`)
- `BiometricVerificationLog` entity exists
- No Octorate booking sync

**Repo scope:** Backend (webhook handler, sync job, lock code release), Frontend (checkout wizard migration), Mobile (checkout wizard, lock code display)

## Decision

### Webhook Handler

Per ADR-022-R19: respond within 3 seconds.

```csharp
[AllowAnonymous]
[HttpPost("/api/webhooks/octorate")]
public async Task<IActionResult> HandleWebhook()
{
    var signature = Request.Headers["X-Octorate-Signature"].FirstOrDefault();
    var payload = await ReadBodyAsync();
    
    // Verify HMAC-SHA256 signature
    if (!VerifySignature(payload, signature, _config.WebhookSecret))
    {
        _logger.LogWarning("WebhookSignatureInvalid from {IP}", HttpContext.Connection.RemoteIpAddress);
        return Unauthorized();
    }
    
    // Idempotency check
    var eventId = ExtractEventId(payload);
    var existing = await _db.WebhookEvents.FindAsync(eventId);
    if (existing != null)
    {
        return Ok();  // Already processed
    }
    
    // Write to outbox (ADR-022 pattern)
    _db.WebhookEvents.Add(new WebhookEvent { EventId = eventId, ProcessedAt = DateTime.UtcNow });
    _events.Record(DomainEventType.OctorateBookingStateChanged, Guid.Empty, payload);
    await _db.SaveChangesAsync();
    
    return Ok();  // Within 3 seconds
}
```

### Booking State Event Handler

```csharp
public class BookingOctorateEventHandler : IEventHandler<OctorateBookingStateChanged>
{
    public async Task HandleAsync(OctorateBookingStateChanged evt, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<OctorateBookingWebhook>(evt.PayloadJson);
        var booking = await _db.Bookings
            .Where(b => b.OctorateReservationId == payload.ReservationId)
            .SingleOrDefaultAsync(ct);
            
        if (booking == null)
        {
            _logger.LogWarning("Booking not found for Octorate reservation {Id}", payload.ReservationId);
            return;
        }
        
        booking.Status = MapStatus(payload.Status);
        booking.EffectiveCheckInUtc = payload.EffectiveCheckin;
        
        if (payload.Status == "CHECKIN_COMPLETED")
        {
            _events.Record(DomainEventType.CheckInCompleted, booking.Id, new { booking.PropertyId });
        }
        
        await _db.SaveChangesAsync(ct);
    }
}
```

### Lock Code Release

Per Q-A27 (intake Q1-13): only after biometric verification.

```csharp
public class LockCodeReleaseHandler : IEventHandler<BiometricVerificationPassed>
{
    public async Task HandleAsync(BiometricVerificationPassed evt, CancellationToken ct)
    {
        var verification = await _db.BiometricVerificationLogs.FindAsync(evt.VerificationId, ct);
        if (verification?.Verified != true)
        {
            _logger.LogWarning("LockCodeRelease blocked: verification {Id} not verified", evt.VerificationId);
            return;  // Never release without verification
        }
        
        var booking = await _db.Bookings.FindAsync(verification.BookingId, ct);
        await _lockService.ReleaseCodeAsync(booking.PropertyId, booking.Id, ct);
        
        _events.Record(DomainEventType.AccessCodeSent, booking.Id, new { booking.PropertyId });
    }
}
```

### Reconciliation Job

Per Q-A28: every 6 hours.

```csharp
public class OctorateBookingSyncJob
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var connections = await _db.OctorateConnections.Where(c => c.Status == Connected).ToArrayAsync(ct);
        
        foreach (var conn in connections)
        {
            var lastSync = conn.LastSyncedAt ?? DateTime.UtcNow.AddDays(-1);
            var reservations = await _octorate.GetReservationsAsync(lastSync, ct);
            
            foreach (var res in reservations)
            {
                var booking = await _db.Bookings.FindAsync(res.OctorateReservationId, ct);
                if (booking == null) continue;
                
                // Skip if modified locally within 5 minutes (optimistic window)
                if (booking.UpdatedAt > DateTime.UtcNow.AddMinutes(-5))
                    continue;
                    
                if (MapStatus(res.Status) != booking.Status)
                {
                    booking.Status = MapStatus(res.Status);
                    _events.Record(DomainEventType.BookingReconciledFromOctorate, booking.Id, 
                        new { OldStatus = booking.Status, NewStatus = res.Status });
                }
            }
            
            conn.LastSyncedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }
}
```

Hangfire recurring every 6 hours.

### Conflict Detection

Per ADR-027 pattern: if CasaZen modified after last Octorate sync, reconciliation does NOT overwrite.

### Rejected Alternatives

- **Immediate lock code on check-in:** Q-A27 confirms verification required first
- **Manual reconciliation only:** Missed webhooks would cause stale data; automatic 6h interval is safer
- **Lock code from webhook handler:** Separation of concerns; event handler publishes event, lock code handler acts on it

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-031-R1 | P0 | Webhook endpoint `POST /api/webhooks/octorate` MUST verify HMAC-SHA256 signature; invalid returns 401, logs `WebhookSignatureInvalid` |
| ADR-031-R2 | P0 | Webhook processing MUST be idempotent: `WebhookEvent` table (EventId unique); duplicate delivery returns 200, skips processing |
| ADR-031-R3 | P0 | Webhook payload MUST NOT be logged (may contain PII); log only `{eventId, eventType, processed: true}` |
| ADR-031-R4 | P0 | Webhook handler MUST write `OutboxEvent` and return 200 within 3 seconds (ADR-022-R19); no inline processing |
| ADR-031-R5 | P0 | Booking state sync: event handler updates `Booking.Status` from Octorate webhook, publishes secondary events (`CheckInCompleted`, `CheckOutCompleted`) |
| ADR-031-R6 | P0 | Lock code release MUST occur only after `BiometricVerificationPassed` event; `GET /api/bookings/{id}/access-code` returns 403 `verification_required` until verified |
| ADR-031-R7 | P0 | `BiometricVerificationPassed` handler MUST check `BiometricVerificationLog.Verified = true` before releasing code |
| ADR-031-R8 | P0 | Reconciliation job `OctorateBookingSyncJob` (Hangfire every 6 hours) MUST call Octorate `modifiedAfter`, compare states, update mismatches |
| ADR-031-R9 | P0 | Reconciliation MUST NOT overwrite bookings modified locally within 5 minutes (optimistic window) |
| ADR-031-R10 | P0 | Checkout wizard `POST /api/bookings/{id}/checkout` MUST update CasaZen status, write `OutboxEvent`, queue Octorate sync job |
| ADR-031-R11 | P1 | Proof test `LockCode_Release_OnlyAfterVerification`: verify 403 before verification, 200 after |
| ADR-031-R12 | P1 | Proof test `Webhook_InvalidSignature_Returns401`: verify HMAC validation |

## Consequences

**Follow-on work:**
- Backend: Webhook handler with signature verification, idempotency table, booking event handler, lock code release handler, reconciliation job
- Frontend: Checkout wizard migration to guided layer
- Mobile: Checkout wizard, lock code display after verification
- Tests: Webhook signature proof test, lock code timing proof test

**Migration order:** ADR-022 (outbox) and ADR-023 (connector) MUST exist before ADR-031 webhook handling.

**Risks:**
- Webhook reliability: if Octorate webhooks fail, state lags up to 6 hours (reconciliation interval)
- Lock code timing race: event dispatch up to 1 minute; UX should poll for code availability
- Webhook secret rotation: old webhooks fail 401; document rotation procedure

**CI cost:** +10s for proof tests
