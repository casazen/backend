# Final verdict — attempt 1

STATO: GOAL_NON_RAGGIUNTO

## Re-run of original plan

| Scenario | Result |
|---|---|
| S0 health + FE | Pass — API 200, FE 200 |
| S1 harness files | Pass after fixes — runbook, both `e2e-golden-journey.yml`, `golden-journey-supplier-mobile.spec.ts`, L3 describe in `golden-journey-web.spec.ts` |
| S2–S5 API | Pass — 404 / 401 / 201 register (from audit) |
| S6 L3 12-step Playwright | Not fully executed — `E2E_LOCAL=1` + Auth0 setup not completed in this pass; L2 demo run failed because Vite was already bound to the real API (not `dev:demo`) |
| S7 F1–F2 | File exists; live take/complete needs supplier Auth0 session |
| S8 M1–M7 | Fail / BLOCKED — `maestro` CLI missing |
| S9 AC14 | Asserts added in L3 file; live compare not executed |
| S10 AC15 | Workflow files exist |

## Discrepancy final states

| Id | State |
|---|---|
| D-AC16 | APPROVED |
| D-AC15 | APPROVED |
| D-AC1 / D-AC5 | APPROVED (artifact) — live L3 run incomplete |
| D-AC13 | APPROVED (artifact) — live F1–F2 needs supplier session |
| D-AC14 | APPROVED (artifact) |
| D-AC6 | APPROVED (yaml points at real API) |
| D-M-LIVE | BLOCKED |

## Missing for GOAL_RAGGIUNTO

- Install Maestro + run M1–M7 against the same `casazen_dev` seed
- Run `E2E_LOCAL=1 npm run test:e2e:local -- golden-journey-web` with Auth0 storageState
- Optional supplier Auth0 user for steps 8–9 / F1–F2 mutations
