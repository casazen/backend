# ADR-025: Popup Alert Delivery

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-08 (proactive alerts), NF-22 (automation notifications)

## Context

UX-08 requires proactive alerts that appear as popups outside the wizard flow, with "Remind me later" options. Alerts must respect quiet hours, queue one at a time to prevent fatigue, and handle legal obligation overrides.

**Existing code:**
- `src/hooks/use-notifications-realtime.ts` (SignalR, unverified)
- Push notifications exist for mobile
- No popup alert infrastructure

**Repo scope:** Backend (alert entity, routing, quiet hours), Frontend (popup queue, SignalR/polling), Mobile (in-app popup, push routing)

## Decision

### Alert Entity

```csharp
public class PopupAlert : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid UserId { get; set; }
    public AlertType AlertType { get; set; }
    public string EntityType { get; set; }
    public Guid EntityId { get; set; }
    public AlertUrgency Urgency { get; set; }       // High | Normal | Low
    public DateTime CreatedAt { get; set; }
    public DateTime? DismissedAt { get; set; }
    public DateTime? RemindAt { get; set; }
    public DateTime? Deadline { get; set; }         // Legal deadlines
}

public enum AlertType
{
    BookingNeedsReview, CheckInPending, CheckOutPending,
    AlloggiatiDeadline, IstatDeadline,
    ComplianceIssue, ChannelSyncError,
    // ... extensible
}

public enum AlertUrgency { High, Normal, Low }
```

### Quiet Hours (Server-Side)

Per Q-A08: server-side enforcement ensures quiet hours respected even if app is open.

```csharp
public class UserNotificationPreferences : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid UserId { get; set; }
    public TimeOnly QuietHoursStart { get; set; }   // default 22:00
    public TimeOnly QuietHoursEnd { get; set; }     // default 08:00
    public bool LegalAlertsOverrideQuietHours { get; set; } // default true
}
```

Alerts during quiet hours have `RemindAt` set to `QuietHoursEnd` (next day if needed). Legal obligation alerts (Alloggiati, ISTAT deadlines) override quiet hours per requirements.

### Popup Queue (One at a Time)

Per Q-A07: queue model prevents alert fatigue.

```csharp
public async Task<PopupAlert[]> GetPendingAlertsAsync(Guid userId, CancellationToken ct)
{
    var now = DateTime.UtcNow;
    return await _db.PopupAlerts
        .Where(a => a.UserId == userId)
        .Where(a => a.DismissedAt == null)
        .Where(a => a.RemindAt == null || a.RemindAt <= now)
        .OrderByDescending(a => a.Urgency)
        .ThenBy(a => a.CreatedAt)
        .ToArrayAsync(ct);
}
```

Frontend shows one popup at a time; after dismiss/remind, shows next.

### "Remind Me Later" Options

```csharp
public enum ReminderChoice
{
    FifteenMinutes,
    OneHour,
    FourHours,
    TomorrowNineAm
}
```

Backend validates `RemindAt <= Deadline` where deadline exists (can't snooze past legal deadline).

### Delivery Mechanism

SignalR hub for real-time (preferred), with polling fallback:

```csharp
public class AlertHub : Hub
{
    public async Task SendAlert(string userId, PopupAlertDto alert)
    {
        await Clients.User(userId).SendAsync("ReceiveAlert", alert);
    }
}
```

Frontend:
- Attempt SignalR connection at app load
- On connection failure or disconnect, fall back to polling `GET /api/me/alerts/pending` every 30 seconds
- Reconnect SignalR on next page navigation

### Alert Payload (No PII)

Alert payload contains only references, no PII:

```json
{
    "alertId": "uuid",
    "alertType": "BookingNeedsReview",
    "entityType": "Booking",
    "entityId": "uuid",
    "urgency": "High"
}
```

Frontend fetches detail via entity API after popup appears.

### HostScope Filtering

Collaborators with `HostScope.PropertyIds` see only alerts for their assigned properties. Admin/owner sees all org alerts.

### Rejected Alternatives

- **Client-side quiet hours:** User could see alerts during quiet hours if app is open
- **Show all alerts at once (stack):** Overwhelming; one-at-a-time per UX-08
- **SSE instead of SignalR:** SignalR handles reconnection better; Railway supports both

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-025-R1 | P0 | `PopupAlert` table (OrgId, UserId, AlertType enum, EntityType, EntityId, Urgency enum, CreatedAt, DismissedAt nullable, RemindAt nullable, Deadline nullable, ITenantOwned) |
| ADR-025-R2 | P0 | `UserNotificationPreferences` table (UserId, QuietHoursStart, QuietHoursEnd, LegalAlertsOverrideQuietHours, ITenantOwned) with defaults 22:00-08:00 |
| ADR-025-R3 | P0 | Alert router MUST check quiet hours before delivery; alerts during quiet hours MUST set `RemindAt` to `QuietHoursEnd`; legal obligation alerts (Alloggiati, ISTAT) MUST override quiet hours |
| ADR-025-R4 | P0 | `GET /api/me/alerts/pending` MUST filter by HostScope: collaborators see only alerts for assigned properties |
| ADR-025-R5 | P0 | Alert popup payload MUST NOT contain PII; only `{alertId, alertType, entityType, entityId, urgency}`; frontend fetches detail via entity API |
| ADR-025-R6 | P0 | Frontend MUST attempt SignalR connection at app load; on failure, poll `/api/me/alerts/pending` every 30 seconds until reconnected |
| ADR-025-R7 | P0 | AlertPopup component MUST NOT render during wizard (`window.location.pathname` matches `/app/*/wizard/*`); alerts queue until wizard exit |
| ADR-025-R8 | P0 | Frontend popup queue MUST show one alert at a time; "Dismiss" or "Remind me later" shows next alert |
| ADR-025-R9 | P0 | "Remind me later" (15min, 1h, 4h, tomorrow 9am) MUST call `PATCH /api/me/alerts/{id}/remind` with ISO 8601 timestamp |
| ADR-025-R10 | P0 | Backend MUST validate `RemindAt <= Deadline` where deadline exists; invalid choice returns 400 |
| ADR-025-R11 | P1 | i18n keys MUST exist: `alert.remindMeLater.title`, `alert.remindMeLater.15min`, `alert.remindMeLater.1hour`, `alert.remindMeLater.4hours`, `alert.remindMeLater.tomorrow` (IT/EN) |
| ADR-025-R12 | P1 | Mobile MUST implement in-app alert popup (foreground only) with "Do now" and "Remind me later"; reminder sent to backend for push scheduling |
| ADR-025-R13 | P1 | Mobile "Remind me later" MUST send choice to backend; backend schedules push notification at chosen time |

## Consequences

**Follow-on work:**
- Backend: `PopupAlert` entity, `UserNotificationPreferences` entity, alert router, SignalR hub, quiet hours logic
- Frontend: AlertPopup component, SignalR hook with polling fallback, wizard detection, remind API integration
- Mobile: In-app popup component, push notification routing for alerts, "Remind me later" backend integration
- Tests: Quiet hours proof test, HostScope filtering integration test

**Risks:**
- SignalR connection limit on Railway: profile with 100 concurrent users in H2
- Alert fatigue: if org has many properties with issues, queue can grow long; "Dismiss all" deferred to P1
- Quiet hours timezone: uses property timezone (Europe/Rome for Italian); Swiss properties may need adjustment

**CI cost:** +5s for quiet hours proof test
