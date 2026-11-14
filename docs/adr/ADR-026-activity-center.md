# ADR-026: Activity Center ("To do" List)

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-07 (unified activities), NF-48 (compliance deadlines), NF-52 (staff activities)  
**Amended:** 2026-11-05 (R1, R4: the To do list extends the existing `InternalActivity`; no second activity table. Decided in the plan task E04/F01/US01/T01 after reading the code)

## Context

Hosts and agencies need a unified "To do" list aggregating actionable items: pending check-ins, compliance deadlines, approval queues, and routine reminders. Items must auto-close when conditions are satisfied and filter by collaborator's assigned properties.

**Existing code:**
- `src/features/compliance/use-cockpit.ts` for compliance items
- `src/features/dashboard/dashboard-page.tsx` for scattered items
- No unified "To do" list exists, but an activity entity does: `InternalActivity` (table `InternalActivities`, service `IInternalActivityService`, `InternalActivitiesController` for the agency plan, staff screens `/app/admin/activities`; NF-22). Today it is the work of the staff (cleaning, maintenance, check-in assistance). The "To do" list extends it (see Activity Schema), it does not create a second table

**Repo scope:** Backend (aggregator, auto-close logic), Frontend (activity list, "Do now" navigation), Mobile ("To do" screen, push routing)

## Decision

### Activity Schema

**Amended 2026-11-05.** The sketch below shows the fields of the "To do" item. They are NOT a new table: they are added to the existing `InternalActivity` (table `InternalActivities`, `ITenantOwned`). Mapping: `Action`, `EntityType`, `EntityId`, `Urgency` (`Low = 1`, `Medium = 2`, `High = 3`) become new nullable columns; `DueAt` is the new nullable column `Deadline`; `ClosedAt` is the existing `CompletedAt` together with `Status = "Completed"`; new `Reason` (one line, never personal data) and `ClosedBy`; `PropertyId` and `StaffUserId` become nullable (an organization-level item such as a lost channel connection has no property; the person the item is for is `StaffUserId`). The kind of item is `ActivityType` (existing string column, the names of the new members: `CheckInPending`, `CheckOutPending`, `AlloggiatiDeadline`, `IstatDeadline`, `ReceiptToRetrieve`, `MessageToApprove`, `PublicationError`, `FiscalDocument`, `WizardResume`, `IntegrationError`, `CredentialsToRenew`, `ReconnectChannelManager`; the staff values "Cleaning", "Maintenance", "CheckIn" stay plain text). A staff row has no `Action`; a "To do" row always has one. The service is `IInternalActivityService` (new methods `GetPendingAsync`, `CreateAsync`, `CloseAsync`); there is no `IActivityService`.

```csharp
public class Activity : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public ActivityAction Action { get; set; }
    public string EntityType { get; set; }
    public Guid EntityId { get; set; }
    public ActivityUrgency Urgency { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }         // soft-delete for audit
}

public enum ActivityAction
{
    ReviewBooking,              // /app/short-rent/bookings/{id}
    StartCheckIn,               // /app/short-rent/bookings/{id}/check-in
    CompleteCheckOut,           // /app/short-rent/bookings/{id}/check-out
    VerifyIdentity,             // /app/short-rent/compliance/verification
    UploadAlloggiatiProof,      // /app/short-rent/compliance/alloggiati
    UploadIstatProof,           // /app/short-rent/compliance/istat
    FixListingError,            // /app/short-rent/listings/{id}
    ReplyToMessage,             // /app/short-rent/messages/{threadId}
    ApproveAiDraft,             // /app/short-rent/messages/{threadId}
    ResumeWizard,               // /app/short-rent/{wizardType}/wizard?resume=true
    ReconnectChannel,           // /app/short-rent/settings/channels
    // ... extensible
}

public enum ActivityUrgency { High, Normal, Low }
```

### HostScope Filtering

Per Q-A10: collaborators see only activities for assigned properties.

```csharp
public async Task<Activity[]> GetActivitiesAsync(CancellationToken ct)
{
    var query = _db.Activities
        .Where(a => a.OrgId == _tenant.OrgId)
        .Where(a => a.ClosedAt == null);

    var hostScope = await _authService.GetHostScopeAsync(_user.Id, ct);
    if (hostScope?.PropertyIds != null)
    {
        query = query.Where(a => hostScope.PropertyIds.Contains(a.PropertyId));
    }

    return await query
        .OrderByDescending(a => a.Urgency)
        .ThenBy(a => a.DueAt)
        .ThenBy(a => a.CreatedAt)
        .ToArrayAsync(ct);
}
```

### Auto-Close Logic

Per Q-A09: items auto-close when condition satisfied.

Activities subscribe to events via ADR-022 event bus:

```csharp
public class ActivityAutoCloseHandler : IEventHandler<BookingCheckedIn>
{
    public async Task HandleAsync(BookingCheckedIn evt, CancellationToken ct)
    {
        var activity = await _db.Activities
            .Where(a => a.Action == ActivityAction.StartCheckIn && a.EntityId == evt.BookingId)
            .SingleOrDefaultAsync(ct);
        
        if (activity != null)
        {
            activity.ClosedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
    }
}
```

Closed activities kept for audit (5-year retention per compliance).

### Action Navigation Map

