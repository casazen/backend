# Spec — LTR Flow End-to-End Verification & Test Hardening (US-009)

## Overview

**Verify** the existing lease flow (create → e-sign → RLI register → receipt) and the **3 lease
background jobs** end-to-end, and **add the missing coverage**. This is a **verification /
test-hardening** spec — the goal is to **assert the shipped subsystem works** and close test gaps,
**NOT to rebuild** it. Any production defect discovered is filed as a separate issue (e.g. the known
`LeaseSignStatusPollingJob` TODO `#177`), not fixed inline here unless it is a trivial test seam.

Reference: **US-009** (Phase 1.5 — LTR Complete + Verify)
Entry stage: **Stage 02 Design**
Mode: **verify + add coverage (no rebuild)**

### What EXISTS vs what is NEW

| | Item |
|---|---|
| **EXISTS (verify)** | `LeaseWorkflowServiceTests` (17 unit tests); `LeaseContractSerializationTests`; integration harness `CasazenWebApplicationFactory` + `TestAuthHandler`; `LeasesController`; `LeaseWorkflowService`; jobs `LeaseSignStatusPollingJob` / `LeaseRegistrationStatusPollingJob` / `ESignWebhookJob`; `WebhooksController /webhooks/esign` HMAC verification |
| **NEW (tests)** | `LeasesController` integration tests (auth + context-policy + owner-scoping + status transitions + receipt); job tests for the 3 lease jobs; webhook-signature tests; GDPR retention/erasure tests; receipt-integrity tests; an explicit **operator-attended RLI** assertion; numeric coverage report |

---

## User Story

As the **CasaZen team**, before LTR goes GA we need automated proof that the lease lifecycle and its
background jobs work correctly under real auth and context-RBAC, that retention/erasure and receipts
behave, and that RLI submission is **operator-attended only** — so we can sign off the release gate
with confidence.

---

## Acceptance Criteria

### Backend

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

- **AC11 (Playwright E2E)**: `e2e/leases.spec.ts` (demo mode) navigates the long-term layer →
  creates a lease → advances the workflow stepper across statuses (provider responses stubbed) →
  downloads the receipt, and asserts `fiscalCode` masking in the list. (Exercises `spec-ltr-frontend`.)

- **AC12 (Vitest)**: Component/hook tests assert `use-leases` query/mutation behaviour and that the
  workflow stepper renders the correct enabled action per `LeaseStatus`.

---

## Technical Notes

### Backend — Files to create / modify

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

| File | Action |
|---|---|
| `e2e/leases.spec.ts` | **CREATE** — Playwright happy path (AC11) |
| `src/features/leases/__tests__/lease-workflow.test.tsx` | **CREATE** — Vitest stepper/hooks (AC12) |

> **Note**: discovered production defects are tracked as **separate issues**, not patched in this spec
> (e.g. the `LeaseSignStatusPollingJob` active-poll gap, `TODO(#177)`).

---

## Compliance

- **Lease `DataRetentionUntil` / erasure correctness** (GDPR Art. 17) — verified by AC7.
- **Receipt integrity** — verified by AC8.
- **Operator-attended RLI confirmation** — verified by AC9; ties to `spec-ltr-rli-registration`
  (Openapi.it = filing channel; CasaZen ≠ *intermediario abilitato*).
- **No real PII in fixtures** — synthetic test data only (matching the existing
  `RSSMRA80A01H501Z` style fixtures).

---

## Dependencies

- **Requires**: the full LTR subsystem — `LeaseContract` / `LeaseRegistration` / `Party` / `LeaseEvent`, `LeasesController`, `LeaseWorkflowService`, the 3 lease jobs, `WebhooksController`, `OpenapiLeaseRegistrationProvider` — all **EXIST**; the integration harness (`CasazenWebApplicationFactory`, `TestAuthHandler`) **EXISTS**.
- **Blocks**: LTR GA / release sign-off (this is the assurance gate).
- **Related**: `spec-ltr-frontend` (its UI is exercised by the E2E), `spec-ltr-rli-registration` (operator-attended assertion, AC9), `spec-ltr-recurring-rent` (its `RentChargeJob` coverage is added once that ledger lands).
- **Does not modify**: production LTR code paths — this spec is **test-only**; defects route to new issues.
