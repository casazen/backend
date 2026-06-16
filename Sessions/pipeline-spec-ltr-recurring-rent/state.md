# Pipeline: LTR Recurring Rent (US-007)

## Status
- status: escalated
- current_stage: 03-development
- last_updated: 2026-06-11T21:30:00Z
- harness_iteration: 3 (max)

## Artifacts
- issue: #269 — https://github.com/casazen/backend/issues/269
- branch: feature/269-ltr-recurring-rent
- design_spec: Sessions/design-269.md
- pr_backend: (not opened — gates failed iteration 3)
- pr_frontend: N/A (API contract only AC12)

## Stage History
| Stage | Status | Notes |
|---|---|---|
| 01-planning | completed | Issue #269 |
| 02-design | completed | design-269.md |
| 03-development | escalated | Max 3 iterations — cross-pipeline workspace pollution (#230/#271) |
| 04-review | pending | Blocked until Stage 03 gates pass |
| 05-release | pending | |
| 06-operations | pending | |

## Gate Report (Stage 03 — iteration 3)

### Backend
| Gate | Result | Notes |
|---|---|---|
| G1 dotnet test | FAIL | Could not stabilize build; 520 tests passed once before branch pollution |
| G2 dotnet format | PARTIAL | Passed when build succeeded |
| G3 dotnet build /warnaserror | FAIL | #230 billing artifacts (BillingController, PlatformInvoice, StripeBillingService) repeatedly reintroduced on shared workspace |
| G4 migration script | PARTIAL | `AddRentLedger` migration created once; empty Up() then superseded by branch churn |

### Frontend (N/A — AC12)
| Gate | Result | Evidence |
|---|---|---|
| G5-G9 | N/A | Design scopes FE out; frontend repo on `feature/271-onboarding-plg` — zero #269 rent UI files |
| G9 E2E | N/A | No new AC-driven E2E for #269 (UI deferred to spec-ltr-frontend) |

### Compliance
| Gate | Result |
|---|---|
| G10 CIN | N/A |
| G11 secrets | PASS |
| G12 GDPR | N/A |
| G13 tourist tax | N/A |

## Escalation
Concurrent pipelines (#230 SaaS billing, #271 onboarding) on the same backend checkout caused repeated branch switches (`feature/230-saas-billing`, `feature/271-onboarding-plg`) and overwrote #269 rent implementation (`RentSchedule`, `StripeWebhookHandler`, `AppDbContext`).

**Recommended unblock:** Run Stage 03 on an isolated `feature/269-ltr-recurring-rent` worktree with other pipelines paused; then re-run harness.

## Implementation checklist (designed, not committed)
- RentSchedule / RentLedgerEntry + enums
- IRentBillingService / RentBillingService / RentChargeJob (06:00 UTC)
- LeasesController `/api/leases/{id}/rent/*` (5 endpoints)
- CreateConnectPaymentIntentAsync (Connect MoR, ApplicationFeeAmount=0)
- StripeWebhookHandler `metadata.kind=rent-charge` on Connect only (AC11)
- EF migration AddRentLedger + rent.read/rent.manage RBAC
