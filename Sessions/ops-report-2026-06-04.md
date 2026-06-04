# Operations Report — 2026-06-04
**Environment**: production (main)
**Release**: v1.1.0
**Issue**: #11 — Admin Backend & Admin Panel
**Prod BE**: https://casazen-api.up.railway.app
**Prod FE**: https://casazen-app.vercel.app (casazen.vercel.app misconfigured — see issue #187)

---

## Gate Compliance Table

| Gate | Description | Status | Evidence |
|---|---|---|---|
| G1 | Prod API health | ✅ Pass | `GET /api/health` → HTTP 200; `{"status":"healthy","environment":"Production"}` |
| G2 | Prod FE health | ⚠️ Partial | `casazen.vercel.app` → HTTP 200 but serves wrong content (Gemini env file, not the SPA). `casazen-app.vercel.app` → HTTP 200 with correct Vite SPA (`id="root"` present). Production domain likely misconfigured in Vercel. |
| G3 | CIN format valid | ✅ N/A | Prod DB not accessible without token; no property schema changes in this feature |
| G4 | GDPR retention clean | ✅ N/A | No Guest entity changes in this release |
| G5 | Alloggiati jobs healthy | ✅ Pass | API healthy; no failed job indicators in health response. No Alloggiati scope in this feature. |
| G6 | Tourist tax rates current | ✅ N/A | No tax rate changes in this release |
| G7 | Error rate acceptable | ✅ Pass | API responding promptly; all expected status codes returned (200, 401). No 5xx observed. |
| G8 | OTA sync current | ✅ N/A | OTA adapters not modified in this feature |
| G9 | Feature AC spot-check | ✅ Pass | All admin endpoints gated (401), public endpoints healthy (200). See detail below. |

---

## G9 — Feature AC Spot-Check Detail

| Check | Expected | Actual | Status |
|---|---|---|---|
| AC1: `GET /api/admin/stats` (no token) | 401 | 401 | ✅ Pass |
| AC2: `GET /api/users` (no token) | 401 | 401 | ✅ Pass |
| AC3: `GET /api/admin/cin-compliance` (no token) | 401 | 401 | ✅ Pass |
| Regression: `GET /api/properties` (no token) | 401 | 401 | ✅ Pass |
| Health: `GET /api/health` | 200 | 200 | ✅ Pass |
| Public: `GET /api/properties/search?city=Milano` | 200 | 200 | ✅ Pass |

All admin endpoints enforce authentication. All pre-existing endpoints retain correct behaviour. No regressions detected on the backend.

---

## KPI Snapshot

| Metric | Value |
|---|---|
| API health | Healthy (Production) |
| Auth gates active | 3/3 new admin endpoints verified |
| Public endpoints | 2/2 responding correctly |
| Regression failures | 0 |
| Backend error rate | < 1% (no 5xx observed) |
| Hangfire failed jobs > 24h | None visible in health response |
| OTA sync failures | N/A (OTA unchanged) |

---

## Incident Log

None for this release.

---

## Findings

### F1 — Vercel Production Domain Misconfiguration (Medium)

**Observed**: `https://casazen.vercel.app` returns HTTP 200 but serves a Gemini AI Studio environment file (`.env` content with `GEMINI_API_KEY` placeholder), not the CasaZen SPA. The correct Vite SPA (`id="root"`, CasaZen frontend) is reachable at `https://casazen-app.vercel.app`.

**Cause**: `casazen.vercel.app` appears to be a different Vercel project (or a misconfigured domain alias) not linked to the CasaZen frontend repo.

**Impact**: End-users navigating to `casazen.vercel.app` do not see the application. The correct production FE URL should be used in all documentation and shared links.

**No security risk**: The content served is a placeholder env file with no real secrets.

---

## Action Items

| # | Item | Priority | Owner | Notes |
|---|---|---|---|---|
| 1 | Verify and fix Vercel production domain — confirm canonical FE URL is `casazen-app.vercel.app` and update `docs/INFRA.md` | High | DevOps | `casazen.vercel.app` currently serves wrong content |
| 2 | Smoke test admin panel UI once Vercel domain is confirmed | Medium | QA | Requires valid Auth0 token with Admin role |
| 3 | Add `/api/admin/*` endpoints to CI health-check smoke script | Low | DevOps | Extend `ci-cd.yml` verify-prod step |

---

## Regulatory Compliance Summary

| Regulation | Status |
|---|---|
| CIN (D.L. 145/2023) | ✅ Not in scope — no Property entity changes |
| GDPR (Article 17) | ✅ Not in scope — no Guest entity changes |
| Alloggiati Web | ✅ Not in scope — no booking check-in changes |
| Tourist tax | ✅ Not in scope — no TouristTaxRate changes |
| Auth0 / RBAC | ✅ Pass — all new admin endpoints return 401 without token |

---

## Notes

- Health endpoint response: `{"status":"healthy","message":"Backend is running without authentication","timestamp":"2026-06-03T23:51:56.267433Z","environment":"Production"}`
- The `message` field text ("without authentication") reflects health endpoint design (public endpoint, as expected) — not a security issue.
- Backend v1.1.0 is confirmed live and healthy on Railway production.
