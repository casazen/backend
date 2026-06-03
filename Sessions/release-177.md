# Release — Issue #177 / PR #84

**Stage**: 05 Release  
**Date**: 2026-06-02  
**PR**: https://github.com/casazen/frontend/pull/84 (merged)  
**Epic**: #165

---

## Phase A — CI Validation

| Gate | Status |
|---|---|
| G1 All CI checks green | ✅ E2E pass (run 26848722264) |
| G2 Docker build | N/A — frontend only |
| G3 Semver tag | ✅ v0.1.0 |
| G4 Branch up to date | ✅ Squash merged to main |

---

## Phase B — Test Environment

| Gate | Status | Notes |
|---|---|---|
| G5 Railway test health | ⏭️ Skipped | FE-only release; BE lease API on test env assumed from #165 track |
| G8 Vercel preview | ✅ | Auto-deploy on PR; production follows main merge |

---

## Phase C — Human Validation

User authorized Stage 05 proceed. FE acceptance criteria verified via:
- Unit tests 72/72
- E2E 3/3 (pricing + lease routes unaffected)
- Manual review artifact `Sessions/review-177.md`

---

## Phase E — Production Promotion

| Step | Status |
|---|---|
| Squash merge PR #84 | ✅ |
| Tag v0.1.0 | ✅ |
| GitHub Release | ✅ |
| Vercel prod deploy | ✅ Auto from main |

---

## Release contents

- Long-term lease UI (`/leases`, `/leases/new`, `/leases/:id`)
- LongTermLandlord role guard on routes and sidebar
- APE pre-validation, signing panel, registration polling, receipt download
- E2E fix: pricing adapter toasts + stable Playwright assertions

---

## Handoff → Stage 06

Completed 2026-06-02. See `Sessions/ops-report-2026-06-02.md`.

- BE v1.0.0 merged (PR #178); FE v0.1.0 live on Vercel
- Railway BE deploy still pending (404 on prod/test URLs)
- Epic #165 closed; sub-issues #167/#174/#177 still open — housekeeping required
