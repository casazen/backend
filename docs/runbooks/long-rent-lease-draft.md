# Long-term lease: the guided draft (LR-12)

Task LR-12 of the redesign wave (analysis `redesign/docs/wave/analysis/LR/`, brief `tasks/LR-12.md`; decisions LR-D3, LR-D8, LR-D10,
LR-D11, LR-D12, LR-D14, LR-D15, LR-D28, LR-D29, LR-D33, § 15). **Backend only, one migration, no feature flag (always on).** The wizard
of a lease is saved on the server from its first step: the landlord creates a draft with only the property, saves each step with
whatever is valid and whatever is not, comes back from another device and finds the same draft, reviews it, marks that the contract was
downloaded for signature, or deletes it. The complete creation `POST /api/leases` and every endpoint that existed are **unchanged**.

Code: `LeaseDraftsController`, `LongRentLeaseSetupController`; service `LeaseDraftService` (+ `LeaseDraftDeleter`,
`LeaseDraftRetentionService`, `LongRentLandlordDefaultsService`, `LeaseConcordatoAssessor`, `LeaseDrafts/LeaseDraftProjection`); pure rules in
`Casazen.Core/Leases/` (`LeaseDraftValues`, `LeaseDraftPatch`, `LeaseDraftRules`, `LeaseDraftProgress`, `LeaseReview`,
`LeaseDraftFingerprint`, `LongRentRules`, `LeaseDraftCodes`); job `LeaseDraftRetentionJob`; options `LongRentLeaseOptions`. The import of a contract
signed elsewhere (LR-13) is in [`long-rent-lease-import.md`](long-rent-lease-import.md).

## 1. Endpoints

All of them are always on (no feature flag) and need `lease.create` unless stated. Each one authorizes the lease (or the property) as a `HostResource` of its property
(`LeaseOperations.Create`, TN-3, AM-03): another org's lease or property is **404** (`lease_not_found`, `property_not_found`), a property
the caller may not handle (a collaborator limited to some properties) is **403**. The answers that carry personal data
(`GET/PATCH …/draft`, `POST …/drafts`, `landlord-defaults`) are `Cache-Control: private, no-store`.

| Endpoint | Does | Answers |
|---|---|---|
| `POST /api/leases/drafts` `{ propertyId, origin? }` | A draft with **only the property** (no APE, parties or dates asked). `origin` is `Created` (default) or `Imported` (a contract signed elsewhere, LR-13) | 201 `{ id, version: 1, flow }` + `Location` |
| `GET /api/leases/{id}/draft` | Every value of the draft, **not masked**, with `version`, `origin`, `wizardMaxStep`, `reviewConfirmedAt`, `sentForSignatureAt`, `updatedAt`, `values`, `computed { endDate, termMonths }` and `flow` | 200; 404 `lease_not_draft` for a lease that is not a draft (its masked detail is `GET /api/leases/{id}`) |
| `PATCH /api/leases/{id}/draft` `{ version, step, values }` | Saves the `values` of one `step` (1 to 5), **whatever they are** (§ 3) | 200 `{ version, flow, updatedAt, landlordIds, tenantIds }`; 409 `lease_draft_conflict`, 409 `lease_not_draft`; 400 `validation_error`; 422 `lease_parties_too_many`; body at most 256 KB |
| `GET /api/leases/{id}/review` | The checks of the review, worst first (§ 5) | 200 `{ version, reviewConfirmedAt, checks, counts }` |
| `POST /api/leases/{id}/review/confirm` `{ version? }` | Confirms the review (§ 5) | 200 `{ version, reviewConfirmedAt, flow }`; 422 `lease_review_blocked`, `lease_review_not_applicable` |
| `DELETE /api/leases/{id}/review/confirm` `{ version? }` | Withdraws the confirmation (and "downloaded for signature") | 200, same shape |
| `POST /api/leases/{id}/sent` `{ version? }` | Marks the contract as downloaded for the signature on paper (§ 6) | 200 `{ version, sentForSignatureAt, flow }`; 422 `lease_review_not_confirmed`, `lease_review_blocked`, `contract_template_not_approved`, `contract_data_missing`, `lease_sent_not_applicable` |
| `DELETE /api/leases/{id}/sent` `{ version? }` | Takes it back | 200, same shape |
| `DELETE /api/leases/{id}` | Deletes the draft with everything tied to it (§ 7) | 204; 409 `lease_not_deletable` |
| `POST /api/leases/{id}/import` (multipart, `lease.create` + `lease.sign`) | Brings in a contract already signed elsewhere, from a draft of origin `Imported` (LR-13): see [`long-rent-lease-import.md`](long-rent-lease-import.md) | 200 the masked detail of the lease; 409 `lease_not_draft`, `lease_draft_conflict`; 422 `lease_import_not_imported_origin`, `lease_review_blocked`, `lease_signed_contract_invalid`, `lease_stipula_date_in_future` |
| `GET /api/long-rent/landlord-defaults` | The landlord data the wizard fills in (§ 9) | 200 `{ firstName, lastName, fiscalCode, residence, contactEmail }`, each may be `null` |
| `GET /api/long-rent/rules` (`lease.read`) | The figures of the area, from the constants the backend checks against (§ 9) | 200 |

