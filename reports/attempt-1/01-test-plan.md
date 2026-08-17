# Test plan — Golden Journey real local stack (attempt 1)

Source: `reports/attempt-1/00-catalog.md` only.

Stack: API `http://localhost:5000`, FE `http://localhost:5173`, DB `casazen_dev`. No mocks. No production URLs.

## 1. Preconditions / env vars

| Name | Required for | Notes |
|---|---|---|
| API health | All | `GET http://localhost:5000/api/health` → 200 |
| `E2E_AUTH0_EMAIL` / `E2E_AUTH0_PASSWORD` | Authenticated UI/API (steps 1–3, 5, 7–12, PE2E-ORG) | `REQUIRES_ENV` — values live in frontend `.env.e2e` (do not copy secrets into reports) |
| `E2E_AUTH0_ADMIN_EMAIL` / `E2E_AUTH0_ADMIN_PASSWORD` | Admin invite (step 1 alt) | `REQUIRES_ENV` if unused |
| Maestro + Expo simulator | M1–M7 | `REQUIRES_ENV` — `maestro` CLI + running `casazen-host` app |
| Questura | Alloggiati `Inviato` | **Skip** — do not assert |

Auth0 tenant used by FE: `VITE_AUTH0_DOMAIN` / `VITE_AUTH0_AUDIENCE` from frontend `.env`. Backend JWT must validate that same tenant.

## 2. Seed strategy

```
RUN=gj-$(Get-Date -Format yyyyMMddHHmmss)
SUPPLIER_EMAIL=gj-sup-$RUN@mailinator.com
HOST_SLUG=gj-org-$RUN
PROPERTY_NAME=GJ Villa $RUN
```

Reuse one booking id + one service-request id for web, F1–F2, and M1–M7.

## 3. Executable scenarios

### S0 — Smoke

```
curl -s -o - -w "%{http_code}" http://localhost:5000/api/health
curl -s -o - -w "%{http_code}" http://localhost:5173/
```

Pass: API 200 `healthy`; FE 200.

### S1 — Harness file existence (AC1, AC6, AC13, AC15, AC16)

```
Test-Path frontend/e2e/golden-journey-web.spec.ts
Test-Path frontend/e2e/golden-journey-supplier-mobile.spec.ts
Test-Path mobile/e2e/golden-journey-host-app.e2e.ts
Test-Path mobile/e2e/m1-calendar.yaml
Test-Path backend/.github/workflows/e2e-golden-journey.yml
Test-Path frontend/.github/workflows/e2e-golden-journey.yml
Test-Path backend/Sessions/golden-journey-runbook.md
```

Pass: all true AND `golden-journey-web.spec.ts` contains sequential steps 1–12 against real API (no `page.route` / `mock*` on path under test).

### S2 — PE2E-BOOK public org missing slug

```
curl -s -o body.json -w "%{http_code}" http://localhost:5000/api/public/orgs/no-such-gj-slug
```

Pass: 404. FE: open `http://localhost:5173/book/no-such-gj-slug` — Italian empty/404, no 500.

### S3 — PE2E-CAL calendar without propertyId

```
curl -s -o - -w "%{http_code}" "http://localhost:5000/api/bookings/calendar"
```

Pass: 400 or 401 (not 500). Authenticated call without `propertyId` must not 500.

### S4 — Step 1 supplier register (anonymous)

```
curl -s -o - -w "%{http_code}" -X POST http://localhost:5000/api/suppliers/register -H "Content-Type: application/json" -d "{\"email\":\"$SUPPLIER_EMAIL\",\"legalName\":\"GJ Supplier $RUN\",\"phone\":\"+390612345678\",\"comuneCode\":\"058091\"}"
```

Pass: 201; org created. No 500.

### S5 — Auth gate

```
curl -s -o - -w "%{http_code}" http://localhost:5000/api/properties
```

Pass: 401 (not 500).

### S6 — Steps 2–12 + PE2E-ORG `REQUIRES_ENV` Auth0

Run Playwright against real local API (not demo, not InMemory):

```
cd frontend
$env:E2E_LOCAL=1
$env:E2E_LOCAL_API_URL="http://localhost:5000/api"
$env:E2E_BASE_URL="http://localhost:5173"
npx playwright test --project=local e2e/local-integration.spec.ts
```

Then (when the 12-step L3 file exists):

```
npx playwright test e2e/golden-journey-web.spec.ts
```

Pass: property create 201 with org context (PE2E-ORG); no `/api/` 500; Italian validation on empty property form; unique name; then steps 2–12 mutate real DB.

If Auth0 login fails or backend JWT audience/domain mismatch: record evidence, mark `REQUIRES_ENV` / fail.

### S7 — F1–F2 supplier mobile `REQUIRES_ENV`

```
npx playwright test e2e/golden-journey-supplier-mobile.spec.ts
```

Pass: file exists; mobile viewport; take → `PresoInCarico`; complete → `Completato`; host GET same status within 30s.

### S8 — M1–M7 Maestro `REQUIRES_ENV`

```
maestro --version
maestro test mobile/e2e/m1-calendar.yaml
...
maestro test mobile/e2e/m7-checkout.yaml
```

Pass: app talks to `http://localhost:5000` (not `EXPO_PUBLIC_E2E_DEMO=1`); same booking as S6; asserts in AC7–AC12.

### S9 — AC14 parity

After steps 7–10, compare `GET /api/bookings/{id}` and `GET /api/service-requests/{id}` status from the same API used by web and mobile. Pass: identical `status`.

### S10 — AC15 CI file

Pass: workflow exists and runs web suite on PR.

## 4. Shared artifacts

Write `reports/attempt-1/seed.json` during the run: `run`, `supplierEmail`, `orgSlug`, `propertyId`, `bookingId`, `serviceRequestId`, `checkInToken`.

## 5. Evidence

For each scenario: command, HTTP status, truncated body (no secrets), pass/fail. File-existence failures are discrepancies.
