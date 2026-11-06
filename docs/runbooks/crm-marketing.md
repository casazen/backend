# Runbook: marketing module of epic E07 (CRM, consents, suppression, campaigns)

Tasks E07/F06/US01/T01 (flag, options, keyed index) and E07/F06/US01/T02 (keyed suppression list) of the Octorate plan, requirement NF-95
(campaign compliance, CRM-08), NF-89 (contact legal basis). This is the **overview** of the module: the switch, the variables, the key and the
suppression list. The runbook of each feature is created by the tasks of that feature (so no two tasks write the same page):
[`crm-profiles.md`](crm-profiles.md), [`crm-consent.md`](crm-consent.md), [`crm-segments.md`](crm-segments.md),
[`crm-campaigns.md`](crm-campaigns.md), [`crm-octorate-contacts.md`](crm-octorate-contacts.md). Until a task lands, its page does not exist yet.

## 1. State of the module

The module (CRM profiles, segments, campaigns, automations) is unfinished: it must not reach a real send before the compliance work of NF-95 is
done. It is closed behind the feature flag `CrmMarketing`, **off by default**. What the flag does not touch is a legal record, which is made
whatever the flag says.

| Behind `Features__CrmMarketing` (404 before authentication while off) | Not behind it |
|---|---|
| Every endpoint of `CrmController` (`api/crm/*`: the CRM profiles) and of `CampaignsController` (`api/campaigns/*`: segments, campaigns, approval, schedule, send) | The public consent, confirmation, unsubscribe and welcome endpoints (`api/public/marketing/*`: a link already sent keeps working) |
| **The send of a campaign scheduled before**: the Hangfire job `CampaignSendJob` is the only way a campaign leaves, so it is closed with the module (§ 7) | The GDPR endpoints (`api/gdpr/*`), including the host recording a withdrawal: the legal record is saved whatever the flag and the key say (§ 3) |
| The menu entries of the web app that open them (the frontend reads the key `crmMarketing` of `GET /api/public/features`) | The collection of contacts and consents at the check-in and at the checkout of a booking |
| | The suppression list and the keyed index (§ 3 and § 5): they serve these paths too |

While the flag is off `GET /api/public/features` answers `"crmMarketing": false`.

## 2. Variables

| Variable | Required | Default | Effect |
|---|---|---|---|
| `Features__CrmMarketing` | no | unset (off) | The switch of § 1. Missing or anything but `true` is off. Test: the product owner turns it on when he wants to try the module; production: leave it unset |
| `Crm__ContactIndexKey` | to turn the flag on (startup); advised on every environment before that (§ 3) | none | The secret of the keyed index of the contacts and of the suppression list: at least 32 random characters. With the flag on, outside Development and Testing, the application **does not start** without it, and **only then**: with the flag off it starts, and the withdrawal the host records is saved without its suppression (§ 3). A key that is set and shorter than 32 characters stops the startup whatever the flag. In Development and Testing a fixed public key stands in and the log says so once |
| `Crm__OctorateContactsEnabled` | no | `false` | Reads the contacts of the guests from Octorate, one booking at a time (`crm-octorate-contacts.md`). The product owner turns it on once the static IP addresses are whitelisted by Octorate |
| `Crm__BookingEngineSoftOptIn` | no | `false` | A customer of the booking engine of the agency's own site becomes a contact on soft opt-in for a confirmed booking. Turning it on is the product owner's declaration that the terms of Octorate allow it (D24) and that the privacy notice is shown at the booking (D34) |
| `Crm__OtaForwardDomains__0`, `__1`… | no | empty | Bare host names (`guest.example.com`: no scheme, no path, no port, no address) of the relay addresses the OTAs give to their guests. An address on one of them is an OTA forward: service only, never marketing. **Empty on purpose**: no relay domain has been confirmed against the Octorate sandbox (it answers the flags `emailAvailable` and `phoneAvailable`, not the addresses). Add a domain only after seeing it in a real booking. An entry that is not a bare host name stops the startup outside Development and Testing |
| `Crm__ConfirmationLinkValidityHours`, `Crm__ConfirmationMaxSends` | no | `72`, `3` | How long a double opt-in link works (1 to 720 hours) and how many confirmation messages one contact gets (1 to 10). Out of range: startup fails |
| `Gdpr__MarketingConsentVersion` | yes, to offer the marketing consent | none | Version of the text of the marketing consent ([`gdpr.md`](gdpr.md)). Without it the check-in and the checkout do not offer the consent |
| `Gdpr__SoftOptInNoticeVersion` | for the soft opt-in | none | Version of the privacy notice shown to a direct customer (PO-07). **Introduced by the consent tasks** ([`crm-consent.md`](crm-consent.md)): the code does not read it yet |
| `SIGNED_LINK_SECRET` | yes, to send a campaign | none | Signs the unsubscribe and confirmation links ([`email.md`](email.md)); rotating it invalidates the links already sent |

