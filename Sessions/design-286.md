# Design — #286 MVP Fase 0 Epic (orchestration)

Epic-level design coordinating child issues #282–#285 (merged), #287–#301. Pre-F0 fixes landed via FE #152; this spec covers remaining F0 deliverables on branch `feature/286-mvp-fase-0`.

## Child issue map

| Issue | Deliverable | Repo | Status |
|---|---|---|---|
| #282–#285 | Blocking fixes (calendar, billing gate, public booking, admin org) | FE (+BE N/A) | Merged #152 |
| #287 | Expo scaffold + ADR layout | `mobile/` | This PR |
| #288 | ADR custom domain | `docs/adr/` | This PR |
| #289 | ADR iCal parser | `docs/adr/` | This PR |
| #290 | Public site design brief | `Sessions/design-public-site-brief.md` | This PR |
| #301 | GJ E2E skeleton steps 1–4 | FE `e2e/` | This PR |
| — | GJ manual runbook steps 1–4 | `Sessions/runbooks/` | This PR |

## API Contract

N/A for epic orchestration — no new endpoints in Fase 0. Child fixes use existing endpoints:

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/api/bookings/calendar` | `[Authorize]` | #282 — requires `propertyId` query param |
| GET | `/api/orgs/me/plan` | `[Authorize]` | #283 — 404 gated until org exists |
| GET | `/api/public/orgs/{slug}` | Public | #284 — public booking read model |
| POST | `/api/admin/users/{id}/onboarding` | `[Authorize]` Admin | #285 — creates org for host mode |

Future APIs (ADR only, Fase 1): `GET /api/public/resolve-host`, `PUT /api/properties/{id}/ical` — documented in ADRs #288/#289.

## Frontend Flow

### Blocking fixes (done #152)

| Route | Component | Change |
|---|---|---|
| `/app/short-rent/bookings/calendar` | `CalendarPage` | Property guard before API (#282) |
| `/onboarding`, `/app/settings/plan` | Onboarding + Plan | Org gate before billing (#283) |
| `/book/{slug}` | Public booking | Vercel routing + prod deploy (#284) |
| `/admin/onboarding` | Admin flow | Org creation for host (#285) |

### New — GJ E2E skeleton (#301)

| File | Purpose |
|---|---|
| `e2e/golden-journey-web.spec.ts` | Steps 1–4 stubbed with demo mocks; steps 5–12 `test.fixme` for Fase 1 |
| `e2e/helpers/golden-journey-mock.ts` | Shared mocks for supplier + host + guest flows |

Steps 1–4 mapping (F0 scope):

1. Supplier creation — admin or signup mock → supplier account
2. Supplier wizard → `Active` state
3. Host onboarding + property wizard → property `Active`
4. Guest direct booking on `/book/{slug}` → booking `Confirmed`

All steps assert no HTTP 500 on intercepted API calls.

### Design brief (#290)

Deliverable: `Sessions/design-public-site-brief.md` — moodboard tokens, Template 1 wireframe, LCP budget. No FE code in F0.

## Security Notes

- Pre-F0 fixes preserve `[Authorize]` on all `/api` routes; public booking uses existing anonymous read endpoints only.
- ADR #288: host-header allowlist for `resolve-host`; reject unknown hosts at edge before tenant injection.
- ADR #289: iCal import URLs stored encrypted; export tokens unguessable (Fase 1).
- Expo scaffold (#287): Auth0 PKCE only; no client secrets in app bundle; tokens in secure storage.
- No OTA API keys in F0 scope.

## Migration Plan

N/A — no schema changes in Fase 0. iCal `CalendarBlock` entity deferred to Fase 1 per ADR-002.

## GDPR Scope

N/A — F0 does not touch Guest PII fields. Guest booking step 4 uses existing checkout flow (no new fields).

## Open Questions

None — child spikes are time-boxed ADRs + scaffold only.
