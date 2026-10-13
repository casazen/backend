# Spec — Self-Serve Onboarding & Activation (PLG) (US-006)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

`spec-role-onboarding` covers the first-run **role choice** (short-term / long-term / both → Auth0
roles) for an already-signed-in user. This spec extends it into a full **product-led-growth (PLG)**
flow: a self-serve signup that **provisions a tenant `Org`** (`spec-tenant-boundary`), captures the
**legal consents** required to launch (GDPR consent + ToS + **DPA** + **subprocessor list**), and then
**drives the user to activation** — the two milestones that prove value: **publish a branded booking
site** and **take a first booking**.

Activation is the GTM north-star ("time-to-first-direct-booking"), so the flow includes an activation
checklist that tracks these milestones across the new public surfaces.

Phase: **1 (MVP Sellable — onboarding/PLG)** · User story: **US-006**
Stage of entry: **Stage 01 Planning** (new macro-spec)

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a new operator, I want to sign up myself, accept the required legal terms, choose how I use CasaZen,
get my own organization, and be guided step-by-step to publish my booking site and take my first
booking — so that I reach value without sales hand-holding.

As CasaZen, I want every new account to have an `Org`, recorded consents (GDPR/ToS/DPA + subprocessor
acknowledgement) with versions and timestamps, and a measurable activation funnel.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: `POST /api/users/onboarding` (extends `spec-role-onboarding`) provisions an **`Org`** for the new user if none exists (`spec-tenant-boundary`: create `Org`, set `User.OrgId`, **`PlanTier` from onboarding step 2** defaulting to `Starter`). Idempotent: a user who already has an `Org` is not given a second one and the existing plan is **not** overwritten on re-run.

- **AC2**: The request body adds a `consents` block: `{ tosAccepted: true, tosVersion, privacyAccepted: true, privacyVersion, dpaAccepted: true, dpaVersion, subprocessorsAcknowledged: true, subprocessorsVersion, marketingOptIn? }`. The endpoint returns `400` unless `tosAccepted && privacyAccepted && dpaAccepted && subprocessorsAcknowledged` are all true.

- **AC3**: Consents are persisted to a new `ConsentRecord` entity `{ Id, UserId, OrgId, type (Tos|Privacy|Dpa|Subprocessors|Marketing), version, acceptedAt, ipAddress }` — one row per consent type, append-only (re-acceptance on version bump creates a new row; history is retained).

- **AC4**: `GET /api/legal/subprocessors` (`[AllowAnonymous]`) returns the current **subprocessor list** with `version`: at minimum **Supabase (EU — data hosting)**, **Auth0 (authentication)**, **Stripe (payments)**, **SendGrid (email)**, each with purpose + region. `GET /api/legal/dpa` and `/api/legal/tos` return the current document version metadata.

- **AC5**: `GET /api/onboarding/status` (authenticated) returns the activation checklist for the caller's `Org`: `{ roleChosen, orgProvisioned, consentsAccepted, propertyCreated, sitePublished, firstBookingTaken }` (each bool) + an overall `activated` flag.

- **AC6**: Activation milestones are derived from real state — `propertyCreated` from the org having ≥1 `Property`; `sitePublished` when the org's branded site is enabled (`Org.IsActive` + ≥1 active property, per `spec-branded-booking-site`); `firstBookingTaken` when the org has ≥1 `Confirmed` direct `Booking` (per `spec-direct-checkout`). No manual flags that can drift from reality.

- **AC7**: `PUT /api/users/onboarding` (idempotent re-run from settings, per `spec-role-onboarding`) keeps the existing `Org` and consents; only role/`rentalType` changes are applied. Admins bypass the onboarding guard (`spec-role-onboarding` AC9).

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC8**: The onboarding flow (extending `src/features/onboarding/onboarding-page.tsx`) becomes a short wizard: **(1) role choice** (existing cards) → **(2) legal consents** → completion. Step 2 shows checkboxes for **ToS**, **Privacy Policy**, **DPA**, and **subprocessor list** (with a link/expander listing Supabase EU, Auth0, Stripe, SendGrid), plus optional marketing opt-in. "Continua" is disabled until the four required boxes are checked.

- **AC9**: On completion the FE calls the enhanced `POST /api/users/onboarding` with `rentalType` + `consents`, then refreshes the Auth0 token (`getAccessTokenSilently({ ignoreCache: true })`) and `useUserStore`/`useCurrentUser` (so the new `org` + roles are present), then routes to the dashboard.

- **AC10**: An **activation checklist** widget (`src/features/onboarding/components/activation-checklist.tsx`) on the dashboard renders `GET /api/onboarding/status`: each step shows done/todo with a deep link — "Crea proprietà" → property create, "Pubblica il sito" → branded-site settings, "Prima prenotazione" → share the public booking URL. The widget hides once `activated` is true.

- **AC11**: A legal subprocessor view (`src/features/legal/subprocessors-page.tsx`, public) renders `GET /api/legal/subprocessors`; linked from the onboarding consents step and the public site footer. End-user strings in Italian (e.g. "Responsabili del trattamento", "Informativa sulla privacy").

- **AC12 (Regression)**: The existing `OnboardingGuard` ordering from `spec-role-onboarding` is preserved (guard after `<ProtectedRoute>`, before `AppLayerProvider`; `/onboarding` outside the guard to avoid redirect loops); demo mode (`VITE_DEMO_MODE`) still renders the flow without Auth0.

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