## 3. The key (`Crm__ContactIndexKey`)

The key makes the index of an address a secret: the table `GuestContacts` (`ValueHash`) and the table `SuppressionListEntries` (`EmailHash`) hold an
HMAC-SHA256 of the normalized address, so a copy of the database cannot be used to test addresses against them (like `ServiceCustomers.EmailHash`
with `Suppliers__CustomerIndexKey`, [`suppliers.md`](suppliers.md) § 23).

- **Generate** one per environment, 32 characters at least, random (for example `openssl rand -base64 48`), and keep it in the password manager next
  to the other secrets. Never the same value on test and production, never in the repository.
- **Never change it once a contact or a suppression entry exists.** Every hash already stored would stop matching: the same person would become a
  new contact, and **a person who opted out would stop being found by the suppression list and could be written to again**. There is no rotation;
  losing the key is losing the index (the legacy plain SHA-256 entries of the suppression list keep working, the keyed ones do not).
- **Set it on every environment before turning the module on, and as soon as you can.** The startup asks for it only with the flag on: a missing
  key must not stop production, where the module is off, and the legal record of a withdrawal is made whatever the flag and the key say. Without
  the key (flag off, outside Development and Testing):
  - **the withdrawal that the host records on a guest's request is saved as before**: `PUT api/gdpr/guests/{id}/consent` answers 204, with the
    consent record, the audit entry and the flag `Guest.MarketingConsent` ([`gdpr.md`](gdpr.md));
  - **its suppression is skipped**: the guest's addresses do not enter the suppression list and the CRM contacts are not flagged. The log gets one
    error per withdrawal, `Marketing withdrawal <consent record id> of guest <guest id> recorded WITHOUT its suppression: Crm__ContactIndexKey is
    not configured` (ids and the name of the variable, nothing about the guest);
  - **known risk**: the guest is still left out of every campaign, because the recipients are the guests with `Guest.MarketingConsent`, which the
    withdrawal clears (the list is a second barrier, not the first); but **nothing repeats the suppression later**: a consent that is not in force
    changes nothing, so the endpoint cannot be called again for it. After the key is set, the people withdrawn in that state (the error lines
    name them; so do the `GuestConsentRecords` with source `HostOnGuestRequest` recorded since the release) have to be added to the list by an
    operator, and the consent tasks of the epic ([`crm-consent.md`](crm-consent.md)) must read the withdrawn consents, not only the list, when
    they build the contacts;
  - the public unsubscribe link needs no key (it carries the hash already) and works the same; a campaign is not sent at all with the flag off
    (§ 7).
- Nothing logs the key or a hash: only the org and the source of a suppression.

## 4. Turn the module on in the test environment

1. Railway → project `casazen-api` → environment **`test`** → Variables: add `Crm__ContactIndexKey` (§ 3), then `Features__CrmMarketing=true`. The change
   redeploys the service; with a missing or short key the container exits and Railway keeps the previous deployment (look at the deploy log).
2. Check: `GET /api/public/features` answers `"crmMarketing": true`; a signed-in host gets `200` from `GET /api/campaigns/segments` (without the flag it
   was `404`).
3. Turn it off again: remove `Features__CrmMarketing` (or set `false`). The module answers 404 again; **leave the key set**.

Production: leave `Features__CrmMarketing` unset until the product owner decides, after the checks of NF-95 on the test environment (the keyed
suppression list, the signed link with its page, the GDPR export and erasure, the recipients from the per-channel consents). **Set
`Crm__ContactIndexKey` in test and in production before turning `CrmMarketing` on** (with the flag on and no key the service does not start), and
before that if you want the withdrawals the hosts record to enter the suppression list (§ 3).

## 5. Suppression (E07/F06/US01/T02)

The suppression list (`SuppressionListEntries`, one row per org and index) is what stops the marketing e-mail to a person who asked not to be
written to. It stores an index of the address, never the address.

