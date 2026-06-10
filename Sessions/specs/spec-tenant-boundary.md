# Spec — Tenant Boundary (`Org` + `OrgId` + Plan Entitlement) (US-004)

## Overview

CasaZen has context-scoped RBAC (`AppContext`, `UserContextMembership`, `Role`, `RolePermission`,
policy convention `RequireContext:{context}:{permission}`) but **no tenant boundary**: data is scoped
per-`OwnerId` (Auth0 `sub`) + context, not isolated into an organization/tenant. Selling to PM teams
and agencies requires an **`Org`** tenant key and an **`OrgId`** foreign key on the core tenant-scoped
tables, plus **plan entitlement** (Starter/Pro/Scale).

This spec introduces the `Org` entity, adds `OrgId` to `Property`, `Booking`, `LeaseContract`, and
`Payment`, and a plan-entitlement service. It establishes a cross-cutting invariant and a strict
migration sequence so existing data is preserved and the parallel Phase 1.5 work cannot ship un-scoped.

> **Invariant (RF1)** — **every new tenant-scoped table MUST carry `OrgId` and honor plan entitlement.**
> No tenant-scoped table (including the Phase 1.5 recurring-rent ledger) may ship without an `OrgId` FK.

Phase: **1 (MVP Sellable — tenant boundary)** · User story: **US-004**
Stage of entry: **Stage 01 Planning** (new macro-spec)

---

## User Story

As a property-management company, I want all my properties, bookings, leases, and payments to belong
to **my organization** so that my data is isolated from other CasaZen customers and my subscription
plan limits apply to my org as a whole.

As the platform, I want a single tenant key (`OrgId`) enforced on every tenant-scoped table and a
plan-entitlement check, so that data isolation and tier limits are structural, not ad-hoc.

---

## Acceptance Criteria

### Backend

- **AC1**: New `Org` entity `{ Id (Guid), Name, Slug (unique), PlanTier (enum: Starter|Pro|Scale), DisplayName, LogoUrl?, ThemeColor?, ContactEmail, StripeCustomerId?, StripeConnectedAccountId?, IsActive, CreatedAt, UpdatedAt }`, registered as a `DbSet<Org>` with a unique index on `Slug`.

- **AC2**: `OrgId` (`Guid`) foreign key added to `Property`, `Booking`, `LeaseContract`, and `Payment`, each with an index on `OrgId` and `OnDelete(DeleteBehavior.Restrict)` (deleting an `Org` must not cascade-wipe operational/financial history).

- **AC3 (RF3 — migration sequencing, step 1)**: Migration `AddOrgIdNullable` adds `Org` table + **nullable** `OrgId` columns on the four tables. No data change yet. Applies cleanly to an existing populated DB.

- **AC4 (RF3 — step 2)**: Migration `BackfillDefaultOrgs` creates **one default `Org` per distinct existing `Property.OwnerId`** (slug derived from owner; `PlanTier = Starter`), sets `User.OrgId` for that owner, and backfills `OrgId` on `Property`/`Booking`/`LeaseContract`/`Payment` by walking existing relationships (booking→property, payment→booking, lease→property). Idempotent and re-runnable; verified row-counts logged.

- **AC5 (RF3 — step 3)**: Migration `MakeOrgIdRequired` sets the four `OrgId` columns **NOT NULL** and adds the FK constraints, only after AC4 backfill leaves zero NULLs. The three migrations land **in order**, **before** any Phase 1.5 migration.

- **AC6 (RF3 — snapshot discipline)**: After these migrations, Phase 1.5 (and all later) migrations **rebase onto the regenerated `AppDbContextModelSnapshot.cs`** — the snapshot is **never hand-merged**. New Phase 1.5 tables carry `OrgId` from creation (enforcing RF1).