Frontend maps `ActivityAction` to routes:

```typescript
const ACTION_ROUTE_MAP: Record<ActivityAction, (id: string, propertyId?: string) => string> = {
    ReviewBooking: (id) => `/app/short-rent/bookings/${id}`,
    StartCheckIn: (id) => `/app/short-rent/bookings/${id}/check-in`,
    CompleteCheckOut: (id) => `/app/short-rent/bookings/${id}/check-out`,
    VerifyIdentity: (id) => `/app/short-rent/compliance/verification?bookingId=${id}`,
    UploadAlloggiatiProof: () => `/app/short-rent/compliance/alloggiati`,
    UploadIstatProof: () => `/app/short-rent/compliance/istat`,
    FixListingError: (id) => `/app/short-rent/listings/${id}`,
    ReplyToMessage: (id) => `/app/short-rent/messages/${id}`,
    ApproveAiDraft: (id) => `/app/short-rent/messages/${id}?approve=true`,
    ResumeWizard: (id, props) => `${id}?resume=true`,
    ReconnectChannel: () => `/app/short-rent/settings/channels`,
};
```

### Mobile "To do" Screen

New screen at `app/(tabs)/todo.tsx` with urgency-ordered list, "Do now" button per item, badge counter in tab navigation.

### Rejected Alternatives

- **Computed aggregator without entity:** N+1 query performance; activity entity enables indexing and caching
- **Explicit user dismissal:** UX friction; auto-close per Q-A09 is cleaner
- **Show all org activities to collaborators:** Security risk; HostScope filtering required

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-026-R1 | P0 | **Amended 2026-11-05.** The "To do" list is the existing `InternalActivities` table extended with the nullable columns `Action` (enum), `EntityType`, `EntityId`, `Urgency` (enum), `Deadline` (the DueAt of the sketch), `Reason` and `ClosedBy`; `PropertyId` and `StaffUserId` become nullable; the closing time is the existing `CompletedAt` with `Status = "Completed"`; the table stays `ITenantOwned`; there is NO second activity table and NO `IActivityService` (the service is `IInternalActivityService`) |
| ADR-026-R2 | P0 | `GET /api/me/activities` MUST filter by HostScope: collaborators see only activities for assigned properties; admin/owner sees all org activities |
| ADR-026-R3 | P0 | Activity schema `{action, entityType, entityId, propertyId, dueAt, urgency}` MUST NOT include PII; frontend fetches detail via entity API |
| ADR-026-R4 | P0 | Auto-close: when condition satisfied (check-in completed, receipt uploaded), activity MUST set `CompletedAt` (the ClosedAt of the sketch) and `Status = "Completed"`; `GET /api/me/activities` excludes rows with `CompletedAt` set; closed rows kept 5 years for audit |
| ADR-026-R5 | P0 | Frontend activity list MUST render from response array, sorted server-side by urgency+dueAt; no client-side re-sorting |
| ADR-026-R6 | P0 | "Do now" button MUST map `action` field to route via `ACTION_ROUTE_MAP` constant; all values: `ReviewBooking`, `StartCheckIn`, `CompleteCheckOut`, `VerifyIdentity`, `UploadAlloggiatiProof`, `UploadIstatProof`, `FixListingError`, `ReplyToMessage`, `ApproveAiDraft`, `ResumeWizard`, `ReconnectChannel` |
| ADR-026-R7 | P0 | Auto-close MUST refetch `/api/me/activities` after any mutation that could satisfy an activity |
| ADR-026-R8 | P0 | Home widget "To do" summary MUST show count of urgent items with navigation to `/app/short-rent/activities` |
| ADR-026-R9 | P0 | Mobile app MUST implement "To do" screen at `app/(tabs)/todo.tsx` with urgency ordering, "Do now" button per item |
| ADR-026-R10 | P0 | Mobile "Do now" action MUST deep-link to wizard screen at correct step via Expo Router navigation |
| ADR-026-R11 | P0 | Mobile push notifications for activities MUST route to "To do" screen on tap with item highlight |
| ADR-026-R12 | P0 | Mobile tab navigation MUST show badge counter with count of urgent (High) activities only |
| ADR-026-R13 | P1 | Navigation counter (if implemented) MUST show badge with urgent count, updating via SignalR or polling |
| ADR-026-R14 | P1 | Mobile home screen "Now" block MUST show preview of top 3 urgent activities with "Do now" and "See all" |

## Consequences

**Follow-on work:**
- Backend: extension of the `InternalActivity` entity (new columns and migration), pending/create/close methods on `IInternalActivityService`, auto-close event handlers (per ADR-022), HostScope filtering
- Frontend: Activity list page, ACTION_ROUTE_MAP, "To do" widget, refetch after mutations
- Mobile: "To do" screen, deep-linking, push routing, badge counter, home screen preview
- Tests: HostScope filtering integration test, auto-close event handler proof test

**Risks:**
- Aggregator performance: paginated (max 50 items) and indexed (PropertyId, DueAt, ClosedAt)
- Event bus dependency: auto-close lags up to 1 minute per ADR-022 dispatch interval
- Stale items: if entity deleted, activity shows "Not found" on "Do now"; backend should auto-close orphaned activities

**CI cost:** +10s for auto-close proof test
