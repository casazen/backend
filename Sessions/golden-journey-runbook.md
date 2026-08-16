# Runbook — Golden Journey (manual fallback)

**Spec:** GJ-001 (`Sessions/specs/spec-golden-journey-e2e.md`)  
**Environment:** local/dev only (`http://localhost:5000` + `http://localhost:5173`). Do not use production URLs.

Use this checklist when CI or Playwright/Maestro is unavailable. Record a short video or screenshots per step.

## Preconditions

- [ ] `GET http://localhost:5000/api/health` → 200
- [ ] Frontend at `http://localhost:5173` pointed at `VITE_API_BASE_URL=http://localhost:5000/api`
- [ ] Postgres `casazen_dev` (not InMemory)
- [ ] Unique emails/slugs: `gj-{yyyyMMddHHmmss}@mailinator.com`
- [ ] Do not assert Alloggiati `Inviato` unless Questura test credentials are configured

## Web — 12 steps

| Step | Actor | Action | Pass |
|---|---|---|---|
| 1 | Admin or supplier | Invite or `POST /api/suppliers/register` | 201; no 500; user/org visible |
| 2 | Supplier | Activation wizard → Active | `GET /api/me/contexts` includes supplier; inbox loads |
| 3 | Host | Onboarding + property + iCal + public site | Property created with org; `GET /api/public/orgs/{slug}` 200 |
| 4 | Guest | `/book/{slug}` checkout | Booking created; no 500 |
| 5 | Host | Calendar month | Events for booking + iCal; request includes `propertyId` |
| 6 | Guest + host | Guest check-in link | Session progresses; skip Alloggiati `Inviato` without Questura |
| 7 | Host | Create service request | Status `Richiesto` |
| 8 | Supplier | Presa in carico (desktop or phone) | Status `PresoInCarico` |
| 9 | Supplier | Completato | Status `Completato` |
| 10 | Host | Mark paid | Status `Pagato` |
| 11 | Host | Check-out wizard | Completes; no 500 |
| 12 | Host | Compliance cockpit | No critical red badges for this property |

Italian UI errors where the product shows validation.

## Host app — M1–M7

Same backend seed as the web run (`EXPO_PUBLIC_API_URL=http://localhost:5000`, not demo mode).

| # | Pass |
|---|---|
| M1 | Calendar shows the web booking + iCal blocks |
| M2 | Booking detail matches web guest/dates |
| M3 | Push or `casazen://bookings/{id}` opens that booking |
| M4 | Richiedi fornitore → `Richiesto` |
| M5 | After supplier web actions: `PresoInCarico` → `Completato` |
| M6 | Mark paid → `Pagato` |
| M7 | Quick check-out; no critical red badges |
| AC12 | 0 crashes |

## Supplier mobile web — F1–F2

Phone viewport (375×812) on `/app/supplier/inbox`.

| # | Pass |
|---|---|
| F1 | Presa in carico reachable; status `PresoInCarico` |
| F2 | Completato; host web/app reflect within 30s |

## Parity (AC14)

After steps 7–10, `GET /api/bookings/{id}` and `GET /api/service-requests/{id}` return the same `status` the app shows.

## Automated commands

```bash
# Frontend — L2 demo (no real API)
npm run test:e2e -- golden-journey-web

# Frontend — L3 real local API
E2E_LOCAL=1 npm run test:e2e:local -- golden-journey-web
E2E_LOCAL=1 npm run test:e2e:local -- golden-journey-supplier-mobile

# Mobile (requires Maestro + simulator)
maestro test e2e/m1-calendar.yaml
```

## Sign-off

| Role | Date | Web 1–12 | M1–M7 | F1–F2 |
|---|---|---|---|---|
| Dev | | ☐ | ☐ | ☐ |
| PO | | ☐ | ☐ | ☐ |
