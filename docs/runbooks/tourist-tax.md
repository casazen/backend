# Tourist tax paid online in the guest check-in

E25/F01/US01/T01 (NF-119, decision O12 of `operazioni/analisi-tecnica.md`). The rates and the calculation guest by guest are in
[`tourist-tax-rates.md`](tourist-tax-rates.md); this runbook is about the **collection**: what the guest pays online, how it is recorded and given back.

## Switches

| Switch | Default | Where |
|---|---|---|
| `Features__TouristTaxOnlinePayment` | off | Railway ([`feature-flags.md`](feature-flags.md), [`deploy-checklist.md`](deploy-checklist.md) § 2.11) |
| `TouristTaxOnlinePaymentEnabled` of the organization | off | `PUT api/orgs/me/tourist-tax-settings` `{ enabled, requiredBeforeAccessCode }` (`OrgBillingAdmin`) |
| `TouristTaxRequiredBeforeAccessCode` of the organization | off | same endpoint; saved as off while the payment is off |

A guest pays only with the flag on, the setting of the organization on and its connected account taking payments (`Org.CanTakeDirectPayments`):
otherwise the guest endpoints answer 409 `tourist_tax_payment_not_available` (404 when the flag is off). `Stripe__PublishableKey` is needed for the
Payment Element (409 without it).

### The lock code is held back by the server (`RequiredBeforeAccessCode`)

The lock code is never held back unless the organization turns on `RequiredBeforeAccessCode`. With it on, the payment offered, the account ready and
`Stripe__PublishableKey` set, **the server** holds the code back, not only the page: `IAccessCodeService.ReleaseToGuestAsync`, the one gate of every release of the
code to a guest, returns no code while the stay owes a tourist tax that the guest can pay online now (due, not paid online, not exempt, not recorded as «Pagata in
struttura», not collected by the channel). A guest who cannot pay (setting off, account not ready, no key, nothing payable online) is never kept out.

- The selfie step (`POST api/public/checkin/{token}/selfie`) answers `result: ok` with **no `lockCode`** and `lockCodeHeld: "tourist_tax_due"`. The guest pays on the tourist
  tax endpoints and sends the step again (a passed verification is not run again): the code comes with that answer.
- Every exit of the code to a guest goes through that gate and is held back the same way: the check-in screen and the e-mail after a passed selfie
  (`IdentityVerificationFlowService`), the e-mail after an operator's approval (`VerificationQueueService`; nothing is e-mailed while it is held back, and it is not
  sent again after the payment). The guest page reads `requiredBeforeAccessCode` of the tourist tax answer to show the step first.
- Not held back: the host's own read of the code (`GET api/bookings/{id}/access-code`, with its scope), which is the host's.

## Endpoints

| Who | Endpoint | What |
|---|---|---|
| Guest (check-in link) | `GET api/public/checkin/{token}/tourist-tax` | what can be paid online now, the currency, the tax by position (no name, never the reason of an exemption), the state |
| Guest | `POST api/public/checkin/{token}/tourist-tax/payment-intent` | the PaymentIntent on the connected account (client secret, publishable key, account id) |
| Host (`booking.read`) | `GET api/bookings/{id}/tourist-tax` | the block `collection`: to collect, paid online, refunded, owed back |
| Host (`payment.write`) | `POST api/bookings/{id}/tourist-tax/paid-at-property` | «Pagata in struttura»: cancels the open PaymentIntent, so the guest cannot pay twice |

A link reaches its own stay only (unknown, expired, cancelled: 404). A check-in already completed still pays its tax.

## What is due (`TouristTaxCollectionRules`)

- The channel collects the tax (the channel rule of the comune or the host's declaration; in Switzerland the portals of the issuer's profile,
  `SwissTouristTax:PlatformCollected` by default): **nothing to pay**, state `CollectedByChannel`. (The **guest document** of an Italian portal booking decides who collects
  with two more sources, the organization's own list of portals and a default, the host at the property: `ItalianPortalTouristTax`, decision 2 of 2026-11-20, see
  [`fiscal-documents.md`](fiscal-documents.md) § The tourist tax of an Italian portal stay. This collection is not changed by it.)
- Who collects is not known (a channel stay with no rule and no declaration) or the amount cannot be calculated: **nothing is charged**, the page says the
  host will tell the amount.
