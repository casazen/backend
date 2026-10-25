# ADR-022: Event Source for Automation Routines - event bus with a transactional outbox

**Status:** Accepted (amended 2026-10-25: the operator confirmed the event bus, Q-A54)  
**Date:** 2026-10-24 (amended 2026-10-25)  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-26, NF-22, NF-24

## Context

Automation routines (WHEN event, IF conditions, THEN actions) need events: booking lifecycle, check-in and check-out, payment, ID and document completed, Alloggiati receipt, guest message, review, and time-based triggers ("2 days before check-in", weekly). The design must keep three guarantees: no event without its domain change and no domain change without its event (transaction safety), no routine executed twice for one event (idempotence), and a test mode that replays recent events without effects.

**Operator decision (Q-A54): event bus.** The council draft of 2026-10-24 reverted it to a polled append-only table because of three objections: O-adr-lead-be-4 (P0, extra infrastructure and an outbox needed), O-adr-security-2 (P0, publishing outside the database transaction can create an event without its domain change, or the reverse) and O-adr-qa-1 (P1, more complexity than a table for phase 1). The operator confirmed the event bus on 2026-10-25 and an operator answer is final: this ADR keeps the bus and removes the objections by design. Producers never publish to the bus; they write the event to a transactional outbox in the same transaction as the domain change, and a dispatcher publishes from the outbox (at-least-once) to idempotent subscribers. Phase 1 adds no infrastructure: the bus runs in process behind an interface, and a message broker can replace it later without touching producers.

**Existing code** (from `kb/gap/gap-analysis.md` and the backend snapshot):
- No event bus, no generic event store; no MediatR, MassTransit or broker package is referenced
- `LeaseEvent` entity for domain-specific events (not generic)
- Hangfire (Hangfire.Core 1.8.17, Hangfire.PostgreSql storage) and `RecurringJobsRegistration` for recurring jobs

**Repo scope:** Backend (outbox, bus, dispatcher, routine handler, actions), Frontend (routine builder, test mode), Mobile (notification routing only)

## Decision

### Event bus behind an interface

```csharp
public interface IEventBus
{
    Task PublishAsync(DomainEvent domainEvent, CancellationToken ct);
}

public interface IEventHandler<in T> where T : DomainEvent
{
    Task HandleAsync(T domainEvent, CancellationToken ct);
}
```

Phase 1 implementation: `InProcessEventBus`. It resolves the registered `IEventHandler<T>` of the event from dependency injection and runs each in its own scope under the tenant context of the event's `OrgId`. No new NuGet package and no external broker. A broker adapter (RabbitMQ, Azure Service Bus) is a later `IEventBus` implementation; producers and the outbox do not change.

### Transactional outbox (producers never publish)

```csharp
public class OutboxEvent : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public DomainEventType EventType { get; set; }
    public Guid EntityId { get; set; }          // BookingId, ServiceRequestId, ...
    public DateTime OccurredAt { get; set; }
    public string PayloadJson { get; set; }
    public string? DedupKey { get; set; }       // unique per org when set (time-based triggers)
    public DateTime? DispatchedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public bool DeadLettered { get; set; }
}

public enum DomainEventType
{
    BookingCreated, BookingConfirmed, BookingModified, BookingCancelled,
    CheckInStarted, CheckInCompleted, CheckOutCompleted,
    IdVerificationCompleted, AlloggiatiReceiptArrived,
    ServiceRequestCreated, ServiceRequestCompleted,
    PaymentSucceeded, PaymentFailed,
    GuestMessageReceived, ReviewReceived,
    TimeTrigger,                                 // emitted by TimeEventEmitterJob
    // ... extensible
}
```

A domain service records an event through `IDomainEventRecorder.Record(type, entityId, payload)`, which only adds an `OutboxEvent` to the current `DbContext`. The single `SaveChangesAsync` of the use case then commits the domain change and the event in ONE transaction. Services never call `IEventBus` directly.

```csharp
public async Task ConfirmBookingAsync(Guid bookingId, CancellationToken ct)
{
    var booking = await _db.Bookings.FindAsync([bookingId], ct);
    booking.Status = BookingStatus.Confirmed;

    _events.Record(DomainEventType.BookingConfirmed, bookingId,
        new { booking.GuestId, booking.PropertyId });

    await _db.SaveChangesAsync(ct);   // domain change + outbox row, one transaction
}
```

### Dispatcher

`OutboxDispatcherJob` (Hangfire recurring job registered in `RecurringJobsRegistration`, every minute, the finest resolution Hangfire offers):

1. Read the undispatched, not dead-lettered events ordered by `OccurredAt`, in batches
2. Publish each through `IEventBus`
3. Set `DispatchedAt` only after every handler returned (at-least-once delivery)
4. On failure: `Attempts++`, store `LastError`; after 5 attempts set `DeadLettered = true` (the row is kept and logged) and carry on with the next event

