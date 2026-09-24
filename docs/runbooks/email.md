# Runbook: transactional email (Resend)

Task FD-13, audit defects A9-06, A9-16, A4-06, A4-07, A4-08, A5-33, A4-20. Booking emails: task BK-10, defect A3-11.

## How it works

| Piece | Where | What it does |
|---|---|---|
| `EmailOptions` | `Casazen.Infrastructure/Email/EmailOptions.cs` | Typed configuration, section `Email` (`Provider`, `ApiKey`, `FromAddress`, `FromName`). No sender in code. |
| Startup validation | `Casazen.Web/Extensions/EmailServiceCollectionExtensions.cs` | Outside `Development` and `Testing` (so in `Production`, `Staging`, …) a missing or invalid value **stops the app at startup** with the list of problems (`OptionsValidationException`). |
| `ResendEmailService` | `Casazen.Infrastructure/External/ResendEmailService.cs` | The only `IEmailService`. Sends with the configured sender, unchanged: the old rewrite to `onboarding@resend.dev` is gone. |
| Templates | `Casazen.Infrastructure/Email/Templates/` | `EmailTexts.resx` (Italian, default) and `EmailTexts.en.resx`; `EmailTemplates` renders every email; every dynamic value (names, notes, property, reasons) is HTML-encoded. |
| Links | `PublicSiteLinks` | Every link comes from `App__PublicSiteBaseUrl`, the same public domain as the SEO canonical URLs and the sitemap ([`seo-domain.md`](seo-domain.md)). Missing or invalid value = configuration error, never a fallback domain. |
| Queue | `IEmailQueue` → Hangfire job `EmailDeliveryJob` | Request handlers only queue: a slow or failing provider never turns a saved operation into an error. Transient errors (timeouts, 429, 5xx) are retried 5 times, then the job is deleted. Recurring jobs that already run in Hangfire (check-in link) may send directly; the stay alerts (CO-10) queue theirs. |

