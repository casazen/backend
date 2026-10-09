# Runbook: guest data rights, consents and retention (GDPR)

Task CO-15 (audit defects A5-12, A5-13, A5-14, A5-15, A9-18). Legal context: `.claude/context/regulations/gdpr.md`
(retention § "Conservazione Limitata", legal bases § "Basi Giuridiche") and `alloggiati.md` § 4 (Police receipt).
Nothing below invents a retention period or a legal text: the periods and the texts are decisions of the product owner
(D14) and stay **off until configured**.

## 1. What the backend does

| Concern | Behaviour | Code |
|---|---|---|
| Erasure (art. 17) | `DELETE /api/gdpr/guests/{id}` and `DELETE /api/guests/{id}` of a guest with past bookings: every personal field of the guest and of the guests of its stays, the IP and note of its consent events, the special requests of its bookings, the **document scan object** in the private storage; open check-in links expire; the record is marked deleted and kept for its bookings. 409 `guest_has_open_bookings` while a stay is open (its Alloggiati communication is still a legal obligation, art. 17.3.b). Idempotent: a second call changes and audits nothing | `GdprService.EraseGuestDataAsync`, `GuestDataEraser` |
| Guest without bookings | `DELETE /api/guests/{id}` removes the row after deleting its scan object (TN-1 rule) | `GuestService.DeleteGuestAsync`, `GdprService.EraseStoredFilesBeforeRemovalAsync` |
| Anonymization | `POST /api/gdpr/guests/{id}/anonymize`: same as the erasure, the record is not marked deleted | `GdprService.AnonymizeGuestDataAsync` |
| Export (art. 15, 20) | `GET /api/gdpr/guests/{id}/export`: versioned JSON (`schemaVersion` = `casazen.guest-data-export/2`) with identity and contacts, birth, **document in clear** (only here), bookings with payments, guests of the stays, check-in links, Alloggiati communications, consent history with versions and IPs, processing status and retention per category. `guest.read` on the guest's org, `Cache-Control: no-store`, audited | `GdprService.ExportGuestDataAsync`, `GuestDataExport` |
| GDPR tab | `GET /api/gdpr/guests/{id}`: current consents with their version, history (no IP), retention per category, status | `GdprService.GetGuestPrivacySummaryAsync` |
| Marketing consent (A5-14) | The host can **never** grant it: `PUT /api/gdpr/guests/{id}/consent` with `marketingConsent: true` answers 422 `gdpr_marketing_consent_host_grant_forbidden`. A withdrawal needs the guest's documented request in `note` (date, channel; 422 `gdpr_marketing_withdrawal_note_required` without it) and is recorded with the host's user id | `GdprService.UpdateMarketingConsentAsync` |
| Check-in portal (A5-15) | The Alloggiati registration is a legal obligation (art. 109 TULPS, art. 6.1.c GDPR): **no consent checkbox**. The portal shows the privacy notice; on submit the notice version is recorded. The marketing consent is optional, offered only when its text has a version, recorded with version, time and IP. An unticked box leaves an earlier consent as it is (it is not a withdrawal) | `GuestCheckInService.RecordPrivacyChoices`, frontend `checkin-page.tsx` |
| History | Append-only `GuestConsentRecords`: purpose (`PrivacyNotice`, `Marketing`), action (`NoticePresented`, `Granted`, `Withdrawn`, `Expired`), version, source (`GuestPortal`, `HostOnGuestRequest`, `RetentionPolicy`), IP, note, time | `GuestConsentRecord` |
| Audit | `GuestPrivacyAuditEntries`: export, anonymization, erasure, retention applied (with category), marketing withdrawal; host user id (null for the job), counters of guests of the stays anonymized and files deleted. **No personal data of the guest**, no foreign key (it survives the removal of a guest) | `GuestPrivacyAuditEntry` |
| Retention | Nightly job `gdpr-data-retention` (03:00 UTC, FD-11) applies each configured category (§ 3) to every org. Idempotent: processed rows carry a marker and are not selected again | `GdprDataRetentionJob`, `GuestDataRetentionService` |

The invented 7-year retention (`Compliance:GdprRetentionYears`, `Guest.DataRetentionUntil = now + 7y` at booking,
check-in and check-out) is gone: the column `Guests.DataRetentionUntil` is dropped by the migration
`GuestPrivacyConsentsAndRetention`, the dates come from § 3.

## 2. Configuration (Railway, per environment)

No default in code or in `appsettings.json` (the keys are there, empty).

