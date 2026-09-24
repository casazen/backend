# Runbook: Stripe webhooks and platform billing (idempotency, one subscription per org)

Task PL-10 (audit defects A1-09, A3-03 P0, A1-10, A1-11). The code is in place; the product owner checks the
Stripe settings below, test mode first, then live mode. Keys, the two webhook endpoints and their event lists
are in `docs/INFRA.md` § "Stripe keys and webhooks".

## What the backend does

### Webhooks: signature, then exactly-once processing

| Step | Behaviour | Code |
|---|---|---|
| Signature | Mandatory on both endpoints: `/webhooks/stripe` with `Stripe__WebhookSecret`, `/webhooks/stripe/connect` with `Stripe__ConnectWebhookSecret`. A wrong signature (or API version) answers **400** `invalid_signature`. A missing secret, or the placeholder committed in `appsettings.json` (`whsec_YOUR_…`), answers **500** `stripe_webhook_not_configured`: the placeholder is public, so it is never used to verify. Stripe retries a non-2xx delivery | `Casazen.Web/Controllers/WebhooksController.cs` |
| Queue | A verified event is queued on Hangfire (`StripeWebhookJob`) and acknowledged with 200 | same |
| Claim | The job inserts the event id into `ProcessedStripeEvents` (primary key) **in the same transaction** as the business updates | `Casazen.Infrastructure/External/StripeWebhookHandler.cs` |
| Duplicate delivered later | Finds the committed claim: skipped, log `Skipping duplicate Stripe event {EventId}` | same |
| Duplicate processed in parallel | Waits on the uncommitted claim of the first worker; skipped when it commits, processed when it rolls back | same |
| Failure | Everything is rolled back, the claim included, and the error is rethrown (log `Error handling Stripe event {EventId} … claim released for the retry`). Hangfire retries the job (10 attempts with growing delays); a later retry or a resend from Stripe processes the event normally | same |

Logs carry the event id, type and source, and Stripe/org ids only: no names, e-mails or card data.

### Subscription status and paid access (A1-11)

| Stripe status | Stored `SubscriptionStatus` | Paid plan | New checkout |
|---|---|---|---|
| `active` | Active | yes, tier of the price | refused (409) |
| `trialing` | Trialing | yes, tier of the price | refused (409) |
| `past_due` | PastDue | only within `Billing:PastDueGraceDays` (default 7, spec-saas-billing AC6); no start date → no | refused (409) |
| `unpaid` | Unpaid | no | refused (409): pay from the portal |
| `incomplete` (first payment not succeeded) | Incomplete | no, and the tier of the price is **not** stored | refused (409): complete the payment from the portal, or wait 23 h for `incomplete_expired` |
| `incomplete_expired`, `canceled` | Canceled | no | allowed |
| `paused`, unknown | None | no | refused when Stripe still lists the subscription |

- The tier of the price is written only by an `active` or `trialing` subscription event. `past_due` keeps the tier already paid; every other state leaves it unchanged and the entitlement resolves to Starter.
- `invoice.payment_failed` starts the past-due grace only for a renewal of the org's paying subscription. A failed first payment (`billing_reason = subscription_create`) leaves the subscription incomplete.
- `invoice.paid` reactivates only the org's current subscription, never a canceled one.
- Stripe does not guarantee event order and Hangfire runs jobs in parallel. Events that would move a subscription back to `incomplete`, reopen a canceled subscription, or come from another subscription of the customer while one is paying are ignored (log `Ignoring Stripe event {EventId} for subscription …`).
- Immediate fail-closed on `past_due` (no grace): set `Billing__PastDueGraceDays=0` on Railway.

### Checkout: never a second subscription (A1-10)

`POST /api/billing/checkout-session` (`BillingCheckoutService`):

1. Refuses with **409** `already_subscribed` when the org stores a subscription that still exists (table above). The frontend then opens the billing portal (`POST /api/billing/portal-session`).
2. Runs one checkout at a time per org (PostgreSQL advisory lock): a double click or a second tab waits for the first request.
3. Reads the customer's subscriptions **from Stripe** and refuses with 409 when one still exists, even if its webhook has not been processed yet.
4. Reuses the open Checkout Session of the same plan (same URL for a double click) and **expires** open sessions of another plan, so only one can be paid.
5. The Stripe customer is created with an idempotency key per org: a retry after a failure does not create a second customer.

