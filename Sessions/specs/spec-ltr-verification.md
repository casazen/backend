# Spec — LTR Flow End-to-End Verification & Test Hardening (US-009)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

**Verify** the existing lease flow (create → e-sign → RLI register → receipt) and the **3 lease
background jobs** end-to-end, and **add the missing coverage**. This is a **verification /
test-hardening** spec — the goal is to **assert the shipped subsystem works** and close test gaps,
**NOT to rebuild** it. Any production defect discovered is filed as a separate issue (e.g. the known
`LeaseSignStatusPollingJob` TODO `#177`), not fixed inline here unless it is a trivial test seam.

Reference: **US-009** (Phase 1.5 — LTR Complete + Verify)
Entry stage: **Stage 02 Design**
Mode: **verify + add coverage (no rebuild)**

### What EXISTS vs what is NEW

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| | Item |
|---|---|
| **EXISTS (verify)** | `LeaseWorkflowServiceTests` (17 unit tests); `LeaseContractSerializationTests`; integration harness `CasazenWebApplicationFactory` + `TestAuthHandler`; `LeasesController`; `LeaseWorkflowService`; jobs `LeaseSignStatusPollingJob` / `LeaseRegistrationStatusPollingJob` / `ESignWebhookJob`; `WebhooksController /webhooks/esign` HMAC verification |
| **NEW (tests)** | `LeasesController` integration tests (auth + context-policy + owner-scoping + status transitions + receipt); job tests for the 3 lease jobs; webhook-signature tests; GDPR retention/erasure tests; receipt-integrity tests; an explicit **operator-attended RLI** assertion; numeric coverage report |

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As the **CasaZen team**, before LTR goes GA we need automated proof that the lease lifecycle and its
background jobs work correctly under real auth and context-RBAC, that retention/erasure and receipts
behave, and that RLI submission is **operator-attended only** — so we can sign off the release gate
with confidence.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1 (full-flow integration)**: With `TestAuthHandler` seeding a `LongTermLandlord` + `long-rent`
  context, an integration test drives the whole API flow:
  `POST /api/leases` (201) → `POST /{id}/signing` (200, signers) → simulate e-sign webhook
  (lease → `Signed`) → `POST /{id}/registration` (202) → simulate registration poll
  (registration → `Registered`, lease → `Registered`) → `GET /{id}/registration/receipt` (200,
  `application/pdf`). It asserts each `LeaseStatus` transition **and** the emitted `LeaseEvent`
  sequence: `Created`, `SigningInitiated`, `AllPartiesSigned`, `RegistrationSubmitted`, `RegistrationConfirmed`.

- **AC2 (RBAC enforcement)**: Requests **without** the `LongTermLandlord` role **or** without the
  required `RequireContext:long-rent:{perm}` are `403`. `POST` create/sign/register require
  `lease.create` / `lease.sign` / `lease.register` respectively. A second owner accessing another
  owner's lease gets `404`/`Forbid` (owner-scoping via `GetVerifiedLeaseAsync`).

- **AC3 (`LeaseRegistrationStatusPollingJob`)**: Given a `RegistrationStatus.SentToProvider` registration
  whose `PollStatusAsync` returns `IsConfirmed = true`, the job transitions registration → `Registered`,
  lease → `Registered`, sets `RegistrationCode` + `ConfirmedAt`, and emits a
  `LeaseEventType.RegistrationConfirmed` event. `[DisableConcurrentExecution]` behaviour is covered.

- **AC4 (`ESignWebhookJob`)**: A valid payload calls `HandleESignEventAsync`; **all-signed** →
  lease `Signed` + `SignedPdfStoragePath` set + `AllPartiesSigned` event; **partial** →
  `PartySignedDocument` event; **unknown session** → no-op (logs, no throw, no `UpdateAsync`).

- **AC5 (`LeaseSignStatusPollingJob`)**: Test asserts the **current** behaviour (it logs leases in
  `AwaitingSignature`) and explicitly documents the `TODO(#177)` active-poll gap as a **known, tracked
  limitation**. This spec **flags** it; it does **not** implement the poller.

- **AC6 (webhook signatures)**: `/webhooks/esign` — valid hex HMAC-SHA256 → `200` + `ESignWebhookJob`
  enqueued; **missing** signature header → `401`; **non-hex** header → `401`; **wrong secret** → `401`
  (constant-time `FixedTimeEquals`). Regression: `/webhooks/stripe` with an invalid signature → `400`.

- **AC7 (GDPR retention / erasure)**: Assert `CreateDraftAsync` sets
  `DataRetentionUntil == StartDate.AddYears(10)` and `RegistrationDeadline == StartDate.AddDays(30)`.
  An erasure request sets `ErasureRequested = true` and emits `LeaseEventType.ErasureRequested`; after
  erasure, `Party` PII is scrubbed/excluded per `GdprService` rules and no PII leaks in serialized output.

