# Spec — RLI Registration, Rescoped to Assisted / Operator-Attended (US-010)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

**Rescope** the RLI capability from "automated filing" to **assisted / operator-attended** (Legal C4):
counsel-reviewed contract templates + cedolare-secca decision support + RLI **pre-fill / export** +
a guided **30-day checklist & notifications**, all layered over the **existing** Openapi.it Docuengine
integration. **CasaZen does NOT file taxes unattended.** CasaZen is **not** an *intermediario abilitato*
(DPR 322/1998); Openapi.it is the **filing channel**, used **only** on explicit per-lease landlord
authorization.

This reconciles PA#2 (the Openapi.it integration **stays**) with Legal C4 (it is **framed as
operator-attended/assisted**). It is **complete + reframe + add guardrails**, NOT greenfield.

Reference: **US-010** (Phase 1.5 — LTR Complete + Verify)
Entry stage: **Stage 02 Design**
Mode: **rescope existing integration to assisted + add guardrails**

### What EXISTS vs what is NEW

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| | Item |
|---|---|
| **EXISTS** | `LeaseRegistration` (Status, `ExternalRegistrationId`, `RegistrationCode`, `ReceiptStoragePath`, `SubmittedAt`, `ConfirmedAt`); `OpenapiLeaseRegistrationProvider` (`SubmitRegistrationAsync` / `PollStatusAsync` / `DownloadReceiptAsync` — currently a **stub** with `TODO`s); `LeaseWorkflowService.TriggerRegistrationAsync` (operator-triggered, requires `Signed`, guards double-submit); `LeaseRegistrationStatusPollingJob` (read-only poll); `LeaseContract.RegistrationDeadline` (= `StartDate + 30d`), `FiscalRegime`, `HasExtraEUTenant`; `LeaseContractTemplateService.GeneratePdfAsync`; `SendGridService` |
| **NEW** | Per-filing landlord **delega/authorization** capture + ToS attestation gating submission; counsel-reviewed **template variants** per `FiscalRegime` (versioned); **cedolare decision-support** service (non-binding); **RLI pre-fill/export** endpoint; **30-day checklist + reminder job** (SendGrid); **extra-EU authority-communication** checklist item; new `LeaseEventType` values |

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a **long-rent landlord**, I want CasaZen to help me register my lease correctly — give me a
counsel-reviewed contract, advise on cedolare secca vs ordinary regime, pre-fill the RLI for review,
and guide me through the 30-day deadline with reminders — while making clear that **I (or my authorized
intermediary) remain responsible for the filing** and that I must explicitly authorize each submission,
so I stay compliant without CasaZen acting as an unauthorized tax intermediary.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1 (per-filing authorization / delega)**: `TriggerRegistrationAsync` and
  `POST /api/leases/{id}/registration` require a recorded landlord **authorization (delega)** for that
  specific lease; absent → `400`/forbidden **before** any Openapi.it call. A new
  `LeaseRegistrationAuthorization` record (carries `OrgId`, RF1) captures who/when/scope/ToS-version.
  Exact delega wording is **[COUNSEL_REQUIRED]**.

- **AC2 (operator-attended only)**: Assert there is **no** scheduled or automatic submission path —
  submission happens **exclusively** via the explicit operator action. Regression assertion:
  `LeaseRegistrationStatusPollingJob` only **polls** status (read-only) and **never** calls
  `SubmitRegistrationAsync`.

- **AC3 (counsel-reviewed templates)**: `ILeaseTemplateService` produces a template **variant per
  `FiscalRegime`** (`CedolareSecca`, `RegimeOrdinario`, `CanoneConcordato`), each stamped with a
  **counsel-reviewed template version id** (config/DB driven). A lease whose regime has no
  reviewed/approved template is **blocked** from PDF generation. **[COUNSEL_REQUIRED]**

- **AC4 (cedolare-secca decision support)**: New `ICedolareAdvisoryService.Evaluate(lease)` returns a
  **structured, non-binding** comparison — cedolare (21%, or 10% for *canone concordato*) vs ordinary
  (IRPEF + ~2% *imposta di registro* + *imposta di bollo*) — with an explicit
  `Disclaimer` field ("informativa, non consulenza fiscale"). Rates are **config-driven, never
  hardcoded magic numbers**. **[COUNSEL_REQUIRED]**

