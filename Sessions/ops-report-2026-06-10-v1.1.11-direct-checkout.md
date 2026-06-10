# Ops Report — v1.1.11 Direct Checkout (#226)

> **Date**: 2026-06-10 · **Tag**: v1.1.11 · **Pipeline**: `spec-direct-checkout` · **Environment**: production

---

## Production health

| Check | URL | Status |
|---|---|---|
| BE health | `https://casazen-api.up.railway.app/api/health` | ✅ 200 |
| FE SPA | `https://casazen-app.vercel.app` | ✅ `id="root"` |
| Auth gate | `/api/properties` (no token) | ✅ 401 |
| prod-deploy-smoke | Playwright | ✅ 2/2 |

---

## Release artifacts

| Artifact | Link |
|---|---|
| Issue | [#226](https://github.com/casazen/backend/issues/226) |
| BE PR | [#227](https://github.com/casazen/backend/pull/227) → develop |
| FE PR | [#119](https://github.com/casazen/frontend/pull/119) → develop |
| BE release | [v1.1.11](https://github.com/casazen/backend/releases/tag/v1.1.11) |
| FE release | [v1.1.11](https://github.com/casazen/frontend/releases/tag/v1.1.11) |
| Design | `Sessions/design-226.md` |
| Review | `Sessions/review-226.md` |

---

## Feature summary

Anonymous guests can complete direct checkout on `/book/:orgSlug/property/:id/checkout`:
- `POST /api/public/bookings` creates Pending booking + Connect PaymentIntent (`application_fee_amount = 0`)
- Connected webhook confirms booking on `payment_intent.succeeded`
- FE: guest form, GDPR consent, price breakdown, Stripe Elements (demo mode in E2E)

---

## Post-deploy actions (operator)

1. **Stripe Dashboard**: Add `payment_intent.succeeded`, `payment_intent.payment_failed`, `payment_intent.canceled` to Connect webhook at `{API_BASE}/webhooks/stripe/connect`
2. **Railway prod**: Verify `Stripe:PublishableKey`, `Stripe:ConnectWebhookSecret`, `DirectBooking:ConsentVersion`
3. **Smoke test**: Operator with Connect active → branded site → checkout → test card (Stripe test mode)

---

## Gate summary (Stage 06)

| Gate | Status |
|---|---|
| G1 Prod BE health | ✅ |
| G2 Prod FE SPA | ✅ |
| G7 No 500 on core endpoints | ✅ (api-regression + prod smoke) |
| G9 Ops report written | ✅ |

**Pipeline**: `spec-direct-checkout` — **COMPLETE** (6/6 stages)
