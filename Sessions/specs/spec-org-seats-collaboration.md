# Spec — Org Seats & Collaboration (US-013)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Enable PM teams and agencies to **invite teammates** and assign them **seat-scoped roles**,
so multiple people collaborate inside one `Org` under least-privilege RBAC — without sharing
credentials.

This spec **extends the existing context-RBAC subsystem** — `AppContext`,
`UserContextMembership`, `Role`, `RolePermission`, `ContextAuthorizationService`, and the
`RequireContext:{context}:{permission}` policy convention registered in
`ServiceCollectionExtensions.cs`. It **does NOT rebuild RBAC**: invitation acceptance simply
creates a `UserContextMembership` row bound to an existing `Role` within the `Org`'s context.
There is **no `Org` seats/invitation mechanism today** — that is the gap this spec closes.

The `Org` tenant key and plan entitlement (including seat limits) come from Phase 1's
`spec-tenant-boundary`; every new table here carries `OrgId` from creation (RF1).

User story reference: **US-013** (Phase 2 — Operations AI Copilot)
Stage of entry: **Stage 01 Planning** (create the issue before design)

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As an `Org` owner/admin (a PM team or agency), I want to invite teammates by email and grant
them a specific role within a specific context (`short-rent` / `long-rent` / `admin`), so my
team operates collaboratively under least-privilege RBAC.

As an invited teammate, I want to accept a secure, single-use, expiring invitation link and be
granted exactly the seat-scoped access I was offered — no more, no less.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: New entity `OrgInvitation` (carries `OrgId` from creation — RF1):
  `{ Id, OrgId (FK), Email, ContextKey, RoleId (FK → existing Role), TokenHash, Status (enum:
  Pending|Accepted|Revoked|Expired), ExpiresAt, InvitedByUserId, AcceptedByUserId?, AcceptedAt?,
  CreatedAt, UpdatedAt }`.

- **AC2 (secure invitation token)**: the invitation token is **cryptographically random
  (≥ 256-bit)**, **single-use**, and **expiring** (default **7 days**). Only a **hash** is
  persisted (`TokenHash`, via `HMACSHA256` — same primitive the `esign` webhook already uses);
  the raw token appears **only** in the emailed link, is compared in **constant time**
  (`CryptographicOperations.FixedTimeEquals`), and is **never logged**.

- **AC3 (seat enforcement)**: an `Org` has a `SeatLimit` derived from its plan entitlement
  (`spec-tenant-boundary`). Active members + outstanding pending invitations are counted; when
  seats are exhausted, invitation creation and acceptance are blocked with **409 Conflict**
  (Italian message) — no membership is created beyond the seat limit.

- **AC4 (reuse membership model — do NOT rebuild RBAC)**: accepting an invitation creates a
  `UserContextMembership { UserId, ContextKey, RoleId, OrgId }` for the invitee. No parallel
  permission system is introduced; permissions continue to resolve through
  `ContextAuthorizationService` / `RolePermission`.

- **AC5**: `POST /api/orgs/{orgId}/invitations` — create an invitation and send the email via
  `SendGridService`. Body: `{ email, contextKey, roleId }`. Requires the `Org`-admin permission
  (AC8). Returns the created invitation (without the raw token).

- **AC6**: `GET /api/orgs/{orgId}/invitations` (list, admin-only) and
  `DELETE /api/orgs/{orgId}/invitations/{id}` (revoke → `Status=Revoked`, frees the reserved
  seat).

- **AC7**: Acceptance flow:
  - `GET /api/invitations/validate?token=…` — pre-acceptance preview returning `{ orgName,
    contextKey, roleKey, expiresAt }` for an unexpired, unused token (no membership change).
  - `POST /api/invitations/accept` (authenticated) body `{ token }` — validates the token
    (unexpired, `Pending`, hash match), creates the `UserContextMembership`, sets
    `Status=Accepted` + `AcceptedByUserId`/`AcceptedAt`. A used/expired token returns **410 Gone**.

- **AC8 (least-privilege via existing convention)**: new permissions `org.members.read`,
  `org.members.invite`, `org.members.manage` are registered in
  `RegisterContextPolicies` (`ServiceCollectionExtensions.cs`) under the `admin` context,
  producing policies `RequireContext:admin:org.members.*`. Only `Org` admins may invite/manage.

- **AC9 (no privilege escalation)**: an inviter **cannot grant a role whose permission set
  exceeds the inviter's own** within that context; `roleId` must reference an existing `Role`
  row for the target `ContextKey` (seeded via the `Role`/`RolePermission` `HasData` pattern).
  Attempts to over-grant return **403**.

- **AC10 (member removal)**: `DELETE /api/orgs/{orgId}/members/{userId}` removes the user's
  `UserContextMembership` for that `Org` (frees a seat). The **last remaining `Org` owner cannot
  be removed** (returns 409).

- **AC11 (tenant isolation)**: all invitation/member operations are scoped to `OrgId`;
  cross-`Org` access returns **403**, consistent with the tenant boundary.

- **AC12**: Migration `AddOrgInvitations` creates `OrgInvitation` and adds `OrgId` to
  `UserContextMembership` (extending the existing entity, not replacing it); tables carry `OrgId`
  from creation and the change **rebases onto `AppDbContextModelSnapshot.cs`** (never hand-merge,
  RF3). Any new seat `Role`/`RolePermission` rows are seeded via `HasData`.

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC13**: `team-page.tsx` at `/settings/team` — member list (name, email, context, role,
  status) with a **seat-usage indicator** (e.g. *"7 / 10 posti"*) and an "Invita membro" button.

