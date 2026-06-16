# Pipeline: Branded Booking Site (US-003) — COMPLETE

## Status: completed · Tag v1.2.0 · Issue [#215](https://github.com/casazen/backend/issues/215)

## Final Artifacts
- PR BE [#280](https://github.com/casazen/backend/pull/280) · FE [#150](https://github.com/casazen/frontend/pull/150)
- Release [v1.2.0](https://github.com/casazen/backend/releases/tag/v1.2.0)
- Review: `Sessions/review-215.md`
- Ops Report: `Sessions/ops-report-2026-06-16.md`

## Stage History

| Stage | Status | Iterations | Artifact |
|---|---|---|---|
| 01-planning | ✅ completed | 1 | Issue #215 |
| 02-design | ✅ completed | 1 | `Sessions/design-215.md` |
| 03-development | ✅ completed | 2 | PR #280 (BE), #150 (FE) |
| 04-review | ✅ completed | 2 | `Sessions/review-215.md` |
| 05-release | ✅ completed | 1 | v1.2.0 tag, main deployed |
| 06-operations | ✅ completed | 1 | `Sessions/ops-report-2026-06-16.md` |

## Release Summary

- **Version**: v1.2.0
- **Issue**: #215 — Branded Public Booking Site & Self-Serve Onboarding
- **Released**: 2026-06-16 17:05:00Z
- **Status**: ✅ LIVE IN PRODUCTION

## What to test in prod

1. Find your org slug (Admin or DB: `Orgs.Slug`, e.g. `org-auth0|xxx`)
2. Open `https://casazen-app.vercel.app/book/{your-org-slug}`
3. Verify branding, property cards, property detail, cookie banner
4. Checkout shows placeholder (payment in next spec: direct-checkout)

## Operations Audit Results

All 9 gates passed (G1–G9):
- ✅ API health: HTTP 200
- ✅ FE health: HTTP 200, id="root" present
- ✅ CIN compliance: 0 invalid codes
- ✅ GDPR retention: 0 overdue erasures
- ✅ Alloggiati jobs: 0 failures
- ✅ Tourist tax: schema ready
- ✅ Error rate: <1%
- ✅ OTA sync: no stale integrations
- ✅ Issue #215 ACs: all critical verified

**Caveat**: RLS disabled on 69 Supabase tables (pre-existing, requires remediation before Phase C sign-off).

