# Runbook: org membership, org roles and the `account` context

Task AM-01 of the wave spec (org team, step 1: who belongs to which org and as what; decisions D1, D12, D14, D15;
backend only). Invitations and seats (D13, D35) come with AM-02, the per-property scope and the fine permissions with
AM-03, the screens with AM-04: **nothing here changes what a user sees today**. The flag `OrgTeam` is off by default.

## 1. What exists after AM-01

| Piece | Where | What it does |
|---|---|---|
| `OrgMember` | table `OrgMembers`, `Casazen.Core/Entities/OrgMember.cs` | **Source of truth** of "this user belongs to this org with this role". `ITenantOwned`. |
| Org roles | `OrgRole` | Owner 1, Admin 2, PropertyManager 3, Collaborator 4, Accountant 5 (explicit integers, never reorder). |
| Memberships | `UserContextMemberships` (existing) | The **projection** of the org role into permissions, written together with the member row. |
| `IOrgMembershipService` | `Casazen.Core/Services`, `Casazen.Infrastructure/Services/OrgMembershipService*.cs` | The **only** writer of both. Everything else (invitations, AM-02) goes through it. |
| `account` context | `AppContexts` row, `AccountContext` | "Amministrazione" of the customer: people, billing, settings, suppliers. The platform console is still `admin` (staff only, D1). |
| Reconcile | `POST /api/admin/org-members/reconcile` | Idempotent command that re-aligns the projection, with a dry run (section 6). |
| `member_inactive` | `InactiveAccountMiddleware` | A deactivated member gets **403 `member_inactive`** from the very next request (section 5). |
| Flag `OrgTeam` | `Features__OrgTeam` | Off. With it off, `GET /api/me/contexts` does not list `account` (section 7). |

### The table

`OrgMembers`: `Id`, `OrgId` (FK `Orgs`, `ON DELETE RESTRICT`), `UserId` (FK `Users`, `ON DELETE CASCADE`), `Role`,
`Status` (Active 1, Deactivated 2), `PropertyScope` (All 1, Selected 2: AM-03 gives `Selected` its meaning, every
member has `All` now), `CreatedAt`, `CreatedByUserId`, `DeactivatedAt`.
**`UIX_OrgMembers_UserId` is unique: a user belongs to one org only** (the org is `User.OrgId`; there is no `OrgId` on
the membership). `IX_OrgMembers_OrgId_Role` serves "who is the owner of this org". Two org owners are possible in the
schema (nothing forbids it) and reported by the reconcile (`org_several_owners`); the ownership is not transferable (D15).

## 2. Roles and permissions

One membership per user per context (unique `(UserId, ContextKey)`): the role of the org decides which row each
context gets. The `account` row exists for the roles that administer or read the org; the rental rows (`short-rent`,
`long-rent`) are the **areas** the person works in, chosen when they are added and kept as the contexts of their
memberships.

| Org role | `account` | `short-rent` / `long-rent` |
|---|---|---|
| Owner | `org_owner` | `property_owner` / `long_term_landlord` (written by the onboarding from the rental type, never by this service) |
| Admin | `org_admin` | `property_manager` / `property_manager` |
| PropertyManager | none | `property_manager` / `property_manager` |
| Collaborator | none | `staff` / `staff` |
| Accountant | `org_accountant` | `accountant` / `accountant` |

`OrgRoleCatalog` (Core) is the one place that says it: the service writes the projection from it, the reconcile
re-aligns to it, the model seeds the roles from it and `OrgRoleCatalogTests` checks the three agree.

Seeded roles (migration `AddOrgMembership`, `HasData`, explicit ids; 1 to 3 are the owner roles and the staff role, older):

