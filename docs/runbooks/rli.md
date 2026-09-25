# Runbook: RLI registration of lease contracts

Task **LT-01** (defects A7-01 and A7-21), decision **D15**, research **RS-4** (`docs/integrations/rli-esign.md`).
Registration deadline and reminders: task **LT-04** (defect A7-04), rule verified by **RS-5**. Contract signature
(offline by default, provider off): task **LT-02**, see [Contract signature](#contract-signature-lt-02).

Until LT-01 the "registration" was a stub: `POST /api/leases/{id}/registration` returned `RLI-STUB-{id}` without
calling anyone, the lease went to `SentToProvider`, the checklist ticked "RLI sent", the toast said "Registration
submitted successfully" and the receipt was the text `[RECEIPT PLACEHOLDER]` served as a PDF. The landlord believed the
contract was registered and risked the late-registration penalty.

Now the registration is **manual by default** and nothing is shown as registered until the landlord records the
registration details and the official receipt.

## States the landlord sees

| State (UI) | Lease status | `LeaseRegistration.Status` | Meaning |
|---|---|---|---|
| Not ready to register | `Draft`, `AwaitingSignature`, `PartiallySigned` | none | The contract is not signed by every party yet |
| **To register** | `Signed` | none | The landlord must register the contract (steps, deadline, "Enter registration details") |
| **Registration in progress** | `RegistrationPending`, `SentToProvider` | `Pending`, `SentToProvider` | Provider path only: the provider is working on it, **not registered yet** |
| **Registered** | `Registered` | `Registered` | Number or protocol, date and receipt recorded; receipt downloadable |
| **Registration failed** | `Signed` | `Failed` (+ `FailureCode`) | The last provider attempt failed: **not registered**; retry (provider) or register manually |

The checklist item `rli_registered` is ticked only with the lease `Registered`, the registration `Registered` and its
receipt stored; a `Failed` registration shows the item as failed with a link to the registration panel. The old item
`rli_submitted` ("RLI sent to the filing channel") is gone. `delega_captured` is listed only while the provider path is
available (or once a delega was given).

The deadline shown is computed by the API (task LT-04, see [Registration deadline](#registration-deadline-lt-04)):
`min(stipula, start date) + 30` days, or "to be determined".

## Manual registration (default path)

1. The landlord registers the contract on an official channel of the Agenzia delle Entrate (`rli-esign.md` §2.1):
   RLI web service in the reserved area (SPID, CIE, CNS or Entratel/Fisconline credentials), the RLI software with
   filing through Entratel/Fisconline, an authorised intermediary (accountant, CAF, trade association) or an office of
   the Agenzia. CasaZen is not an authorised intermediary (`rli-esign.md` §2.2) and does not file for them.
2. In the lease page, "Enter registration details": registration number or protocol, registration date and the
   receipt PDF, plus the explicit declaration checkbox.
3. `POST /api/leases/{id}/registration/manual` (multipart: `registrationCode`, `registrationDate`, `receipt`), policy
   `lease.register` on the lease (TN-3: the owner, or an org-wide member with `lease.register`):
   - `registrationCode`: 1-100 characters, trimmed, no format check (no official format was verified);
   - `registrationDate`: not later than today in Europe/Rome;
   - `receipt`: checked on its content (`%PDF-` signature), at most 10 MB;
   - the lease must be `Signed` with no registration in progress or done (409 `rli_registration_in_progress` /
     `rli_already_registered`, 422 `rli_lease_not_signed`).
4. The receipt is stored in the **private bucket** (FD-07) under
   `leases/{orgId}/{leaseId}/registration/{random}.pdf`, then one transaction records the registration (`Channel =
   Manual`, code, date, receipt key, `DeclaredByUserId`), the lease `Registered` and the `RegistrationConfirmed` event
   (payload `manual`). If the transaction fails, the uploaded receipt is deleted.
5. `GET /api/leases/{id}/registration/receipt` streams the receipt from the private bucket, only to a caller who may
   read the lease (tenant filter + TN-3), with `Cache-Control: private, no-store`. 404 `rli_receipt_not_available`
   before the registration.

## Provider path (off)

Behind the feature flag **`Features:RliProvider`** (default `false`, see `docs/runbooks/feature-flags.md`) **and** a
configured provider (`ILeaseRegistrationProvider.IsConfigured`). Both are checked by `RliProviderFiling.IsAvailable`.

| Situation | `POST /api/leases/{id}/registration` | Polling job `lease-registration-status-poll` | UI |
|---|---|---|---|
| Flag off (default) | 404 (before authentication) | not registered, removed with `RemoveIfExists` | manual only |
| Flag on, provider not configured | 409 `rli_provider_unavailable`, nothing recorded | registered, does nothing | manual only (`providerFilingAvailable: false`) |
| Flag on, provider configured | 202: filing **in progress** | every 5 minutes | "Send through the provider (with delega)" next to the manual path |

**There is no real provider client yet.** The registered provider is `UnconfiguredLeaseRegistrationProvider`
(`IsConfigured = false`, every call refuses), so the provider path stays unavailable even with the flag on. RS-4 found
the Openapi DocuEngine API publicly documented, but the lease-registration service id and its input fields are **not in
the public specification** (`rli-esign.md` §1.3, §1.7): writing the mapping without them would invent the payload.

### What is needed before writing the Openapi client

From `rli-esign.md` §1.7 and §5, with a sandbox account:

1. `GET https://test.docuengine.openapi.com/documents`: id of the "lease contract registration" service,
   `requestStructure.fields`, validation, options, `isSync`/`hasSearch`, and that it exists in the sandbox.
2. The "Condizioni Specifiche" of the service: who files (the provider's professional), which mandate or delega the
   landlord must give, TULPS identity check.
3. When the wallet is charged (request `NEW` or `SEARCH`) and whether the receipt carries the registration number.
4. Accepted contract format (PDF/A or TIFF for the Agenzia).
5. Legal opinion on the provider's professional and the delega text (`Rli:AttestationText` is still a draft), and
   approval of the cost per filing (about 12.90 EUR + VAT).

### How to plug a real client

1. Implement `ILeaseRegistrationProvider` (mapping in `rli-esign.md` §1.8): `SubmitAsync` → `POST /requests`
   (402 credit and every error as `LeaseRegistrationProviderException`, never as success); `GetStatusAsync` →
   `GET /requests/{id}` (`WAIT` in progress, `DONE` registered, `CANCELLED` failed with a stable code);
   `DownloadReceiptAsync` → `GET /requests/{id}/documents` (the URL expires: the service copies the PDF into the private
   bucket before marking the lease Registered). `IsConfigured` true only with every setting present and no placeholder.
2. Register it instead of `UnconfiguredLeaseRegistrationProvider` in `ServiceCollectionExtensions.AddCasazenServices`.
3. Settings only as Railway variables (`rli-esign.md` §1.9): `Openapi__BaseUrl`, `Openapi__Token` (or
   `Openapi__Email` + `Openapi__ApiKey`), `Openapi__LeaseDocumentId`, and for a callback `Openapi__CallbackUrl` +
   `Openapi__CallbackSecret` (the callback is not signed: check the secret header, then read the state again with
   `GET /requests/{id}`, work in a background job).
4. Test against the sandbox, then `Features__RliProvider=true` on Railway **test** only.

### Atomicity and failures (A7-21)

`RliRegistrationService` writes each state change in one transaction under a row lock on the lease
(`SELECT … FOR UPDATE`); the provider is called between two transactions, never inside one:

1. reserve: delega (`LeaseRegistrationAuthorizations`) + registration `Pending` (`RequestedAt`) + lease
   `RegistrationPending` + event `RegistrationAuthorized`;
2. provider call;
3. success: registration `SentToProvider` + lease `SentToProvider` + event `RegistrationSubmitted`;
   failure: registration `Failed` (`FailureCode`) + lease back to `Signed` + event `RegistrationFailed`, response 502
   `rli_provider_failed`. A retry reuses the same row (one registration per lease); the manual path is available too.

Concurrent requests on the same lease run one after the other: the second one sees the registration in progress (409).
A reservation left `Pending` by a crash between 1 and 3 is failed by the polling job after 15 minutes with
`provider_outcome_unknown`: the provider may have received it, so the UI asks the landlord to check before filing again.

Failure codes (`LeaseRegistration.FailureCode`, translated by the frontend): `provider_error`, `provider_rejected`,
`provider_outcome_unknown`, `simulated_submission` (see below).

### Before turning the flag off again

Registrations still in progress are not polled any more and block the manual path (409). Check first:

```sql
SELECT r."LeaseContractId", r."Status", r."RequestedAt", r."SubmittedAt"
FROM "LeaseRegistrations" r
WHERE r."Channel" = 0 AND r."Status" IN (0, 1);
```

Wait for them to finish, or agree with the landlord and the provider how to close them before switching off.

## Data migration `RliHonestRegistration`

Applied automatically at startup with the other EF migrations:

- stub submissions (`ExternalRegistrationId` starting with `RLI-STUB-`) → `Failed` with `simulated_submission`;
- reservations left `Pending` and `Registered` rows without a receipt → `Failed` with `provider_outcome_unknown`;
- a `RegistrationFailed` event for each of them;
- leases `RegistrationPending` / `SentToProvider` / `Registered` without a live or registered registration → `Signed`
  (the landlord sees "Registration failed" with the reason and registers manually);
- check constraint `CK_LeaseRegistrations_RegisteredRequiresReceipt`: `Registered` only with a stored receipt.

Check after the deploy (test, then production):

```sql
SELECT "Status", "FailureCode", count(*) FROM "LeaseRegistrations" GROUP BY 1, 2;
SELECT count(*) FROM "LeaseRegistrations" WHERE "ExternalRegistrationId" LIKE 'RLI-STUB-%' AND "Status" <> 3; -- expected 0
```

On the **test** environment the landlords who used the old "Authorize and submit" now see their lease as "Registration
failed — the previous submission was only simulated": they must register the contract themselves.

## Contract signature (LT-02)

Task **LT-02** (defects A7-02 P0, A7-16, A7-20), decision **D15**, research **RS-4** (`rli-esign.md` §3, §4).

Until LT-02 the signature was a stub (`LeaseESignHttpAdapter`): "Avvia firma" sent the PDF to nobody, moved the lease
to `AwaitingSignature` with links to the non-existent `sign.provider.example.com` and the lease never reached `Signed`
(the polling job only logged). The links lived only in the page state and were lost at every refresh. The e-sign
webhook had no status guard (an `allSigned` event moved a `Registered` lease back to `Signed`), never set
`PartiallySigned`, and its committed secret was the public string `PLACEHOLDER_SET_IN_ENV`.

Now the signature is **offline by default** and nothing is shown as signed until CasaZen records the signature of
every party. A lease signed electronically needs at least the FEA (CAD art. 20; FEQ over 9 years), which no provider
offers self-serve: the provider path exists in the code but stays **off, with no real client** (`rli-esign.md` §4).

### Offline signature (default path)

1. `GET /api/leases/{id}/contract.pdf`, policy `lease.sign` (the PDF carries the full fiscal codes): the **final**
   contract, only from a complete, lawyer-approved template with every datum known (LT-03). Otherwise 422
   `contract_template_not_approved` / `contract_data_missing` and the UI offers only the preview
   `GET /api/leases/{id}/contract/preview`, marked BOZZA and not valid for signature. 409 `lease_already_signed` once
   every party signed. The term of the contract type (LT-10: libero at least 4 years, concordato at least 3,
   transitorio 1-18 months; 422 `lease_term_too_short` / `lease_term_too_long`) and the APE are checked as for the
   provider path.
2. The parties sign **outside CasaZen**: by hand on paper, or each with their own digital signature or FEA. CasaZen does
   not verify the signatures of the uploaded file (the UI says so).
3. `POST /api/leases/{id}/signed-document` (multipart: `signedContract`, `stipulaDate`), policy `lease.sign` on the
   lease (TN-3: the owner, or an org-wide member with `lease.sign`):
   - `signedContract`: checked on its content (`%PDF-` signature, the declared type is ignored), at most 20 MB
     (422 `lease_signed_contract_invalid`);
   - `stipulaDate`: the day the last party signed, not later than today in Europe/Rome (422 `lease_stipula_date_in_future`);
   - the lease must be `Draft`, `AwaitingSignature` or `PartiallySigned` (409 `lease_already_signed`), with the
     template gate of step 1 (422).
4. The file is stored in the **private bucket** (FD-07) under `leases/{orgId}/{leaseId}/signed-contract/{random}.pdf`,
   then one transaction under a row lock on the lease records `LeaseContract.RecordStipula(stipulaDate)` (LT-04: the RLI
   deadline becomes `min(stipula, start) + 30`), `StipulaDeclaredByUserId`, a `LeaseSigner` per party (`Offline`,
   `Signed`, `SignedAt` = stipula), the lease `Signed` and the event `AllPartiesSigned` (payload `offline`). If the
   transaction fails, the uploaded file is deleted.
5. `GET /api/leases/{id}/signed-document` streams it from the private bucket, only to a caller who may read the lease
   (tenant filter + TN-3), with `Cache-Control: private, no-store`. 404 `lease_signed_contract_not_available` before.
6. The RLI registration (manual path above, LT-01) starts once the lease is `Signed`.

### Signers (A7-16)

Table `LeaseSigners` (tenant-owned, one row per lease and party): method `Offline` / `Provider`, status `Pending` /
`Signed`, provider signer id, personal link and its expiry, `SignedAt`. No personal data: the party is referenced by id.

`GET /api/leases/{id}/signers` (policy `lease.read`) returns the signature panel:

```json
{ "providerSigningAvailable": false, "contractAvailable": true, "contractUnavailableCode": null,
  "signers": [ { "partyId": "…", "role": "Landlord", "firstName": "…", "lastName": "…", "method": "Offline",
                 "status": "Pending", "signingUrl": null, "signingUrlExpiresAt": null, "signingUrlExpired": false,
                 "signedAt": null } ] }
```

- Without a row, a party is `Offline`/`Pending` before the signature and `Signed` (with the stipula date) after it.
- The provider link is returned only while pending and only to a caller with `lease.sign`; `signingUrlExpired` is
  computed on read, so the UI shows "Link scaduto".
- `contractUnavailableCode`: `contract_template_not_approved`, `contract_data_missing` or `lease_already_signed`.

### Leases signed before LT-02

- **Signed without a stipula date** (deadline "to be determined", no reminders, see LT-04): the landlord uses "Dichiara
  data di stipula" in the lease page, `POST /api/leases/{id}/stipula` `{ "stipulaDate": "YYYY-MM-DD" }`, policy
  `lease.sign`. Only once (409 `lease_stipula_already_recorded`), only for a lease signed by every party (422
  `lease_stipula_lease_not_signed`: use `signed-document`), not after today. It records the stipula and the deadline,
  `StipulaDeclaredByUserId` and the event `StipulaDeclared`.
- **Stuck in `AwaitingSignature` by the old stub** (`ExternalSigningSessionId` = `stub-session-…`, no signer rows):
  the offline signature accepts them, the panel shows the offline path. No data migration.
- **`SignedPdfStoragePath` written by the old stub** (`/signed/…`, not a storage key): not a file CasaZen holds, so
  `hasSignedPdf` is false and the download answers 404. The UI says the signed contract was not uploaded.

### Provider path (off)

Behind the feature flag **`Features:ESignProvider`** (default `false`, `docs/runbooks/feature-flags.md`) **and** a
configured provider (`ILeaseESignService.IsConfigured`), both checked by `ESignProviderSigning.IsAvailable`.

| Situation | `POST /api/leases/{id}/signing` | `POST /webhooks/esign` | Job `lease-sign-status-poll` | UI |
|---|---|---|---|---|
| Flag off (default) | 404 | 404, nothing read or queued | not registered, removed with `RemoveIfExists` | offline only |
| Flag on, provider not configured (today) | 409 `esign_provider_unavailable`, nothing recorded | HMAC checked; the job drops the event | registered, only logs | offline only (`providerSigningAvailable: false`) |
| Flag on, provider configured | 200: links persisted, lease `AwaitingSignature`, event `SigningInitiated` | events applied (below) | logs the leases waiting for the provider | "Invia per firma elettronica", links with expiry, offline as alternative |

**There is no real provider client yet**: the registered provider is `UnconfiguredLeaseESignService`
(`IsConfigured = false`, every call refuses). A provider failure answers 502 `esign_provider_failed` and records nothing.

Webhook (A7-20):

- The body must be signed with `ESign:WebhookSecret` (HMAC-SHA256 of the raw body, hex in `X-ESign-Signature`):
  401 `invalid_signature` otherwise. **With the flag on the secret is required at startup**: missing, containing
  `PLACEHOLDER` or shorter than 16 characters, the service does not start (`ESignOptionsValidator`, message with this
  runbook). The committed value is empty.
- `ESignWebhookJob` applies an event only to a lease `AwaitingSignature` or `PartiallySigned`: a replayed, late or
  forged-but-signed "all signed" never moves a `Signed` or `Registered` lease back.
  - `signer_signed`: that signer `Signed`, the lease `PartiallySigned`, event `PartySignedDocument` (payload: party id,
    never an email). A replay changes nothing (the signer is already signed).
  - `all_signed`: the signed PDF is downloaded from the provider and copied to the private bucket, then the lease is
    `Signed`, the stipula is the Rome day on which CasaZen processes the event (LT-04) and the event `AllPartiesSigned`
    (payload `provider`) is written. Without a valid PDF nothing changes and the job fails (Hangfire retries it).
- Idempotency is by state (signer or lease already signed), not by provider event id: the provider's event ids are
  unknown until a client is chosen.

### How to plug a real client (phase 2)

Prerequisites (`rli-esign.md` §4, §5): budget for a plan with AES (Yousign/Youtrust: annual plan with add-on), legal
opinion on the FEA obligations (DPCM 22/02/2013 art. 57), QES for contracts over 9 years.

1. Implement `ILeaseESignService`: `IsConfigured` true only with `BaseUrl`, `ApiKey` and `WebhookSecret`;
   `InitiateSigningAsync` returns the provider session and one link per party with its expiry (never a guessed URL);
   `ParseWebhookEventAsync` maps the provider events to `SignerSigned` / `AllSigned` / `Other`;
   `DownloadSignedDocumentAsync` returns the signed PDF.
2. Adapt the signature check of `POST /webhooks/esign` to the provider header (Yousign: `X-Yousign-Signature-256:
   sha256=<hex>`; today `X-ESign-Signature: <hex>`).
3. Register it in `LeaseSigningExtensions.AddCasazenLeaseSigning` instead of `UnconfiguredLeaseESignService`. The
   expected behaviour is in `LeaseSigningProviderIntegrationTests` (fake provider).
4. Railway **test** first: `ESign__BaseUrl` (sandbox), `ESign__ApiKey`, `ESign__WebhookSecret`, then
   `Features__ESignProvider=true`.

### Data migration `AddLeaseSigners`

Adds the table `LeaseSigners` and the column `LeaseContracts.StipulaDeclaredByUserId`; no existing row changes.
Checks after the deploy (test, then production; replace the schema):

```sql
-- Leases left waiting by the old stub: to be signed offline by the landlord
SELECT "Id", "Status", "UpdatedAt" FROM casazen_prod."LeaseContracts"
WHERE "Status" IN (1, 2) AND "ExternalSigningSessionId" LIKE 'stub-session-%';

-- Signed leases whose "signed PDF" is a path of the old stub, not a stored file
SELECT "Id", "Status" FROM casazen_prod."LeaseContracts" WHERE "SignedPdfStoragePath" LIKE '/%';
```

## Registration deadline (LT-04)

Task **LT-04** (defect A7-04, P0). Until LT-04 the deadline was `StartDate + 30`: a contract signed on 1/8 starting on
1/10 showed "deadline 31/10" and the reminders followed that date, while the legal deadline was 31/8. The job also
reminded only on the exact days 15/7/1 (a skipped run lost the reminder), only for `Signed` / `RegistrationPending` /
`SentToProvider`, and sent "overdue" already on the deadline day.

### The rule

Verified by RS-5 (`.claude/context/regulations/fiscale.md`, rule **L1**, Agenzia delle Entrate: "Registrazione di un
nuovo contratto" and "Atti e contratti di locazione"): registration "entro 30 giorni dalla data di stipula o dalla
data di decorrenza, se anteriore", so

`RegistrationDeadline = min(StipulaDate, StartDate) + 30 days`

- Single implementation: `Casazen.Core/Regulatory/RliRegistrationDeadline.cs`. Calendar days on the Europe/Rome
  calendar; dates stored as midnight UTC of the Rome date (FD-06). Example: signed 1/8, start 1/10 → **31/8**; start
  1/9, signed 20/9 → **1/10**.
- **No shift for Saturdays or public holidays**: the verified sources do not say whether a deadline falling on a
  non-working day moves to the next working day, so CasaZen shows the base date (the earlier, prudent one). Open point
  for the product owner / accountant before changing it.

### The stipula date

`LeaseContract.StipulaDate` is the Europe/Rome day on which every party had signed. It is recorded only by
`LeaseContract.RecordStipula(signedAt)`, which also fixes `RegistrationDeadline`:

- offline signature (LT-02, default): the date the landlord declares with the signed PDF (`POST signed-document`);
- declared stipula of a lease signed before LT-02 (`POST stipula`), once;
- e-sign provider (off): the `all_signed` webhook (`LeaseSigningService.HandleProviderEventAsync`). The provider sends
  no signing time, so the stipula is the day CasaZen processes the event, the same instant as the `AllPartiesSigned`
  event.

### What the API shows

`GET /api/leases`, `GET /api/leases/{id}` (`stipulaDate`, `registrationDeadline`) and `GET /api/leases/{id}/rli/checklist`
(`registrationDeadline`, `daysRemaining`: 0 on the deadline day, negative after it) resolve the deadline on read:

| Lease | Deadline |
|---|---|
| Stipula recorded | `min(stipula, start) + 30` (stored in `RegistrationDeadline`) |
| Not signed by every party yet (`Draft`, `AwaitingSignature`, `PartiallySigned`), start date reached | `start + 30`: the stipula can only come today or later, so the start date is the earlier one |
| Not signed yet, start date ahead | `null` = **to be determined** (at least 30 days away, depends on the signing day) |
| Signed but no stipula recorded (older leases without a signing event) | `null` = **to be determined**, never guessed |

The frontend shows "Da determinare" / "To be determined" and the rule; the RLI prefill PDF prints "Data di stipula" and
"Scadenza registrazione: da determinare".

### Reminders (`rli-deadline-reminder`, daily 08:00 UTC)

`RliDeadlineReminderJob` runs on every lease not registered yet in any status before registration (`Draft` included;
`Registered` and `Rejected` excluded) that has a deadline as above. It reasons on **thresholds**, not exact days:

| Days to the deadline (Rome calendar) | Threshold sent |
|---|---|
| more than 15 | none |
| 15 … 8 | `t-15` |
| 7 … 2 | `t-7` |
| 1 and 0 (the deadline day is **not** overdue) | `t-1` |
| from −1 (the day after the deadline) | `overdue` |

- Only the most urgent threshold reached today is sent, once per deadline: after a skipped run the next run sends it;
  a first run 3 days before sends `t-7` only, not `t-15` too; two runs on the same day send once.
- Each sent threshold is a `DeadlineReminderSent` lease event with payload `{threshold}:{deadline}` (e.g.
  `t-7:2026-08-31`), written **only when the email was accepted**: a failed send is retried at the next run. A
  deadline that changes (an earlier signing date declared later) starts its own thresholds.
- Email to the landlord party (`IEmailService`, templates `RliDeadlineReminder` / `RliDeadlineOverdue`, IT/EN in
  `EmailTexts*.resx`); for a lease not signed yet the email adds that the deadline counts from the start date.
- A signed lease without a stipula date gets no reminder and logs a warning (`No RLI reminder for LeaseId=…`).
- The Questura communication of an extra-EU tenant has its own reminders in the same job: see
  [Questura communication (LT-07)](#questura-communication-for-extra-eu-tenants-lt-07).

### Data migration `AddLeaseStipulaDate`

Applied at startup with the other EF migrations:

1. `StipulaDate` = Europe/Rome date of the first `AllPartiesSigned` event of each lease; none → `null`.
2. Reminders already sent by the old job (payload `t-15`, `t-7`, `t-1`, `overdue`) get the deadline they were sent for
   (`t-15:2026-10-31`): not repeated when the corrected deadline is the same date, sent again for the corrected one when
   it differs. So after the deploy a landlord whose corrected deadline has already passed receives one "overdue" email:
   it is the intended correction, the contract had to be registered by the earlier date.
3. `RegistrationDeadline` = `min(stipula, start) + 30` days where the stipula is known, otherwise `null` (the old
   `StartDate + 30` of unsigned leases is removed: the API resolves it on read).

Checks after the deploy (test, then production; replace the schema):

```sql
-- Signed leases without a stipula date (deadline "to be determined", no reminders): expected 0
SELECT l."Id", l."Status", l."StartDate"
FROM casazen_prod."LeaseContracts" l
WHERE l."Status" IN (3, 4, 5, 6) AND l."StipulaDate" IS NULL;

-- Deadlines already passed for leases not registered yet
SELECT l."Id", l."Status", l."StipulaDate", l."StartDate", l."RegistrationDeadline"
FROM casazen_prod."LeaseContracts" l
WHERE l."Status" NOT IN (6, 7) AND l."RegistrationDeadline" < now()
ORDER BY l."RegistrationDeadline";

-- Reminders sent in the last 7 days, per threshold
SELECT split_part("Payload", ':', 1) AS threshold, count(*)
FROM casazen_prod."LeaseEvents"
WHERE "EventType" = 12 AND "OccurredAt" > now() - interval '7 days'
GROUP BY 1;
```

A signed lease without a stipula date needs its signing date from the landlord: the lease page offers "Dichiara data di
stipula" (`POST /api/leases/{id}/stipula`, LT-02), which records it once together with the deadline.

## Questura communication for extra-EU tenants (LT-07)

Task **LT-07** (defect A7-08), rule verified by **RS-5** (`.claude/context/regulations/fiscale.md` L13-L15,
`canone_concordato.md` "Adempimenti separati"). Before LT-07 the checklist item "Comunicazione Questura (art. 7 D.Lgs
286/1998)" was ticked as soon as CasaZen **sent a reminder email** (event `DeadlineReminderSent`, payload `extra-eu`),
sent once on any day, even months before the delivery: the landlord could believe the communication was done.

### The rule

- Whoever lets a property, in any form, to a foreign citizen (not EU) or a stateless person must notify the **local
  public-security authority** in writing **within 48 hours of the delivery of the property** (art. 7 D.Lgs. 286/1998;
  L13, Polizia di Stato, class U). The competent office and the channel (in person, PEC, registered mail) change from
  province to province: the landlord checks the page of the local Questura.
- The RLI registration **does not replace it** (L14): registered leases keep the item and the reminders.
- Sanction: 160 to 1.100 € (art. 7 c. 2-bis; L15, Polizia di Stato). The range 103-1.549 € cited by the audit is the
  one of art. 12 D.L. 59/1978, not of art. 7: it is not shown anywhere.
- CasaZen **does not send** the communication.

### Who is extra-EU (choice documented)

The lease form already asks each party's **citizenship as a 2-letter ISO 3166-1 code** (`Party.Citizenship`, now
checked as two letters: 400 `LeasePartyCitizenshipInvalid`). `Party.IsExtraEU` is set at creation by comparing it with
the explicit list of the **27 EU member states** in `Casazen.Core/Regulatory/EuMemberStates.cs` (source: European Union,
"EU countries", checked 2026-09-25; Greece is `GR`). No list of all countries is kept. Any code outside the 27 counts as
extra-EU, including EEA and Swiss citizens (`NO`, `IS`, `LI`, `CH`) and a code used for a stateless person: the item is
then shown (conservative; open point below). Only **tenants** count, not the landlord.

### Delivery date and deadline

- `LeaseContract.PropertyDeliveryDate` (new, nullable). Without it the **start date** is the delivery date (documented
  default: the property is usually delivered on the first day of the lease).
- The landlord sets or clears it from the Questura panel of the lease page:
  `PUT /api/leases/{id}/rli/questura/delivery-date` `{ "deliveryDate": "2026-09-28" | null }` (`lease.register`; 422
  `questura_delivery_date_after_end` after the end of the lease; event `PropertyDeliveryDateDeclared`).
- CasaZen knows the delivery **day**, not the hour: the 48 hours end at the latest on **delivery + 2 calendar days**
  (Europe/Rome), shown as the deadline (`QuesturaCommunicationDeadline`). From the day after, the communication is
  overdue.

### Declaring the communication (the only way to tick the item)

`POST /api/leases/{id}/rli/questura/mark-done`, multipart: `communicationDate` (calendar date, not after today) and an
optional `receipt` (PDF checked on its content, at most 10 MB, stored in the **private bucket** under
`leases/{orgId}/{leaseId}/questura/`, FD-07). Needs `lease.register` on the lease (TN-3); another org's lease answers 404.

| Answer | When |
|---|---|
| 200 + updated checklist | Declared: `QuesturaCommunicationDate`, receipt key and declaring user stored, event `QuesturaCommunicationMarkedDone` |
| 422 `questura_not_required` | No tenant is extra-EU (or the lease is rejected) |
| 422 `questura_communication_date_in_future` | Date after today (Europe/Rome) |
| 422 `questura_receipt_invalid` | Receipt not a PDF or over 10 MB |
| 409 `questura_already_marked_done` | Already declared (one declaration per lease, row lock) |

The receipt is served only by `GET /api/leases/{id}/rli/questura/receipt` (`lease.read`, 404
`questura_receipt_not_available` without one). The checklist (`GET /rli/checklist`) returns the item
`questura_extra_eu` only for an extra-EU tenant, `done` only after the declaration, and a `questura` block:
`deliveryDate`, `deliveryDateDeclared`, `deadline`, `daysRemaining`, `communicationDate`, `hasReceipt`.

### Reminders (same job `rli-deadline-reminder`, daily 08:00 UTC)

For every lease with an extra-EU tenant, in any status except `Rejected` (**registered leases included**), not ended
yet (`EndDate` ≥ today) and not declared. Same threshold logic as the RLI deadline: only the most urgent threshold
reached today, once per deadline, recorded as `DeadlineReminderSent` with payload `{threshold}:{deadline}` only when the
email was accepted; a changed delivery date starts its own thresholds. Thresholds are a product choice:

| Days to the deadline (delivery + 2) | Threshold | Meaning |
|---|---|---|
| more than 5 | none | |
| 5 … 3 | `questura-before-delivery` | delivery in 3 … 1 days |
| 2 … 1 | `questura-delivery` | delivery day and the day after |
| 0 | `questura-deadline` | the 48 hours end |
| from −1 | `questura-overdue` | not declared after the deadline (once) |

Emails `QuesturaCommunicationReminder` / `QuesturaCommunicationOverdue` (IT/EN in `EmailTexts*.resx`) to the landlord
party: property, delivery date, deadline, the default delivery date when none was declared, "CasaZen does not send it",
how to mark it done. A reminder **never** ticks the item.

### After the deploy (migration `AddLeaseQuesturaCommunication`)

Four nullable columns on `LeaseContracts`, no data change. Consequences:

1. Leases with an extra-EU tenant lose the false ✓: the old `extra-eu` events stay in the timeline as history and are
   no longer read.
2. Active leases (not ended) whose default deadline (start + 2 days) has already passed get **one** `questura-overdue`
   email at the next run: intended, CasaZen has no declaration. The landlord declares it (with the real date) and the
   reminders stop.

```sql
-- Active leases with an extra-EU tenant and no declared Questura communication
SELECT l."Id", l."Status", l."StartDate", l."PropertyDeliveryDate"
FROM casazen_prod."LeaseContracts" l
WHERE l."Status" <> 7 AND l."QuesturaCommunicationDate" IS NULL AND l."EndDate" >= now()
  AND EXISTS (SELECT 1 FROM casazen_prod."Parties" p
              WHERE p."LeaseContractId" = l."Id" AND p."Role" = 1 AND p."IsExtraEU");

-- Questura reminders sent in the last 7 days, per threshold
SELECT split_part("Payload", ':', 1) AS threshold, count(*)
FROM casazen_prod."LeaseEvents"
WHERE "EventType" = 12 AND "Payload" LIKE 'questura-%' AND "OccurredAt" > now() - interval '7 days'
GROUP BY 1;
```

### Open points (product owner / counsel)

- EEA and Swiss citizens counted as extra-EU (the TUI defines "stranieri" as non-EU citizens; whether free-movement
  rules exempt them is not in the verified sources).
- No "undo" or correction of a declaration (409 on a second one): a wrong date needs a support intervention.
- The hour of delivery is not recorded: the deadline is the calendar day, the texts say "entro 48 ore".

### Tests

`QuesturaCommunicationDeadlineTests` (EU list, delivery default, deadline, Rome calendar), `RliChecklistServiceTests`
(item not ticked after the reminder emails, ticked after the declaration, rejected lease),
`RliDeadlineReminderJobTests` (thresholds once each, first run late → only overdue, registered lease still reminded,
declared delivery date, declared communication, ended lease, EU tenant), `LeaseQuesturaCommunicationIntegrationTests`
(PostgreSQL, fixed clock: EU tenant → no item, extra-EU → not ticked after the email, mark-done with and without
receipt, 422/409, delivery date, other org 404, 403/401). Frontend: `questura-communication-panel.test.tsx`.

## Tax advisory (LT-08)

Task **LT-08** (defect A7-09). The panel "Cedolare secca o regime ordinario" on the lease page compared the options with
wrong numbers: the 10% cedolare on every canone concordato lease, the registration tax at 2% of the annual rent with
neither the 70% base nor the 67 € minimum, a flat 16 € stamp duty, and no IRPEF. Example of the audit: concordato in
Seveso at 800 €/month showed cedolare 10% = 960 € although the ATA listing of Seveso is not verified (21% = 2.016 €).

### Endpoints

- `GET /api/leases/{id}/rli/advisory`: the advisory with the data CasaZen holds.
- `POST /api/leases/{id}/rli/advisory` with `{ writtenPages, lines, copies, otherTaxableIncomeEur }` (each optional,
  400 when out of range): the same, with the stamp duty and the IRPEF comparison computed from the landlord's data.
  A calculation (`lease.read`), not a write: nothing is stored; POST keeps the income out of URLs and access logs.

The response carries codes and statuses (`Computed`, `InputRequired`, `NotComputed`), never texts: the frontend
localizes them (`leases.rli.advisory.*`).

### Rules (verified by RS-5, `fiscale.md` L4-L14, C11-C12)

| Item | Computation | When not computed |
|---|---|---|
| Cedolare | 10% only for a `Concordato` lease in a comune whose ATA listing is `VerifiedDirectly`, otherwise 21%. Same rule as the canone concordato calculator (`HighTensionArea`). No registration tax, no stamp duty | — |
| Registration tax, first annuity | `max(67, annual rent × (concordato and verified ATA ? 70% : 100%) × 2%)`. Later annuities: 2% of each annuity's rent within 30 days of the end of the previous one, no minimum (shown as a rule) | — |
| Stamp duty | `16 € × ceil(max(pages / 4, lines / 100)) × copies` | Without pages and copies: only the rule. Without lines: amount on the pages, with a warning |
| IRPEF | `tax(other income + rent × 95%) − tax(other income)` with the brackets of `Irpef:TaxYear`; national gross tax only | Without the other income (asked); brackets of an earlier year; total income over 200.000 €; concordato in a verified ATA comune (its IRPEF reduction is not verified) |

Notes (codes, no effect on the amounts): ATA not verified or not listed, comuni in a state of emergency (L12, no
official list), attestation of conformity for the concordato, transitorio reliefs to confirm, term shorter than a year
(figures on 12 months), tax regime unknown (older concordato leases), extra-EU tenant: the registration does **not**
replace the 48-hour communication to the Questura (L13-L14).

### Configuration (`appsettings.json` → `CedolareAdvisory`)

Every rate, minimum and threshold is there with its `Source`; there is no default in code. `CedolareAdvisoryOptionsValidator`
stops the startup when a value is missing or impossible (a rate outside (0, 1), the reduced rate above the standard one,
brackets not ascending, a missing source). The IRPEF block is optional: without `Brackets` the comparison is "to be
assessed with the accountant".

Railway overrides use the usual names, e.g. `CedolareAdvisory__Registro__FirstYearMinimumEur`,
`CedolareAdvisory__Irpef__Brackets__1__Rate`. Do not override them unless a verified source changes a value: update
`appsettings.json` and `fiscale.md` instead.

**Every January:** the IRPEF brackets are those of `CedolareAdvisory:Irpef:TaxYear` (2026). From 1 January of the next
year the comparison shows "brackets outdated" until the brackets of the new year are verified on an official source
(budget law, MEF) and committed with the new `TaxYear`. From 1/1/2027 the new TUIR and the TU registro apply
(`fiscale.md`, "Avviso di riordino normativo"): check whether the article numbers in the sources change.

### Marking an ATA comune as verified

Only after reading the official text (D.L. 551/1988 art. 1 for the big cities, their neighbouring comuni and the
provincial capitals; the CIPE resolution of 13/11/2003 for the others). The same flag drives the calculator and the
advisory, so both switch together:

```sql
UPDATE "HighTensionAreaComuni"
SET "VerifiedDirectly" = true, "LastVerifiedAt" = '<date of the check>', "SourceReference" = '<official text and URL>'
WHERE "Comune" = 'Seveso';
```

Record the source in `canone_concordato.md` and report the change in the seed (`CanoneConcordatoMbSeed.BuildAtaCandidates`)
with a data migration (LT-13 will bring an administration of these data).

### Tests

`CedolareAdvisoryServiceTests` (audit cases: Seveso 800 €/month with ATA not verified → 21%; 70% base → 134,40 €;
250 €/month → 67 €; cedolare without registration tax and stamp duty; stamp duty rule; IRPEF brackets and the cases not
computed), `CedolareAdvisoryConfigurationTests` (committed values and validator), `LeaseTaxAdvisoryIntegrationTests`
(GET/POST on PostgreSQL, 400, another org). Frontend: `cedolare-decision-panel.test.tsx`.

## Product owner steps (Railway)

- Tax advisory (LT-08): nothing to set, the parameters are in `appsettings.json`. Remove, if present, the variables no
  longer read: `CedolareAdvisory__CedolareSeccaRate`, `CedolareAdvisory__CanoneConcordatoRate`,
  `CedolareAdvisory__RegistroRate`, `CedolareAdvisory__BolloEur`, `CedolareAdvisory__Disclaimer`,
  `CedolareAdvisory__OrdinaryIrpefNote`. Every January update the IRPEF brackets (see "Tax advisory (LT-08)").
- Nothing to set: the default is manual registration and offline signature. Do **not** set `Features__ESignProvider`
  until a real e-signature client exists and the open points of `rli-esign.md` §5 (FEA opinion, budget) are closed;
  with the flag on the service does not start without `ESign__WebhookSecret`.
- Remove, if present, `ESign__WebhookSecret=PLACEHOLDER_SET_IN_ENV` or any other placeholder (it is never a secret).
- Do **not** set `Features__RliProvider` until a real client exists and the open points of `rli-esign.md` §5 are
  closed.
- Remove, if present, the variables no longer read: `Rli__FilingEnabled`, `Openapi__BaseUrl`, `Openapi__ClientId`,
  `Openapi__ClientSecret` (the Openapi model has no client id/secret, `rli-esign.md` §1.2).
- Questura communication (LT-07): nothing to set. After the deploy expect one `questura-overdue` email per active lease
  with an extra-EU tenant whose default deadline has passed (see "After the deploy").
- Storage: the receipts and the signed contracts use the private bucket configured for FD-07
  (`docs/runbooks/storage.md`); `application/pdf` must be among its allowed MIME types if a restriction was set, and
  the bucket size limit must allow 20 MB files (signed contracts).