The body of `confirm`, `sent` and their `DELETE` is optional; with `version` the action is refused (409 `lease_draft_conflict`) when the
draft moved on. Every other status of an error is the usual RFC 7807 problem with a stable `code` and a localized `detail`
(`SharedResources`: `LeaseDraftConflict`, `LeaseNotDraft`, `LeaseReviewBlocked`, `LeaseReviewNotConfirmed`, `LeaseReviewNotApplicable`,
`LeaseSentNotApplicable`, `LeaseNotDeletable`, `LeaseDraftValuesInvalid`).

| Code | Status | Meaning |
|---|---|---|
| `lease_draft_conflict` | 409 | The save (or the action) was made on an older `version`: another tab or device saved first. **Nothing was written.** Reload the draft |
| `lease_not_draft` | 404 (`GET draft`, `GET review`) · 409 (the writers) | The lease is no longer a draft (signed, registered…) |
| `lease_review_blocked` | 422 | A check of the review blocks: the confirmation (and the download for signature) is refused |
| `lease_review_not_confirmed` | 422 | `sent` before the review is confirmed |
| `lease_review_not_applicable` | 422 | An imported contract has no review (it is already signed) |
| `lease_sent_not_applicable` | 422 | An imported contract is never downloaded for signature |
| `lease_not_deletable` | 409 | Not a draft, or a signed copy, or a rent plan exists |
| `lease_parties_too_many` | 422 | More than 10 landlords or 10 tenants |
| `validation_error` | 400 | The request is not shaped like the step's (§ 3): never about a value that is merely wrong |
| `lease_not_found`, `property_not_found` | 404 | Not visible to the caller's org |

## 2. What a draft is

A draft is a `LeaseContract` in status `Draft`. Its NOT NULL columns (`StartDate`, `EndDate`, `MonthlyRent`, `ContractType`) cannot be
empty because the list, the occupancy of a property, the retention of the parties' data, the RLI reminders and the agenda read them, so
a draft created with only the property carries **starting values** (`LeaseDraftDefaults`): both dates `3000-01-01` (midnight UTC, obviously
not a real date, far enough that no reader acts on it: no deadline, no agenda window, not an "ended" lease, no fiscal year), rent `0`, type
`Libero`. A value is written into its column **only when the landlord has typed a valid one**; the column is a function of the typed state
(a value cleared goes back to the starting value).

| Where | What |
|---|---|
| `LeaseContract.DraftData` | The whole typed state as JSON (`LeaseDraftValues`), **encrypted**, exactly as typed (a half typed date, a fiscal code that is wrong, a type that is not available). `null` once every value is valid: then the columns and the rows of the parties are the only copy |
| `Party` rows | Only the **complete** entries (name, valid fiscal code or VAT number, citizenship, residence, an e-mail that is an address or none). The entry keeps its id: it is the id of the row. Rows are deleted and written again in two saves (the unique index on lease, role, position refuses two rows that swap places), only when they differ |
| `LeaseContract.DraftVersion` | The number the saves and the actions are checked against (§ 4). 1 at creation, +1 at each change |
| `LeaseContract.WizardMaxStep` | The furthest step reached (1 to 7); a lease created complete by `POST /api/leases` is taken to have reached 5 |
| `LeaseContract.Origin` | `Created` (0) or `Imported` (1) |
| `LeaseContract.ReviewConfirmedAt`, `ReviewFingerprint` | The confirmed review and the SHA-256 of the values it covers (§ 5) |
| `LeaseContract.SentForSignatureAt` | When the contract was downloaded for the signature on paper (§ 6) |
| `Party.Residence` (**encrypted**), `Party.DocumentsVerifiedOn` | The residence (or registered office) of the party and the day the landlord checked an identity document. **No document is kept** (LR-D3) |
| `LeaseContract.TransitoryParty`, `TransitoryNeed` (**encrypted**), `TransitoryDocumentation` (**encrypted**) | The need of a **transitory** contract made in CasaZen: who has it, what it is and the documents that prove it (TR, § 16). Written like the other columns, only when valid and only for that type; the contract prints them in art. 2 of its model |

The step the landlord is on is never saved as such: `WizardMaxStep` says how far they went, and the **flow** (§ 3) says what each step still
needs. A step is `Done` only when nothing is missing **and** `WizardMaxStep` reached it.

## 3. Saving a step

`PATCH` carries the `values` of one step as a JSON object whose keys are the fields of that step; keys of another step are refused.
**Merge semantics:** a key that is there replaces the field (`null` clears it, a list is replaced as a whole), a key that is not there leaves
it as it was, so a client that sends one field never wipes the others.