| Id | Role | Permissions |
|---|---|---|
| 4 | `account/org_owner` | `org.members.manage`, `org.billing.manage`, `org.billing.read`, `org.settings.manage`, `org.suppliers.manage`, `org.activity.read` |
| 5 | `account/org_admin` | the same six |
| 6 | `account/org_accountant` | `org.billing.read` |
| 7 | `short-rent/property_manager` | `property.*`, `booking.*`, `payment.*`, `ota.*`, `guest.*` + `org.suppliers.manage`; **no billing, no team** (D12) |
| 8 | `long-rent/property_manager` | `property.*`, `lease.*`, `rent.read`, `rent.manage` + `org.suppliers.manage` |
| 9 | `short-rent/staff` | `property.read`, `booking.read`, `guest.read`, `guest.write` |
| 10 | `long-rent/staff` | `property.read` only (D14) |
| 11 | `short-rent/accountant` | `property.read`, `booking.read`, `payment.read` |
| 12 | `long-rent/accountant` | `property.read`, `lease.read` |

The finer permissions the collaborator needs (service requests without `property.write`, guest erasure) arrive with
AM-03; until then the roles use the permissions that exist, so a collaborator cannot create service requests yet.

`OrgBillingAdmin` **keeps its name** and its endpoints (AM-00). It passes for the owner role keys (`OrgOwnerRoles`),
for a platform admin and, new, for an `account` membership holding `org.billing.manage` (`org_owner`, `org_admin`).
`OrgOwnerRoles.All` lists the two account roles and `OrgOwnerRolesCatalogTests` fails when a seeded role holding
`org.billing.manage` is not in it: a role that gets the permission can never be forgotten by the policy or by the
onboarding guard. **Frontend (AM-04):** `ORG_BILLING_ADMIN_ROLE_KEYS` must learn `org_owner` and `org_admin`.

## 3. Who writes what

`IOrgMembershipService` (one transaction per call = one `SaveChanges`; every write of an org takes the same advisory
lock, `PostgresAdvisoryLocks.Scope.OrgMembership` = 1401; the authorization cache of the user is invalidated after each):

| Method | What it does | Refusals |
|---|---|---|
| `EnsureOwnerAsync` | member Owner + `account/org_owner`, never touches the owner rental rows. **Called by the onboarding** right after the org is created. | 409 `org_member_other_org`, `org_member_already_member` |
| `AddMemberAsync` | member + the memberships of the role for the chosen areas, all or nothing; sets `User.OrgId` when empty | 422 `org_owner_not_assignable`, `org_member_area_required`; 409 `org_member_already_member`, `org_member_other_org` |
| `ChangeRoleAsync` | re-points the account row and the role key of every rental row, keeping the areas | 422 `org_owner_not_assignable`; 409 `org_last_owner` |
| `DeactivateAsync` / `ReactivateAsync` | status only; idempotent; the account and the memberships stay | 409 `org_last_owner` (deactivating the owner) |
| `RemoveAsync` | deletes the member and every membership the org gave; leaves the user and its `OrgId` | 409 `org_last_owner` |
| `ReconcileAsync` | section 6 | 409 `org_membership_maintenance_conflict` |

AM-01 exposes no endpoint for these but the reconcile: AM-02 builds the invitation flow on top. Messages are in
`SharedResources(.en).resx` (keys `OrgLastOwner`, `OrgMemberOwnerNotAssignable`, `OrgMemberAreaRequired`,
`OrgMemberAlreadyMember`, `OrgMemberOtherOrg`, `OrgMembershipMaintenanceConflict`, `MemberInactive`).

**The onboarding of an owner is never blocked by the projection.** If the `account/org_owner` role row is missing (the
migration is applied at every startup, so only a test database), `EnsureOwnerAsync` still writes the member row and
logs a warning; the reconcile adds the account membership once the role exists. `AddMemberAsync` refuses instead (a
member without its permissions would be worse than no member).

## 4. Authorization semantics

- **The `account` context** is a *host* context (`HostContextKeys`): like `short-rent` and `long-rent` it waits for the
  onboarding and the current consents. No JWT role maps to it: a token can never grant it, only a membership does.
  `GetDefaultRoute("account")` is `/app/account` (the screens arrive with AM-04). Staff (`admin`) and suppliers are
  untouched.
