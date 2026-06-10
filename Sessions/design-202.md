# Design — Issue #202 Multi-Tenant `Org` Boundary (`Org` + `OrgId` FK + Plan Entitlement)

> **Stage 02 — Design** · Spec: `Sessions/specs/spec-tenant-boundary.md` (US-004) · Phase 1 (foundational, migration lands first)
> **Architecture**: AD-4 (tenant boundary before seats), AD-6 (migration sequencing), RF1 (every tenant-scoped table carries `OrgId` + entitlement)
> **Stack**: .NET 10 · EF Core · PostgreSQL (Supabase) · layered `Casazen.Core` / `Casazen.Infrastructure` / `Casazen.Web` · React 19 SPA (`casazen/frontend`)
> **Specialist synthesis**: `api-designer` (API Contract + Migration Plan) · `frontend-designer` (Frontend Flow + ProtectedRoute) · `security-by-design` (Security Notes + GDPR Scope).

This spec introduces `Org` as the tenant key and adds `OrgId` to `Property`, `Booking`, `LeaseContract`, `Payment` (and nullable `User.OrgId`), an EF global query filter that scopes all tenant reads to the caller's `OrgId`, and an `IEntitlementService` enforcing per-tier limits. Data is currently scoped per-`OwnerId` (Auth0 `sub`); after this change isolation becomes structural. No tenant-scoped row may be read across orgs; `Property` creation is gated by plan entitlement.

**Grounding note (verified against source):** `Property.OwnerId` is the Auth0 `sub` **string** (not a FK); `User.Id == sub`. `GET /api/users/me` already exists in `UsersController` (returns `UserDetailDto`); `GET /api/me/contexts` exists in `MeController`. There are **no** EF global query filters today. Migrations are timestamp-prefixed with `Up`/`Down`; data migrations use `migrationBuilder.Sql(...)`. Secrets (`Stripe:*`, `OTA:*:ApiKey`) live in config/env, never the DB. `Guest` holds PII but is **not** one of the four `OrgId` tables — its PII is scoped transitively via `Booking → Property`.

---

## API Contract

**Conventions** — All authenticated endpoints require a valid Auth0 JWT (Bearer). `[Authorize]` = `RequireAuthenticatedUser`. Context policies are `RequireContext:{context}:{permission}` (handled by `ContextAuthorizationHandler`). `OrgId` is **never** accepted from the request body or query for caller-scoped endpoints — it is resolved server-side from the authenticated principal via `ITenantContext` (see Security Notes). Cross-org reads return `404`/empty, never another org's rows.

### A. New / changed endpoints (full detail)