| Step | Keys of `values` |
|---|---|
| 1 · Immobile | `propertyId` (a property of the org the caller may handle; chosen, never un-chosen) |
| 2 · Parti | `landlords`, `tenants`: lists of `{ id?, firstName, lastName, fiscalCode, citizenship, residence, contactEmail }` (an entry nobody typed in is dropped; an entry keeps the `id` of an entry of **the same role**: any other `id` — unknown, of the other role, repeated — is a new entry, so an id is never in two entries; at most 10 per role) |
| 3 · Condizioni | `contractType` (`Libero`, `Concordato`, `Transitorio` since TR, LR-D12; any other name is kept and reported as not available), `taxRegime` (`CedolareSecca`, `Ordinario`), `monthlyRent`, `securityDeposit` (required of a contract made in CasaZen, `0` if none; optional of an imported one), `concordato` (`sqm`, `typeAElementCount`…`qualifyingTypeDElementCount`, `isFurnished`, `stoveHeating`, `airConditioning`, `garageSqm`, `balconySqm`, `otherAppurtenanceSqm`, `privateGreenSqm`, `zoneName`, `cadastralSheet`; an imported contract does not use them), and, **for the transitory type made in CasaZen only** (TR, § 16), `transitoryParty` (`Landlord` or `Tenant`: who has the need that justifies the short term) and `transitoryNeed` (the need, at most 500 characters) |
| 4 · Durata e date | `startDate` (`yyyy-MM-dd`); the end of the first period is computed (`computed.endDate`) from the type: 48 months for the libero, 36 for the concordato. **Imported contracts and the transitory type** (LR-13, TR): `endDate` (`yyyy-MM-dd`), the last day of the term, free inside the limits of the type (1 to 18 months for the transitory, which has no term of its own and needs it); a libero or a concordato made in CasaZen never reads `endDate`. **Transitory type made in CasaZen only**: `transitoryDocumentation` (the documents attached to prove the need, at most 300 characters), asked when the term lasts more than 30 days (`transitoryDocumentationAfterDays` of the rules) |
| 5 · Modello e allegati | `parties`: `[{ id, documentsVerifiedOn }]`, the day a document of each party was checked (`yyyy-MM-dd`, not after today) |

**A wrong or incomplete value is not an error**: it is saved and reported in the `flow`. That includes a number too big to hold (`1e30` as
a rent, `3000000000` as a count): it is kept as the largest (or smallest) value the type has, which no rule accepts, so the flow says
`rent_invalid` and the autosave goes on. The only errors are the request that is not shaped like the step (a key of another step, a number
sent as text or a fraction where a whole number goes, an `id` that is not one of the draft, a text over 500 characters, values that are not
an object: **400**), more than 10 parties of a role (**422**), an old `version` or a draft that is busy (**409**) and a lease that is not a
draft (**409**). The messages name the field, never what was sent.

The **flow** is computed on the server by a pure function (`LeaseDraftProgress`) from the values and the facts about the property (APE,
cadastral data): `{ steps: [{ step, state, needs: [{ code, params }] }] }` for the eight steps of the wizard. `state` is `Done`, `Todo`,
`Waiting` (reserved for the day the tenant fills in their own data, never produced yet), `Locked` (a step before it is not complete) or
`NotApplicable`. Steps 6 to 8 follow the facts: 6 `Done` once the review is confirmed, 7 once the contract is signed, 8 once it is registered.
For an imported contract steps 5 and 6 are `NotApplicable` and step 7 asks for the signed copy.