1. Enter the primary route for `onboarding-plg`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | `POST /api/users/onboarding` (extends `spec-role-onboarding`) provisions an **`Org`** for the new user if none exists (`spec-tenant-bound... | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | The request body adds a `consents` block: `{ tosAccepted: true, tosVersion, privacyAccepted: true, privacyVersion, dpaAccepted: true, dpa... | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | Consents are persisted to a new `ConsentRecord` entity `{ Id, UserId, OrgId, type (Tos/Privacy/Dpa/Subprocessors/Marketing), version, acc... | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | `GET /api/legal/subprocessors` (`[AllowAnonymous]`) returns the current **subprocessor list** with `version`: at minimum **Supabase (EU —... | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | `GET /api/onboarding/status` (authenticated) returns the activation checklist for the caller's `Org`: `{ roleChosen, orgProvisioned, cons... | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | Activation milestones are derived from real state — `propertyCreated` from the org having ≥1 `Property`; `sitePublished` when the org's b... | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | `PUT /api/users/onboarding` (idempotent re-run from settings, per `spec-role-onboarding`) keeps the existing `Org` and consents; only rol... | Outcome not met; wrong status; silent no-op |
| AC8 | L2 + L3 | The onboarding flow (extending `src/features/onboarding/onboarding-page.tsx`) becomes a short wizard: **(1) role choice** (existing cards... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC9 | L1 + L2 + L3 | On completion the FE calls the enhanced `POST /api/users/onboarding` with `rentalType` + `consents`, then refreshes the Auth0 token (`get... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L1 + L2 + L3 | An **activation checklist** widget (`src/features/onboarding/components/activation-checklist.tsx`) on the dashboard renders `GET /api/onb... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC11 | L1 + L2 + L3 | A legal subprocessor view (`src/features/legal/subprocessors-page.tsx`, public) renders `GET /api/legal/subprocessors`; linked from the o... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `Casazen.Web/Controllers/UsersController.cs` | Modify — enhance `POST/PUT /api/users/onboarding`: provision `Org` + persist consents (AC1–AC3, AC7) |
| `Casazen.Web/Controllers/OnboardingController.cs` | Create — `GET /api/onboarding/status` activation checklist (AC5–AC6) |
| `Casazen.Web/Controllers/LegalController.cs` | Create — `[AllowAnonymous]` `GET /api/legal/subprocessors|dpa|tos` (AC4) |
| `Casazen.Core/Entities/ConsentRecord.cs` | Create — append-only consent log (AC3) |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — `DbSet<ConsentRecord>` + indexes (`UserId`, `OrgId`, `type`) |
| `Casazen.Infrastructure/Migrations/<ts>_AddConsentRecords.cs` | Create — consent table (rebased on snapshot per RF3) |
| `Casazen.Core/Services/IOnboardingService.cs` + `Infrastructure/Services/OnboardingService.cs` | Create — provision org, record consents, compute activation status |
| `Casazen.Infrastructure/Services/OrgService.cs` | Modify — `EnsureOrgForUserAsync` (reuse from `spec-tenant-boundary`) |
| `Casazen.Core/Services/IUserService.cs` | Modify — onboarding takes consents payload |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — register `IOnboardingService` |

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `src/features/onboarding/onboarding-page.tsx` | Modify — add legal-consents step to the wizard (AC8) |
| `src/features/onboarding/components/consents-step.tsx` | Create — ToS/Privacy/DPA/subprocessors checkboxes (AC8) |
| `src/features/onboarding/components/activation-checklist.tsx` | Create — dashboard activation widget (AC10) |
| `src/features/legal/subprocessors-page.tsx` | Create — public subprocessor list (AC11) |
| `src/api/users.api.ts` | Modify — `postOnboarding(rentalType, consents)` |
| `src/api/legal.api.ts` | Create — `getSubprocessors()`, `getDpa()`, `getTos()` |
| `src/queries/use-onboarding.ts` | Create — `useOnboardingStatus()`, `useCompleteOnboarding()` |
| `src/types/onboarding.types.ts` | Create — `ConsentsPayload`, `OnboardingStatus`, `Subprocessor` |
| `src/features/dashboard/dashboard-page.tsx` | Modify — mount activation checklist widget |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **GDPR consent + ToS**: required consents (Privacy + ToS) captured at signup with **version + timestamp + IP**, append-only in `ConsentRecord` (AC2–AC3); demonstrable consent per GDPR Art. 7.
- **DPA (data processing agreement)**: the operator (`Org` = data controller) accepts a DPA with CasaZen (processor) at onboarding — the controller/processor delineation from `spec-tenant-boundary` is made contractual here (Legal C5).
- **Subprocessor list**: explicit acknowledgement of **Supabase (EU hosting)**, **Auth0**, **Stripe**, **SendGrid** with purpose/region, exposed publicly and versioned (AC4, AC11); re-acknowledgement required on version bump.
- **Data minimization**: onboarding stores only what is needed (role, org, consent metadata); no guest/tenant PII handled in this flow.

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**: `spec-role-onboarding` (role-choice flow, `OnboardingGuard`, `POST/PUT /api/users/onboarding`, `User.RentalType`); `spec-admin-backend` (Auth0 Management API client / `Auth0ManagementService`, `UsersController`, `/me`); `spec-tenant-boundary` (`Org` provisioning + `User.OrgId`).
- **Requires (activation signals)**: `spec-branded-booking-site` (`sitePublished`) and `spec-direct-checkout` (`firstBookingTaken`) for the checklist to reflect real milestones.
- **Blocks**: PLG GTM motion (self-serve activation funnel) and the Phase 1 exit criterion "an external PM self-onboards".
- **Related**: `spec-saas-billing` (post-activation upgrade prompt links into billing).
- **Does not touch**: `LayerSwitcher`, `AppLayerProvider`, lease subsystem, OTA adapters.

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