- CasaZen collected the tax with the price: a **direct** booking with a payment through CasaZen that went through (completed, or completed and partly refunded)
  included `Booking.TouristTaxAmount` (decision O12, «per le dirette già pagate»). Only the part of the **confirmed** amount above it is paid online; before the
  host confirms, nothing more is asked; a lower confirmed amount raises the host's «To do» `TouristTaxRefundDue` (no automatic refund). For **any other booking**
  the amount written on the booking (the quote of a manual booking, the figure of an OTA reservation, a direct booking paid at the property or at the deadline)
  is no tax collected by CasaZen and counts as 0: the whole tax is payable online. For a channel stay the double charge is the business of the channel rule of
  the comune (`TouristTaxChannelRule`) and of the host's declaration, decided before any amount counts.
- Otherwise the amount due less what was already paid online. One online payment per stay: what is due beyond it is collected at the property.
- Switzerland: the rule of the canton (`SwissTouristTax:Rules`, guests × nights × rate) when the host collects; no rule: not calculated.
- A no-show or a stay cancelled before any night owes nothing (`NotDue`) in every reader.

## Payment, webhook, refunds

- One PaymentIntent per stay, direct charge on the organization's connected account, no fee of CasaZen, metadata `kind = tourist-tax`, `bookingId`,
  `stayTouristTaxId`; idempotency key `tourist-tax:{record}:{amount}:{previous intent}`. A new amount cancels the open intent first; an intent being paid
  stops everything (409 `tourist_tax_payment_in_progress`) until the webhook records it.
- `payment_intent.succeeded` (Connect endpoint, or the platform endpoint listening to connected accounts): the stay is `PaidOnline` with the amount and
  the currency **of the intent**, once; only the intent the stay asked for, on its account. The hosts get the bell, the pop-up and the live refresh
  (push type `tourist-tax-paid`).
- `charge.refunded`: `RefundedAmount` follows the charge (never more than what was paid). A refund of a payment not recorded yet is delivered again.
- Cancellation or no-show (`BookingCancelled`, `BookingNoShowMarked`): the open intent is cancelled; no night spent: full refund on the connected
  account (idempotency key `tourist-tax-refund:{record}:{intent}`), state `NotDue`; cancelled after the arrival: «To do», the host decides. A refusal of
  the provider that will not pass, or a refund the provider answers `failed`, `canceled` or `requires_action`, raises the «To do» (the code is in the log); one
  that may pass (no answer, 429, 5xx) is retried by the outbox; a refund still being made (`pending`) follows its `charge.refunded`. A refund that fails
  after it was created (`refund.failed`) is not listened to: the host's block keeps showing the amount owed back (`refundDue`).
- A payment recorded **after the stay ended** (the event of its end was consumed before it, or gave up after its five attempts while the payment was on its
  way): the webhook that records it makes the same decision as the event of its end, at once (refund in full when no night was spent, «To do» otherwise); a refund
  the provider cannot make then raises the «To do» instead of waiting for an event that will not come.
- `CheckInCompleted`, `CheckOutCompleted`, `BookingModified`, and the **change of the tax itself** (the confirmation of the tax, in the tax endpoints and in the
  host check-in wizard, a host's exemption, the declaration of the collector: none of them records an event, so the collection is evaluated right after the
  change): the state is evaluated again and a difference owed back raises the «To do» once per booking.

**No `Payment` row is ever created**: the owner ledger, the balance of the booking and the revenue do not change. The collection is on
`StayTouristTaxes` (`CollectionStatus`, `CollectedAmount`, `Currency`, `CollectedAt`, `StripePaymentIntentId`, `StripeAccountId`, `RefundedAmount`; migration
`AddTouristTaxCollection`); a row created by the collection before the host confirms the tax is unconfirmed, like the host's declaration of the collector.

## Try it in the test environment

1. `Features__TouristTaxOnlinePayment=true` on Railway test (`octorate-harness/railway-flags.mjs`), an organization with a ready connected account.
2. `PUT api/orgs/me/tourist-tax-settings` `{ "enabled": true }`.
3. Open the check-in link of a confirmed stay of a comune with a rate, register the guests, pay with a Stripe test card.
4. The booking shows the tax paid online; cancel a stay before its check-in day: the tax is refunded on the connected account.

Rollback: turn the flag off (nothing is offered, nothing is refunded by itself; payments already made stay recorded and their webhooks are still applied).
