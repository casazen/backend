# Runbook: guest contacts read from Octorate, one booking at a time (E07/F06/US02/T02)

Task E07/F06/US02/T02 of the Octorate plan, requirement NF-94 (CRM-07), decisions D24 and D34, ADR-022. This page is the feature runbook of the
marketing module for the contacts that come from the channel manager; the overview of the module is [`crm-marketing.md`](crm-marketing.md), the
rules of who may be written to are in [`crm-profiles.md`](crm-profiles.md) and the rights of the guest on these rows in [`gdpr.md`](gdpr.md) § 13.

## 1. What it does

The e-mail address and the phone number of a guest are **sensitive data** for Octorate: it releases them only to an application with the dedicated
permission, from the static IP addresses registered with it (even in the sandbox, S7, S18, D24), and logs every read. So CasaZen reads the contact
of the guest of **one booking at a time, when someone needs it**, and records it as contacts of the CRM for the service (the messages, the check-in,
the invoice). It never reads a list, never exports, never reads a guest that has no booking.

| Piece | What it is |
|---|---|
| `IOctorateApiClient.GetGuestContactInfoAsync` | The one Octorate operation: `POST /rest/v1/guests/{acc}/{guestId}/contactInfo` with an empty JSON body, through the same client as the other calls (the org's token and bucket, the typed errors, the refresh of a refused token, the backoff of a 429). A **401 or 403 is `OctorateSensitiveDataNotAllowedException`** (code `octorate_sensitive_data_not_allowed`, 409), never an empty result |
| `IOctorateContactsService.ReadForBookingAsync(orgId, bookingId)` | The per-booking read and the storage of what it returns (`OctorateContactsService`). Called by the flows that need a contact (messages E05, check-in E03, invoice E06) and by the handler below |
| `OctorateContactsEventHandler` | On `BookingCreated` of a booking that came from Octorate (payload `Origin = Octorate`) it calls the service, so the contact is there before the first flow asks. It only shortens the wait; a failure is logged with the ids and swallowed |
| `OctorateContactReadLedger` | The memory of the cap per hour and of the bookings read or refused (in memory, one for the application) |

All of it is registered in `AddCrmMarketing` and does nothing while the switches of § 2 are off.

## 2. Switches and prerequisites

| Variable | Default | Effect |
|---|---|---|
| `Crm__OctorateContactsEnabled` | `false` | The product owner turns it on **once the static IP addresses are registered with Octorate** and the hosting that gives them is decided. Off, nothing is read and nothing is queried |
| `Features__OctorateConnector` | off | The connector flag: the contacts are read only while the connector is on |
| `Features__CrmMarketing` and `Crm__ContactIndexKey` | off / none | **Both are needed before anything is read**: what is read is stored by the CRM, and the CRM stores nothing while its module is closed (`CrmProfileSyncGate`, [`crm-profiles.md`](crm-profiles.md) § 4.4). With either missing the call ends `Disabled` before any call to Octorate: nothing is asked, nothing is spent, nothing is remembered. On production neither is set until the product owner decides ([`crm-marketing.md`](crm-marketing.md) § 3), so the contact read waits for that decision too |
| `Crm__OctorateContactReadsPerHour` | `50` | The most bookings one org has their contacts read in an hour (1 to 500; anything else stops the startup). A booking over the cap is read by the next call that needs it, after the hour |
| `Crm__BookingEngineSoftOptIn` | `false` | The product owner's declaration that the terms of Octorate allow it (D24) and that the notice is shown at the booking (D34): a customer of the booking engine of the agency becomes soft opt-in for a **confirmed** booking. Off, service only |
| `Crm__OtaForwardDomains__0`, `__1`… | empty | The relay domains of the OTAs: an address on one of them is an OTA forward whatever the booking |

Prerequisites outside the code, none of which CasaZen can check: (1) the outbound IP addresses of the API are static and registered with Octorate
(S7, D24); (2) the application has the `contactInfo` permission; (3) the operation is checked once against the Octorate sandbox (§ 5).

## 3. The rules, in order

For `ReadForBookingAsync` the first rule that does not hold ends the call, and no Octorate call is made until all of them hold:

1. `Crm__OctorateContactsEnabled` and the connector are on, and the CRM module is open: `Features__CrmMarketing` on and `Crm__ContactIndexKey` set (else `Disabled`, and not even a query).
2. The booking is a booking of the org with an Octorate reservation id **that came from Octorate** (else `NotAnOctorateBooking`). A booking made in CasaZen and sent to Octorate has a reservation id too, and a row of the mirror (`OctorateBookingSyncs`, made when CasaZen creates the reservation, never by the import): it is CasaZen's own, its guest's contact is already here, and it is written on channel `270` (`Octorate__DirectChannelId`), the same channel as the booking engine of the agency, so it must not be taken for a customer of that engine and given its soft opt-in.
3. Its **check-out is not in the past** in Rome (else `StayOver`: no history is read).
4. The booking is **not cancelled** (else `BookingCancelled`: a cancelled booking has no stay to serve, so its guest's contact is not worth a sensitive read). A pending booking is read.
5. Its guest was not erased or anonymized (else `GuestErased`: the contacts erased by [`gdpr.md`](gdpr.md) § 13 do not come back).
6. The ledger does not remember it: a booking read in the last 24 hours is `AlreadyRead` (or `NothingToStore`), one Octorate refused or answered in a form not understood in the last 6 hours is `NotAvailable` or `Unrecognized`: no call.
7. The property of the booking has a confirmed listing on Octorate (else `NotMapped`).
8. The org has fewer than `Crm__OctorateContactReadsPerHour` reads in the last hour (else `OverCap`). Every call that reaches Octorate counts, refused or not.

Then, for the org of the booking: the reservation is read (`GET reservation/{acc}/{id}`: the id of its first guest, and the flags `emailAvailable` and
`phoneAvailable` of that guest), and, unless Octorate says it holds neither an address nor a number, the contact of that guest is read. Two calls for
one booking.

**What is stored** (`ICrmContactService.UpsertContactAsync`, origin `OctorateContactInfo`, so the basis is the one `CrmContactRules` gives):

| Case | Result |
|---|---|
| An address of a booking made through an OTA (Booking.com, Airbnb, Vrbo, Expedia, Agoda, TripAdvisor, another portal) or on a relay domain | Origin `OtaForward`, **service only, never marketing**, never in a campaign |
| An address of the booking engine of the agency (channel `270`), `Crm__BookingEngineSoftOptIn` on, booking confirmed or later | `SoftOptIn` (the notice recorded as `octorate-booking-engine`, source `BookingCheckout`, at the time of the booking) |
| The same, with the flag off, or a booking that is still pending | Service only |
| Any other address | Service only |
| A phone number | Service only, always |
| Airbnb | Octorate gives no address and no number: nothing is stored (`NothingToStore`) |

Whatever is stored is attached to the guest of the booking and to its CRM profile when it has one; the same address given twice is one contact; a
weaker basis never lowers a stronger one and a suppression is never cleared (see `crm-profiles.md`).

## 4. Outcomes and what is remembered

| Outcome | Meaning | Remembered |
|---|---|---|
| `Stored` | At least one address or number was stored | read (24 h) |
| `NothingToStore` | Octorate holds none for the guest (the flags say so, or the answer has the members of a contact, empty, and no flag says Octorate holds one) | read (24 h) |
| `AlreadyRead` | The booking was read in the last 24 hours | - |
| `NotAvailable` | Octorate refused the read (no static IP, no permission), rejected the request (a 4xx that is no missing reservation and no limit), no longer knows the reservation, or names no guest id | refused (6 h) |
| `Unrecognized` | Octorate answered, but CasaZen does not understand the answer: it has none of the members of a contact (another wrapper, other names, not an object), or it is empty while the reservation says Octorate holds an address or a number. **Not "the guest has no contact"**; the log carries the code `octorate_contact_info_unrecognized` and nothing of the answer | unrecognized (6 h) |
| `Deferred` | Octorate did not answer, or a limit was reached, or the connection is not usable, or the body cannot be parsed at all: not the booking's fault | nothing; a later call tries again; **never retried in a loop** |
| `Disabled`, `NotAnOctorateBooking`, `StayOver`, `BookingCancelled`, `GuestErased`, `NotMapped`, `OverCap` | Nothing was read (rules 1 to 8) | nothing |

The ledger is in memory: a restart forgets it, which can cost one more call for a booking and never a stored contact, and with several API
instances each one counts its own reads. Today the API runs as one instance.

## 5. What is assumed and not proven (to check in the sandbox)

The sandbox answers the reservations with flags (`emailAvailable`, `phoneAvailable`), not with contacts, and refuses the contact read before the IP
addresses are registered, so these points are written from the specification and have to be seen once, with a booking of the sandbox:

1. The id of the guest is read from the reservation as `guestId`, else as `id` of its first guest.
2. The operation takes an empty JSON body and answers an object with `email` and `phone` (the usual spellings `emailAddress`, `phoneNumber`,
   `mobile` are read too); a `{data: {...}}` wrapper is opened.
3. The channel code of the customers of the booking engine of the agency is `270` (as the import already assumes).
4. Which relay domains the OTAs use: `Crm__OtaForwardDomains` stays empty until a domain is seen in a real booking.

If one of them is wrong the effect is on the safe side, and it is said: an id that is not found gives `NotAvailable` (nothing stored); a request Octorate
rejects gives `NotAvailable` too; an answer with none of the members of a contact (other names, another wrapper, not an object), or empty while the
reservation says Octorate holds an address or a number, gives `Unrecognized` (a warning with the code `octorate_contact_info_unrecognized`, remembered for
6 hours): that line is the first sign that the shape is not the one assumed, and it is never recorded as a guest with no contact; a body that cannot be
parsed at all gives `Deferred`; and an unknown relay domain is still an OTA forward by the source of the booking.

## 6. Verification once the IP addresses are registered

1. On **test**: `Features__OctorateConnector=true` and `Crm__OctorateContactsEnabled=true`, with the CRM module open: `Features__CrmMarketing=true` and the CRM key ([`crm-marketing.md`](crm-marketing.md) § 3). With either of the last two missing nothing is read, and the outcome of every call is `Disabled`.
2. Import a booking of a sandbox structure (or wait for `BookingCreated`). The log says `Octorate contact of booking {BookingId} of org {OrgId} read: N contacts stored`.
3. In the database: `select "Channel", "Origin", "LegalBasis", "IsSuppressed" from "GuestContacts" where "GuestId" = '<guest id>';` shows origin 5
   (`OctorateContactInfo`) or 1 (`OtaForward`) and basis 1 (`ServiceOnly`).
4. With the IP addresses **not** registered the log says `Octorate guest-contact-info for org {OrgId}: refused (HTTP 403)` and
   `Octorate contact of booking ... is not available`, and no row is written: expected, and not repeated for 6 hours.
5. Set `Crm__OctorateContactReadsPerHour=2`, import three bookings: two are read, the third logs `not read: the 2 reads an hour of the org are used`.
6. If the log says `the answer is not understood (octorate_contact_info_unrecognized)` the shape of the answer of `contactInfo` is not the assumed one (§ 5):
   look at it once in the sandbox, with a booking that has an address, and teach `OctorateApiClient.ReadGuestContactInfo` the names it uses.

## 7. Privacy

No address, number, name, hash or body is ever written to a log by this feature: the logs carry the booking, the org, the outcome, the count of
contacts and the code of an error. `OctorateGuestContactInfo` prints nothing of its data, even if a log line formats it. The contacts are stored
encrypted at rest ([`encryption.md`](encryption.md)). The rights of the guest on them (export, erasure, retention) are [`gdpr.md`](gdpr.md) § 13.
