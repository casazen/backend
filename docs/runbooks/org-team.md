# Runbook: org membership, org roles and the `account` context

Task AM-01 of the wave spec (org team, step 1: who belongs to which org and as what; decisions D1, D12, D14, D15;
backend only) is described in sections 1 to 9. Task AM-02 (step 2: invitations, members and seats; decisions D13, D14,
D15, D35; backend only) is described in sections 10 to 19. Task AM-03 (step 3: the properties each member reaches, read
from the database, and the finer permissions; stakes S3 and S5 of the wave spec; backend only) is described in
sections 20 to 28. The activity log and the access requests come with AM-02b, the screens with AM-04: **nothing here
changes what a user sees today, but for a collaborator limited to "Solo alcuni" (section 20), which nobody is yet**. The
flag `OrgTeam` is off by default and gates every endpoint of the team (sections 18 and 26).

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
`Status` (Active 1, Deactivated 2), `PropertyScope` (All 1, Selected 2: AM-03 gives `Selected` its meaning, section 20; every
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
| 7 | `short-rent/property_manager` | `property.*`, `booking.*`, `payment.*`, `ota.*`, `guest.*`, `servicerequest.write`, `guest.manage`, `alloggiati.submit` (AM-03) + `org.suppliers.manage`; **no billing, no team** (D12) |
| 8 | `long-rent/property_manager` | `property.*`, `lease.*`, `rent.read`, `rent.manage` + `org.suppliers.manage` |
| 9 | `short-rent/staff` | `property.read`, `booking.read`, `guest.read`, `guest.write`, `servicerequest.write` (AM-03) |
| 10 | `long-rent/staff` | `property.read` only (D14) |
| 11 | `short-rent/accountant` | `property.read`, `booking.read`, `payment.read` |
| 12 | `long-rent/accountant` | `property.read`, `lease.read` |

The finer permissions the collaborator needs (service requests without `property.write`, guest erasure, the Alloggiati
declaration) are AM-03's: section 21. The owner role (id 1) holds them too, so nothing changes for it.

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
| `AddMemberAsync` | member + the memberships of the role for the chosen areas, all or nothing; sets `User.OrgId` when empty and records the property scope (AM-02); the onboarding and the four consents of the person are the acceptance's (D14), not copied here | 422 `org_owner_not_assignable`, `org_member_area_required`; 409 `org_member_already_member`, `org_member_other_org` |
| `ChangeRoleAsync` | re-points the account row and the role key of every rental row, keeping the areas | 422 `org_owner_not_assignable`; 409 `org_last_owner` |
| `DeactivateAsync` / `ReactivateAsync` | status only; idempotent; the account and the memberships stay | 409 `org_last_owner` (deactivating the owner) |
| `RemoveAsync` | deletes the member and every membership the org gave and **unlinks the org** (`User.OrgId`, and the last used context when it was one of the org's; AM-02-FU1), so a later onboarding provisions a new org and never makes the person a second owner of this one; the account stays | 409 `org_last_owner` |
| `AbandonEmptyOrgAsync` | AM-02: the owner of an empty org leaves it (owner row, memberships, `OrgId`); only the acceptance of an invitation calls it (section 12) | 409 `org_member_other_org` |
| `ReconcileAsync` | section 6 | 409 `org_membership_maintenance_conflict` |

AM-01 exposes no endpoint for these but the reconcile; AM-02 builds the invitations and the member endpoints on top (sections
10 to 14). Messages are in
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
depend on the list: memberships, policies and `member_inactive` work with the flag off. AM-02 gates every endpoint of the
invitations and the members on it (404 for everybody, before the authentication, while it is off; section 18);
**turn it on together with the account screens (AM-04)**, never before. Rollback: unset the variable.

## 8. Rollback

| Level | How | Effect |
|---|---|---|
| Hide the context | `Features__OrgTeam` unset | `account` leaves `GET /api/me/contexts` (it is already off by default) |
| Re-align | `POST /api/admin/org-members/reconcile?dryRun=false` | repairs the projection, never deletes a member |
| Redeploy the previous build | the schema stays (additive): the old code ignores `OrgMembers` and the `account` rows | the veto, `member_inactive` and the account context disappear; the owners keep their rental memberships |
| Undo the migration | `dotnet ef database update <previous migration>` with the code of the previous build deployed | drops `OrgMembers`, the account memberships, the roles 4 to 12 and the context. Do it only if nothing wrote members you need (AM-02 onwards; the `AddOrgInvitations` migration is undone first, section 18) |

## 9. Tests

Unit (they run everywhere): `OrgMembershipServiceTests`, `OrgMembershipReconcileTests`, `OrgRoleCatalogTests`,
`OrgOwnerRolesCatalogTests`, `ContextAuthorizationServiceOrgMemberTests` (veto, deactivated member, account context,
property manager), `CallerTenantQueryTests`, `InactiveAccountMiddlewareTests`, `AddOrgMembershipMigrationSqlTests` (the
SQL that the Npgsql provider generates), `TenantQueryFilterArchitectureTests` (`OrgMember` is tenant-owned, no exception).
`[PostgresFact]` (CI only, they need PostgreSQL): `AddOrgMembershipMigrationPostgresTests` (backfill, idempotency,
ambiguous orgs, dry-run queries, `Down`), `OrgMembershipPostgresTests` (unique index, foreign keys, tenant filter,
concurrent writes), `OrgTeamPostgresIntegrationTests` (HTTP: onboarding, billing policy per role, veto,
`member_inactive`, reconcile endpoint).

---

# Part 2 - AM-02: invitations, members and seats

## 10. What AM-02 adds

| Piece | Where | What it does |
|---|---|---|
| `OrgInvitation` | table `OrgInvitations`, `Casazen.Core/Entities/OrgInvitation.cs` | An invitation to join the org with a role: the email and name of the invitee (personal data), the role, the areas, the SHA-256 of the secret link, the state and the dates. `ITenantOwned`. |
| `IOrgInvitationService` | `Casazen.Infrastructure/Services/OrgInvitationService*.cs` | Create, list, send again, revoke, copy the link, look up and accept. |
| `IOrgTeamService` | `OrgTeamService.cs` | List the people and their seats; change a role; deactivate, reactivate, remove. The non-escalation rules (section 14). |
| `IOrgSeatService` | `OrgSeatService.cs` | The seats of an org: active members + pending invitations against the plan (section 13). |
| `IOrgEmptinessChecker` | `OrgEmptinessChecker.cs` | Decides whether the org of a person can be left when the person accepts an invitation elsewhere (section 12). |
| `OrgInvitationMaintenanceJob` | `Casazen.Web/BackgroundJobs` | Hourly: reminder of the third day, expiry, deletion of the closed ones after 30 days (section 15). |
| Policy | `RequireContext:account:org.members.manage` (`CasazenPolicies.OrgMembersManage`) | Owner and administrators hold the permission (section 2). |

### Endpoints

All of them answer **404 to everybody, before the authentication, while `Features__OrgTeam` is off** (`FeatureGate`).
The org is always the caller's own (`IOrgContextResolver`), never a value of the request. Errors are the usual problem
details with a stable `code` and a message in the language of `Accept-Language`.

| Endpoint | Who | Answer |
|---|---|---|
| `POST /api/orgs/me/invitations` | owner, administrator | 201 `{ invitation, emailQueued }` |
| `GET /api/orgs/me/invitations` | same | the invitations that are open or expired (never the token), newest first |
| `POST /api/orgs/me/invitations/{id}/resend` | same | 200 `{ invitation, emailQueued }`: new link, seven more days |
| `POST /api/orgs/me/invitations/{id}/revoke` | same | 204, idempotent |
| `POST /api/orgs/me/invitations/{id}/link` | same | 200 `{ invitationId, url, expiresAt }`, `Cache-Control: no-store`: a fresh link to hand out ("copy link") |
| `GET /api/orgs/me/members` | same | `{ items, seats }`, owner first |
| `PUT /api/orgs/me/members/{id}` | same | 200 member; body `{ role }` |
| `POST /api/orgs/me/members/{id}/deactivate`, `.../reactivate` | same | 200 member |
| `DELETE /api/orgs/me/members/{id}` | same | 204: the person leaves the org, its account stays |
| `POST /api/org-invitations/lookup` | anonymous, rate limited | 200 preview (org name, invited email and name, role, areas, expiry) or **410 `invitation_invalid`** |
| `POST /api/org-invitations/accept` | any signed-in account | 200 `{ orgId, orgName, role, areas, leftEmptyOrg }` |

`GET /api/orgs/me/entitlement` also carries `limits.maxSeats`, `usage.seats` and `canInviteMember` (section 13).
`link` and `DELETE` are not in the task text: the first is what "copy link" needs without sending an email, the second is
the only way to finish what `RemoveAsync` (AM-01) already does. The request and response shapes are in
`Casazen.Web/DTOs/Orgs/OrgTeamDtos.cs`; enums travel as member names (`"Collaborator"`).

The validation errors of the body are 400 (invalid email or name, language other than `it`/`en`); the rules are 422
(`org_owner_not_assignable`: nobody invites an owner; `org_member_area_required`: at least one of `short-rent`, `long-rent`).

## 11. The invitation

**Table `OrgInvitations`.** `Id`, `OrgId` (FK `Orgs`, cascade), `Email` (trimmed, lowercase, 255), `Name` (200), `Role` (never
Owner), `Areas` (`text[]`: `short-rent`, `long-rent`), `PropertyScope`, `TokenHash` (64 hex characters), `Status`
(Pending 1, Accepted 2, Revoked 3, Expired 4), `ExpiresAt`, `ReminderSentAt`, `InvitedByUserId`, `Language` (`it`/`en`),
`AcceptedAt`, `AcceptedByUserId`, `ClosedAt`, `CreatedAt`, `UpdatedAt`. Indexes: `UIX_OrgInvitations_TokenHash` (unique),
**`UIX_OrgInvitations_OrgId_Email_Pending` (unique, partial: `"Status" = 1`)**, `IX_OrgInvitations_OrgId_Status`,
`IX_OrgInvitations_Status_ExpiresAt`.

**The token.** 256 random bits (64 hex characters) from `RandomNumberGenerator`. Only its SHA-256 is stored, compared in
constant time; nobody (not the inviter, not an operator) can read it back. It travels in the body of the two public
calls, never in a URL of the API, and it is never logged (logs carry the invitation id, the org id and the email
**masked**, `LogRedaction.MaskEmail`). The link of the email is `<App__PublicSiteBaseUrl>/invite/accept?token=...`: the page
that receives it (AM-04) must read the token and take it out of the address bar at once, and send it in the body.
**Every new link replaces the old one** (resend, copy link, reminder): the previous email stops working.

**Lifecycle.**

| From | To | Because |
|---|---|---|
| (new) | Pending | `POST .../invitations`: valid seven days, holds a seat |
| Pending | Accepted | the invited account accepts (section 12) |
| Pending | Revoked | `.../revoke` (a second call finds it done) |
| Pending | Expired | the job marks it once `ExpiresAt` has passed; **for every reader it is already expired the moment the date passes**, and the seat is free |
| Expired | Pending | `.../resend` (takes a seat again; refused if the person joined or has a newer invitation meanwhile) |

One **pending** invitation per email and org: 409 `org_invitation_already_pending` (the partial unique index decides
when two requests race; an overdue row of the same email is closed first so it never blocks a new one). The email of a
person who already is a member (active or deactivated): 409 `org_member_already_member`. Only the **owner** invites an
administrator, and only the owner sends that invitation again, revokes it or copies its link: 403 `org_owner_required`.
An administrator does those three for every other role.

**What happens on create.** Validation, then the caller's own member row (must be active, owner or administrator), then
the public URL (a missing `App__PublicSiteBaseUrl` is a configuration error found **before** anything is saved), then one
transaction under the org's seats lock (section 13): close the overdue row of the email, member check, duplicate check, seat check,
insert, commit. **The email is queued after the commit**; if the queue refuses it the answer says `emailQueued: false`
and the invitation exists: copy the link or send it again.

## 12. The acceptance and the change of org

`POST /api/org-invitations/accept` (body: `token` and the four `consents` of the onboarding). The checks run in this order,
so an old link says why it no longer works and a stranger learns nothing:

| # | Check | Refusal |
|---|---|---|
| 1 | The consents are complete and at the current versions (decision D14: the person accepts Terms, Privacy, DPA and the subprocessors list again, **for the org it joins**) | 400 `consents_incomplete`, `stale_documents` |
| 2 | The token is well formed and known | 410 `invitation_invalid` |
| 3 | The account is not staff (token role `Admin`, DB role `Admin` or a membership of the `admin` context): staff never join a customer's org | 403 `invitation_platform_admin` |
| 4 | The same account accepted this very invitation and still is an active member: **the answer of the first time again** (double click, retry after a lost answer) | 200 |
| 5 | The state: accepted by somebody else / revoked / expired | 410 `invitation_used`, `invitation_revoked`, `invitation_expired` |
| 6 | The email of the account is verified and **equal to the invited one** (trimmed, case-insensitive). The email and its flag come from the access token (`https://casazen.app/email`, `/email_verified`) or from Auth0, never from the body | 403 `invitation_email_not_verified`, `invitation_email_mismatch` |
| 7 | The org still exists, is active and is a host org | 410 `invitation_invalid` |
| 8 | What the account is today (below) | 409 `org_member_already_member`, `invitation_user_has_organization` |
| 9 | Under the locks, in one transaction: the presented token is still the current one (2), then the checks 4, 5 and 8 again (5 on the clock under the lock), then the writes | the same codes |

Checks 2, 4, 5 and 8 are made again under the lock. The token is compared with the row as it is now, and the expiry with
the clock as it is now: a link replaced or expired while the request waited is refused. Of two requests for the same
token, one writes and the other finds the work done (4), whichever read the row first.

**What the account is today (check 8).**

- **No org**: it joins.
- **A member of this org** (another invitation): 409 `org_member_already_member`.
- **A member of another org who is not its owner**: 409 `invitation_user_has_organization`. Nothing is merged, ever.
- **The owner of its own org** (the one the onboarding created for it): it joins **only if that org is empty**, otherwise the same 409.
  If the org is empty the person leaves it (below). A legacy owner whose member row was never written is treated the same.
- A `User.OrgId` that points to an org that is not a host org (a supplier's own org): the link is only cleared, the supplier link stays on `User.SupplierOrgId`.

**"Empty" (`IOrgEmptinessChecker`).** The org is a host org, on Starter, with no Stripe customer, subscription, Connect
account, custom domain, subdomain or branding; the person is the only user and the only member; and no row in **any
tenant-owned table**, except the consents, the signup attribution and the previous slugs the onboarding itself writes.
The tables are read from the EF model (`ITenantOwned`), so a new tenant table blocks the move from the day it exists;
`OrgEmptinessCheckerTests` fails if one cannot be read. Any doubt is "not empty". An invitation the person sent from that org
counts as data: whoever invited somebody cannot leave.

**What leaving does** (all in the transaction of the acceptance): the owner `OrgMember` row and the memberships of the
`account`, `short-rent` and `long-rent` contexts are removed; `User.OrgId` and the last used context are cleared; the
account's `Role` goes to `None` and its `RentalType` to null; the old org is **deactivated, not deleted** (its consents
and attribution stay); then the person is added to the new org with the invited role and areas, gets
`OnboardingCompletedAt` if it had none, and its four consents are recorded for the new org (with the client IP).

**Locks, always in this order** (no cycle between two requests): the seats lock of the org joined, and when an empty org is
left the seats lock of that org too (the two ordered by org id), then its property-slot lock (a property being created for
it at that very moment finishes first, or finds the org gone), then the people lock of each org (taken by
`IOrgMembershipService`). The whole thing is one transaction: any failure, a missing role row included, leaves the person
exactly as it was (`OrgInvitationsPostgresTests`).

**Races.** Two orgs inviting the same person, both accepted at once: the unique index on `OrgMembers.UserId` lets one in; the
other gets 409 `invitation_user_has_organization` and its invitation stays open. Accept against revoke: one wins; the
loser answers 410 `invitation_revoked` or 409 `org_invitation_not_pending`.

**After the commit.** The person's authorization cache is invalidated again (the inner writes did it before the commit).
When an org was left, the Auth0 roles `PropertyOwner` and `LongTermLandlord` are removed, **best effort**: a failure is
logged (`Auth0 owner roles of user ... not removed`) and the acceptance stands. Section 17 says what an operator does.
Meanwhile nothing is open to the person that it should not have: its contexts come from the DB, and
`OrgBillingAdminAuthorizationHandler` ignores the two owner roles of a token for a member who is not the owner.

## 13. Seats (decisions D13 and D35)

**Seats used = active members + pending invitations that have not expired.** The accountant counts like everybody else; a
deactivated member does not (reactivating takes a seat); an invitation holds its seat until it is accepted, revoked or
expires, and an expired one frees it **the moment its date passes**, whether or not the job has run.

| Plan | Seats | Override |
|---|---|---|
| Starter | 2 | `Entitlement__Tiers__Starter__MaxSeats` |
| Pro | 10 | `Entitlement__Tiers__Pro__MaxSeats` |
| Scale | unlimited (`2147483647`) | `Entitlement__Tiers__Scale__MaxSeats` |

The override is a positive integer; anything else (missing, text, zero, negative) is ignored, so a typo never locks an org
out nor makes a plan unlimited. The limit is the one of the **effective** plan, the same rule as the property limit
(`IEntitlementService.ResolveEffectiveTier`, A1-11): an org whose subscription is not in good standing has the Starter number
whatever it stored. The members stay, nothing is deactivated, every new invitation and every reactivation is refused
(409 `org_seat_limit_reached`) until the org is back in good standing or below the limit; `usage.seats` can be above
`limits.maxSeats` and `Available` is never negative.

**The last seat goes to one request.** Every decision about seats (create an invitation, send an expired one again, accept,
reactivate, and the reminders and expiries of the job) runs in one transaction that first takes the PostgreSQL advisory lock
`PostgresAdvisoryLocks.Scope.OrgSeats` (1403, key: the org id), exactly like `CreatePropertyWithinLimitAsync` does for the
properties. Lock order, always: seats of the org (or of two orgs, by id) -> property slot of an org being left -> people of
the org (`OrgMembership`, 1401). `OrgInvitationsPostgresTests` proves it with six parallel invitations for one seat, an
invitation against a reactivation, and an acceptance against an invitation.

Where the numbers are shown: `GET /api/orgs/me/members` -> `seats` (`max`, `used`, `available`, `activeMembers`,
`pendingInvitations`, `unlimited`, `canInvite`) and `GET /api/orgs/me/entitlement` -> `limits.maxSeats`, `usage.seats`,
`canInviteMember`. The entitlement endpoint stays under the billing policy (owner and administrator).

## 14. Members and the non-escalation rules (decisions D12 and D15)

`OrgTeamRules` (pure functions, shared by the services and the tests); the policy only lets the owner and the
administrators in, these rules say what each may do to whom:

| Action | Owner | Administrator |
|---|---|---|
| Invite / give the role **Admin** | yes | **no** (403 `org_owner_required`) |
| Invite / give PropertyManager, Collaborator, Accountant | yes | yes |
| Give the role **Owner** | **never** (422 `org_owner_not_assignable`: no transfer of ownership, D15) | never |
| Change, deactivate, reactivate, remove an Admin | yes | **no** (403 `org_owner_required`) |
| Change, deactivate, reactivate, remove a PropertyManager, Collaborator or Accountant | yes | yes |
| Touch the owner | **never** (409 `org_last_owner`, whoever asks) | never |

The rules are checked **inside the lock of the org's people, on the roles as they are at that moment**, not as the page
drew them: an administrator cannot act on somebody who has just become one. A member of another org is 404
`org_member_not_found` (the id is looked up inside the caller's org). A property manager and a collaborator do not reach
these endpoints at all (the policy answers 403): there is no billing or team access for them (D12).

Changing the role keeps the areas the person works in and re-points its memberships (section 3). Reactivating takes a seat
(409 `org_seat_limit_reached`). **Removing** a member deletes its member row and its memberships and unlinks the org
(`User.OrgId`); the account, its consents and the data it wrote stay, and it can be invited again. A deactivated member
keeps everything but is refused with 403 `member_inactive` (section 5).

## 15. Emails and the maintenance job

Three emails, Italian and English (`EmailTexts.resx`, `EmailTexts.en.resx`), listed in `docs/runbooks/email.md`. All are
queued through `IEmailQueue` (Hangfire `EmailDeliveryJob`), always **after** the data is saved:

| Template | To | When | Language |
|---|---|---|---|
| `org-invitation` | the invited email | the invitation is created or sent again | the one the inviter chose (`language`, default `it`) |
| `org-invitation-reminder` | the invited email | the third day after it was sent, while still pending: **with a new link** | same |
| `org-invitation-expired` | the inviter (the owner when the inviter no longer manages the org) | the job marks the invitation expired; the seat is free | Italian |

**The job** `org-invitation-maintenance` (`OrgInvitationMaintenanceJob`, cron `10 * * * *` UTC, `[DisableConcurrentExecution]`,
and the session advisory lock `PostgresAdvisoryLocks.Scope.OrgInvitationMaintenance` 1404 so two runs never overlap even
outside Hangfire's own lock; a skipped run logs `Org invitation maintenance skipped`). Each step is idempotent and takes
the org's seats lock **for the one invitation it touches**, so it never races with a person who accepts, revokes or sends
it again, and one failing invitation does not stop the others (200 per step per run, the next run takes the rest):

1. **Expire**: `Pending` with `ExpiresAt <= now` -> `Expired`, `ClosedAt = ExpiresAt`; the inviter is told, once (only the run that changes the status sends the note).
2. **Remind**: `Pending`, not expired, no reminder yet, due within four days of the expiry. The email is queued **first** and the
   new link saved only if it was queued: a refused email leaves the person with the old, working link and the next run tries again.
3. **Purge**: closed invitations (accepted, revoked, expired) are **deleted with the name and the email of the invitee**
   `OrgTeam__InvitationRetentionDays` days after they were closed (default 30, minimum 1).

The job is registered whatever the flag says, because the retention of personal data does not depend on a feature being on.
With `Features__OrgTeam` off it only purges (steps 1 and 2 would write to people who cannot see the screens).

**What Hangfire keeps.** The queued job of the invitation email carries the recipient, the subject and the **HTML with the
live link**, so the token is readable in the Hangfire job arguments until Hangfire removes the succeeded job (24 hours;
`docs/runbooks/email.md`, "Data kept in Hangfire"). The retention of the job arguments is not configured further, and the
risk is accepted because the link is **single use** and the acceptance needs an account whose **verified email is the
invited one**: whoever reads the Hangfire schema can read the link but cannot use it with another account. The dashboard
stays off and the schema restricted all the same (`docs/runbooks/hangfire.md`). After the job is gone the token exists
nowhere: the database has only its hash.

## 16. GDPR

- **Personal data of an invitation**: name and email of the invitee, kept until 30 days after the invitation closed, then
  deleted with the row (section 15, step 3). The 30 days are the time to answer "who invited me and when?". Nothing else
  of the invitee is stored before it accepts.
- **Consents**: the person who accepts records the four consents (Terms, Privacy, DPA, subprocessors) for the org it joins
  (D14) with the version and the IP. An empty org that is left keeps its consents (history).
- **Org export** (`GET /api/gdpr/org/export`): the new `members` list, with `UserId`, role, status, property scope and the dates
  of each member of the org. **No names, no emails**: the export is reachable with property permissions until #461 moves it
  under the owner's, and the names are on the people page. The invitations are not exported (temporary, closed ones are deleted).
- **Logs** never carry the token, and carry the email only masked (`m***@example.com`).
- **Privacy notice (legal text).** It must say that the name and the email of the invitee are processed to send the
  invitation and kept until 30 days after it closed, and that the people of an org are visible to its owner and
  administrators. The wording belongs to the legal owner (`docs/runbooks/legal-documents.md`): **not changed by AM-02**, to do before the flag is turned on.
- Erasing the account of a member is not part of AM-02 (`docs/runbooks/gdpr.md`): the member row is deleted with the user (cascade).

## 17. Operating it

| Situation | What to do |
|---|---|
| Log `Auth0 owner roles of user ... not removed after it left its empty org` | Auth0 was down or refused. In the Auth0 dashboard, *User Management -> Users -> the user -> Roles*, remove `PropertyOwner` and `LongTermLandlord`. Nothing is open to the person meanwhile (its contexts come from the DB and the billing policy ignores the owner roles of a member who is not the owner), but the roles are of no use to a member and should go. A token already issued keeps them until it is renewed. |
| "The email did not arrive" | The create answer said `emailQueued`: `false` means the queue refused it (provider not configured or queue down, `docs/runbooks/email.md`): copy the link (`.../link`) or send it again. With `true`, look for `Email org-invitation queued` and the delivery log of `EmailDeliveryJob`; ask to check the spam folder. Sending again or copying the link replaces the link: tell the person to use the newest. |
| 403 `invitation_email_mismatch` | The person signed in with another address than the invited one. Either it signs in with the invited address, or revoke and invite the address it uses. |
| 403 `invitation_email_not_verified` | The email is not verified in Auth0. After verifying it the client needs a new token (`docs/runbooks/auth0.md` section 6). Without the post-login Action the backend asks the Management API (`read:users`). |
| 409 `invitation_user_has_organization` | The person already works in an org, or its own org has data or billing. Nothing is merged: it uses another account, or the staff deals with it case by case. |
| A seat looks taken and nobody uses it | A **pending** invitation holds it until it expires or is revoked (a deactivated member does not). The people page shows both. |
| Lookup answers 429 | 20 calls a minute for each IP (`RateLimiting__PublicInvitationLookup__PermitLimit`); the answer is uniform on purpose, so there is nothing to retry faster. |

Read-only queries for support (never write by hand: the services keep the member rows and the memberships together):

```sql
-- invitations by state
SELECT "Status", count(*) FROM "OrgInvitations" GROUP BY "Status" ORDER BY "Status";

-- seats of one org
SELECT
  (SELECT count(*) FROM "OrgMembers" WHERE "OrgId" = '<org id>' AND "Status" = 1) AS active_members,
  (SELECT count(*) FROM "OrgInvitations"
     WHERE "OrgId" = '<org id>' AND "Status" = 1 AND "ExpiresAt" > now()) AS pending_invitations;
```

## 18. Deploy, flag and rollback of AM-02

**Configuration.** Nothing is required: every variable has a default. `App__PublicSiteBaseUrl` and the email provider must be
set for the links and the emails (they already are in every environment that sends mail). Optional:
`OrgTeam__InvitationRetentionDays` (30), `Entitlement__Tiers__{Starter|Pro|Scale}__MaxSeats` (2, 10, unlimited),
`RateLimiting__PublicInvitationLookup__PermitLimit` (20 a minute for each IP).

**The migration** `20261008205127_AddOrgInvitations` is additive: one table and four indexes, no data is touched, the previous
build ignores the table. It is applied at startup like the others.

**Order.** (1) Deploy with `Features__OrgTeam` unset: every endpoint of this part answers 404, `GET /api/me/contexts` is
unchanged, the entitlement answer gains `limits.maxSeats`, `usage.seats` and `canInviteMember` (additive), the org export gains
`members`, and the maintenance job runs (it only deletes closed invitations, of which there are none). Nothing changes for a
user. (2) AM-03 (per-property scope and fine permissions) and AM-04 (the screens, which must take the token out of the
address bar and learn `org_owner` / `org_admin` for the billing screens). (3) **Only then** set `Features__OrgTeam=true`:
turning it on earlier shows the `account` context to a web app that does not know it.

| Level | How | Effect |
|---|---|---|
| Stop the feature | unset `Features__OrgTeam` | the endpoints answer 404; the people already invited can no longer accept; open invitations keep expiring and closed ones keep being purged (the job runs anyway); members keep their access |
| Stop one person | deactivate the member (`.../deactivate`) or revoke the invitation | immediate; the seat is free (revoke) or kept (deactivate does not count) |
| Redeploy the previous build | nothing to undo: the table is ignored | members keep working through the `AM-01` rows; an invitation link no longer works |
| Undo the migration | `dotnet ef database update 20261008192122_AddOrgMembership`, with the previous build deployed | drops `OrgInvitations` (open invitations are lost, no member or consent is touched). The people who already joined stay members |

## 19. Tests of AM-02

Unit (they run everywhere, the services over the EF in-memory provider with a fake clock): `OrgInvitationServiceTests`,
`OrgInvitationAcceptanceTests` (every refusal and every way in), `OrgEmptinessCheckerTests` (with the reflective test that every
tenant table is read), `OrgTeamServiceTests`, `OrgSeatServiceTests`, `OrgInvitationMaintenanceServiceTests`,
`OrgExportMembersTests`, `OrgTeamRulesTests`, `OrgInvitationTokensTests`, `OrgInvitationRulesTests`, the controllers
(`OrgTeamControllersTests`, `OrgInvitationAcceptanceControllerTests`, `OrgsControllerEntitlementSeatsTests`),
`AccountEmailResolverTests`, `OrgInvitationEmailTemplatesTests`, `OrgInvitationMaintenanceJobTests`,
`OrgTeamLocalizationTests` (every code has a message in both languages), `AddOrgInvitationsMigrationSqlTests`,
`TenantQueryFilterArchitectureTests` and `EndpointAuthorizationArchitectureTests` (the policy is used, the acceptance is
the only authenticated-only action and says why), `ErrorHandlingMiddlewareTests` (403 and 410).
HTTP (in-memory locally, PostgreSQL in CI): `OrgInvitationsHttpIntegrationTests` (the journey, who may do what, validation,
seats, every refusal, an owner leaving an empty org, an org in use) and `OrgInvitationsFlagOffIntegrationTests` (404 of every
endpoint).
`[PostgresFact]` (CI only): `OrgInvitationsSchemaPostgresTests` (the indexes), `OrgInvitationsPostgresTests` (concurrency
of the seats and of the acceptance, atomicity of leaving an empty org, the maintenance job and its lock, the tenant filter).

---

# Part 3 - AM-03: the properties each member reaches, and the finer permissions

## 20. What AM-03 adds

A member of the org sees and touches **only the properties it reaches**, in every list and on every single resource, and
what it reaches is read **from the database**, not from the claims of the token (stake S3 of the wave spec).

| Piece | Where | What it does |
|---|---|---|
| `PropertyMemberAccess` | table `PropertyMemberAccesses`, `Casazen.Core/Entities/PropertyMemberAccess.cs` | One row per (person, property): the properties given to a **collaborator "Solo alcuni"**. `ITenantOwned`. Unique `(UserId, PropertyId)`; deleted with its property or its person (cascade). |
| `Property.ResponsibleUserId` | column `Properties.ResponsibleUserId` (FK `Users`, `ON DELETE SET NULL`) | The member in charge of a property: told about it, with the administrators (section 24). |
| `HostScope`, `IHostScopeResolver` | `Casazen.Core/Authorization`, `HostScopeResolver` | The one answer to "which properties does the caller reach?", read from the org membership in the authorization snapshot (database, cached 60 s). Replaces `GetHostScope`, which looked at the JWT roles. |
| `query.InScope(scope)` | `HostScopeQueryExtensions` | The one way a list narrows to the scope: an `EXISTS` on `PropertyMemberAccesses`, parameterised, in the same SQL statement. Replaces the thirteen `scope.OwnerId` filters. |
| `IOrgPropertyAccessService` | `OrgPropertyAccessService` | Reads and sets the properties of a member (with the count of people who reach each one) and the person in charge of a property. |
| `IOrgHolderService` | `OrgHolderService` | Who is the holder of the org for the RLI delega and the IMU communication (section 25, stake S5). |
| Fine permissions | `HostPermissions` | `servicerequest.write`, `guest.manage`, `alloggiati.submit` (section 21). |

### The rule (what a caller reaches)

| The caller | Reaches |
|---|---|
| Owner, Admin, PropertyManager or Accountant member, active | every property of the org (a stored `Selected` on these roles is ignored; the writes refuse it) |
| Collaborator member, active, scope `All` | every property of the org, the ones created later too |
| Collaborator member, active, scope `Selected` | **only** the properties of its `PropertyMemberAccesses` rows; none while it was given none |
| Deactivated member, member of another org, inactive account | nothing (`null` scope: 403 where a scope is required, an empty list elsewhere), whatever the token says |
| Account in **no** org team (an owner from before the team, a test account) | the rule of before, from the token: `PropertyManager` or `Admin` role = the org; any other role = the properties it created (`Property.OwnerId`) |

A role left in the token **never widens** a member: the row decides. The decision is `HostScopeResolver.Decide`, a pure
function of the snapshot (`HostScopeResolverTests` is its table).

### Lists and single resources

- **Lists** (`InScope`). Bookings, leases, payments, properties (also the CIN summary), the three fiscal reports, the
  dashboard (properties, bookings, iCal feeds), the requests waiting for the host, the interventions, and, added with the
  scope, the compliance cockpit and the Alloggiati stays. One SQL statement each: the reach is an `EXISTS` inside it, never a
  list of ids loaded first and never a query per row (`HostScopeSqlShapeTests`, `HostScopePostgresTests`). It is **not** a second
  global EF filter on purpose: a global filter would also apply to the jobs, which run for no user, and to every `Include`.
  A row bound to no property (an org-level request) is outside every restricted scope.
- **Single resources** (`HostResourceAuthorizationHandler`). `HostResource` carries the id of the property. The handler asks
  the resolver, which answers from the cached snapshot (no query). A restricted member without a property id on the resource
  is refused (fail closed). The org-wide members never reach this check.
- The fiscal area needs `payment.read` as well as `property.read` (a collaborator reads properties, not money).

## 21. The finer permissions

Three permissions of the `short-rent` context were carved out of broad ones, so a role can be given the one action without
the rest. The migration gives them to the roles that did the work before (the owner, id 1, and the property manager, id 7), so
**nothing changes for them**; the collaborator gets only the first.

| Permission | Actions (was) | Owner | Property manager | Collaborator | Accountant |
|---|---|---|---|---|---|
| `servicerequest.write` | create an intervention for a stay, find a supplier for it, mark it paid: `POST /api/service-requests`, `.../match-supplier`, `.../{id}/mark-paid` (`property.write`) | yes | yes | **yes** | no |
| `guest.manage` | erase, anonymize and change the consents of a guest: `DELETE /api/guests/{id}` and the three write actions of `GdprController` (`guest.write`) | yes | yes | no | no |
| `alloggiati.submit` | register the guests of a stay and declare the Alloggiati communication sent: `PUT /api/alloggiati/{bookingId}/stay-guests`, `.../mark-sent-manually`, `.../send` (`booking.write`) | yes | yes | no (see below) | no |

The collaborator of the seeded catalogue therefore holds `property.read`, `booking.read`, `guest.read`, `guest.write`,
`servicerequest.write` and **not** `property.write` (prices, CIN, pause, delete), `booking.write` (create, cancel, move),
`payment.*`, `ota.*`, `guest.manage`, `alloggiati.submit`. `OrgRoleActionMatrixTests` is the table above for 46 actions of
the API, evaluated on the policies the actions really carry and the roles really seeded.

**Decision to confirm (product owner).** The collaborator does *not* get `alloggiati.submit`: the registration of the guests
for the police is a legal act of the host, and the task text lists the permissions of the collaborator without it. The change is
one row of `RolePermissions` (role 9) plus the catalogue (`OrgRoleCatalog`); until it is made, a collaborator can read the
Alloggiati list but not register or declare.

## 22. Endpoints

All of them need the policy `RequireContext:account:org.members.manage` (owner and administrators) except the last one, and the
first two answer **404 to everybody, before the authentication, while `Features__OrgTeam` is off**. The org is always the one of the caller.

| Endpoint | Answer |
|---|---|
| `GET /api/orgs/me/members/{id}/properties` | 200 `{ memberId, role, propertyScope, scopeSupported, properties: [{ propertyId, name, city, granted, peopleWithAccess }] }`: the active properties of the org, with whether this member reaches each one and **how many active people of the org reach it** ("chi puo accedere"). `scopeSupported` is `false` for a member who is not a collaborator. 404 `org_member_not_found`. |
| `PUT /api/orgs/me/members/{id}/properties` | 200 the same view. Body `{ propertyScope: "All" \| "Selected", propertyIds: [guid] }` (up to 500). `All` removes the rows (every property, the ones added later too); `Selected` replaces the set by exactly the ids given, none meaning the person sees nothing. The difference is written, not the whole set. 400 for a missing scope or more than 500 ids; 403 `org_owner_required` (only the owner touches an administrator); 404 `org_member_not_found`; 422 `org_member_scope_not_supported` (`Selected` for a role that is not the collaborator) and `org_member_property_unknown` (an id that is not a property of the org). |
| `PUT /api/properties/{id}/responsible` | 204. Body `{ userId }`, `null` for nobody. Needs the permission to change the property (`property.write` in a rental context, and the same check on the property as `PUT /api/properties/{id}`). 404 `property_not_found`; 422 `property_responsible_invalid` when the person is not an active member of the org with an active account who reaches the property. The record of the property (`PropertyResponse`) carries `responsibleUserId`. |

Messages are in `SharedResources(.en).resx` (keys `OrgMemberScopeNotSupported`, `OrgMemberPropertyUnknown`,
`OrgMemberPropertyScopeRequired`, `OrgMemberPropertyIdsTooMany`, `PropertyResponsibleInvalid`); `OrgTeamLocalizationTests`
checks every code in both languages.

## 23. Who writes what, and the cache

- **Only a collaborator can be "Solo alcuni".** `AddMemberAsync`, the invitation (`CreateAsync`) and the endpoint refuse
  `Selected` for any other role with 422 `org_member_scope_not_supported`. An invitation sent before the rule is still accepted:
  the person gets `All` (as its role reaches the whole org).
- **The grants end with the role that made them possible.** `ChangeRoleAsync` to any role but the collaborator, `RemoveAsync`
  and `AbandonEmptyOrgAsync` delete the rows and set the scope back to `All`; a person made a collaborator again reaches
  everything, the old grants do not come back to life. A deactivation keeps them: the reactivation gives everything back.
- **A new property is not given to anybody.** A collaborator "Solo alcuni" does not see the properties created after its list
  was set until the owner adds them; the creator of a property reaches it by being an owner, an administrator or a manager.
- **Locks and transaction.** `SetAsync` takes the org people lock (`OrgMembershipService.OrgLock`, the one the role changes take),
  reads the actor and the member under it, applies the rules on the roles as they are at that moment and writes the scope and the
  rows in one `SaveChanges`. Two writers on the same member leave one of the two sets whole (`HostScopePostgresTests`).
- **Cache.** After the commit the writer calls `IUserAuthorizationCache.Invalidate(userId)`: the instance that served the write
  sees the new scope and grants on the next request; **the other API instances within `Authorization:UserCacheSeconds` (60 s)**.
  What may be late on them is the decision (is the member limited at all) and the single-resource checks; the lists read the
  grants in SQL. Consequence to know: when the owner *narrows* a collaborator from "all" to "some", an instance that still holds the
  old snapshot treats it as org-wide for up to a minute. Deactivating a member is not affected: that is read from the database
  on every request (`member_inactive`, section 5).

## 24. Who is told

When something happens on a property (a new booking, a failed deferred charge, an update of a supplier), the people told are
the **member in charge of the property and the administrators of the org** (the owner and the `Admin`s), plus, for the emails, the
contact address of the org as before. While nobody is named, **the creator of the property stands in** (a manager who added a
property keeps hearing about it until someone is put in charge). A deactivated member, an inactive account and a user of another
org are never told. One rule for the push (`PushDeliveryJob`) and for the emails (`BookingNotifier`): `HostNotificationAudience`.
The old rule read `User.Role` (`Admin` or `PropertyManager`), which says nothing about the team: a platform admin was in every
org, a property manager of the team in none. The tests are `PushDeliveryJobTests` and `BookingNotifierHostAudienceTests`.

What changes for an owner who never used the team: it is an `Owner` member (the AM-01 backfill), so it is told at the address of
its own account as well as at the contact address of the org (one message when they are the same address).

## 25. The holder of the org: RLI and IMU (stake S5)

The delega for the RLI filing and the communication of the canone concordato to the Comune are acts of the **landlord**. They used
to be allowed to `Property.OwnerId == caller`, i.e. to whoever created the property; with a team a property manager creates
properties too. They now require the **holder of the org**: an active `Owner` or `Admin` of the org of the lease
(`IOrgHolderService`, read from the database on every call, never from the authorization cache). The creator of the property is
the holder only for an account in **no** org team, as it always was. `RliRegistrationService.SubmitToProviderAsync` answers
`UnauthorizedAccessException` (403) otherwise; `ComuneImuNotificationService.MarkSentAsync` answers as for a lease that is not the caller (null, 404 at `POST .../canone-concordato/imu-notification/mark-sent`). Tests: `OrgHolderServiceTests`,
`RliRegistrationHolderTests`, `ComuneImuNotificationServiceTests`.

## 26. Deploy, flag and rollback of AM-03

**Configuration.** Nothing is required. `Authorization__UserCacheSeconds` (60) is the freshness of the scope on the other
instances (section 23); lower it only if narrowing an access must propagate faster, at the price of one more query per instance and
user every that many seconds.

**The migration** `20261009091620_AddPropertyMemberAccess` is additive: one nullable column, one table with three indexes, seven
rows of `RolePermissions`. No existing row is changed; the previous build ignores the table and the column. It is applied at
startup like the others.

**What the flag does and does not do.** `Features__OrgTeam` gates the endpoints that *write* the team (AM-02 and the two
`.../properties` endpoints of this part). The **enforcement** of the scope is not behind a flag, and does not need one: it changes
what a user sees only for a collaborator "Solo alcuni", and that state can be created only through the gated endpoints (the
invitation or the `PUT`). With the flag off nobody is limited, so nothing changes for any user. The notification audience (section 24)
and the S5 rule (section 25) apply from the deploy.

**Order.** (1) Deploy (flag off). (2) Check with the queries of section 27 that no member has `PropertyScope = 2` (there should be none).
(3) AM-04 (the screens) and then `Features__OrgTeam=true`, as in section 18.

| Level | How | Effect |
|---|---|---|
| Stop one person | deactivate the member, or give it `All` | immediate on this instance, within 60 s on the others (`All` only); the deactivation is read from the database on every request |
| Stop the feature | unset `Features__OrgTeam` | the endpoints answer 404. A collaborator already limited **stays limited** (the rows and the scope are in the database): to widen it, set it to `All` first, or change its role |
| Redeploy the previous build | nothing to undo: the table, the column and the permission rows are ignored | the old code reads the roles of the token again, which a collaborator has none of in the host roles: it sees only the properties it created, i.e. none, until the build is replaced or the member is made another role. The finer permissions are unused |
| Undo the migration | `dotnet ef database update 20261008224500_PreferAlloggiatiReceiptDuplicates` (the migration before), with the previous build deployed | drops `PropertyMemberAccesses`, `Properties.ResponsibleUserId` and the seven permission rows; nothing else. The people limited to some properties lose the limit |

**Merge order with the other branches.** AM-02b and PM-01 also add a migration and edit shared files (`PropertiesController`,
`ComplianceWizardService`, `PropertyResponse`); the one that merges later regenerates its migration on the updated snapshot
(`dotnet ef migrations remove`, then `add`), and PM-01 owns the `Legacy*Rows` helpers of the migration tests (the copy here is
byte-identical).

## 27. Operating it

| Situation | What to do |
|---|---|
| "The collaborator sees nothing" | It is "Solo alcuni" with no property given (`propertyScope: Selected`, every `granted: false`): give it properties with `PUT .../properties`, or set `All`. A property added after the list was set is not in it. |
| "The collaborator still sees a property I took away" | Within 60 s on the other API instances (section 23); the instance that served the change shows it at once. Wait a minute, or deactivate the member to stop it now (deactivation is read from the database on every request). |
| 422 `org_member_scope_not_supported` | "Solo alcuni" is for collaborators. For another role the person reaches every property of the org; change the role first if it must be limited. |
| 403 `org_owner_required` on `.../properties` | The target is an administrator: only the owner touches it (the access of an administrator cannot be limited anyway). |
| A manager cannot give the RLI delega | Expected since AM-03 (stake S5): only the owner or an administrator of the org does. |
| Nobody is told about a property | The person in charge left or was deactivated and the administrators are the only ones left; put somebody in charge (`PUT /api/properties/{id}/responsible`). |

Read-only queries for support (never write by hand: the service keeps the scope and the rows together):

```sql
-- members limited to some properties, with how many they were given
SELECT m."UserId", m."Status", count(a."Id") AS properties
FROM "OrgMembers" m
LEFT JOIN "PropertyMemberAccesses" a ON a."UserId" = m."UserId" AND a."OrgId" = m."OrgId"
WHERE m."PropertyScope" = 2
GROUP BY m."UserId", m."Status";

-- grants of a member that is not (or no longer) limited: should be none
SELECT a."UserId", a."PropertyId"
FROM "PropertyMemberAccesses" a
JOIN "OrgMembers" m ON m."UserId" = a."UserId"
WHERE m."PropertyScope" <> 2 OR m."Role" <> 4;

-- people with access to one property: members who reach everything + collaborators given it
SELECT
  (SELECT count(*) FROM "OrgMembers"
     WHERE "OrgId" = '<org id>' AND "Status" = 1 AND ("Role" <> 4 OR "PropertyScope" = 1)) AS reach_everything,
  (SELECT count(*) FROM "PropertyMemberAccesses" WHERE "PropertyId" = '<property id>') AS given_it;
```

## 28. Tests of AM-03

Unit and in-memory (they run everywhere): `HostScopeResolverTests` (role by scope by status, the single check, the cache),
`HostScopeListsTests` (the 13 filters and the lists added with the scope, a collaborator who sees only its own, the org-wide scope
and the other org, shared with PostgreSQL through `HostScopePoints`), `HostScopeSqlShapeTests` (the SQL on the Npgsql provider:
one `EXISTS`, parameterised, no id list, no second global filter), `HostScopeNpgsqlTranslationTests` (every list with the three
kinds of scope translated by Npgsql, with no server), `HostScopeArchitectureTests` (nobody builds the filter by hand or reads the
token again), `OrgRoleActionMatrixTests` (46 actions by 5 roles), `OrgRoleCatalogTests`, `OrgPropertyAccessServiceTests`,
`OrgMembershipPropertyScopeTests`, `OrgHolderServiceTests`, `RliRegistrationHolderTests`, `BookingNotifierHostAudienceTests`,
`PushDeliveryJobTests`, `ComplianceWizardServiceTests` (the cockpit), `TenantQueryFilterArchitectureTests` (`PropertyMemberAccess`
is tenant-owned), `OrgTeamLocalizationTests`, `AuthorizationAttributeTests`.
HTTP (in-memory locally, PostgreSQL in CI): `OrgPropertyScopeHttpIntegrationTests` (a collaborator that exists only in the database,
limited by the owner through the real endpoints, sees only its own on the lists and the single resources, the change is seen on the next
request, every refusal with its code, the person in charge).
`[PostgresFact]` (CI only): `HostScopePostgresTests` (every point on real SQL with the foreign keys and unique indexes enforced, the
number of commands does not grow with the number of properties, cascade and set null, two writers on one member, the snapshot),
`AddPropertyMemberAccessMigrationPostgresTests` (the permissions added and nothing else, the table, the indexes, the delete rules, `Down`).

## Decisions and follow-ups of AM-03

- **"Solo alcuni" only for the collaborator** (an administrator or a manager limited to some properties would be a role that is not
  the one the permissions say). Refused with 422; a stored `Selected` on another role is ignored.
- **The collaborator has no `alloggiati.submit`** (section 21): to confirm with the product owner.
- **A new property is not given to a limited collaborator**; there is no "all future properties" for "Solo alcuni" (that is `All`).
- **The fiscal area needs `payment.read`** as well as `property.read`.
- **FU1.** An invitation carries only the scope, not the properties: a limited collaborator joins seeing nothing until the owner gives
  it properties. The AM-04 screen can do it in the same step with the member id of the acceptance.
- **FU2.** The guests of the org are listed to every member with `guest.read`: a limited collaborator still lists the guests of the
  properties it does not reach (the guest list has no property). Narrowing it needs a rule on the guest (guests of a stay of a reachable
  property).
- **FU3.** The contact shown to a supplier is still the creator of the property and the host email of the service request is still
  the contact of the org (section 24 covers the push and the booking emails).
- **FU4.** The activity log of the grants (who gave what to whom) is AM-02b's; `PropertyMemberAccesses.CreatedByUserId` and `CreatedAt`
  are the raw material.
