# Runbook: direct booking — "Paga in struttura" requests, checkout outcome page, payment options

Task BK-06 (audit defects A3-06 P0, R-11), decision **D5** of the product owner: a "pay at the property" booking
(`PaymentOption = OnSite`) **must always be confirmed by the host**. It stays waiting until the host accepts it; once
accepted it is valid. The code is in place; the product owner decides the provisional values of section 2.

## 1. How a request goes

| Step | Booking | Dates | iCal export to the OTAs | Emails |
|---|---|---|---|---|
| Guest submits the checkout with "Paga in struttura" (`POST /api/public/bookings`) | `Pending`, `Source = Direct`, `PaymentOption = OnSite`, `RequestExpiresAt = now + email window`; a `Payments` row `CashOnArrival`, `Pending` | held on the booking site and in the host calendar (no double request) | **not exported** | guest: "request received" with the confirmation link (`/book/{orgSlug}/requests/{bookingId}/confirm?token=…`) |
| Guest opens the link and clicks "Conferma e invia la richiesta" (`POST /api/public/bookings/{id}/confirm-email`) | `GuestEmailVerifiedAt` set, `RequestExpiresAt = now + OnSiteApprovalHours` | held | not exported | host (`Org.ContactEmail`): "new request to approve", with the link to the console (`/app/short-rent/bookings?view=requests`) |
| Host accepts (`POST /api/bookings/{id}/approve`) | `Confirmed`, check-in token issued, `RequestExpiresAt` cleared | taken | **exported** like every confirmed booking | guest: the standard "Prenotazione confermata" (BK-10) saying the host accepted and the amount to pay at the property; no email to the host, who accepted |
| Host declines (`POST /api/bookings/{id}/decline`, optional `message`) | `Cancelled`, `CancellationReason = 3` (`OnSiteRequestDeclined`), payment row `Canceled` | released | — | guest: "request not accepted" with the host's message |
| Nobody answers by `RequestExpiresAt` | job `checkout-hold-expiry` (every 5 min): `Cancelled`, reason `4` (`OnSiteRequestExpired`) or `5` (`OnSiteEmailNotConfirmed`) | released at the deadline (reads ignore it before the job runs) | — | guest: "request expired" (only when the email had been confirmed) |

- **The host never sees an unconfirmed request** in the requests list and cannot answer it (409
  `onsite_request_email_not_confirmed`): an address typed by someone else, or a fake one, never reaches the host and
  holds the dates only for the email window.
- The confirmation page does not confirm on load: the guest must click (mail scanners that open links confirm
  nothing). The link carries only the booking id and a random 256-bit token; the database keeps its SHA-256 only.
  A second click answers the current state.
- **Concurrency**: accept, decline and the host cancellation (BK-02) of the same booking take the same PostgreSQL
  advisory lock, then the booking row (`FOR UPDATE`); the expiry job skips a locked row (`SKIP LOCKED`). Two answers
  sent together give one outcome; the other gets 409 `onsite_request_not_pending`.
- **Accept re-checks the OTA blocks**: while pending the request is not exported, so an OTA booking may be imported
  meanwhile (iCal import every 15 min). If an imported block overlaps, accept answers 409 `onsite_request_dates_blocked`
  and the host can only decline.
- Push notifications to the host are task MO-04 (not here).

Code: `Casazen.Core/Services/OnSiteRequests.cs` (rules and settings), `CheckoutHolds.cs` (shared expiry predicate, BK-21),
`Casazen.Infrastructure/Services/OnSiteBookingRequestService.cs`, `OnSiteRequestNotifier.cs`, `BookingService.cs`,
`CheckoutHoldExpiryService.cs`; controllers `PublicBookingsController`, `BookingApprovalController`. Frontend: checkout
page (message "Richiesta inviata: in attesa di conferma dell'host"), page `/book/:orgSlug/requests/:bookingId/confirm`,
panel "Richieste da approvare" and badges in the bookings list.

## 2. Configuration (Railway variables, per environment)

