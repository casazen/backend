# Review — Issue #226 Direct Checkout (Stripe Connect, Operator = MoR)

> **Stage 04 — Review** · Date: 2026-06-10 · PRs: BE [#227](https://github.com/casazen/backend/pull/227) · FE [#119](https://github.com/casazen/frontend/pull/119)

## Verdict

**APPROVE** — 0 critical findings. Ready for Stage 05 release.

## Gate Summary

| Gate | Status | Notes |
|---|---|---|
| G1 Security (MoR, no platform fee) | ✅ | `ApplicationFeeAmount = 0`; PI on connected account |
| G2 RF2 webhook separation | ✅ | Connect route + `WebhookSource.Connected` discriminator |
| G3 GDPR consent | ✅ | Guest upsert with consent fields + IP |
| G4 Tourist tax | ✅ | Server-side via `ITaxCalculationService` |
| G5 Public data minimization | ✅ | Response omits ownerId/org secrets |
| G6 Rate limiting | ✅ | `PublicBookingCreate` 10/min/IP |
| G7 BE tests | ✅ | 481 pass incl. `DirectCheckoutIntegrationTests` |
| G8 FE E2E | ✅ | `direct-checkout.spec.ts` 2/2 |
| G9 Auth on public routes | ✅ | No JWT on `/public/bookings` |
| G10 Stripe.js / no PAN | ✅ | Elements only; demo fallback in E2E |

## Non-blocking notes

1. **Stripe Dashboard**: Add `payment_intent.*` events to Connect webhook endpoint in staging/prod.
2. **Config**: Ensure `Stripe:PublishableKey` and `DirectBooking:ConsentVersion` set per environment.
3. **FE lint (G7)**: Pre-existing repo-wide eslint debt unchanged; new files lint-clean.

## AC Coverage

| AC | Evidence |
|---|---|
| AC1–AC9 BE | `PublicBookingsController`, `BookingService.CreateDirectBookingAsync`, integration tests |
| AC10–AC14 FE | `checkout-page.tsx`, E2E specs |
