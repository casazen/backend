# Runbook: CRM profiles, contacts and the sync with the bookings

Tasks E07/F01/US02/T01 (matching), T02 (contacts) and T03 (sync from the bookings), and E07/F01/US03/T01 and T02 (the profile API, § 6), of the
Octorate plan, requirements NF-88 (unified customer database), NF-89 (legal basis of a contact) and NF-20 (populated CRM). This page is the
runbook of the profiles; the overview of the marketing
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
| `ICrmProfileSyncService` | Syncs the booker of a booking: profile, contacts, aggregates | `Casazen.Infrastructure/Services/CrmProfileSyncService.cs` |
| `CrmBookingEventHandler` | Runs the sync when a booking event arrives on the bus (it only shortens the delay) | `Casazen.Infrastructure/Events/CrmBookingEventHandler.cs` |
| `CrmProfileSyncJob` | Recurring job `crm-profile-sync`: guarantees the result and backfills the guests with no profile | `Casazen.Infrastructure/Jobs/CrmProfileSyncJob.cs` |

All of it is registered in `AddCrmMarketing` (`Casazen.Web/Extensions/CrmServiceCollectionExtensions.cs`). **The sync is closed with the module**: the
event handler, the job and the demo seed run only with `Features__CrmMarketing` **on and** the key of the keyed index, `Crm__ContactIndexKey`
([`crm-marketing.md`](crm-marketing.md) § 3), set (`CrmProfileSyncGate`); with either missing they do nothing and say so in the log (§ 4.4). The
reason is privacy: the sync copies the e-mail, the phone and the name of the bookers, and a keyed hash of the birth date, into `GuestContacts`,
`GuestCrmProfiles` and `GuestMergeCandidates`, which the erasure, the anonymization and the retention of the guests (`GdprService`,
`GuestDataEraser`, `GuestDataRetentionService`) reach since task E07/F06/US02/T01 ([`gdpr.md`](gdpr.md) § 13). **Neither the key nor the flag is set
in production until the product owner decides** ([`deploy-checklist.md`](deploy-checklist.md)).

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

**An OTA forward is never a key.** An address is a forward when the booking comes from an OTA channel (whatever the address, so it holds for a relay
domain nobody has seen), or when its host is one of `Crm__OtaForwardDomains` or a subdomain of one ([`crm-marketing.md`](crm-marketing.md) § 2). An
OTA channel is what the code calls one everywhere, `FiscalCopy.IsOtaBookingSource` (`OtaForwardAddress.IsOtaSource` asks it, there is no second
list): Airbnb, Booking.com, Expedia, Vrbo, TripAdvisor, Agoda **and `OtherChannel`**, the portals of Octorate that have no value of their own and the
stays made from the calendar of another channel. A contact already stored as a forward is not a way to find a person either, so a direct booking that
brings the same address does not join the guest of the channel.

### 2.3 Proposals

A proposal is a row of `GuestMergeCandidates`: two profiles, a score and the reason codes, never a name, a number or a date. The matching **never
executes a merge**; a person decides (the merge is the profile API of E07/F01/US03, § 6).

- **One row for each pair**: the smaller id first (`Guid.CompareTo`, the order the database check accepts), unique with the org. A pair that has a row
  in **any** state (proposed, merged, dismissed) is never proposed again, so a dismissed pair stays dismissed.
- **At most five open proposals for a profile**, and none that would take a profile it points at past five: a phone shared by an agent with a hundred
  guests is five questions to a person. The strongest are kept.
- **When** they are made: for a new profile (phone, name at a property; saved with the profile, § 4.2); when the e-mail and the name-and-birth key of a guest point at two profiles
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

Never `StayGuest`, a document, the place of birth, the citizenship, the sex or any other Alloggiati field of the booker (AC-NF88-6, Q1-05); the
sync loads a record with the columns it needs and nothing else ([§ 4](#4-the-sync-with-the-bookings-t03)). The `Country` of the guest (not the
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

1. the **e-mail** of the guest record, with the origin of the guest's latest booking (an OTA source, `OtherChannel` included (§ 2.2): `OtaForward`; `Direct`:
   `DirectBooking`; anything else, including a guest with no booking: `Manual`);
2. the **phone number** of the guest record, service only (a phone of an OTA booking is a number like another: only an e-mail address is a forward);
3. the **per-channel consents of the check-in** (`MarketingConsentRecord`): the marketing e-mail is a consent (typed at the check-in, so it does not arrive
   with the booking: only its domain can make it a forward), **confirmed only when the guest confirmed it** (the record keeps a flag, not a time: the
   time of the confirmation is the time the sync learned of it); WhatsApp is a consent on the phone of the guest. **A consent given before the last end of
   the guest's marketing consent (an `Expired` event of the retention, a `Withdrawn` one) is not read again** (the record stays as proof, but the sync
   would give back what the end took away); one given after it counts;
