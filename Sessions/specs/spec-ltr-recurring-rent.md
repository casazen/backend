# Spec — LTR Recurring Rent Ledger + Billing Job (US-007)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Close the **one real LTR gap**: `LeaseContract.MonthlyRent` exists today as a static
`decimal(18,2)` field with **no recurring-rent ledger and no billing job**. This spec adds a
recurring-rent **ledger** (new entities), a **Hangfire recurring job** that materialises monthly
charges, and **Stripe Connect** collection with the **landlord as merchant of record**.

This is a **complete + verify** spec, NOT greenfield: the lease subsystem
(`LeaseContract` / `LeaseRegistration` / `Party` / `LeaseEvent`, `LeasesController`,
`LeaseWorkflowService`, the 3 lease jobs) **already exists**. We extend it.

Reference: **US-007** (Phase 1.5 — LTR Complete + Verify, runs parallel to Phase 1)
Entry stage: **Stage 02 Design**
Mode: **NEW capability over an existing subsystem**

### What EXISTS vs what is NEW

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| | Item |
|---|---|
| **EXISTS** | `LeaseContract.MonthlyRent`, `Status`, `StartDate`, `EndDate`, `FiscalRegime`, `DataRetentionUntil`; Hangfire (`Program.cs › ConfigureRecurringJobs`); `StripeService` (PaymentIntent create/confirm/refund — **no Connect**); `WebhooksController /webhooks/stripe` + `StripeWebhookHandler` + `StripeWebhookJob` (single platform-account secret); `Payment` entity (Booking-scoped only) |
| **NEW** | `RentSchedule` + `RentLedgerEntry` entities (carry `OrgId` from creation); `RentChargeJob`; Stripe **Connect** path (landlord = MoR); connected-account webhook routing; rent-receipt generation (IVA/bollo); rent repositories/service; `long-rent:rent.*` permissions; EF migration |

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a **long-rent landlord**, I want CasaZen to automatically generate and collect each month's
rent for an active registered lease — with the rent settling directly to **my** Stripe account
(I am the merchant of record, never CasaZen) — and to produce a compliant rent receipt, so that I
stop tracking `MonthlyRent` by hand and stay PSD2/SCA- and tax-compliant.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: New entity `RentSchedule` (one per billable lease) with: `Id`, `OrgId` (**NOT NULL + FK**, RF1),
  `LeaseContractId` (FK), `Cadence` (`RentCadence.Monthly` default), `BillingDayOfMonth` (1–28),
  `Currency` (default `eur`), `Amount` (seeded from `LeaseContract.MonthlyRent`), `NextRunDate`,
  `IsActive`, `LandlordStripeAccountId`, `MandateReference`, `CreatedAt`/`UpdatedAt`.
  A schedule may only be activated for a lease whose `Status == LeaseStatus.Registered`.

- **AC2**: New entity `RentLedgerEntry` (one per billing period) with: `Id`, `OrgId` (**NOT NULL + FK**),
  `LeaseContractId` (FK), `RentScheduleId` (FK), `PeriodStart`, `PeriodEnd`, `AmountDue`,
  `Status` (new enum `RentLedgerStatus { Scheduled, Charged, Paid, Failed, Cancelled }`),
  `StripePaymentIntentId`, `ConnectedAccountId`, `IsVatExempt` (bool), `StampDutyAmount` (decimal),
  `ReceiptStoragePath`, `ChargedAt`, `PaidAt`. **Unique constraint** on `(LeaseContractId, PeriodStart)`.

- **AC3**: `RentChargeJob` (new Hangfire recurring job, registered in `Program.cs › ConfigureRecurringJobs`,
  `[AutomaticRetry(Attempts = 3)]` + `[DisableConcurrentExecution(timeoutInSeconds: 60)]`) scans
  active `RentSchedule`s due on/before today and creates **exactly one** `RentLedgerEntry` per lease
  per period. **Idempotency test**: running the job twice for the same period creates no duplicate
  (relies on the AC2 unique constraint).

- **AC4**: For each `Scheduled` entry the job initiates a **Stripe Connect** PaymentIntent where the
  **landlord's connected account is the merchant of record** (`on_behalf_of` + `transfer_data.destination`,
  or a direct charge on the connected account). Test asserts the PaymentIntent is **not** created on the
  platform account and CasaZen takes **no** `application_fee` on rent (CasaZen never holds/settles tenant funds).

- **AC5**: `IStripeService` is extended with `CreateConnectPaymentIntentAsync(long amount, string currency, string connectedAccountId, Dictionary<string,string> metadata, bool offSession)`.
  The **existing** booking PaymentIntent path (`CreatePaymentIntentAsync`) is **unchanged** (regression AC).

