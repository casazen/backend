# ADR-034: Per-Channel State Tracking

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-09 (channel status), NF-38 (property state)

## Context

Properties publish to multiple OTA channels via Octorate. Each channel has its own publication status (draft, pending, published, suspended) and sync state. UX-09 requires "clear state for everything" including per-channel status and sync lag detection.

**Existing code:**
- No per-channel state tracking exists
- Property status is single enum, not per-channel

**Repo scope:** Backend (state entity, sync lag detection), Frontend (multi-channel badges), Mobile (property status read-only)

## Decision

### Per-Channel State Entity

Per Q-A11: separate join table (not embedded in entity).

```csharp
public class ListingChannelState : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public OtaChannel Channel { get; set; }
    public PublicationStatus Status { get; set; }
    public int Completeness { get; set; }               // 0-100%
    public DateTime? LastSyncedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public enum OtaChannel { Airbnb, Booking, Expedia, Vrbo, Direct }

public enum PublicationStatus
{
    Draft,          // Not submitted
    UnderReview,    // Submitted, pending approval
    Published,      // Live on channel
    Suspended,      // Removed by channel
    ManagedOnChannel // Expedia-managed
}
```

Unique index on `(PropertyId, Channel)`.

### Sync Lag Detection

Per Q-A12: `LastSyncedAt` timestamp per channel.

```csharp
public class ChannelStatusDto
{
    public OtaChannel Channel { get; set; }
    public PublicationStatus Status { get; set; }
    public int Completeness { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public int SyncLagSeconds { get; set; }
    public bool SyncAlert { get; set; }                 // true if LastSyncedAt > 24 hours ago
}
```

### Channel Health Check Job

```csharp
public class ChannelHealthCheckJob
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var staleChannels = await _db.ListingChannelStates
            .Where(s => s.LastSyncedAt < DateTime.UtcNow.AddHours(-24))
            .ToArrayAsync(ct);
            
        foreach (var channel in staleChannels)
        {
            _db.Activities.Add(new Activity
            {
                OrgId = channel.OrgId,
                PropertyId = channel.PropertyId,
                Action = ActivityAction.FixListingError,
                Urgency = ActivityUrgency.Normal,
                CreatedAt = DateTime.UtcNow
            });
        }
        
        await _db.SaveChangesAsync(ct);
    }
}
```

Hangfire daily.

### MultiChannelStatus Component

```typescript
function MultiChannelStatus({ channels }: { channels: ChannelStatusDto[] }) {
    return (
        <div className="flex gap-2">
            {channels.map(c => (
                <Badge 
                    key={c.channel} 
                    variant={getVariant(c.status)}
                    title={c.syncAlert ? t('status.syncDelayed') : ''}
                >
                    {c.channel}
                </Badge>
            ))}
        </div>
    );
}

function getVariant(status: PublicationStatus) {
    switch (status) {
        case 'Published': return 'success';     // green
        case 'UnderReview': return 'warning';   // yellow
        case 'Suspended': return 'error';       // red
        case 'ManagedOnChannel': return 'muted'; // gray
        default: return 'outline';
    }
}
```

### Rejected Alternatives

- **Embedded in Listing entity:** Harder to query, harder to add new channels
- **No sync lag tracking:** UX-09 requires "clear state"; stale sync is unclear state
- **Real-time sync check:** Too many API calls; daily health check is sufficient

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-034-R1 | P0 | `ListingChannelState` table (OrgId, PropertyId, Channel enum, Status enum, Completeness 0-100, LastSyncedAt, ErrorCode nullable, ErrorMessage nullable, ITenantOwned) with unique index (PropertyId, Channel) |
| ADR-034-R2 | P0 | `GET /api/properties/{id}/channel-status` MUST return per-channel: Status, Completeness, LastSyncedAt, SyncLagSeconds, SyncAlert (true if LastSyncedAt > 24 hours) |
| ADR-034-R3 | P0 | `ChannelHealthCheckJob` (Hangfire daily) MUST create activity for properties with LastSyncedAt > 24 hours on any channel |
| ADR-034-R4 | P0 | MultiChannelStatus component MUST render per-channel badges with color: green=Published, yellow=UnderReview, red=Suspended, gray=ManagedOnChannel |
| ADR-034-R5 | P0 | Sync lag alert: if LastSyncedAt > 24 hours, property card MUST show warning badge "Sync delayed" |
| ADR-034-R6 | P1 | Mobile property detail screen MUST show per-channel status badges (read-only) |
| ADR-034-R7 | P1 | Mobile property status MUST display sync lag warning when LastSyncedAt > 24 hours |
| ADR-034-R8 | P1 | i18n keys MUST exist: `status.published`, `status.pending`, `status.fixNeeded`, `status.managedOnChannel`, `status.syncDelayed` (IT/EN) |

## Consequences

**Follow-on work:**
- Backend: `ListingChannelState` entity, health check job, status endpoint
- Frontend: MultiChannelStatus component, sync lag warning
- Mobile: Property detail with status badges
- Tests: Sync lag alert integration test

**Tier 2 priority:** This is a Tier 2 ADR with UX quality focus. May be deferred if wave 1 timeline is tight.

**Risks:**
- Sync lag threshold (24h) may need tuning based on Octorate sync frequency
- Completeness calculation formula not specified (deferred to implementation)

**CI cost:** +5s for sync lag test
