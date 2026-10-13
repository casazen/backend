# Catalog — Golden Journey E2E vs implemented features

Scope: `GJ-001` (`spec-golden-journey-e2e.md`, AC1–AC16), superseded `spec-production-e2e-flow-verification.md` leftover gates, and `Sessions/PLANNING.md` § Golden Journey (12 steps, M1–M7, F1–F2).

Code roots: `backend`, `frontend`, `mobile`. Branch: `develop`.

This catalog maps artifacts only. It does not assess correctness.

## 1. Spec list

### 1.1 GJ-001 — `golden-journey-e2e`

| Field | Value |
|---|---|
| id | GJ-001 |
| slug | golden-journey-e2e |
| file | Sessions/specs/spec-golden-journey-e2e.md |
| AC ids | AC1–AC16 |

**AC summary:** AC1–AC5 web Playwright 12-step; AC6–AC12 host app M1–M7; AC13 supplier mobile F1–F2; AC14 parity; AC15 CI; AC16 runbook.

**PLANNING 12 steps:** 1 supplier create → 2 activation Active → 3 host property+iCal+site → 4 guest book Confirmed → 5 calendar coherent → 6 guest check-in → 7 ServiceRequest Richiesto → 8 PresoInCarico → 9 Completato → 10 Pagato → 11 checkout wizard → 12 cockpit green.

**M1–M7:** calendar, booking detail, push/deep-link, create SR, status after supplier, mark paid, checkout summary.

**F1–F2:** mobile inbox take; mobile complete; host reflects within 30s.

### 1.2 Production E2E (superseded)

Leftover local gates: PE2E-ORG (property+org), PE2E-CAL (calendar propertyId), PE2E-BOOK (public /book/{slug}), PE2E-G1..G8.

## 2. Implemented feature list

### Named harness

| Artifact | Present |
|---|---|
| frontend/e2e/golden-journey-web.spec.ts | Yes — steps 3–4 demo + page.route mocks only |
| frontend/e2e/golden-journey-supplier-mobile.spec.ts | No |
| mobile/e2e/golden-journey-host-app.e2e.ts | No |
| mobile/e2e/m1-calendar.yaml … m7-checkout.yaml | Yes — EXPO_PUBLIC_E2E_DEMO=1 |
| .github/workflows/e2e-golden-journey.yml | No |
| Sessions/golden-journey-runbook.md | No (F0 steps 1–4 runbook exists) |
| frontend/e2e/l3/** | No on develop |

### Product APIs (real stack)

- Step 1: POST /api/suppliers/register, POST /api/admin/suppliers/invite
- Step 2: GET/POST /api/supplier/profile/activation*
- Step 3: POST /api/users/onboarding, POST /api/properties, iCal, GET /api/public/orgs/{slug}
- Step 4: POST /api/public/bookings
- Step 5: GET /api/bookings/calendar?propertyId&startDate&endDate
- Step 6: GET/POST /api/public/checkin/{token}
- Steps 7–10: POST /api/service-requests, .../take, .../complete, .../mark-paid
- Steps 11–12: POST /api/bookings/{id}/checkout-wizard/*, GET /api/compliance/summary

### FE screens

register, admin invite, supplier activation/inbox, onboarding, property create, /book/:orgSlug, calendar, /checkin/:token, marketplace, checkout-wizard, compliance summary.

### Mobile screens

calendar.tsx, bookings/[id].tsx, PushHandler, service-request.tsx, checkout.tsx.

### Adjacent L2 (not GJ harness)

admin-supplier-invite, supplier-layout (F1 smoke), onboarding, local-integration, branded-booking, calendar-property-guard, guest-checkin-portal, marketplace-suppliers, compliance-wizards.

xUnit integration tests exist (InMemory) — not GJ evidence.

## 3. 1:1 mapping

| Spec | Artifact | Note |
|---|---|---|
| AC1 file | golden-journey-web.spec.ts | Exists; body is steps 3–4 mocked |
| AC6 yaml | mobile/e2e/m1–m7.yaml | Exist; demo env, not shared seed |
| AC13 | — | File absent; closest supplier-layout F1 smoke |
| AC15 | frontend e2e.yml | No-op echo |
| AC16 | F0 runbook only | Wrong path, steps 1–4 |
| PE2E-ORG | PropertiesController + onboarding | Mapped |
| PE2E-CAL | BookingController calendar requires propertyId; FE guard | Mapped |
| PE2E-BOOK | PublicOrg + /book/:orgSlug | Mapped |

## 4. Explicit gaps

- No 12-step real-API Playwright (AC1–AC5)
- No supplier-mobile F1–F2 spec (AC13)
- Maestro yaml does not assert M1–M7 outcomes or share AC1 seed (AC6–AC12)
- No web↔app status parity test (AC14)
- No e2e-golden-journey.yml (AC15)
- No Sessions/golden-journey-runbook.md (AC16)
- No frontend/e2e/l3 GJ tests on develop
- Production evidence pack not in scope (local only)

## Live stack (coordinator note for next steps)

- API: http://localhost:5000 — GET /api/health → 200
- DB: Docker postgres:16 casazen_dev (postgres/dev), migrations applied
- FE: http://localhost:5173 — VITE_API_BASE_URL=http://localhost:5000/api
- Do not use InMemory, page.route on path under test, or production URLs
