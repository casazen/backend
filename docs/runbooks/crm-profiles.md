# Runbook: CRM profiles, contacts and the sync with the bookings

Tasks E07/F01/US02/T01 (matching) and T02 (contacts) of the Octorate plan, requirements NF-88 (unified customer database) and NF-89
(legal basis of a contact). This page is the runbook of the profiles; the overview of the marketing
module (the switch, the variables, the key) is [`crm-marketing.md`](crm-marketing.md). The tables are those of E07/F01/US01
([`TECHNICAL.md`](../TECHNICAL.md)): `GuestCrmProfiles`, `GuestContacts`, `GuestMergeCandidates`.

## 1. What runs

| Piece | What it does | Where |
|---|---|---|
| `CrmNormalizer` | The one place that turns an e-mail address, a phone number and a name into the form the keyed index hashes | `Casazen.Core/Services/CrmNormalizer.cs` |
| `OtaForwardAddress` | Says whether an address is one an online travel agency forwards a guest's mail through | `Casazen.Core/Services/OtaForwardAddress.cs` |
| `GuestMatchRules` | The decision table of the matching (scores, reason codes, the cap of proposals) and what a stay is | `Casazen.Core/Services/GuestMatchRules.cs` |
| `IGuestMatchingService` | Finds or creates the profile of a guest or of a lead, proposes merges, keeps the aggregates | `Casazen.Infrastructure/Services/GuestMatchingService.cs` |
| `CrmContactRules` | Who may be written to, on which channel and why (`CanReceiveMarketing`, `CanReceiveService`, `ReachabilityReason`) | `Casazen.Core/Services/CrmContactRules.cs` |
| `ICrmContactService` | The one writer of the contacts: origin, legal basis, double opt-in, suppression | `Casazen.Infrastructure/Services/CrmContactService.cs` |

All of it is registered in `AddCrmMarketing` (`Casazen.Web/Extensions/CrmServiceCollectionExtensions.cs`). **Nothing here is behind
`Features__CrmMarketing`** (that flag closes the endpoints and the send of the campaigns), but everything needs the key of the keyed index,
`Crm__ContactIndexKey` ([`crm-marketing.md`](crm-marketing.md) § 3): without it nothing can be hashed.

## 2. Matching: who is the same guest (T01)

### 2.1 The keys

A key is an HMAC-SHA256 (lowercase hex, 64 characters) of a normalized value, keyed by `Crm__ContactIndexKey`. The database holds the hash and
never the value; a copy of the database cannot be used to test addresses, numbers or birth dates against it.

| Key | Over | Kept in | Skipped when |
|---|---|---|---|
| E-mail key | the normalized address | `GuestContacts.ValueHash` of the e-mail contact (the profile is its `GuestCrmProfileId`) | the address is an OTA forward (§ 2.2), or is not a plain address |
| Phone key | the phone number as E.164 | `GuestCrmProfiles.PhoneKeyHash`, and `ValueHash` of the phone contact | the number is not a phone number (§ 2.4) |
| Name-and-birth key | `<normalized name>|<yyyyMMdd>` | `GuestCrmProfiles.NameBirthKeyHash` | there is no birth date, it cannot be a person's (before 1900 or after today in Rome), or the name has fewer than two words |
| Composite key | `email|name|surname|yyyyMMdd|fiscal code` (ADR-021-R2) | `GuestCrmProfiles.CompositeKeyHash` | never; it only describes the profile, nothing is found by it |

The birth date therefore exists in the CRM only inside the third hash. Nothing else of the registration is read (§ 2.7).

### 2.2 The decision table (`GuestMatchRules`)

| Evidence | Reason | Score | What the matching does |
|---|---|---|---|
| Same e-mail address, not an OTA forward | `same_email` | 100 | the guest is linked to the profile of the e-mail contact |
| Same normalized name and birth date | `same_name_birth` | 90 | the guest is linked to the profile with that key; when the e-mail points to another profile, the guest goes to the e-mail one and the pair is **proposed** |
| Same phone number | `same_phone` | 80 | **proposed**, never linked: a phone is shared by a family, a company, an agent who books for others |
| Same normalized name and a property both stayed in | `same_name_same_property` | 60 | **proposed** |

The order for a guest is: the profile it already has (a merged profile is followed to the one that took its place), then the e-mail contact, then
the name-and-birth key; with none a profile is created, and the proposals of phone and of name-at-a-property are made for it. A pair that has
several reasons takes the highest score and lists them all, the strongest first.

**An OTA forward is never a key.** An address is a forward when the booking comes from Airbnb, Booking.com, Expedia, Vrbo, TripAdvisor or Agoda
(whatever the address, so it holds for a relay domain nobody has seen), or when its host is one of `Crm__OtaForwardDomains` or a subdomain of one
([`crm-marketing.md`](crm-marketing.md) § 2). A contact already stored as a forward is not a way to find a person either, so a direct booking that
brings the same address does not join the guest of the channel.

