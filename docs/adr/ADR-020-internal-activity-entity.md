# ADR-020: Internal Activity Entity

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-22, NF-26

## Context

Agencies need to track internal staff activities (cleaning, maintenance, check-in) separate from supplier service requests. Staff activities can be recharged to property owners via monthly statements.

**Operator decision (Q-A46):** New InternalActivity entity chosen over ServiceRequest extension for clear domain separation.

**Existing code** (from `kb/gap/gap-analysis.md`):
- `ServiceRequest` entity has `SupplierOrgId` as non-null (supplier work)
- Collaborator role with "Solo alcuni" perimeter exists
- `PropertyMemberAccess` pattern at `docs/TECHNICAL.md:188-190`

**Repo scope:** Backend (InternalActivity entity, staff assignment), Frontend (activity CRUD, staff view), Mobile (out of phase 1)

## Decision

### InternalActivity Entity

Separate from ServiceRequest for clear domain separation:

```csharp
public class InternalActivity : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid? BookingId { get; set; }  // Optional link to booking
    public Guid AssignedStaffUserId { get; set; }  // FK to User (Collaborator role)
    public InternalActivityType Type { get; set; }
    public DateTime ScheduledAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public InternalActivityStatus Status { get; set; }
    public decimal? InternalRate { get; set; }
    public bool RechargeToOwner { get; set; }
    public Guid? StatementLineId { get; set; }  // When recharged
    public string Note { get; set; }
}

public enum InternalActivityType { Cleaning, Maintenance, CheckIn, Other }
public enum InternalActivityStatus { Scheduled, InProgress, Completed, Cancelled }
```

### Internal Rate Configuration

```csharp
public class InternalActivityRate : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public InternalRateType RateType { get; set; }  // PerActivity | PerPerson
    public InternalActivityType? ActivityType { get; set; }  // NULL = all types
    public Guid? StaffUserId { get; set; }  // NULL = org default
    public decimal RateAmount { get; set; }
}

public enum InternalRateType { PerActivity, PerPerson }
```

Per Q-A47 answer, default rate from staff profile, override per activity.

### Staff Assignment and Perimeter

Staff users have Collaborator role with HostScope perimeter:
- Staff sees only activities in their PropertyMemberAccess perimeter
- Follows existing `IHostScopeResolver` pattern

### Recharge to Owner Statement

When `RechargeToOwner = true`:
- Create OwnerStatementLine on statement generation
- Type = InternalActivity
- Amount = InternalRate
- Link via StatementLineId

### Routine Integration

Per ADR-022, routines can create internal activities:

```json
{
  "type": "CreateActivity",
  "activityType": "Cleaning",
  "assignedStaffUserId": "uuid",
  "rechargeToOwner": true
}
```

### Mobile Out of Phase 1

Per Q-A48 answer, staff mobile app is out of phase 1:
- Mobile requires role/context awareness
- Web console only for internal activities

### Staff Compensation Outside CasaZen

Per Q-A49 answer, staff compensation tracking is future scope:
- Internal rate is for billing only
- No payroll features

### Rejected Alternatives

- **ServiceRequest extension:** Overloads ServiceRequest with internal logic, nullable SupplierOrgId is implicit
- **Shared workflow base class:** Premature abstraction, can add later if needed

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-020-R1 | P0 | InternalActivity table (OrgId, PropertyId, AssignedStaffUserId FK User, Type enum, ScheduledAt, CompletedAt, Status enum, InternalRate decimal nullable, RechargeToOwner bool, StatementLineId nullable, Note, ITenantOwned) |
| ADR-020-R2 | P0 | InternalActivityRate table (OrgId, RateType enum PerActivity|PerPerson, ActivityType nullable, StaffUserId nullable, RateAmount decimal, ITenantOwned) |
| ADR-020-R3 | P0 | Staff sees only activities in their PropertyMemberAccess perimeter (HostScope pattern) |
| ADR-020-R4 | P0 | POST /api/internal-activities MUST authorize with `RequireContext:short-rent:activity.create` (new permission, Collaborator + above) |
| ADR-020-R5 | P0 | Recharged activity (RechargeToOwner = true) MUST create OwnerStatementLine (Type = InternalActivity, amount = InternalRate) on statement generation |
| ADR-020-R6 | P0 | Mobile app out of phase 1 (no mobile API extensions, web-only endpoints) |
| ADR-020-R7 | P0 | Internal activity creation form MUST include: type, staff person dropdown (Collaborator role, HostScope perimeter), scheduled date/time, internal rate (default from staff profile), recharge to owner checkbox |
| ADR-020-R8 | P0 | Staff activity list page MUST show only activities assigned to logged-in staff user AND within their HostScope perimeter |
| ADR-020-R9 | P0 | Staff activity detail MUST allow status update (Scheduled -> InProgress -> Completed) and completion timestamp entry |
| ADR-020-R10 | P1 | Routine integration: RoutineAction payload `{type: "CreateActivity", activityType, assignedStaffUserId, rechargeToOwner}` triggers InternalActivity creation |
| ADR-020-R11 | P1 | Internal rate default: if staff person has default rate in profile, pre-fill form; allow override per activity |

## Consequences

**Follow-on work:**
- Backend: InternalActivity entity, internal rate config, staff assignment logic + perimeter, owner statement integration, routine integration
- Frontend: Internal activity creation UI, staff activity list, staff update UI, internal rate input, owner statement integration display
- Tests: Permission proof test `InternalActivity_StaffSees_OnlyTheirPerimeter`, money proof test `InternalActivity_Recharged_EntersOwnerStatement`

**Future work:**
- Mobile staff app (role/context awareness)
- Staff compensation tracking

**Risks:**
- ServiceRequest vs InternalActivity: separate entities, no overloading
- Routine integration via payload (no circular dependency)

**CI cost:** +5s for perimeter and recharge proof tests
