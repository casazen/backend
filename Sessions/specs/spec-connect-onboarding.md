# Spec — Stripe Connect Onboarding (Operator / Landlord = Merchant of Record) (US-002 / US-007 enabler)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Both `spec-direct-checkout` (guest payments, operator = MoR) and `spec-ltr-recurring-rent` (tenant rent,
landlord = MoR) require funds to settle to the **operator's / landlord's own Stripe connected account**,
with `Org.StripeConnectedAccountId` (and the landlord equivalent) populated. `spec-tenant-boundary` only
**defines** those fields; nothing yet builds the **onboarding flow** that creates the connected account,
runs Stripe-hosted **KYC/identity verification**, and tracks when the account is actually able to accept
charges. This spec closes that gap.

Without it, the Phase 1 "sellable" exit ("takes a commission-free booking, operator = MoR via Stripe
Connect") and the Phase 1.5 rent exit are **unreachable** — `spec-direct-checkout` AC3 would always hit
its `409 operator not onboarded` path. This was raised as a **blocking** gap by the Devil's Advocate review.

CasaZen uses **Stripe Connect (Express recommended)**: operators onboard via a Stripe-hosted flow; CasaZen
stores only the connected-account id and capability flags and **never holds or settles operator/landlord
funds** (consistent with AD-5 and the C3/C6 merchant-of-record gates).

Phase: **1 (MVP Sellable — payments enabler; also unblocks Phase 1.5 LTR rent)** · Enables **US-002**, **US-007**
Stage of entry: **Stage 01 Planning** (new macro-spec — added in Devil's Advocate consolidation)

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As an operator (or long-term landlord), I want to connect my own Stripe account through a guided,
Stripe-hosted onboarding (identity/KYC) so that guest payments / tenant rent settle directly to me — I am
the merchant of record — and CasaZen never holds my money.

As CasaZen, I want to know reliably whether an `Org`'s connected account can accept charges
(`charges_enabled`) before exposing checkout, so guests/tenants never start a payment that cannot complete.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: `POST /api/connect/account` (Org admin) creates a **Stripe Connect (Express)** account for the caller's `Org` if absent, persists `Org.StripeConnectedAccountId`, and is idempotent (returns the existing account if already created). Capability request: `card_payments` + `transfers`.

- **AC2**: `POST /api/connect/onboarding-link` (Org admin) creates a Stripe **Account Link** (`type = account_onboarding`) with server-supplied `return_url` and `refresh_url`, and returns `{ url }`. The link is short-lived; a fresh one is minted on each request (Stripe Account Links expire).

- **AC3**: `GET /api/connect/status` (Org admin) returns `{ connectedAccountId?, chargesEnabled, payoutsEnabled, detailsSubmitted, requirementsDue[] }` derived from the Stripe account, cached on the `Org` and refreshed on demand / via webhook.

- **AC4 (RF2 — connected-account webhook routing)**: the connected-account **`account.updated`** event (verified with the **Connect** signing secret `Stripe:ConnectWebhookSecret`, routed via the `source = connected` discriminator) updates `Org.ConnectChargesEnabled` / `ConnectPayoutsEnabled` / `ConnectRequirementsDue`. Processing stays async via `StripeWebhookJob`; idempotent by event id. Platform-account events are not handled here.

- **AC5 (charge gate)**: `spec-direct-checkout` AC3 and `spec-ltr-recurring-rent` MUST check `Org.ConnectChargesEnabled == true` before creating a `PaymentIntent` on the connected account; if `false`, return `409` with a clear "complete Stripe onboarding" error. A connected account that is created but not yet `charges_enabled` is treated as **not onboarded**.

- **AC6 (landlord parity for LTR)**: the same onboarding serves the **long-rent** context — an `Org`/landlord that operates leases uses the same connected account (or a clearly-scoped landlord account id) for `spec-ltr-recurring-rent`; the rent flow resolves the **landlord** as merchant of record. No second onboarding mechanism is introduced.

- **AC7**: CasaZen never stores operator bank details or card data — only the Stripe connected-account id and capability flags; all PII/KYC is collected by Stripe's hosted flow.

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC8**: A "Pagamenti / Payouts" settings page (Org admin, behind `<ProtectedRoute>` + admin check) shows connection status (Non collegato / In verifica / Attivo) from `GET /api/connect/status` and a **"Collega Stripe"** CTA that calls `POST /api/connect/onboarding-link` and redirects to the Stripe-hosted onboarding.

- **AC9**: On `return_url`, the page re-fetches status; if `requirementsDue` is non-empty it shows a "completa la verifica" prompt that re-mints an onboarding link. End-user strings in Italian.

- **AC10**: Until `chargesEnabled` is true, the UI surfaces that the **branded booking site / direct checkout cannot go live** (ties the §spec-branded-booking-site publish action to onboarding completion), preventing a guest from hitting the AC5 `409`.

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



1. Enter the primary route for `connect-onboarding`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---


## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | `POST /api/connect/account` (Org admin) creates a **Stripe Connect (Express)** account for the caller's `Org` if absent, persists `Org.St... | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | `POST /api/connect/onboarding-link` (Org admin) creates a Stripe **Account Link** (`type = account_onboarding`) with server-supplied `ret... | Outcome not met; wrong status; silent no-op |
| AC3 | L1 + L2 + L3 | `GET /api/connect/status` (Org admin) returns `{ connectedAccountId?, chargesEnabled, payoutsEnabled, detailsSubmitted, requirementsDue[]... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC4 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | CasaZen never stores operator bank details or card data — only the Stripe connected-account id and capability flags; all PII/KYC is colle... | Outcome not met; wrong status; silent no-op |
| AC8 | L2 + L3 | A "Pagamenti / Payouts" settings page (Org admin, behind `<ProtectedRoute>` + admin check) shows connection status (Non collegato / In ve... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC9 | L2 + L3 | On `return_url`, the page re-fetches status; if `requirementsDue` is non-empty it shows a "completa la verifica" prompt that re-mints an ... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L2 + L3 | Until `chargesEnabled` is true, the UI surfaces that the **branded booking site / direct checkout cannot go live** (ties the §spec-brande... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

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
| `Casazen.Web/Controllers/ConnectController.cs` | Create — `POST /account`, `POST /onboarding-link`, `GET /status` (AC1–AC3) |
| `Casazen.Infrastructure/External/StripeConnectService.cs` | Create — Express account create, Account Link, account retrieve/capabilities (separate from platform `StripeBillingService` and the checkout `StripeService`) |
| `Casazen.Core/Entities/Org.cs` | Modify — add `ConnectChargesEnabled`, `ConnectPayoutsEnabled`, `ConnectRequirementsDue` (builds on `spec-tenant-boundary`'s `StripeConnectedAccountId`) |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — map new `Org` fields |
| `Casazen.Infrastructure/Migrations/<ts>_AddConnectStatusFields.cs` | Create — rebased on snapshot per RF3 |
| `Casazen.Web/Controllers/WebhooksController.cs` | Modify (RF2) — handle connected-account `account.updated` on the Connect route (`Stripe:ConnectWebhookSecret`) |
| `Casazen.Infrastructure/External/StripeWebhookHandler.cs` | Modify — `account.updated` → update Org capability flags (AC4) |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — register `StripeConnectService`; Connect config |

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `src/features/settings/payments-page.tsx` | Create — connection status + "Collega Stripe" + return handling (AC8–AC10) |
| `src/api/connect.api.ts` | Create — `createAccount`, `createOnboardingLink`, `getConnectStatus` |
| `src/queries/use-connect.ts` | Create — `useConnectStatus`, `useStartConnectOnboarding` |
| `src/types/connect.types.ts` | Create — `ConnectStatusDto` |
| `src/routes/index.tsx` | Modify — add `/settings/payments` behind `<ProtectedRoute>` + admin check |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Merchant of record = operator/landlord, never CasaZen** (AD-5; C3/C6): connected-account charges with `application_fee_amount = 0`; CasaZen holds only ids + capability flags, never funds, bank, or card data.
- **KYC/AML handled by Stripe**: identity verification is delegated to Stripe's hosted onboarding (Stripe is the KYC-performing payment institution); CasaZen does not collect or store KYC PII.
- **Charge-gating**: no `PaymentIntent` is created on an account that is not `charges_enabled` (AC5) — prevents stranded guest/tenant payments and the AC3 `409` in production.
- **GDPR**: only the connected-account id + capability flags persist on the `Org`; no operator personal/bank data stored by CasaZen.

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**: `spec-tenant-boundary` (`Org` + `StripeConnectedAccountId`); existing Stripe integration + `WebhooksController`/`StripeWebhookJob`/`StripeWebhookHandler` (RF2 connected-account routing).
- **Blocks**: `spec-direct-checkout` (operator must be `charges_enabled` before guest checkout), `spec-ltr-recurring-rent` (landlord must be `charges_enabled` before recurring rent), `spec-branded-booking-site` (publish gated on onboarding completion, AC10), `spec-supplier-marketplace` (Phase 3 payouts reuse Connect).
- **Related (RF2)**: shares the connected-account webhook route with `spec-direct-checkout`; platform-account billing webhooks (`spec-saas-billing`) stay on the separate platform route.
- **Does not touch**: platform-account Stripe Billing (subscription), OTA adapters, the lease workflow logic itself.

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
