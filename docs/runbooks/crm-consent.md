# Runbook: collecting the marketing consents (CRM, double opt-in)

Tasks E07/F04/US01/T01 to T04 of the Octorate plan, requirement NF-90 (CRM-03: collect the consent where the guest already is, per channel,
with proof). This is the runbook of the **collection of the marketing consents**; the overview of the module (switch, key, suppression list) is
[`crm-marketing.md`](crm-marketing.md), the profiles and contacts are [`crm-profiles.md`](crm-profiles.md). The page of the public site and the
screens of the host are frontend tasks; this page is about the API.

## 1. The rules, in one place

| Rule | Where it lives |
|---|---|
| **Nothing is preselected, nothing is a condition of a booking or of a stay.** A box is ticked by the guest, per channel (e-mail, WhatsApp), or it is not a consent. | The request bodies below |
| **A consent needs the version of its text** (`Gdpr__MarketingConsentVersion`). Without one the consent is not offered and an API call that carries one is refused (422). | Every endpoint of § 4 |
| **An e-mail consent is not marketable until the person confirms** (double opt-in, § 3): `CrmContactRules.CanReceiveMarketing`. | `CrmContactRules`, `MarketingConfirmationService` |
| **Minors are excluded**: at the check-in the consent is the booker's, who must be of age; on the welcome page the guest declares to be of age. | `GuestCheckInService`, `WelcomeConsentService` |
| **A suppression is final.** An address that opted out is never written to again: not by a new consent, not by a new booking, not by an old confirmation link. A consent given afterwards is stored, the contact stays suppressed, and no confirmation e-mail is sent. | `CrmContactService`, `MarketingConfirmationService` |
| **The proof is kept**: the version of the text, the time, the source and the IP address of the person, on the contact (`BasisVersion`, `BasisRecordedAt`, `ConsentSource`, `ConsentIpAddress`) and, for a guest, in the privacy history (`GuestConsentRecords`; the row of a consent names its channel in `Note`: `channel=email`, `channel=whatsapp`). | `CrmContactService.RecordConsentAsync` |
| **No address in a log, in a URL or in a link.** | everywhere |

## 2. Variables

| Variable | Required | Effect |
|---|---|---|
| `Gdpr__MarketingConsentVersion` | to offer any consent | Version of the text of the marketing consent. Unset: the checkout, the check-in, the welcome page and the guest page do not offer it ([`gdpr.md`](gdpr.md)). |
| `Gdpr__SoftOptInNoticeVersion` | to show the soft opt-in notice | Version of the soft opt-in notice of the checkout (art. 130 c. 4 Codice privacy, PO-07). **No default in code or in `appsettings.json`**: the text is the product owner's. Unset: no notice is shown or recorded and the customer's e-mail stays service only (§ 4.1). |
| `SIGNED_LINK_SECRET` | to send the double opt-in e-mail | Signs the confirmation link (and the unsubscribe link). Unset: the confirmation e-mail is not sent (the outcome `Unavailable`, an error in the log that names the variable) and the endpoint that checks the link answers 500 `configuration_error`. Rotating it invalidates the links already sent. |
| `App__PublicSiteBaseUrl` | to send the double opt-in e-mail | The address of the web app the link points at (`/marketing/confirm`). |
| `Email__ApiKey`, `Email__FromAddress` | to send the double opt-in e-mail | The e-mail provider ([`email.md`](email.md)). Unset: the queue refuses the message, nothing is counted as sent, the consent stays unconfirmed. |
| `Crm__ConfirmationLinkValidityHours` | no (72) | How long the link of the e-mail works, 1 to 720. |
| `Crm__ConfirmationMaxSends` | no (3) | How many confirmation e-mails one contact gets, ever, 1 to 10. |
| `Crm__ContactIndexKey` | to store the contacts | The keyed index of the contacts ([`crm-marketing.md`](crm-marketing.md) § 3). Without it the checkout and the check-in still record the consent in the privacy history and never fail, but no contact is made and no e-mail is sent; when the key is there the sync of the CRM makes the **e-mail** contact of a checkout consent (from `Guest.MarketingConsent`) and the contacts of the check-in (from its `MarketingConsentRecord`), but **not the WhatsApp contact of a checkout consent** (§ 4.1). |
| `RateLimiting__PublicMarketingConsent__PermitLimit`, `__WindowSeconds` | no (60 per 10 minutes per client IP) | The limit of the anonymous consent calls ([`proxy-ip.md`](proxy-ip.md)). |

