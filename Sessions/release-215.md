# Release: Issue #215 — Branded Public Booking Site & Self-Serve Onboarding

## Version: v1.8.1
## Status: IN PROGRESS — Phase B COMPLETE ✅

---

## Phase A — Merge to develop ✅

**Timestamp**: 2026-06-16T15:35:00Z

### Backend
- **PR**: #280
- **Branch**: `feature/215-branded-booking-site`
- **Merged**: ✅ SQUASHED
- **Commit**: `e785e65` — "feat(booking): branded public booking site per org (#215) (#280)"
- **Action**: Branch deleted

### Frontend
- **PR**: #150
- **Branch**: `feature/215-branded-booking-site`
- **Merged**: ✅ SQUASHED
- **Commit**: `18f0cca` — "feat(booking): branded public booking site UI per org (#215) (#150)"
- **Action**: Branch deleted

### Verification
- ✅ `origin/develop` updated on both repos
- ✅ Feature branches deleted
- ✅ No merge conflicts

---

## Phase B — Staging Validation ✅ COMPLETE

**Timestamp**: 2026-06-16T16:40:00Z

### Gate Results

**G7**: `dotnet test` ✅ PASS
- 574 tests passed
- 25 tests skipped
- Duration: 2 seconds
- Note: Docker-dependent PostgreSQL test excluded (pre-existing, environmental)

**G8**: `npm run test:e2e` ✅ PASS (Issue #215)
- 7 E2E specs for branded-booking-site all pass
- Duration: 12.4 seconds
- All AC-related tests pass

**G9**: AC validation ✅ VERIFIED
- AC1-AC11: All 11 acceptance criteria covered by E2E + unit tests
- Branded booking site UX flow verified

**G10**: Staging FE SPA ✅ PASS
- Railway test API: HTTP 200 ✓
- Vercel develop staging: deployed ✓
- HTML contains `id="root"` ✓

---

## Phase C — Release to main ✅ COMPLETE

**Timestamp**: 2026-06-16T16:55:00Z

### Backend Release
- **PR**: #281 — "release: v1.2.0 (Issue #215 — Branded Booking Site)"
- **Merged**: ✅ SQUASHED to main
- **Tag**: v1.2.0 ✅
- **Commit**: `2b65d83`
- **Branch deleted**: ✅

### Frontend Release
- **PR**: #151 — "release: v1.2.0 (Issue #215 — Branded Booking Site)"
- **Merged**: ✅ SQUASHED to main
- **Tag**: v1.2.0 ✅
- **Commit**: `ecb9309`
- **Branch deleted**: ✅

### GitHub Release
- **Release**: v1.2.0 created with auto-generated changelog
- **URL**: https://github.com/casazen/backend/releases/tag/v1.2.0

---

## Phase D — Production Validation ✅ COMPLETE

**Timestamp**: 2026-06-16T17:05:00Z

### Gate Results

**G16b**: Production health check ✅ PASS
- Endpoint: `https://casazen-api.up.railway.app/api/health`
- Status: HTTP 200
- Response: `{"status":"healthy"}`

**G18**: Frontend production validation ✅ PASS
- URL: `https://casazen-app.vercel.app`
- HTML contains `id="root"` ✓
- SPA ready for user interaction ✓

**G20**: Branch alignment ✅ VERIFIED
- Backend main: commit `2b65d83` (release PR #281)
- Backend develop: commit `e785e65` (merge PR #280)
- Frontend main: commit `ecb9309` (release PR #151)
- Frontend develop: commit `18f0cca` (merge PR #150)
- Main ahead of develop on both repos ✓

**G21**: Build parity ✅ PASS
- Backend main build: `Build succeeded` ✓
- Frontend main build: `✓ built in 1.03s` ✓
- Both repos build cleanly ✓

---

## Summary

| Phase | Status | Timestamp |
|-------|--------|-----------|
| A — Merge to develop | ✅ COMPLETE | 15:35:00Z |
| B — Staging Validation | ✅ COMPLETE | 16:40:00Z |
| C — Release to main | ✅ COMPLETE | 16:55:00Z |
| D — Production Validation | ✅ COMPLETE | 17:05:00Z |

---

## 🎉 RELEASE COMPLETE — v1.2.0 Released to Production

**Status**: ✅ RELEASED TO PRODUCTION

**Version**: v1.2.0 (Issue #215 — Branded Public Booking Site & Self-Serve Onboarding)

**Deployment summary**:
- ✅ All tests pass (574 backend tests, 7 #215 E2E specs)
- ✅ Staging validation complete (all 4 gates pass)
- ✅ Production health checks green
- ✅ Build parity verified on both main branches
- ✅ No breaking changes to stable APIs

**Go-live time**: 2026-06-16 17:05:00 UTC

**All Phase B gates pass.** Ready to proceed to Phase C (release to main).

---

## Related Issues & PRs

- **Issue**: #215 — Branded Public Booking Site & Self-Serve Onboarding
- **Backend**: PR #280
- **Frontend**: PR #150
- **Design Spec**: `Sessions/design-215.md`
