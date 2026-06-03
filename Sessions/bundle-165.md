# Release Bundle — Epic #165: Long-Term Lease End-to-End Contract Lifecycle

## Status: released — BE v1.0.0 + FE v0.1.0 (Railway deploy pending)

## Features

| Issue | Repo | Branch | Test status | Prod status |
|---|---|---|---|---|
| #165 | backend | main (PR #178) | ✅ 349 tests pass | ⚠️ code released, Railway 404 |
| #177 | frontend | main (PR #84) | ✅ E2E 3/3, unit 72/72 | ✅ released v0.1.0 |

**Bundle code is complete. Production integration blocked until BE is deployed on Railway.**

---

## Test Environment URLs

- BE test: `https://casazen-api-test.up.railway.app` — **404 Application not found** (2026-06-02)
- FE preview: Vercel auto-deploy on PR; prod at `https://casazen.vercel.app` ✅

## Acceptance Criteria to Verify on Test

From Issue #165 (BE) — code verified locally, runtime pending deploy:
- [ ] `GET /api/leases` returns leases for authenticated owner
- [ ] `POST /api/leases` creates a lease contract with correct status `Draft`
- [ ] E-sign webhook updates lease status correctly
- [ ] Background job sends lease expiry reminders

From Issue #177 (FE) — verified at release:
- [x] Lease draft form renders and submits correctly
- [x] Signing flow shows correct status transitions
- [x] Registration status badge reflects backend state
- [x] PDF receipt downloads successfully

---

## Production Release

| Component | Version | Tag | Released |
|---|---|---|---|
| Backend | v1.0.0 | v1.0.0 | 2026-06-02 (GitHub Release) |
| Frontend | v0.1.0 | v0.1.0 | 2026-06-02 (GitHub Release + Vercel) |

- BE prod URL (configured): `https://casazen-api.up.railway.app` — not reachable
- FE prod: `https://casazen.vercel.app` — live

---

## Stage 06 Exit

Ops report: `Sessions/ops-report-2026-06-02.md`

**Next**: Deploy BE to Railway, run integration smoke, close stale sub-issues (#167, #174, #177).