| Rule | Where |
|---|---|
| **A new entry holds the keyed hash**: the HMAC-SHA256 of the normalized address (`ShowcaseBookingRules.NormalizeEmail`: trimmed, lowercase) by `ICrmContactIndex`, 64 lowercase hex characters, in the same column `EmailHash` (no schema change). Never a plain SHA-256 | `SuppressionService.AddToSuppressionListAsync` |
| **The old entries still suppress.** An address is suppressed when the list holds its keyed hash **or** the plain SHA-256 of the address. The old rows are legacy: never deleted and never rewritten (the address is not there to compute the keyed hash from) | `SuppressionService.IsSuppressedAsync`, `LegacyEmailHash` (read only) |
| **The unsubscribe link carries the keyed hash**, never the address; a link issued before (valid 30 days, plain SHA-256) keeps working. `Verify` does not change: the hash is opaque to it | `MarketingUnsubscribeLinks` |
| **`POST api/public/marketing/unsubscribe`** keeps its signed query and its answers (`{ unsubscribed: true }`, the same problem codes) and adds the entry through the service: once however many times it is posted (a mail client may post `List-Unsubscribe=One-Click` and the person may click too), storing the hash as given | `PublicMarketingController.Unsubscribe`, `SuppressionService.AddByHashAsync` |
| **The CRM contacts with the same keyed hash are flagged** (`IsSuppressed`, `SuppressedAt`, `SuppressedSource`: `unsubscribe`, `manual`, `gdpr`) in the same `SaveChanges` as the entry. A hash of a link issued before cannot match a contact: only the entry is added | `SuppressionService` |
| **A withdrawal the host records on the guest's request** (`PUT api/gdpr/guests/{id}/consent` with `marketingConsent: false` and the documented note) suppresses the e-mail of the guest and every `MarketingConsentRecord.MarketingEmail` of the guest (source `manual`), each address once, in the **same unit of work** as the withdrawal. A consent that is not in force still changes nothing (nothing to withdraw). **The withdrawal never depends on the key**: without `Crm__ContactIndexKey` the consent record, the audit entry and the flag are saved all the same (204) and the suppression is skipped, with an error in the log (§ 3) | `GdprService.UpdateMarketingConsentAsync`, `ISuppressionService.CanIndexAddresses`, `ISuppressionService.StageSuppressionAsync` |
| **An entry is sticky.** Nothing clears it and no later consent does: re-subscription is not specified. Erasing a guest removes its personal data and leaves the hash, so it cannot be written to again | `SuppressionListEntry` |
| **No log holds an address or a hash**: the logs of the service and of the endpoint carry the org and the source | `SuppressionService`, `PublicMarketingController` |

Open point, for the product owner: a guest whose only consent is a per-channel one (the check-in records a `MarketingConsentRecord` and does **not**
set the yes/no flag `Guest.MarketingConsent`, which the campaigns read) is not offered the withdrawal by the GDPR tab, and the endpoint changes nothing
for a consent that is not in force. The withdrawal of those consents is part of the consent tasks of the epic ([`crm-consent.md`](crm-consent.md)).

## 6. Rollback

Turning the flag off hides the module and changes no data, but for one case: a campaign whose send time arrives while the flag is off is not sent and
goes back to Approved (§ 7). The suppression list, the contacts and the key stay: do not remove or change
`Crm__ContactIndexKey` (§ 3). A deployment of the previous version still reads the table (the column is the same) but writes the plain SHA-256 again and
does not know the keyed entries: do not roll back to a version without the keyed suppression once keyed entries exist, unless the product owner accepts
that the people who opted out in the meantime are not honored by it.

## 7. The send of a campaign while the flag is off

The endpoints of `CampaignsController` are closed by the flag, but a campaign can be **scheduled before** the module was closed (or while it was on),
and its send is a Hangfire job (`CampaignSendJob` → `CampaignService.SendCampaignAsync`) that the flag of the controller does not reach. So the
service checks the flag itself, before it reads the recipients, the key or the link secret:

- **Flag off at the time of the send**: nothing is sent, whatever the key and `SIGNED_LINK_SECRET` say. The job writes one warning
  (`Campaign <id> of org <id> not sent: Features:CrmMarketing is off`) and **ends without failing**, so Hangfire does not retry it. A campaign that
  was `Scheduled` goes back to `Approved` with no schedule (an explicit state: it is not waiting for a send that will not happen), and the host can
  schedule it again once the module is on. A campaign already `Approved` is left as it is.
- **Flag on**: as before. The key is then required at startup, so the recipients and the unsubscribe links of the campaign can be computed;
  without `SIGNED_LINK_SECRET` or `App__PublicSiteBaseUrl` the job fails and is retried ([`email.md`](email.md)).