| `needs[].code` | Step | `params` |
|---|---|---|
| `property_missing` | 1 | — |
| `ape_missing`, `cadastral_missing` | 1 | — (the facts about the property; they do not keep `DraftData` alive; an imported contract only gets a warning in the review) |
| `ape_identification_missing` | 1 | — (**a contract made in CasaZen only** (LR-APE): the property has an APE but no APE document has both its code and its energy class. The three models print them (`{{ape_estremi}}`) and the reading of the certificate does not guarantee they were saved; without them the final contract would answer `422 contract_data_missing` after the review was confirmed. A fact about the property like `ape_missing` (it does not keep `DraftData` alive). The rule is the contract's — it prints the latest APE document that has both — so the review and the contract cannot disagree. A certificate that could not be read whole (`APE_IDENTIFICATION_REQUIRED`) asks for the same thing. Never for an imported contract, which prints nothing) |
| `landlord_missing`, `tenant_missing` | 2 | — |
| `landlord_incomplete`, `tenant_incomplete` | 2 | `index`, `fields` (`firstName`, `lastName`, `fiscalCode`, `citizenship`, `residence`, `contactEmail`) |
| `party_fiscal_code_duplicate` | 2 | `entries: [{ role, index }]` (the same fiscal code twice in the contract: the later entries are the ones to change) |
| `contract_type_missing` | 3 | — |
| `lease_type_not_available` | 3 | `type` when it names a type that exists and cannot be created (none today: the transitory type is available since TR, as it was for an imported contract, LR-13), none when it names nothing (the student lease is not a type of CasaZen) |
| `transitory_party_missing`, `transitory_need_missing` | 3 | — (**the transitory type made in CasaZen only**, TR: who has the need that justifies the short term, `Landlord` or `Tenant`, and what the need is, D.M. 16/01/2017 art. 2 c. 4. The contract prints them in art. 2 of its model. Not for an imported contract, which prints nothing of its own) |
| `tax_regime_missing` | 3 | — |
| `cedolare_not_allowed_company` | 3 | — (the cedolare secca does not apply to a tenant with an 11-digit code) |
| `rent_missing`, `rent_invalid` | 3 | — (a rent is more than 0, at most 1,000,000, with cents at most) |
| `concordato_data_missing`, `concordato_data_invalid` | 3 | — (not for an imported contract: the range is not checked, LR-D13) |
| `concordato_unavailable` | 3 | `reason` (`CanoneConcordatoReasonCodes`: no agreement data for the comune, a zone that is missing…); not for an imported contract |
| `rent_out_of_band` | 3 | `minMonthly`, `maxMonthly` (a rent outside a **verified** range; outside an indicative one it is only a warning of the review); not for an imported contract |
| `deposit_missing` | 3 | — (**a contract made in CasaZen only** (LR-DEP): the model prints the security deposit, so it has to be typed — `0` is a valid answer, "none"; without it the final contract would answer `422 contract_data_missing` after the review was confirmed. Never for an imported contract, whose deposit stays optional) |
| `deposit_invalid` | 3 | — |
| `deposit_too_high` | 3 | `maxAmount`, `maxRents` (the security deposit is at most 3 rents, `LongRentRules.DepositMaxRents`) |
| `start_missing`, `start_invalid` | 4 | — |
| `end_missing` | 4 | — (the transitory type, imported or made in CasaZen (TR), whose term is free, without the `endDate`; any other type without one follows its type) |
| `end_invalid` | 4 | — (an imported or a transitory contract: `endDate` is typed and is not a date) |
| `term_out_of_range` | 4 | `reason` (`lease_end_before_start`, `lease_term_too_short`, `lease_term_too_long`), `type` (when the type is known), `minMonths`, `maxMonths` (the limits of the type: 48 the libero, 36 the concordato, 1 to 18 the transitory) — an imported or a transitory contract whose stated term is not one its type allows |
| `transitory_documentation_missing` | 4 | — (**the transitory type made in CasaZen only**, TR: the term is good and lasts more than 30 days (`transitoryDocumentationAfterDays`), so the need must be proved with documents attached to the contract, D.M. 16/01/2017 art. 2 c. 4, and none was typed. Asked here, with the term that decides it) |
| `transitory_documentation_too_long` | 4 | `maxLength` (300; the same contract: the documentation is longer than that) |
| `party_documents_date_invalid` | 5 | `role`, `index` |
| `review_not_confirmed` | 6 | — |
| `not_sent` | 7 | — (the review is confirmed, the contract was not downloaded for signature) |
| `signed_copy_missing` | 7 | — (downloaded for signature, the signed copy is not uploaded; an imported contract: the signed contract) |
| `registration_missing` | 8 | — |

A code is never renamed: a new one is added. The interface turns each code into a sentence.

## 4. Versions, concurrency, two tabs

`PATCH` needs the `version` the client read; the answer carries the new one. **Every change** (`PATCH`, `confirm`, `sent` and their
`DELETE`) runs in a READ COMMITTED transaction that first takes a row lock on the lease (`SELECT … FOR UPDATE`, PostgreSQL only; EF InMemory
does not lock), reads the lease after the lock and compares the version: two saves of the same draft at the same moment are applied one
after the other and the second, made on the version the first replaced, is refused with **409 `lease_draft_conflict` and writes nothing**.
The version is an explicit integer column (not `xmin`, not an EF concurrency token): the client must see it and the actions that carry no
values bump it too. `GET draft` returns the same `version` to every tab; the screen that gets 409 reloads and asks the landlord. The
confirmation of the review and a save racing each other can never leave a confirmed review of values that changed: the confirmation
fingerprints the values it reads under the lock (`LeaseDraftPostgresTests`).

**A draft that is busy is a 409 too, never a timeout.** The wait for the row lock is limited to 5 seconds (`SET LOCAL lock_timeout`,
`LeaseDraftLocking`): somebody confirming the draft (which reads the energy certificate from the storage while it holds the lock) or a job
deleting it makes a second change wait that long at most, and then answer 409 `lease_draft_conflict` with nothing written; the same answer
for a transaction the database stopped in a race (serialization failure, deadlock) or a command that timed out while it waited. The client
reloads and tries again. The log says `Lease draft busy` with the id of the lease.

## 5. The review and its confirmation

`GET …/review` lists the checks `{ code, severity, step, params }`, worst first (`Block`, `Warn`, `Info`, `Ok`; inside a severity in the
order of the steps), with `counts { block, warn, info, ok }`. No personal data in the parameters: counts, amounts, dates.

| Severity | Codes |
|---|---|
| `Block` | the needs of steps 1 to 5 (same code, same step, same params): the review and the "what is left" list never disagree. For an imported contract `ape_missing` and `cadastral_missing` are `Warn` |
| `Warn` | `property_short_mode` (the property is let for short stays, LR-D15: a warning, not a block) · `start_in_past` (`start`; not for an imported contract, which is already in force) · `lease_overlap` (`count`: another contract of the property, not rejected and not a draft nobody handed out for signature, in the dates of the first period; LR-D14: a warning, one can let by rooms) · `rent_outside_indicative_band` (`minMonthly`, `maxMonthly`) · `template_data_missing` (the template is approved but the draft lacks data it uses) |
| `Info` | `documents_not_verified` (`count`) · `tenant_extra_eu` (the Questura communication, LT-07) · `template_not_approved` (LR-D28: to know, not a block) · `transitory_rent_band_to_check` (step 3, `inhabitants` 10000: **a transitory contract made in CasaZen only**, TR. In the municipalities with more inhabitants than that the rent has to be inside the bands of the territorial agreement, or the contract falls back to a 4+4 (D.M. 16/01/2017 art. 2 c. 2 and c. 6). CasaZen knows neither the inhabitants nor the agreement and has no calculator for the bands (TR-FU1): it tells the landlord to check it, it never blocks) |
| `Ok` | `property_ok`, `ape_ok`, `cadastral_ok`, `landlords_ok` (`count`), `tenants_ok` (`count`), `rent_ok` (`amount`), `rent_in_band`, `deposit_ok` (`amount`, `rents`), `start_ok` (`start`, `end`, `months`) |

`POST …/review/confirm` is refused (422 `lease_review_blocked`) while a check blocks. It saves `ReviewConfirmedAt` and the **fingerprint**
(`LeaseDraftFingerprint`): a SHA-256 of the values of the five data steps read in a fixed order and form (ids and noise ignored: the same
fiscal code in lower case, a space more, the same number written another way, are not a change; the characteristics of the unit count only
for a canone concordato and are kept to the cents of their columns, so a form that goes on sending them after the landlord chose the libero
does not withdraw the review at every save), event `ReviewConfirmed`. Confirming again changes nothing. A later save that changes a value the
review covers recomputes the fingerprint and, if it differs, **withdraws the confirmation** (`ReviewConfirmedAt = null`, event
`ReviewReopened` payload `changed`) and, with it, "downloaded for signature". `DELETE` withdraws it by hand (payload `withdrawn`).

The confirmation also does what a save would when **the data became complete without one** (the agreement data of the comune were loaded,
a date typed ahead of today has come): the draft still keeps its typed state, so the columns and the rows of the parties are written now and the
typed state is dropped, and what is confirmed is what every other reader of the lease sees. It is one change: one version up.

## 6. "Downloaded for signature"

`POST …/sent` records that the landlord downloaded the contract to sign it on paper (`SentForSignatureAt`, event `SentForSignature`, step 7
`signed_copy_missing`). The lease **stays a draft**: the signature remains the offline one (`signed-document`, LT-02). It needs the
confirmed review and passes **the same gate as `GET /api/leases/{id}/contract.pdf`** (`LeaseContractTerms.EnsureTerm`, then
`ILeaseTemplateService.EnsureFinalContractAvailable`), so the state never says something the download would refuse: while the template of the
type and regime is not approved (LR-D28; today every template is a draft, `lease-contract-templates.md`) the answer is
**422 `contract_template_not_approved`** and nothing changes. Marking again changes nothing. `DELETE …/sent` takes it back (event `SentRevoked`
payload `withdrawn`; `review_reopened` when a change withdrew the review).

## 7. Deleting a draft (closes BE-PM02-1)

`DELETE /api/leases/{id}` removes the lease with its parties, signers, events, registration, authorizations and the files in the private
bucket (the Questura receipt, a registration receipt). **Only** a lease in status `Draft`, **without a signed copy and without a rent plan**:
anything else is 409 `lease_not_deletable` (so is a record that refers to the lease and was added after this code). It is the deletion PM-02
asks for (`property_mode_blocked_by_draft_lease`, "the draft must be deleted first"): with the only draft of a property gone, the property can
change mode again. The files are deleted after the commit; one that cannot be deleted is logged without any personal data and left.
`LeaseDraftDeleter` is the one place that knows what a draft is made of; the retention job uses it too.

## 8. The abandoned drafts: the job `lease-draft-retention` (decision LR-D33)

A draft holds the names, fiscal codes and residences of people and is not kept for ever. The job deletes, with their parties, events and
files, the drafts **nobody changed for `LongRentLease:DraftRetentionDays` days** (default **180**, whole number from 1 to 3650: another value
makes the **startup fail**, `LongRentLeaseOptionsValidator`). A draft with a signed copy, a rent plan, or that is not a draft any more is never
deleted. Every day at 03:50 UTC (`LeaseDraftRetentionJob.Cron`), after the GDPR retention (03:00) and the other retention jobs; **always
registered** (the retention of personal data does not depend on a switch).

- **Idempotent**: it deletes what matches, a second run finds nothing. Each draft is deleted in a transaction of its own under the row lock
  of § 4 after the conditions are checked again, so a draft saved or signed since the list was read is left alone (a draft idle for exactly
  the retention is kept; one second more is deleted).
- **Batches**: 100 drafts per batch, oldest first, at most 50 batches per run (5,000 drafts a night; the first run after a long stop takes
  the backlog over several nights).
- **One draft never stops the run.** A draft that is busy (its row lock is taken for more than 5 seconds, § 4), that was saved or signed
  since the list was read, that a record added after this code refers to, or that fails for any other reason (a value that cannot be
  decrypted, a failure of the database) is left, **not read again by this run**, so the ones behind it are still deleted and a draft that
  refuses to go at the head of the list does not stall the job night after night. The run logs how many it deleted and how many it left;
  a failure is logged as a warning with the id of the lease (not personal data) and the error.
- **A session advisory lock** (`PostgresAdvisoryLocks.Scope.LeaseDraftRetentionRun`, key `lease-draft-retention`) keeps two runs apart even
  outside Hangfire's own lock; a run that finds it held returns `Skipped` and does nothing.
- **No tenant**: the job reads every org (`IgnoreQueryFilters` on the tenant filter only). It logs **counts** and the cutoff, never an id of a
  party, never a name.

## 9. The landlord and the rules

`GET /api/long-rent/landlord-defaults` (`lease.create`, not masked, `no-store`): the first landlord of the **most recent lease the caller
reaches** whose data were not anonymized (`CreatedAt` descending), read in SQL through the caller's `HostScope` (a collaborator limited to
some properties never gets the landlord of a lease of another one); otherwise the fiscal code (or VAT number) of the org and the name and
e-mail of the caller. Any field may be `null`; the residence is only known from an earlier lease.

