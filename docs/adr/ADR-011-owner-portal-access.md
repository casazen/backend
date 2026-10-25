# ADR-011: Owner Portal Access Model

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-04, NF-16

## Context

Property owners need access to a dedicated portal to view their properties' booking calendar, revenue summaries, monthly statements, and invoices. The access model must support owners with properties at multiple agencies while maintaining strict tenant isolation.

**Operator decision (Q-A05):** Auth0 login context chosen over signed link for persistent sessions, password reset, and multi-device access.

**Existing code** (from `kb/gap/gap-analysis.md`):
- `GuestSession` policy at `CasazenPolicies.cs:46-51` shows signed-link pattern for guest check-in
- No owner context or OwnerUser entity exists
- Auth0 integration exists for host console users

**Repo scope:** Backend (OwnerUser entity, Auth0 integration, owner portal endpoints), Frontend (owner portal area, login flow)

**Mobile:** NOT-APPLICABLE - owner portal is web-only in phase 1

## Decision

### OwnerUser Entity (Not ITenantOwned)

Create `OwnerUser` entity for owner identity across agencies:

```csharp
public class OwnerUser
{
    public Guid Id { get; set; }
    public string Auth0Sub { get; set; }  // Auth0 user ID
    public string Email { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
```

OwnerUser is NOT ITenantOwned - one owner can have properties at multiple agencies.

### PropertyOwnership Link (ITenantOwned)

```csharp
public class PropertyOwnership : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }  // Agency org
    public Guid PropertyId { get; set; }
    public Guid OwnerUserId { get; set; }  // FK to OwnerUser
    public Guid PropertyOwnerId { get; set; }  // FK to PropertyOwner (registry entry)
    public DateTime OwnershipStart { get; set; }
    public DateTime? OwnershipEnd { get; set; }  // NULL = active
}
```

### Auth0 Multi-Org Pattern (Option A)

Per O-adr-lead-fe-1 resolution, one Auth0 user per owner email with custom claims:

JWT claims structure:
```json
{
  "sub": "auth0|abc123",
  "context": "owner_portal",
  "owner_id": "uuid",
  "agency_ids": ["uuid1", "uuid2"],
  "owned_property_ids": ["prop1", "prop2", "prop3"]
}
```

If owner has properties at multiple agencies, frontend shows workspace-like switcher.

### Owner Portal Policy

New policy `owner_portal` in CasazenPolicies (pattern like `GuestSession`):

- Checks `context=owner_portal` custom claim
- Resolves `OwnerUserId` from JWT
- MUST NOT include host roles (PropertyOwner, Staff, Manager)

### Endpoint Isolation

Every owner portal endpoint MUST:
1. Enforce `owner_portal` policy (never `[AllowAnonymous]`)
2. Filter properties by `PropertyOwnership.OwnerUserId = owner AND OwnershipStart <= now AND (OwnershipEnd == null OR OwnershipEnd >= now)`
3. Filter bookings/statements/invoices by visible properties
4. Apply guest data masking (dates/channel/amounts only, no names/contacts/documents)
5. Return 404 for property of another owner (never 403 with detail)

### Invitation Flow

1. Agency admin calls `POST /api/owners/{propertyOwnerId}/invite`
2. Backend creates OwnerInvitation, sends email with invite link
3. Owner clicks link → Auth0 signup/login
4. Backend links Auth0 sub to OwnerUser (create if not exists)
5. PropertyOwnership created linking OwnerUser to PropertyOwner

Duplicate invitation click is idempotent (does not create duplicate Auth0 user).

### Access Revocation

- Mandate end sets `PropertyOwnership.OwnershipEnd = now`
- Next owner portal request for that property returns 404
- If no properties visible, redirect to login with "Access revoked" message
- Rate limit: 100 invitations/day/org

### Rejected Alternatives

- **Signed link (recommended default)**: Simpler but lacks password reset, persistent sessions, multi-device. Operator chose Auth0.
- **Option B (one Auth0 user per agency)**: Confusing UX for owner with multiple agencies; two accounts for same person.
- **Auth0 Organizations per agency**: Auth0 org proliferation; custom claims in one application is simpler.

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-011-R1 | P0 | OwnerUser entity MUST NOT implement ITenantOwned (owner is cross-org). PropertyOwnership MUST implement ITenantOwned with OrgId = agency org |
| ADR-011-R2 | P0 | Every GET /api/owner-portal/* endpoint MUST filter by `PropertyOwnership.OwnerUserId = User.GetOwnerUserId() AND period active`. A property of another owner MUST return 404, never 200 or 403 |
| ADR-011-R3 | P0 | Guest data in owner portal responses (bookings, revenue) MUST mask: names NULL, emails NULL, phones NULL, documents excluded, contacts excluded. Only dates, channel, amounts visible |
| ADR-011-R4 | P0 | Owner invitation endpoint POST /api/owners/{id}/invite MUST enforce `agency.owner.manage` policy AND rate limit 100/day/org |
| ADR-011-R5 | P0 | Owner JWT MUST include custom claim `context=owner_portal`, MUST NOT include host roles (PropertyOwner, Staff, Manager) |
| ADR-011-R6 | P0 | Owner portal login route `/portale-proprietari/accedi` MUST use Auth0 login flow |
| ADR-011-R7 | P0 | Owner JWT MUST include claims: `owner_id`, `agency_ids[]`, `owned_property_ids[]` |
| ADR-011-R8 | P0 | GET /api/owner-portal/properties MUST filter properties by JWT `owned_property_ids` and ownership period (backend enforces, frontend never sees other owners' data) |
| ADR-011-R9 | P0 | Mandate end or agency revoke MUST invalidate owner access immediately (next API call returns 401 for that property, frontend redirects to login with "Access revoked" message) |
| ADR-011-R10 | P1 | Owner invite acceptance flow MUST create Auth0 user idempotently (duplicate click does not fail) |
| ADR-011-R11 | P1 | OrgActivityLog MUST record owner invitation sent, accepted, revoked with admin user |
| ADR-011-R12 | P1 | Owner portal shell MUST NOT show workspace switcher if owner has properties at only one agency; MUST show switcher if multi-agency |

## Consequences

**Follow-on work:**
- Backend: OwnerUser entity + Auth0 integration, OwnerInvitation flow, owner portal endpoints with guest masking
- Frontend: Owner portal area + login flow, property/content displays, invite UI in agency console
- Tests: Tenant isolation proof test `OwnerPortal_OwnerA_CannotSeeOwnerBData`, guest masking proof test

**Risks:**
- Auth0 multi-org mapping complexity (one owner, multiple agencies)
- Cross-tenant leak if property scoping or guest masking missed
- Invitation email enumeration (rate limit mitigates)

**Dissent (adr-lead-be):** Backend lead recommended signed-link pattern for simplicity. Chair rejected per operator choice Q-A05; Auth0 benefits (multi-device, password reset, persistent sessions) justify complexity.

**Feature flag:** `owner_portal` default OFF

**Pre-launch gate:** Auth0 multi-org pattern verified on staging
