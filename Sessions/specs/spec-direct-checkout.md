# Spec — Direct Checkout (Stripe Connect, Operator = Merchant of Record) (US-002)

## Overview

The current Stripe integration charges guests but assumes an **authenticated owner** creating the
booking (`BookingController` is behind `PropertyOwner` + `RequireContext:short-rent:booking.*`) and
uses a single platform Stripe account (`StripeService.CreatePaymentIntentAsync`). For commission-free
**direct booking**, an **anonymous guest** must be able to book and pay, and the money must settle to
the **operator's** Stripe **connected account** — the operator is the **merchant of record (MoR)**.
CasaZen never holds or settles guest funds (`application_fee_amount = 0`).

This spec adds a public booking + checkout flow: create a `Pending` booking (`BookingSource.Direct`),
create a **Stripe Connect** `PaymentIntent` on the operator's connected account, confirm the booking on
the connected-account `payment_intent.succeeded` webhook, compute **tourist tax at checkout**, and keep
the existing **Alloggiati Web on check-in** behavior (enqueued, never inline).

Phase: **1 (MVP Sellable — direct booking)** · User story: **US-002**
Stage of entry: **Stage 01 Planning** (new macro-spec)

---

## User Story

As an anonymous guest, I want to select dates for a published property, enter my details and consent,
and pay securely so that my booking is confirmed instantly — paying the **operator** directly with no
booking commission added by CasaZen.

As an operator, I want guest payments to land in **my** Stripe account (I am the merchant of record),
with tourist tax computed at checkout and the Italian police report (Alloggiati Web) filed on check-in,
so I stay compliant without manual steps.

---

## Acceptance Criteria

### Backend

- **AC1**: New `POST /api/public/bookings` (`[AllowAnonymous]`) accepts `CreateDirectBookingRequest`:
  `{ propertyId, checkInDate, checkOutDate, numberOfAdults, numberOfChildren, guest: { firstName, lastName, email, phone, country }, consent: { dataProcessing: true, consentVersion }, specialRequests? }`.
  - Validates availability via the existing `IBookingService.IsPropertyAvailableAsync`; `409 Conflict` if unavailable.
  - Rejects if the property is not `IsActive` (`404`), reusing the `spec-public-booking-readmodel` public read.
  - Requires `consent.dataProcessing == true`; else `400`.

- **AC2**: On valid request the endpoint (a) upserts a `Guest` (match by email; sets `DataProcessingConsentDate`, `ConsentIpAddress`, `ConsentVersion`, `DataRetentionUntil`), (b) creates a `Booking` with `Source = BookingSource.Direct`, `Status = BookingStatus.Pending`, and (c) computes amounts server-side: `BasePrice` from the property nightly rate × nights + cleaning fee; `TouristTaxAmount` via `ITaxCalculationService.CalculateTouristTaxAsync`; `TotalPrice = BasePrice + TouristTaxAmount`. Client-supplied prices are ignored.

- **AC3**: The endpoint creates a **Stripe Connect** `PaymentIntent` **on the operator's connected account** and returns `DirectBookingResponse { bookingId, clientSecret, connectedAccountPublishableContext, amount, currency, touristTaxAmount }`.
  - The connected account id is resolved from the property's `Org.StripeConnectedAccountId` (see `spec-tenant-boundary`).
  - **`application_fee_amount` is `0`** and there is **no `transfer_data`/destination charge to CasaZen** — funds settle to the operator (operator = MoR). `PaymentIntent.Metadata` carries `{ bookingId, propertyId, orgId, kind: "direct-booking" }`.
  - `automatic_payment_methods.enabled = true` so Stripe enforces **PSD2/SCA**; no card data ever touches CasaZen servers.
  - `409` with a clear error if the operator's `Org` has no `StripeConnectedAccountId` (operator not onboarded to Connect).

