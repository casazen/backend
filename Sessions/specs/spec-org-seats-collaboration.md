# Spec — Org Seats & Collaboration (US-013)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

> **Revision AM-01 (2026-10-08) — read this before the ACs.** The spec predates the Resend email stack, the plan
> entitlements and the context RBAC as they are today; AM-01 (wave "redesign", `docs/runbooks/org-team.md`) builds the
> membership model and this text is corrected to match. What changed:
>
> | Was | Is |
> |---|---|
> | AC12: `OrgId` added to `UserContextMembership` | **`OrgMember`** (table `OrgMembers`, `ITenantOwned`, `UserId` unique, `OrgRole` Owner/Admin/PropertyManager/Collaborator/Accountant, Status, `PropertyScope`) is the source of truth of "who belongs to which org"; `UserContextMembership` stays the **projection of the permissions**, written in the same transaction by one service (`IOrgMembershipService`) with a reconcile command. **No `OrgId` on the membership: the org is `User.OrgId`.** |
> | AC8: `org.members.read/invite/manage` under the `admin` context (`RequireContext:admin:org.members.*`) | The **`account`** context ("Amministrazione" of the customer: `org_owner`, `org_admin`, `org_accountant`), permission `org.members.manage` (plus `org.billing.manage/read`, `org.settings.manage`, `org.suppliers.manage`, `org.activity.read`). `admin` stays the console of CasaZen staff (D1). |
> | AC3: `SeatLimit` from the entitlement | The field is **`MaxSeats`** (`Entitlement:Tiers:{Tier}:MaxSeats`; `SeatLimit` does not exist): Starter 2, Pro 10, Scale unlimited. Seats = active members + pending unexpired invitations; creation and acceptance under an advisory lock above 1_206; with a subscription not in good standing the effective tier drops to Starter (members stay, new invitations are blocked). |
> | AC5, Dependencies: `SendGridService` | `IEmailQueue` (**Resend**); the link stays in the arguments of the Hangfire job, mitigated by single use and the verified email. |
> | AC7: `GET /api/invitations/validate?token=…` | **`POST /api/invitations/lookup`** with the token **in the body** (a token in a query string ends up in logs and history), like `POST` accept. |
> | AC2: `HMACSHA256` hash | SHA-256 of a 256-bit random token, generalizing `SupplierInviteTokens` (an HMAC adds no security at 256 bits and forces a key rotation). |
> | AC13, AC16, AC17: `/settings/team` | `/app/account/people`; visible to who holds `org.members.manage` in the `account` context. |
>
> Division of the work: **AM-01 (done here)** the model, the roles, the `account` context, the backfill of the owners, the
> veto of the JWT and the immediate deactivation (`member_inactive`); **AM-02** invitations, seats, acceptance and the
> change of org; **AM-03** the scope per property and the fine permissions; **AM-04** the screens. No custom roles and no
> transfer of the ownership in this version (D13, D15): only the owner creates an Admin, nobody assigns Owner, the last
> owner stays (409 `org_last_owner`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Enable PM teams and agencies to **invite teammates** and assign them **seat-scoped roles**,
so multiple people collaborate inside one `Org` under least-privilege RBAC — without sharing
credentials.

This spec **extends the existing context-RBAC subsystem** — `AppContext`,
`UserContextMembership`, `Role`, `RolePermission`, `ContextAuthorizationService`, and the
`RequireContext:{context}:{permission}` policy convention registered in
`ServiceCollectionExtensions.cs`. It **does NOT rebuild RBAC**: invitation acceptance creates an
`OrgMember` and, through `IOrgMembershipService` in the same transaction, the `UserContextMembership`
rows (bound to existing `Role` rows) that project its role into permissions.
Since AM-01 the **org membership model exists** (`OrgMember`, the org roles, the `account` context);
there is still **no invitation/seats mechanism** — that is the gap this spec closes (AM-02).

The `Org` tenant key and plan entitlement (including seat limits) come from Phase 1's
`spec-tenant-boundary`; every new table here carries `OrgId` from creation (RF1).

User story reference: **US-013** (Phase 2 — Operations AI Copilot)
Stage of entry: **Stage 01 Planning** (create the issue before design)

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As an `Org` owner/admin (a PM team or agency), I want to invite teammates by email and grant
them a specific org role (Admin, Property manager, Collaborator, Accountant) in the areas I choose
(`short-rent` / `long-rent`; the `account` administration for administrators and accountants), so my
team operates collaboratively under least-privilege RBAC.

As an invited teammate, I want to accept a secure, single-use, expiring invitation link and be
granted exactly the seat-scoped access I was offered — no more, no less.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: New entity `OrgInvitation` (carries `OrgId` from creation — RF1):
  `{ Id, OrgId (FK), Email (normalized), Role (OrgRole), Areas (short-rent / long-rent), PropertyScope,
  TokenHash, Status (enum: Pending|Accepted|Revoked|Expired), ExpiresAt, ReminderSentAt?,
  InvitedByUserId, AcceptedByUserId?, AcceptedAt?, CreatedAt, UpdatedAt }`, with a partial unique
  index on `(OrgId, Email)` where `Status = Pending`. (AM-01 corrected: the role is an `OrgRole`,
  not a `RoleId` the inviter picks; the `Role` rows are seeded by AM-01 and chosen by the service.)

- **AC2 (secure invitation token)**: the invitation token is **cryptographically random
  (≥ 256-bit)**, **single-use**, and **expiring** (default **7 days**). Only a **hash** is
  persisted (`TokenHash`, SHA-256 of the 256-bit token — AM-01 corrected: generalize
  `SupplierInviteTokens`; an HMAC adds no security at 256 bits and forces a key rotation);
  the raw token appears **only** in the emailed link, is compared in **constant time**
  (`CryptographicOperations.FixedTimeEquals`), and is **never logged**. "Resend", "Copy link" and the
  day-3 reminder **rotate** the token (only the hash is stored).

- **AC3 (seat enforcement)**: an `Org` has `MaxSeats` derived from its plan entitlement
  (`Entitlement:Tiers:{Tier}:MaxSeats`: Starter 2, Pro 10, Scale unlimited; AM-01 corrected: there
  is no `SeatLimit`). Active members + outstanding pending, unexpired invitations are counted;
  when seats are exhausted, invitation creation and acceptance are blocked with **409 Conflict**
  (Italian message) — no membership is created beyond the seat limit. Creation and acceptance run
  under an advisory lock (above 1_206, like `CreatePropertyWithinLimitAsync`). With a subscription
  not in good standing the effective tier drops to Starter: the members stay, new invitations are
  blocked.

- **AC4 (reuse membership model — do NOT rebuild RBAC)**: accepting an invitation creates an
  `OrgMember` (`UserId` unique: one org per user) and, in the same transaction, the
  `UserContextMembership { UserId, ContextKey, RoleId }` rows that project its role, both written by
  `IOrgMembershipService` (AM-01). **No `OrgId` on the membership: the org is `User.OrgId`.** No
  parallel permission system is introduced; permissions continue to resolve through
  `ContextAuthorizationService` / `RolePermission`.

- **AC5**: `POST /api/orgs/{orgId}/invitations` — create an invitation and send the email via
  `IEmailQueue` (Resend, not SendGrid; the link stays in the arguments of the Hangfire job, mitigated
  by single use and the verified email). Body: `{ email, role, areas, propertyScope }`. Requires
  `org.members.manage` in the `account` context (AC8). Returns the created invitation (without the
  raw token).

- **AC6**: `GET /api/orgs/{orgId}/invitations` (list, admin-only) and
  `DELETE /api/orgs/{orgId}/invitations/{id}` (revoke → `Status=Revoked`, frees the reserved
  seat).

- **AC7**: Acceptance flow:
  - `POST /api/invitations/lookup` body `{ token }` — pre-acceptance preview returning `{ orgName,
    role, areas, expiresAt }` for an unexpired, unused token (no membership change). The token goes
    **in the body**, never in a query string (AM-01 corrected: it was `GET .../validate?token=…`).
  - `POST /api/invitations/accept` (authenticated) body `{ token }` — validates the token
    (unexpired, `Pending`, hash match), creates the `OrgMember` and its projection through
    `IOrgMembershipService.AddMemberAsync`, sets `Status=Accepted` + `AcceptedByUserId`/`AcceptedAt`.
    A used/expired token returns **410 Gone**.

- **AC8 (least-privilege via existing convention)**: the permission `org.members.manage` (one
  permission: invite, change, deactivate, remove) belongs to the **`account`** context
  ("Amministrazione" of the customer, seeded by AM-01 together with `org.billing.manage/read`,
  `org.settings.manage`, `org.suppliers.manage`, `org.activity.read`) and gives the policy
  `RequireContext:account:org.members.manage`, registered by the task that first uses it
  (`RegisterContextPolicies`, `ServiceCollectionExtensions.cs`). AM-01 corrected: it was
  `org.members.read/invite/manage` under the `admin` context, which is the console of CasaZen staff
  (D1) and stays so. Only the owner and the administrators hold it.

- **AC9 (no privilege escalation)**: an inviter **cannot grant a role whose permission set
  exceeds the inviter's own**; the invitation names an `OrgRole` and the service picks the seeded
  `Role` rows (`HasData`, AM-01) of the areas — there are no custom roles (D13). Only the owner
  creates an Admin and **nobody assigns Owner** (422 `org_owner_not_assignable`, D15: no transfer
  of the ownership in this version). Attempts to over-grant return **403**.

- **AC10 (member removal)**: `DELETE /api/orgs/{orgId}/members/{userId}` removes the `OrgMember`
  and every membership the org gave (`IOrgMembershipService.RemoveAsync`; frees a seat). The
  **owner cannot be removed, deactivated or given another role** (409 `org_last_owner`). A member
  can also be deactivated and reactivated: from the next request a deactivated member gets 403
  `member_inactive` (AM-01).

- **AC11 (tenant isolation)**: all invitation/member operations are scoped to `OrgId`;
  cross-`Org` access returns **403**, consistent with the tenant boundary.

- **AC12**: Migration `AddOrgMembership` (AM-01, done) creates `OrgMembers` and seeds the `account`
  context and the new `Role`/`RolePermission` rows via `HasData`; migration `AddOrgInvitations`
  (AM-02) creates `OrgInvitations`. **`UserContextMembership` gets no `OrgId`**: the org is
  `User.OrgId` and the source of truth of the membership is `OrgMember`. Tables carry `OrgId` from
  creation and the changes **rebase onto `AppDbContextModelSnapshot.cs`** (never hand-merge, RF3);
  migrations only via `dotnet ef`.

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC13**: `team-page.tsx` at `/app/account/people` (AM-01 corrected: not `/settings/team`) —
  member list (name, email, areas, role, status) with a **seat-usage indicator** (e.g. *"7 / 10
  posti"*) and an "Invita membro" button.

- **AC14**: Invite dialog — email + role selector + area selector (short-rent / long-rent), with
  validation and explicit **seat-exhausted** and **success** states.

- **AC15**: Pending-invitations list with **Revoca** (revoke) and **Invia di nuovo** (resend).

- **AC16**: Invitation acceptance page at `/invite/accept?token=…` — calls `lookup` (POST, token in the body) to show
  `Org` + role, an **Accetta** CTA → `accept` → redirect to the granted context's home;
  expired/used token shows an explicit error state.

- **AC17**: `<ProtectedRoute>` on team-management routes; team management is visible **only** to
  users holding `org.members.manage` in the `account` context (permission-gated). All end-user strings in Italian
  ("Team", "Invita membro", "Revoca", "Posti", "Invito scaduto").

- **AC18**: TanStack Query hooks, API client, and types for invitations/members.

---


## UX / UI Quality



**Required** (Frontend ACs present). Testable bar for Stage 03.



| Criterion | Required | How to verify |

|---|---|---|

| Primary path clear | User completes happy path without guessing | L3 scripted flow below |

| Language | End-user strings Italian | L2/L3 assert Italian primary labels |

| Empty state | No blank dead-end when data length = 0 | L2 empty fixture |

| Error state | 4xx/5xx as human Italian message | L2/L3 forced error |

| Destructive / legal copy | Confirmations/disclaimers as in ACs | Assert documented phrases |



**Happy-path script:**



1. Enter the primary route for `org-seats-collaboration`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | New entity `OrgInvitation` (carries `OrgId` from creation — RF1): | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | `POST /api/orgs/{orgId}/invitations` — create an invitation and send the email via | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | `GET /api/orgs/{orgId}/invitations` (list, admin-only) and | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | Acceptance flow: | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC9 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC10 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC11 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC12 | L1 | Migration `AddOrgMembership` (AM-01) creates `OrgMembers`, `AddOrgInvitations` (AM-02) creates `OrgInvitations`; no `OrgId` on the membership | Outcome not met; wrong status; silent no-op |
| AC13 | L2 + L3 | `team-page.tsx` at `/app/account/people` — member list (name, email, areas, role, | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC14 | L2 + L3 | Invite dialog — email + role selector + area selector, with validation and | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC15 | L2 + L3 | Pending-invitations list with **Revoca** (revoke) and **Invia di nuovo** (resend). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC16 | L2 + L3 | Invitation acceptance page at `/invite/accept?token=…` — calls `lookup` to show | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC17 | L2 + L3 | `<ProtectedRoute>` on team-management routes; team management is visible **only** to | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC18 | L2 + L3 | TanStack Query hooks, API client, and types for invitations/members. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — Files to create/modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `Casazen.Core/Entities/OrgInvitation.cs` | Create — incl. `OrgId`, `TokenHash`, `Status`, `ExpiresAt`, `RoleId` |
| `Casazen.Core/Entities/Enums/InvitationStatus.cs` | Create — `Pending/Accepted/Revoked/Expired` |
| `Casazen.Core/Entities/OrgMember.cs`, `Enums/OrgRole.cs`, `OrgMemberStatus.cs`, `PropertyScope.cs` | **AM-01, done** — the org membership (source of truth), `ITenantOwned` |
| `Casazen.Core/Entities/UserContextMembership.cs` | **No change** (AM-01 corrected: no `OrgId`, the org is `User.OrgId`); it stays the projection of the permissions — **extend, do not rebuild** |
| `Casazen.Core/Repositories/IOrgInvitationRepository.cs` | Create |
| `Casazen.Infrastructure/Repositories/OrgInvitationRepository.cs` | Create — EF Core, `OrgId`-filtered |
| `Casazen.Core/Services/IOrgMembershipService.cs` | **AM-01, done** — add/change role/deactivate/reactivate/remove/reconcile; AM-02 adds `IOrgInvitationService` (invite/accept/revoke + seat checks) |
| `Casazen.Infrastructure/Services/OrgMembershipService.cs` | **AM-01, done** — member + projection in one transaction. Token generation/hash (SHA-256) and seat enforcement are AM-02's (`OrgInvitationService`) |
| `Casazen.Infrastructure/Services/ContextAuthorizationService.cs` | **AM-01, done** — the `account` context, the veto of the token for org members, the deactivated member (no `OrgId` lookup: the org is `User.OrgId`) |
| `Casazen.Web/Controllers/OrgInvitationsController.cs` | Create — create/list/revoke/lookup/accept |
| `Casazen.Web/Controllers/OrgMembersController.cs` | Create — list/remove members |
| `Casazen.Web/DTOs/Org/CreateInvitationRequest.cs` | Create |
| `Casazen.Web/DTOs/Org/InvitationDto.cs` | Create — never includes the raw token |
| `Casazen.Web/DTOs/Org/OrgMemberDto.cs` | Create |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — register service/repo; the policy `RequireContext:account:org.members.manage` goes in `RegisterContextPolicies` where it is first used (AM-02: an unused registered policy fails the architecture test) |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | AM-01 (done): `DbSet<OrgMember>` and the seed of the `account` context and roles (`HasData`). AM-02: `DbSet<OrgInvitation>`, config, indexes (`OrgId`, partial unique `(OrgId, Email)` where `Status = Pending`) |
| `Casazen.Infrastructure/Email/EmailQueue.cs` (`IEmailQueue`) + `Email.Templates.EmailTemplates` (Resend) | Modify — invitation email template, queued with `IEmailQueue` (raw token in the link only); not `SendGridService` |
| `Casazen.Infrastructure/Migrations/` | AM-01 `AddOrgMembership` (done); AM-02 `AddOrgInvitations` (rebase `AppDbContextModelSnapshot.cs`, RF3) |

### Frontend — Files to create/modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `src/features/team/team-page.tsx` | Create — member list + seat usage |
| `src/features/team/components/member-list.tsx` | Create |
| `src/features/team/components/invite-member-dialog.tsx` | Create — email/context/role + seat-exhausted state |
| `src/features/team/components/pending-invitations.tsx` | Create — revoke/resend |
| `src/features/team/invitation-accept-page.tsx` | Create — lookup → accept |
| `src/queries/use-team.ts` | Create — TanStack Query hooks |
| `src/api/team.api.ts` | Create — team/invitations API client |
| `src/types/team.types.ts` | Create — invitation/member/seat types |
| `src/routes/index.tsx` | Modify — add `/app/account/people` (protected) + `/invite/accept` |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Least-privilege RBAC (council wording)**: seat roles are bounded by existing
  `Role`/`RolePermission` rows; an inviter cannot escalate beyond their own permissions (AC9);
  only `Org` admins invite/manage, enforced through the `RequireContext:{context}:{permission}`
  convention (AC8). RBAC is **extended, not rebuilt**.
- **Secure invitation tokens**: ≥ 256-bit random, **single-use**, **expiring** (7-day default),
  stored **only as a SHA-256 hash**, compared in constant time
  (`CryptographicOperations.FixedTimeEquals`), raw token only in the email link, **never logged**
  (AC2).
- **GDPR**: an invitee's email is PII — lawful basis (legitimate interest / contract), data
  minimization, and revoke/erasure of pending invitations on request.
- **Tenant isolation (RF1)**: `OrgMember` and `OrgInvitation` carry `OrgId` (`ITenantOwned`);
  `UserContextMembership` does not (the org is `User.OrgId`); all operations are `OrgId`-scoped
  (cross-`Org` = 403) and honor plan-entitlement seat limits (`MaxSeats`).

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**:
  - `spec-tenant-boundary` (Phase 1) — `Org`/`OrgId` + plan entitlement (`MaxSeats`).
  - Context-RBAC — `AppContext`, `UserContextMembership`, `Role`, `RolePermission`,
    `ContextAuthorizationService`, and the `RequireContext:{context}:{permission}` convention in
    `ServiceCollectionExtensions.cs` (extended here, not replaced).
  - `IEmailQueue` (Resend) — invitation emails.
- **Blocks**:
  - Phase 2 exit criterion — "team members invited with seat-scoped RBAC".
- **Related**:
  - `spec-saas-billing` — seat counts feed seat-based subscription pricing/entitlement.
  - `spec-onboarding-plg` / `spec-role-onboarding` — first-user signup + role-assignment patterns
    that invitation acceptance complements.

## Test expectations (process contract)



| Layer | Allowed | Forbidden as sole proof |

|---|---|---|

| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |

| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |

| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |



Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

## Regulatory / Legal Gates

- None

## Out of Scope

- See Acceptance Criteria non-goals / PLANNING freeze list

## Open Questions

- None (or list with owner/date before Stage 03)
