# ADR-029: Compliance Sender Configuration

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-42 (sender choice), NF-43 (never two senders)

## Context

Italian regulatory compliance (Alloggiati Web, ISTAT) requires exactly one sender per property: either channel manager (Octorate), CasaZen, or manual. Dual send is a legal violation. CHK-03 requires explicit configuration with never-two-senders enforcement.

**Existing code:**
- `AlloggiatiWebReport` entity with submission status
- `OrgQuesturaCredentials` for credentials
- No sender configuration entity

**Repo scope:** Backend (sender entity, enforcement), Frontend (sender wizard), Mobile (none - compliance config is desktop)

## Decision

### Sender Entity

```csharp
public class PropertyComplianceSender : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public ComplianceSenderType AlloggiatiSender { get; set; }
    public ComplianceSenderType IstatSender { get; set; }
    public DateTime LastChangedAt { get; set; }
    public Guid LastChangedByUserId { get; set; }
}

public enum ComplianceSenderType
{
    ChannelManager,  // Octorate sends
    CasaZen,         // CasaZen sends
    Manual           // Host sends manually
}
```

Unique index on `PropertyId` ensures one config per property.

### Never Two Senders Enforcement

Per Q-A21: database constraint + code check.

```csharp
public async Task SetSenderAsync(Guid propertyId, ComplianceSenderType newSender, bool confirmed, CancellationToken ct)
{
    var config = await _db.PropertyComplianceSenders.FindAsync(propertyId, ct);
    var currentSender = config?.AlloggiatiSender ?? ComplianceSenderType.Manual;
    
    if (currentSender != newSender && !confirmed)
    {
        return new SenderChangeResult
        {
            RequiresConfirmation = true,
            Warning = _localizer["sender.switchWarning", currentSender]
        };
    }
    
    if (newSender == ComplianceSenderType.CasaZen)
    {
        // Verify Octorate is not active
        var octorateConnection = await _db.OctorateConnections.FindAsync(config.OrgId, ct);
        if (octorateConnection != null)
        {
            var policeAccountValid = await _octorate.CheckPoliceAccountAsync(ct);
            if (policeAccountValid)
            {
                throw new ConflictException("octorate_sender_active", 
                    _localizer["sender.octorateStillActive"]);
            }
        }
    }
    
    // Disable old sender
    if (currentSender == ComplianceSenderType.CasaZen)
    {
        var credentials = await _db.OrgQuesturaCredentials.FindAsync(config.OrgId, ct);
        if (credentials != null)
        {
            credentials.Enabled = false;
        }
    }
    
    config.AlloggiatiSender = newSender;
    config.IstatSender = newSender;  // Per Q-A22: ISTAT follows Alloggiati
    config.LastChangedAt = DateTime.UtcNow;
    config.LastChangedByUserId = _user.Id;
    
    await _db.SaveChangesAsync(ct);
    _events.Record(DomainEventType.SenderSwitched, propertyId, 
        new { OldSender = currentSender, NewSender = newSender });
}
```

### ISTAT Follows Alloggiati

Per Q-A22: same model for both. Single wizard configures both.

### Sender Activation Wizard

1. Choose sender type (ChannelManager / CasaZen / Manual)
2. If ChannelManager: guided Octorate panel passage (open in new tab, instructions overlay, "I completed it" button)
3. If CasaZen: enter Questura credentials, test connection
4. Confirmation with warning about switching
5. Connection test step

### Send Logic Guard

```csharp
public async Task SendAlloggiatiAsync(Guid bookingId, CancellationToken ct)
{
    var booking = await _db.Bookings.FindAsync(bookingId, ct);
    var senderConfig = await _db.PropertyComplianceSenders.FindAsync(booking.PropertyId, ct);
    
    if (senderConfig?.AlloggiatiSender != ComplianceSenderType.CasaZen)
    {
        _logger.LogInformation("Skipping Alloggiati send: property {PropertyId} sender is {Sender}", 
            booking.PropertyId, senderConfig?.AlloggiatiSender);
        return;  // Never error, just skip
    }
    
    await _soapClient.SendAsync(booking, ct);
}
```

### Rejected Alternatives

- **UI-only prevention:** API call can bypass UI; database + code enforcement required
- **Separate sender for ISTAT:** CHK-05 requires same model; simpler UX with single wizard
- **Auto-retry on switch failure:** Could cause account lockout; explicit user action required

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-029-R1 | P0 | `PropertyComplianceSender` table (PropertyId unique, OrgId, AlloggiatiSender enum, IstatSender enum, LastChangedAt, LastChangedByUserId, ITenantOwned) |
| ADR-029-R2 | P0 | Switching sender from ChannelManager to CasaZen MUST check `OctorateConnection.PoliceAccountValid`; if true, return 409 `octorate_sender_active` |
| ADR-029-R3 | P0 | Sender switch endpoint `PUT /api/properties/{id}/sender` MUST require `confirmed=true` in request; if false, return warning text |
| ADR-029-R4 | P0 | Sender switch MUST log `SenderSwitched` event (PropertyId, OldSender, NewSender, UserId, OccurredAt) for audit |
| ADR-029-R5 | P0 | `IstatSender` MUST follow `AlloggiatiSender` per CHK-05; setting one MUST set both; API rejects mismatch |
| ADR-029-R6 | P0 | Alloggiati send logic MUST verify `Property.AlloggiatiSender = CasaZen` before every send; if not, skip (never error) |
| ADR-029-R7 | P0 | Sender activation wizard MUST check current sender before new activation; show confirmation if sender exists and differs |
| ADR-029-R8 | P0 | Octorate panel passage step MUST open Octorate URL in new tab with instructions overlay; "I completed it" button proceeds |
| ADR-029-R9 | P0 | Connection test step MUST verify credentials with backend endpoint; show green checkmark or red error with guidance |
| ADR-029-R10 | P1 | Proof test `ComplianceSender_DualSendPrevention_NeverTwoActive`: verify simultaneous activation blocked |
| ADR-029-R11 | P1 | Sender switch requires `CasazenPolicies.ManageCompliance` permission; collaborators cannot switch |

## Consequences

**Follow-on work:**
- Backend: `PropertyComplianceSender` entity, sender switch service, send logic guard, audit logging
- Frontend: Sender activation wizard, Octorate panel passage UI, connection test UI
- Mobile: None (compliance configuration is desktop)
- Tests: Dual-send prevention proof test

**Risks:**
- Octorate API `policeAccount` read (Q-002) unconfirmed; fallback to user attestation with warning
- Dual send window (user enables CasaZen while Octorate still active): send logic guard prevents; daily poll for `policeAccount.valid` recommended (P1)
- Sender switch is irreversible (old receipts not recovered); document in confirmation dialog

**CI cost:** +5s for dual-send prevention proof test
