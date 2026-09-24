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
