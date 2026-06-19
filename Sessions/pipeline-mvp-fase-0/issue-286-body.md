## Summary

**MVP Fase 0 epic** (4–5 weeks part-time). Reproduce Golden Journey on staging, file fixes, deliver spikes and design brief. Outcome: GJ executable on staging **through step 4**; prioritized fix list for Fase 1.

**Planning:** `Sessions/PLANNING.md` § Fase 0

## User Story

As a product owner, I want CasaZen staging to run Golden Journey steps 1–4 reliably and document spikes/ADRs for Fase 1 so that we can commit to the MVP roadmap with known risks retired.

## Acceptance Criteria

- [ ] Given staging deploy with seed data, when a host runs Golden Journey steps 1–4 manually (runbook), then each step completes without HTTP 500 and UI blockers.
- [ ] Given Pre-F0 blocking issues #282–#285, when their PRs merge to develop, then calendar, billing gate, public booking prod routing, and admin org onboarding pass smoke checks on staging.
- [ ] Given spike issues #287–#289, when ADRs and Expo scaffold land on develop, then `docs/adr/` contains domain + iCal decisions and Expo app builds on iOS/Android simulator.
- [ ] Given design issue #290, when moodboard + template 1 brief is delivered, then `Sessions/design-public-site-brief.md` exists and is linked from the issue.
- [ ] Given harness issue #301, when skeleton Playwright spec merges, then `e2e/golden-journey-web.spec.ts` exists with steps 1–4 stubbed and runnable in demo/CI mode.

## Technical Notes

**Affected components**: FE calendar routes, onboarding/org context, public booking Vercel routing, admin onboarding flow; docs/adr; optional Expo monorepo scaffold; Playwright e2e harness (FE repo)
**EF Core migration required**: No — Fase 0 is hardening + spikes; schema changes deferred to Fase 1 (iCal, compliance)
**OTA platforms affected**: None (iCal ADR only — no adapter implementation in F0)
**Background jobs**: None in F0 (iCal sync job scoped to Fase 1 per ADR)
**External services**: Auth0 (onboarding fix), Stripe (billing gate), Vercel (public booking deploy) — configuration/routing fixes only
**Complexity**: L — multi-track epic coordinating 9 child issues + manual GJ audit
**Technical risks**: Pre-F0 PR #305/#152 must merge before GJ step 4; ADRs may surface Fase 1 scope creep if not time-boxed

## Child workstreams

| Track | Issue |
|---|---|
| Blocking fixes | #282 calendar loop, #283 billing gate, #284 public booking prod, #285 admin org |
| GJ audit | Manual runbook + baseline before Fase 1 |
| Spikes | #287 Expo scaffold, #288 ADR custom domain, #289 ADR iCal parser |
| Design | #290 public site moodboard + template 1 |
| Harness | #301 golden-journey-e2e skeleton (steps 1–4) |

## Spec index

`Sessions/specs/spec-golden-journey-e2e.md`, `spec-ical-calendar-sync.md`, `spec-compliance-wizards.md`, `spec-guest-check-in-portal.md`
