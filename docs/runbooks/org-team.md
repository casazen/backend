# Runbook: org membership, org roles and the `account` context

Task AM-01 of the wave spec (org team, step 1: who belongs to which org and as what; decisions D1, D12, D14, D15;
backend only) is described in sections 1 to 9. Task AM-02 (step 2: invitations, members and seats; decisions D13, D14,
D15, D35; backend only) is described in sections 10 to 19. The per-property scope and the fine permissions come with
AM-03, the activity log and the access requests with AM-02b, the screens with AM-04: **nothing here changes what a user
sees today**. The flag `OrgTeam` is off by default and gates every endpoint of AM-02 (section 18).

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
| `AddMemberAsync` | member + the memberships of the role for the chosen areas, all or nothing; sets `User.OrgId` when empty and records the property scope (AM-02) | 422 `org_owner_not_assignable`, `org_member_area_required`; 409 `org_member_already_member`, `org_member_other_org` |
| `ChangeRoleAsync` | re-points the account row and the role key of every rental row, keeping the areas | 422 `org_owner_not_assignable`; 409 `org_last_owner` |
| `DeactivateAsync` / `ReactivateAsync` | status only; idempotent; the account and the memberships stay | 409 `org_last_owner` (deactivating the owner) |
| `RemoveAsync` | deletes the member and every membership the org gave and **unlinks the org** (`User.OrgId`, and the last used context when it was one of the org's; AM-02-FU1); the account stays | 409 `org_last_owner` |
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
administrator: 403 `org_owner_required`.

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
| 9 | Under the locks, in one transaction: the checks 4, 5 and 8 again, then the writes | the same codes |

Checks 4, 5 and 8 first read their data without locks and are made again under the lock: of two requests for the same
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