## 3. The double opt-in (T01)

A consent given on an e-mail address is stored as a contact with the basis `Consent` and the confirmation `Unconfirmed`. The service
`IMarketingConfirmationService` (`MarketingConfirmationService`) sends the address a message with a link; only the click makes the contact
marketable.

**What the e-mail says** (`EmailTemplates.MarketingConfirmation`, `EmailTexts.resx` and `.en.resx`, keys `MarketingConfirmation_*`; the
language is the one of the contact, English for `en` and Italian for everything else):

- *Subject*: Confirm your subscription to the news of {organization}.
- *Body*: someone asked, with this address, to receive offers and news from {organization}; to start the subscription confirm that the address is
  yours; until then nothing promotional is written to it.
- *Button* and, for clients that do not draw buttons, the link written out.
- *Validity*: the link is personal and works until {date and time in Italian time}.
- *If it was not you*: ignore the message, nothing happens and nothing will be sent.

Every dynamic value (the name of the organization, the link) is HTML-encoded; there is no tracking pixel; the address that receives the message is
not written in it.

**The link** (`MarketingConfirmationLinks`, same family as the unsubscribe link of `MarketingUnsubscribeLinks`): HMAC-SHA256, with the secret of the
signed links (`SIGNED_LINK_SECRET`), of `{contactId}.{expiresAt}` (the id as 36 lowercase characters, the expiry as `yyyy-MM-ddTHH:mm:ssZ`),
in lowercase hex, valid `Crm__ConfirmationLinkValidityHours` hours. It carries **only the contact id, the expiry and the signature**: never the
address, never the organization. The page is `https://<web app>/marketing/confirm#contactId=…&expiresAt=…&signature=…`: the values are in the
**fragment** of the address, which a browser never sends to a server, so they are in no access log; the page reads them and posts them unchanged as
the query of the call below.

**The calls**