- **AC5 (RLI pre-fill / export)**: `GET /api/leases/{id}/rli/export` returns a **pre-filled RLI dataset**
  (and/or PDF) — quadri/contraente fields drawn from the lease + parties — for the landlord/intermediary
  to **review and submit**. It **does not file**. Gated `RequireContext:long-rent:lease.register` and
  owner-scoped.

- **AC6 (30-day checklist + notifications)**: A new `RliDeadlineReminderJob` (Hangfire recurring,
  registered in `Program.cs › ConfigureRecurringJobs`) uses `RegistrationDeadline` to send SendGrid
  reminders at **T-15 / T-7 / T-1 / overdue**, **idempotently** (one reminder per milestone — guarded by
  an emitted event). `GET /api/leases/{id}/rli/checklist` returns checklist items + done state.

- **AC7 (extra-EU tenant duty)**: When `LeaseContract.HasExtraEUTenant` is true, the checklist includes
  the **authority/Questura communication** item (**Art. 7 D.Lgs 286/1998**) and a distinct reminder is
  sent. **[COUNSEL_REQUIRED]**

- **AC8 (Openapi.it = filing channel)**: When the landlord authorizes (AC1), the real
  `OpenapiLeaseRegistrationProvider` calls (replacing the current stub `TODO`s — `Openapi:ApiKey` /
  `Openapi:BaseUrl`, `HttpClient("Openapi")`) submit through Openapi.it **as a filing channel on the
  landlord's behalf**; CasaZen is recorded (metadata + ToS) as a **software facilitator, not an
  *intermediario abilitato***. The stub remains acceptable behind a feature flag until counsel sign-off.
  **[COUNSEL_REQUIRED]**

- **AC9 (ToS / attestation)**: The registration flow surfaces and **records** the landlord's attestation
  that filing responsibility lies with the **landlord / authorized intermediary** (DPR 322/1998), tied to
  a ToS version on the `LeaseRegistrationAuthorization` (AC1).

- **AC10 (events)**: New `LeaseEventType` values `RegistrationAuthorized`, `RliExported`,
  `DeadlineReminderSent` are appended and emitted at the corresponding steps (persisted-enum migration if needed).

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC11 (assisted-flow contract — UI built in `spec-ltr-frontend`)**: The lease detail page surfaces:
  a **delega/attestation capture** that gates the "Submit RLI" action; a **cedolare decision panel** with
  the non-advice disclaimer; a **30-day countdown** from `RegistrationDeadline`; a **checklist** including
  the extra-EU item when `HasExtraEUTenant`; and an **"Export RLI pre-fill"** button. There is **no**
  unattended-filing affordance anywhere in the UI.

- **AC12 (GDPR)**: RLI export and checklist contain `Party` PII strictly for the **authorized landlord**;
  PII is not logged; Italian-language disclaimers are shown verbatim.

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



1. Enter the primary route for `ltr-rli-registration`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
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

## Export / Report Criteria

**Required** (export / feed / report ACs present).

### Feed / file

| Requirement | Required |
|---|---|
| Declared Content-Type matches payload (e.g. text/calendar, text/csv, application/pdf) | yes |
| Non-empty body when seed data exists | yes |
| No CF / P.IVA / secrets in filename or URL | yes |
| Documented columns/fields or VEVENT shape in AC / design | yes |

### PDF (when applicable)

