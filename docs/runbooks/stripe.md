# Runbook: Stripe webhooks and platform billing (idempotency, one subscription per org)

Task PL-10 (audit defects A1-09, A3-03 P0, A1-10, A1-11). The code is in place; the product owner checks the
Stripe settings below, test mode first, then live mode. Keys, the two webhook endpoints and their event lists
are in `docs/INFRA.md` § "Stripe keys and webhooks". Environments, plan prices and return pages (task PL-11):
section [Environments](#environments-stripe-mode-plan-prices-and-return-pages-pl-11).

## Environments: Stripe mode, plan prices and return pages (PL-11)

Task PL-11 (audit defects A1-31, A1-32). Before it the Railway test environment ran as `Production` with test keys,
so the billing entry gate stayed closed and the billing could not be tested; the return pages pointed at a domain and
routes that do not exist; the price ids were placeholders; `successUrl` / `cancelUrl` were taken from the client
without any check.

### Which environment uses which mode

| Railway environment | `ASPNETCORE_ENVIRONMENT` | Stripe keys | Price ids | Auth0 tenant |
|---|---|---|---|---|
| `test` | **`Staging`** | test mode (`sk_test_…` or `rk_test_…`, `pk_test_…`) | the test-mode prices | test tenant ([auth0.md](auth0.md) §1) |
| `production` | `Production` | live mode (`sk_live_…` or `rk_live_…`, `pk_live_…`) | the live-mode prices | production tenant |

What the API enforces at startup (`Casazen.Web/Configuration/BillingConfiguration.cs`). "Stops" = the new container
exits with the list of problems and Railway keeps the previous deployment:

| Situation | Result |
|---|---|
| `Production` with a test-mode `Stripe__SecretKey` or `Stripe__PublishableKey` | stops (`… is a Stripe test-mode key but ASPNETCORE_ENVIRONMENT is Production`) |
| Any other environment (`Staging`, `Development`, `Testing`) with a live-mode key | stops (`… is a Stripe live-mode key but ASPNETCORE_ENVIRONMENT is Staging`) |
| `Production` with `Stripe__SecretKey` set and a plan without a valid Price id, or two plans with the same id | stops, naming the `Billing__Prices__<Tier>` variables |
| `Production` without `Stripe__SecretKey` (payments not active yet) | starts; `/api/health/ready` reports `stripe: degraded` (FD-12: payments are optional) |
| `Staging` / `Development` with a plan without a Price id | starts; that plan is **not purchasable**: `GET /api/billing/plans` returns `purchasable: false` and an empty `stripePriceId`, the checkout answers **422 `billing_plan_unavailable`**, `/api/health/ready` reports `stripe: degraded` naming the variable |

Choice for the price ids (PL-11): in Production a plan offered by the catalogue but not payable is a configuration error
and stops the deploy; elsewhere the plan is disabled with an explicit error, so the test environment keeps working
while its prices are being created. A value is a valid Price id when it starts with `price_` and is not a placeholder
(`price_PLACEHOLDER_…`, `…YOUR_…`). The catalogue has three paid plans, `Starter`, `Pro` and `Scale`
(`PlanCatalog`, spec-saas-billing AC1): each one needs a price.

The billing entry gate (`BillingEntryGate`): with a test-mode secret key outside Production the checkout is open
without the invoicing prerequisites (log `Billing entry gate bypassed for Stripe test secret key`); in Production it
needs `Billing__VatNumber` and a decision on the e-invoices: an SDI provider (none in this build) or
`Sdi__ManualIssuanceAccepted=true` (task PL-13, [billing-tax.md](billing-tax.md)). `Sdi__ProviderConfigured` is no longer read.

### Plan prices (`Billing__Prices__<Tier>`)

For each mode (test first, then live), Stripe Dashboard → Product catalogue → **Add product**, one product per plan
(`Starter`, `Pro`, `Scale`) with a **recurring monthly price** in EUR. The amounts are a product decision: the plans page
shows `Billing:Display:<Tier>:PriceMonthly` from `appsettings.json` (29 / 79 / 199 €), so create prices with the same
amounts or change both. Open the price and copy its id (`price_…`, not the product id `prod_…`) into Railway:

| Variable | `test` (Staging) | `production` |
|---|---|---|
| `Billing__Prices__Starter` | test-mode price of Starter | live-mode price of Starter |
| `Billing__Prices__Pro` | test-mode price of Pro | live-mode price of Pro |
| `Billing__Prices__Scale` | test-mode price of Scale | live-mode price of Scale |

A price of the other mode is not detected at startup (Price ids carry no mode): Stripe answers "No such price" and the
checkout answers 503 `payment_provider_error`. `appsettings.json` keeps the three keys empty. The webhook maps a
subscription back to its plan through the same variables: a price not listed here grants no paid plan.

### Return pages and allow-list

Stripe Checkout and the billing portal send the browser back to the web app, always on `App__PublicSiteBaseUrl` (the
public domain of the environment, SE-02 / decision D3, no domain in code). By default:

| Page | URL |
|---|---|
| Checkout paid | `{App__PublicSiteBaseUrl}/app/short-rent/settings/plan?checkout=success` |
| Checkout abandoned | `{App__PublicSiteBaseUrl}/app/short-rent/settings/plan?checkout=cancel` |
| Billing portal "return" link | `{App__PublicSiteBaseUrl}/app/short-rent/settings/plan` |

**Page of the context the user started from (PL-16).** `POST /api/billing/checkout-session` (field `returnPath`) and
`POST /api/billing/portal-session` (optional body `{ "returnPath": … }`) take the page to come back to, a path of this
allow-list only (`PublicSiteLinks.BillingReturnPagePaths`, exact match, no query string):

| `returnPath` | Shell |
|---|---|
| `/app/short-rent/settings/plan` (default), `/app/short-rent/settings/billing` | short-rent (host) |
| `/app/long-rent/settings/plan`, `/app/long-rent/settings/billing` | long-rent (landlord with only long-term leases) |

The backend builds `{App__PublicSiteBaseUrl}{returnPath}?checkout=success|cancel` (portal: `{App__PublicSiteBaseUrl}{returnPath}`).
Anything else (another page, an absolute URL, `//host`, a query string) answers **400 `validation_error`** before any
change. The web app sends the page it is on, so a Vercel preview too comes back to the right shell (on the public domain
of the environment). The frontend mirror of the list is `src/lib/billing-routes.ts`: add a page to both.

- `App__PublicSiteBaseUrl` is already required outside Development/Testing (the startup stops without it). In
  Development/Testing without it the checkout and the portal answer **503 `billing_return_url_not_configured`**.
- `POST /api/billing/checkout-session` still accepts `successUrl` / `cancelUrl` from the client (e.g.
  `?session_id={CHECKOUT_SESSION_ID}`), but only **absolute URLs on the same scheme, host and port as
  `App__PublicSiteBaseUrl`**, without user info, **on one of the allow-listed pages above** (PL-16; any query string).
  Anything else (another host, `http` instead of `https`, another port, a relative path, another page of the site)
  answers **400 `validation_error`** before any change. The web app no longer sends them (it sends `returnPath`).
- `Billing__PortalReturnUrl` no longer exists: delete it from Railway if it was set.
- Stripe Dashboard: nothing to configure for the return pages (they are sent with each session). For the portal, the
  "default redirect link" of Settings → Billing → Customer portal is only used by portal links created in the
  Dashboard; leave it empty or set it to the plan page above.

### Switching the Railway test environment to Staging (one-time, product owner)

1. Railway → project → environment **test** → service → Variables: check that `Hangfire__Schema=hangfire_casazen_test`
   and the connection string with `SearchPath=casazen_test` are set (the Hangfire schema derived from the environment
   name would otherwise change, see [hangfire.md](hangfire.md)).
2. Set `ASPNETCORE_ENVIRONMENT=Staging`. Check that `Stripe__SecretKey` / `Stripe__PublishableKey` are **test-mode** keys
   and add `Billing__Prices__Starter`, `__Pro`, `__Scale` with the test-mode prices; delete `Billing__PortalReturnUrl`.
3. Redeploy and open `GET /api/health/ready` with an admin token: `stripe` is `healthy` (test mode).
4. Railway → environment **production**: `ASPNETCORE_ENVIRONMENT=Production` (unchanged), live-mode keys and the three
   live-mode prices **before** the release that contains PL-11 reaches `main`: with a live secret key and no prices the
   new production deployment stops (the previous one keeps running). Without Stripe keys the prices are not required.

What `Staging` changes besides Stripe: every startup validation of Production still applies (email, storage, Auth0,
CORS, public domain: they are enforced everywhere except Development/Testing), HSTS is still sent, EF migrations still
run at startup. Only three behaviours depend on the name `Production`: the billing entry gate (above), the draft SEO
pages, which the public API serves only on Staging (preview of `/p/*` pages not yet approved), and the Hangfire schema
fallback of step 1.

### Verification

1. Automated: `BillingConfigurationTests` (Production without prices, placeholders, duplicated price, test key in
   Production, live key in Staging/Development/Testing), `BillingReturnUrlTests` (return pages from the configured
   domain, allow-list), `BillingIntegrationTests` (default pages sent to Stripe, allowed and refused client URLs,
   portal return page, plan without price → 422 and `purchasable: false`, 503 without public domain),
   `ConfigurationHealthChecksTests`, `NoHardcodedPublicDomainTests` (no Stripe URL left in its allow-list),
   `LongRentBillingAccessPostgresTests` (PL-16: long-term landlord 200 on entitlement, checkout and portal with the
   long-rent return pages, `Staff` collaborator 403, return path outside the allow-list 400).
2. Test environment (Staging, test keys): from the plans page start the checkout of Pro and pay with
   `4242 4242 4242 4242`: Stripe sends the browser to `{App__PublicSiteBaseUrl}/app/short-rent/settings/plan?checkout=success`
   and the plan becomes Pro after the webhook. Open the billing portal and click the return link: same page without
   parameters.
3. `curl -X POST …/api/billing/checkout-session -d '{"planTier":"Pro","billingCountry":"IT","successUrl":"https://example.com/"}'`
   (with a billing admin token): 400 `validation_error`, no Checkout Session in the Stripe Dashboard. Same with
   `"returnPath":"https://example.com/"` or `"returnPath":"/app/long-rent/leases"`.
4. Long-term landlord (PL-16): sign in with a user onboarded as "Locazioni di lungo periodo" only; the header badge
   and the menu "Piano" / "Fatturazione" open `/app/long-rent/settings/plan` and `…/billing` in the long-rent shell;
   start the checkout of Pro: Stripe sends the browser back to `{App__PublicSiteBaseUrl}/app/long-rent/settings/plan?checkout=success`.
   The billing portal return link opens the page it was opened from.

## What the backend does

### Webhooks: signature, then exactly-once processing

| Step | Behaviour | Code |
|---|---|---|
| Signature | Mandatory on both endpoints: `/webhooks/stripe` with `Stripe__WebhookSecret`, `/webhooks/stripe/connect` with `Stripe__ConnectWebhookSecret`. A wrong signature answers **400** `invalid_signature`. A missing secret, or the placeholder committed in `appsettings.json` (`whsec_YOUR_…`), answers **500** `stripe_webhook_not_configured`: the placeholder is public, so it is never used to verify. Stripe retries a non-2xx delivery | `Casazen.Web/Controllers/WebhooksController.cs` |
| API version | `EventUtility.ConstructEvent` is called with `throwOnApiVersionMismatch: false` (A3-39): the event's own `api_version` field is **not** required to match this SDK build's pinned version (currently `2025-12-15.clover`, `Stripe.StripeConfiguration.ApiVersion`). If the Stripe dashboard endpoint is ever configured on a different API version, its events are still accepted (and the mismatch is visible in the "Received Stripe … webhook" log line, which now includes the event's api version) instead of being answered 400 forever. Signature verification is unaffected: a forged event with any `api_version` still answers 400. The tolerance is a safety net, not a configuration: **create both endpoints on the SDK's API version** (chosen when the endpoint is created in Dashboard → Developers → Webhooks), because the job deserializes the payload with this SDK's models and a field moved or renamed in another version reads as missing. | same |
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
2. **Restricted key** (only if `Stripe__SecretKey` is an `rk_…` key): besides the permissions already used, the checkout now needs **Checkout Sessions: write** (create, list, expire) and **Subscriptions: read** (list the customer's subscriptions). Without them the checkout answers 503 `payment_provider_error`. The paid invoices (PL-13) also need **Tax rates: read** and **Customers: read** (tax ids).
3. **Customer portal** (Settings → Billing → Customer portal): enabled, with payment method update and invoice history, so an org answered with `already_subscribed` can change plan, update its card and pay an open invoice there. The web app (PL-12) sends every subscribed org to the portal to **change plan**: turn on the subscription update (plan switch) and list the Starter, Pro and Scale products with the same prices as `Billing__Prices__<Tier>`, otherwise "Cambia dal portale" opens a portal without the plan change.
4. **Failed payments** (Settings → Billing → Subscriptions and emails → manage failed payments): at the end of the retries the subscription may be canceled or marked unpaid; both end paid access (Canceled / Unpaid).
5. **Stripe Tax** (PL-13): head office, Italian registration and product tax codes, see [billing-tax.md](billing-tax.md) § Settings. Without them the checkout charges no VAT or fails with 503 `payment_provider_error`.

## Web app: plans, checkout, portal and billing profile (PL-12)

Task PL-12 (audit defect A1-07). Pages of the web app, visible in the menu only to the org billing administrator
(frontend mirror of the policy `OrgBillingAdmin`: owner of either rental context, i.e. host or long-term landlord
(PL-16), or platform admin). Anyone else who opens them sees "contatta l'amministratore" and no billing call is made.

**Both rental shells (PL-16, A1-36).** The same pages exist as `/app/short-rent/settings/{plan,billing}` and
`/app/long-rent/settings/{plan,billing}`: a landlord with only long-term leases manages its plan without the short-rent
context. The header badge opens the plan page of the current shell (from the admin or supplier shell, the one of the
user's rental context, short-rent first; no link when the user has none). A plan or billing page of a context the user
does not work in (old link, a Stripe return page created before PL-16) redirects to the same page of its context, query
string included. On the backend `GET /api/orgs/me/entitlement` and every billing endpoint use the org policy
`OrgBillingAdmin`, which admits the owner as `PropertyOwner` or `LongTermLandlord` (JWT role, or DB membership with the
owner's role key of the short-rent or long-rent context), the platform admin and, since AM-01, a member whose `account`
membership holds `org.billing.manage` (`org_owner`, `org_admin`; `docs/runbooks/org-team.md`); a `Staff` collaborator, a
`PropertyManager` (it does not manage plan and invoices), an accountant (`org.billing.read` only) and a member of the org
with any other DB role get 403.
The same policy protects the custom domain and the Stripe Connect account endpoints: a long-term landlord can call
them too, but the web app shows those pages only in the short-rent shell.

| Page | What it does |
|---|---|
| `/app/short-rent/settings/plan` ("Piano") | Plans of `GET /api/billing/plans`: name, allowance, features and the price only when the API sends one (`Billing:Display:<Tier>:PriceMonthly` > 0, otherwise "the price is shown on Stripe"). A plan with `purchasable: false` is shown as "Non disponibile". "Scegli piano" asks country and optional VAT id, then `POST /api/billing/checkout-session` and the redirect to Stripe. An org with a live subscription (active, trialing, past due, unpaid, incomplete) gets "Cambia dal portale" instead of a second checkout. |
| same page, `?checkout=success` | Reads `GET /api/billing/subscription` every 3 s for up to 60 s: "Pagamento confermato" only once the webhook has made it active or trialing; incomplete/unpaid/past due show the payment notice with "Completa il pagamento" (portal); after 60 s without the webhook, "conferma non ancora ricevuta" with a refresh button. The redirect alone never shows success. |
| same page, `?checkout=cancel` | "Pagamento annullato, nessun addebito" and the real state of the plans. |
| `/app/short-rent/settings/billing` ("Fatturazione") | Subscription (effective plan, status badge, next due date), "Gestisci pagamenti" → `POST /api/billing/portal-session`, notices for past due (grace), incomplete and unpaid, and the billing profile form (country, VAT id) → `PUT /api/billing/profile`. |

Error codes shown to the user: 409 `already_subscribed` (message with the portal button), 409 `billing_gate_closed`,
422 `billing_plan_unavailable` (the plans are read again), 503 `billing_return_url_not_configured`,
503 `payment_provider_error`. The portal answers 400 while the org has no Stripe customer (never started a checkout):
the web app says the portal is available after the first payment.

VAT id: the web app and the backend only check its shape (letters and digits, 4-20 characters, spaces, dots and dashes
removed) and store it as declared. Since PL-13 the VAT is computed by **Stripe Tax** on the checkout and the renewals;
the VAT id that counts is the one entered in Stripe Checkout, verified by Stripe (VIES). `Vies__StubMode` no longer
exists. Details, Stripe Tax settings and the e-invoices: [billing-tax.md](billing-tax.md).

Return pages: the plan and billing pages send their own path as `returnPath` (checkout and portal), so Stripe brings the
user back to the page and the shell it started from (PL-16); the backend accepts only the allow-listed pages.

Verification on the test environment (Staging, test keys): open "Piano" as a host owner, choose Pro, country Italia,
pay with `4242 4242 4242 4242`; back on the page the banner goes from "Stiamo verificando" to "Pagamento confermato",
the Pro card shows "Piano attuale" and "Fatturazione" shows Attivo with the next due date. On the same page the other plans
offer "Cambia dal portale", which opens the Stripe portal. The incomplete, unpaid and past due states are covered by the
frontend tests (`plans-page.test.tsx`, `billing-settings-page.test.tsx`).

## Refunds and booking cancellations on Stripe Connect (BK-02)

Task BK-02 (audit defects A3-05 P0, A9-15 payments part; issue #51). Before it, "Rimborsa" only changed the database,
`POST /api/payments/{id}/process` marked a payment Completed without Stripe, and cancelling a paid booking kept the money.

### Charge model (verified in the code)

The checkout creates the guest's PaymentIntent **on the host's connected account** (`StripeService.CreateConnectedAccountPaymentIntentAsync`
and, for the deferred option, `ChargePaymentMethodAsync` (off-session, BK-08, section "Deferred charge" below), both with the `Stripe-Account` header and no `application_fee_amount` (A3-40: left unset, not sent as an explicit `0` — there is no platform take-rate yet)):
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
| `Pending` hold expired, or `Cancelled` (expiry job, host, legacy) | free: no confirmed / checked-in booking, no valid hold, no iCal block | **confirmed again** (`CancellationReason` cleared), the same two emails with a note on the late payment |
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
cancellation, settings) is in [direct-booking.md](direct-booking.md) § 9; this section covers the Stripe calls.

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
| `application_fee_amount` | not sent | A3-40 (BK-19): left unset rather than an explicit `0` — no platform take-rate yet, and Stripe treats the two differently. Set it to the real fee if/when a take-rate ships |
| Idempotency key (creation) | `direct-booking-deadline:{bookingId}:{deadline yyyyMMdd}:{attempt}` | bound to the booking, its deadline and the attempt: a Hangfire retry or a crash before the commit sends the same key and gets the same PaymentIntent |
| Idempotency key (retry) | `direct-booking-deadline-confirm:{PaymentIntentId}:{attempt}` | one confirmation per attempt |
| Before a creation | `GET /v1/payment_intents?customer=…` on the connected account | Stripe keeps idempotency keys for 24 hours and the job runs daily: a PaymentIntent of an attempt whose answer was lost (timeout) is found by `metadata.bookingId` + `kind` and used instead of creating a second charge |

Status mapping (`DeferredCharges.StatusOf`): `succeeded` → payment `Completed`; `processing` (SEPA) / `requires_capture` →
`Processing`, completed by the webhook; `requires_action`, `requires_payment_method`, `requires_confirmation` (and the 402
answer) → `Failed`, the guest must act; `canceled` → `Canceled` (canceled outside CasaZen: no new attempt, see
direct-booking.md § 9).

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

## Recurring rent of long-term leases (LT-06)

Task LT-06 (#269, audit defect A7-07, P1). Before it `NullRentBillingService` threw `NotImplementedException`, there was
no job, endpoint or UI, and the `rent-charge` webhooks were claimed as processed and dropped. Code:
`Casazen.Infrastructure/Services/RentBillingService.cs`, plan rules `Casazen.Core/Leases/RentInstallmentPlan.cs`,
settings `Casazen.Core/Services/RentCharges.cs`, API `LeaseRentController` / `PublicRentPaymentsController`, job
`RentCollectionJob` ([hangfire.md](hangfire.md) § 12).

### How it works

1. **Schedule** (`PUT /api/leases/{id}/rent/schedule`, lease signed by every party): cadence (monthly, bimonthly,
   quarterly, semiannual), due day 1-28 (default: the start day of the lease) and installment amount (default: the
   lease's monthly rent × the months of the cadence). Periods are anchored on the lease start; each installment is due on
   the first due day on or after the start of its period. A final period shorter than the cadence gets **no installment**
   (no pro rata rule is assumed): the page shows it to the landlord. The cadence cannot change once an installment is
   paid or has a PaymentIntent.
2. **Payment request**: the daily job emails the tenants (every tenant party with an email, not anonymized) a personal
   link `/rent/pay/{installmentId}?token=…` `RentBilling:PaymentRequestDaysBeforeDue` days before the due date (only for
   installments that were not already due when generated; older ones: the landlord marks them paid or sends the link
   from the page). The landlord can (re)send the link of an installment at any time; a new link replaces the previous one.
3. **Payment**: the tenant pays on the public page with the Stripe Payment Element, **direct charge on the org's
   connected account** (like the direct checkout): `Stripe-Account` header, automatic payment methods, no application fee
   (A3-40), `metadata.kind = rent-charge`, `metadata.rentLedgerEntryId`, `leaseId`, `orgId`, idempotency key
   `rent-charge:{installmentId}:{n}`. One PaymentIntent per installment at a time: reused while payable with the same
   amount, canceled (key `rent-charge-cancel:{installmentId}:{pi}`) when the amount changed.
4. **Offline payment** (`POST …/installments/{id}/mark-paid`): the landlord declares a payment received outside CasaZen
   (date not in the future, optional note). A payable PaymentIntent is canceled first; one paid or in flight meanwhile
   wins and the declaration is refused (409).
5. **Disable** (`POST …/schedule/disable`): unpaid installments become `Cancelled` (their payable PaymentIntents canceled);
   paid ones are kept. Saving the schedule again re-enables it.

States are honest: `Scheduled` (to collect, "overdue" computed on read), `Processing` (Stripe `processing`, e.g. SEPA),
`Paid` (only from `succeeded` or the landlord's declaration), `Failed` (last online payment failed; the tenant can pay
again), `Cancelled`.

### Webhooks

`StripeWebhookHandler` sends every `payment_intent.*` event with `metadata.kind = rent-charge` to `RentBillingService`,
from the **Connect endpoint** and from the platform endpoint **when the event carries `account`**; an event of the
platform account itself, or of an account other than the installment's, is ignored (logged). Exactly once like every
event (PL-10); the lease's rent lock (`PostgresAdvisoryLocks.Scope.RentLease`) is taken in the event transaction.

| Event | Effect |
|---|---|
| `payment_intent.succeeded` | installment `Paid` (via Stripe, `PaidOn` = Rome date); the landlords get "Canone ricevuto". A second payment of an installment already paid is logged as an error: refund it from the Stripe dashboard |
| `payment_intent.processing` | `Processing` (add this event to the Connect endpoint; otherwise the daily job reads the status) |
| `payment_intent.payment_failed` | `Failed` with the Stripe error code; when the payment was already in flight (SEPA returned) the tenants get a new link |
| `payment_intent.canceled` | the current PaymentIntent is dropped; the tenant can start a new one |

### Settings (Railway, optional)

| Variable | Default | Meaning |
|---|---|---|
| `RentBilling__PaymentRequestDaysBeforeDue` | `5` (**provisional technical default**, to be decided by the product owner) | days before the due date on which the payment link is emailed (0-31) |

### Stripe settings to check (product owner)

1. Connect endpoint: `payment_intent.succeeded`, `payment_intent.payment_failed`, `payment_intent.canceled` (already
   needed by the checkout) and `payment_intent.processing` (recommended for SEPA).
2. Restricted key only (`rk_…`): **PaymentIntents: Write** on connected accounts (create, read, cancel).
3. The landlord org must have completed the Connect onboarding (payments page `/app/short-rent/settings/payments`) with
   charges enabled; otherwise the rent page offers only offline payments.

### Not covered (open questions)

Rent receipts and stamp duty, ISTAT updates, automatic off-session debit (SEPA mandate), reminders after the due date,
pro rata of a partial final period, who receives the money when the org is an agency and not the landlord: see the
DUBBI of LT-06.

### Verification

1. Automated: `RentInstallmentPlanTests`, `LeaseRentPostgresTests` (schedule, job request once, payment session reused,
   webhook on the connected account, foreign account ignored, offline payment cancels the PaymentIntent, SEPA failure
   emails a new link, disable).
2. Test mode: sign a lease (offline upload), open the lease page, generate the schedule, press "Invia link di pagamento",
   open the email link, pay with `4242 4242 4242 4242`: after `payment_intent.succeeded` the installment is "Pagata" and
   the landlord receives "Canone ricevuto". With a SEPA test IBAN the installment is "Pagamento in corso" until Stripe
   settles it.

## Connect onboarding: the linked account survives Stripe errors (BK-09)

Task BK-09 (audit defects A3-19 P1, A3-42). Before it any Stripe error while reading the org's connected account
(rate limit, timeout, key problem) was taken for a "stale" account: `StripeConnectedAccountId` was cleared and a new
Express account created, without idempotency key. A host who clicked "Collega Stripe" during a 429 lost the verified
account: checkouts answered 409 and the deferred charges (customer and card saved on the old account) failed. The
controller also used `property.write` and sent the host back to the `returnUrl` / `refreshUrl` chosen by the client.

### What unlinks an account, and what does not

`StripeConnectGateway` turns every failure into a `StripeConnectException` with one `StripeConnectFailure`
(`Casazen.Core/Exceptions/StripeConnectException.cs`). Stripe.net 50.1 (`SystemNetHttpClient`) has already retried
connection errors, 409, 5xx and the 429 marked `Stripe-Should-Retry` before this point.

| Stripe answer | Failure | `POST /api/connect/account`, `/onboarding-link` | `GET /api/connect/status?refresh=true` |
|---|---|---|---|
| error code `resource_missing` (404) or `account_invalid` (e.g. 403 "does not have access to account … Application access may have been revoked") | `AccountUnavailable` | a **new** account replaces it (log `… does not exist or was revoked …; creating a replacement`) | capabilities set to false (checkout gate closed); the id stays until the onboarding replaces it |
| 429 (`rate_limit`, `lock_timeout`), 409, any 5xx, network error, HTTP timeout | `Transient` | **503 `stripe_connect_unavailable`** + `Retry-After: 10`, account untouched | same |
| 401, 403 without `account_invalid`, `api_key_expired`, `platform_api_key_expired`, `secret_key_required`, key missing or placeholder | `Configuration` | **503 `stripe_connect_not_configured`**, account untouched | same |
| any other refusal (400 `invalid_request_error`, `idempotency_error`) | `Rejected` | **502 `stripe_connect_failed`**, account untouched | same |
| account gone between the read and the Account Link | `AccountUnavailable` | 409 `stripe_connect_account_unavailable`: the next click replaces it | – |

The error codes are values of the `ErrorCode` list that the official SDK generates from Stripe's OpenAPI spec
(stripe-go `error.go`, checked 2026-09-24: `resource_missing`, `account_invalid`, `rate_limit`, `lock_timeout`,
`api_key_expired`, `platform_api_key_expired`, `secret_key_required`); descriptions in https://docs.stripe.com/error-codes.
Stripe.net 50.1 has no constants for them. A network failure reaches the gateway as `HttpRequestException` or as the
timeout's `OperationCanceledException`, not as `StripeException` (`SystemNetHttpClient.SendHttpRequest`).

The payments page of the web app shows the message of the code with **"Riprova"** for `stripe_connect_unavailable`,
`stripe_connect_account_unavailable` and network / generic 5xx errors, never a "Non collegato" state it did not read.

### One account per org

- **Lock**: `EnsureExpressAccountAsync` runs under the advisory lock `OrgConnectAccount` (1012, key = org id), Stripe
  calls included: a second click waits, then reads the account the first one created.
- **Idempotency key** of `POST /v1/accounts`: `connect-account:{orgId}` for the first account,
  `connect-account:{orgId}:replaces:{oldAccountId}` for the replacement of an unavailable one (the first key would
  return the old account). A retry whose answer was lost gets the same account from Stripe. Stripe keeps a key for at
  least 24 hours: a creation retried later may create a second account, visible in Dashboard → Connect → Accounts (the
  org links only the last one).
- The e-mail sent to Stripe is the org's contact e-mail, else its oldest user's: stable across retries (a different
  e-mail under the same key answers `idempotency_error`).

### Who may start it, and where Stripe sends the host back (A3-42)

| Endpoint | Policy |
|---|---|
| `POST /api/connect/account`, `POST /api/connect/onboarding-link` | `RequireOrgBillingAdmin` (`CasazenPolicies.OrgBillingAdmin`, TN-3): a `Staff` collaborator gets 403 |
| `GET /api/connect/status` | `payment.read` in short-rent |

The Account Link pages are built by the API from `App__PublicSiteBaseUrl` (decision D3, same helper `PublicSiteLinks`
as the billing return pages of PL-11): `return_url` =
`{App__PublicSiteBaseUrl}/app/short-rent/settings/payments?stripe_return=1`, `refresh_url` = `…?stripe_refresh=1`. A
request body with `returnUrl` / `refreshUrl` is ignored. Without `App__PublicSiteBaseUrl` (possible only in
Development/Testing) `POST /api/connect/onboarding-link` answers **503 `connect_return_url_not_configured`** before any
Stripe call.

### Stripe settings to check (product owner)

1. Nothing new on the Dashboard. Keep the platform key in the mode of the environment (PL-11 stops a `Production` start
   with a test key): with a key of the other mode Stripe answers `resource_missing` for every existing account, and the
   next "Collega Stripe" of each host would link a new account of that mode.
2. Restricted key only (`rk_…`): **Accounts: Write** and **Account Links: Write** (Connect).

### Verification

1. Automated: `StripeConnectGatewayTests` (mocked `IStripeClient`: 404 `resource_missing` / 403 `account_invalid` →
   unavailable, 429 / 409 / 5xx / network / timeout → transient, 401 / 403 / key → configuration, idempotency key and
   Account Link URLs sent), `ConnectOnboardingServiceTests` (429 → account unchanged, `resource_missing` → replacement
   with its key), `ConnectOnboardingIntegrationTests` (429 → 503 with the account unchanged, two parallel clicks → one
   account on PostgreSQL, `Staff` collaborator → 403, server-side URLs), web `payments-page.test.tsx`.
2. Test mode: connect a test account, then delete it in the Stripe Dashboard (Connect → Accounts → the account →
   Delete): the next "Collega Stripe" links a new account (log `creating a replacement`). Set an invalid secret key on
   the API and click "Collega Stripe": 503 `stripe_connect_not_configured`, `Orgs.StripeConnectedAccountId` unchanged.

## Connect onboarding of the suppliers (SP-14)

Redesign wave task SP-14 (branch `feature/rd-supplier-payments-account`, backend only: no screen yet, the supplier settings page
is SP-16). Decision D2: the supplier is paid with a **direct charge on its own Stripe account** (Express, v1: `type=express`,
`StripeConnectGateway`), with a platform commission (D3) that comes with SP-15. This task is **only the account**: the supplier
connects it, CasaZen reads its state and opens its Express Dashboard. **No PaymentIntent, commission, payment record, payment
page, refund or earnings existed in SP-14** (SP-15 added the payment and the commission, see [Services of the suppliers](#services-of-the-suppliers-sp-15); the earnings pages of SP-16 and SP-17 are still to come); `StripeServiceApplicationFeeTests` is untouched, so the rule "no
application fee on guest bookings and rent" (A3-40, [Charge model](#charge-model-verified-in-the-code)) still holds.

### Same engine as the host, other org and other routes

The supplier's account is the `Org` of the supplier (`OrgType.Supplier`), and `ConnectOnboardingService` works on any org: the same
advisory lock (`OrgConnectAccount`, key = org id), the same idempotency key (`connect-account:{orgId}`, `…:replaces:{old}` for a
replacement), the same rule "only `resource_missing` / `account_invalid` replaces the account" and the same failure table as the
host's onboarding (BK-09, section above). The host's `ConnectController` and `ConnectOnboardingService` are **not changed**. A
dual-role account (a host who is also a supplier) has **two accounts**: `api/connect/*` works on its host org, `api/supplier/payments/*`
on its supplier org (`User.SupplierOrgId`), each with its own return pages.

### Endpoints (`SupplierPaymentsController`, policy `RequireSupplier`, behind `Features__SupplierOnlinePayments`)

The org is the caller's own supplier link (`ISupplierOrgContextResolver.GetLinkedSupplierOrgIdAsync`): it is never provisioned
(404 `not_found` without one) and an org without a supplier profile never gets an account through these routes.

| Method and path | What it does |
|---|---|
| `GET api/supplier/payments/account[?refresh=true]` | The state **from the database** (what `account.updated` stored); with `refresh=true` Stripe is read first (one call, only when an account is linked) and the answer is saved. Without an account every flag is false and Stripe is not called |
| `POST api/supplier/payments/account` | Creates the Express account when missing (idempotent, one account for parallel requests) and returns the state. The onboarding link does the same; this is for a client that wants the account before the link |
| `POST api/supplier/payments/onboarding-link` | `{ "url" }`: the Account Link (single use). Creates the account when missing. A request body is ignored |
| `POST api/supplier/payments/dashboard-link` | `{ "url" }`: the single-use login link to the supplier's **Express Dashboard** (`POST /v1/accounts/{id}/login_links`, new `IStripeConnectGateway.CreateDashboardLoginLinkAsync`). **Needs an existing account**; it never creates one |

Answer of the state: `hasAccount`, `chargesEnabled`, `payoutsEnabled`, `detailsSubmitted`, `requirementsDue` (the **names** of the
fields Stripe still needs, e.g. `external_account`), `canReceivePayments` (account linked **and** charges **and** payouts enabled:
what SP-15a requires before a job is taken as payable online), `verified` and `verificationMissing`. There is no bank data, no document
data and no Stripe account id in it. The responses carry `Cache-Control: private, no-store`: the links are credentials, they are
never logged and never stored.

Errors (ProblemDetails `code`): the Stripe ones are those of the host's onboarding, with the same texts: 503
`stripe_connect_unavailable` (+ `Retry-After: 10`), 503 `stripe_connect_not_configured`, 502 `stripe_connect_failed`, 409
`stripe_connect_account_unavailable` (only the two link routes: the linked account is gone, the next onboarding link replaces it; a
status read never answers it, it only clears the capabilities), 503 `connect_return_url_not_configured` (onboarding link). New:
**422 `supplier_payments_not_ready`** (`SupplierPaymentsNotReady`, Italian and English) for a dashboard link without an account,
**and** when Stripe refuses the login link as a rejected request, which is how an account that has not completed the onboarding is
expected to answer (the account stays linked: the supplier finishes it with the onboarding link). That last mapping is proved with a
mocked Stripe only: check it once in test mode (Verification, point 2).

### Return pages (built by the server)

`PublicSiteLinks` (same helper and rule as the host's, D3, no domain in code): `return_url` =
`{App__PublicSiteBaseUrl}/app/supplier/settings?stripe_return=1`, `refresh_url` = `…?stripe_refresh=1`
(`SupplierConnectOnboardingReturn()` / `SupplierConnectOnboardingRefresh()`). They are not in `BillingReturnPagePaths` (the pages of
the Checkout and billing portal of the plans: a supplier has no plan page, and that list is mirrored by the web app's
`billing-routes.ts`), which is unchanged. What the supplier settings page has to do (SP-16): on `?stripe_return=1` read the state with
`GET …/account?refresh=true` (Stripe may need a moment: poll once or twice if `detailsSubmitted` is still false); on
`?stripe_refresh=1` (expired or already used link) ask for a new `onboarding-link` and send the supplier there. Without
`App__PublicSiteBaseUrl` (Development/Testing only) the onboarding link answers 503 `connect_return_url_not_configured` before any
Stripe call.

### "Verificato" (decision D11)

`SupplierVerification` (one rule, read-only; the activation is not changed by it): the profile is `Active`, the account
`canReceivePayments`, and the profile has a VAT number (P.IVA, not blank). `verificationMissing` lists what is not true yet, in this
order: `profile_not_active`, `payments_not_enabled`, `vat_number_missing`. It is part of the state; the public showcase still does
not show it (it stays `noindex`, D11).

### Feature flag and webhook

`Features__SupplierOnlinePayments` (default **off**, [feature-flags.md](feature-flags.md)): while it is off the four routes answer **404
before authentication**, like a missing route. The processing of Stripe's events is **not** behind it: `account.updated` of a
connected account (Connect endpoint) is applied by `StripeWebhookHandler` to `ConnectOnboardingService.ApplyAccountUpdatedAsync`,
which finds the org by `StripeConnectedAccountId` and updates **that org only** (a supplier's or a host's alike; an unknown account
is logged and ignored). The supplier's `GET …/account` reads what it stored.

### Stripe settings to check (product owner)

1. **Connect webhook endpoint** (`/webhooks/stripe/connect`, "Connected accounts"): `account.updated` must be among the events (the
   hosts already need it, `docs/INFRA.md`). Nothing new to add.
2. **Express Dashboard**: it is what the login link opens (Express accounts have it by default). Review the platform's name, branding and
   support details that Stripe shows the supplier there (Dashboard → Settings → Connect); the link itself is created by the API.
3. **Restricted key** (`rk_…`) only: besides **Accounts: Write** and **Account Links: Write** (BK-09), the key needs the permission that
   covers `POST /v1/accounts/{id}/login_links` (Connect; check the key's permission list in Developers → API keys). With the standard
   secret key nothing to do.
4. The supplier's onboarding collects the identity and the bank account **on Stripe**; CasaZen stores none of it. Which legal texts the
   supplier accepts before connecting (ToS §1/§6 are written for another charge model and must be aligned to D2, `[CONSULENTE LEGALE]`)
   is the open point of LG-01: keep the flag off in production until they are approved.

### Verification

1. Automated: `StripeConnectGatewayTests` (login link: path, classified errors, network, missing key, blank account),
   `SupplierPaymentsAccountServiceTests` (state from the database / from Stripe on request, account created once, links, dashboard
   link only with an account, Stripe refusal → 422, an org without supplier profile is never reached), `SupplierVerificationTests`,
   `PublicSiteLinksTests` (supplier return pages, host ones and the billing allow-list unchanged), `SupplierPaymentsIntegrationTests`
   (HTTP: 401, 403, 404 without link, flag off = 404 also anonymous, state, server-side return URLs ignoring client URLs, one
   account for repeated and for **parallel** requests on PostgreSQL, Stripe failures, dual-role account, two suppliers isolated),
   `SupplierAccountUpdatedWebhookTests` (`account.updated` updates the supplier's org and no other), `SupplierPaymentsControllerGuardTests`
   (policy, flag and the four routes), `StripeServiceApplicationFeeTests` (unchanged).
2. Test mode (Staging, `Features__SupplierOnlinePayments=true`, test keys): as a supplier `POST …/onboarding-link`, complete the Express
   onboarding with Stripe's test data, come back to `/app/supplier/settings?stripe_return=1` and `GET …/account?refresh=true`: `hasAccount`,
   then `chargesEnabled` and `payoutsEnabled` true once Stripe verified it; `POST …/dashboard-link` opens the Express Dashboard;
   before the onboarding is finished it is 422 `supplier_payments_not_ready`. Stripe sends `account.updated`: the plain `GET …/account`
   follows without `refresh`.

## Services of the suppliers (SP-15)

Redesign wave task SP-15 (decisions D2, D3, D5, D7 and D24; gap report 05 §4.3), in two stacked pull requests. **SP-15a** (branch `feature/rd-supplier-payments`, backend only, stacked on SP-14 and SP-04) is what this section describes: the model, the configuration, the gateway with the commission, the payment session, the payer's page, the supplier's request and offline record, and the emails. **SP-15b** (branch `feature/rd-supplier-payments-webhook`) adds the Stripe webhook that makes a payment *paid*, the reminder and sync jobs, refunds, the admin tools and the monthly commission export. No screen exists yet: the public page `/service/pay/:paymentId` and the supplier console are frontend tasks.

**Keep `Features__SupplierOnlinePayments` off in production until SP-15b is deployed.** SP-15a can create a payment and open a Stripe session, but only the webhook of SP-15b records the money as received: a payment made online before it would stay `Requested` (or `Processing`) in CasaZen.

### Charge model (decision D2): a direct charge, CasaZen holds no funds

The payer pays the **supplier's own Stripe Express account** (the one of SP-14); CasaZen takes its commission as the `application_fee_amount` of the same charge. Same family as the guest bookings and the rent ([Charge model](#charge-model-verified-in-the-code)), with one difference: **this is the only PaymentIntent of the application that carries an application fee**.

| Parameter | Value | Why |
|---|---|---|
| `Stripe-Account` header | the supplier's `acct_…` (`Orgs.StripeConnectedAccountId` when the PaymentIntent is created; saved on the payment as `ConnectedAccountId`) | direct charge: the PaymentIntent exists only on the supplier's account |
| `application_fee_amount` | the commission snapshot of the payment, **only if strictly between 0 and the amount**; otherwise the parameter is **left out** | Stripe wants `0 < fee < amount`; an explicit `0` is never sent (A3-40). Set in `StripeSupplierPaymentGateway` and nowhere else (`ApplicationFeeArchitectureTests`) |
| `automatic_payment_methods` | enabled | like the checkout and the rent: the Payment Element offers what the supplier's account supports |
| `metadata` | `kind = service-charge`, `serviceRequestPaymentId`, `serviceRequestId`, `supplierOrgId` | ids only, no name, no email, no address. The webhook of SP-15b routes on `kind` **before** the generic `payment_intent.succeeded` case (as for the rent) |
| `Idempotency-Key` (creation) | `service-charge:{paymentId:N}:{n}`, `n` = `PaymentIntentCount` after the increment | a retry whose answer was lost (timeout, a failed save) sends the same key and gets the same PaymentIntent (Stripe keeps a key for 24 hours) |
| `Idempotency-Key` (cancellation) | `service-charge-cancel:{paymentId:N}:{pi}` | one cancellation per PaymentIntent |
| `description` | `Service request {requestId}` | no personal data |

The supplier receives the price **minus CasaZen's commission and minus Stripe's own fees** (decision D3: Stripe's fees stay on the supplier's account); the payout to its bank follows the payout settings of its Stripe account, which CasaZen neither anticipates nor holds back. No text of CasaZen promises a payout time (D24). `StripeServiceApplicationFeeTests` (guest bookings and deferred charges carry **no** fee) is untouched; the architecture test `ApplicationFeeArchitectureTests` makes the rule "the fee is set in the supplier payments gateway only" fail the build if any other code sets one.

### The commission (decision D3)

`SupplierPayments:CommissionPercent` (Railway: `SupplierPayments__CommissionPercent`, committed value **10, provisional**: the product owner's hypothesis). **It is configuration, never code:** the options class has no default of the percentage and the application does not start without a value from 0 to 50 with at most two decimals. A supplier can have its own percentage (`SupplierProfiles.CommissionPercentOverride`, e.g. 0 for a free period; an admin sets it in SP-15b). The commission is `amount × percent / 100` rounded to the cent **away from zero**; if it is 0 or not strictly below the amount it is not charged and not sent.

| Price | Percentage | Commission | Net to the supplier (before Stripe's fees) |
|---|---|---|---|
| 60,00 € | 10 | 6,00 € | 54,00 € |
| 60,55 € | 10 | 6,06 € (6,055 rounded up) | 54,49 € |
| 0,50 € | 10 | 0,05 € | 0,45 € |
| 60,00 € | 0 | none, `application_fee_amount` not sent | 60,00 € |

**Snapshot.** The percentage, the commission and the net are computed **once, when the payment is created**, and kept on the row (`CommissionPercent`, `ApplicationFeeCents`, `NetCents`): changing the configuration later never rewrites a payment, and the PaymentIntent is created with the snapshot (a PaymentIntent whose amount or fee no longer matches the payment is canceled and made again). The webhook of SP-15b compares what Stripe reports with the snapshot. The VAT on the commission and DAC7 are **open** (`[CONSULENTE FISCALE]`, decision D4): `FeeVatMode` and `FeeVatCents` exist on the row and stay empty, no rate is configured, and nothing is sent to the tax authority.

### The flow

| Moment | What happens |
|---|---|
| The supplier **takes** the request (`take`, or the host accepting the time the supplier proposed) | `PaymentMode` is fixed: `Online` only if the flag is on **and** the supplier's Stripe account is linked with charges **and** payouts enabled (`SupplierVerification.CanReceivePayments`: no payment before the KYC), else `Manual`. It never changes afterwards, except the fall-back below |
| The supplier **completes** it (`complete`) | `Online` with a final amount of at least `MinAmountCents` (50, Stripe's minimum for euro) and the flag still on: a `ServiceRequestPayment` is created `Requested`, in the same save as the completion, with the snapshot, and the **link** is emailed to the host org's address. If the final amount is missing or below the minimum, or the flag was switched off meanwhile, the request **falls back to `Manual`** (no payment; the host marks it paid as before) so nothing is stuck |
| The final amount is **above the quote by more than the tolerance** (decision D7: 20 % by default, `Suppliers__ServiceRequests__FinalAmountTolerancePercent`) | no payment yet: the host confirms with `POST api/service-requests/{id}/final-amount/confirm` (and the long-rent twin), which clears `price.needsCustomerConfirmation`, keeps the trace in `FinalAmountConfirmedAt`, creates the payment and sends the link. Confirming twice is not an error |
| The supplier cannot be paid at that moment, or the host org has no email | the payment exists but is **pending**: no token, nothing sent. The supplier asks again when it can (`payment-request`); SP-15b also sends the pending ones when `account.updated` makes a supplier ready |
| The payer opens the **link** `/service/pay/{paymentId}?token=…` | `POST api/public/service-payments/{id}` (the page) and `…/payment-session` (the PaymentIntent for the Payment Element) |
| The payment is **paid** | SP-15b: the webhook, only if account, amount, currency and fee match the snapshot, else `NeedsReview` and no "Pagato" |
| The host tries `mark-paid` on an `Online` request | 422 `service_request_online_payment` (decision D5): it is paid with the link |
| The supplier **records a payment received outside CasaZen** (`payment/offline`) | the traced exception (D5): required `reason` for an `Online` request, the waiting payment is withdrawn and its PaymentIntent canceled, an offline payment **without commission** is kept, the request becomes `Pagato` (its history credits the payment to the supplier, `ServiceRequests.PaidBy`), the host is told with the reason |

### The link and the payment session

- **Token.** 256 random bits, URL-safe; only its **SHA-256** is stored (`PaymentTokenHash`), compared in constant time (the same `CheckoutOutcomes` as the rent and the checkout). It goes in the **body** of the API calls, never in their URL. Each email replaces the previous token (the old link stops working). A link is valid **30 days** (`PaymentLinkValidityDays`) from the email that carries it, for a payment still to be made; a paid payment keeps showing its state.
- **One answer for everything.** A wrong payment id, a wrong token, a payment that has no link yet (pending) and an expired link all give the same `404 service_payment_link_invalid` with the same text: nothing tells whether a payment exists. A token that is missing or longer than 128 characters is a 400 `validation_error`. Rate limit `PublicBookingLookup` per client IP. The answers carry `Cache-Control: private, no-store`.
- **What the page shows.** The supplier, the service, the payer's own property, the date, the price and its lines, the state (`Payable`, `Processing`, `Paid`, `Unavailable`), whether the last attempt failed (`lastAttemptFailed`) and, for a payment still to be made, until when the link works. **Never** the commission or the net, the address or a contact of anyone, the token or the Stripe account (the names it shows are the supplier's business name, the service and the payer's own property).
- **The session** runs under the advisory lock `ServiceRequestPayment` (scope **1_320**, key = the request id) in a READ COMMITTED transaction held for the Stripe calls, and reads the payment again after the lock. It creates the PaymentIntent on the supplier's account **or reuses the current one** while it is payable (`requires_payment_method`, `requires_confirmation`, `requires_action`) **and** its amount, currency, fee and account are still the payment's. If any of them changed, or the supplier replaced its Stripe account, the old PaymentIntent is canceled (when it is still payable) and a new one is created with the next key; one that Stripe says no longer exists (`resource_missing` / `account_invalid`: the account it lived on was deleted) is dropped without waiting for it, while any other Stripe failure is a 503 and keeps the payment as it is. One that Stripe reports as paid or in progress (`succeeded`, `processing`, `requires_capture`) is **never created again**: the payment is shown as `Processing` (409 `service_payment_in_flight`) until the webhook of SP-15b settles it. A supplier whose account cannot take charges and payouts any more gives 409 `service_payment_supplier_not_ready` without calling Stripe. The signed-in host has the same session without the link: `POST api/service-requests/{id}/payment-session` (`PropertyWrite` + `HostResource`; long-rent twin under `api/long-rent/service-requests`).
- **Concurrency.** Two sessions of the same payment (the link and the console, two tabs) create **one** PaymentIntent: the second waits for the lock and finds the first. The unique partial index `UIX_ServiceRequestPayments_ServiceRequestId_Live` allows one payment per request that is not `Canceled`, and `UIX_ServiceRequestPayments_StripePaymentIntentId` one payment per PaymentIntent; a violation or a request changed under the `xmin` token is a **409**, never a 500.

### Endpoints

| Endpoint | Who | Notes |
|---|---|---|
| `POST api/public/service-payments/{id}` | anyone with the link | the page; 404 `service_payment_link_invalid`; **not behind the flag** |
| `POST api/public/service-payments/{id}/payment-session` | anyone with the link | `{ paymentId, clientSecret, publishableKey, stripeAccountId, amountCents, currency }` for Stripe.js; 409 `service_payment_not_payable` / `service_payment_in_flight` / `service_payment_supplier_not_ready`; 503 `payment_provider_error` if Stripe fails; **not behind the flag** |
| `POST api/service-requests/{id}/payment-session`, `POST api/long-rent/service-requests/{id}/payment-session` | the host (`PropertyWrite` / long-rent `property.write` on the request's property) | the same session without the link; 404 `service_payment_not_found` when the request has no payment; **not behind the flag** |
| `POST api/service-requests/{id}/final-amount/confirm`, `POST api/long-rent/service-requests/{id}/final-amount/confirm` | the host | decision D7; 422 `service_request_no_confirmation_needed` |
| `POST api/supplier/requests/{id}/payment-request` | the supplier | **behind the flag** (404 before authentication while off): sends the request or, if one went already, a reminder with a new link; at most one a day (422 `service_payment_request_too_soon`); 422 `supplier_payments_not_ready`, `service_payment_not_online`, `service_payment_not_requestable`, `service_payment_amount_unconfirmed`, `service_payment_amount_required`, `service_payment_no_recipient`, `service_payment_request_not_sent`; 409 `service_payment_not_payable` / `service_payment_in_flight` when the payment is paid or being processed; 403 for a request of another supplier; the answer carries the split (gross, commission, net) |
| `POST api/supplier/requests/{id}/payment/offline` | the supplier | **not behind the flag**; body `{ reason? }` (≤ 500 characters, else 400 `validation_error`; required for an `Online` request); 409 if the payment is paid or in progress on Stripe |

The payment of a request is not yet in the `GET` answers of the consoles (they carry `paymentMode`); the earnings page and the payments list come with SP-15b and SP-16.

### Statuses of a payment

`Requested` → `Processing` → `Paid` (`PartiallyRefunded`, `Refunded`), `Failed` (the payer can try again with the same link), `Canceled` (dropped: the only status that frees the request for a new payment), `NeedsReview` (SP-15b). SP-15a creates `Requested`, sets `Processing` when a session finds a PaymentIntent paid or in progress, `Canceled` when the supplier records an offline payment, and `Paid` with `PaidVia = Offline` for that record. The values are stored as integers (0 to 7) and serialized by name: append only.

### Emails (decision D24)

`ServicePaymentRequest` and `ServicePaymentReminder` to the host org's contact address (the payer), `ServicePaymentReceived` to the supplier (used by SP-15b), `ServicePaymentOfflineRecorded` to the host; texts in `EmailTexts.resx` / `EmailTexts.en.resx`. They are true and short: the payer sees the price, who asks and the link's validity (never the commission); the supplier's receipt shows "prezzo al lordo, commissione CasaZen, netto per te, prima delle commissioni di Stripe"; **no text promises when the supplier is paid out** (`ServicePaymentEmailTemplatesTests` fails if one does). Queued on Hangfire after the save; a link that cannot be queued (provider not configured) is taken back and the payment stays pending.

### Stripe settings to check (product owner)

1. **Connect webhook endpoint**: nothing new for SP-15a. SP-15b needs `payment_intent.succeeded`, `payment_intent.processing`, `payment_intent.payment_failed`, `payment_intent.canceled`, `charge.refunded`, `refund.*` and `charge.dispute.created` on `/webhooks/stripe/connect`.
2. **Restricted key** (`rk_…`) only: **PaymentIntents: Write** on connected accounts (create, read, cancel). A PaymentIntent with `application_fee_amount` made by a restricted key was **not verified here**: try it in test mode (Verification, point 2); if Stripe asks for the Connect application-fee permission, add it. With the standard secret key nothing to do.
3. The supplier's account must have completed the onboarding of SP-14 (charges **and** payouts enabled): without it a request is taken as `Manual` and no payment is created.
4. Stripe's minimum charge in euro is 0,50 € (hence `MinAmountCents` 50): a completed job below it is paid by hand.

### Risks and open points (declared in the pull request)

- **Model A (direct charge):** the payout times depend on the supplier's own Stripe payout schedule; CasaZen cannot suspend a single payout and does not hold the funds. The drafts of the supplier and service legal texts (`fornitori`, `servizi`, LG-01) are written for another model and **must be aligned to D2** before the flag goes on; accepting them is not asked when a supplier connects Stripe.
- **VAT on the commission and DAC7** are `[CONSULENTE FISCALE]` open points (D4): fields ready, nothing decided or sent; commissions are invoiced by hand from the monthly export of SP-15b.
- **Mobile** (`paymentMode`, no new push type) was not verified; the Golden Journey L3 keeps using "Segna pagato", which stays valid for `Manual` requests (the flag is off in CI).
- **A request with no quote and no estimate** has nothing to compare the final amount with (SP-04), so decision D7 never asks the host to confirm it: the link goes out at once with whatever amount the supplier declared (up to the 100.000 € bound of every request). The payer chooses to pay or not, and the supplier can only record an exception: **to be confirmed by the PO** (require the confirmation for any amount of an online request that had no reference?).
- **Dispute flow** for an amount the host does not want to confirm (D7) is not built: the request stays completed until the host confirms, the supplier can record the exception, and an admin decides (follow-up).

### Verification

1. Automated: `SupplierCommissionTests` (rounding, thresholds), `SupplierPaymentsOptionsTests` (the commission required and 0 to 50), `StripeSupplierPaymentGatewayTests` (header, fee only when real, metadata, keys), `ApplicationFeeArchitectureTests` and `StripeServiceApplicationFeeTests` (the fee in one place), `SupplierPaymentModeTests`, `SupplierPaymentCompletionTests`, `SupplierPaymentRequestTests`, `SupplierPaymentOfflineTests`, `SupplierPaymentSessionTests`, `ServiceRequestPaymentTenancyTests`, `ServicePaymentEmailTemplatesTests`, `ServicePaymentsIntegrationTests` / `ServicePaymentsFlagOffTests` (HTTP) and, on PostgreSQL in CI, `ServicePaymentsPostgresTests` (parallel sessions create one PaymentIntent, the lock is held, two asks send one email, the offline record against a session, two confirmations) and `AddSupplierPaymentsPostgresTests` (migration up and down, checks, the unique indexes).
2. Test mode (Staging, `Features__SupplierOnlinePayments=true`, a supplier with a ready test account): the supplier takes a request (`paymentMode: Online`), completes it with an amount; the host's address gets the email; `POST api/public/service-payments/{id}/payment-session` with the token returns a client secret; in the Stripe Dashboard (connected account → Payments) the PaymentIntent shows the application fee of the snapshot; pay it with `4242 4242 4242 4242` from a test page and check the fee in the Dashboard (the payment becomes *paid* in CasaZen only with SP-15b). Try the restricted key too. Complete a request above the quote by more than 20 % and confirm it as the host.

## Operations

| Situation | What to do |
|---|---|
| `Error handling Stripe event {EventId}` in the logs | Nothing while Hangfire retries. If the job ends in *Failed*, fix the cause, then requeue it from the Hangfire dashboard or resend the event from Stripe (Developers → Events → the event → Resend): processing is idempotent |
| Log `Received Stripe … webhook` with an `api version` other than `2025-12-15.clover` | The endpoint is pinned to another API version (A3-39): events are accepted, but recreate that endpoint on the SDK's API version (new signing secret on Railway, then delete the old endpoint) and resend any event whose job failed |
| Stripe shows failed deliveries with 500 `stripe_webhook_not_configured` | Set the signing secret of that endpoint on Railway (`docs/INFRA.md`), redeploy; Stripe retries on its own for up to 3 days, older events can be resent |
| Log `Org {OrgId} has a second subscription …` | A duplicate subscription exists on Stripe (created before PL-10 or outside the app): the current one keeps the plan. Cancel the duplicate in Stripe and refund its payments |
| Log `Checkout refused for org {OrgId}: Stripe subscription … (webhook pending?)` repeating for the same org | The org pays but its webhook was not applied: check failed `StripeWebhookJob` jobs and resend the `customer.subscription.*` event |

## Verification

1. Automated: `StripeWebhookIdempotencyPostgresTests` (sequential and parallel duplicates on both endpoints, failure then retry, incomplete/unpaid without access), `BillingCheckoutPostgresTests` (repeated checkout, double click, other plan, incomplete), `StripeWebhookSubscriptionStateTests`, `WebhookSignatureTests`.
2. Test mode: from Stripe Dashboard resend the same event twice to the platform endpoint: the second job logs `Skipping duplicate Stripe event`.
3. Test mode: complete a checkout, then start another from the plans page: 409 `already_subscribed`, no second session in Stripe.