- **AC4 (RF2 — connected-account webhook routing)**: A connected-account `payment_intent.succeeded` event (delivered with the `Stripe-Account` header / non-null `event.Account`, verified with the **Connect** signing secret `Stripe:ConnectWebhookSecret`) transitions the booking `Pending → Confirmed`, writes a `Payment` (`StripePaymentIntentId`, `Status = Completed`, `ProcessedAt`), and is **idempotent** by `PaymentIntentId`. Platform-account events (`Stripe:WebhookSecret`) are **not** handled here. Processing stays async via `StripeWebhookJob`.

- **AC5**: A connected-account `payment_intent.payment_failed` / `canceled` event leaves the booking `Pending` (or marks it `Cancelled` after expiry) and records a `Payment` with `Status = Failed`; no Alloggiati/tax side effects fire for unpaid bookings.

- **AC6**: **Tourist tax at checkout** — the `TouristTaxAmount` shown in `DirectBookingResponse` equals the amount included in the `PaymentIntent`; it is derived from `TouristTaxRate` (DB-driven, per city, never hardcoded) and the adult/child split.

- **AC7**: **Alloggiati Web on check-in (enqueue, never inline)** — guest registration is **not** triggered by checkout. It remains driven by `POST /api/bookings/{id}/check-in`, which enqueues `AlloggiatiWebReportJob` (24h obligation, D.L. 286/1998 Art. 7). Direct bookings reuse this existing path unchanged.

- **AC8**: A public, anonymous `POST /api/public/bookings` cannot create a booking for another guest's data exfiltration — the response exposes only the new `bookingId`, `clientSecret`, and amounts; it never returns `ownerId`, the connected account secret key, or other guests' data.

- **AC9**: Rate-limiting / abuse guard on `POST /api/public/bookings` (per-IP throttle) and a short `Pending` booking TTL so unpaid holds release inventory (a `Pending` direct booking older than the TTL does not block availability).

### Frontend

- **AC10**: A public checkout flow (no Auth0) under the booking surface: date/guest step → payment step using **Stripe.js Elements** (`@stripe/stripe-js` + `@stripe/react-stripe-js`), initialized for the operator's connected account from `DirectBookingResponse`.

- **AC11**: `src/api/public-booking.api.ts` `createDirectBooking(payload)` → `POST /api/public/bookings`; `src/queries/use-public-booking.ts` exposes `useCreateDirectBooking()` (no auth header on these calls).

- **AC12**: The payment step calls Stripe `confirmPayment` with the returned `clientSecret`; **SCA** challenges (3DS) are handled by Stripe.js. On success the UI shows a confirmation screen with booking reference; on failure, an inline error and retry.

- **AC13**: A mandatory **data-processing consent** checkbox (GDPR) and a visible price breakdown (nightly × nights, cleaning fee, **tourist tax** line) are shown before payment; submit is disabled until consent is checked. End-user strings in Italian (e.g. "Tassa di soggiorno", "Acconsento al trattamento dei dati").

- **AC14**: No card data passes through CasaZen — only Stripe Elements; the publishable key/connected-account context comes from the API response, never hardcoded.

---

## Technical Notes

### Backend

