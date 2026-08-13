# Spec — Supplier Marketplace (US-014)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Open CasaZen's second revenue stream (F10): a marketplace where operators discover,
hire, and pay vetted **suppliers** (cleaning, maintenance, photography, linen) directly
inside the platform, and CasaZen earns a transparent **platform take-rate** on each
completed marketplace transaction.

The take-rate charged here is the **only take-rate in the business model** — it is
levied on operator↔supplier service transactions and is **NEVER** applied to guest
bookings (direct booking stays commission-free, F3/F6). Payouts to suppliers run on
**Stripe Connect** (a distinct integration from today's single-account `StripeGateway`),
with escrow-style hold-until-completion and supplier KYC.

Reference: **US-014** (Phase 3 — Distribution + Marketplace; draft-v3 §B Phase 3 + §C row `spec-supplier-marketplace`)
Stage of entry: **Stage 01 Planning** (epic-level macro-spec; splits into issues at Stage 02)

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a **property manager (operator)**, I want to browse vetted suppliers, request and
track service jobs (cleaning, maintenance, photography, linen), and pay them securely
in-platform, so that I can outsource operations without leaving CasaZen — and as the
**platform**, CasaZen collects a transparent take-rate on each completed transaction,
paying out the supplier's net via Stripe Connect.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: New tenant-scoped entities, each carrying `OrgId` per the `spec-tenant-boundary`
  invariant (RF1): `Supplier`, `SupplierServiceListing`, `MarketplaceOrder`,
  `MarketplaceTransaction` (take-rate + payout breakdown). All four reject inserts
  without a valid `OrgId` (FK + NOT NULL).

- **AC2**: `GET /api/marketplace/suppliers` — paginated, filterable by `category`
  (`cleaning | maintenance | photography | linen`) and service area; returns only
  `Active` + KYC-`Verified` suppliers. No supplier bank/Stripe identifiers in the payload.

- **AC3**: `POST /api/marketplace/orders` — operator creates a service order against a
  listing; returns `MarketplaceOrder` in `Pending` status. RBAC: operator role within the
  `OrgId` only (403 otherwise).

- **AC4**: Order lifecycle endpoints enforce a state machine
  `Pending → Accepted → InProgress → Completed → PaidOut` (plus `Cancelled`/`Disputed`);
  illegal transitions return 409.

- **AC5**: **Stripe Connect** payout flow — on order completion, payment is captured and
  split via `application_fee_amount` (the platform take-rate, configurable per category),
  with the supplier as the connected account receiving the net. CasaZen never retains
  funds beyond the escrow hold window. Take-rate percentage is DB-driven, **never hardcoded**.

- **AC6**: Supplier onboarding creates a **Stripe Connect connected account** and blocks
  any payout until KYC/identity verification returns `Verified` (`charges_enabled` +
  `payouts_enabled`).

- **AC7**: **Connected-account vs platform-account webhook routing** (RF2): connected-account
  events (`account.updated`, `payout.*`, `transfer.*`, `payment_intent.*` on the connected
  account) are verified with the **Connect signing secret** and dispatched via the async
  `StripeWebhookJob`, kept separate from platform-account billing events.

- **AC8**: **DAC7 seller-data collection** — supplier onboarding captures the DAC7 reportable
  dataset (legal name, address, TIN/VAT, registration); `GET /api/marketplace/dac7/export`
  (Admin only) produces an annual reportable export of consideration paid per supplier.

- **AC9 (Regression)**: No marketplace endpoint ever returns guest-booking data, and the
  take-rate is asserted to apply **only** to `MarketplaceTransaction` — a test verifies
  zero take-rate/`application_fee` is ever attached to a guest `Booking`/direct-checkout flow.

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC10**: Marketplace browse page (`/marketplace`) — supplier cards filtered by category
  and area; each card shows rating, categories, and "Richiedi servizio" CTA. Verified badge
  shown; no financial identifiers rendered.

- **AC11**: Supplier detail page with listings + "Crea ordine" dialog (service, date,
  notes); on submit shows the order with a transparent fee breakdown
  (totale, commissione piattaforma, netto fornitore).

- **AC12**: Operator order-tracking view reflects the AC4 state machine with status badges
  and timestamps; cancel allowed only in `Pending`/`Accepted`.

- **AC13**: Supplier earnings/payouts view (for supplier role) — list of orders with
  gross, take-rate, net, payout status; reads from Connect payout state.

- **AC14**: All `/marketplace/*` routes wrapped in `<ProtectedRoute>`; supplier vs operator
  views gated by role.

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



1. Enter the primary route for `supplier-marketplace`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | New tenant-scoped entities, each carrying `OrgId` per the `spec-tenant-boundary` | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | `GET /api/marketplace/suppliers` — paginated, filterable by `category` | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | `POST /api/marketplace/orders` — operator creates a service order against a | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | Order lifecycle endpoints enforce a state machine | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | **Stripe Connect** payout flow — on order completion, payment is captured and | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | Supplier onboarding creates a **Stripe Connect connected account** and blocks | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | **Connected-account vs platform-account webhook routing** (RF2): connected-account | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | **DAC7 seller-data collection** — supplier onboarding captures the DAC7 reportable | Outcome not met; wrong status; silent no-op |
| AC9 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC10 | L2 + L3 | Marketplace browse page (`/marketplace`) — supplier cards filtered by category | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC11 | L2 + L3 | Supplier detail page with listings + "Crea ordine" dialog (service, date, | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L2 + L3 | Operator order-tracking view reflects the AC4 state machine with status badges | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC13 | L2 + L3 | Supplier earnings/payouts view (for supplier role) — list of orders with | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC14 | L2 + L3 | All `/marketplace/*` routes wrapped in `<ProtectedRoute>`; supplier vs operator | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

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
| `Casazen.Core/Entities/Supplier.cs` | Create (new module) — incl. `OrgId` FK, `StripeConnectAccountId`, `KycStatus`, DAC7 fields |
| `Casazen.Core/Entities/SupplierServiceListing.cs` | Create (new module) — category, price, service area; `OrgId` |
| `Casazen.Core/Entities/MarketplaceOrder.cs` | Create (new module) — state machine status; `OrgId` |
| `Casazen.Core/Entities/MarketplaceTransaction.cs` | Create (new module) — gross/take-rate/net + payout ref; `OrgId` |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — add DbSets + relationships + `OrgId` query filters |
| `Casazen.Infrastructure/Migrations/` | Create — migration `AddSupplierMarketplace` (rebase on `AppDbContextModelSnapshot.cs`, never hand-merge) |
| `Casazen.Infrastructure/Payments/IPaymentGateway.cs` | Modify — add Connect operations (connected-account create, destination charge/`application_fee`, payout) |
| `Casazen.Infrastructure/Payments/StripeGateway.cs` | Modify — implement Stripe Connect (currently single-account stub) |
| `Casazen.Infrastructure/External/StripeWebhookHandler.cs` | Modify — connected-account event handlers (`account.updated`, `payout.*`, `transfer.*`) (RF2) |
| `Casazen.Web/Controllers/WebhooksController.cs` | Modify — verify Connect signing secret for connected-account events; keep platform path separate |
| `Casazen.Web/BackgroundJobs/StripeWebhookJob.cs` | Modify — dispatch connected-account events |
| `Casazen.Web/Controllers/MarketplaceController.cs` | Create (new module) — suppliers/orders/transactions endpoints |
| `Casazen.Core/Services/IMarketplaceService.cs` + `Casazen.Infrastructure/Services/MarketplaceService.cs` | Create (new module) — order state machine + fee split |
| `Casazen.Core/Services/ISupplierService.cs` + `Casazen.Infrastructure/Services/SupplierService.cs` | Create (new module) — onboarding + KYC gating |
| `Casazen.Core/Services/IDac7ReportService.cs` + `Casazen.Infrastructure/Services/Dac7ReportService.cs` | Create (new module) — DAC7 dataset + annual export |
| `Casazen.Web/BackgroundJobs/Dac7ReportJob.cs` | Create (new module) — annual reportable aggregation |
| `Casazen.Web/Program.cs` | Modify — register `Dac7ReportJob` in `ConfigureRecurringJobs` (annual cadence) |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — supplier/operator marketplace policies + Scale/Pro plan entitlement |

### Frontend — Files to create/modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `src/features/marketplace/marketplace-page.tsx` | Create (new module) — browse + filters |
| `src/features/marketplace/supplier-detail-page.tsx` | Create (new module) |
| `src/features/marketplace/components/supplier-card.tsx` | Create (new module) |
| `src/features/marketplace/components/service-order-dialog.tsx` | Create (new module) — fee breakdown |
| `src/features/marketplace/components/order-tracking-panel.tsx` | Create (new module) |
| `src/features/marketplace/components/supplier-earnings-table.tsx` | Create (new module) — payout status |
| `src/api/marketplace.api.ts` | Create (new module) — suppliers/orders/payouts calls |
| `src/queries/use-marketplace.ts` | Create (new module) — TanStack Query hooks |
| `src/types/marketplace.types.ts` | Create (new module) — DTOs (no financial identifiers) |
| `src/routes/index.tsx` | Modify — add `/marketplace/*` under `<ProtectedRoute>` |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Marketplace VAT**: take-rate invoicing and supplier-side VAT treatment to be confirmed
  with counsel (IT 22% IVA on the platform commission; EU cross-border supplier scenarios). **[COUNSEL_REQUIRED]**
- **Stripe Connect / escrow**: payouts and escrow-style hold-until-completion run on Stripe
  Connect; CasaZen takes the platform fee via `application_fee_amount` and never holds funds
  beyond the escrow window (mirrors the operator-MoR principle of `spec-direct-checkout`).
- **DAC7**: seller-data collection at onboarding + **annual reporting** of consideration paid
  per reportable supplier. **[COUNSEL_REQUIRED]** on reportable-threshold and filing channel.
- **Supplier KYC**: identity verification via Stripe Connect; no payouts before `Verified`.
- **Take-rate boundary (hard invariant)**: the marketplace take-rate is the **only** take-rate
  in the model and is **never** applied to guest bookings (presided by AC9 regression test).

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**: `spec-tenant-boundary` (`OrgId` + plan entitlement, RF1); **Stripe Connect
  payouts** integration (shared with `spec-direct-checkout`); KYC via Connect.
- **Blocks**: the §A.8 marketplace revenue stream (10–20% mix, F10) and the Phase 3 exit
  criterion "a marketplace transaction completes with platform take-rate".
- **Related**: `spec-saas-billing` (shared `WebhooksController`/`StripeWebhookHandler` —
  platform-account vs connected-account routing, RF2); `spec-direct-checkout` (Connect
  enablement); `spec-google-vacation-rentals` (sibling Phase 3 spec).

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