- **AC7**: A tenant resolution mechanism: `ITenantContext` resolves the caller's `OrgId` from the authenticated `User` (`User.OrgId`), and an EF **global query filter** (or repository-level filter) scopes `Property`/`Booking`/`LeaseContract`/`Payment` reads to the caller's `OrgId`. Cross-org access returns empty/`404`, not another org's rows. (Anonymous public endpoints from `spec-public-booking-readmodel`/`spec-branded-booking-site` filter by explicit `orgId`, not the caller filter.)

- **AC8**: Plan entitlement — `IEntitlementService.CanAddProperty(orgId)` (and a generic `GetEntitlement(orgId)`) enforces per-tier limits (e.g. Starter unit cap) sourced from a tier→limits map; `Property` creation returns `403`/`409` with a clear "plan limit reached" error when the org is over its tier limit.

- **AC9**: `User` gains `OrgId` (FK to `Org`); membership of a user in an org is established at onboarding/backfill. `GET /api/users/me` returns the caller's `{ orgId, org: { id, name, slug, planTier } }` so the FE can surface the current org.

- **AC10 (Regression)**: Existing authenticated endpoints keep working post-migration with backfilled data (an existing owner sees exactly their pre-migration properties/bookings/leases/payments, now under their default `Org`); no orphaned rows; `dotnet test` migration/integration suites pass against `casazen_test`.

- **AC10b (Migration safety / rollback — DA amendment)**: each of the three migrations has a **tested down-migration** (`MakeOrgIdRequired` → revert to nullable; `BackfillDefaultOrgs` → documented reversible/no-op; `AddOrgIdNullable` → drop `OrgId` + `Org`). Before the `MakeOrgIdRequired` NOT-NULL flip, a **pre-flight check fails loudly if any tenant-scoped row still has a NULL `OrgId`** (e.g. direct-booking guests/bookings created without an owner): such rows are assigned to a dedicated fallback `Org` or quarantined per an explicit rule — **never silently NOT-NULL-flipped**. The three steps run as **separate deploys** (nullable → backfill → NOT NULL) so writes are never blocked mid-migration (online/zero-downtime).

### Frontend

- **AC11**: `src/types/user.types.ts` / `org.types.ts` add `Org` + `planTier` to the current-user model; `src/queries/use-users.ts` `useCurrentUser` surfaces `org`. The owner console header shows the current org name + plan badge with link to plan settings.

- **AC11b (MVP plan management — pre-Stripe)**: Onboarding wizard step 2 lets the operator pick **Starter/Pro/Scale** (`POST /api/users/onboarding` with `planTier`). `GET /api/orgs/plans` returns the catalogue. `PUT /api/orgs/me/plan` lets the org owner change tier. Admin uses `PATCH /api/admin/orgs/{orgId}/plan`. UI: `plan-settings-page.tsx`, admin **Piano** action on users table. **Paid checkout** remains in `spec-saas-billing`.

- **AC12**: When property creation is blocked by entitlement (`403`/`409` "plan limit reached"), the UI shows an Italian message (e.g. "Hai raggiunto il limite del tuo piano") with a link toward `/app/short-rent/settings/plan`.

---

## Technical Notes

### Backend