| Requirement | Required |
|---|---|
| Real PDF bytes (%PDF) - not empty stub | yes |
| Readable labeled content for the intended audience | yes |

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — Files to create / modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `Casazen.Infrastructure/External/OpenapiLeaseRegistrationProvider.cs` | **MODIFY** — implement real Openapi.it calls behind the authorization gate; framed as filing channel (EXISTS — stub) |
| `Casazen.Infrastructure/Services/LeaseWorkflowService.cs` | **MODIFY** — require delega before `TriggerRegistrationAsync`; emit `RegistrationAuthorized` (EXISTS) |
| `Casazen.Web/Controllers/LeasesController.cs` | **MODIFY** — add delega-capture, `/rli/export`, `/rli/checklist` endpoints (EXISTS) |
| `Casazen.Core/Entities/LeaseRegistrationAuthorization.cs` | **CREATE** — `OrgId`, `LeaseContractId`, authorizer, timestamp, scope, ToS version (RF1) |
| `Casazen.Core/Entities/Enums/LeaseEventType.cs` | **MODIFY** — add `RegistrationAuthorized`, `RliExported`, `DeadlineReminderSent` (EXISTS) |
| `Casazen.Core/Services/ICedolareAdvisoryService.cs` + `Casazen.Infrastructure/Services/CedolareAdvisoryService.cs` | **CREATE** — non-binding comparison + disclaimer |
| `Casazen.Core/Services/IRliExportService.cs` + `Casazen.Infrastructure/Services/RliExportService.cs` | **CREATE** — pre-fill dataset/PDF (no filing) |
| `Casazen.Infrastructure/External/LeaseContractTemplateService.cs` | **MODIFY** — per-`FiscalRegime` counsel-reviewed variants + version id (EXISTS) |
| `Casazen.Web/BackgroundJobs/RliDeadlineReminderJob.cs` | **CREATE** — SendGrid T-15/7/1/overdue, idempotent |
| `Casazen.Infrastructure/External/SendGridService.cs` | **MODIFY** — reminder template usage (EXISTS) |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | **MODIFY** — register advisory/export services (EXISTS) |
| `Casazen.Web/Program.cs` | **MODIFY** — register `RliDeadlineReminderJob` + `RecurringJob.AddOrUpdate` (EXISTS) |
| `Casazen.Infrastructure/Migrations/*_AddLeaseRegistrationAuthorization.cs` | **CREATE** — `OrgId`, rebase onto `AppDbContextModelSnapshot.cs` (RF3) |
| `Casazen.Tests/Unit/Services/CedolareAdvisoryServiceTests.cs` | **CREATE** — regime matrix + disclaimer present |
| `Casazen.Tests/Unit/Services/RliAuthorizationGateTests.cs` | **CREATE** — no delega ⇒ submission blocked (AC1, AC2) |
| `Casazen.Tests/Unit/Jobs/RliDeadlineReminderJobTests.cs` | **CREATE** — milestone idempotency + extra-EU item (AC6, AC7) |

### Frontend — Files to create / modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `src/features/leases/components/{cedolare-decision-panel,rli-checklist,delega-capture-dialog}.tsx` | **Delivered in `spec-ltr-frontend`** — listed here for traceability (AC11) |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **No unattended filing** — operator-attended; **per-filing landlord authorization (delega capture)**;
  ToS places filing responsibility on the **landlord / authorized intermediary**; **Openapi.it = filing
  channel**; **CasaZen ≠ *intermediario abilitato* (DPR 322/1998)**. **[COUNSEL_REQUIRED]**
- **30-day RLI deadline** — driven by `LeaseContract.RegistrationDeadline`; checklist + reminders.
- **Extra-EU tenant authority-communication duty (Art. 7 D.Lgs 286/1998)** where `HasExtraEUTenant`. **[COUNSEL_REQUIRED]**
- **Cedolare-secca decision support is non-binding** — "informativa, non consulenza fiscale" disclaimer. **[COUNSEL_REQUIRED]**
- **Counsel-reviewed contract templates** — versioned; unreviewed regime blocked. **[COUNSEL_REQUIRED]**
- **GDPR**: `Party` PII in templates/exports limited to the authorized landlord; not logged.

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**: `LeaseRegistration` (EXISTS), `LeaseWorkflowService` (EXISTS), `LeaseRegistrationStatusPollingJob` (EXISTS), `OpenapiLeaseRegistrationProvider` (EXISTS — stub), `RegistrationDeadline` / `FiscalRegime` / `HasExtraEUTenant` (EXISTS), `SendGridService` (EXISTS); `spec-tenant-boundary` — `OrgId` lands first so `LeaseRegistrationAuthorization` carries it from creation (RF3).
- **Blocks**: LTR GA **legal** sign-off — the assisted framing + delega gate is the regulatory gate for the lease flow.
- **Related**: `spec-ltr-verification` (asserts operator-attended, AC9 there), `spec-ltr-frontend` (builds the assisted-flow UI), `spec-ltr-recurring-rent` (the selected `FiscalRegime` drives the cedolare bollo rule on rent receipts).
- **Does not modify**: the e-sign flow; the recurring-rent ledger; the read-only registration polling behaviour (it stays read-only).

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