`GET /api/long-rent/rules` (`lease.read`) hands the screens the figures the backend applies, so **no figure of law is written in the
interface** (LR-D29): `termMonths { Libero: 48, Concordato: 36 }`, `renewalMonths { Libero: 48, Concordato: 24 }`, `noticeMonths: 6`,
`registrationDays: 30`, `depositMaxRents: 3`, `availableTypes: [Libero, Concordato, Transitorio]` (the transitory type since TR) and, since LR-13,
`importedTypes: [Libero, Concordato, Transitorio]` (what an imported contract can be) and `transitoryTermMonths { min: 1, max: 18 }` (the free
term of the transitory type); since TR also `transitoryDocumentationAfterDays: 30` (a transitory contract that lasts more days than that proves its
need with documents) and `transitoryBandFromInhabitants: 10000` (above it the rent has to be inside the bands of the territorial agreement). The terms, the notice and the registration deadline are the
constants the rest of the backend already uses (`LeaseContractTerms`, `LongRentDeadlineRules`, `RliRegistrationDeadline`); the deposit limit
(3 rents) and the renewal periods are product values taken from the demo and the interview and **not verified by a lawyer**: LR-D29 makes
that verification a condition for the production release of the area. Changing a figure is a change in `LongRentRules` and nowhere else.

