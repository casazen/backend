# Runbook: automatic tenant filter and OrgId on child rows (TN-2)

Task TN-2 (remaining part of A1-28, section 1 of the A9 audit). Before this change the EF global tenant
filter (`!FilterEnabled || OrgId == tenant.OrgId`) was written by hand on 11 entities, and child rows exposed
by controllers (property documents, OTA integrations, pricing config and history, Alloggiati reports) had no
`OrgId`: each controller had to remember to check the parent. Now:

- every entity implementing `ITenantOwned` gets the filter automatically (named `Tenant`, see
  `AppDbContext.ApplyTenantQueryFilters`); the semantics are unchanged: active only for authenticated
  requests, off for public endpoints, background jobs and design time;
- `PropertyDocuments`, `OtaIntegrations`, `PricingAdapterConfigs`, `PricingHistories` and
  `AlloggiatiWebReports` carry the `OrgId` of their parent (FK `Restrict` to `Orgs`);
  `GuestCheckInSessions.OrgId` (already present) is realigned to its booking and gets the same FK;
- `ConsentRecords` and `PlatformInvoices` (they already had `OrgId`) are filtered too;
- the test `TenantQueryFilterArchitectureTests` fails if a DbSet is neither `ITenantOwned` nor in its
  motivated allow-list (supplier marketplace, user-owned push devices, reference data, lease children
  reached only through the filtered lease, ...).

The migrations run automatically at API startup (`Database.Migrate()`), on Railway `test` first, then
`production`. Nothing has to be configured; the steps below are checks.

## What the migrations do

| Migration | Effect |
|---|---|
| `AddChildEntityOrgIdNullable` | Adds `OrgId uuid NULL` + index to the five child tables. |
| `BackfillChildEntityOrgIds` | Data only, idempotent: copies the parent's org (`Properties` for documents, OTA integrations and pricing; `Bookings` for Alloggiati reports and check-in sessions) into rows whose `OrgId` differs. Logs `RAISE NOTICE 'BackfillChildEntityOrgIds: …'` with the counts. |
| `MakeChildEntityOrgIdsRequired` | Pre-flight: aborts with `Pre-flight failed: …` if a child row has no org, an org different from its parent's, or a check-in session points at an unknown org. Then `SET NOT NULL`, index on `GuestCheckInSessions.OrgId` and FKs `FK_<table>_Orgs_OrgId` (`ON DELETE RESTRICT`). |

## Verification after the deploy

```sql
-- All must be 0.
SELECT count(*) FROM "PropertyDocuments" c JOIN "Properties" p ON p."Id" = c."PropertyId" WHERE c."OrgId" <> p."OrgId";
SELECT count(*) FROM "OtaIntegrations" c JOIN "Properties" p ON p."Id" = c."PropertyId" WHERE c."OrgId" <> p."OrgId";
SELECT count(*) FROM "PricingAdapterConfigs" c JOIN "Properties" p ON p."Id" = c."PropertyId" WHERE c."OrgId" <> p."OrgId";
SELECT count(*) FROM "PricingHistories" c JOIN "Properties" p ON p."Id" = c."PropertyId" WHERE c."OrgId" <> p."OrgId";
SELECT count(*) FROM "AlloggiatiWebReports" c JOIN "Bookings" b ON b."Id" = c."BookingId" WHERE c."OrgId" <> b."OrgId";
SELECT count(*) FROM "GuestCheckInSessions" c JOIN "Bookings" b ON b."Id" = c."BookingId" WHERE c."OrgId" <> b."OrgId";
```

If the API does not start and the logs show `Pre-flight failed`, a row was written without org between
step 2 and step 3 (for example by an old instance still running). Re-run the body of
`BackfillChildEntityOrgIds` (idempotent) and restart the API.

Rollback: reverting `MakeChildEntityOrgIdsRequired` drops the FKs and makes the columns nullable;
`BackfillChildEntityOrgIds` is a no-op on the way down; reverting `AddChildEntityOrgIdNullable` drops the
columns.

## For developers

- A new tenant entity implements `ITenantOwned` and copies `OrgId` from its parent when it is created;
  the filter is registered by itself.
- Cross-org access (admin aggregates, supplier reading the host's property, public token endpoints,
  onboarding right after the org is provisioned) uses `IgnoreQueryFilters()` with an explicit
  `OrgId`/token predicate and a short comment saying why. `IgnoreQueryFilters([AppDbContext.TenantQueryFilter])`
  removes only the tenant filter.

## The tenant is a host org only (PL-05, A1-40)

The tenant of a request is `User.OrgId`, and since PL-05 only when that org has `OrgType = Host`
(`TenantContext.ResolveAsync`, `OrgContextResolver`, the host onboarding gate). A supplier's org is reached only
through `User.SupplierOrgId` and the explicit `SupplierOrgId` predicates of the supplier tables (allow-listed in
`TenantQueryFilterArchitectureTests`): a supplier org never scopes the tenant filter, so a supplier-only account reads
and writes no `ITenantOwned` row, and a user who is host and supplier sees the host rows of its host org only.

The migration `SeparateSupplierOrgFromHostOrgId` moves the existing rows to this rule. It moves **no** tenant row
(`ITenantOwned` tables keep their `OrgId`): a supplier org that already held host rows becomes `OrgType = Host` in
place, and only its supplier side (profile, availability, `SupplierOrgId` links) moves to a new supplier org.
Procedure, pre-deploy query and post-deploy checks: [`suppliers.md` §13](suppliers.md#13-the-supplier-org-never-becomes-the-host-org--pl-05-a1-40).
Extra check after the deploy, must be 0 (every tenant row is on a host org):

```sql
SELECT count(*) FROM "Properties" p JOIN "Orgs" o ON o."Id" = p."OrgId" WHERE o."OrgType" <> 0;
SELECT count(*) FROM "Bookings" b JOIN "Orgs" o ON o."Id" = b."OrgId" WHERE o."OrgType" <> 0;
```