4. the **old yes/no flag** (`Guest.MarketingConsent`, set by the old check-in and checkout): a consent on the e-mail of the guest, **unconfirmed**, so
   **not marketable until confirmed**, with the details of the latest marketing consent of the privacy history when there is one.

It is idempotent (running it twice changes nothing, not even an update time), flags the addresses already on the suppression list, reads the booker's
record, consents and latest booking only, and ignores a guest that was erased or anonymized.

## 4. The sync with the bookings (T03)

### 4.1 The three paths

| Path | When | What |
|---|---|---|
| Event | `BookingCreated`, `BookingConfirmed`, `BookingModified`, `BookingCancelled`, `CheckOutCompleted` on the event bus (ADR-022), under the org of the event | `CrmBookingEventHandler` syncs the booker of the booking at once. It **only shortens the delay**; a failure is logged and never fails the event |
| Recent bookings | job `crm-profile-sync`, every 15 minutes | the bookers of every booking with `UpdatedAt` in the last two hours: **this guarantees the result** (an import or a producer that records no event is covered here) |
| Backfill | the same job, after the recent bookings | 200 guests with no `GuestCrmProfileId` (not erased, oldest first); it needs no watermark of its own and **ends by itself** when every guest is linked |

`ICrmProfileSyncService.SyncBookingAsync(orgId, bookingId)` loads the booker of the booking (the record, never `StayGuest`), runs the matching, builds the
contacts, recomputes the aggregates, and sets the language of the profile from the most recent contact language and its country from the guest when
empty. `SyncGuestAsync(orgId, guestId)` is the same for a guest in hand (the backfill, a guest created from the guest list who never had a booking).

### 4.2 Idempotency, failures, tenant

- **Running it twice changes nothing**, so a retry or an event delivered again never duplicates a profile or a contact. If a run stops half way, what it
  saved stays (each step saves its own changes) and the next run finishes it.
- **A new profile is saved with the questions about its duplicates.** The profile, the link of the guest and the proposals for the profiles that share its
  phone or a name at a property are one `SaveChanges` (all or nothing): a run that fails there leaves no profile and the guest unlinked, so the next run
  makes the profile again with its proposals (they are made once, when the profile is made, and a linked guest does not make them again). When the e-mail
  and the name-and-birth key of a guest point at two profiles, the question about that pair is stored **before** the guest is linked, for the same reason.
  If another sync proposed one of the pairs at the same moment (the unique index refuses the batch), only that pair is lost: the others are stored in a
  second pass.
- **One profile for one guest.** The event handler and the job can reach the same guest, with no profile yet, at the same moment. The profile is made
  and the guest linked to it under a PostgreSQL advisory lock of the guest (`PostgresAdvisoryLocks.Scope.CrmGuestProfile`, held only while the profile
  is saved); the second sync reads the link of the guest again inside the lock and uses the profile of the first, so no profile is left with no guest.
  Two **different** guest records of one person (the same address) synced at the same moment can still make a profile each: the next sync of either
  finds the contact of the other and proposes the pair (`same_email`, 100, § 2.3), and a person decides.
- **A booking that fails** is logged with its ids, the kind of error and the SQLSTATE (never the message, which can carry a value) and skipped; the next
  run retries it. A failing booking never stops the job or the event.
- **The job reads across the orgs** with `IgnoreQueryFilters([TenantQueryFilter])` (ids only), then syncs each booking or guest in a scope of its own under
  its org (`IBackgroundTenantScope`, as the event bus does): the scope has its own `DbContext`, the tenant filter sees that org only, and every row is
  written with its own `OrgId`.
- A run starts no booking or guest after ten minutes, so it ends before the next one is due; what is left is done by the next run. Never two runs at
  once (`[DisableConcurrentExecution]`, 60 s lock wait, [`hangfire.md`](hangfire.md)).