- **AC14**: Invite dialog — email + context selector + role selector, with validation and
  explicit **seat-exhausted** and **success** states.

- **AC15**: Pending-invitations list with **Revoca** (revoke) and **Invia di nuovo** (resend).

- **AC16**: Invitation acceptance page at `/invite/accept?token=…` — calls `validate` to show
  `Org` + role, an **Accetta** CTA → `accept` → redirect to the granted context's home;
  expired/used token shows an explicit error state.

- **AC17**: `<ProtectedRoute>` on team-management routes; team management is visible **only** to
  users holding `org.members.manage` (permission-gated). All end-user strings in Italian
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
| AC12 | L1 | Migration `AddOrgInvitations` creates `OrgInvitation` and adds `OrgId` to | Outcome not met; wrong status; silent no-op |
| AC13 | L2 + L3 | `team-page.tsx` at `/settings/team` — member list (name, email, context, role, | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC14 | L2 + L3 | Invite dialog — email + context selector + role selector, with validation and | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC15 | L2 + L3 | Pending-invitations list with **Revoca** (revoke) and **Invia di nuovo** (resend). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC16 | L2 + L3 | Invitation acceptance page at `/invite/accept?token=…` — calls `validate` to show | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
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
| `Casazen.Core/Entities/UserContextMembership.cs` | Modify — add `OrgId` (Org-scoped membership, RF1) — **extend, do not rebuild** |
| `Casazen.Core/Repositories/IOrgInvitationRepository.cs` | Create |
| `Casazen.Infrastructure/Repositories/OrgInvitationRepository.cs` | Create — EF Core, `OrgId`-filtered |
| `Casazen.Core/Services/IOrgMembershipService.cs` | Create — invite/accept/revoke/remove + seat checks |
| `Casazen.Infrastructure/Services/OrgMembershipService.cs` | Create — token gen/hash (`HMACSHA256`), seat enforcement, membership creation |
| `Casazen.Infrastructure/Services/ContextAuthorizationService.cs` | Modify — `OrgId`-aware membership lookup in `GetUserContextsAsync`/`HasPermissionAsync` |
| `Casazen.Web/Controllers/OrgInvitationsController.cs` | Create — create/list/revoke/validate/accept |
| `Casazen.Web/Controllers/OrgMembersController.cs` | Create — list/remove members |
| `Casazen.Web/DTOs/Org/CreateInvitationRequest.cs` | Create |
| `Casazen.Web/DTOs/Org/InvitationDto.cs` | Create — never includes the raw token |
| `Casazen.Web/DTOs/Org/OrgMemberDto.cs` | Create |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — register service/repo; add `org.members.read/invite/manage` in `RegisterContextPolicies` |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — `DbSet<OrgInvitation>`, config, indexes (`OrgId`, unique `(OrgId, Email, Status)`); seed any seat `Role`/`RolePermission` via `HasData` |
| `Casazen.Infrastructure/External/SendGridService.cs` | Modify — send invitation email (raw token in link only) |
| `Casazen.Infrastructure/Migrations/` | Add migration `AddOrgInvitations` (`OrgId` on new + membership tables; rebase `AppDbContextModelSnapshot.cs`, RF3) |

### Frontend — Files to create/modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `src/features/team/team-page.tsx` | Create — member list + seat usage |
| `src/features/team/components/member-list.tsx` | Create |
| `src/features/team/components/invite-member-dialog.tsx` | Create — email/context/role + seat-exhausted state |
| `src/features/team/components/pending-invitations.tsx` | Create — revoke/resend |
| `src/features/team/invitation-accept-page.tsx` | Create — validate → accept |
| `src/queries/use-team.ts` | Create — TanStack Query hooks |
| `src/api/team.api.ts` | Create — team/invitations API client |
| `src/types/team.types.ts` | Create — invitation/member/seat types |
| `src/routes/index.tsx` | Modify — add `/settings/team` (protected) + `/invite/accept` |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Least-privilege RBAC (council wording)**: seat roles are bounded by existing
  `Role`/`RolePermission` rows; an inviter cannot escalate beyond their own permissions (AC9);
  only `Org` admins invite/manage, enforced through the `RequireContext:{context}:{permission}`
  convention (AC8). RBAC is **extended, not rebuilt**.
- **Secure invitation tokens**: ≥ 256-bit random, **single-use**, **expiring** (7-day default),
  stored **only as an `HMACSHA256` hash**, compared in constant time
  (`CryptographicOperations.FixedTimeEquals`), raw token only in the email link, **never logged**
  (AC2).
- **GDPR**: an invitee's email is PII — lawful basis (legitimate interest / contract), data
  minimization, and revoke/erasure of pending invitations on request.
- **Tenant isolation (RF1)**: `OrgInvitation` and `UserContextMembership` carry `OrgId`; all
  operations are `OrgId`-scoped (cross-`Org` = 403) and honor plan-entitlement seat limits.

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**:
  - `spec-tenant-boundary` (Phase 1) — `Org`/`OrgId` + plan entitlement (`SeatLimit`).
  - Context-RBAC — `AppContext`, `UserContextMembership`, `Role`, `RolePermission`,
    `ContextAuthorizationService`, and the `RequireContext:{context}:{permission}` convention in
    `ServiceCollectionExtensions.cs` (extended here, not replaced).
  - `SendGridService` — invitation emails.
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