The job must not run concurrently with itself (Hangfire `DisableConcurrentExecution` or an equivalent lock). A crash leaves the rows undispatched and the next run picks them up. Optional fast path: after a commit that recorded events, enqueue one fire-and-forget dispatcher run to cut the latency below one minute.

### Time-based triggers

A domain change cannot say "2 days before check-in". `TimeEventEmitterJob` (Hangfire recurring, `Routine:EvaluationIntervalMinutes`, default 5, proposed default Q-A55) writes `TimeTrigger` events into the outbox, with `PayloadJson` `{kind: BeforeCheckIn | AfterCheckIn | BeforeCheckOut | AfterCheckOut | Weekly, offset, entityId}` and `DedupKey = {kind}:{offset}:{entityId}:{windowStart}`. The unique index on `(OrgId, DedupKey)` makes a restart or an overlapping run unable to emit the same trigger twice.

### Subscriber: routine handler

`RoutineEventHandler` handles every event type a routine can trigger on: it finds the enabled routines of the org whose `TriggerEvent` (and time parameters) match, evaluates `ConditionDefinitionJson` (ADR-014 format) and runs the actions. A failure of one routine is recorded in its `RoutineExecution` (`Outcome = Failed`) and does not fail the event or the other routines; an infrastructure failure is rethrown and the dispatcher retries the event.

### Routine entity

```csharp
public class Routine : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; }
    public DomainEventType TriggerEvent { get; set; }
    public string ConditionDefinitionJson { get; set; }  // ADR-014 condition format
    public string ActionsJson { get; set; }              // array of RoutineAction
    public bool Enabled { get; set; }
    public bool IsLibrary { get; set; }                  // pre-built template
}
```

### Routine actions

```json
[
  {"type": "Wait", "days": 2},
  {"type": "SendMessage", "templateId": "uuid", "channel": "email"},
  {"type": "CreateActivity", "activityType": "Cleaning", "assignedStaffUserId": "uuid"},
  {"type": "SendAccessCode"}
]
```

### Execution log and idempotence

```csharp
public class RoutineExecution : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid RoutineId { get; set; }
    public Guid EventId { get; set; }                    // FK to OutboxEvent
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public RoutineExecutionOutcome Outcome { get; set; }
    public DateTime? ResumeAt { get; set; }              // for waits
    public string ResultJson { get; set; }
}
```

Unique constraint on `(RoutineId, EventId)`: the at-least-once delivery of the bus can hand the same event twice and the second run is ignored.

### Waits

`Wait` uses Hangfire scheduling: `RoutineExecution.ResumeAt` tracks the continuation and `RoutineResumeJob` picks it up at the resume time.

### Test mode simulation

Test mode reads the `OutboxEvent` rows of the last 30 days (proposed default Q-A56):
1. Query `OutboxEvent WHERE OccurredAt >= now - 30 days`
2. Evaluate the routine conditions
3. Return the simulated outcomes (no actual action)
4. `DryRun` flag: the simulation never calls `IEventBus`, never writes `RoutineExecution`, never sends anything

`OutboxCleanupJob` (daily) deletes dispatched events older than `EventBus:RetentionDays` (default 60, minimum 30) so the lookback of the test mode is always available.

### Action guardrails

Server-side checks (not the condition builder):
- Access code only after verification completed
- No Airbnb contact before booking confirmation
- Marketing only with consent

### Rejected alternatives