| Call | Who | Effect |
|---|---|---|
| `POST /api/public/marketing/confirm?contactId=&expiresAt=&signature=` | anonymous, limit `PublicMarketingConsent` | Checks the timestamp, the expiry and the signature (in constant time), then confirms the contact. `200 { confirmed: true, alreadyConfirmed }`; a second click answers `alreadyConfirmed: true` and changes nothing; a wrong value changes nothing. Errors: 400 `contact_id_missing`, `expires_at_missing`, `signature_missing`; 422 `invalid_timestamp`, `link_expired`, `invalid_signature`; 404 `contact_not_found` (only after a valid signature); 409 `contact_suppressed` (the address opted out: nothing is confirmed and the opt-out stands); 500 `configuration_error` (no secret). Not behind `Features__CrmMarketing`: a link already sent keeps working. |
| `POST /api/crm/contacts/{id}/send-confirmation` | the host (`guest.write`), flag `CrmMarketing` | Asks for the e-mail of a contact now (a consent collected before the e-mail existed, an e-mail that was lost). `202 { sent }` (`sent: false` when the last one went out less than ten minutes ago). Errors: 404 `crm_contact_not_found` (another org's contact, or out of the reach of a collaborator limited to some properties); 422 `crm_confirmation_not_required` (not an e-mail with a consent, or already confirmed); 409 `crm_confirmation_limit_reached`, `crm_contact_suppressed`; 503 `marketing_confirmation_unavailable` (the message could not be queued). |

**The limits** (the same for every caller): at most `Crm__ConfirmationMaxSends` messages for each contact, **one every ten minutes**; a repeat inside
the ten minutes or past the maximum sends nothing. A message the e-mail queue refuses is not counted. A contact that is not an e-mail address with the
person's consent (service only, an OTA forward, a phone number), that is already confirmed, or that is suppressed, gets none.

**The limits hold when requests arrive together** (the welcome page and the guest page are public, so a script can call them many times at once).
The send is **booked before the message is queued**, by one conditional `UPDATE` of the contact (not suppressed, not confirmed, fewer than the maximum
sent, the last one at least ten minutes old) that also counts the send: of the requests for the same contact that arrive together, only the one whose
update touched the row queues the e-mail, and the others answer "too soon" or "limit reached". When the queue refuses the message (or fails) the
booking is given back, so the count never includes a message that was not queued. Proof: `MarketingConfirmationConcurrencyPostgresTests` (PostgreSQL).

**What the click also does**: `ICrmContactService.MarkConfirmedAsync` (the only call that sets `Confirmed`) and, for the rows of the per-channel
consent that the online check-in recorded before the contacts existed (`MarketingConsentRecord`), the confirmation of the same guest and the same
address, so the profile of the guest reads it too.

### Troubleshooting

| Symptom | Cause |
|---|---|
| The guest says no e-mail arrived | The outcome in the log is `not queued: the e-mail queue refused it` (no `Email__ApiKey`), or `not sent: SIGNED_LINK_SECRET is missing` / `App:PublicSiteBaseUrl is missing`. The contact stays `Unconfirmed` and the send is not counted: fix the variable and ask again from the host (`send-confirmation`). |
| The host gets 409 `crm_confirmation_limit_reached` | Three messages already went out to that contact. Nothing resets the count; the person can still use a link of an earlier message while it is valid. |
| The link says it is expired | 72 hours (`Crm__ConfirmationLinkValidityHours`) passed. The host sends a new one (up to the limit). |
| The click answers 409 `contact_suppressed` | The person opted out after the e-mail was sent. A confirmation cannot lift an opt-out. |

## 4. The collection points

### 4.1 The checkout of the booking site (T02)

`POST /api/public/bookings` ([`direct-booking.md`](direct-booking.md) § 11.6). Three optional fields, none required and none preselected:

| Field | Meaning |
|---|---|
| `marketingConsentEmail` | Offers by e-mail. The old `marketingConsent` still works and counts as this channel. |
| `marketingConsentWhatsApp` | Offers on WhatsApp. Needs the guest's phone number: 422 `direct_booking_whatsapp_requires_phone` when it is missing or is not a number the CRM can store. |
| `softOptInNoticeVersion` | The version of the soft opt-in notice the page showed (art. 130 c. 4 Codice privacy, PO-07). |

- **Consents.** A consent needs `Gdpr__MarketingConsentVersion` (the public org's `marketingConsentVersion`): without it, 422
  `direct_booking_marketing_consent_unavailable` and nothing is stored. Each ticked channel is one row of the privacy history
  (`GuestConsentRecords`: `Marketing`, `Granted`, source `BookingCheckout`, the version, the IP, the time, and in `Note` the channel,
  `channel=email` or `channel=whatsapp`: the register has no column for it and the two rows of a guest who ticks both would be the same),
  **written in the same insert as the booking and the guest** (`IBookingRepository.AddWithGuestConsentsAsync`): a checkout that loses the dates
  or whose payment cannot start leaves none of them. The e-mail channel also marks `Guest.MarketingConsent` (the flag the host's recorded
  withdrawal looks at).
- **Contacts.** Once the guest has a payment to complete (or a request to confirm) each channel becomes a consent contact
  (`ICrmContactService.RecordConsentAsync`: origin `DirectBooking`, source `BookingCheckout`, the version, the IP, the time, the guest and the
  default language of the org), and the e-mail one gets its double opt-in message (§ 3). The history row is not written twice
  (`CrmConsentInput.RecordHistory = false`). **It never fails the checkout**: without `Crm__ContactIndexKey`, or if a contact or the e-mail
  fails, a line in the log (ids and error type, no address) says so and the history has the consent. **What the sync recovers**: the e-mail
  contact (the CRM sync reads `Guest.MarketingConsent` and the history), never the WhatsApp contact of a checkout: the sync builds a WhatsApp
  contact only from the `MarketingConsentRecord` of the check-in, so a WhatsApp consent of the checkout whose contact was not made stays in
  the history (with its channel) and the guest has to give it again (guest page or welcome page). The host can send the confirmation of an
  e-mail contact again (`send-confirmation`).
- **The notice.** `GET /api/public/orgs/{slug}` carries `softOptInNoticeVersion` (`Gdpr__SoftOptInNoticeVersion`; `null` while the product
  owner has not set it, and nothing in code or in `appsettings.json` sets it). A `softOptInNoticeVersion` equal to the current one is recorded
  in the same insert (`SoftOptInNotice`, `NoticePresented`, source `BookingCheckout`, version, IP): the customer is *told*, never asked.
  Any other version, or one when none is configured, is 409 `direct_booking_soft_opt_in_notice_outdated` and nothing is stored. **No
  configured notice means the customer's e-mail stays service only**: the contact service makes it soft opt-in only for a confirmed booking
  with a recorded notice.

### 4.2 The online check-in (T02)

`POST /api/public/checkin/{token}` ([`gdpr.md`](gdpr.md)). The per-channel consent of the portal (`marketingConsentEmail`,
`marketingConsentWhatsApp`, `marketingEmail`) was already validated and recorded in a `MarketingConsentRecord` (unconfirmed). After the check-in
is saved:

- a ticked **e-mail** channel makes the consent contact of the address the guest typed (origin `CheckIn`, source `GuestPortal`, the version, the
  IP, the time, the default language of the org, the guest) and queues its double opt-in message;
- a ticked **WhatsApp** channel makes a contact only when the booker's phone number is known; otherwise the consent stays in the
  `MarketingConsentRecord` and the CRM sync (`SyncFromGuestAsync`) makes the contact when a phone exists;
- a box **left unticked changes nothing**, not even an earlier consent: withdrawing is a deliberate act of its own;
- **minors are excluded**: the consent is the booker's, who must be of age (400 on `MarketingConsentEmail` / `MarketingConsentWhatsApp`
  otherwise), and a submission whose booker is a minor makes no contact;
- the legacy box (`marketingConsent`) still writes `Guest.MarketingConsent` for the privacy history; no campaign reads it any more.

The contacts are made after the check-in is saved and never fail it, with the same rules as the checkout (§ 4.1).

### 4.3 The welcome page of a property (T03)

A guest who scans the QR code in the apartment lands on `/benvenuto/{token}` of the web app and, if they want, leaves an e-mail address and/or a
phone number with a per-channel consent. The host makes the code; the guest uses it without an account.

**The link of a property** (`IPropertyWelcomeLinkService`, `PropertyWelcomeLinksController`, `api/properties/{propertyId}/welcome-link`, flag
`CrmMarketing`, policy `property.write`; a property of another org is 404, a visible one the caller may not edit is 403):

| Call | Effect |
|---|---|
| `GET` | `{ active, url, createdAt }`; `active: false` and nothing else when the property has no link. `url` is `<App__PublicSiteBaseUrl>/benvenuto/{token}`. |
| `POST` | Makes the link: 201 with the new one, 200 with the one that exists. Never changes the code of a link that exists. |
| `POST rotate` | Revokes the active link and makes a new one in the same transaction, 201. The printed code of the old one stops working **at once**. |

The token is 192 random bits in base64url (32 characters), unique among all the links, **revoked ones included**, so a printed code that was replaced
never reaches another property: the revoked row stays. A property has one active link (a partial unique index, not a check of the code: two
requests that race end with one link).

**The public page** (`PublicWelcomeController`, `api/public/welcome/{token}`, anonymous, **not behind the flag**: a code already printed keeps
working; limit `PublicMarketingConsent`; never indexable and never cached):

- `GET` answers `{ orgName, propertyName, marketingConsentVersion }`, no personal data. `marketingConsentVersion` is `null` while the consent text
  has no version: the page then offers no consent. An unknown token and a revoked one are the same 404 `welcome_link_invalid`; so is a deleted
  property and an org that is not active.
- `POST consent` takes `{ email, phone, consentEmail, consentWhatsApp, adultConfirmed, consentVersion, language }`. In this order: 404 for the
  link; 422 `welcome_adult_required` (**adults only**: the guest declares it, as minors are excluded at the check-in); 422
  `welcome_channel_required` (**nothing preselected**, at least one box); 422 `welcome_email_invalid` / `welcome_phone_invalid` (an address for each
  ticked box, and **only what is ticked is used**: an address typed and not consented to is neither stored nor hashed); 422
  `welcome_consent_unavailable` (the text has no configured version, or the CRM has no key to store a contact); 409 `welcome_consent_outdated`
  (the version sent is not the current one).
- Then a **lead profile** is found (through the keyed e-mail contact, or the phone key) or made empty, the property of the link is added to its
  properties (so the host of that property sees the lead; a later booking recomputes them), each ticked channel becomes a consent contact (origin
  and source `WelcomePage`, the version, the IP, the time) and the double opt-in e-mail goes out (§ 3). No guest is created.
- **The answer is always `202 { confirmationSent: true }`** when the call is valid, whether the address is new, known, already confirmed or
  suppressed: the page cannot be used to learn who is a customer, and it **never lifts a suppression** (a consent given by a suppressed address is
  stored, the contact stays suppressed, no message is sent). The `true` is a constant, not a report of what was sent.

**The demo** (scenario `crm-welcome-link`, [`demo-environment.md`](demo-environment.md)): the lake villa has an active link with the fixed test
token `TEST-benvenuto-villa-lago-demo-01`.

### 4.4 The guest page of a booking (T04)

The guest who opened the guest area of a booking site (`POST /api/public/guest/session` with the booking code and the e-mail of the booking)
reads and changes the marketing choices of **their own booking**. Policy `GuestSession`, `api/public/guest/bookings/{code}/marketing`, not behind
the flag, never cached. A booking of another guest, of another site or that does not exist is the same 404 `guest_booking_not_found` as everywhere
in the area, for the read, the change and the resend, and nothing is read or written for it.

| Call | Effect |
|---|---|
| `GET` (limit `PublicBookingLookup`) | `{ marketingConsentVersion, softOptInNoticeVersion, email: { state, maskedAddress, canResend }, whatsapp: { state, maskedPhone } }`. **`state`**: `none` (no contact of the guest on the channel), `pending` (an e-mail consent waits for the click), `confirmed` (the guest consented, and for e-mail confirmed), `soft_opt_in` (the guest was told, not asked: the address of a confirmed direct booking with a recorded notice), `service_only` (used for the stay only), `withdrawn` (the guest opted out). Nothing is shown as ticked unless the guest ticked it: the page reads `state`, it does not guess. The values are masked (`m***@e***.com`, `+39******567`). `canResend` is true when the consent is pending and the limits of § 3 allow another message. |
| `PUT` (limit `PublicMarketingConsent`) | `{ email: { enabled, address }, whatsapp: { enabled, phone }, consentVersion, language }`; a channel left out stays as it is, and **a channel that is named must say `enabled`** (a body with `{ "email": { "address": "…" } }` is a 400 `validation_error` and changes nothing: an opt-out is final, so a forgotten member is never taken for one). **On**: a consent contact (origin and source `GuestPage`, the version, the IP, the time, the guest) and, for e-mail, the double opt-in message; the address is the one of the booking when it is a real address (not an OTA forward), else it must be typed (an address typed next to a real one is ignored: a session proves the booking, not the ownership of another address, and the confirmation e-mail goes to the address of the consent); WhatsApp needs a phone number (the booking's or a typed one). The same consent again on the same text adds nothing to the history and sends no second message. **Off**: every open contact of the guest on the channel is suppressed (source `unsubscribe`, consent source `GuestPage`, the address goes on the suppression list) and the withdrawal is written in the privacy history; an e-mail address that is no contact yet is made one and suppressed, so a notice or a booking that comes later cannot make it reachable. The answer is the choices as they are. |
| `POST resend-confirmation` (limit `PublicMarketingConsent`) | The double opt-in message of the pending e-mail consent again, with the limits of § 3. `202 { sent }` (`false`: nothing was waiting, or the limits did not allow another). |

- **An opt-out is final.** Turning the channel on again after it was turned off stores the new consent and lifts nothing: the state stays
  `withdrawn`, no message is sent, the address is never marketable.
- **The version** of the consent text must be the current one to turn a channel on: 409 `guest_marketing_consent_outdated`; 422
  `guest_marketing_consent_unavailable` when the text has no configured version, or when the CRM has no key to store the choice here
  (`Crm__ContactIndexKey`: the page cannot record an opt-out it cannot suppress, so it says so). Turning a channel **off** needs no version.
- 422 `guest_marketing_email_invalid` (the booking has an OTA forward or no address and none was typed, or the typed one is not a plain address
  or is a relay of an OTA) and `guest_marketing_phone_invalid`.
- The request is validated as a whole before anything is written: a request that fails changes nothing.