Every email of the application is listed in [Complete list of emails](#complete-list-of-emails). Recipients have no language preference yet, so emails go out in Italian; the English texts are ready (see [Language](#language)).

When the provider is not configured (only possible in Development or Testing) every skipped email is logged as a warning: `Email <template> skipped: the email provider is not configured`.

## 1. Verify the sender domain on Resend (one time, product owner)

Without a verified domain Resend only accepts `onboarding@resend.dev`, which delivers **only to the owner of the Resend account**: in production no guest, host or supplier would receive anything (A9-06). The app refuses that sender outside Development.

1. Resend dashboard → **Domains → Add domain**. Enter the domain (or a subdomain) that will send the emails, e.g. the CasaZen domain. If asked for a region, pick the EU one.
2. Resend shows the DNS records to create: an **SPF** record (TXT) and a return-path **MX** record on the sending subdomain, and a **DKIM** record (TXT, `…._domainkey`). Copy them **exactly as shown** into the DNS provider of the domain. Do not merge the SPF value into another existing SPF record of a different host.
3. Recommended: a **DMARC** TXT record on `_dmarc.<domain>`, starting with a monitoring policy (`p=none`), then tighten it once the reports are clean.
4. Back on Resend → **Verify DNS records**. Wait until the domain status is **Verified** (DNS propagation can take from minutes to hours).
5. Resend → **API Keys → Create API key** with **Sending access** only, restricted to that domain if possible. The key starts with `re_`. Create one key per environment (test, production).

## 2. Railway variables (per environment: `test` and `production`)

Railway → service `casazen/backend` → environment → **Variables**:

| Variable | Value | Notes |
|---|---|---|
| `Email__Provider` | `Resend` | Only provider implemented. |
| `Email__ApiKey` | `re_…` | Key of step 1.5 for this environment. Secret: never in the repo. |
| `Email__FromAddress` | e.g. `noreply@<verified domain>` | Only the address; must be on the domain verified in step 1. `…@resend.dev` is rejected outside Development. |
| `Email__FromName` | `CasaZen` | Display name. |
| `App__PublicSiteBaseUrl` | `https://<web app of this environment>` | Base of every link (invite `/register`, supplier inbox `/app/supplier/inbox`, guest check-in `/checkin/{token}`). Absolute URL, https outside Development/Testing. |

Notes:

- The old variable `Email__ResendApiKey` is still read when `Email__ApiKey` is empty. Rename it to `Email__ApiKey` and delete the old one. If `Email__ApiKey` already exists with a SendGrid key (`SG.…`), replace it with the Resend key: validation rejects keys that do not start with `re_`.
- `Email__SendGridApiKey`, `Email__Smtp*`, `SendGrid__ApiKey`, `App__SupplierLoginUrl` and `App__FrontendBaseUrl` are no longer read: delete them.
- **Set the variables before the release reaches `main`.** From this version the production service does not start without `Email__ApiKey`, `Email__FromAddress` and `App__PublicSiteBaseUrl`: the deploy log shows lines such as `Email__FromAddress is missing: set a sender of the domain verified on Resend.`
- Both Railway services run with `ASPNETCORE_ENVIRONMENT=Production` (`secrets/railway.test.variables.example.json`, [`docs/INFRA.md`](../INFRA.md#variables-required-in-production)): the `test` service needs these variables too. Only `Development` and `Testing` (local runs, CI) start without them, skipping each email with a warning; `/api/health/ready` then reports `email: degraded`.

## 3. Send test

On the **test** environment first, then on production after the release:

1. Log in as a platform admin and invite a supplier using a mailbox you control: admin area → supplier invite, or `POST /api/admin/suppliers/invite` with `{"email":"<your mailbox>","comuneCode":"H501"}`. The API answers `201` as soon as the email is queued.
2. Railway logs: `Email supplier-invite queued (job …)` followed by `Email supplier-invite delivered to the provider`. A warning `skipped: the email provider is not configured` means the variables of step 2 are missing.
3. Resend dashboard → **Emails**: the message is listed as delivered, with the configured sender (not `onboarding@resend.dev`).
4. In the mailbox: the link opens `App__PublicSiteBaseUrl/register?inviteToken=…`. In the message headers ("Show original" in Gmail) SPF and DKIM are `PASS` for the verified domain.
5. Optional: from a confirmed booking, **Resend check-in link** to your own address; the email links to `App__PublicSiteBaseUrl/checkin/<token>`.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Service does not start, `OptionsValidationException` with `Email__…` or `App__PublicSiteBaseUrl` | Variables of step 2 missing or invalid in that environment. |
| Log `Email <template> rejected by the provider, not retried: InvalidFromAddress …` / `ValidationError` | Sender not on a verified domain, or domain verification lost (DNS records removed). Check step 1. |
| Log `… not delivered, will be retried: RateLimitExceeded` | Resend rate or daily quota reached; the job retries. If it persists, raise the Resend plan. |
| Log `… rejected …: InvalidApiKey` / `RestrictedApiKey` | Wrong key, revoked key, or key without sending access for that domain. |
| No log line at all after an action | The email is queued in Hangfire: check that the Hangfire server runs (connection string set) and, if enabled, the Hangfire dashboard. |

## Data kept in Hangfire

The queued job carries recipient, subject and HTML (the check-in email includes the check-in link). Hangfire removes succeeded jobs after 24 hours; failed deliveries are deleted after the last retry (each failure is logged without the recipient address).

## Complete list of emails

Template names are those of the logs (`Email <template> queued`). "Queued" = `IEmailQueue` → Hangfire `EmailDeliveryJob`; "job" = sent directly by a recurring Hangfire job.

| Template | To | When | Sent by |
|---|---|---|---|
| `guest-booking-confirmed` | guest | a booking becomes confirmed: payment succeeded (webhook), card saved for a deferred payment (webhook), "pay at the property" request accepted by the host, late payment confirmed again (BK-04) | `BookingNotifier`, queued |
| `host-booking-confirmed` | host (`Org.ContactEmail`) | the same confirmations, except the "pay at the property" acceptance (the host did it) | `BookingNotifier`, queued |
| `guest-booking-cancelled` | guest | the host cancels a booking; includes the refund (made, or started) | `BookingNotifier` (from `BookingCancellationService`), queued |
| `guest-refund-confirmed` | guest | Stripe confirms a refund later (webhook or retry job), or a refund made from the payment page | `PaymentRefundService`, queued, once per refund (`PaymentRefunds.GuestNotifiedAt`) |
| `guest-payment-refunded-dates-unavailable` | guest | a late payment whose dates were taken, refunded in full (BK-04) | `PaymentRefundService`, queued, once |
| `onsite-request-received` | guest | "pay at the property" request sent: link to confirm the email (BK-06) | `OnSiteRequestNotifier`, queued |
| `onsite-request-to-host` | host | the guest confirmed the email: request to accept or decline | `OnSiteRequestNotifier`, queued |
| `onsite-request-declined` | guest | the host declined the request (with the host's optional message) | `OnSiteRequestNotifier`, queued |
| `onsite-request-expired` | guest | the host did not answer in time | `OnSiteRequestNotifier` (expiry job), queued |
| `guest-checkin-link` | guest | self check-in link: daily job before arrival, or "resend link" by the host | `GuestCheckInSendJob` (job) / `BookingsController` (queued) |
| `guest-checkin-incomplete` | host | "Dati ospiti mancanti": guest data for Alloggiati Web still incomplete the day before arrival | `stay-alerts` job → `NotificationService`, queued, once per stay (CO-10, [hangfire.md §9](hangfire.md#9-stay-alerts-co-10)) |
| `alloggiati-deadline` | host | Alloggiati Web communication not sent, deadline approaching | same, once per stay |
| `alloggiati-overdue` | host | Alloggiati Web deadline passed without the communication, then at most `StayAlerts__MaxOverdueReminders` daily reminders | same |
| `alloggiati-failed` | host | Alloggiati Web communication rejected or failed | same, once per stay |
| `checkout-reminder` | host | check-out day of a confirmed or checked-in stay, 20:00 property time | same, once per check-out date |
| `service-request-created` | supplier | new service request | `ServiceRequestService`, queued |
| `service-request-status-changed` | host | request taken / completed / rejected by the supplier | `ServiceRequestService`, queued |
| `supplier-invite` | prospective supplier | invite by a platform admin | `SupplierService`, queued |
| `rli-deadline-reminder`, `rli-deadline-overdue`, `rli-extra-eu-notice` | landlord | RLI registration deadline / extra-EU tenant (long rents) | `RliDeadlineReminderJob` (job) |

Not sent by design: bookings entered by the host (`Manual`, PC-01) and the host's own confirmations or cancellations get no email to the host; manual bookings get no confirmation to the guest (the host can send the check-in link). There is no guest self-service cancellation yet, so no "cancelled by the guest" email to the host. No email for the CIN deadline alert (CO-20: only logged as not delivered).

## Booking emails (BK-10)

Code: `Casazen.Infrastructure/Services/BookingNotifier.cs`, templates `GuestBookingConfirmed`, `HostBookingConfirmed`, `GuestBookingCancelled` in `EmailTemplates`.

- **When.** Only on the transition to `Confirmed`, after the change is committed, by the code that made it: `CheckoutPaymentSettlementService.CompleteAsync` (payment webhook: `Confirmed` / `Reconfirmed`; SetupIntent webhook: `ConfirmedWithSavedCard`) and `OnSiteBookingRequestService.AcceptAsync`. The emails are skipped if the booking is no longer confirmed when they are prepared.
- **Once per transition.** A duplicate webhook is skipped by the event claim (`ProcessedStripeEvents`); another event of a payment already applied is `AlreadySettled`; the SetupIntent handler locks the booking row, so a second event sees it confirmed; a second host acceptance gets 409. None of them sends anything. Tests: `BookingEmailsPostgresTests`.
- **Guest confirmation content.** Booking code (the booking id, asked by "Le mie prenotazioni"), property, dates, nights, guests, stay, cleaning, tourist tax (only when charged, "inclusa nel totale", BK-03), total; then the payment: "Pagamento ricevuto" (online), the day of the deferred charge (`FreeRefundDeadline`, the day `DirectBookingChargeJob` charges), or "pay at the property". A late payment confirmed again (BK-04) adds a note; an accepted "pay at the property" request says the host accepted it. The standard confirmation replaces the former `guest-late-payment-confirmed` (BK-04) and `onsite-request-accepted` (BK-06) emails.
- **Link.** `App__PublicSiteBaseUrl/book/{orgSlug}/my-bookings` ("Le mie prenotazioni": code + email). The checkout outcome page of BK-07 needs the checkout token, which only the guest's browser has, so it cannot be linked from an email.
- **Host contact.** Only what the booking site already shows publicly in its footer: `Org.DisplayName` and `Org.ContactEmail`; without an email the guest is told to contact the host. When the opt-in public contact of BK-12 exists, `BookingNotifier` must read that instead.
- **Host email.** To `Org.ContactEmail`: guest name, stay, amounts, payment, link to `/app/short-rent/bookings/{id}`. `BookingNotifier.AlertHostOfNewBooking` is the single place where the host learns of a new booking: the push of MO-04 goes there, next to the email.
- **Cancellation and refund, one coherent set.** The host's cancellation sends one `guest-booking-cancelled`: with "Ti abbiamo rimborsato X €" when Stripe confirmed the refund at once (that refund's own `guest-refund-confirmed` is then claimed and never sent), or "È stato avviato un rimborso di X €" when Stripe has not confirmed it yet (the `guest-refund-confirmed` follows, once). Before BK-10 a card refund produced two emails at once, the confirmation first. The host's reason (`CancellationNote`) is never sent.

### Payment receipt choice (BK-10)

- The guest confirmation **is** the payment confirmation: amounts, tourist tax and amount received. It states that it is not a fiscal document; receipts and invoices belong to the fiscal task (SDI), not to these emails.
- Stripe's automatic receipts go to the PaymentIntent's `receipt_email` or to its Customer's email; the checkout PaymentIntent (on the host's connected account) sets neither, so no Stripe receipt duplicates the confirmation and there is nothing to switch off. Do not add `receipt_email` to the checkout without removing the payment lines from the confirmation.
- Deferred payments do create a Customer with the guest's email (to charge the saved card later). If "Successful payments" is enabled in the customer emails settings that apply to the connected account, Stripe may email its receipt when the card is charged at the deadline: that charge has no CasaZen email today (BK-08 owns the deferred charge notifications). Keep one of the two when BK-08 adds its email.

### Language

The booking does not record the language of the checkout and guests have no preference: every email goes out in Italian (`EmailTemplates.DefaultCulture`). The English texts exist for every template (`EmailTemplatesTests` checks both files). Sending in English needs the checkout to record the language (frontend field + column on `Bookings`), not done.

## Adding a new email

1. Add the texts to `EmailTexts.resx` **and** `EmailTexts.en.resx` (`EmailTemplatesTests` fails if a key is missing or untranslated). Texts may contain markup; dynamic values only through `{0}`, `{1}` placeholders.
2. Add a method to `EmailTemplates` using `EmailHtmlBuilder` (never string interpolation of values into HTML).
3. Build links only with `PublicSiteLinks`.
4. In a request handler, queue with `IEmailQueue.Enqueue(...)` after the data is saved; inside a Hangfire job you may call `IEmailService` directly.
5. A booking status email goes in `BookingNotifier` (called after the commit, by the code that makes the transition, so a repeated request or webhook sends nothing).
6. Add it to [Complete list of emails](#complete-list-of-emails).