## Stripe settings to check (product owner)

Stripe Dashboard labels may differ slightly between versions.

1. **Webhook endpoints**: as in `docs/INFRA.md`. The platform endpoint must include `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted`, `invoice.paid`, `invoice.payment_failed`.
2. **Restricted key** (only if `Stripe__SecretKey` is an `rk_…` key): besides the permissions already used, the checkout now needs **Checkout Sessions: write** (create, list, expire) and **Subscriptions: read** (list the customer's subscriptions). Without them the checkout answers 503 `payment_provider_error`.
3. **Customer portal** (Settings → Billing → Customer portal): enabled, with payment method update and invoice history, so an org answered with `already_subscribed` can change plan, update its card and pay an open invoice there.
4. **Failed payments** (Settings → Billing → Subscriptions and emails → manage failed payments): at the end of the retries the subscription may be canceled or marked unpaid; both end paid access (Canceled / Unpaid).

## Refunds and booking cancellations on Stripe Connect (BK-02)

Task BK-02 (audit defects A3-05 P0, A9-15 payments part; issue #51). Before it, "Rimborsa" only changed the database,
`POST /api/payments/{id}/process` marked a payment Completed without Stripe, and cancelling a paid booking kept the money.

### Charge model (verified in the code)

The checkout creates the guest's PaymentIntent **on the host's connected account** (`StripeService.CreateConnectedAccountPaymentIntentAsync`
and, for the deferred option, `ChargePaymentMethodAsync` (off-session, BK-08, section "Deferred charge" below), both with the `Stripe-Account` header and `application_fee_amount = 0`):
**direct charges**. Consequences for refunds:

| Parameter | Value | Why |
|---|---|---|
| `Stripe-Account` header | the connected account of the payment (`Payments.StripeAccountId`; older rows: the org's `StripeConnectedAccountId`) | the PaymentIntent exists only on that account: without the header Stripe answers `No such payment_intent` |
| `Idempotency-Key` | `payment-refund:{PaymentRefunds.Id}` | the refund row is written before the call; a retry (timeout, 5xx) sends the same key and gets the same refund |
| `amount` | the amount chosen by the host, in cents | partial refunds are allowed; the backend never lets the total of succeeded + in-progress refunds exceed the payment |
| `reverse_transfer` | not sent | only for destination charges / transfers: a direct charge has no transfer to reverse |
| `refund_application_fee` | not sent | the checkout takes no application fee. If a platform fee is introduced, decide whether a refund gives it back (product decision) |

The refund is paid from the **connected account's balance**. With an insufficient balance Stripe may leave the refund
pending or fail it (the host sees the status); for Express accounts the platform is liable for a negative balance.

### Status: never "Rimborsato" before Stripe confirms

| Stripe refund status | `PaymentRefunds.Status` | Effect on the payment |
|---|---|---|
| `succeeded` (response or webhook) | Succeeded | counted in `RefundedAmount`; payment `PartiallyRefunded` or `Refunded`; guest email "Rimborso confermato" (once) |
| `pending`, `requires_action` | Pending / RequiresAction | amount reserved, payment unchanged |
| `failed`, `canceled` | Failed / Canceled | amount released, payment unchanged (a refund that fails after succeeding moves the payment back) |
| no answer (timeout, 429, 5xx) | Pending | Hangfire job `PaymentRefundSubmitJob` resends it with the same idempotency key (10 attempts) |
| 4xx (e.g. `charge_disputed`) | Failed, with the Stripe error code | nothing was created on Stripe; the host can retry |

Refunds made **outside CasaZen** (Stripe Dashboard, API) are recorded from the webhooks with origin `Stripe`, so the
payment shows them too.

### Booking cancellation (`POST /api/bookings/{id}/cancel`)

Replaces `DELETE /api/bookings/{id}`, which only changed the status. The dialog first reads `GET /api/bookings/{id}/cancellation`.

1. **Not paid yet**: the PaymentIntent (immediate payment) is canceled on the connected account; the SetupIntent (deferred
   payment) is canceled, or, when the card is already saved, the card is detached so the deadline charge can never run.
   If the guest's payment is succeeding at that moment the API answers **409** `booking_payment_in_progress`: retry once the
   payment shows as completed, then cancel with a refund.
2. **Paid through Stripe**: the host chooses the refund (`refundAmount`, from 0 to what was paid and not refunded yet).
   The minimum comes only from rules already in the model:
   - until the **free refund deadline** of the booking (`FreeRefundDeadline`: check-in − 7 days, or the last day of the full
     refund of the property's cancellation policy for bookings made after BK-07; no longer shown to the guest as a free
     cancellation, see [direct-booking.md](direct-booking.md) § 7.5), Europe/Rome day included: full refund;
   - the property's **cancellation policy** (`CancellationPolicies`, semantics of issue #51: 100% at least `FullRefundHours`
     before the start of the check-in day, `PartialRefundPercent` at least `PartialRefundHours` before, otherwise 0%);
   - otherwise no minimum: the host decides. No other policy exists in the code (see open questions of BK-02).
3. **Paid outside Stripe** (cash, bank transfer recorded by the host): shown in the quote, never refunded by CasaZen.

Authorization (TN-3): `booking.write` on the booking; when the cancellation moves money (refund or intent to cancel) also
`payment.write`, so only the owning host or an org member with payment write. `POST /api/payments/{id}/refund` needs
`payment.write`.

### Stripe settings to check (product owner)

1. **Connect webhook endpoint** (`/webhooks/stripe/connect`, "Connected accounts"): add `charge.refunded`,
   `refund.created`, `refund.updated`, `refund.failed` (and `charge.refund.updated` if the Dashboard still offers it) to the
   events of `docs/INFRA.md`. Refunds of direct charges are events of the connected account: without them a refund that
   is `pending` never becomes Rimborsato in CasaZen, and a Dashboard refund is never seen.
2. **Platform endpoint**: `charge.refunded` stays; platform refund events concern only PaymentIntents of the platform
   account (none for bookings today).
3. **Restricted key** (only with an `rk_…` key): **Refunds: Write**, **PaymentIntents: Write** (read and cancel),
   **SetupIntents: Write** (read and cancel), **PaymentMethods: Write** (detach), and the key must be allowed to act on
   connected accounts (Connect permissions of the key). With the standard secret key nothing to do.
4. **Customer emails** (Settings → Customer emails): Stripe's own refund receipt is independent from the CasaZen email;
   keep one of the two if guests should get a single message. Payment receipts: CasaZen's "Prenotazione confermata"
   carries the amounts and the payment received (BK-10) and is not a fiscal document; see
   [email.md](email.md#payment-receipt-choice-bk-10) before enabling Stripe's "Successful payments" emails.

### Data written before BK-02

Payments marked Refunded / PartiallyRefunded by the old endpoint were **never refunded on Stripe**. Find them with
`SELECT "Id", "Amount", "RefundedAmount" FROM "Payments" p WHERE "RefundedAmount" > 0 AND NOT EXISTS (SELECT 1 FROM "PaymentRefunds" r WHERE r."PaymentId" = p."Id");`
and check each one in the Stripe Dashboard (Payments → search the `pi_…`) before refunding it from CasaZen.

### Verification

1. Automated: `StripeServiceRefundTests` (mocked `IStripeClient`: `Stripe-Account`, idempotency key, amount, cancel paths),
   `PaymentRefundServiceTests`, `BookingCancellationServiceTests`, `CancellationRefundPolicyTests`,
   `StripeRefundPostgresTests` (Connect `charge.refunded` / `refund.*` on PostgreSQL, parallel double click),
   `BookingRefundIntegrationTests` (HTTP pipeline).
2. Test mode, with a connected test account: book with card `4242 4242 4242 4242`, then "Rimborsa" 10 € from the payment
   page: the dialog shows "Rimborso confermato", Stripe Dashboard → connected account → Payments shows the partial refund.
3. Test mode: refund part of a payment from the Stripe Dashboard: after the webhook the payment page lists it with origin
   Stripe and the payment is "Parzialmente rimborsato".
4. Test mode: cancel a booking not paid yet (checkout left open): the PaymentIntent is `canceled` on the connected account.

## Late payments: confirmed again or refunded in full (BK-04)

Task BK-04 (audit defect A3-04 P0). Before it, a guest who paid after the checkout hold had expired (3-D Secure plus a
banking app easily take more than 15 minutes) could be charged with no booking, no refund and no email: the payment was
marked `Completed` on the `Cancelled` booking with a warning in the log. PR #442 was discarded for leaving exactly that
state. Now `payment_intent.succeeded` of a booking payment never ends as "Completed on a cancelled booking".

### What the webhook does

`StripeWebhookHandler` → `CheckoutPaymentSettlementService`, for the checkout's PaymentIntents (`kind = direct-booking`)
on the Connect endpoint and for every booking payment reported by the platform endpoint:

| Booking when the payment succeeds | Dates | Result |
|---|---|---|
| `Pending`, hold still valid (within `DirectBooking:PendingTtlMinutes`, or payment seen in flight by the expiry job) | no other booking on them | `Confirmed` (as before); guest email "Prenotazione confermata" and host email "Nuova prenotazione confermata" (BK-10, [email.md](email.md#booking-emails-bk-10)) |
| `Pending` hold expired, or `Cancelled` (expiry job, host, legacy) | free: no confirmed / checked-in booking, no valid hold, no iCal block | **confirmed again** (`CancellationReason` cleared, check-in token issued), the same two emails with a note on the late payment |
| same | taken | stays / becomes `Cancelled` (a pending one gets `CancellationReason = 2`, `DatesUnavailableAtPayment`); **full refund** of what is still refundable; guest email "Date non più disponibili, pagamento rimborsato" once Stripe confirms the refund |
| `Confirmed`, `CheckedIn`, `CheckedOut` | — | payment `Completed`, booking unchanged |

- A hold still valid kept its dates in the iCal export, so only another booking can stand in its way (a block imported
  meanwhile is an OTA-side double booking for the host to solve). An expired hold or a cancelled booking had released its
  dates to the site and to the OTAs: iCal blocks count.
- "Valid hold" and "expired hold" are the single definition of BK-21 (`CheckoutHolds.IsExpired` / `OccupiesDates`).
- The refund reuses BK-02 (`PaymentRefundService`): row `PaymentRefunds` with origin `BookingCancellation` (the payment page
  shows "Cancellazione prenotazione"), `Stripe-Account` = the connected account of the payment (the event's `account`,
  stored at checkout in `Payments.StripeAccountId`), **idempotency key `late-payment-refund:{PaymentIntentId}`**
  (unique in the table and sent to Stripe), amount = everything still refundable (the whole payment when nothing was
  refunded). The payment becomes `Refunded` only when Stripe confirms, as for every refund.
- **Platform endpoint.** An event of the platform endpoint that carries `account` (the endpoint also listens to connected
  accounts) is handled like a Connect event. Without `account` the PaymentIntent lives on the platform account: the
  payment is flagged `StripeIntentOnPlatform` and the refund is created **without** `Stripe-Account`. The checkout never
  creates platform PaymentIntents today; the rule covers older data and a misrouted endpoint.
- An event whose `account` differs from the one stored on the payment is ignored (log `reported by account … expected …`).

### Exactly once, and no race with a new checkout

- The event claim and every write (payment, booking, refund reservation) share one transaction (PL-10): a duplicate
  delivery is skipped; another event of the same PaymentIntent finds the `late-payment-refund:` row and does nothing.
- Under PostgreSQL the webhook takes, in order, the **property lock of the booking checks** (the same
  `pg_advisory_xact_lock` as `BookingRepository.AddAsync`), the BK-02 booking-cancellation and payment-refund locks, and
  the booking row (`FOR UPDATE`, so the expiry job skips it). A checkout of the same dates waits for the commit: if the
  late payment was confirmed again the newcomer gets **409**; if the newcomer committed first the late payment is refunded.
- The Stripe refund call and the emails happen **after** the commit. A `PaymentRefundSubmitJob` is scheduled (1 minute)
  before the commit as a safety net: if the process stops in between, the job sends the refund with the same key.

### Hold duration (`DirectBooking:PendingTtlMinutes`, default 30)

Checked in the code on 2026-09-24:

- The public checkout (frontend `src/features/public-booking/checkout-page.tsx`) pays a PaymentIntent with the Stripe
  Payment Element (`stripe.confirmPayment`, `redirect: 'if_required'`) and, for the deferred option, a SetupIntent
  (`confirmSetup`). There is **no Stripe Checkout Session** and **no timer** in the page.
- The PaymentIntent is created without any expiry (`StripeService.CreateConnectedAccountPaymentIntentAsync`): it stays
  payable until CasaZen cancels it. The only bound is the hold TTL plus the `checkout-hold-expiry` job (every 5 minutes,
  BK-21), so a hold lasts between TTL and TTL + 5 minutes.
- Stripe's own hosted Checkout Session cannot expire sooner than **30 minutes** (`expires_at`: "anywhere from 30 minutes
  to 24 hours after Checkout Session creation", Stripe.net 50.1 API reference of `SessionCreateOptions.ExpiresAt`). The
  audit scenario (3-D Secure plus banking app) took 18 minutes.

Hence 30 minutes (was 15): long enough for strong customer authentication in normal cases, short enough not to block
dates for long; what arrives later is confirmed again or refunded as above. Change it with `DirectBooking__PendingTtlMinutes`
on Railway (minimum 1); a longer TTL blocks abandoned dates longer, a shorter one causes more late payments.

### Operations

| Log / situation | What to do |
|---|---|
| `… succeeded on booking … after its hold ended …: booking confirmed again` | Nothing: informative. The host sees the booking as confirmed |
| `… when its dates were no longer free: booking cancelled, full refund … reserved` | Nothing if the refund succeeds (payment page: "Rimborsato"). |
| `Automatic refund … of the late payment of booking … was not made by Stripe (<code>)` | Stripe rejected the refund (e.g. `charge_disputed`, insufficient balance on the connected account). The payment stays `Completed` with a failed refund row: the host refunds it from the payment page (BK-02), or support from the Stripe Dashboard |
| Refund `Pending` for a long time | Stripe or the bank is still working (`refund.updated` completes it); `PaymentRefundSubmitJob` in Hangfire *Failed* means Stripe never answered: requeue it |

**Data written before BK-04**: payments completed on cancelled bookings without any refund. Check each one (Stripe
Dashboard → Payments → the `pi_…`) and refund it from the payment page, or confirm the booking again if the dates are free:

```sql
SELECT b."Id" AS booking_id, p."Id" AS payment_id, p."StripePaymentIntentId", p."Amount", b."UpdatedAt"
FROM casazen_prod."Bookings" b JOIN casazen_prod."Payments" p ON p."BookingId" = b."Id"
WHERE b."Status" = 4 AND p."Status" = 2 AND p."StripePaymentIntentId" IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM casazen_prod."PaymentRefunds" r WHERE r."PaymentId" = p."Id")
ORDER BY b."UpdatedAt" DESC;
```

### Stripe settings

Nothing new: the Connect endpoint already sends `payment_intent.succeeded` and the refund events of BK-02; a restricted
key needs **Refunds: Write** (BK-02).

### Verification

1. Automated: `LateCheckoutPaymentPostgresTests` (free dates → confirmed again + email; dates taken by another guest →
   one refund with `Stripe-Account`, amount and key checked + email; expired pending hold over an iCal block → refund;
   valid hold → confirmed; the same event twice in parallel, again later and another event of the same PaymentIntent →
   one refund; late payment and new checkout racing on the property lock in both orders → one booking on the dates;
   platform endpoint with and without `account`; foreign account ignored), `DirectCheckoutIntegrationTests` (HTTP
   pipeline), `EmailTemplatesTests`.
2. Test mode, with a connected test account: start a checkout for some dates and stop before paying; set that booking
   `Cancelled` in the SQL editor (`UPDATE … SET "Status" = 4 WHERE "Id" = '…'`, which leaves the PaymentIntent payable as
   before BK-21), book the **same dates** from another browser, then pay the first checkout with `4242 4242 4242 4242`:
   the first booking stays cancelled, the connected account shows a full refund, the first guest receives
   "Date non più disponibili". Repeat without the second booking: the first booking becomes `Confirmed`, the guest
   receives "Prenotazione confermata" (with the late-payment note) and the host "Nuova prenotazione confermata".

## Deferred charge of "Paga più tardi" (BK-08)

Task BK-08 (audit defect A3-14, P1). Before it the deferred charge job marked the payment `Completed` whatever the
PaymentIntent status, without `off_session`: with an EU card and 3-D Secure the PaymentIntent stayed `requires_action`,
CasaZen said "paid" and the guest arrived without paying. Errors were only logged and the job retried every day forever;
the webhook ignored the `direct-booking-deadline-charge` kind on the platform endpoint. The booking side (attempts, emails,
cancellation, settings) is in [direct-booking.md](direct-booking.md) § 8; this section covers the Stripe calls.

### Stripe parameters (checked 2026-09-24)

`StripeService.ChargePaymentMethodAsync` creates the PaymentIntent **on the connected account the card was saved on**
(`Payments.StripeAccountId` of the deferred payment, written at checkout with the SetupIntent; the org's current account
only for rows without it): `Stripe-Account` header, `customer` and `payment_method` of the booking, `amount` = the
booking total in cents, `confirm=true`, **`off_session=true`**, `metadata.kind = direct-booking-deadline-charge`,
`metadata.bookingId`. A failed attempt is retried (next days) by confirming **the same** PaymentIntent again
(`ConfirmPaymentIntentOffSessionAsync`: `payment_method`, `off_session=true`), so one booking never has two payable
deferred PaymentIntents.

| Parameter | Choice | Why (sources) |
|---|---|---|
| `off_session` | `true` | The guest is not in the checkout. Stripe.net 50.1 API reference (`PaymentIntentCreateOptions.OffSession`): "the customer isn't in your checkout flow during this payment attempt and can't authenticate… only with `confirm=true`". Stripe docs "Save a customer's payment method" / "Charge the saved payment method later": a failed off-session attempt answers **HTTP 402** and leaves the PaymentIntent in `requires_payment_method`; for `authentication_required` bring the customer back and confirm the same PaymentIntent on-session |
| `error_on_requires_action` | **not sent** | Stripe changelog 2023-08-16 "automatic payment methods" (our API version `2025-12-15.clover` is later): PaymentIntents use automatic payment methods by default and `error_on_requires_action` is accepted only with explicit `payment_method_types`. Restricting `payment_method_types` (e.g. `card`) would refuse the other methods the SetupIntent (automatic payment methods, `usage=off_session`) may have saved, such as SEPA Debit. With `off_session=true` an authentication request already fails the attempt, so the flag adds nothing. PR #447 was discarded for sending it without `payment_method_types` |
| `return_url` | **not sent** | Same changelog: confirming requires a `return_url` **unless `off_session=true`**; nobody is redirected off-session. The on-session payment of the guest (outcome page) sends its own `return_url` from Stripe.js |
| `payment_method_types` | not sent | automatic payment methods, like the checkout |
| `application_fee_amount` | `0`, unchanged | left to task BK-19 (A3-40) |
| Idempotency key (creation) | `direct-booking-deadline:{bookingId}:{deadline yyyyMMdd}:{attempt}` | bound to the booking, its deadline and the attempt: a Hangfire retry or a crash before the commit sends the same key and gets the same PaymentIntent |
| Idempotency key (retry) | `direct-booking-deadline-confirm:{PaymentIntentId}:{attempt}` | one confirmation per attempt |
| Before a creation | `GET /v1/payment_intents?customer=…` on the connected account | Stripe keeps idempotency keys for 24 hours and the job runs daily: a PaymentIntent of an attempt whose answer was lost (timeout) is found by `metadata.bookingId` + `kind` and used instead of creating a second charge |

Status mapping (`DeferredCharges.StatusOf`): `succeeded` → payment `Completed`; `processing` (SEPA) / `requires_capture` →
`Processing`, completed by the webhook; `requires_action`, `requires_payment_method`, `requires_confirmation` (and the 402
answer) → `Failed`, the guest must act; `canceled` → `Canceled` (canceled outside CasaZen: no new attempt, see
direct-booking.md § 8).

### Webhooks

`StripeWebhookHandler` sends every `payment_intent.*` event whose `metadata.kind` is `direct-booking-deadline-charge` to
`DeferredChargeService`, from the **Connect endpoint** and from the **platform endpoint when the event carries `account`**
(an endpoint that also listens to connected accounts). A platform event without `account` is ignored (deferred charges
never live on the platform account), as is an event whose `account` differs from the one stored on the payment.

| Event | Effect |
|---|---|
| `payment_intent.succeeded` | payment `Completed` (amount of the PaymentIntent), failure cleared. On a booking no longer confirmed it is settled as a late payment (BK-04): confirmed again or refunded in full, never "Completed" on a cancelled booking |
| `payment_intent.processing` | payment `Processing` (**add this event to the Connect endpoint**, `docs/INFRA.md`; without it the job reads the status again every day) |
| `payment_intent.payment_failed` | payment `Failed`; the first failure of a confirmed booking emails the guest (link to pay) and the host, once |
| `payment_intent.canceled` | payment `Canceled` |

Exactly once like every event (PL-10). The handler takes the BK-02 booking-cancellation and payment-refund locks of the
booking, the same as the job, so an event that arrives while the job is charging waits for the job's commit and sees its
result (no second email).

### Stripe settings to check (product owner)

1. Connect endpoint: add `payment_intent.processing` (optional but recommended for SEPA).
2. Restricted key only (`rk_…`): **PaymentIntents: Write** (create, confirm, list, cancel), **PaymentMethods: Write**
   (detach at the automatic cancellation), on connected accounts.
3. Customer emails: the deferred PaymentIntent has a `customer` (the one of the SetupIntent). Stripe sends its own
   receipt only if "Successful payments" emails are enabled for the account and the customer has an email: CasaZen
   sends no email for a successful deferred charge, see the open question in
   [email.md](email.md#payment-receipt-choice-bk-10).

### Verification

1. Automated: `StripeServiceDeferredChargeTests` (mocked `IStripeClient`: `Stripe-Account`, `off_session`, `confirm`, no
   `error_on_requires_action` / `payment_method_types` / `return_url`, idempotency keys), `DirectBookingChargeJobTests`,
   `DeferredChargePostgresTests` (webhook on both endpoints, foreign account ignored).
2. Test mode, connected test account: book with "Paga più tardi" and the card `4000 0027 6000 3184` (always asks for
   3-D Secure), set the booking's `FreeRefundDeadline` to today in the SQL editor, trigger `direct-booking-charge` from the
   Hangfire dashboard: Stripe shows the PaymentIntent `requires_payment_method` with `authentication_required`, the payment
   is "Fallito", the guest receives "Pagamento non riuscito" and the host "Addebito non riuscito". Open the link, complete
   3-D Secure: after `payment_intent.succeeded` the payment is "Completato". Repeat with `4242 4242 4242 4242`: completed at
   the first run, no email. `4000 0000 0000 9995` (insufficient funds): failed, retried on the next days.

## Operations

| Situation | What to do |
|---|---|
| `Error handling Stripe event {EventId}` in the logs | Nothing while Hangfire retries. If the job ends in *Failed*, fix the cause, then requeue it from the Hangfire dashboard or resend the event from Stripe (Developers → Events → the event → Resend): processing is idempotent |
| Stripe shows failed deliveries with 500 `stripe_webhook_not_configured` | Set the signing secret of that endpoint on Railway (`docs/INFRA.md`), redeploy; Stripe retries on its own for up to 3 days, older events can be resent |
| Log `Org {OrgId} has a second subscription …` | A duplicate subscription exists on Stripe (created before PL-10 or outside the app): the current one keeps the plan. Cancel the duplicate in Stripe and refund its payments |
| Log `Checkout refused for org {OrgId}: Stripe subscription … (webhook pending?)` repeating for the same org | The org pays but its webhook was not applied: check failed `StripeWebhookJob` jobs and resend the `customer.subscription.*` event |

## Verification

1. Automated: `StripeWebhookIdempotencyPostgresTests` (sequential and parallel duplicates on both endpoints, failure then retry, incomplete/unpaid without access), `BillingCheckoutPostgresTests` (repeated checkout, double click, other plan, incomplete), `StripeWebhookSubscriptionStateTests`, `WebhookSignatureTests`.
2. Test mode: from Stripe Dashboard resend the same event twice to the platform endpoint: the second job logs `Skipping duplicate Stripe event`.
3. Test mode: complete a checkout, then start another from the plans page: 409 `already_subscribed`, no second session in Stripe.