## 10. Personal data

- **Encrypted at rest**: `LeaseContract.DraftData` (purpose `Casazen.LeaseContract.Draft`), `Party.Residence` (purpose
  `Casazen.LeaseParty`) and, since TR, `LeaseContract.TransitoryNeed` and `TransitoryDocumentation` (purpose
  `Casazen.LeaseContract.TransitoryNeed`: the reason for a short term can say why a person moves house) go through the Data Protection
  converters like the other encrypted columns (`encryption.md`; `EncryptedColumns.All`;
  the startup step also encrypts values found in clear). A payload is longer than its text, so none of these columns has a length. The other fields
  of a party (names, fiscal code, e-mail) keep the regime they always had; the masked DTOs never return the residence.
- **Masked detail unchanged**: `GET /api/leases/{id}` gains only `origin`, `wizardMaxStep`, `reviewConfirmedAt`, `sentForSignatureAt` and, per
  party, `documentsVerifiedOn` and `hasResidence` (fields at the end, optional: a client that does not know them reads the DTO as before).
  The fiscal code and the e-mail stay masked; **the clear data are only in `GET …/draft` and `landlord-defaults`**, for who may create the lease.
- **No identity document** is stored (LR-D3): only the day it was checked.
- **Erasure of the parties' data** (`LeasePartyPrivacyService.AnonymizeParty`, LT-12) also clears `Residence`, `DocumentsVerifiedOn` and
  the lease's `DraftData` (when a party is anonymized, and when the lease is marked as anonymized even if it had no party row: a draft whose
  entries are all incomplete lives in `DraftData` alone). Since TR, when every party of the lease is anonymized the need of a transitory
  contract and its documents (`TransitoryNeed`, `TransitoryDocumentation`) are cleared too: they are free text about a person.
- **A lease that leaves the draft drops its typed state**: the offline signature and the start of a signature with the provider
  (`LeaseSigningService`) set `DraftData` to null; the rows of the parties are the data of the contract.
- **Logs and errors** carry ids, steps, versions and counts: never a name, a fiscal code, a residence or a value sent
  (`LeaseDraftServiceTests.TheLogs_OfAWholeLifeOfADraft_HoldNoPersonalData_OnlyIdsStepsAndCounts`).

## 11. What reads a draft

- `GET /api/leases` and the views of LR-01 list it like every draft; while it is incomplete its dates are the starting values
  (`3000-01-01`) and its rent 0 (the interface should show a draft by its stage, not by these: follow-up of LR-01b/LR-02).