### 4.3 The backfill

The first runs after the module is opened (flag on, key set; § 4.4) link the guests that existed before: 200 a run, every 15 minutes, so 19,200 a day. The log of each run says how many
bookings and guests it read, synced and failed, and `more guests to link` while the chunk was full. To follow it:

```sql
-- Guests still to link (the backfill ends when this is 0)
SELECT count(*) FROM casazen_prod."Guests"
WHERE "GuestCrmProfileId" IS NULL AND "DataAnonymizedDate" IS NULL AND NOT "IsDeleted" AND NOT "ErasureRequested";

-- Profiles, contacts and proposed merges
SELECT (SELECT count(*) FROM casazen_prod."GuestCrmProfiles") AS profiles,
       (SELECT count(*) FROM casazen_prod."GuestContacts") AS contacts,
       (SELECT count(*) FROM casazen_prod."GuestMergeCandidates" WHERE "Status" = 1) AS open_proposals;
```

### 4.4 While the module is closed

With `Features__CrmMarketing` off, or `Crm__ContactIndexKey` not configured (`CrmProfileSyncGate`; production has neither today,
[`crm-marketing.md`](crm-marketing.md) § 3), the sync is closed: the job ends at once with one line (`CRM profile sync skipped: Features:CrmMarketing is
off or Crm:ContactIndexKey is not configured`) and does not fail, the event handler does nothing, and the demo reset skips the CRM profiles. The gate is asked
at every run and at every event. **No profile and no contact is created in an environment until its flag is on and its key is set**; neither alone opens it
(the keyed index also serves the suppression list of a withdrawal, so a key can exist for that). **Production stays closed until the product owner decides**
(the erasure and the anonymization of the CRM contacts, E07/F06/US02/T01, are delivered: [`gdpr.md`](gdpr.md) § 13): no key and no flag there
([`deploy-checklist.md`](deploy-checklist.md)).

### 4.5 The demo showcase

The scenario `crm-profiles` ([`demo-environment.md`](demo-environment.md)) adds seven bookings with invented guests (`@example.com`, texts versioned
`DEMO-TEST-1`): a returning guest with a stay that is over at Villa del Lago and one to come at Casa Trastevere; a Booking.com booking at Attico Firenze
(its address becomes an OTA forward, service only); a direct customer at Loft Torino who was shown the soft opt-in notice (soft opt-in); a guest at Villa
Amalfi with a confirmed marketing consent from the online check-in; and two guests at Appartamento Napoli who left the same phone number (a merge
proposed, not done). With the sync open (§ 4.4) the reset runs the CRM sync itself on every showcase booking (one scope under the demo org for each), marks
the profiles, contacts and proposed merges it made (`TestDataEntities.CrmProfile`, `CrmContact`, `CrmMergeCandidate`) and deletes them before the guests
and houses go; with it closed the bookings are there and no CRM profile is made. Every other demo guest has a placeholder phone number that the CRM does
not take for a phone, so no two of them share one.

## 5. Troubleshooting

| Symptom | Meaning / action |
|---|---|
| `CRM profile sync skipped: Features:CrmMarketing is off or Crm:ContactIndexKey is not configured` | The module is closed in this environment: nothing is synced, by design. On test, to try it: set the key ([`crm-marketing.md`](crm-marketing.md) § 3) and `Features__CrmMarketing=true`, and the next run starts. In production leave both unset until E07/F06/US02/T01 is delivered (§ 4.4) |
| `CRM sync of booking … failed: DbUpdateException/23505/UIX_GuestContacts_OrgId_Channel_ValueHash` | Two writers made the same contact at the same moment; the next run finds it and goes on. Nothing to do |
| `CRM sync of booking … failed: …` repeated for the same ids | Look at the kind of error in the line; the booking is retried every 15 minutes while it is in the two-hour window, and the guest by the backfill while it has no profile |
| `more guests to link` in every run | The backfill is working through a long list, 200 a run |
| A guest with no profile although the job ran | The guest was erased, anonymized or asked for erasure (left alone on purpose), or has no row in the org of the booking |
| Two profiles for one person | The matching proposes the merge (`GuestMergeCandidates`); a person decides. Two addresses and no common name, phone or birth date cannot be told apart |
| An address is not marketable | `CrmContactRules.ReachabilityReason` says why (§ 3.2): forward, not confirmed, service only, suppressed |
| `404 crm_profile_not_found` for a profile the screen showed a moment ago | It was merged into another (the screen holds the id of the tombstone), or the caller does not reach it (§ 6.1); reload the list |
| `409 crm_merge_not_proposed` | The proposal was already decided, by a double click or by someone else (§ 6.2); nothing changed |
| A merge proposal the owner sees is missing for a collaborator «Solo alcuni» | A profile of the pair has a guest (or a contact given by a guest) at a property the collaborator does not reach: it is not listed for it and answers 404 `crm_merge_candidate_not_found` (§ 6.2, fail closed); the owner or an administrator decides it |
| `422 crm_tags_invalid` with fewer than 50 tags | The tags together must fit 2,000 characters (the column); shorten them or drop some |

