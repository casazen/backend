# Catalog — attempt 7

Scope: `GJ-001` AC1–AC16. Code roots: backend, frontend, mobile. Branch: `develop`. Maps artifacts only.

## Spec list

| Field | Value |
|---|---|
| id | GJ-001 |
| slug | golden-journey-e2e |
| AC ids | AC1–AC16 |

## Implemented harness

| Artifact | Present |
|---|---|
| frontend/e2e/golden-journey-web.spec.ts | Yes — L3 steps 1–12 against `http://localhost:5000/api`; SR create includes `bookingId`; seed `e2e/.auth/gj-seed.json` |
| frontend/e2e/golden-journey-supplier-mobile.spec.ts | Yes — F1–F2 must click take + complete on a Richiesto SR |
| mobile/e2e/m1-calendar.yaml … m7-checkout.yaml | Yes — M2–M7 open L3 booking via `casazen://bookings/${BOOKING_ID}`; required status asserts |
| backend `GET /api/service-requests?bookingId=` | Yes — host list filters by booking |
| Sessions/golden-journey-runbook.md | Yes |

## Mapping

| Spec | Artifact |
|---|---|
| AC1–AC5, AC14 | `golden-journey-web.spec.ts` L3 |
| AC6–AC12 | `mobile/e2e/m1`–`m7.yaml` + L3 seed |
| AC9 | M4 required `Richiesto` after `Invia richiesta` |
| AC10 | M5 required `Pagato`; list filtered by booking |
| AC11 | M7 `Check-out rapido` + `Nessun badge critico` |
| AC13 | F1–F2 required take + complete + GET Completato |
| AC15 | frontend workflow + backend pointer |
| AC16 | `Sessions/golden-journey-runbook.md` |