- The **RLI and Questura reminders** (`rli-deadline-reminder`) and the **agenda** (`LongRentAgendaService`) skip a lease that is a draft
  **and** whose `DraftData` is not null: a draft whose start date was typed in the past would otherwise start a registration deadline. A
  lease that is not a draft is never skipped, whatever it kept in `DraftData`. A draft still being typed is counted among the leases to sign
  but has neither date nor lease in the checklist.
- PM-02 sees the draft as it always saw one (`property_mode_blocked_by_draft_lease`) and § 7 is the way out.
- The new events appear in the timeline of the masked detail (`events[].eventType`): the interface must know `ReviewConfirmed`,
  `ReviewReopened`, `SentForSignature` and `SentRevoked` (LR-03 and LR-10 add their labels): the events are written as soon as a draft is
  confirmed or marked as sent.

## 12. Database

One additive migration, `AddGuidedLeaseDraft` (generated with `dotnet ef`, after the last of `develop`): on `LeaseContracts` `DraftData`
(text, null), `DraftVersion` (int, default 0), `WizardMaxStep` (int, default 0), `ReviewConfirmedAt`, `SentForSignatureAt` (timestamptz,
null), `ReviewFingerprint` (varchar(64), null), `Origin` (int, default 0 = `Created`); on `Parties` `Residence` (text, null) and
`DocumentsVerifiedOn` (date, null). `LeaseEventType` gains four members **at the end** (`ReviewConfirmed` 16, `ReviewReopened` 17,
`SentForSignature` 18, `SentRevoked` 19).

**Backfill**: the drafts that exist now were all created complete by one call, so each is set to `WizardMaxStep = 5`
(`AddGuidedLeaseDraft.BackfillSql`: only the rows in status `Draft` still at 0, which makes a rerun harmless); nothing else is invented, no
other status and no `UpdatedAt` changes. Such a draft opens in the guided draft with the steps whose data are complete done; the residence of
its parties and the tax regime, which the old form never asked, are to do. **The wizard owns the term**: the end of the first period is the
one the type gives (48 months for the libero, 36 for the concordato), which `computed.endDate` already shows before any save, so a draft made
by the old form with another term gets the term of its type at its first save. A draft made with the **transitory** type, which the old form
accepted, is a transitory draft of the wizard since TR: it keeps its type and its end (read back from the columns as the end the landlord
typed), and asks for the need the old form never asked for (`transitory_party_missing`, `transitory_need_missing`, and the documentation when
the term lasts more than 30 days); the first save of a step never turns it into a libero on its own.

**Second migration, TR: `AddLeaseTransitoryNeed`** (generated with `dotnet ef`, after the last of `develop`, additive): on `LeaseContracts`
`TransitoryParty` (integer, null: 0 `Landlord`, 1 `Tenant`, the numbers of `PartyRole`), `TransitoryNeed` and `TransitoryDocumentation` (text,
null, **encrypted** by the converter, no length). Nothing is backfilled: every existing lease has none, and only a transitory contract made
in CasaZen ever gets them. `Down` drops the three columns, so the need and its documents of the transitory drafts and contracts made since are
**lost** (the contracts already signed are untouched: their PDF is in the bucket).

**Rollback**: there is no flag to turn off: roll the code back (the endpoints and the job go with it), then `Down`. `Down` drops the columns: the typed state of the drafts still
incomplete, the residences, the day documents were checked and the wizard progress are **lost** (the contracts themselves are untouched).
The events of the four new types stay in `LeaseEvents` (`Down` does not touch them): if the code is rolled back too, delete them first,
`DELETE FROM "LeaseEvents" WHERE "EventType" IN (16, 17, 18, 19)` (the numbers of `LeaseEventType` at the merge; check the enum), because the
previous code does not know them.
Proved on PostgreSQL with historical rows written by SQL, revert and reapply by `AddGuidedLeaseDraftMigrationPostgresTests` (CI).

## 13. Configuration

| Variable | Default | Meaning |
|---|---|---|
| `LongRentLease__DraftRetentionDays` | `180` | § 8; whole number from 1 to 3650, anything else makes the startup fail |

## 14. After the deploy: what to check in operation

Deploy **on the test environment first**; the figures of § 9 are to be confirmed before the production release. Then:

- `lease-draft-retention` is in the recurring jobs (34 with every flag on, 29 with the defaults); the first run logs how many drafts it deleted.
- A save that answers **409** is the normal answer to two tabs; one that answers **500** is not (look for `Lease draft` in the logs, which
  carry the id of the lease and no data).
- A draft that cannot be deleted by the landlord (409 `lease_not_deletable`) has a signed copy or a rent plan: it is a contract now.
- `sent` answering 422 `contract_template_not_approved` is the honest answer while the templates are drafts; it goes away when the product
  owner approves them (`lease-contract-templates.md`).
- To clean by hand: `DELETE /api/leases/{id}` as the owner; never `DELETE FROM "LeaseContracts"` (the signers, the registration and the
  authorizations hold foreign keys, and the files stay in the bucket).

## 15. Decisions applied and open points

