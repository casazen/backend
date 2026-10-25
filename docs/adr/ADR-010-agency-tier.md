# ADR-010: Agency Tier Representation

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-01, NF-03, NF-04, NF-16, NF-17, NF-18, NF-19, NF-20, NF-21, NF-22

## Context

CasaZen introduces a B2B tier ("Agency") for property management agencies that manage properties on behalf of owners. Agencies need additional features: property owner registry with encrypted financial data, owner portal access, monthly statements, withholding tax management, wire transfers, invoicing, internal staff activities, and trusted supplier management.

**Existing code** (from `kb/gap/gap-analysis.md`):
- `PlanTier` enum at `Casazen.Core/Entities/Enums/PlanTier.cs:7-12` has Starter/Pro/Scale
- Policy infrastructure at `CasazenPolicies.cs:24-100` shows context-based authorization
- `Org.PlanTier` field at `Org.cs:34` determines tier
- Entitlement pattern at `docs/TECHNICAL.md:177` shows `maxSeats`, `canInviteMember`

**Repo scope:** Backend (tier enum, policies, entitlements), Frontend (tier detection, B2B area visibility)

**Mobile:** NOT-APPLICABLE - tier-gated endpoints return 403, mobile shows generic feature-not-available

## Decision

### PlanTier Enum Extension

Per Q-A01 answer, add `Agency` value to the existing `PlanTier` enum:

```csharp
public enum PlanTier
{
    Starter = 0,
    Pro = 1,
    Scale = 2,
    Agency = 3
}
```

Agency is the highest tier with all Pro/Scale features plus B2B capabilities.

### Policy Infrastructure

Create `agency.*` policy prefix for B2B endpoints:

- `agency.owner.manage` - invite/remove property owners
- `agency.owner.view` - view owner list and details
- `agency.owner.financial.read` - view full IBAN (requires audit log)
- `agency.statement.manage` - create/edit/publish statements
- `agency.statement.view` - view statements
- `agency.invoice.manage` - create/issue invoices
- `agency.withholding.manage` - manage withholding tax
- `agency.wire.manage` - create wire transfer batches
- `agency.activity.manage` - manage internal activities
- `agency.supplier.manage` - manage trusted suppliers

All `agency.*` policies require `Org.PlanTier == PlanTier.Agency`.

### Tier Assignment

Admin-only in phase 1 (no self-service). OrgActivityCatalog records `TierAssigned` event with admin user and timestamp.

### Tier Downgrade

If tier removed (Agency → Scale/Pro/Starter):
- B2B data retained (read-only mode)
- B2B mutations return 403 `tier_insufficient`
- Org flag `IsAgencyTierDowngraded` tracks state

### Frontend Tier Detection

GET /api/orgs/me response includes `planTier` field. B2B routes guarded by tier check.

### Rejected Alternatives

- **Org attribute IsAgency**: Dual dimension (Pro+Agency) overcomplicates; tier hierarchy is cleaner
- **Both enum + attribute**: Over-engineering for phase 1

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-010-R1 | P0 | PlanTier enum MUST add `Agency` value (additive migration, no data backfill required) |
| ADR-010-R2 | P0 | Policy `RequireAgencyTier` MUST check `Org.PlanTier == PlanTier.Agency`, return 403 `feature_requires_agency_tier` if not |
| ADR-010-R3 | P0 | Every B2B endpoint (owner portal, statements, withholding, invoices, internal activities, trusted suppliers) MUST enforce `agency.*` policy from CasazenPolicies, never role check in service code |
| ADR-010-R4 | P0 | GET /api/orgs/me/entitlement MUST include `isAgency: boolean` in response DTO |
| ADR-010-R5 | P0 | Tier downgrade (Agency → lower) MUST set read-only mode; B2B mutations return 403 `tier_insufficient`; data retained |
| ADR-010-R6 | P0 | Frontend B2B routes (owner portal, statements, invoices, wire transfers, withholding) MUST be guarded with tier check `planTier === 'Agency'` |
| ADR-010-R7 | P1 | OrgActivityLog MUST record `TierAssigned` event with admin user and timestamp |
| ADR-010-R8 | P1 | Frontend org settings page MUST display tier badge (read-only for non-admin users) |
| ADR-010-R9 | P1 | Frontend plan comparison table MUST include Agency tier with "Contact us" notice |

## Consequences

**Follow-on work:**
- Backend: PlanTier enum migration, policy infrastructure (`agency.*` prefix), entitlement service extension, tier assignment endpoint
- Frontend: OrgSummary DTO tier field, B2B area visibility toggles, admin tier assignment UI
- Tests: Permission proof test `AgencyTierPolicy_NonAgencyOrg_Returns403`

**Risks:**
- Existing plan limits tests may enumerate PlanTier values; update for new Agency value
- Policy naming: `agency.*` prefix prevents collision with existing `PropertyOwner` role

**Feature flag:** `agency_tier` default OFF until agency features ready

**CI cost:** +5s for permission proof test
