# Runbook: direct booking — "Paga in struttura" requests approved by the host

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
| `App__PublicSiteBaseUrl`, `Email__*` | Links and delivery of the emails | — | required, see [email.md](email.md) |

A changed value applies to new requests and new confirmations: a request already waiting keeps the deadline stored in
`Bookings.RequestExpiresAt`. Values below 1 are read as 1.

The option is still offered on every property with Stripe Connect ready (the audit proposed "enabled per property,
default off": not decided, see DUBBI). Without Connect the checkout answers 409 `direct_booking_payments_not_ready`
and the guest reads "Questa struttura non accetta ancora prenotazioni online. Contatta direttamente l'host." (R-11).

## 3. API

| Endpoint | Auth | Answers |
|---|---|---|
| `POST /api/public/bookings` (`paymentOption: "OnSite"`) | anonymous, rate limited | 200 with `emailConfirmationExpiresAt`; 422 `onsite_request_too_many_nights`, `direct_booking_invalid_stay`, `booking_too_many_guests`, `direct_booking_consent_outdated`, `direct_booking_invalid_payment_option`; 409 `booking_dates_unavailable`, `direct_booking_payments_not_ready`; 400 `direct_booking_consent_required`; 404 `not_found` |
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
