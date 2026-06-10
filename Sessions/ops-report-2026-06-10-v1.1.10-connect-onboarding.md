# Operations Report — 2026-06-10

**Environment**: production (main)  
**Release**: v1.1.10  
**Issue**: [#224](https://github.com/casazen/backend/issues/224) — Stripe Connect onboarding  
**Prod BE**: https://casazen-api.up.railway.app  
**Prod FE**: https://casazen-app.vercel.app

---

## Gate status (G1–G9)

| # | Gate | Status | Notes |
|---|---|---|---|
| G1 | Prod API health | ✅ | `/api/health` → 200 |
| G2 | Prod FE health | ✅ | `casazen-app.vercel.app` → 200, `id="root"` |
| G3 | CIN format (prod DB) | ⚠️ N/A | No Property schema changes in v1.1.10; prior release clean |
| G4 | GDPR retention (prod DB) | ⚠️ N/A | No Guest entity changes; Connect stores capability flags only |
| G5 | Alloggiati jobs (prod) | ⚠️ N/A | No Alloggiati changes in release |
| G6 | Tourist tax rates (prod DB) | ⚠️ N/A | No pricing/tax changes |
| G7 | Error rate (prod logs) | ✅ | No 5xx on auth smoke; prod-deploy-smoke green |
| G8 | OTA sync (prod DB) | ⚠️ N/A | No OTA adapter changes |
| G9 | Feature AC spot-check | ✅ | Connect routes live; prod-deploy-smoke 2/2 |

---

## Production health

| Check | Result |
|---|---|
| API health | ✅ 200 |
| Auth gates | ✅ 401 on `/api/properties`, `/api/bookings`, `/api/users/me`, `/api/me/contexts` |
| Connect ingress | ✅ `POST /api/connect/account` → 401 without JWT (route deployed) |
| Connect webhook route | ✅ `POST /webhooks/stripe/connect` registered (signature required) |
| FE SPA | ✅ `casazen-app.vercel.app` |
| Migrations | ✅ `AddConnectStatusFields` applied to `casazen_prod` pre-release |
| Public read-model regression | ✅ `GET /api/properties/search` → 200 (empty array) |

---

## Feature verification (Issue #224 ACs)

| AC | Prod check | Result |
|---|---|---|
| AC1–AC3 | Connect API endpoints respond (401 unauthenticated) | ✅ Deployed |
| AC4 | Webhook route `/webhooks/stripe/connect` | ✅ Deployed — **requires** `Stripe:ConnectWebhookSecret` in Railway |
| AC5 | Charge gate for checkout | ⏳ Deferred to `spec-direct-checkout` |
| AC8–AC10 | FE payments page | ✅ Bundle includes `payments-page` chunk; route `/app/short-rent/settings/payments` |

---

## Compliance (regulatory-monitor)

| Area | Status | Notes |
|---|---|---|
| MoR = operator (AD-5) | ✅ | CasaZen stores only account id + capability flags |
| KYC delegation | ✅ | Stripe-hosted onboarding; no bank/PII persisted |
| GDPR | ✅ | `Org.Connect*` fields are non-PII capability metadata |
| RF2 webhook separation | ⚠️ Action | Configure Connect webhook secret in Railway prod |

---

## Operations (incident-responder)

| Area | Status | Notes |
|---|---|---|
| Deploy | ✅ | Railway prod + Vercel prod healthy post v1.1.10 |
| Regression | ✅ | `prod-deploy-smoke` 2/2 pass |
| Branch alignment | ✅ | `main` @ `b64490d` (BE), `5d304a4` (FE) |

---

## Action items

| Priority | Item | Owner |
|---|---|---|
| P1 | Set `Stripe:ConnectWebhookSecret` in Railway prod + register `account.updated` in Stripe Dashboard → `/webhooks/stripe/connect` | Ops |
| P2 | Fix `scripts/release-smoke.ps1` parse error (line 97) | SDLC |
| P3 | Manual smoke: operator completes Stripe test-mode onboarding on prod FE | QA |

---

## Verdict

**Compliance**: ✅ (no new regulatory surface; Connect KYC delegated to Stripe)  
**Operations**: ✅ (production healthy; feature deployed)

**Pipeline**: `spec-connect-onboarding` — **COMPLETE** (6/6 stages)
