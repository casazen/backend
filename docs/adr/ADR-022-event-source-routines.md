# ADR-022: Event Source for Automation Routines

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-26, NF-22, NF-24

## Context

Automation routines need an event source to trigger actions (send message, create activity, send access code) based on booking lifecycle events. The architecture must ensure transaction safety and idempotence.

**Operator choice (Q-A54):** Event bus chosen over append-only table.

**Chair decision:** Revert to append-only EventLog table despite operator choice. Multiple seats raised P0 objections about transaction boundary risks (event without domain change, or domain change without event). The EventLog pattern is transaction-safe, testable, and sufficient for phase 1.

**Existing code** (from `kb/gap/gap-analysis.md`):
- No event bus infrastructure exists
- `LeaseEvent` entity for domain-specific events (not generic)
- `RecurringJobsRegistration` for Hangfire jobs

**Repo scope:** Backend (EventLog, routine evaluation, actions), Frontend (routine builder, test mode), Mobile (notification routing only)

## Decision

### Append-Only EventLog Table

Per chair decision accepting O-adr-lead-be-4, O-adr-security-2, O-adr-qa-1:

```csharp
public class EventLog : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public EventType EventType { get; set; }
    public Guid EntityId { get; set; }  // BookingId, ServiceRequestId, etc.
    public DateTime OccurredAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string MetadataJson { get; set; }  // Additional event data
}

public enum EventType
{
    BookingCreated,
    BookingConfirmed,
    BookingCancelled,
    CheckInStarted,
    CheckInCompleted,
    CheckOutCompleted,
    ServiceRequestCreated,
    ServiceRequestCompleted,
    PaymentReceived,
    // ... extensible
}
```

### Transaction Safety

EventLog row MUST be written in same transaction as domain change:

```csharp
public async Task ConfirmBookingAsync(Guid bookingId)
{
    await using var transaction = await _db.Database.BeginTransactionAsync();
    
    var booking = await _db.Bookings.FindAsync(bookingId);
    booking.Status = BookingStatus.Confirmed;
    
    _db.EventLogs.Add(new EventLog
    {
        OrgId = booking.OrgId,
        EventType = EventType.BookingConfirmed,
        EntityId = bookingId,
        OccurredAt = DateTime.UtcNow,
        MetadataJson = JsonSerializer.Serialize(new { booking.GuestId, booking.PropertyId })
    });
    
    await _db.SaveChangesAsync();
    await transaction.CommitAsync();
}
```

### Routine Evaluation Job

`RoutineEvaluationJob` (Hangfire recurring, every 5 minutes per Q-A55):

1. Query `EventLog WHERE ProcessedAt IS NULL`
2. For each event, evaluate matching routines
3. Execute routine actions
4. Set `ProcessedAt = now`
5. Atomic per event (one event fails, others continue)

### Routine Entity

```csharp
public class Routine : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; }
    public EventType TriggerEvent { get; set; }
    public string ConditionDefinitionJson { get; set; }  // ADR-014 condition format
    public string ActionsJson { get; set; }  // Array of RoutineAction
    public bool Enabled { get; set; }
    public bool IsLibrary { get; set; }  // Pre-built template
}
```

### Routine Actions

```json
[
  {"type": "Wait", "days": 2},
  {"type": "SendMessage", "templateId": "uuid", "channel": "email"},
  {"type": "CreateActivity", "activityType": "Cleaning", "assignedStaffUserId": "uuid"},
  {"type": "SendAccessCode"}
]
```

### Execution Log and Idempotence

```csharp
public class RoutineExecution : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid RoutineId { get; set; }
    public Guid EventId { get; set; }  // FK to EventLog
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public RoutineExecutionOutcome Outcome { get; set; }
    public DateTime? ResumeAt { get; set; }  // For waits
    public string ResultJson { get; set; }
}
```

Unique constraint on `(RoutineId, EventId)` prevents duplicate execution.