### 2.3 Proposals

A proposal is a row of `GuestMergeCandidates`: two profiles, a score and the reason codes, never a name, a number or a date. The matching **never
executes a merge**; a person decides (the merge is the profile API of E07/F01/US03).

- **One row for each pair**: the smaller id first (`Guid.CompareTo`, the order the database check accepts), unique with the org. A pair that has a row
  in **any** state (proposed, merged, dismissed) is never proposed again, so a dismissed pair stays dismissed.
- **At most five open proposals for a profile**, and none that would take a profile it points at past five: a phone shared by an agent with a hundred
  guests is five questions to a person. The strongest are kept.
- **When** they are made: for a new profile (phone, name at a property); when the e-mail and the name-and-birth key of a guest point at two profiles
  (90); and when a guest that already has a profile is found to hold an address that a contact of another profile holds (100, `same_email`: a race
  of two syncs, an address corrected to one already known). Two writers proposing the same pair at the same moment end with one row.

### 2.4 Normalization (`CrmNormalizer`)

- **E-mail**: trimmed and lowercase (the rule of `ShowcaseBookingRules.NormalizeEmail`); a plain address only (no display name, no spaces, a host with a
  dot, at most 255 characters).
- **Phone**: only the digits count. A number that starts with a plus keeps its calling code (`+44 (0)20…`: the zero in brackets is dropped), `00` at the
  start becomes the plus, and a national number takes `Crm__DefaultCallingCode` (default `+39`); a leading zero of a national number is kept (Italian
  landlines). Under six digits (the digits the person typed) or over fifteen (E.164) it is not a phone number. **A number whose national part is one
  digit repeated (`+39 0000000000`) is a placeholder, not a phone**: the demo and some forms put it in the box, and two guests who both left it must
  never look like the same person.
- **Name**: lowercase, accents and punctuation removed, words sorted, so `Rossi, Mario`, `mario rossi` and `Mário  ROSSI` are one name. An apostrophe
  joins (`D'Angelo` is `dangelo`); every other mark separates (`Anna-Maria` is `anna maria`). A name of fewer than two words (a first name alone) is
  not evidence of a person and gives no key.

### 2.5 The aggregates of a profile

`UpdateProfileAggregatesAsync` recomputes them from the bookings of **every guest linked to the profile**, so a returning guest (a new guest record for
each booking) is one profile with the stays of all of them:

- a **stay** is a booking that is not cancelled, not pending and either checked out or with a check-out date before today in Rome (`TodayInRome` of the
  injected clock); a stay in progress and an arrival to come are not stays yet;
- `TotalStays`, `TotalSpend` (the sum of `TotalPrice` of the stays), `FirstStayAt` and `LastStayAt` (the first and last **arrival** of the stays); with no
  stay both dates are the creation of the profile and `TotalStays` is 0;
- `PropertyIdsJson` lists the properties of **every booking that is not cancelled, the ones to come included**, so the scope of a host sees a guest
  before the arrival; sorted, so that the same bookings always write the same text (at most 50 properties fit the column; more are logged and cut);
- nothing is written, not even `UpdatedAt`, when the values are the same.

The old fields `ConfirmedMarketingEmail`, `MarketingConsentEmail` and `MarketingConsentWhatsApp` of the profile are not maintained: the source of
truth of who may be written to is the contacts and `CrmContactRules` (§ 3).

### 2.6 A person with no booking (the welcome page)

`FindOrCreateLeadProfileAsync(orgId, email, phone, displayName, language)` finds the profile through the e-mail contact, or through the phone key **when
exactly one profile has it**; otherwise it creates an empty profile (no stay, no primary guest, no `Guest`) and, when several profiles share the
phone, proposes the merge with them. It does not write the contact: the caller does, with `ICrmContactService`.

### 2.7 What it never reads

Never `StayGuest`, a document, the place of birth, the citizenship, the sex or any other Alloggiati field of the booker (AC-NF88-6, Q1-05). The `Country` of the guest (not the
citizenship) fills the country of the profile when empty. The logs carry ids and counts, never a name, an address, a number or a hash.

## 3. Contacts (T02)

### 3.1 Origin, double opt-in, legal basis, suppression