- **AC8 (receipt integrity)**: `GET /{id}/registration/receipt` before `Registered` → error/`404`
  ("Receipt is not available yet."); when `Registered` → a non-empty `application/pdf` stream;
  `ReceiptStoragePath` is honoured when populated.

- **AC9 (operator-attended RLI — ties to `spec-ltr-rli-registration`)**: Assert RLI submission occurs
  **only** via the explicit operator action `POST /{id}/registration`; there is **no** code path that
  auto-submits a registration (the polling job is **read-only** — it polls, never submits). A second
  submission for the same lease is rejected (`400`, "already been submitted").

- **AC10 (coverage)**: Meet the project targets (`docs` / `domain-context.md › testing-landscape`):
  critical create→register→receipt path **100%**, `LeaseWorkflowService` **≥ 80%**, `LeasesController`
  **≥ 70%**. The PR reports the measured numbers.

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC11 (Playwright E2E)**: `e2e/leases.spec.ts` (demo mode) navigates the long-term layer →
  creates a lease → advances the workflow stepper across statuses (provider responses stubbed) →
  downloads the receipt, and asserts `fiscalCode` masking in the list. (Exercises `spec-ltr-frontend`.)

- **AC12 (Vitest)**: Component/hook tests assert `use-leases` query/mutation behaviour and that the
  workflow stepper renders the correct enabled action per `LeaseStatus`.

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



1. Enter the primary route for `ltr-verification`

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

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — Files to create / modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `Casazen.Tests/Integration/LeasesControllerIntegrationTests.cs` | **CREATE** — full flow + RBAC + owner-scoping + receipt (AC1, AC2, AC8) |
| `Casazen.Tests/Unit/Jobs/LeaseRegistrationStatusPollingJobTests.cs` | **CREATE** — AC3 |
| `Casazen.Tests/Unit/Jobs/ESignWebhookJobTests.cs` | **CREATE** — AC4 |
| `Casazen.Tests/Unit/Jobs/LeaseSignStatusPollingJobTests.cs` | **CREATE** — AC5 (asserts logging + documents `#177` gap) |
| `Casazen.Tests/Integration/WebhookSignatureTests.cs` | **CREATE** — `/webhooks/esign` + `/webhooks/stripe` signature paths (AC6) |
| `Casazen.Tests/Unit/Services/LeaseGdprRetentionTests.cs` | **CREATE** — retention dates + erasure event (AC7) |
| `Casazen.Tests/Unit/Services/LeaseWorkflowServiceTests.cs` | **MODIFY** — add erasure/retention/receipt-availability cases (EXISTS, 17 tests) |
| `Casazen.Tests/Integration/CasazenWebApplicationFactory.cs`, `TestAuthHandler.cs` | **MODIFY** — seed `LongTermLandlord` role + `long-rent` context membership (EXISTS) |
| `Casazen.Web/Controllers/LeasesController.cs` | **VERIFY (no change)** — reference only |
| `Casazen.Infrastructure/Services/LeaseWorkflowService.cs` | **VERIFY (no change)** — reference only |
| `Casazen.Web/BackgroundJobs/Lease*Job.cs`, `ESignWebhookJob.cs` | **VERIFY (no change)** — reference only |
| `Casazen.Web/Controllers/WebhooksController.cs` | **VERIFY (no change)** — reference only |

### Frontend — Files to create / modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `e2e/leases.spec.ts` | **CREATE** — Playwright happy path (AC11) |
| `src/features/leases/__tests__/lease-workflow.test.tsx` | **CREATE** — Vitest stepper/hooks (AC12) |

> **Note**: discovered production defects are tracked as **separate issues**, not patched in this spec
> (e.g. the `LeaseSignStatusPollingJob` active-poll gap, `TODO(#177)`).

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Lease `DataRetentionUntil` / erasure correctness** (GDPR Art. 17) — verified by AC7.
- **Receipt integrity** — verified by AC8.
- **Operator-attended RLI confirmation** — verified by AC9; ties to `spec-ltr-rli-registration`
  (Openapi.it = filing channel; CasaZen ≠ *intermediario abilitato*).
- **No real PII in fixtures** — synthetic test data only (matching the existing
  `RSSMRA80A01H501Z` style fixtures).

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**: the full LTR subsystem — `LeaseContract` / `LeaseRegistration` / `Party` / `LeaseEvent`, `LeasesController`, `LeaseWorkflowService`, the 3 lease jobs, `WebhooksController`, `OpenapiLeaseRegistrationProvider` — all **EXIST**; the integration harness (`CasazenWebApplicationFactory`, `TestAuthHandler`) **EXISTS**.
- **Blocks**: LTR GA / release sign-off (this is the assurance gate).
- **Related**: `spec-ltr-frontend` (its UI is exercised by the E2E), `spec-ltr-rli-registration` (operator-attended assertion, AC9), `spec-ltr-recurring-rent` (its `RentChargeJob` coverage is added once that ledger lands).
- **Does not modify**: production LTR code paths — this spec is **test-only**; defects route to new issues.

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