| Variable | Meaning | Default | Status |
|---|---|---|---|
| `DirectBooking__OnSiteApprovalHours` | Hours the host has to accept or decline, from the guest's email confirmation | **24** | **PROVISIONAL technical default** so that no request holds dates forever. Not a product rule: the product owner decides it (DUBBI BK-06) |
| `DirectBooking__OnSiteEmailVerificationMinutes` | Minutes the guest has to confirm the email; meanwhile the dates are held | the checkout TTL (`DirectBooking__PendingTtlMinutes`, 30) | Same time the guest has to pay online. Raise it if guests report expired links |
| `DirectBooking__OnSiteMaxNights` | Longest stay of a "pay at the property" request (anti-abuse, A3-06): longer requests get 422 `onsite_request_too_many_nights` | **30** | **PROVISIONAL**: no spec defines it; aligned with the "locazione breve" of art. 4 D.L. 50/2017 (contracts up to 30 days, `.claude/context/regulations/fiscale.md` C1). The product owner decides it |
| `DirectBooking__PendingTtlMinutes` | Checkout hold of online payments (BK-21, BK-04) | 30 | unchanged |
| `RateLimiting__PublicBookingCreate__PermitLimit` | Checkouts per client IP per minute (FD-10) | 10 | applies to "pay at the property" too |
| `RateLimiting__PublicBookingLookup__PermitLimit` | Includes `confirm-email` | 30 | |
| `RateLimiting__PublicGuestBookingLookup__PermitLimit` / `__WindowSeconds` | "Le mie prenotazioni": lookups and check-in link requests per client IP (§ 8) | 10 per 300 s | BK-11 |
| `RateLimiting__GuestBookingLookupPerEmail__PermitLimit` / `__WindowSeconds` | Same, per email address, whatever the IP (§ 8) | 5 per 900 s | BK-11 |
| `App__PublicSiteBaseUrl`, `Email__*` | Links and delivery of the emails | — | required, see [email.md](email.md) |

A changed value applies to new requests and new confirmations: a request already waiting keeps the deadline stored in
`Bookings.RequestExpiresAt`. Values below 1 are read as 1.