### Waits

`Wait` action uses Hangfire.Schedule:
- RoutineExecution.ResumeAt tracks continuation
- `RoutineResumeJob` picks up at resume time

### Test Mode Simulation

Per Q-A56 answer, test mode queries last 30 days:
1. Query `EventLog WHERE OccurredAt >= 30 days ago`
2. Evaluate routine conditions
3. Return simulated outcomes (no actual actions)
4. DryRun flag prevents effects

### Action Guardrails

Server-side checks (not condition builder):
- Access code only after verification completed
- No Airbnb contact before booking confirmation
- Marketing only with consent

### Rejected Alternatives

- **Event bus (MediatR/RabbitMQ/Azure Service Bus):** Transaction boundary complexity, outbox pattern required, external dependency. Operator chose this but chair reverted due to P0 objections.
- **MediatR in-process:** No persistence for test mode simulation
- **Real-time processing:** Polling every 5 minutes acceptable for "2 days before check-in" use cases

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-022-R1 | P0 | EventLog table (OrgId, EventType enum, EntityId, OccurredAt, ProcessedAt nullable, MetadataJson, ITenantOwned) |
| ADR-022-R2 | P0 | EventLog row MUST be written in same transaction as domain change (booking, payment, service request, etc.) |
| ADR-022-R3 | P0 | RoutineEvaluationJob (Hangfire recurring every 5 minutes) MUST process unprocessed events, mark ProcessedAt, handle crash recovery |
| ADR-022-R4 | P0 | RoutineExecution table (RoutineId, EventId, StartedAt, CompletedAt, Outcome, ResumeAt, ITenantOwned) with unique constraint (RoutineId, EventId) for idempotence |
| ADR-022-R5 | P0 | Duplicate event (job restart) MUST NOT re-execute routine (idempotence via unique constraint) |
| ADR-022-R6 | P0 | Test mode simulation MUST query EventLog for last 30 days, evaluate routine conditions, return outcomes without effects (DryRun flag) |
| ADR-022-R7 | P0 | Waits (RoutineAction.Wait) MUST use Hangfire.Schedule, RoutineExecution.ResumeAt tracks continuation |
| ADR-022-R8 | P0 | RoutineAction guardrails (access code only after verification, no Airbnb contact before booking, consent for campaigns) MUST be server-side checks |
| ADR-022-R9 | P0 | Routine creation UI MUST include: trigger event picker, condition builder (ADR-014), action list builder, test button |
| ADR-022-R10 | P0 | Routine library (pre-built templates) MUST be org-scoped copies, editable after creation |
| ADR-022-R11 | P1 | Mobile app MUST route routine-triggered push notifications to existing booking/property routes |
| ADR-022-R12 | P1 | Polling frequency configurable via `Routine:EvaluationIntervalMinutes` (default 5) |

## Consequences

**Follow-on work:**
- Backend: EventLog table, RoutineEvaluationJob polling, routine entity + evaluator, execution log, library pre-built routines
- Frontend: Routine creation UI, list + edit, test UI, execution log display, library UI
- Mobile: Notification routing for routine-triggered push types
- Tests: Idempotence proof test `RoutineExecution_SameEvent_ExecutesOnce`, test mode simulation proof test

**Dissent (operator):** Operator chose event bus (Q-A54). Chair reverted to EventLog table due to P0 objections from adr-lead-be (O-adr-lead-be-4), adr-security (O-adr-security-2), and adr-qa (O-adr-qa-1). EventLog pattern is transaction-safe, testable, and sufficient for phase 1. Migrate to event bus in future phase if scalability requires.

**Risks:**
- Polling latency (5 minutes) acceptable for routine use cases
- Job crash recovery: unprocessed events remain, next job picks up
- Transaction boundary: EventLog in same transaction guarantees atomicity

**Feature flag:** `routines` default OFF

**CI cost:** +10s for idempotence and test mode proof tests