| Decision | Applied |
|---|---|
| LR-D3 | No identity document stored; only the day it was checked (`Party.DocumentsVerifiedOn`) |
| LR-D8 (a) | The data of law of a party: name, fiscal code or VAT number, residence or registered office, citizenship; the e-mail is optional |
| LR-D10 (a) | `sent` ("Scarica da firmare") needs the **explicit** confirmation of the review: 422 `lease_review_not_confirmed` otherwise |
| LR-D11 (a) | A company as tenant with the cedolare secca: `cedolare_not_allowed_company`, a block of the review |
| LR-D12 (a) | `Libero` and `Concordato` can be created; the student lease is kept as typed and reported as not available (`lease_type_not_available`); the transitory contract entered as an imported one (LR-13: done, `long-rent-lease-import.md`) and, since TR, can be made in CasaZen with the official Allegato B (§ 16) |
| LR-D14 (a) | Overlap with another contract of the property: a warning of the review, not a block |
| LR-D15 (a) | A property let for short stays: a warning of the review, not a block |
| LR-D28 | A template that is not approved: `sent` answers 422 `contract_template_not_approved`, the review says it as to know |
| LR-D29 (a) | Figures of law only on the server (`GET /api/long-rent/rules`); to be confirmed by a lawyer before the production release |
| LR-D33 (a) | Drafts idle for 180 days are deleted, never one with a signed copy |

Open: the starting dates of an incomplete draft show in the lease list until the interface shows drafts by stage; the renewal periods are in
`LongRentRules` for LR-14. The rules specific to an imported contract (the transitory type, the term it states, the APE and a start in the past
relaxed, no range of a concordato) and the import itself are in [`long-rent-lease-import.md`](long-rent-lease-import.md) (LR-13).
Known limits, not in this task: nothing caps the number of drafts an org can create (only the retention bounds them: a limit per org or a rate
limit on `POST /api/leases/drafts` is a decision of the product owner); the fiscal reports count any lease that overlaps the tax year as
"leased", an abandoned draft with a typed start date included, as every draft always did; every read of a concordato draft asks the
calculator of the range, which loads the property (a cache is a follow-up if the screens read it often).

## 16. The transitory contract made in CasaZen (TR)

Task TR (2026-10-25). The transitory type (1 to 18 months, D.M. 16/01/2017 art. 2) can be made in the wizard, with the official model: the
Allegato B, as the variant `Transitorio` of the contract templates ([`lease-contract-templates.md`](lease-contract-templates.md), section
«Transitorio, bozza 2026-11-bozza-v1: scelte rispetto all'Allegato B»). That model is **not approved** (the product owner approves it with the
environment variables of the host, after the opinion of the lawyer), so until then `sent` answers `422 contract_template_not_approved` for a
transitory contract, as for the other types, and the preview is a BOZZA.

- **Type and term.** `LongRentRules.AvailableTypes` has `Transitorio` (`GET /api/long-rent/rules`). The term is free from 1 to 18 months and typed
  by the landlord as `endDate` at step 4 (the needs `end_missing`, `end_invalid`, `term_out_of_range` of the imported contract, with the limits
  from `transitoryTermMonths`). There is no band of the concordato, no tacit renewal and no notice: `LongRentRules.RenewalMonths(Transitorio)` is
  `null`, so `LeaseRenewal.RenewableTypes` leaves it out and the job `lease-term-renewal` never extends it, and `LongRentDeadlineRules.NoticeDate`
  gives no notice date. The deposit (at most 3 rents) and the APE and cadastral data are asked as for the other types made in CasaZen.
- **The need (D.M. art. 2 c. 4).** Step 3: `transitoryParty` (who has the need, `Landlord` or `Tenant`) and `transitoryNeed` (what it is, at most 500
  characters). Step 4: `transitoryDocumentation` (the documents attached, at most 300 characters), asked only when the term lasts more than 30 days
  (`transitoryDocumentationAfterDays`; 30 days or less is free of it). The needs are in § 3; the values are in the encrypted `DraftData` while the draft is
  incomplete and in the columns `TransitoryParty`, `TransitoryNeed`, `TransitoryDocumentation` once it is complete, where the contract reads them
  (§ 2, § 10). They are part of the fingerprint of the review (`LeaseDraftFingerprint`, only for the transitory type, so the impression of the
  reviews already confirmed does not change): changing one withdraws the confirmation.
- **The band of the rent.** In the municipalities with more than 10,000 inhabitants the rent of a transitory contract has to be inside the bands
  of the territorial agreement, or the contract falls back to a 4+4 (D.M. art. 2 c. 2 and c. 6). CasaZen has no calculator for it and does not
  know the inhabitants (TR-FU1): the review shows the check `transitory_rent_band_to_check` (`Info`, `inhabitants`) and never blocks.
- **Nothing is sent to anybody**: no e-mail, SMS or payment request comes from the transitory type; what already goes out for a signed lease
  (the RLI reminders) is as for the others.
- **Not done** (TR-FU1…TR-FU4 in the templates runbook): the calculator of the bands, the territorial agreement per comune, the data the model
  leaves out, and showing the need in the lease page. `POST /api/leases` (complete creation, no longer called by the interface) does not take
  the need, so a transitory contract made through it cannot get its final contract.