The option is still offered on every property with Stripe Connect ready (the audit proposed "enabled per property,
default off": not decided, see DUBBI). Without Connect the checkout answers 409 `direct_booking_payments_not_ready`
and the guest reads "Questa struttura non accetta ancora prenotazioni online. Contatta direttamente l'host." (R-11).

## 3. API

| Endpoint | Auth | Answers |
|---|---|---|
| `POST /api/public/bookings` (`paymentOption: "OnSite"`) | anonymous, rate limited | 200 with `emailConfirmationExpiresAt` and `checkoutToken` (§ 7); 422 `onsite_request_too_many_nights`, `direct_booking_invalid_stay`, `booking_too_many_guests`, `direct_booking_consent_outdated`, `direct_booking_invalid_payment_option`; 409 `booking_dates_unavailable`, `direct_booking_payments_not_ready`; 400 `direct_booking_consent_required`; 404 `not_found` |
| `POST /api/public/bookings/{id}/confirm-email` `{ token }` | anonymous, rate limited | 200 `{ bookingId, status, state, requestExpiresAt }`; 404 `onsite_request_link_invalid`; 409 `onsite_request_expired` |
| `GET /api/bookings/approval-requests` | `booking.read`, filtered in SQL by org (and owned properties for non org-wide roles) | requests waiting for the host, soonest deadline first |
| `POST /api/bookings/{id}/approve` | `booking.write` on the booking's property (TN-3) | 200 booking; 404 (other org / no permission); 409 `onsite_request_not_pending`, `onsite_request_expired`, `onsite_request_email_not_confirmed`, `onsite_request_dates_blocked`; 422 `booking_not_onsite_request` |
| `POST /api/bookings/{id}/decline` `{ message? }` (max 500) | same | same (except `dates_blocked`) |

`GET /api/bookings` now carries `paymentOption`, `onSiteRequestState` (`AwaitingGuestEmail` / `AwaitingHostApproval`)
and `requestExpiresAt`.

## 4. After the deploy (one time)

Before BK-06 "pay at the property" bookings were **confirmed at once** (and exported to the OTAs). They stay
`Confirmed`: nothing is cancelled automatically. List them and ask each host to check them (SQL editor, replace the
schema):

```sql
SELECT b."Id", b."OrgId", b."PropertyId", b."CheckInDate", b."CheckOutDate", b."CreatedAt"
FROM casazen_prod."Bookings" b
WHERE b."Source" = 0 AND b."PaymentOption" = 2 AND b."Status" = 1
  AND b."CheckOutDate" >= now()
ORDER BY b."CheckInDate";
```

A host who does not recognize one cancels it from the booking (BK-02 cancellation).

## 5. Checks

```sql
-- Requests waiting for the host, with their deadline
SELECT b."Id", b."PropertyId", b."GuestEmailVerifiedAt", b."RequestExpiresAt"
FROM casazen_prod."Bookings" b
WHERE b."Status" = 0 AND b."Source" = 0 AND b."PaymentOption" = 2 AND b."GuestEmailVerifiedAt" IS NOT NULL
ORDER BY b."RequestExpiresAt";

-- Past their deadline but not cancelled yet: should be empty or only a few minutes old (job every 5 minutes)
SELECT b."Id", b."RequestExpiresAt", now() - b."RequestExpiresAt" AS late
FROM casazen_prod."Bookings" b
WHERE b."Status" = 0 AND b."Source" = 0 AND b."PaymentOption" = 2 AND b."RequestExpiresAt" < now();

-- Outcomes of the last 30 days: 3 declined, 4 not answered by the host, 5 email never confirmed
SELECT "CancellationReason", count(*) FROM casazen_prod."Bookings"
WHERE "PaymentOption" = 2 AND "CancellationReason" IN (3, 4, 5) AND "UpdatedAt" > now() - interval '30 days'
GROUP BY 1;
```

Many reason `5` rows from few addresses mean someone is holding dates with fake requests: lower
`RateLimiting__PublicBookingCreate__PermitLimit` or `DirectBooking__OnSiteEmailVerificationMinutes`.

Logs (booking ids only, no personal data): `On-site request {BookingId} created`, `guest email confirmed, sent to the
host until …`, `accepted by the host`, `declined by the host`, `On-site request {BookingId} expired (OnSiteRequestExpired)`.

## 6. Troubleshooting

| Symptom | Cause / action |
|---|---|
| The guest says the email never arrived | Check `Email onsite-request-received queued` in the logs and Resend (§ [email.md](email.md)). The request expires after the email window: the guest can submit again |
| The host never got the "new request" email | `Org.ContactEmail` empty or wrong (log `Email onsite-request-to-host … skipped: no recipient address`). The request is visible anyway in the console panel "Richieste da approvare" |
| Accept answers 409 `onsite_request_dates_blocked` | An OTA booking was imported for the same dates while the request was waiting: decline the request |
| Guests get "link expired" too often | Raise `DirectBooking__OnSiteEmailVerificationMinutes` |

## 7. Checkout outcome page, Stripe redirects, error codes, payment options (BK-07)

Task BK-07 (audit defects A3-15 and A3-16, P1). Before it the checkout showed "Prenotazione confermata!" right after
`stripe.confirmPayment`, before the webhook (a SEPA debit still `processing`, or a webhook that never came, ended as a
"confirmed" booking cancelled by the hold expiry); a redirect method (iDEAL, Bancontact, Klarna…) came back to
`window.location.href`, an empty checkout whose new booking the guest's own hold blocked (409); "Paga alla scadenza" was
offered for stays of more than 7 nights even with the arrival tomorrow, and every option promised "Cancellazione gratuita
fino a …" although the guest cannot cancel by themselves.

### 7.1 Outcome page

- Route of the booking site: **`/book/{orgSlug}/booking/{bookingId}?token={checkoutToken}`**. The checkout goes there after
  the payment (or the "pay at the property" request), and it is the Stripe **`return_url`** of `confirmPayment` /
  `confirmSetup`: a redirect method brings the guest back to their booking. Stripe appends `payment_intent`,
  `payment_intent_client_secret` (or `setup_intent…`) and `redirect_status`; the page reads `redirect_status` once and
  removes the Stripe parameters (the client secret among them) from the address bar.
- The state shown is **always the backend's** (`POST /api/public/bookings/{id}/outcome`). "Prenotazione confermata" only
  for `Confirmed`. A payment that Stripe.js reports as succeeded is "Pagamento in elaborazione" until the webhook confirms
  the booking.
- The page polls with a backoff (2 s, 3 s, 4.5 s … up to 30 s, 20 polls ≈ 7 minutes) while the state is intermediate
  (payment being confirmed, request waiting for the email or the host, expired hold whose guest has paid: BK-04 may
  confirm it again); then it offers "Aggiorna lo stato". Final states stop the polling.
- States (`state` of the answer):

| `state` | When | Page |
|---|---|---|
| `Confirmed` | booking `Confirmed` / `CheckedIn` / `CheckedOut` | "Prenotazione confermata!", stay, payment (paid online / card charged on the deadline day / at the property) |
| `PaymentProcessing` | `Pending` with a payment `Processing` or `Completed` (webhook not processed yet; SEPA for a few days) | "Pagamento in elaborazione", polling |
| `PaymentFailed` | hold valid, last PaymentIntent attempt `Failed` (`payment_intent.payment_failed`) | "Pagamento non riuscito" + "Riprova il pagamento" on the same hold, until `expiresAt` |
| `AwaitingPayment` | hold valid, nothing paid | "Pagamento da completare" + "Completa il pagamento" |
| `AwaitingGuestEmail` / `AwaitingHostApproval` | "pay at the property" request (§ 1) | next steps, deadline `expiresAt`, polling |
| `Expired` | cancelled `CheckoutHoldExpired`, `OnSiteRequestExpired`, `OnSiteEmailNotConfirmed`, or a hold past the TTL (`CheckoutHolds.IsExpired`, the same rule that released the dates) | "Tempo scaduto" + "Prenota di nuovo" (property page with the same stay) |
| `Declined` | request declined by the host | "Richiesta non accettata" |
| `DatesUnavailable` | payment arrived when the dates were taken (BK-04): cancelled and refunded in full | "Date non più disponibili", refund email when Stripe confirms it |
| `Cancelled` | any other cancellation (host) | "Prenotazione annullata" |

### 7.2 Checkout token (security)

- `POST /api/public/bookings` answers a **`checkoutToken`** (256 random bits, URL-safe) once; the database keeps only its
  SHA-256 in **`Bookings.CheckoutTokenHash`** (migration `AddBookingCheckoutTokenHash`). The token travels in the URL of the
  page (the guest's own link) and in the **body** of the API calls, never in an API URL, so it does not land in access logs.
- A wrong booking id, a wrong token, a booking not from the public checkout and a booking created before BK-07 all answer
  the same **404 `checkout_link_invalid`**: the booking id alone (visible to the host, in emails, in links) reveals nothing
  and ids cannot be enumerated. The answer has no personal data of the guest (property, dates, guests count, total, state).
- The old anonymous **`GET /api/public/bookings/{id}/status`**, which answered the status of any booking id, is removed.
- Rate limit: `RateLimiting__PublicBookingLookup__PermitLimit` (30 per minute per IP) covers `outcome` and
  `payment-session`; the page polls at most about 7 times in its first minute.
- Bookings created before the deploy have no token: their guests use the emails; nothing to migrate.

### 7.3 Paying the same hold again (no 409 from the guest's own hold)

- `POST /api/public/bookings/{id}/payment-session` `{ token }` reads the booking's **own** PaymentIntent (or SetupIntent
  for "Paga alla scadenza") from Stripe, on the connected account, and returns its client secret: the page mounts the
  Payment Element again on the same hold. Answers: 200; 404 `checkout_link_invalid`; **409 `checkout_hold_expired`** (hold
  past the TTL, or intent `canceled` by the expiry job: dates released); **409 `checkout_payment_not_resumable`** (already
  paid or being paid: intent `succeeded` / `processing`, booking confirmed or cancelled, "pay at the property" request).
- The checkout remembers, in `sessionStorage` of the tab, the booking it just created: if the guest goes back to the form
  and books the same dates again, the 409 `booking_dates_unavailable` comes with "Riprendi la prenotazione" (link to the
  outcome page) instead of a dead end.

### 7.4 Error codes of the checkout

All the checkout errors are ProblemDetails with a stable `code` and a localized `detail` (IT/EN resx); the frontend shows
its own translation of the code (`apiErrors.codes.*`, `getProblemMessage`). `BookingService` throws
`DomainRuleException` / `DomainConflictException` / `NotFoundException` / `PaymentProcessingException` (the old
`DirectBookingException` with English messages and the controller mapping are gone).

| Status | `code` | When |
|---|---|---|
| 400 | `validation_error` | malformed body (field errors) |
| 400 | `direct_booking_consent_required` | no data processing consent |
| 404 | `not_found` | property missing, inactive or not compliant |
| 409 | `booking_dates_unavailable` | dates taken (another booking, a valid hold, an iCal block) |
| 409 | `direct_booking_payments_not_ready` | the host has not completed Stripe Connect |
| 422 | `direct_booking_invalid_stay` | check-in in the past, check-out not after check-in, too long (quote too; it used to answer `booking_invalid_dates`) |
| 422 | `booking_too_many_guests` | over the capacity |
| 422 | `direct_booking_consent_outdated` | consent text changed |
| 422 | `direct_booking_invalid_payment_option` | unknown option |
| 422 | **`direct_booking_deferred_payment_unavailable`** | "Paga alla scadenza" for a stay whose charge day is not after today (§ 7.5) |
| 422 | `tourist_tax_child_ages_required` | ages of the minors missing (BK-03) |
| 422 | `onsite_request_too_many_nights` | § 2 |
| 429 | `rate_limited` | FD-10 |
| 503 | `payment_provider_error` | Stripe could not create the intent (the hold is cancelled) |

### 7.5 Payment options (A3-16)

- **Free refund deadline** of a stay (`Bookings.FreeRefundDeadline`, `DirectBookingPaymentRules.FreeRefundDeadline`): with
  a `CancellationPolicy` on the property, the last whole day (Europe/Rome) at least `FullRefundHours` before the start of
  the check-in day, i.e. the same full refund window `CancellationRefundPolicy` applies (e.g. 48 h → check-in − 3 days);
  without a policy, the existing rule **check-in − 7 days**. It is also the day the deferred payment is charged
  (`DirectBookingChargeJob`, unchanged: the robust deferred charge is task BK-08).
- **"Paga alla scadenza"** is offered, and accepted by the API, only when that day is **after today in Europe/Rome**: with
  the default rule only for an arrival more than 7 days away, whatever the number of nights (10 nights from tomorrow: not
  offered, 422 if forced). The quote (`POST /api/public/bookings/quote`) answers
  `paymentOptions: { deferredPaymentAvailable, deferredChargeDate, freeCancellationUntil }` and the checkout shows the
  button "Paga più tardi: la carta viene salvata ora e addebitata il {data}" only from it.
- **"Cancellazione gratuita fino a …"** is not shown anywhere: the guest has **no self-service cancellation** (only the host
  cancels, BK-02), so `freeCancellationUntil` is always `null` (`DirectBookingPaymentRules.GuestSelfCancellationAvailable`
  = false). The guest booking page shows "Pagamento previsto per {data}" for the deferred payment instead. When a guest
  cancellation exists, set the flag and the checkout shows the text again from the quote.
- The refund floor of a host cancellation (BK-02) keeps using `FreeRefundDeadline`: for new bookings of a property with a
  policy it now coincides with the policy's full refund window instead of check-in − 7.

### 7.6 Checks

1. Test mode, card `4242 4242 4242 4242`: after "Paga ora" the page is `/book/{org}/booking/{id}?token=…`, shows
   "Pagamento in elaborazione" for a moment and "Prenotazione confermata!" once the webhook `payment_intent.succeeded` is
   processed. Without the Connect webhook (misconfigured endpoint) it stays in elaboration: that is the real state.
2. Test mode, a redirect method (e.g. iDEAL / Bancontact test page): "Fail test payment" brings back to the outcome page with
   "Pagamento non riuscito" and "Riprova il pagamento" on the same booking (no 409); "Authorize" ends as confirmed.
3. SEPA Debit test IBAN `DE89370400440532013000`: "Pagamento in elaborazione" until Stripe settles it.
4. Checkout with check-in tomorrow and 10 nights: no "Paga più tardi" button; check-in in 30 days: the button shows
   check-in − 7 (or the policy's day).
5. `POST /api/public/bookings/{id}/outcome` with another token: 404 `checkout_link_invalid`.

Code: `Casazen.Core/Services/CheckoutOutcomes.cs`, `DirectBookingPaymentRules.cs`, `ICheckoutOutcomeService.cs`,
`Casazen.Infrastructure/Services/CheckoutOutcomeService.cs`, `BookingService.cs`, `PublicBookingsController.cs`. Frontend:
`src/features/public-booking/checkout-outcome-page.tsx`, `checkout-outcome.ts`, `components/stripe-intent-payment.tsx`,
`checkout-page.tsx`, `src/lib/pending-checkout.ts`. Tests: `DirectCheckoutIntegrationTests` (outcome, token, resume,
codes, payment options), `CheckoutOutcomesTests`, `DirectBookingPaymentRulesTests`, `BookingServiceTests`; vitest
`checkout-outcome-page.test.tsx`, `checkout-outcome.test.ts`, `checkout-page.test.tsx`.

## 8. "Le mie prenotazioni" with the booking code (BK-11)

Task BK-11 (audit defects A3-10, R-06, P1). The page `/book/{orgSlug}/my-bookings`, linked from the confirmation email
and from the site menu, sent only `{ email }` while the API required a `bookingId`: every search answered 400 and the page
showed "Nessuna prenotazione trovata" plus an error.

### 8.1 Booking code

- Every booking has a **`Bookings.BookingCode`** (migration `AddBookingCode`): 10 random characters of the Crockford
  base32 alphabet (digits and letters without I, L, O, U: 50 bits), shown as **`XXXXX-XXXXX`**, unique per org (index
  `IX_Bookings_OrgId_BookingCode`). It is not the booking id: the id is logged, in the host's links and in Stripe metadata,
  so it is no secret. `Casazen.Core/Services/BookingCodes.cs` (frontend mirror `src/lib/booking-code.ts`).
- The migration gives existing bookings a random code of the same form (`gen_random_uuid()` bytes, PostgreSQL 13+).
- Where the guest reads it: the **confirmation email** (BK-10, "Codice prenotazione", with the button "Le mie prenotazioni"
  that opens `/book/{orgSlug}/my-bookings?code=XXXXX-XXXXX`) and the **checkout outcome page** (BK-07, "Codice prenotazione"
  with the same link). The host's "new booking" email shows the same code.
- What the guest types is normalized like the code is read aloud: spaces and dashes ignored, lower case accepted, `O` read
  as `0`, `I` and `L` as `1`.

### 8.2 Lookup

| Endpoint | Body | Answers |
|---|---|---|
| `POST /api/public/bookings/lookup` | `{ orgSlug, bookingCode, email }` | 200: `bookingCode`, `status`, `paymentOption`, property (id, slug, name, city), `checkInDate`, `checkOutDate`, adults/children, `lodging`, `cleaningFee`, `touristTax`, `totalPrice`, `paidAmount`, `refundedAmount`, `currency`, `expiresAt`, `deferredChargeDate`, `host { name, email }`, `checkIn { status, opensOn, linkSentAt }`. 400 `validation_error`; **404 `guest_booking_not_found`**; 429 `rate_limited` |
| `POST /api/public/bookings/lookup/check-in-link` | same | **202**: a new online check-in link (CO-02) emailed to the address of the booking; 404 `guest_booking_not_found`; 409 `guest_check_in_link_unavailable`; 429 |

- **No enumeration.** Org of the site (`orgSlug`), code and email (trimmed, case-insensitive) are matched in one query. A
  code that does not exist, a malformed code (the booking id, for instance), a code of another org's site and a wrong
  email all get the same 404 `guest_booking_not_found`, same body except the trace id. Only bookings of the public checkout
  (`Source = Direct`): the guests who receive a code. Code and email travel in the body, never in an API URL; nothing of
  them is logged (the per-email limiter keys on a hash).
- **Rate limits (FD-10).** Per client IP: policy `PublicGuestBookingLookup` (10 requests per 5 minutes, both endpoints
  together). Per email, whatever the IP: `GuestBookingLookupPerEmail` (5 per 15 minutes, both endpoints), counted on
  every attempt, found or not, in memory per replica. A code has 2^50 values: with the email limit an attacker who knows a
  guest's email gets 480 tries a day.
- **What the page shows.** `status`: the states of the outcome page (§ 7.1: `Confirmed`, `AwaitingPayment`,
  `PaymentProcessing`, `PaymentFailed`, `AwaitingGuestEmail`, `AwaitingHostApproval`, `Expired`, `Declined`,
  `DatesUnavailable`, `Cancelled`) plus `StayInProgress` (checked in) and `StayCompleted` (checked out). Amounts as
  recorded on the booking (BK-03 tax, PC-07 cleaning); paid and refunded from the Stripe payments. The host's contact is
  the one of the site footer and of the confirmation email (`Org.DisplayName`, `Org.ContactEmail`). **No personal data**:
  no guest name, email, phone, no booking id; the guest already knows who they are.
- **Online check-in (CO-02).** The raw check-in link is never stored (only its hash) nor shown on the page: the page
  offers **"Inviami il link"**, which emails a new link to the address of the booking (the same steps as the host's "resend
  link": the older links of the booking stop working). `checkIn.status`:

| `checkIn.status` | When | Page |
|---|---|---|
| `NotApplicable` | booking not confirmed / checked in, or the stay is over | nothing |
| `NotYetOpen` | confirmed, arrival more than `CheckIn:SendWindowDays` (3) days away | "the link arrives by email on {opensOn}" (the daily job `GuestCheckInSendJob` sends it) |
| `Open` | from `opensOn` until the departure day | "link sent on {linkSentAt}" when one still works, and the button to get it (again) |
| `Completed` | the guests' data were sent, or the Alloggiati Web communication is done (receipt or declared sent) | "online check-in completed" |

  The button is not offered before `opensOn` on purpose: a check-in session expires 7 days after it is created and the
  daily job does not send a new one while an old one exists, so a link asked weeks before the arrival would be dead on
  the day.

### 8.3 Checks

1. Test mode: complete a checkout, open the confirmation email, click "Le mie prenotazioni": the page opens with the code
   filled in and shows nothing until the email is typed. With the email: state, dates, amounts, host.
2. Same code with another email, a changed character, or on another org's site: "Nessuna prenotazione corrisponde…" (404
   `guest_booking_not_found`), identical in the three cases.
3. Six attempts in a row for the same email (from any IP): the sixth answers 429 with `Retry-After`.
4. A confirmed booking arriving within 3 days: "Inviami il link" → the guest receives the check-in email; the page shows
   "link sent".

```sql
-- Bookings without a code: must be empty after the migration
SELECT count(*) FROM casazen_prod."Bookings" WHERE "BookingCode" = '';
```

Logs: `Rate limit exceeded: policy GuestBookingLookupPerEmail on POST …` / `policy PublicGuestBookingLookup` (no email,
no IP), `Check-in link sent again at the request of the guest of booking {BookingId}`.

Not done (DUBBI BK-11): a "magic link" (the guest types only the email and receives the links of their bookings). The
code + email lookup covers the confirmation email's link; a magic link needs a new token per booking, a new email and a
per-address cool-down against mail bombing.

Code: `Casazen.Core/Services/BookingCodes.cs`, `IGuestBookingLookupService.cs`,
`Casazen.Infrastructure/Services/GuestBookingLookupService.cs`, `Casazen.Web/Infrastructure/GuestBookingEmailRateLimiter.cs`,
`PublicBookingsController.cs`, `BookingNotifier.cs`, `PublicSiteLinks.GuestBookings`. Frontend:
`src/features/public-booking/guest-bookings-page.tsx`, `src/lib/booking-code.ts`, `checkout-outcome-page.tsx`
(`BookingReference`). Tests: `GuestBookingLookupPostgresTests`, `BookingCodesTests`, `DirectCheckoutIntegrationTests`,
`BookingEmailsPostgresTests`, `BookingNotifierTests`, `PublicSiteLinksTests`; vitest `guest-bookings-page.test.tsx`,
`booking-code.test.ts`, `checkout-outcome-page.test.tsx`.
