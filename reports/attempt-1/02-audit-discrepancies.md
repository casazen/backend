# Audit discrepancies — attempt 1

Executed against local stack 2026-08-15. API `http://localhost:5000`, FE `http://localhost:5173`, DB `casazen_dev`. No production URLs. No mocks.

## Passed (not discrepancies)

| Scenario | Evidence |
|---|---|
| S0 API health | `GET /api/health` STATUS=200 `{"status":"healthy"}` |
| S0 FE | `GET http://localhost:5173/` STATUS=200 |
| S2 PE2E-BOOK missing slug | `GET /api/public/orgs/no-such-gj-slug` STATUS=404 |
| S3 calendar unauthenticated | `GET /api/bookings/calendar` STATUS=401 (not 500) |
| S5 auth gate | `GET /api/properties` STATUS=401 (not 500) |
| S4 step 1 register | `POST /api/suppliers/register` STATUS=201 `orgId=3f0ef250-8af8-4e67-8cc0-e586440cc760` `authRedirectUrl=/supplier/activation` |

## Discrepancies

### D-AC1

- **spec:** AC1 — `e2e/golden-journey-web.spec.ts` executes steps 1–12 in order on real staging/local seed, no manual DB edits
- **observed:** File exists but is L2 demo: `mockBrandedBookingApi`, steps 3–4 only
- **evidence:** `frontend/e2e/golden-journey-web.spec.ts` imports `branded-booking-mock`; describe comment “Fase 0 batch”; `playwright.config.ts` keeps this file out of `local` L3 project
- **severity:** blocker

### D-AC13

- **spec:** AC13 — `e2e/golden-journey-supplier-mobile.spec.ts` F1–F2 mobile viewport
- **observed:** File does not exist
- **evidence:** `Test-Path .../golden-journey-supplier-mobile.spec.ts` = False
- **severity:** blocker

### D-AC6

- **spec:** AC6 — M1–M7 against the same backend seed as AC1
- **observed:** `m1`–`m7` yaml exist with `EXPO_PUBLIC_E2E_DEMO=1`; no `golden-journey-host-app.e2e.ts`; `maestro` CLI not installed
- **evidence:** `maestro --version` → MISSING; yaml env `EXPO_PUBLIC_E2E_DEMO=1`
- **severity:** blocker

### D-AC15

- **spec:** AC15 — CI workflow runs web suite on every PR; app suite on main/nightly/`e2e-app`
- **observed:** `e2e-golden-journey.yml` absent; frontend `e2e.yml` is a no-op echo
- **evidence:** `Test-Path` workflow = False (backend and frontend)
- **severity:** major

### D-AC16

- **spec:** AC16 — `Sessions/golden-journey-runbook.md` manual fallback
- **observed:** Path missing; only F0 steps 1–4 runbook
- **evidence:** `Test-Path backend/Sessions/golden-journey-runbook.md` = False
- **severity:** major

### D-AC14

- **spec:** AC14 — after steps 7–10, web and app GET return identical status
- **observed:** No parity test artifact
- **evidence:** catalog §4.2 AC14; no command produced a comparison
- **severity:** major

### D-AC5

- **spec:** AC5 — unique emails/slugs per CI run in the GJ web harness
- **observed:** GJ web spec uses fixed `DEMO_ORG_SLUG`
- **evidence:** import from `branded-booking-mock`
- **severity:** major

### D-M-LIVE

- **spec:** AC6–AC12 live Maestro execution
- **observed:** Cannot execute M1–M7 on this machine
- **evidence:** `maestro MISSING`
- **severity:** blocker (environment)

## Not run (blocked on Auth0 UI / missing harness)

S6 12-step L3 Playwright and S7 F1–F2 were not executable because the L3 files/projects do not include a real 12-step journey. Authenticated host UI requires `E2E_AUTH0_*` (present locally in gitignored `.env.e2e`) plus backend JWT tenant alignment — not asserted in this pass.
