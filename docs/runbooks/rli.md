# Runbook: RLI registration of lease contracts

Task **LT-01** (defects A7-01 and A7-21), decision **D15**, research **RS-4** (`docs/integrations/rli-esign.md`).
Registration deadline and reminders: task **LT-04** (defect A7-04), rule verified by **RS-5**.

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

- e-sign: the `all_signed` webhook (`LeaseWorkflowService.HandleESignEventAsync`). The provider sends no signing time,
  so the stipula is the day CasaZen processes the event, the same instant as the `AllPartiesSigned` event;
- offline or declared signature (task LT-02, not built yet): the flow must call `RecordStipula` with the declared
  signing date.

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
- The extra-EU Questura notice is unchanged (signed leases, once, payload `extra-eu`; task LT-07).

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

A signed lease without a stipula date needs its signing date from the landlord: until the offline/declared signing of
LT-02 exists, record it only after checking the signed contract (SQL, then `RegistrationDeadline` as in step 3).

## Product owner steps (Railway)

- Nothing to set: the default is manual registration. Do **not** set `Features__RliProvider` until a real client exists
  and the open points of `rli-esign.md` §5 are closed.
- Remove, if present, the variables no longer read: `Rli__FilingEnabled`, `Openapi__BaseUrl`, `Openapi__ClientId`,
  `Openapi__ClientSecret` (the Openapi model has no client id/secret, `rli-esign.md` §1.2).
- Storage: the receipts use the private bucket configured for FD-07 (`docs/runbooks/storage.md`); `application/pdf`
  must be among its allowed MIME types if a restriction was set.
