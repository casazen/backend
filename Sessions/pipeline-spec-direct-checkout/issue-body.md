## User Story

As an anonymous guest, I want to select dates for a published property, enter my details and consent, and pay securely so that my booking is confirmed instantly — paying the **operator** directly with no booking commission added by CasaZen.

As an operator, I want guest payments to land in **my** Stripe account (I am the merchant of record), with tourist tax computed at checkout and the Italian police report (Alloggiati Web) filed on check-in, so I stay compliant without manual steps.

**Spec**: `Sessions/specs/spec-direct-checkout.md` · Phase 1 MVP · Depends on #212 (read-model), #202 (tenant-boundary), #224 (connect-onboarding), #215 (branded booking site)

---

## Acceptance Criteria

### Backend

- **AC1**: New `POST /api/public/bookings` (`[AllowAnonymous]`) accepts `CreateDirectBookingRequest` with propertyId, dates, guest details, consent. Validates availability (`409` if unavailable); property must be `IsActive` (`404`); requires `consent.dataProcessing == true` (`400`).

- **AC2**: On valid request: upsert `Guest` (match by email; consent fields), create `Booking` with `Source = Direct`, `Status = Pending`, compute amounts server-side (nightly rate × nights + cleaning fee + tourist tax via `ITaxCalculationService`). Client prices ignored.

- **AC3**: Create Stripe Connect `PaymentIntent` on operator's connected account; return `DirectBookingResponse { bookingId, clientSecret, connectedAccountPublishableContext, amount, currency, touristTaxAmount }`. `application_fee_amount = 0`. `409` if no `StripeConnectedAccountId`.

- **AC4 (RF2)**: Connected-account `payment_intent.succeeded` webhook (Connect signing secret) transitions booking `Pending → Confirmed`, writes `Payment` idempotently. Platform webhook not used here.

- **AC5**: `payment_intent.payment_failed` / `canceled` leaves booking Pending or marks Cancelled after expiry; no Alloggiati side effects.

- **AC6**: Tourist tax at checkout from `TouristTaxRate` (DB-driven, never hardcoded).

- **AC7**: Alloggiati Web on check-in only (existing `AlloggiatiWebReportJob` path unchanged).

- **AC8**: Public response data-minimized — no ownerId, no secret keys, no other guests' data.

- **AC9**: Rate-limiting per-IP on `POST /api/public/bookings`; Pending booking TTL releases inventory.

### Frontend

- **AC10**: Public checkout flow (no Auth0): date/guest step → Stripe Elements payment step for operator connected account.

- **AC11**: `createDirectBooking()` → `POST /api/public/bookings`; `useCreateDirectBooking()` without auth header.

- **AC12**: `confirmPayment` with SCA/3DS; success shows confirmation; failure shows inline error + retry.

- **AC13**: Mandatory GDPR consent checkbox + price breakdown (nightly, cleaning, tourist tax) in Italian before payment.

- **AC14**: No card data through CasaZen — Stripe Elements only; publishable key from API response.

---

## Technical Notes

### Backend impact
- New `PublicBookingsController` — `[AllowAnonymous] POST /api/public/bookings`
- Modify `StripeService` — connected-account PaymentIntent overload (`RequestOptions.StripeAccount`, `application_fee_amount = 0`)
- Modify `BookingService` — `CreateDirectBookingAsync`, Pending TTL helper
- Modify `WebhooksController` / `StripeWebhookHandler` / `StripeWebhookJob` — RF2 connected-account `payment_intent.*` events
- Rate limiter registration for public booking endpoint
- **No EF migration expected** unless new fields needed for Pending TTL (reuse existing Booking/Guest/Payment entities)

### Frontend impact
- New checkout page, price breakdown, consent checkbox under `src/features/public-booking/`
- Add `@stripe/stripe-js`, `@stripe/react-stripe-js`
- Modify `axios.ts` to skip auth for `/public/bookings`

### Background jobs
- `StripeWebhookJob` — async processing of connected-account payment events
- `AlloggiatiWebReportJob` — unchanged; triggered on check-in only

### OTA impact
- None — direct booking is separate from OTA adapters

### Compliance
- Stripe Connect operator = MoR (C3 gate): `application_fee_amount = 0`, funds settle to operator
- PSD2/SCA via Stripe.js `automatic_payment_methods`
- Tourist tax from DB `TouristTaxRate` at checkout
- GDPR consent captured on guest upsert
- Alloggiati Web on check-in (D.L. 286/1998 Art. 7), never inline at checkout

---

## Dependencies

- Requires: #212 (public read-model), #202 (tenant-boundary), #224 (connect-onboarding), #215 (branded booking site checkout surface)
- Blocks: full branded-site publish gate with live payments