A contact is one row for each org, channel (e-mail, phone, WhatsApp) and value, found by its keyed hash. It records where it came from (`Origin`), whether
the person confirmed it (`Confirmation`: unconfirmed, pending, confirmed, with `ConfirmedAt`), the legal basis **for that channel** (`LegalBasis`, with
the version of the text, the source, the address of the person's connection and the time) and whether the person asked not to be written to
(`IsSuppressed`). The stored value is the normalized one (a lowercase address, an E.164 number).

| Origin | Basis the origin gives (`CrmContactRules.ResolveBasis`) |
|---|---|
| `OtaForward` | service only, **always** |
| `Manual` | service only |
| `DirectBooking` | soft opt-in when the soft opt-in notice was recorded for the guest (a `GuestConsentRecord` with purpose `SoftOptInNotice` and a version) **and** the booking is a purchase (confirmed, checked in or checked out); else service only |
| `OctorateContactInfo` | service only; soft opt-in in the same case as a direct booking, **only while `Crm__BookingEngineSoftOptIn` is on** |
| `CheckIn`, `GuestPage` | service only; a consent only through `RecordConsentAsync` |
| `WelcomePage` | none (a person who left an address with no consent has given no basis); a consent only through `RecordConsentAsync` |

Soft opt-in is for e-mail (art. 130 c. 4 Codice privacy): on another channel it is service only. The bases are ordered **consent > soft opt-in > service
only > none**, and a weaker basis never overwrites a stronger one.

### 3.2 Who may be written to (`CrmContactRules`)

| Question | Rule |
|---|---|
| `CanReceiveMarketing` | an **e-mail** address: not suppressed, origin not `OtaForward`, and either soft opt-in or a **confirmed** consent (double opt-in). A **WhatsApp** or **phone** number: not suppressed and a consent |
| `CanReceiveService` | any basis other than none: a marketing opt-out does not stop the messages of a stay |
| `ReachabilityReason` | why a contact cannot be written to for marketing, the first that applies, or `null` |

| Reason | Meaning |
|---|---|
| `suppressed` | the person asked not to be written to on this channel (nothing clears it) |
| `ota_forward` | the address is a relay of an online travel agency |
| `not_confirmed` | a consent without the double opt-in (including the old yes/no flag, § 3.4) |
| `no_basis` | service only, or none: nothing allows marketing |
| `channel_not_available` | the person can be written to but nothing sends on the channel yet (WhatsApp and phone: no provider exists) |

### 3.3 The service

- `UpsertContactAsync`: normalizes, hashes, finds by org, channel and hash, creates or updates. Fills the profile, the guest, the display name and the language
  when the contact has none (the language of the latest page wins); raises the basis to the one the origin gives when that is stronger; an address that
  arrives with an OTA booking, or on a relay domain, is stored `OtaForward` and service only and **no later call raises it**; an address already on the
  suppression list is flagged suppressed. It returns `null` for a value that is neither a plain address nor a phone number, and writes nothing.
- `RecordConsentAsync`: basis **consent**, confirmation **unconfirmed** (a consent already confirmed that is renewed stays confirmed), with the version of
  the text, the source, the address of the connection, the time and the language. With a guest known it appends one `GuestConsentRecord` (marketing,
  granted), so the privacy history stays complete — also for an OTA forward address, whose contact stays service only (the consent is in the history, the
  address is never marketable). A consent on a suppressed contact is stored, and the contact stays suppressed.
- `MarkConfirmedAsync`: sets `Confirmed` and `ConfirmedAt` (idempotent). Nothing else sets it, except the read of a confirmation the check-in recorded
  before (§ 3.4).
- `WithdrawAsync`: suppresses the contact, puts an e-mail address on the suppression list (`ISuppressionService`), returns the basis to service only and,
  with a guest known, appends a `Withdrawn` `GuestConsentRecord`. Idempotent. The suppression is **sticky**: no consent, booking or confirmation
  afterwards clears it.
- A race of two writers that make the same contact ends with one contact: the unique index refuses the second row and the service applies its change on top.

### 3.4 `SyncFromGuestAsync`: the contacts of a guest

Builds the contacts of one guest from what CasaZen already has and links them to the guest's profile:

1. the **e-mail** of the guest record, with the origin of the guest's latest booking (an OTA source: `OtaForward`; `Direct`: `DirectBooking`; anything else,
   including a guest with no booking: `Manual`);
2. the **phone number** of the guest record, service only (a phone of an OTA booking is a number like another: only an e-mail address is a forward);
3. the **per-channel consents of the check-in** (`MarketingConsentRecord`): the marketing e-mail is a consent (typed at the check-in, so it does not arrive
   with the booking: only its domain can make it a forward), **confirmed only when the guest confirmed it** (the record keeps a flag, not a time: the
   time of the confirmation is the time the sync learned of it); WhatsApp is a consent on the phone of the guest;
4. the **old yes/no flag** (`Guest.MarketingConsent`, set by the old check-in and checkout): a consent on the e-mail of the guest, **unconfirmed**, so
   **not marketable until confirmed**, with the details of the latest marketing consent of the privacy history when there is one.

It is idempotent (running it twice changes nothing, not even an update time), flags the addresses already on the suppression list, reads the booker's
record, consents and latest booking only, and ignores a guest that was erased or anonymized.
