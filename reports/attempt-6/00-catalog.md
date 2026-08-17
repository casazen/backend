# Catalog — attempt 6 (verification after env unblock)

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
| frontend/e2e/golden-journey-web.spec.ts | Yes — L2 demo skipped when `E2E_LOCAL=1`; L3 walks steps 1–12 against `http://localhost:5000/api` |
| frontend/e2e/golden-journey-supplier-mobile.spec.ts | Yes — F1–F2, viewport 375×812 |
| mobile/e2e/m1-calendar.yaml … m7-checkout.yaml | Yes — `appId: it.casazen.host`, `EXPO_PUBLIC_API_URL=http://localhost:5000` |
| backend + frontend `.github/workflows/e2e-golden-journey.yml` | Yes — backend pointer; frontend owns Playwright/Maestro jobs |
| Sessions/golden-journey-runbook.md | Yes |

## Mapping

| Spec | Artifact |
|---|---|
| AC1–AC5, AC14 | `golden-journey-web.spec.ts` L3 describe |
| AC6–AC12 | `mobile/e2e/m1`–`m7.yaml` |
| AC13 | `golden-journey-supplier-mobile.spec.ts` |
| AC15 | frontend workflow + backend pointer |
| AC16 | `Sessions/golden-journey-runbook.md` |