| File | Action |
|---|---|
| `Casazen.Web/Controllers/PublicBookingsController.cs` | Create — `[AllowAnonymous] POST /api/public/bookings` (AC1–AC3, AC8, AC9) |
| `Casazen.Web/DTOs/CreateDirectBookingRequest.cs` | Create — guest + dates + consent payload |
| `Casazen.Web/DTOs/DirectBookingResponse.cs` | Create — `{ bookingId, clientSecret, amount, currency, touristTaxAmount }` |
| `Casazen.Infrastructure/External/StripeService.cs` | Modify — overload `CreatePaymentIntentAsync` to accept a connected-account id (`RequestOptions { StripeAccount = ... }`), `application_fee_amount = 0`, metadata |
| `Casazen.Core/Services/IBookingService.cs` | Modify — add `CreateDirectBookingAsync(...)` (guest upsert + pending booking + amounts) |
| `Casazen.Infrastructure/Services/BookingService.cs` | Modify — implement direct booking; reuse `ITaxCalculationService`; Pending TTL helper |
| `Casazen.Web/Controllers/WebhooksController.cs` | Modify (RF2) — add `POST /webhooks/stripe/connect` verified with `Stripe:ConnectWebhookSecret`; pass a `source` discriminator to the job |
| `Casazen.Web/BackgroundJobs/StripeWebhookJob.cs` | Modify (RF2) — `ProcessEventAsync` takes the webhook `source` (platform vs connected) |
| `Casazen.Infrastructure/External/StripeWebhookHandler.cs` | Modify — handle connected-account `payment_intent.succeeded/failed`; confirm booking; idempotent write Payment (AC4–AC5) |
| `Casazen.Web/BackgroundJobs/AlloggiatiWebReportJob.cs` | Reference — unchanged; still enqueued by `BookingController.CheckIn` (AC7) |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — register per-IP rate limiter for the public booking endpoint |

### Frontend

| File | Action |
|---|---|
| `src/features/public-booking/checkout-page.tsx` | Create — date/guest + Stripe Elements payment step |
| `src/features/public-booking/components/price-breakdown.tsx` | Create — nightly/cleaning/tourist-tax lines (AC13) |
| `src/features/public-booking/components/consent-checkbox.tsx` | Create — GDPR consent gate |
| `src/api/public-booking.api.ts` | Create — `createDirectBooking()` (no auth) |
| `src/queries/use-public-booking.ts` | Create — `useCreateDirectBooking()` |
| `src/types/booking.types.ts` | Modify — `CreateDirectBookingRequest`, `DirectBookingResponse` |
| `src/lib/axios.ts` | Modify — skip auth for `/public/bookings` |
| `package.json` | Modify — add `@stripe/stripe-js`, `@stripe/react-stripe-js` |

---

## Compliance

- **Stripe Connect, operator = merchant of record**: the `PaymentIntent` is created **on the operator's connected account** with `application_fee_amount = 0` and no destination charge to CasaZen — **CasaZen never holds or settles guest funds**. This is the C3 regulatory gate; it must be enforced server-side (the connected account id is required, AC3).
- **PSD2 / SCA**: `automatic_payment_methods` + Stripe.js `confirmPayment` delegate Strong Customer Authentication to Stripe; no PAN/card data touches CasaZen.
- **Tourist tax at checkout**: computed from DB-driven `TouristTaxRate` (never hardcoded), included in the charged total (AC6).
- **Alloggiati Web (D.L. 286/1998 Art. 7)**: filed on **check-in**, enqueued via `AlloggiatiWebReportJob` (24h) — **never inline** in the checkout request (AC7).
- **GDPR**: guest consent captured at booking (`DataProcessingConsentDate`, `ConsentIpAddress`, `ConsentVersion`, `DataRetentionUntil`); public response is data-minimized (AC8).

---

## Dependencies

- **Requires**: `spec-public-booking-readmodel` (public property read-model for the checkout surface); `spec-tenant-boundary` (defines `Org.StripeConnectedAccountId`); **`spec-connect-onboarding` (operator Connect-account onboarding/KYC that actually populates `Org.StripeConnectedAccountId` — without it the AC3 `409` path is permanent and no direct booking can complete)**; existing Stripe integration, `Booking`/`Guest`/`Payment` entities, `ITaxCalculationService`, `AlloggiatiWebReportJob`.
- **Blocks**: `spec-branded-booking-site` (the branded surface wraps this checkout); `spec-google-vacation-rentals` (Phase 3) feeds this flow.
- **Related (RF2)**: `spec-saas-billing` shares `WebhooksController`/`StripeWebhookJob` — this spec owns **connected-account** routing; billing owns **platform-account** routing. Both must verify the correct signing secret per source.
- **Does not touch**: authenticated owner booking endpoints (`BookingController` CRUD), OTA adapters, lease subsystem.