| Variable | Meaning | While empty |
|---|---|---|
| `Gdpr__PrivacyNoticeVersion` | Version of the privacy notice shown on the check-in portal (frontend keys `checkin.privacyNotice.*`) | The check-in works (legal obligation) but the notice shown is not recorded; warning `Gdpr:PrivacyNoticeVersion is not configured` at each check-in |
| `Gdpr__MarketingConsentVersion` | Version of the marketing consent text (frontend key `checkin.marketingConsent`) | The portal does not offer the marketing consent; a submit with `marketingConsent: true` answers 400 on `MarketingConsent` |
| `Gdpr__Retention__{Category}__Years` / `__Months` / `__Days` | Retention period of the category (§ 3), summed | The category is not applied |
| `Gdpr__Retention__{Category}__Source` | Source that justifies the period (law, article, written decision) | The category is not applied, even with a period |

`{Category}` is `DocumentScans`, `AlloggiatiData`, `Marketing` or `FiscalData`; `LeaseParties` is the parties of the
long-term leases (§ 7); `SupplierCustomers` is the private customers of the suppliers (§ 8). A category is applied only with at
least one amount (0 is allowed: `Days = 0` means "the day after the reference date"), no negative amount and a source.
Otherwise every run of the job logs
`GDPR retention: category {Category} not applied (...)` and the host's GDPR tab shows "Periodo non configurato".

### Publishing a text

1. The product owner provides the text (D14). Replace the placeholder in `src/i18n/locales/it.json` and `en.json`
   (`checkin.privacyNotice.body` and, for marketing, `checkin.marketingConsent`) and deploy the frontend.
2. Set the new version on Railway (`Gdpr__PrivacyNoticeVersion` / `Gdpr__MarketingConsentVersion`) at the same time.
   Every check-in from then on records the new version; the old events keep theirs.
3. Do not set `Gdpr__MarketingConsentVersion` while the marketing text is still the placeholder: a consent to a
   placeholder text is not an informed consent (art. 7).

## 3. Categories and periods

A period ends on the calendar day (Europe/Rome) **after** reference date + period. Stay-based categories use the
latest check-out of the guest's bookings (a guest with a later stay waits for it), or the creation date of a guest
without bookings.

| Category | What is removed | Reference date | Documented source | Status |
|---|---|---|---|---|
| `DocumentScans` | Scan object in the private storage and its reference | latest check-out | gdpr.md § 5: "Scansioni documenti: cancellare dopo verifica (o conservare solo se necessario per obblighi)"; § Impatto: "configurabile (suggerito: cancellazione dopo check-out + periodo sicurezza)" | **No period documented**: to be decided by the product owner |
| `AlloggiatiData` | Birth, citizenship, sex, document and scan of the booker; every guest of each stay (all kinds) after that stay's check-out | check-out of the stay / latest check-out | gdpr.md § 5: "conservazione **consigliata** per prova adempimento (es. 5 anni)" (an example, not an obligation). alloggiati.md § 4: the **Police receipt** must be kept 5 years (verified source): it is a different document, not the guest data | **Not an obligation**: to be decided |
| `Marketing` | The consent ends (`Expired` event, version of the grant) | day of the grant | gdpr.md § 5: "Dati marketing: fino a revoca consenso" | Without a period the consent lasts until withdrawal, which is what gdpr.md says; a maximum duration is a product/DPO decision |
| `FiscalData` | Whole record anonymized (as the erasure, not marked deleted) | latest check-out | gdpr.md § 5: "Dati fiscali/contabili: 10 anni (obbligo fiscale)", without the article of law | **Source to be completed** with the article before configuring |

Example, only after the product owner's decision (values and sources are not provided by this task):

```
Gdpr__Retention__FiscalData__Years=10
Gdpr__Retention__FiscalData__Source=<article of law confirmed by the accountant>
```

## 4. Guests of the stay (companions): rule per category of guest

The guests of a stay (`StayGuest`, CO-12) exist in CasaZen only as the booker's Alloggiati registration: they have no
contact data of their own and no documented obligation keeps them once the communication is done (the proof is the
Police receipt, kept by the host).