| File | Action |
|---|---|
| `Casazen.Core/Entities/Org.cs` | Create — tenant entity (AC1) |
| `Casazen.Core/Entities/Enums/PlanTier.cs` | Create — `Starter|Pro|Scale` |
| `Casazen.Core/Entities/Property.cs` | Modify — add `Guid OrgId` + nav |
| `Casazen.Core/Entities/Booking.cs` | Modify — add `Guid OrgId` + nav |
| `Casazen.Core/Entities/LeaseContract.cs` | Modify — add `Guid OrgId` + nav |
| `Casazen.Core/Entities/Payment.cs` | Modify — add `Guid OrgId` + nav |
| `Casazen.Core/Entities/User.cs` | Modify — add `Guid? OrgId` + nav (AC9) |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — `DbSet<Org>`, relationships, indexes, `Slug` unique, global query filter (AC7) |
| `Casazen.Infrastructure/Migrations/<ts>_AddOrgIdNullable.cs` | Create — step 1 (AC3) |
| `Casazen.Infrastructure/Migrations/<ts>_BackfillDefaultOrgs.cs` | Create — step 2 data migration (AC4) |
| `Casazen.Infrastructure/Migrations/<ts>_MakeOrgIdRequired.cs` | Create — step 3 NOT NULL + FK (AC5) |
| `Casazen.Infrastructure/Migrations/AppDbContextModelSnapshot.cs` | Modify — **regenerated** by EF, never hand-merged (AC6) |
| `Casazen.Core/Services/IOrgService.cs` | Create — org CRUD + `GetPublicBySlugAsync` |
| `Casazen.Infrastructure/Services/OrgService.cs` | Create — implementation |
| `Casazen.Core/Services/IEntitlementService.cs` | Create — plan-limit checks (AC8) |
| `Casazen.Infrastructure/Services/EntitlementService.cs` | Create — tier→limits map |
| `Casazen.Web/Infrastructure/ITenantContext.cs` + `TenantContext.cs` | Create — resolve caller `OrgId` (AC7) |
| `Casazen.Web/Controllers/PropertiesController.cs` | Modify — entitlement check on `Create` (AC8); set `OrgId` from tenant context |
| `Casazen.Web/Controllers/MeController.cs` | Modify — return `org` in `/me` (AC9) |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — register `IOrgService`, `IEntitlementService`, `ITenantContext` |

### Frontend

| File | Action |
|---|---|
| `src/types/org.types.ts` | Create — `Org`, `PlanTier` |
| `src/types/user.types.ts` | Modify — add `org` to current user |
| `src/queries/use-users.ts` | Modify — surface `org` from `/me` |
| `src/components/layout/header.tsx` | Modify — show org name + plan badge |
| `src/features/properties/property-create-page.tsx` | Modify — handle entitlement `403`/`409` (AC12) |

---

## Compliance

- **Tenant data isolation**: `OrgId` FK on every tenant-scoped table + tenant query filter ensure cross-customer isolation; **RF1 invariant** binds all future tenant-scoped tables (incl. the Phase 1.5 rent ledger) to carry `OrgId`.
- **GDPR controller/processor delineation**: each `Org` is the **data controller** for its guests'/tenants' personal data; **CasaZen is the data processor**. This boundary underpins the DPA in `spec-onboarding-plg` and per-org erasure/retention scoping.
- **Data-integrity safety**: `OnDelete(Restrict)` on org FKs prevents accidental cascade-deletion of bookings/payments/leases; the 3-step migration preserves all existing rows (AC10).
- **Migration safety (RF3)**: nullable → backfill → NOT NULL ordering plus snapshot-rebase discipline removes the Phase 1 ↔ 1.5 merge-order ambiguity (resolves draft-v3 §D Q1).

---

## Dependencies

- **Requires**: context-RBAC primitives (`AppContext`/`UserContextMembership`/`Role`/`RolePermission`) and EF Core migrations baseline; an applied, green migration history on `casazen_test`.
- **Blocks**: `spec-direct-checkout` (needs `Org.StripeConnectedAccountId`), `spec-branded-booking-site` (needs `Org` slug + branding), `spec-saas-billing` (needs `Org` + `PlanTier` + `StripeCustomerId`), `spec-onboarding-plg` (provisions an `Org`), and the Phase 1.5 `spec-ltr-recurring-rent` ledger (must inherit `OrgId` via RF1).
- **Related**: `spec-org-seats-collaboration` (Phase 2) extends `UserContextMembership`/`RequireContext` with seat RBAC on top of this `Org` boundary.
- **Does not touch**: OTA adapters, pricing adapter, Alloggiati/tax services (their tables gain `OrgId` only transitively via `Property`/`Booking`).
