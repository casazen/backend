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
| Queue | `IEmailQueue` → Hangfire job `EmailDeliveryJob` | Request handlers only queue: a slow or failing provider never turns a saved operation into an error. Transient errors (timeouts, 429, 5xx) are retried 5 times, then the job is deleted. The check-in link email has its own job (`GuestCheckInLinkEmailJob`, CO-09) that records the real outcome on the link; the stay alerts (CO-10) queue theirs. |

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

- `Email__ApiUrl` is a **test-only** override (mail catcher of the E2E stack, `golden-journey-l3.md`): never set it on Railway. Outside Development/Testing the app refuses to start with it.
- The old variable `Email__ResendApiKey` is still read when `Email__ApiKey` is empty. Rename it to `Email__ApiKey` and delete the old one. If `Email__ApiKey` already exists with a SendGrid key (`SG.…`), replace it with the Resend key: validation rejects keys that do not start with `re_`.
- `Email__SendGridApiKey`, `Email__Smtp*`, `SendGrid__ApiKey`, `App__SupplierLoginUrl` and `App__FrontendBaseUrl` are no longer read: delete them.
- **Set the variables before the release reaches `main`.** From this version the production service does not start without `Email__ApiKey`, `Email__FromAddress` and `App__PublicSiteBaseUrl`: the deploy log shows lines such as `Email__FromAddress is missing: set a sender of the domain verified on Resend.`
- Both Railway services run outside Development/Testing (`Production` on production, `Staging` on test: `secrets/railway.test.variables.example.json`, [`docs/INFRA.md`](../INFRA.md#variables-required-in-production)): the `test` service needs these variables too. Only `Development` and `Testing` (local runs, CI) start without them, skipping each email with a warning; `/api/health/ready` then reports `email: degraded`.

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

The queued job carries recipient, subject and HTML. The check-in link job carries only the session id and the raw token (the email is built when it is sent). Hangfire removes succeeded jobs after 24 hours; failed deliveries are deleted after the last retry (each failure is logged without the recipient address).
The `org-invitation` and `org-invitation-reminder` jobs (AM-02) carry the HTML with the **live link and its secret token**: it
is readable in the job arguments until Hangfire removes the succeeded job (24 hours), so the Hangfire schema and dashboard
stay restricted; after that only the SHA-256 of the token exists (`docs/runbooks/org-team.md` section 15).

## Complete list of emails

Template names are those of the logs (`Email <template> queued`). "Queued" = `IEmailQueue` → Hangfire `EmailDeliveryJob`; "job" = sent directly by a recurring Hangfire job.

| Template | To | When | Sent by |
|---|---|---|---|
| `guest-booking-confirmed` | guest | a booking becomes confirmed: payment succeeded (webhook), card saved for a deferred payment (webhook), "pay at the property" request accepted by the host, late payment confirmed again (BK-04) | `BookingNotifier`, queued |
| `host-booking-confirmed` | host (`Org.ContactEmail`) | the same confirmations, except the "pay at the property" acceptance (the host did it) | `BookingNotifier`, queued |
| `guest-booking-cancelled` | guest | the host cancels a booking; includes the refund (made, or started). Also the automatic cancellation of a "Paga più tardi" booking not paid in time (BK-08), with its own cause: "il pagamento non è stato completato… non ti è stato addebitato nulla" | `BookingNotifier` (from `BookingCancellationService`, or `DeferredChargeService`), queued |
| `guest-deferred-charge-failed` | guest | the deferred charge of "Paga più tardi" failed (authentication required, card declined): link to pay on the checkout outcome page with a new checkout token, last day before the automatic cancellation (BK-08) | `BookingNotifier` (from `DeferredChargeService`: job or `payment_intent.payment_failed`), queued, once per failure |
| `host-deferred-charge-failed` | host | the same failure; or every attempt ended without a charge the guest could complete ("contatta l'ospite") | `BookingNotifier`, queued, once |
| `host-deferred-charge-cancelled` | host | "Paga più tardi" booking cancelled automatically, dates released (BK-08) | `BookingNotifier`, queued |
| `guest-refund-confirmed` | guest | Stripe confirms a refund later (webhook or retry job), or a refund made from the payment page | `PaymentRefundService`, queued, once per refund (`PaymentRefunds.GuestNotifiedAt`) |
| `guest-payment-refunded-dates-unavailable` | guest | a late payment whose dates were taken, refunded in full (BK-04) | `PaymentRefundService`, queued, once |
| `onsite-request-received` | guest | "pay at the property" request sent: link to confirm the email (BK-06) | `OnSiteRequestNotifier`, queued |
| `onsite-request-to-host` | host | the guest confirmed the email: request to accept or decline | `OnSiteRequestNotifier`, queued |
| `onsite-request-declined` | guest | the host declined the request (with the host's optional message) | `OnSiteRequestNotifier`, queued |
| `onsite-request-expired` | guest | the host did not answer in time | `OnSiteRequestNotifier` (expiry job), queued |
| `guest-checkin-link` | guest | self check-in link: daily job before arrival, or "send link" / reminder by the host | `GuestCheckInSendJob` or `BookingCheckInLinkController` → `GuestCheckInLinkEmailJob` (queued, outcome on the link: [alloggiati.md](alloggiati.md#guest-check-in-link-and-host-fallback-co-09)) |
| `guest-checkin-incomplete` | host | "Dati ospiti mancanti": guest data for Alloggiati Web still incomplete the day before arrival | `stay-alerts` job → `NotificationService`, queued, once per stay (CO-10, [hangfire.md §9](hangfire.md#9-stay-alerts-co-10)) |
| `alloggiati-deadline` | host | Alloggiati Web communication not sent, deadline approaching | same, once per stay |
| `alloggiati-overdue` | host | Alloggiati Web deadline passed without the communication, then at most `StayAlerts__MaxOverdueReminders` daily reminders | same |
| `alloggiati-failed` | host | Alloggiati Web communication rejected or failed | same, once per stay |
| `checkout-reminder` | host | check-out day of a confirmed or checked-in stay, 20:00 property time | same, once per check-out date |
| `property-compliance-suspended` | host | an active property lost an activation requirement (CIN, required document, safety checklist, base data) and was suspended from the booking site; the first check of a property published before CO-06 only with `Compliance__StatusCheck__NotifyOnFirstCheck=true` ([compliance.md](compliance.md#3-email-to-the-host)) | `PropertyComplianceStatusService` (request or `property-compliance-check` job), queued, once per suspension |
| `property-mode-change-scheduled` | host | a change of rental mode (short stays ↔ long-term) was programmed: the day, what closes, how to withdraw it (PM-02, [property-rental-mode.md §8.6](property-rental-mode.md#86-e-mails-to-the-host)) | `PropertyModeService.ScheduleAsync` → `NotificationService`, queued, once per change |
| `property-mode-change-applied` | host | the property changed mode at midnight of Rome | `property-mode-change` job → `NotificationService`, queued, once per change |
| `property-mode-change-failed` | host | the job could not apply the change (stays, imported blocks or leases in the way): why and the first free day | same, once per change |
| `service-request-created` | supplier | new service request | `ServiceRequestService`, queued |
| `service-request-status-changed` | host | request taken / completed / rejected by the supplier | `ServiceRequestService`, queued |
| `service-payment-request`, `service-payment-reminder` | host org (the payer) | a service paid inside CasaZen was completed / the supplier asks again: the price, who asks and the link of the payment page (valid `SupplierPayments__PaymentLinkValidityDays`); never the commission (SP-15a) | `ServiceRequestService` (completion, confirmation of the amount) and `SupplierPaymentService.RequestPaymentAsync`, queued after the save; a link that cannot be queued is taken back |
| `service-payment-received` | supplier | a payment made online was received: gross, CasaZen commission, net before Stripe's fees, no payout date promised (SP-15a template, sent by the webhook and the sync job of SP-15b when a payment is recorded as paid, once) | `SupplierPaymentService.Webhook` / `.Notices` (SP-15b), queued after the commit |
| `service-payment-offline-recorded` | host org (the payer) | the supplier recorded a payment received outside CasaZen, with its reason (SP-15a) | `SupplierPaymentService.RecordOfflineAsync`, queued |
| `service-payment-failed` | host org (the payer) | a payment that was in flight (a SEPA debit) failed afterwards: what failed, the price, **a new link** (the previous one stops working); never the commission (SP-15b) | `SupplierPaymentService.Notices`, queued after the commit of the webhook or the sync |
| `service-payment-refunded-payer` | host org (the payer) | a refund of the payment succeeded: how much, and that the time to reach the bank depends on the bank; never the commission, no date promised (SP-15b) | `SupplierPaymentService.Notices`, queued |
| `service-payment-refunded-supplier` | supplier | the same refund: how much went back to the payer from its Stripe balance and, when the payment carried a commission, the part of CasaZen's commission that came back to it (SP-15b) | `SupplierPaymentService.Notices`, queued |
| `service-payment-admin-alert` | the active platform admins (users with the `Admin` role that have an email) | a payment to review (what Stripe reported does not match what was asked for, or a charge for a payment already withdrawn) or a dispute on a payment: ids, supplier name, amount and Stripe's codes, **no data of the payer** (SP-15b; with no admin email an error is logged) | `SupplierPaymentService.Notices`, queued |
| `supplier-invite` | prospective supplier | invite by a platform admin | `SupplierService`, queued |
| `supplier-booking-verification` | customer of a supplier's showcase | the customer booked a slot: the link that checks its address (SP-10) | `ShowcaseBookingNotifier`, queued |
| `supplier-booking-receipt` | customer | the address is checked: code, time by which the supplier answers, estimate | same |
| `supplier-booking-new-request` | supplier | the address is checked: new request with comune and "Nome C." only (D9) | same |
| `supplier-booking-accepted`, `-declined`, `-time-proposed`, `-cancelled`, `-expired` | customer | the supplier took, refused, proposed another time for, or cancelled the request; nobody answered in time (the job) | `ServiceRequestNotifier` → `ShowcaseBookingNotifier`, queued |
| `supplier-booking-reminder` | customer | 18:00 (Rome) of the day before the work, once, if the supplier took the request before that time | `service-request-reminders` job → `ShowcaseBookingNotifier`, queued |
| `supplier-booking-cancellation-receipt`, `-proposal-expired` | customer | it cancelled its request (receipt); the day to answer the time the supplier proposed passed and the request was cancelled (SP-11) | `ShowcaseBookingNotifier`, queued |
| `supplier-booking-cancelled-by-customer`, `-rescheduled-by-customer`, `-proposal-answered-by-customer`, `-proposal-lapsed` | supplier | the customer cancelled, moved, accepted or turned down the proposed time, or let it lapse; comune and "Nome C." only, plus a push (SP-11) | `ShowcaseBookingNotifier`, queued |
| `org-invitation` | invitee | an owner or administrator invites a person to the org team, or sends the invitation again (AM-02): the link carries the secret token and works for 7 days; a newer link replaces it | `OrgInvitationService`, queued after the commit; language chosen by the inviter |
| `org-invitation-reminder` | invitee | the third day after the invitation, while it is pending, with a **new link** (AM-02) | `org-invitation-maintenance` job, queued, once per invitation; only with `Features__OrgTeam` on |
| `org-invitation-expired` | inviter (the owner when the inviter no longer manages the org) | nobody accepted within 7 days: the seat is free again (AM-02) | same job, queued, once per invitation; only with the flag on; Italian |
| `rli-deadline-reminder`, `rli-deadline-overdue`, `rli-extra-eu-notice` | landlord | RLI registration deadline / extra-EU tenant (long rents) | `RliDeadlineReminderJob` (job) |

Not sent by design: bookings entered by the host (`Manual`, PC-01) and the host's own confirmations or cancellations get no email to the host; manual bookings get no confirmation to the guest (the host can send the check-in link). There is no guest self-service cancellation yet, so no "cancelled by the guest" email to the host. No email for the CIN deadline alert (CO-20: only logged as not delivered).

## Booking emails (BK-10)

Code: `Casazen.Infrastructure/Services/BookingNotifier.cs`, templates `GuestBookingConfirmed`, `HostBookingConfirmed`, `GuestBookingCancelled` in `EmailTemplates`.

- **When.** Only on the transition to `Confirmed`, after the change is committed, by the code that made it: `CheckoutPaymentSettlementService.CompleteAsync` (payment webhook: `Confirmed` / `Reconfirmed`; SetupIntent webhook: `ConfirmedWithSavedCard`) and `OnSiteBookingRequestService.AcceptAsync`. The emails are skipped if the booking is no longer confirmed when they are prepared.
- **Once per transition.** A duplicate webhook is skipped by the event claim (`ProcessedStripeEvents`); another event of a payment already applied is `AlreadySettled`; the SetupIntent handler locks the booking row, so a second event sees it confirmed; a second host acceptance gets 409. None of them sends anything. Tests: `BookingEmailsPostgresTests`.
- **Guest confirmation content.** Booking code (the booking id, asked by "Le mie prenotazioni"), property, dates, nights, guests, stay, cleaning, tourist tax (only when charged, "inclusa nel totale", BK-03), total; then the payment: "Pagamento ricevuto" (online), the day of the deferred charge (`FreeRefundDeadline`, the day `DirectBookingChargeJob` charges), or "pay at the property". A late payment confirmed again (BK-04) adds a note; an accepted "pay at the property" request says the host accepted it. The standard confirmation replaces the former `guest-late-payment-confirmed` (BK-04) and `onsite-request-accepted` (BK-06) emails.
- **Link.** `App__PublicSiteBaseUrl/book/{orgSlug}/my-bookings` ("Le mie prenotazioni": code + email). The checkout outcome page of BK-07 needs the checkout token, which only the guest's browser has, so it cannot be linked from an email.
- **Host contact.** Only what the booking site already shows publicly in its footer: `Org.DisplayName` and `Org.ContactEmail`; without an email the guest is told to contact the host. When the opt-in public contact of BK-12 exists, `BookingNotifier` must read that instead.
- **Host email.** To `Org.ContactEmail`: guest name, stay, amounts, payment, link to `/app/short-rent/bookings/{id}`. `BookingNotifier.AlertHostOfNewBooking` is the single place where the host learns of a new booking: it also queues the push `new-booking` to the hosts' phones (MO-04, [mobile-release.md § 9.7](mobile-release.md#97-backend-delivery-queue-batches-receipts-mo-04)), once per booking.
- **Cancellation and refund, one coherent set.** The host's cancellation sends one `guest-booking-cancelled`: with "Ti abbiamo rimborsato X €" when Stripe confirmed the refund at once (that refund's own `guest-refund-confirmed` is then claimed and never sent), or "È stato avviato un rimborso di X €" when Stripe has not confirmed it yet (the `guest-refund-confirmed` follows, once). Before BK-10 a card refund produced two emails at once, the confirmation first. The host's reason (`CancellationNote`) is never sent.

### Payment receipt choice (BK-10)

- The guest confirmation **is** the payment confirmation: amounts, tourist tax and amount received. It states that it is not a fiscal document; receipts and invoices belong to the fiscal task (SDI), not to these emails.
- Stripe's automatic receipts go to the PaymentIntent's `receipt_email` or to its Customer's email; the checkout PaymentIntent (on the host's connected account) sets neither, so no Stripe receipt duplicates the confirmation and there is nothing to switch off. Do not add `receipt_email` to the checkout without removing the payment lines from the confirmation.
- Deferred payments do create a Customer with the guest's email (to charge the saved card later). If "Successful payments" is enabled in the customer emails settings that apply to the connected account, Stripe may email its receipt when the card is charged at the deadline. BK-08 emails only the failures and the automatic cancellation of the deferred charge, **not** its success: whether the guest gets a CasaZen "Pagamento ricevuto" or Stripe's receipt for that charge is an open product question (DUBBI BK-08, recommendation: a CasaZen email through `BookingNotifier`, and Stripe's "Successful payments" left off, so that the guest gets one Italian/English message with the booking code, like every other booking email).

### Language

The booking does not record the language of the checkout and guests have no preference: every email goes out in Italian (`EmailTemplates.DefaultCulture`). The English texts exist for every template (`EmailTemplatesTests` checks both files). Sending in English needs the checkout to record the language (frontend field + column on `Bookings`), not done. The exception is the org team (AM-02): the invitation and its reminder go out
in the language the inviter chose (`it`, the default, or `en`, stored on the invitation); the note to the inviter is in Italian.

## Showcase booking emails (SP-10)

Code: `Casazen.Infrastructure/Services/ShowcaseBookingNotifier.cs` (what is sent, when, to whom), `EmailTemplates.SupplierBooking.cs` (the nine templates),
texts `SupplierBooking*` in `EmailTexts.resx` and `EmailTexts.en.resx`. Details of the flow: [suppliers.md § 23.8](suppliers.md#23-booking-from-the-suppliers-showcase-hold-e-mail-check-request--sp-10).

- **Who and in which language.** The customer in the language it chose when it booked (`it` or `en`, `ServiceCustomerLocales`); the supplier always in Italian.
  The customer's address is the one it typed; a customer whose data were anonymized (retention) is not written to.
- **After the commit, by the winner only.** The verification e-mail is rendered **before** the hold is saved (a missing `App__PublicSiteBaseUrl` stops the
  booking instead of sending a link to nothing) and queued after it; the others are queued after the change that causes them is saved and only by the party
  that won the transition (a race loser sends nothing). A failure to queue is logged and never undoes the change.
- **The supplier never learns more than D9 allows**: comune, "Nome C.", time and estimate; not the address, the contacts or the full name.
- **Only what is true** (D24): no "guaranteed price", no "answers in an hour", nothing about payment. The reminder is promised in the e-mail of the take only
  when it will really be sent. A test searches every template, in both languages, for the phrases that must never appear.
- **Links** (all built by `PublicSiteLinks`): `…/fornitori/{slug}/conferma?hold={id}&token={token}` (the token is single use and works for 30 minutes),
  `…/fornitori/{slug}/richiesta?code={code}` (the page of the request, SP-11), `…/fornitori/{slug}` and the supplier's inbox.
- **What Hangfire keeps.** Like every queued e-mail the job carries the recipient, the subject and the HTML for 24 hours: the customer's e-mails carry its first
  name and the verification e-mail the link with the token (single use, valid for the minutes of the hold). It is outside the encryption at rest of the
  booking ([suppliers.md § 23.7](suppliers.md#237-privacy-and-encryption)) and the same for every e-mail of the product.

## Showcase booking emails: the customer's own area (SP-11)

Code: `ShowcaseBookingNotifier` (`NotifyCancelledByCustomerAsync`, `NotifyRescheduledByCustomerAsync`, `NotifyProposalAnsweredByCustomerAsync`,
`NotifySupplierOfLapsedProposalAsync`, and `NotifyCustomerOfStatusAsync` for the lapsed proposal), `EmailTemplates.SupplierBooking.cs` (the six templates) and
`EmailTemplates.Push.cs` (the four pushes), texts `SupplierBooking*` in `EmailTexts.resx` and `EmailTexts.en.resx`. Flow and rules:
[suppliers.md § 24](suppliers.md#24-the-customers-own-area-find-cancel-move-and-answer-a-proposed-time--sp-11). Same rules as the SP-10 ones
above: after the commit, by the winner of the transition only, a failure is logged and never undoes the change, the customer in its language and the
supplier in Italian, the supplier reads comune and "Nome C." only (D9).

| Template | To | When |
|---|---|---|
| `supplier-booking-cancellation-receipt` | customer | it cancelled its request: what was cancelled, that nothing is owed, the reason it gave, the link to the supplier's showcase |
| `supplier-booking-cancelled-by-customer` | supplier | the customer cancelled: service, comune, "Nome C.", time, its reason, and for a taken job cancelled inside the free notice that the notice was short (+ push `service-request-cancelled`) |
| `supplier-booking-rescheduled-by-customer` | supplier | the customer moved a new request to another time: both times, the time to answer by, that a proposal of the supplier no longer applies (+ push `service-request-rescheduled`, new type) |
| `supplier-booking-proposal-answered-by-customer` | supplier | the customer accepted the proposed time (the request is taken) or turned it down (+ push `service-request-proposal-accepted` / `-rejected`) |
| `supplier-booking-proposal-expired` | customer | the day to answer the proposed time passed and the request was cancelled (reason `ProposalNotAnswered`) |
| `supplier-booking-proposal-lapsed` | supplier | the same event, told to the supplier: it was the customer who did not answer (+ push `service-request-cancelled`) |

When the customer **accepts** the proposed time it also gets the e-mail of a taken request (`supplier-booking-accepted`) with the new time: it took the
request on the supplier's behalf. The customer gets nothing for moving the booking or for turning a proposal down: the page it is on answers with the booking.

- **Only what is true** (D24). "Nothing is owed" is true in v1 (D6); no refund is mentioned because none exists; the short notice is said only to the
  supplier and only when it was short; a lapsed proposal is told as what it is (the customer did not answer), never as the supplier's silence. The
  banned-phrase test (`SupplierBookingEmailTemplatesTests`) covers the six templates, every variant of them, in both languages.
- **Pushes**: the supplier's devices, deduplicated by `PushDeliveryKeys` (`service-request:{id}:rescheduled:{time}` is new; the others reuse the key of
  the status or of the answer). The mobile app does not know the type `service-request-rescheduled` yet (mobile follow-up): until it does, it is for the app to handle an unknown type.

## Adding a new email

1. Add the texts to `EmailTexts.resx` **and** `EmailTexts.en.resx` (`EmailTemplatesTests` fails if a key is missing or untranslated). Texts may contain markup; dynamic values only through `{0}`, `{1}` placeholders.
2. Add a method to `EmailTemplates` using `EmailHtmlBuilder` (never string interpolation of values into HTML).
3. Build links only with `PublicSiteLinks`.
4. In a request handler, queue with `IEmailQueue.Enqueue(...)` after the data is saved; inside a Hangfire job you may call `IEmailService` directly.
5. A booking status email goes in `BookingNotifier` (called after the commit, by the code that makes the transition, so a repeated request or webhook sends nothing).
6. Add it to [Complete list of emails](#complete-list-of-emails).
