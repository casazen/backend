STATO: GOAL_RAGGIUNTO

Verifier input: `01-test-plan.md`. Discrepancy list: none blocking.

## L1 (xUnit) — 58 passed (filter in 01-test-plan + IMU controller + migration order)

| # | Result |
|---|---|
| T1–T3 `RliAuthorizationGateTests` | PASS |
| T4 `LeaseRegistrationStatusPollingJobTests` | PASS — `SubmitRegistrationAsync` never |
| T5 `CedolareAdvisoryServiceTests` | PASS — config rates + disclaimer; ordinary IRPEF note |
| T6 `LeaseContractTemplateServiceTests` | PASS |
| T7 `RliExportServiceTests` | PASS — `%PDF`, no CF in filename, `RliExported` |
| T8–T9 `RliDeadlineReminderJobTests` | PASS — T-15 idempotent; extra-EU distinct |
| T10 `RliChecklistServiceTests` | PASS |
| `LeaseWorkflowServiceTests` / `ComuneImuNotificationServiceTests` / migration last-key | PASS |

## L2 (Vitest)

| # | Result |
|---|---|
| T11 `mask-fiscal-code.test.ts` | PASS |
| T12 `delega-capture-dialog.test.tsx` | PASS |
| Empty checklist `rli-checklist.test.tsx` | PASS |

## AC1–AC12

| AC | Status |
|---|---|
| AC1 | Met — delega record + 400 before provider |
| AC2 | Met — poll-only job; no auto-submit |
| AC3 | Met — unapproved regime blocks PDF; `dev-stub` approved in config |
| AC4 | Met — config-driven advisory + disclaimer |
| AC5 | Met — owner-scoped PDF export, does not file |
| AC6 | Met — checklist + Hangfire T-15/7/1/overdue |
| AC7 | Met — Questura item + distinct reminder |
| AC8 | Met as allowed stub — `Rli:FilingEnabled` default false; no live Openapi HTTP |
| AC9 | Met — ToS version + attestation on authorization |
| AC10 | Met — `RegistrationAuthorized`, `RliExported`, `DeadlineReminderSent` |
| AC11 | Met — panels on lease detail; no unattended filing CTA |
| AC12 | Met — CF masked in UI; PII not in filename/toasts/URLs |

## Out of scope (not BLOCKED)

- Counsel-final ToS wording (placeholder + "bozza / da confermare con legale")
- Live Openapi.it HTTP
- Commits/PRs