| # | Method | Path | Request schema | Response schema | Auth requirement (decision) |
|---|---|---|---|---|---|
| 1 | `GET` | `/api/users/me` | _none_ | `200 UserDetailDto` (extended): `{ id, email, firstName, lastName, role, rentalType?, isActive, phoneNumber, createdAt, updatedAt, orgId: Guid?, org: { id: Guid, name: string, slug: string, planTier: "Starter"\|"Pro"\|"Scale" } \| null }` | **`[Authorize]`** (authenticated; self-scope by `sub`). AC9. Returns caller's own org only. |
| 2 | `GET` | `/api/orgs/me/entitlement` | _none_ | `200 EntitlementDto`: `{ orgId: Guid, planTier: string, limits: { maxProperties: int }, usage: { properties: int }, canAddProperty: bool }` | **`[Authorize]` + `RequireContext:short-rent:property.read`**. New. Backs FE plan badge + create-button gating (AC8/AC11). `orgId` from `ITenantContext`, not client. |
| 3 | `POST` | `/api/properties` | `201`/`CreatePropertyRequest` (unchanged body; `OwnerId` ignored, **`OrgId` ignored** — both server-set) | `201 Property` (now includes `orgId`); **`403`** `{ error, code: "plan_limit_reached", planTier, limit }` when over tier limit; `409` alternative if a concurrent create races the limit | **`[Authorize(Policy="PropertyOwner")]` + `RequireContext:short-rent:property.write`**. AC8: entitlement check before insert; sets `OrgId` from `ITenantContext`. |
| 4 | `GET` | `/api/properties` | _none_ | `200 Property[]` — **scoped to caller `OrgId`** (replaces `OwnerId`-only scoping) | **`[Authorize(Policy="PropertyOwner")]` + `RequireContext:short-rent:property.read`** (unchanged). Behavior change: tenant filter applied. |
| 5 | `GET` | `/api/properties/{id}` · `/{id}/detail` · `/{id}/documents` · `/{id}/images` | path `id: Guid` | `200 Property` / `PropertyDetailResponse` / `PropertyDocumentDto[]` / `string[]`; **`404`** if `id` not in caller's org | **`[Authorize(Policy="PropertyOwner")]`** (+ `RequireContext:short-rent:property.write` on the write image/doc verbs, unchanged). Cross-org `id` → `404` via filter. |
| 6 | `GET` | `/api/bookings` · `/api/bookings/{id}` · `/api/bookings/calendar` | query `propertyId?`, `startDate`,`endDate`,`timezone?` as today | `200 Booking[]` / `Booking` / `CalendarResponseDto` — **scoped to caller `OrgId`**; cross-org `{id}`/`propertyId` → `404`/empty | **`[Authorize(Policy="PropertyOwner")]` + `RequireContext:short-rent:booking.read`** (unchanged). Tenant filter applied. |
| 7 | `POST`/`PUT`/`DELETE` | `/api/bookings` · `/api/bookings/{id}` · `/{id}/check-in` · `/{id}/check-out` | `Booking` body / path `id` | `201`/`204`/`200 Booking`; **`OrgId` server-derived** from the booking's `Property.OrgId` (validated == caller org) | **`[Authorize(Policy="PropertyOwner")]` + `RequireContext:short-rent:booking.write`** (unchanged). Write rejected `404` if target property not in caller org. |
| 8 | `GET` | `/api/payments` · `/api/payments/{id}` (and booking-scoped reads) | as today | `200 Payment[]`/`Payment` — **scoped to caller `OrgId`** | **`[Authorize(Policy="PropertyOwner")]` + `RequireContext:short-rent:payment.read`** (unchanged). Tenant filter applied. |
| 9 | `GET`/`POST`/...| `/api/leases` · `/api/leases/{id}` (LeasesController) | as today | `200 LeaseContract`(s) — **scoped to caller `OrgId`**; cross-org `{id}` → `404` | **`[Authorize] + RequireContext:long-rent:lease.read`** (write verbs: `lease.create`/`lease.sign`/`lease.register`, unchanged). Tenant filter applied. |
| 10 | `GET` | `/api/properties/search` | query `city?`,`bedrooms?`,`maxPrice?` | `200 Property[]` (public catalog) | **`[AllowAnonymous]` — explicit public justification:** anonymous discovery surface (AD-3). **Does NOT use the caller tenant filter** (AC7); it is not org-scoped to a principal. Future public surfaces must filter by **explicit `orgId`** query param, never the caller filter. ⚠️ Pre-existing `OwnerId` leak is **out of scope** here and tracked by `spec-public-booking-readmodel` (AD-3 / counsel item #4). |
| 11 | `GET` | `/api/properties/health` · `/api/health` | _none_ | `200 { status }` | **`[AllowAnonymous]` — public justification:** liveness probe, no tenant data returned. |

### B. Endpoints unchanged in contract but now tenant-filtered (auth decisions restated)

All of the following keep their **existing** `[Authorize]` + `RequireContext:*` decorators (no auth change) and gain the server-side `OrgId` filter; cross-org access yields `404`/empty:

| Area | Endpoints | Auth requirement (decision) |
|---|---|---|
| OTA integrations | `/api/otaintegrations/*`, `/api/ota/*` | `[Authorize(Policy="PropertyOwner")]` + `RequireContext:short-rent:ota.read`/`ota.write` (filtered transitively via `Property.OrgId`) |
| Tourist-tax / pricing | `/api/touristtaxrates/*`, `/api/pricingadapter/*` | `[Authorize]` + relevant `RequireContext` (config/read), filtered transitively via property |
| GDPR | `/api/gdpr/guests/{id}/*` | `[Authorize]` — see GDPR Scope; access additionally constrained to guests reachable from the caller's org bookings |
| Admin | `/api/admin/*`, `/api/users` (list) | `[Authorize(Policy="AdminOnly")]` — **cross-org by design**, uses `IgnoreQueryFilters()` with audit (see Security Notes) |
| Webhooks | `/api/webhooks/*` (Stripe) | `[AllowAnonymous]` — **public justification:** signature-verified by `StripeWebhookHandler` (HMAC), no caller principal; org resolved from event payload, not a JWT |

### C. Explicitly out of scope (deferred, with auth pre-decision)

- **`GET /api/orgs/{slug}` (public Org-by-slug for branded sites)** — `IOrgService.GetPublicBySlugAsync` is referenced by later specs. **Not exposed in US-004.** When added by `spec-branded-booking-site` it will be `[AllowAnonymous]` returning a **whitelisted public projection** (name, slug, displayName, logoUrl, themeColor) — never `Stripe*Id`/`contactEmail`. Pre-decided here to keep the boundary tight.
- **Org management / plan switching endpoints** — owned by `spec-saas-billing` (Phase 1). US-004 is read-only for org.

---

## Frontend Flow

Repo `casazen/frontend` (React 19, feature-slice, TanStack Query, Auth0, `<ProtectedRoute>`). US-004 is **read-only** for org context: it surfaces the current org name + plan badge and handles the entitlement error on property create. **A user maps to exactly one `Org` in Phase 1** (`User.OrgId` is a single FK), so there is **no org switcher** yet — multi-org membership/switching is deferred to Phase 2 `spec-org-seats-collaboration`; plan management to `spec-saas-billing`.

### Route changes & guard status

US-004 introduces **no new top-level authenticated route**. The org indicator mounts inside the existing app shell, which is already wrapped by `<ProtectedRoute>`. All authenticated surfaces below are (and remain) under `<ProtectedRoute>`:

| Route | Status in US-004 | Guard |
|---|---|---|
| `/app/*` (owner console shell — header hosts the new `OrgBadge`) | Modified (header only) | **`<ProtectedRoute>`** (existing) |
| `/app/short-rent/properties/new` (property create page — entitlement handling) | Modified | **`<ProtectedRoute>`** (existing) |
| `/app/billing/upgrade` (entitlement CTA target) | **Referenced only** — route is **owned by `spec-saas-billing`**; the upgrade link points here | **`<ProtectedRoute>`** (must be marked when that spec creates it) |

> Gate G5: every authenticated route touched or referenced by this spec is marked `<ProtectedRoute>`. No new unguarded authenticated route is introduced.

### Component breakdown

| Component / file | Type | Responsibility |
|---|---|---|
| `src/types/org.types.ts` | new | `Org` interface `{ id, name, slug, planTier }`; `PlanTier = 'Starter' \| 'Pro' \| 'Scale'`; `Entitlement { planTier, limits, usage, canAddProperty }`. |
| `src/types/user.types.ts` | modify | Add `orgId?: string` and `org?: Org` to the current-user model (AC11). |
| `src/queries/use-users.ts` | modify | `useCurrentUser` reads `org` from `GET /api/users/me`; expose `org`, `planTier`. New `useEntitlement()` → `GET /api/orgs/me/entitlement` (TanStack Query, cached, invalidated on property create). |
| `src/components/layout/header.tsx` | modify | Render `<OrgBadge />` (org name + `<PlanBadge planTier=… />`), read-only. Hidden while `useCurrentUser` loading; graceful fallback if `org == null` (pre-backfill safety). |
| `src/components/org/OrgBadge.tsx` | new | Presentational: org name + plan badge. No mutation. |
| `src/components/org/PlanBadge.tsx` | new | Colored pill per tier (Starter/Pro/Scale). |
| `src/features/properties/property-create-page.tsx` | modify | On submit, if API returns `403`/`409` `code=plan_limit_reached`, show Italian message **"Hai raggiunto il limite del tuo piano"** + link/CTA toward `/app/billing/upgrade` (AC12). Optionally pre-disable the **"Nuova proprietà"** button when `useEntitlement().canAddProperty === false` with the same message as tooltip. |

### Data flow

`Auth0 session → <ProtectedRoute> → AppShell → useCurrentUser (GET /api/users/me) → header OrgBadge`. Property create page additionally consumes `useEntitlement()` for proactive gating; the server remains the source of truth (403/409 on submit) so a stale client cannot bypass the limit.

---

## Security Notes

### Cross-org IDOR prevention (server-enforced, never client-trusted)
- **Tenant filter is server-side and mandatory.** An EF Core **global query filter** is applied to `Property`, `Booking`, `LeaseContract`, `Payment`: `HasQueryFilter(e => e.OrgId == _tenantContext.OrgId)`. All reads through `AppDbContext` are scoped to the caller's org automatically; a cross-org `{id}` returns `404`, a cross-org list returns empty. The client **cannot** supply, widen, or override `OrgId` — there is no request field or query param for it on caller-scoped endpoints.
- **Writes set `OrgId` from the principal**, not the body: `PropertiesController.Create` sets `property.OrgId = _tenantContext.OrgId`; booking/payment writes validate the target `Property.OrgId == caller org` (else `404`) and inherit it. Any `OrgId`/`OwnerId` in a request body is ignored (consistent with the existing `OwnerId` hardening).
- **Filter-bypass is explicit and audited.** Only three call sites may bypass the filter via `IgnoreQueryFilters()`: (a) **migrations/backfill**, (b) **Hangfire background jobs** (Alloggiati, GDPR retention — cross-org by design, resolve `OrgId` from the entity, never a caller), (c) **Admin** endpoints (`AdminOnly`), which log privileged cross-org access through the existing `IAdminAccessAuditService` pattern.

### How `OrgId` is resolved from the authenticated principal
- `ITenantContext` / `TenantContext` (in `Casazen.Web/Infrastructure`, registered **Scoped**) resolves the caller `sub` from `IHttpContextAccessor` (`sub` → `NameIdentifier` fallbacks, identical to `ContextAuthorizationHandler`), loads `User.OrgId`, and caches it per request. `AppDbContext` depends on `ITenantContext` and reads `_tenantContext.OrgId` inside the query filter (re-evaluated per query via EF parameterization).
- If the principal has **no org** (`User.OrgId == null`, e.g. a brand-new user pre-backfill) the tenant filter matches nothing (`OrgId == null` ⇒ empty result set) — fail-closed, never fail-open. Anonymous/system contexts have no `ITenantContext.OrgId` and must use the explicit-`orgId` pattern or `IgnoreQueryFilters()` deliberately.

### Secrets / keys hygiene
- **OTA keys remain in configuration** (`OTA:{platform}:ApiKey`) and **Stripe keys in config/env** (`Stripe:SecretKey`, `Stripe:WebhookSecret`); none move into the DB. `Org.StripeCustomerId` / `Org.StripeConnectedAccountId` are **non-secret identifiers** (account references), not credentials. No new secret is introduced by this spec. Stripe webhooks stay HMAC-signature-verified (`StripeWebhookHandler`), not JWT-bound.

### PII data-flow summary
- PII (`Guest` name/email/phone/document, `Party` tenant identity, lease docs) is **partitioned by `OrgId`** through the tables that reference it: `Booking.OrgId` and `LeaseContract.OrgId`. `Guest` itself gains **no** `OrgId` in this spec (matches AC2's four-table list); guest PII becomes reachable only via a `Booking` in the caller's org, so the booking filter transitively isolates guest reads. Cross-org guest reads (incl. GDPR export) are constrained to guests linked to the caller's org bookings. No new PII field, no new external PII egress.

### Threat summary
| Threat (STRIDE) | Vector | Mitigation in this design |
|---|---|---|
| **Information disclosure / IDOR** | Caller requests another org's property/booking/payment/lease by `id` | Global query filter ⇒ `404`/empty; server-set `OrgId`; no client-supplied tenant key |
| **Tampering / privilege** | Client sends `OrgId`/`OwnerId` in body to reassign tenant | Body tenant fields ignored; `OrgId` derived from principal only |
| **Elevation (limit bypass)** | Client ignores disabled button to exceed plan limit | Server-side `IEntitlementService.CanAddProperty` enforced on `Create` (403/409) — client gating is advisory |
| **Repudiation** | Admin reads across orgs | `AdminOnly` + `IgnoreQueryFilters()` only, logged via `IAdminAccessAuditService` |
| **Destruction of financial history** | Deleting an `Org` cascades into bookings/payments/leases | All four FKs use `OnDelete(DeleteBehavior.Restrict)` (AC2) |
| **Fail-open on missing org** | Null `OrgId` returns everything | Filter is `OrgId == _tenant.OrgId`; null caller org ⇒ empty (fail-closed) |
| **Migration data exposure** | Backfill mis-assigns rows across owners | Relationship-walk backfill (booking→property→owner) + pre-flight NULL check + `casazen_test` regression (AC10) |

---

## Migration Plan

EF Core, PostgreSQL (Supabase), `MigrationsAssembly("Casazen.Infrastructure")`, timestamp-prefixed names, **three separate deploys** for zero-downtime (AC10b / AD-6). The `AppDbContextModelSnapshot.cs` is **regenerated by EF** after each migration, never hand-merged (AC6); Phase 1.5 migrations rebase onto the regenerated snapshot and new tenant-scoped tables carry `OrgId` from creation (RF1).

### Schema additions (entities)
- New `Casazen.Core/Entities/Org.cs`: `{ Id (Guid, PK), Name, Slug (unique), PlanTier (enum), DisplayName, LogoUrl?, ThemeColor?, ContactEmail, StripeCustomerId?, StripeConnectedAccountId?, IsActive, CreatedAt, UpdatedAt }` → `DbSet<Org>` with **unique index on `Slug`** (AC1).
- New `Casazen.Core/Entities/Enums/PlanTier.cs`: `Starter | Pro | Scale`.
- Modify `Property`, `Booking`, `LeaseContract`, `Payment`: add `Guid OrgId` + `Org` nav, **index on `OrgId`**, FK `OnDelete(DeleteBehavior.Restrict)` (AC2). Modify `User`: add `Guid? OrgId` + nav (AC9).

### Step 1 — `<ts>_AddOrgIdNullable` (Deploy 1)
- **Up:** `CreateTable("Orgs")` + unique index on `Slug`; `AddColumn<Guid?>("OrgId", nullable: true)` on `Properties`, `Bookings`, `LeaseContracts`, `Payments`, `Users`; `CreateIndex` on each `OrgId`. **No data change.** Applies cleanly to the populated DB (AC3). App code (Deploy 1) is tolerant of nullable `OrgId` and **starts writing `OrgId` on new rows** so the backfill set stops growing.
- **Down (tested):** drop the `OrgId` indexes + columns from the five tables; `DropTable("Orgs")` (AC10b).

### Step 2 — `<ts>_BackfillDefaultOrgs` (Deploy 2, data migration via `migrationBuilder.Sql`)
- **Up (idempotent, re-runnable; row-counts logged):**
  1. Insert **one default `Org` per distinct `Property.OwnerId`** (slug derived from owner, e.g. `org-<sanitized-owner>` with dedupe suffix; `PlanTier = Starter`; `IsActive = true`) — `ON CONFLICT (Slug) DO NOTHING`.
  2. Set `Users.OrgId` for each owner whose `Id == OwnerId`.
  3. Backfill `OrgId` by **walking relationships**: `Property` via `OwnerId→Org`; `Booking` via `booking→property`; `Payment` via `payment→booking→property`; `LeaseContract` via `lease→property`. All `WHERE "OrgId" IS NULL` (idempotent).
  4. Create the dedicated **fallback Org** `casazen-unassigned` (`IsActive = false`) used by the quarantine rule below.
  5. `RAISE NOTICE` the affected row counts per table (verification log, AC4).
- **Down (documented reversible / logical no-op, AC10b):** data backfill is **not** auto-reverted; a documented reversal script nulls `OrgId` where it was set to a generated default Org and deletes the generated default + fallback Orgs. Marked no-op in the migration body with the script referenced in the runbook.

### Step 3 — `<ts>_MakeOrgIdRequired` (Deploy 3, only after zero-NULL verification)
- **Pre-flight NULL-`OrgId` check (fail loud, AC10b):** the migration first runs a guard that counts NULL `OrgId` across `Properties`/`Bookings`/`LeaseContracts`/`Payments`; if **any** remain it `RAISE EXCEPTION` and aborts (the NOT-NULL flip never runs silently).
  ```sql
  DO $$
  DECLARE n bigint;
  BEGIN
    SELECT (SELECT count(*) FROM "Properties"     WHERE "OrgId" IS NULL)
         + (SELECT count(*) FROM "Bookings"       WHERE "OrgId" IS NULL)
         + (SELECT count(*) FROM "LeaseContracts" WHERE "OrgId" IS NULL)
         + (SELECT count(*) FROM "Payments"       WHERE "OrgId" IS NULL) INTO n;
    IF n > 0 THEN
      RAISE EXCEPTION 'Pre-flight failed: % tenant-scoped rows still have NULL OrgId. Run quarantine remediation before MakeOrgIdRequired.', n;
    END IF;
  END $$;
  ```
- **Quarantine rule (explicit, never silently flipped):** any residual NULL rows (e.g. orphaned/ownerless bookings, or future anonymous direct-checkout rows) are **assigned to the `casazen-unassigned` fallback Org** by the documented remediation step, then the pre-flight is re-run. Rows are never auto-NULL-flipped into a real tenant.
- **Up:** after the guard passes, `AlterColumn<Guid>("OrgId", nullable: false)` on the four tables and add the FK constraints (`OnDelete: Restrict`). For online/zero-downtime on large tables, add FKs `NOT VALID` then `VALIDATE CONSTRAINT` to avoid long table locks.
- **Down (tested):** drop the four FK constraints and revert the four `OrgId` columns to `nullable: true` (AC10b: `MakeOrgIdRequired → revert to nullable`).

### Zero-downtime / sequencing guarantees (AC10b)
Deploy 1 (add nullable) and Deploy 3 (flip) are fast metadata ops; Deploy 2 (backfill) is online and batched. Because writes are never blocked between deploys and the app writes `OrgId` from Deploy 1 onward, the sequence is online. The three migrations land **in order, before any Phase 1.5 migration** (AC5). `dotnet test` migration + integration suites run against `casazen_test`; AC10 regression asserts each existing owner sees exactly their pre-migration rows under their new default `Org` with no orphans.

### Named migrations (summary)
| Order | Name | Deploy | Up | Down |
|---|---|---|---|---|
| 1 | `AddOrgIdNullable` | 1 | `Orgs` table + nullable `OrgId` + indexes | drop columns/indexes + `Orgs` |
| 2 | `BackfillDefaultOrgs` | 2 | default Orgs + relationship-walk backfill (idempotent, logged) + fallback Org | documented reversible / no-op |
| 3 | `MakeOrgIdRequired` | 3 | pre-flight guard → NOT NULL + FK (`Restrict`) | drop FK + revert to nullable |

---

## GDPR Scope

Regulatory label `compliance:gdpr` (per the issue). This change governs how regulated personal data is partitioned and accessed, so it is **not** `none-required`. No new CIN/Alloggiati/tourist-tax surface is added.

- **Guest/tenant PII now partitioned by `Org`.** Each customer's guest/tenant personal data (names, contacts, booking/lease/payment records) is isolated by `OrgId` through `Booking.OrgId` and `LeaseContract.OrgId` plus the tenant query filter — one `Org` cannot read another's personal data. This is an access-control / data-segregation safeguard under **GDPR Art. 5(1)(f)** (integrity & confidentiality) and **Art. 32** (security of processing). `Guest` rows themselves carry no `OrgId` (per AC2) and are reached only via in-org bookings; cross-tenant guest enumeration is therefore prevented by the booking filter.
- **Controller / processor delineation.** The boundary encodes that each **`Org` is the data controller** for its guests'/tenants' personal data and **CasaZen is the data processor**. This underpins the DPA in `spec-onboarding-plg` (subprocessors: Supabase EU, Auth0, Stripe, SendGrid).
- **Per-Org erasure / retention scoping.** `OrgId` is the structural prerequisite for **per-org right-to-erasure (Art. 17)** and **storage-limitation/retention (Art. 5(1)(e))**. GDPR endpoints (`/api/gdpr/guests/{id}/export|delete|anonymize`) operate on guests reachable from the caller's org bookings; the existing Hangfire retention job runs cross-org by design (resolving org from the entity) and continues to honor `Guest.DataRetentionUntil` / `LeaseContract.DataRetentionUntil` / `ErasureRequested`. Existing consent/retention fields on `Guest` are unchanged.
- **Data-integrity safety.** `OnDelete(Restrict)` on the four org FKs prevents accidental cascade-deletion of bookings/payments/leases when an `Org` is removed; the 3-step migration preserves all existing rows (AC10).

---

## Open Questions

All resolved with a recommended answer.

1. **`/me` path discrepancy — `GET /api/users/me` (AC9/issue) vs the spec's "MeController" note vs existing `GET /api/me/contexts`.**
   **Resolved:** attach `org`/`orgId` to the **`UserDetailDto` returned by the existing `GET /api/users/me`** (`UsersController`), which is the canonical current-user profile surface named by AC9 and already consumed by FE `useCurrentUser`. The spec's "MeController" wording is reconciled to this endpoint; `MeController` (`/api/me/contexts`) stays focused on context bootstrap. No duplicate `/me` is introduced.

2. **Should `Guest` get an `OrgId`?**
   **Resolved: no.** AC2 enumerates exactly four tables (`Property`, `Booking`, `LeaseContract`, `Payment`). A `Guest` can recur across orgs (same person books with multiple operators); adding `OrgId` to `Guest` would force duplication. Guest PII is isolated transitively via `Booking.OrgId`. (Revisit only if a future spec needs per-org guest ownership.)

3. **Default `Org` slug derivation & collisions during backfill.**
   **Resolved:** derive a sanitized slug from the owner identity with a deterministic dedupe suffix and `ON CONFLICT (Slug) DO NOTHING` for idempotency. Slug is internal at this stage (public branded slugs are curated in `spec-branded-booking-site`); operators can rename later via `spec-saas-billing`.

4. **Per-tier limit values (`maxProperties`).**
   **Resolved (provisional, config-driven):** `Starter = 3`, `Pro = 50`, `Scale = unlimited (int.MaxValue)`, sourced from a `tier→limits` map in configuration (`Entitlement:Tiers:*`) so values change without a migration. **`spec-saas-billing` is the source of truth** for final commercial numbers; `IEntitlementService` reads the map, so reconciliation is config-only.

5. **Admin & background-job access under the global filter.**
   **Resolved:** `AdminOnly` endpoints and Hangfire jobs operate cross-org via explicit `IgnoreQueryFilters()` (admin reads audited through `IAdminAccessAuditService`); jobs resolve `OrgId` from the entity, never a caller. This keeps the default path fail-closed while preserving platform operations.

6. **Anonymous `/api/properties/search` `OwnerId` leak.**
   **Resolved (scope boundary):** unchanged by US-004 and intentionally **not** behind the tenant filter (it has no principal). The `OwnerId` PII leak is a separate, already-tracked item (AD-3, counsel item #4) owned by `spec-public-booking-readmodel`; future public surfaces must filter by **explicit `orgId`**, never the caller filter (AC7).

7. **EF global query filter vs repository-level filter (AC7 allows either).**
   **Resolved:** use the **EF global query filter** for the four entities — it is the fail-closed default (no read path can forget it), with the three audited bypass sites above. Repository-level filtering would be opt-in and easier to miss.
