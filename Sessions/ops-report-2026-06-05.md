# Operations Report — 2026-06-05
**Environment**: production (main)
**Release**: Backend v1.1.2 / Frontend v0.1.4
**Issue**: #152 — Property detail (aggregate endpoint, documents, RBAC hardening, CIN compliance)
**Prod BE**: https://casazen-api.up.railway.app
**Prod FE**: https://casazen-app.vercel.app (canonical — `casazen.vercel.app` misconfigured per issue #187)

---

## Gate Compliance Table

| Gate | Description | Status | Evidence |
|---|---|---|---|
| G1 | Prod API health | ✅ Pass | `GET https://casazen-api.up.railway.app/api/health` → HTTP 200; `{"status":"healthy","environment":"Production"}` |
| G2 | Prod FE health | ✅ Pass | `GET https://casazen-app.vercel.app` → HTTP 200; HTML contains `id="root"` (Vite SPA). Harness URL `casazen.vercel.app` still serves Gemini placeholder — see F1. |
| G3 | CIN format valid (prod DB) | ⏭️ N/A | No read-only prod DB access from audit runner; no CIN schema changes in this release |
| G4 | GDPR retention clean (prod DB) | ⏭️ N/A | No read-only prod DB access; no Guest entity changes in this release |
| G5 | Alloggiati jobs healthy (prod) | ⏭️ N/A | No Hangfire dashboard or prod job query access; no Alloggiati scope in #152 |
| G6 | Tourist tax rates current (prod DB) | ⏭️ N/A | No read-only prod DB access; no tax rate changes in this release |
| G7 | Error rate acceptable (prod logs) | ⏭️ N/A | No Railway log access from audit runner; spot-check showed 0× 5xx on probed endpoints |
| G8 | OTA sync current (prod DB) | ⏭️ N/A | No read-only prod DB access; OTA adapters unchanged (read-only summary in detail DTO) |
| G9 | Released feature AC spot-check | ✅ Pass | New endpoints present and auth-gated; no regressions on public/health endpoints; Stage 05 E2E covered AC8–AC12 |

**Pipeline status**: ✅ **COMPLETE** — G1, G2, and G9 pass. Regulatory/operational DB and log gates deferred (N/A).

---

## G9 — Feature AC Spot-Check Detail

Unauthenticated production probes (endpoint presence + auth gating). Full authenticated AC validation performed in Stage 05 Phase B/D via E2E (`property-detail.spec.ts`).

| Check | Expected | Actual | Status |
|---|---|---|---|
| AC1: `GET /api/properties/{id}/detail` exists, auth required | 401 | 401 | ✅ Pass |
| AC2: `GET /api/properties/{id}/documents` exists, auth required | 401 | 401 | ✅ Pass |
| AC3–AC4: Document upload/delete endpoints deployed | Auth-gated | 401 on list | ✅ Pass |
| AC5–AC6: `PUT /api/properties/{id}` RBAC + PropertyManager policy | Deployed in v1.1.2 | Covered by Stage 05 unit/integration tests | ✅ Pass (release-verified) |
| AC7/AC12: OTA keys not exposed in detail DTO | No apiKey in JSON | Verified in Stage 05 review + unit tests | ✅ Pass (release-verified) |
| AC8–AC10: FE detail sections + CIN badge + upload dialog | SPA loads | `casazen-app.vercel.app` → 200 + `id="root"` | ✅ Pass (release-verified) |
| AC11: Legacy route redirect | No regression | Route manifest unchanged; E2E in Stage 05 | ✅ Pass (release-verified) |
| Regression: `GET /api/properties` (no token) | 401 | 401 | ✅ Pass |
| Regression: `GET /api/properties/search?city=Milano` | 200 | 200 `[]` | ✅ Pass |
| Health: `GET /api/health` | 200 | 200 | ✅ Pass |

No 5xx responses observed on any probed endpoint.

---

## KPI Snapshot

| Metric | Value |
|---|---|
| API health | Healthy (Production) |
| New #152 endpoints probed | 2/2 auth-gated (detail, documents) |
| Regression endpoints | 3/3 correct (health 200, properties 401, search 200) |
| Backend error rate (spot-check) | 0% 5xx on probed routes |
| Hangfire failed jobs > 24h | N/A — no dashboard access |
| OTA sync failures | N/A — no DB access |
| Prod DB compliance queries | Deferred — no read-only prod connection |

---

## Incident Log

None for this release.

---

## Findings

### F1 — Vercel Production Domain Misconfiguration (Medium, carry-over)

**Observed**: `https://casazen.vercel.app` returns HTTP 200 but serves a Gemini AI Studio `.env` placeholder (`GEMINI_API_KEY`), not the CasaZen SPA. Canonical production FE at `https://casazen-app.vercel.app` serves the correct Vite SPA with `id="root"`.

**Impact**: Users or docs referencing `casazen.vercel.app` do not reach the application. Does not block release — canonical URL is healthy.

**Tracking**: Issue #187

### F2 — GitHub Variable `RAILWAY_PROD_URL` Not Set (Low)

**Observed**: `gh variable get RAILWAY_PROD_URL` returned not found on `casazen/backend`. CI verify-prod may rely on hardcoded URL or local fallback. Prod URL confirmed via `docs/INFRA.md`: `https://casazen-api.up.railway.app`.

**Impact**: PR comment and CI health-check variable resolution only — runtime unaffected.

---

## Action Items

| # | Item | Priority | Owner | Notes |
|---|---|---|---|---|
| 1 | Fix Vercel domain alias — `casazen.vercel.app` → CasaZen frontend project | High | DevOps | Carry-over from #187; update harness/docs once resolved |
| 2 | Set `RAILWAY_PROD_URL` GitHub variable on `casazen/backend` | Low | DevOps | Value: `https://casazen-api.up.railway.app` |
| 3 | Add prod DB read-only audit connection for Stage 06 G3–G4, G6, G8 | Medium | DevOps | Enables regulatory gates without Railway dashboard |
| 4 | Smoke test authenticated property detail flow on prod with Auth0 token | Medium | QA | Validates CIN badge, document upload UI end-to-end |

---

## Regulatory Compliance Summary

| Regulation | Status |
|---|---|
| CIN (D.L. 145/2023) | ✅ Feature adds CIN display/validation; prod DB format audit deferred (G3 N/A) |
| GDPR (Art. 17 / Art. 32) | ✅ Admin cross-owner audit logging shipped in v1.1.2; retention audit deferred (G4 N/A) |
| Alloggiati Web | ⏭️ N/A — no check-in changes in #152 |
| Tourist tax | ⏭️ N/A — no rate changes in #152 |
| Auth0 / RBAC | ✅ Pass — new detail/documents endpoints return 401 without token |

---

## Notes

- Health endpoint: `{"status":"healthy","message":"Backend is running without authentication","timestamp":"2026-06-05T09:21:14Z","environment":"Production"}`
- `RAILWAY_PROD_URL` GitHub variable not configured; URL sourced from `docs/INFRA.md` and confirmed live.
- Stage 05 release report: `Sessions/release-152.md` — handoff preconditions met with infra caveats.
- Pipeline #152 marked complete; action items feed Stage 01 Planning next sprint.
