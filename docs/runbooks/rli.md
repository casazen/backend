# Runbook: RLI registration of lease contracts

Task **LT-01** (defects A7-01 and A7-21), decision **D15**, research **RS-4** (`docs/integrations/rli-esign.md`).

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

The deadline shown is `LeaseContract.RegistrationDeadline`, as computed by the API (task LT-04 owns the calculation).

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

## Product owner steps (Railway)

- Nothing to set: the default is manual registration. Do **not** set `Features__RliProvider` until a real client exists
  and the open points of `rli-esign.md` §5 are closed.
- Remove, if present, the variables no longer read: `Rli__FilingEnabled`, `Openapi__BaseUrl`, `Openapi__ClientId`,
  `Openapi__ClientSecret` (the Openapi model has no client id/secret, `rli-esign.md` §1.2).
- Storage: the receipts use the private bucket configured for FD-07 (`docs/runbooks/storage.md`); `application/pdf`
  must be among its allowed MIME types if a restriction was set.