## 6. The profile API: list, detail, tags and merge (E07/F01/US03)

Tasks T01 and T02. The routes are in [`TECHNICAL.md`](../TECHNICAL.md) (CRM profiles), all behind `Features:CrmMarketing` (§ 1, [`crm-marketing.md`](crm-marketing.md)). The
pieces: `ICrmProfileReadService` (`CrmProfileReadService`: the scope, the list, the detail, the tags), `ICrmProfileMergeService`
(`CrmProfileMergeService`: list, dismiss, merge), `CrmProfileRules` (the tags, the channel, the group of a stay: pure functions), the controller
`CrmController` (thin), and the DTOs in `Casazen.Web/DTOs/Crm`.

### 6.1 Who sees what

- A caller who reaches the whole org sees every living profile, a lead from the welcome page included. A caller limited to some properties
  (`HostScope`, a collaborator «Solo alcuni») sees a profile when one of its guests has a booking at one of them (`InScope` for `GuestCrmProfile`,
  one `EXISTS`); a lead has no guest, so it is never in a limited scope; a caller with no property sees nothing and is never widened to the org.
- For a limited caller everything that counts or names comes from the bookings and the guests it reaches: stays, spend, first and last arrival,
  channels, the sort and the `minStays` filter, the history and the totals of the detail, the reviews, the name shown and the guest record the
  screen opens. The totals stored on the profile are the whole org's and are read only by a caller who reaches the whole org, in the list.
- The e-mail of the list is masked; the detail shows the values of the contacts to the people who may read the guests of the org. A contact given by
  a guest that is erased is not shown, because the erasure does not reach the CRM yet (§ 1). No document, birth date, place of birth, `StayGuest` or
  Alloggiati field is read or returned (`CrmProfileApiProofTests` reads the types and the JSON).
- The search finds a name in the guests the caller reaches; an exact e-mail address or phone number is found by the keyed hash
  ([`crm-marketing.md`](crm-marketing.md) § 3), so without `Crm__ContactIndexKey` only names are searched. The contacts the search and the filter
  `reachableByEmail` look at are the ones the screen shows (`CrmProfileContactReader.Visible`): never one given by an erased guest.

### 6.2 Deciding a merge

A proposal (§ 2.3) is decided by whoever reaches both of its profiles **in full**. A merge moves every guest and every contact of the profile that
goes, so a collaborator «Solo alcuni» does not decide a pair when a profile has a guest (or a contact given by a guest) at a property it does not reach:
the pair is not listed for it, and the merge and the dismissal answer 404 `crm_merge_candidate_not_found`, as for a profile out of its scope; the
owner, the administrators and the managers (the whole org) decide it. The rule is `ProfilesWhollyInScope`; relaxing it is a decision of the PO.
**Dismiss** records who and when and keeps the row, so the pair is never proposed
again. **Merge** moves the guests and the contacts of the profile that goes to the survivor **as they are** (the update sets the link and nothing
else: basis, confirmation, suppression, version and times are untouched, no contact is copied, nothing is decrypted), joins the tags up to the cap,
computes the aggregates of the survivor again, leaves the other profile as a tombstone (`MergedIntoProfileId`; the lists, the scope and the segments
skip it, the matching follows it) and moves the other open proposals of that profile to the survivor, or drops those that would repeat a pair. Both
run under the advisory lock of the org, so a double click decides once; the second one is 409 `crm_merge_not_proposed` and changes nothing
(`CrmMergeProofTests`). There is no undo: a wrong merge is fixed by hand in the database.
