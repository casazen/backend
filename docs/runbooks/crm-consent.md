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
| **The proof is kept**: the version of the text, the time, the source and the IP address of the person, on the contact (`BasisVersion`, `BasisRecordedAt`, `ConsentSource`, `ConsentIpAddress`) and, for a guest, in the privacy history (`GuestConsentRecords`). | `CrmContactService.RecordConsentAsync` |
| **No address in a log, in a URL or in a link.** | everywhere |

## 2. Variables

| Variable | Required | Effect |
|---|---|---|
| `Gdpr__MarketingConsentVersion` | to offer any consent | Version of the text of the marketing consent. Unset: the checkout, the check-in, the welcome page and the guest page do not offer it ([`gdpr.md`](gdpr.md)). |
| `SIGNED_LINK_SECRET` | to send the double opt-in e-mail | Signs the confirmation link (and the unsubscribe link). Unset: the confirmation e-mail is not sent (the outcome `Unavailable`, an error in the log that names the variable) and the endpoint that checks the link answers 500 `configuration_error`. Rotating it invalidates the links already sent. |
| `App__PublicSiteBaseUrl` | to send the double opt-in e-mail | The address of the web app the link points at (`/marketing/confirm`). |
| `Email__ApiKey`, `Email__FromAddress` | to send the double opt-in e-mail | The e-mail provider ([`email.md`](email.md)). Unset: the queue refuses the message, nothing is counted as sent, the consent stays unconfirmed. |
| `Crm__ConfirmationLinkValidityHours` | no (72) | How long the link of the e-mail works, 1 to 720. |
| `Crm__ConfirmationMaxSends` | no (3) | How many confirmation e-mails one contact gets, ever, 1 to 10. |
| `Crm__ContactIndexKey` | to store the contacts | The keyed index of the contacts ([`crm-marketing.md`](crm-marketing.md) § 3). Without it the checkout and the check-in still record the consent in the privacy history and never fail, but no contact is made and no e-mail is sent; the sync of the CRM makes the contacts later, when the key is there. |
| `RateLimiting__PublicMarketingConsent__PermitLimit`, `__WindowSeconds` | no (30 per 10 minutes per client IP) | The limit of the anonymous consent calls ([`proxy-ip.md`](proxy-ip.md)). |

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