- **Polled append-only EventLog without a bus** (the council draft B): simple and transaction-safe, but the operator chose the event bus. Its good half is kept: the outbox is append-only, queryable and provides the replay of the test mode.
- **Publishing to the bus directly after the commit, or inside the request**: not atomic (an event lost when the process dies between commit and publish, or published for a change that rolled back); this is objection O-adr-security-2.
- **External broker in phase 1 (RabbitMQ, Azure Service Bus)**: a new service to run, secure and pay for; deferred behind `IEventBus`.
- **A bus without persistence**: no replay for the test mode and no recovery after a crash.

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-022-R1 | P0 | Domain changes MUST NOT publish to the bus directly: they record an `OutboxEvent` through `IDomainEventRecorder` on the same `DbContext` and the same `SaveChanges` (one transaction) as the domain change |
| ADR-022-R2 | P0 | `OutboxEvent` table (OrgId, EventType enum, EntityId, OccurredAt, PayloadJson, DedupKey nullable, DispatchedAt nullable, Attempts, LastError, DeadLettered, ITenantOwned) with a unique index on (OrgId, DedupKey) where DedupKey is not null |
| ADR-022-R3 | P0 | `IEventBus` and `IEventHandler<T>` abstractions; phase 1 is `InProcessEventBus` with no new NuGet package and no external broker; a broker adapter MUST NOT require changes in producers or in the outbox |
| ADR-022-R4 | P0 | `OutboxDispatcherJob` (Hangfire recurring, every minute, not concurrent with itself) MUST publish undispatched events in OccurredAt order, set DispatchedAt only after every handler returned (at-least-once) and recover after a crash by picking up the undispatched rows |
| ADR-022-R5 | P0 | A failing event MUST be retried; after 5 attempts it is dead-lettered (row kept, logged) and MUST NOT block the other events |
| ADR-022-R6 | P0 | Handlers MUST run under the tenant context of the event's OrgId, each in its own dependency-injection scope; the dispatcher runs in a system context |
| ADR-022-R7 | P0 | `RoutineExecution` table (RoutineId, EventId, StartedAt, CompletedAt, Outcome, ResumeAt, ITenantOwned) with a unique constraint (RoutineId, EventId); a duplicate delivery MUST NOT execute a routine twice |
| ADR-022-R8 | P0 | `TimeEventEmitterJob` (Hangfire recurring, `Routine:EvaluationIntervalMinutes`, default 5) MUST emit time-based triggers into the outbox with a DedupKey so that a restart or an overlap cannot emit the same trigger twice |
| ADR-022-R9 | P0 | Test mode simulation MUST query `OutboxEvent` for the last 30 days, evaluate the routine conditions and return outcomes without effects (DryRun): it MUST NOT call `IEventBus` and MUST NOT write `RoutineExecution` |
| ADR-022-R10 | P0 | Waits (RoutineAction.Wait) MUST use Hangfire scheduling; `RoutineExecution.ResumeAt` tracks the continuation |
| ADR-022-R11 | P0 | RoutineAction guardrails (access code only after verification, no Airbnb contact before booking, consent for campaigns) MUST be server-side checks |
| ADR-022-R12 | P0 | Routine creation UI MUST include: trigger event picker, condition builder (ADR-014), action list builder, test button |
| ADR-022-R13 | P0 | Routine library (pre-built templates) MUST be org-scoped copies, editable after creation |
| ADR-022-R14 | P1 | Mobile app MUST route routine-triggered push notifications to existing booking/property routes |
| ADR-022-R15 | P1 | `OutboxCleanupJob` (daily) MUST delete dispatched events older than `EventBus:RetentionDays` (default 60, never below 30); dead-lettered rows are never deleted automatically |
| ADR-022-R16 | P1 | A commit that recorded events MAY enqueue one fire-and-forget dispatcher run (fast path) to reduce the latency below one minute |

## Consequences

**Follow-on work:**
- Backend: `OutboxEvent` table and migration, `IDomainEventRecorder`, `IEventBus` with `InProcessEventBus`, `OutboxDispatcherJob`, `TimeEventEmitterJob`, `OutboxCleanupJob`, `RoutineEventHandler`, routine entity and evaluator, execution log, library pre-built routines; record the events in the domain services that raise them (booking, check-in/out, payment, service request, ID, Alloggiati receipt, guest message, review)
- Frontend: Routine creation UI, list + edit, test UI, execution log display, library UI
- Mobile: Notification routing for routine-triggered push types
- Tests (one small proof per rule): `Outbox_EventAndDomainChange_CommitTogether` (a rolled-back change leaves no event), `RoutineExecution_SameEvent_ExecutesOnce`, test mode simulation proof test (no bus call, no execution row)

**Operator decision (Q-A54):** event bus, confirmed 2026-10-25. Objections O-adr-lead-be-4 (P0) and O-adr-security-2 (P0) are resolved by design (transactional outbox, no direct publishing, idempotent handlers, in-process bus without new infrastructure); O-adr-qa-1 (P1) is an accepted cost: one more table, three jobs and a bus interface. The three seats did NOT re-review this amended design.

**Risks:**
- Dispatch latency of up to about one minute (the fast path R16 reduces it); time-based triggers up to `Routine:EvaluationIntervalMinutes`
- At-least-once delivery: handlers MUST stay idempotent (R7)
- Ordering is guaranteed by `OccurredAt` per dispatch run only: a retried event can arrive after a later one, handlers MUST NOT depend on arrival order
- Outbox growth: index on (DispatchedAt, OccurredAt) and the cleanup job (R15)
- The in-process bus shares the API process: a slow handler slows the dispatcher; actions with external side effects (messages, access codes) run as Hangfire jobs, not inline
- Dead-lettered events need an operator look: phase 1 logs them, a screen for them is out of phase 1

**Feature flag:** `routines` default OFF

**CI cost:** +15s for the three proof tests
