## Summary

**MVP Fase 1 epic** (12–16 weeks). Ship sellable MVP: full Golden Journey on prod, E2E green in CI, ecosystem loop host↔supplier.

**Planning:** `Sessions/PLANNING.md` § Fase 1 · **Exit:** GJ 12-step + Maestro M1–M7 + supplier mobile F1–F2

## User Story

As a CasaZen product owner, I need a coordinated Fase 1 delivery so one beta host can complete the full Golden Journey on production with automated E2E coverage, supplier loop closed on web/mobile, and compliance/iCal blocking publish when incomplete.

## Acceptance Criteria

1. Golden Journey complete on prod with 1 beta host (12 web steps + Maestro M1–M7 + supplier F1–F2)
2. Playwright 12-step + Maestro M1–M7 green in CI on every release to `main`
3. iCal import/export blocks visible on public calendar within sync SLA (15–30 min)
4. Compliance wizards gate property publish until CIN, Alloggiati, and guest check-in prerequisites satisfied
5. Child issues #292–#301 delivered in build order with design specs and PRs merged to `develop` then `main`
6. Platform items #271, SaaS billing (#230), #273, #274 integrated or explicitly deferred with documented rationale

## Technical Notes

- **Backend:** .NET 10 API, EF Core migrations per child feature; Hangfire jobs for iCal sync; public resolve-host + custom domain middleware (US-024)
- **Frontend:** React 19 SPA, ProtectedRoute on all authenticated routes; supplier console + public site design system; Playwright E2E harness (`golden-journey-web.spec.ts`, Maestro flows)
- **Mobile:** Expo host app (US-025) in sibling `casazen/mobile` repo; Auth0 PKCE
- **OTA/background jobs:** iCal sync job (15–30 min interval); no OTA partner API in Fase 1
- **Dependencies:** Fase 0 complete (#286 batch); ADRs for iCal parser + custom domain from F0 spikes
- **Child build order:** supplier-console → micro-marketplace → (iCal, compliance, guest portal parallel) → public-site DS → custom domain → native host → SEO → GJ E2E harness

## Feature issues (build order)

1. `supplier-console-web` (US-022) — #292
2. `micro-marketplace-v0` (US-021) — #293
3. Parallel: `ical-calendar-sync` (#294), `compliance-wizards` (#295), `guest-check-in-portal` (#296)
4. `public-site-design-system` (US-023) — #297
5. `custom-domain-booking` (US-024) — #298
6. `native-host-app` (US-025) — #299
7. `seo-funnel` (US-026) — #300
8. `golden-journey-e2e` harness (GJ-001) — #301
9. Platform: #271, SaaS billing, #273/#274

## Exit criteria

- [ ] Golden Journey complete on prod with 1 beta host
- [ ] Playwright 12-step + Maestro M1–M7 green in CI
- [ ] iCal blocks visible on public calendar
- [ ] Compliance wizards gate property publish