| Operation | Booker's own row | Family members | Group members |
|---|---|---|---|
| Erasure / anonymization of the booker | anonymized | anonymized | anonymized |
| Retention `AlloggiatiData` | anonymized with its stay | anonymized with its stay | anonymized with its stay |
| Export for the booker | included, `isBooker: true` | included (entered by the booker or the host for the booker's stay) | included |

Kind and position of each row stay, so a stay keeps its shape. An erasure asked by a companion is handled by erasing
the booker's stay data (there is no separate record to erase).

## 5. What stays after an erasure, and why

| Kept | Why |
|---|---|
| Guest row with `ANONYMIZED` names and `ANON-{id}@deleted.local` | Bookings and Alloggiati reports keep a foreign key to it |
| Bookings: dates, amounts, payments, Stripe references | Accounting records of the host (gdpr.md § 5, fiscal data). Special requests are cleared |
| Kind and position of the guests of the stays | Shape of the stay, no personal data |
| Consent events without IP and note (purpose, action, version, time) | Proof that consent was obtained or the notice shown (art. 7.1, accountability) |
| Alloggiati communication status and receipt reference | Proof of the legal communication; the receipt itself is kept by the host (alloggiati.md § 4) |
| Audit entries | Accountability (art. 5.2), no personal data |
| `DeletionReason` | Written by the host; the web app always sends a fixed text. Never put personal data in it |

Not removed by CasaZen: data held by Stripe for the payments (the processor's own retention), copies in database or
storage backups (they expire with the backup retention of Supabase), a scan object still referenced by another record
of the same guest (a snapshot, `Guest.CreateSnapshot`: it goes with the last reference; the log says so), a legacy
`/uploads/...` path never migrated to the storage (reference cleared, warning in the log: there is no file in the
storage to delete, see `storage.md` § 5).

## 6. Verification after deploy

1. Railway logs at 03:00 UTC: one line per category, `applied` or `not applied (...)`. While nothing is configured
   every category says `not applied`: expected.
2. Host console, guest detail, tab GDPR: consents with version, history, "Conservazione per categoria" with
   "Periodo non configurato" for each category not configured. No switch to turn the marketing consent on.
3. Check-in portal, last step: the notice with the legal obligation text and no checkbox; the marketing checkbox only
   when `Gdpr__MarketingConsentVersion` is set.
4. Erasure of a test guest with a scan: the object disappears from the private bucket
   (`guest-documents/{orgId}/{guestId}/...`), the export of the same guest shows `ANONYMIZED` and no document.
5. Database: `select "Action", "Category", "ActorUserId", "FilesDeleted" from "GuestPrivacyAuditEntries" order by "OccurredAt" desc limit 20;`

## 7. Parties of long-term leases (LT-12, #179)

Task LT-12 (audit defect A7-18). The parties of a lease (`Parties`: landlords and tenants) are anonymized **only after
the lease has ended**: a lease ends the calendar day (Europe/Rome) after its `EndDate`. The old stored
`LeaseContracts.DataRetentionUntil = StartDate + 10 years` is gone (migration `LeasePartyRetention`): a 4+4 lease with
renewals can still be in force ten years after its start. Nothing is anonymized before the end date.

| Trigger | When the parties are anonymized | Code |
|---|---|---|
| Erasure request (art. 17) | `POST /api/leases/{id}/erasure-request` (`lease.create` on the lease, 404 for another org). Recorded once (`ErasureRequested` flag and timeline event). Lease ended: now (`status: Anonymized`). Lease in force: from the day after `EndDate` (`status: Scheduled`, `anonymizationFrom`), by the nightly job. Idempotent | `LeasesController.RequestErasure`, `LeasePartyPrivacyService.RequestErasureAsync` |
| Retention | Nightly job `gdpr-data-retention` (same job as the guests): leases whose `EndDate` + `Gdpr:Retention:LeaseParties` ended. **Off until configured** with a period and its source, like the guest categories (§ 2). Without it, every run logs `GDPR retention: lease parties not applied (...)` and only the erasure requests are honoured | `LeasePartyPrivacyService.ApplyRetentionAsync` |

**Parties still involved in a lease in force.** A party whose fiscal code (trimmed, case-insensitive) is also a party,
not anonymized, of another lease **of the same org** whose end date has not passed keeps its data (typically the landlord
with other leases, or a tenant who signed a new lease). The lease is retried every night and marked anonymized
(`PartiesAnonymizedAt`) only when every party is; the erasure endpoint answers `PartiallyAnonymized` with `partiesKept`.

**What is removed**: first and last name, fiscal code (`ANONYMIZED`), citizenship (empty), e-mail
(`ANON-{partyId}@deleted.local`), and the provider signing link, its expiry and the provider signer id of the party.
Each party gets `AnonymizedAt`. The RLI reminder skips anonymized leases and parties.

**What stays**: role and extra-EU flag of each party (shape of the lease and Questura history, no identity left), dates,
amounts and status of the lease, the timeline events (no personal data), the registration code and dates, and **the stored
documents**: the signed contract (`SignedPdfStoragePath`), the RLI receipt and the Questura receipt in the private bucket
still contain the parties' data. Deleting them is not done (the RLI receipt is required by a database constraint of a
`Registered` lease, and the landlord may need the signed contract for its own obligations): decision of the product
owner, see the open question of LT-12.

Configuration (Railway, per environment), only after the decision of the product owner and the DPO/accountant:

```
Gdpr__Retention__LeaseParties__Years=<years after the end of the lease>
Gdpr__Retention__LeaseParties__Source=<article of law or written decision>
```

Verification after deploy: Railway logs at 03:00 UTC show `GDPR retention: lease parties not applied (...)` (or
`applied`) and `GDPR lease party retention done`. Database:
`select "Id", "EndDate", "ErasureRequested", "PartiesAnonymizedAt" from "LeaseContracts" where "ErasureRequested" or "PartiesAnonymizedAt" is not null;`

## 8. Private customers of the suppliers (SP-10)

A customer who books a supplier from its public showcase has **no account** and is not a guest of a stay: it is a `ServiceCustomer` of
**one supplier** (the supplier is the controller of the work it does for the customer; CasaZen provides the booking and holds the data
for it). What is kept, where and how long (the flow is in [`suppliers.md`](suppliers.md) § 23):

| Data | Where | Protection |
|---|---|---|
| name, e-mail address, phone | `ServiceCustomers` (and, while the e-mail is not checked, in the payload of `ShowcaseBookingHolds`) | encrypted at rest (`Casazen.ServiceCustomer`), found by an HMAC of the address, shown to the supplier as "Nome C." until it takes the request and in full only after (D9) |
| street address, floor, access notes of the work | `ServiceRequests` | encrypted at rest (`Casazen.ServiceRequest.Location`), shown to the supplier only after the take |
| comune and postal code | `ServiceRequests` | not encrypted; the supplier sees them before the take |
| consent: version of the privacy notice, time, client address | `ServiceCustomers` | the booking is refused without it, or with a version that is not the current one (`Suppliers__Showcase__PrivacyNoticeVersion`); the text of the notice is a decision of the product owner and legal (D14) |
| language | `ServiceCustomers` | `it` or `en`, the language of every e-mail to the customer |

An unchecked booking is **deleted by the upkeep job** when its 30 minutes have passed (the data go with it); a checked one is deleted
when it would have expired, and the request and the customer remain. The logs carry ids and codes only.

| Trigger | When the customer is anonymized | Code |
|---|---|---|
| Retention | Nightly job `gdpr-data-retention`: a customer **none of whose requests is open** (`Richiesto`, `PresoInCarico`, `InCorso`) and whose last request ended more than `Gdpr:Retention:SupplierCustomers` ago (the end of the work; its creation when it had no hours; the customer's own creation when it has no request). **Off until configured** with a period and its source, like the other categories (§ 2): without them every run logs `GDPR retention: supplier customers not applied (...)` and nothing is anonymized | `ServiceCustomerPrivacyService.ApplyRetentionAsync` |

**What is removed**: the name (`ANONYMIZED`), the e-mail (`ANON-{customerId}@deleted.local`), the phone and the client address of the
consent, and the e-mail index (replaced, so the old address is never found again and a new booking of the same person makes a new
customer); of every request of the customer that is over, the street address, the floor and the access notes. **What stays**: the
language, the version and time of the consent (the proof that it was given), the comune and the postal code, the request itself (status,
times, price, the supplier's accounts) and the customer row with its `AnonymizedAt`. The reminder skips an anonymized customer. The
period and its source are **not decided here**: the product owner, the DPO and the accountant decide (the supplier may need the
request for its own accounts).

**Not done in SP-10** (decisions for later): an erasure request of the customer (art. 17), the export of its data (art. 15, 20) and the
withdrawal of the consent. They need the customer's own area (SP-11) or an operator procedure; until then a request goes to the supplier
(the controller) or to CasaZen support, who can anonymize the customer with `ServiceCustomerPrivacyService.Anonymize` (code change, no endpoint).

Configuration (Railway, per environment), only after the decision of the product owner and the DPO/accountant:

```
Gdpr__Retention__SupplierCustomers__Years=<years after the end of the last request>
Gdpr__Retention__SupplierCustomers__Source=<article of law or written decision>
```

Verification after deploy: the Railway log at 03:00 UTC shows `GDPR retention: supplier customers not applied (...)` (or `applied`) and
`GDPR supplier customer retention done`. Database:
`select "Id", "AnonymizedAt", "CreatedAt" from "ServiceCustomers" where "AnonymizedAt" is not null;` and
`select count(*) from "ServiceCustomers" where "FullName" not like 'CfDJ8%';` (expected 0: every name is a payload).
