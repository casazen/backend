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
and, for the deferred option, `ChargePaymentMethodAsync`, both with the `Stripe-Account` header and `application_fee_amount = 0`):
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
   - until the **free cancellation deadline** of the booking (`FreeRefundDeadline`, check-in − 7 days, shown to the guest
     as "Cancellazione gratuita fino a …"), Europe/Rome day included: full refund;
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
   keep one of the two if guests should get a single message.

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
