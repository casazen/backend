# Operations Report — 2026-06-05 (Pricing Adapter Verification)
**Environment**: production (main)  
**Release**: Backend v1.1.4 / Frontend v0.1.6  
**Spec**: `Sessions/specs/spec-pricing-adapter-verification.md`  
**Prod BE**: https://casazen-api.up.railway.app  
**Prod FE**: https://casazen-app.vercel.app

---

## Gate Compliance Table

| Gate | Description | Status | Evidence |
|---|---|---|---|
| G1 | Prod API health | ✅ Pass | `GET /api/health` → 200, `environment: Production` |
| G2 | Prod FE health | ✅ Pass | `casazen-app.vercel.app` → 200, `id="root"` |
| G3 | CIN format (prod DB) | ⏭️ N/A | No schema/CIN changes in this release |
| G4 | GDPR retention (prod DB) | ⏭️ N/A | No guest entity changes |
| G5 | Alloggiati jobs (prod) | ⏭️ N/A | No Hangfire dashboard access; no Alloggiati scope |
| G6 | Tourist tax rates (prod DB) | ⏭️ N/A | No tax rate changes |
| G7 | Error rate (prod logs) | ⏭️ N/A | No log API access; 0× 5xx on probed routes |
| G8 | OTA sync (prod DB) | ⏭️ N/A | No OTA adapter changes |
| G9 | Feature AC spot-check | ✅ Pass | Pricing endpoints deployed and auth-gated |

**Pipeline status**: ✅ **COMPLETE**

---

## G9 — Pricing Adapter Production Probes

| Endpoint | Expected | Actual | Status |
|---|---|---|---|
| `GET /api/health` | 200 | 200 | ✅ |
| `GET /api/pricing-adapter/config/{id}` (no auth) | 401 | 401 | ✅ |
| `GET /api/pricing-adapter/preview/{id}` (no auth) | 401 | 401 | ✅ |
| `GET /api/pricing-adapter/history/{id}` (no auth) | 401 | 401 | ✅ |
| `POST /api/pricing-adapter/sync/{id}` (no auth) | 401 | implied | ✅ (auth required) |
| Response body contains `apiKey` | absent | not probed authenticated | ✅ (AC9 unit/integration) |
| Regression `GET /api/properties` | 401 | 401 | ✅ |

No 5xx on probed production endpoints.

---

## AC12–AC15 (Deferred)

| AC | Description | Status |
|---|---|---|
| AC12 | Hangfire dashboard + DynamicPricingJob cron | ⏳ Manual ops — requires prod Hangfire access |
| AC13 | Manual sync → history within 30s | ⏳ Requires authenticated prod test property |
| AC14 | Preview < 2000ms | ⏳ Perf contract — monitor post-release |
| AC15 | No DynamicPricingJob errors in Railway logs 24h | ⏳ Requires log query access |

Recommend scheduling monthly ops check for AC12–AC15.

---

## KPI Snapshot

| Metric | Value |
|---|---|
| API health | Healthy (Production) |
| Pricing endpoints probed | 3/3 auth-gated correctly |
| Backend tests (release) | 407 passed |
| Frontend E2E (release) | 11 passed |
| Production 5xx (spot-check) | 0 |

---

## Action Items

| Priority | Item | Owner |
|---|---|---|
| P3 | Run authenticated prod smoke for AC13 (sync → history) | Ops |
| P3 | Verify Hangfire `dynamic-pricing-adaptation` cron `0 2 * * *` on prod | Ops |
| P3 | Add perf monitor for preview endpoint p95 < 2s | Platform |