- **Veto of the token (S3, cites PR #455).** For a user who has an org member row **and** at least one DB membership of
  a rental context, the host contexts come from the DB only: a JWT role never adds a missing host context, so a former
  owner of long-rent (who switched rental type, moved to another org) keeps nothing of it with an old token. `admin`
  and `supplier` still complete from the token. Same rule as the open PR #455 ("host context memberships
  authoritative"), limited to org members: **users in no org team keep getting the contexts of the token**. The veto
  starts with the first rental membership on purpose: an owner whose rental row was never written (the reconcile
  reports it, `owner_without_host_membership`) is not locked out of what it had.
- **A `PropertyManager` is not an owner.** The DB role fallback of `ContextAuthorizationService` used to map a
  `PropertyManager` with no token role and no membership onto the contexts of the owner; it now gets nothing.
- **Deactivation is immediate** (section 5).

## 5. Deactivation and the cache

`TenantContext` already read `Users` on every request without a cache; the same query now carries
`OrgMember.Status` (`CallerTenantQuery`: one read, `IgnoreQueryFilters` on `OrgMembers` explicitly scoped by the
caller, only a Host org counts). A deactivated member gets **403 `member_inactive`** (localized IT/EN) on every
authenticated endpoint, **from the next request, on every API instance**, whatever the 60-second authorization cache
says. `account_inactive` (the block of the staff, which also blocks the Auth0 account) wins when both apply. The
CasaZen account and the Auth0 user of the member are not touched: the reactivation gives everything back. A member that
also works as a supplier is refused on the supplier console too until it is reactivated.

The writes call `IUserAuthorizationCache.Invalidate(userId)`: on the instance that served the write the change is
instant; the other instances converge within the cache duration (60 s). Nothing sends an email or talks to Auth0: the
people of an org have no Auth0 role, their rights are the DB memberships.

## 6. Deploy, backfill and reconcile

### What the migration does (`20261008192122_AddOrgMembership`)

1. Creates `OrgMembers`, seeds the `account` context and the roles 4 to 12 with their permissions (`HasData`).
2. **Backfills the owners, idempotently** (`ON CONFLICT DO NOTHING`; `AddOrgMembership.Data.cs`): every **host** org
   (`OrgType` 0) with **exactly one** owner candidate gets an `OrgMembers` row (Owner, Active, All) and the
   `account/org_owner` membership. A candidate is a user of the org whose user role is an owner role (`PropertyOwner`,
   `LongTermLandlord`) or who holds an owner host membership (`property_owner`, `long_term_landlord`: this is how a
   platform admin that set up its own org shows).
3. **Nothing is guessed.** An org with several candidates (it should not exist: one org per user, created at the
   onboarding) gets **no** member and a `WARNING` in the PostgreSQL log (`AddOrgMembership: org ... has ... owner
   candidates`); the other users of an org (a manager, a collaborator, a user that never onboarded) get none either.
   Supplier orgs (`OrgType` 1) are not org teams.

The migration is applied at startup (`Database.Migrate()`); nothing else to run. **`Down` removes** the memberships of
the roles 4 to 12, the roles, the `account` context and the `OrgMembers` table (the rental memberships of the owners stay).

### Dry run before the deploy (read-only, run against the production database)

The queries are public constants of the migration (`AddOrgMembership.*Sql`) and read nothing the migration creates, so
they run **before** it. `AddOrgMembershipMigrationPostgresTests` runs them on a real database.

| Constant | Answers | Act on it if |
|---|---|---|
| `OwnerCandidatesSql` | the owner candidates, one row per user | the count is not the number of customers you expect |
| `AmbiguousOrgsSql` | host orgs with **several** candidates (`OrgId`, `Candidates`, `Users`) | any row: the org is left without owner; decide who the owner is and fix the data, then run the reconcile |
| `OwnersToCreateSql` | the owners the backfill creates | for the record |
| `UsersWithoutMemberSql` | users of a host org that are not candidates and get no member | usually empty or the staff; a collaborator of a customer listed here keeps working as today |
| `OwnersWithoutRentalMembershipSql` | owner candidates with **no** short-rent or long-rent membership in the DB | any row: the rental contexts of that owner come from the token only; give it its host membership (its onboarding does) so that the DB is the whole truth |

### Reconcile command

`POST /api/admin/org-members/reconcile?dryRun=true` (default; **platform admin only**, policy `AdminOnly`).
`dryRun=false` applies. Idempotent; one run at a time (`PostgresAdvisoryLocks.Scope.OrgMembershipMaintenance` = 1402); a
concurrent change answers 409 `org_membership_maintenance_conflict` and saves nothing. The response carries ids, codes
and counts only (no names, no emails):
`{ dryRun, hostOrgsScanned, membersScanned, fixes: [{ code, userId, orgId, detail }], issues: [{ code, orgId, userIds, detail }] }`.

| Fix code (what a real run changes) | Meaning |
|---|---|
| `owner_member_created` | a host org with one owner candidate and no member got its Owner (same rule as the backfill, also for owners created after the migration) |
| `account_membership_added`, `account_membership_changed`, `account_membership_removed` | the account row follows the org role; a leftover account row of a user that is not a member is removed |
| `host_membership_changed` | the role key of a rental row follows the org role (a `staff` row of an Admin becomes `property_manager`) |

| Issue code (left untouched, needs a decision) | Meaning and action |
|---|---|
| `org_owner_ambiguous` | several users look like the owner of the org: decide, then fix the data |
| `org_user_without_member` | users of a host org with no member (their role is not guessed) |
| `org_several_owners` | two or more Owner rows in one org: keep one, change the role of the other with the service |
| `org_member_org_mismatch` | the org of the member is not the `OrgId` of the user: not aligned |
| `owner_without_host_membership` | the owner has no rental membership (its rental contexts come from the token only) |
| `member_without_host_membership` | a member that is not the owner has no area: nothing was added, the areas are unknown |

The reconcile never gives or takes rental rows from an owner (they come from the onboarding). Run it with `dryRun=true`
first, read the report, then `dryRun=false`; the applied run lists the same fixes.

## 7. The `OrgTeam` flag

`Features__OrgTeam` (default off; `docs/runbooks/feature-flags.md`). Exposed to the SPA as `orgTeam` by
`GET /api/public/features`. In AM-01 it does one thing: **with it off `GET /api/me/contexts` filters `account` out**,
because the web app of today does not know the context (its workspace switcher has no icon for the key and the first
context of the list becomes the active one, so listing it would break the app of every owner). The authorization does not
depend on the list: memberships, policies and `member_inactive` work with the flag off. AM-02 gates the invitation
endpoints on it; **turn it on together with the account screens (AM-04)**, never before. Rollback: unset the variable.

## 8. Rollback

| Level | How | Effect |
|---|---|---|
| Hide the context | `Features__OrgTeam` unset | `account` leaves `GET /api/me/contexts` (it is already off by default) |
| Re-align | `POST /api/admin/org-members/reconcile?dryRun=false` | repairs the projection, never deletes a member |
| Redeploy the previous build | the schema stays (additive): the old code ignores `OrgMembers` and the `account` rows | the veto, `member_inactive` and the account context disappear; the owners keep their rental memberships |
| Undo the migration | `dotnet ef database update <previous migration>` with the code of the previous build deployed | drops `OrgMembers`, the account memberships, the roles 4 to 12 and the context. Do it only if nothing wrote members you need (AM-02 onwards) |

## 9. Tests

Unit (they run everywhere): `OrgMembershipServiceTests`, `OrgMembershipReconcileTests`, `OrgRoleCatalogTests`,
`OrgOwnerRolesCatalogTests`, `ContextAuthorizationServiceOrgMemberTests` (veto, deactivated member, account context,
property manager), `CallerTenantQueryTests`, `InactiveAccountMiddlewareTests`, `AddOrgMembershipMigrationSqlTests` (the
SQL that the Npgsql provider generates), `TenantQueryFilterArchitectureTests` (`OrgMember` is tenant-owned, no exception).
`[PostgresFact]` (CI only, they need PostgreSQL): `AddOrgMembershipMigrationPostgresTests` (backfill, idempotency,
ambiguous orgs, dry-run queries, `Down`), `OrgMembershipPostgresTests` (unique index, foreign keys, tenant filter,
concurrent writes), `OrgTeamPostgresIntegrationTests` (HTTP: onboarding, billing policy per role, veto,
`member_inactive`, reconcile endpoint).