- **AC6 (RF2 — webhook routing)**: `StripeWebhookHandler` + `WebhooksController` distinguish
  **platform-account vs connected-account** events: the connected-account signing secret
  (`Stripe:ConnectWebhookSecret`) is verified separately from the platform secret
  (`Stripe:WebhookSecret`), and both dispatch via the async `StripeWebhookJob`. A
  `payment_intent.succeeded` carrying rent metadata transitions its `RentLedgerEntry` to `Paid`
  (+ receipt, AC8); `payment_intent.payment_failed` → `Failed` (+ retry/notify). Booking-payment
  routing is preserved.

- **AC7 (PSD2/SCA + consent)**: A `RentSchedule` cannot be activated without a recorded tenant
  mandate (`MandateReference`); off-session rent PaymentIntents are created with `off_session = true`
  and the stored mandate. A charge attempt with no mandate is rejected before any Stripe call.
  Exact mandate/consent wording is **[COUNSEL_REQUIRED]**.

- **AC8 (rent receipt / IVA / bollo)**: On `Paid`, a rent receipt is generated. Residential rent is
  marked **IVA-exempt (Art. 10 DPR 633/72)**; a **€2 _imposta di bollo_** is applied on receipts
  **> €77.47** **except where the cedolare-secca exemption applies**
  (`FiscalRegime == FiscalRegime.CedolareSecca` ⇒ `StampDutyAmount = 0`). Test is parametrised over
  `FiscalRegime × amount` (e.g. `RegimeOrdinario` + €1200 ⇒ €2 bollo; `CedolareSecca` + €1200 ⇒ €0).
  Threshold/amount are config-driven, never hardcoded magic numbers. **[COUNSEL_REQUIRED]**

- **AC9 (migration, RF3)**: EF migration `AddRentLedger` creates `RentSchedules` + `RentLedgerEntries`
  with `OrgId` NOT NULL + FK, **rebased onto the updated `AppDbContextModelSnapshot.cs`** (never
  hand-merged). `MigrationTests` stays green; a `LeaseContract.RentSchedule` navigation is added.

- **AC10 (RBAC)**: New `long-rent` permissions `rent.read` + `rent.manage` registered in
  `ServiceCollectionExtensions.RegisterContextPolicies`. Rent endpoints are gated
  `RequireContext:long-rent:rent.read|rent.manage` and owner-scoped via the existing
  `LeaseWorkflowService` verified-lease pattern.

- **AC11 (regression)**: The Booking `Payment` flow and the 3 existing lease jobs
  (`LeaseSignStatusPollingJob`, `LeaseRegistrationStatusPollingJob`, `ESignWebhookJob`) are unaffected.

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC12 (API contract only — full UI in `spec-ltr-frontend`)**: Expose
  `GET /api/leases/{id}/rent/schedule`, `GET /api/leases/{id}/rent/ledger`,
  `POST /api/leases/{id}/rent/schedule` (enable/update), `POST /api/leases/{id}/rent/schedule/disable`,
  returning DTOs that **omit raw Party PII** (no `FiscalCode`, no full tenant email in ledger rows).
  The rent **schedule card** and **ledger table** are built in `spec-ltr-frontend` against these DTOs.

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



1. Enter the primary route for `ltr-recurring-rent`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---


## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | New entity `RentSchedule` (one per billable lease) with: `Id`, `OrgId` (**NOT NULL + FK**, RF1), | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | New entity `RentLedgerEntry` (one per billing period) with: `Id`, `OrgId` (**NOT NULL + FK**), | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | `RentChargeJob` (new Hangfire recurring job, registered in `Program.cs › ConfigureRecurringJobs`, | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | For each `Scheduled` entry the job initiates a **Stripe Connect** PaymentIntent where the | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | `IStripeService` is extended with `CreateConnectPaymentIntentAsync(long amount, string currency, string connectedAccountId, Dictionary<st... | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC9 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC10 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC11 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC12 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — Files to create / modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `Casazen.Core/Entities/RentSchedule.cs` | **CREATE** — `OrgId`, `LeaseContractId`, cadence, billing day, amount, `NextRunDate`, `IsActive`, `LandlordStripeAccountId`, `MandateReference` |
| `Casazen.Core/Entities/RentLedgerEntry.cs` | **CREATE** — period, `AmountDue`, status, `StripePaymentIntentId`, `ConnectedAccountId`, `IsVatExempt`, `StampDutyAmount`, `ReceiptStoragePath` |
| `Casazen.Core/Entities/Enums/RentCadence.cs`, `RentLedgerStatus.cs` | **CREATE** |
| `Casazen.Core/Entities/LeaseContract.cs` | **MODIFY** — add `virtual RentSchedule? RentSchedule` navigation (EXISTS) |
| `Casazen.Core/Repositories/IRentScheduleRepository.cs`, `IRentLedgerRepository.cs` | **CREATE** |
| `Casazen.Infrastructure/Repositories/RentScheduleRepository.cs`, `RentLedgerRepository.cs` | **CREATE** (EF Core) |
| `Casazen.Core/Services/IRentBillingService.cs` | **CREATE** — due-detection, ledger materialisation, receipt orchestration |
| `Casazen.Infrastructure/Services/RentBillingService.cs` | **CREATE** |
| `Casazen.Infrastructure/External/RentReceiptService.cs` | **CREATE** — IVA-exempt note + bollo rule (reuses PDF approach of `LeaseContractTemplateService`) |
| `Casazen.Web/BackgroundJobs/RentChargeJob.cs` | **CREATE** — recurring billing job |
| `Casazen.Infrastructure/External/StripeService.cs` | **MODIFY** — add `CreateConnectPaymentIntentAsync` (Connect/MoR); `IStripeService` (EXISTS) |
| `Casazen.Infrastructure/External/StripeWebhookHandler.cs` | **MODIFY** — connected-account routing for rent intents (EXISTS, platform-only today) |
| `Casazen.Web/Controllers/WebhooksController.cs` | **MODIFY** — select platform vs connect signing secret per event source, RF2 (EXISTS) |
| `Casazen.Web/BackgroundJobs/StripeWebhookJob.cs` | **MODIFY** — dispatch rent vs booking events (EXISTS) |
| `Casazen.Web/Controllers/LeasesController.cs` | **MODIFY** — add `/{id}/rent/*` endpoints (EXISTS) |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | **MODIFY** — `long-rent:rent.read|rent.manage` policies (EXISTS) |
| `Casazen.Web/Program.cs` | **MODIFY** — register repos/services/`RentChargeJob` + `RecurringJob.AddOrUpdate<RentChargeJob>` in `ConfigureRecurringJobs` (EXISTS) |
| `Casazen.Infrastructure/Migrations/*_AddRentLedger.cs` | **CREATE** — rebase onto `AppDbContextModelSnapshot.cs` (RF3) |
| `Casazen.Tests/Unit/Services/RentBillingServiceTests.cs` | **CREATE** — idempotency, bollo/IVA matrix, mandate gate |
| `Casazen.Tests/Unit/Jobs/RentChargeJobTests.cs` | **CREATE** — due-detection, no-duplicate, Connect MoR assertion |
| `Casazen.Tests/Integration/RentLedgerMigrationTests.cs` | **CREATE** (or extend `MigrationTests.cs`) |

### Frontend — Files to create / modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| (rent schedule card + ledger table) | **Deferred to `spec-ltr-frontend`** — this spec only fixes the DTO/endpoint contract (AC12) |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **(C6 — blocking) Stripe Connect with the LANDLORD as merchant of record — CasaZen never holds or
  settles tenant rent funds** (mirrors `spec-direct-checkout`, landlord ↔ operator).
- **Residential rent generally IVA-exempt — Art. 10 DPR 633/72 — with €2 _imposta di bollo_ on
  receipts > €77.47 except where the cedolare-secca exemption applies. [COUNSEL_REQUIRED]**
- **Recurring-payment PSD2/SCA + consent**: stored tenant mandate; off-session SCA flags. **[COUNSEL_REQUIRED]** (mandate wording)
- **Rent receipt/invoice** issued per paid period.
- **(RF1 — tenant invariant)** `RentSchedule` and `RentLedgerEntry` carry `OrgId` + honour plan
  entitlement; they **cannot ship un-scoped**.
- **(RF2 — webhook routing)** platform-account vs **connected-account** Stripe event routing, separate
  signing-secret verification, async `StripeWebhookJob` dispatch.
- **GDPR**: ledger holds financial PII linked to `Party`; retention aligned to `LeaseContract.DataRetentionUntil`; receipts behind authenticated, owner-scoped endpoints only.

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**: `LeaseContract` (EXISTS); **Stripe Connect** (NEW — extends `StripeService`); Hangfire (EXISTS); `spec-tenant-boundary` — `OrgId` migration **lands first** (RF3) so new tables carry `OrgId` from creation.
- **Blocks**: `spec-ltr-frontend` (rent UI needs these endpoints/DTOs); LTR GA exit criterion ("platform automatically bills recurring monthly rent").
- **Related**: `spec-direct-checkout` (shares the Stripe Connect operator-MoR pattern + `WebhooksController`/`StripeWebhookHandler`/`StripeWebhookJob` routing, RF2); `spec-saas-billing` (platform-account Stripe Billing — **separate** routing); `spec-ltr-rli-registration` (cedolare regime drives bollo on receipts, AC8).
- **Does not modify**: the Booking `Payment` flow; the e-sign / RLI workflow; the 3 existing lease jobs.

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
