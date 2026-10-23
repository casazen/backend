# Long-term lease: importing a contract already signed (LR-13)

Task LR-13 of the redesign wave (analysis `redesign/docs/wave/analysis/LR/`, brief `tasks/LR-13.md`; decisions LR-D2, LR-D12, LR-D13, LR-D28).
**Backend only, no migration, no feature flag (always on).** The brief was reduced by the perimeter review: the import carries the signed
PDF and the day of the signature; the registration is **not** part of it (it is declared afterwards, § 1). A contract signed outside CasaZen, with
the owner's own model, enters with deadlines, rents and reminders at once. Until now it could not: every signed PDF needs an approved template of
CasaZen (`LeaseSigningService`, LT-03) and the three templates are drafts (`lease-contract-templates.md`, LR-D28), so no contract in force could
get in. The import is the **one path that does not ask for a template**; the other paths keep the gate (§ 3).

Code: `LeaseDraftsController.Import`; `ILeaseSigningService.ImportSignedContractAsync` in `LeaseSigningService` (the same file and the same
shape as the offline signature); `LeaseDrafts/LeaseDraftWriter` (the projection of the typed values, shared with `LeaseDraftService`); the rules of an
imported draft in `Casazen.Core/Leases/` (`LeaseDraftRules`, `LeaseDraftProgress`, `LeaseReview`, `LeaseContractTerms.CheckTerm`, `LongRentRules`).
The guided draft it builds on is in [`long-rent-lease-draft.md`](long-rent-lease-draft.md) (LR-12).

## 1. The flow

1. `POST /api/leases/drafts { propertyId, origin: "Imported" }`. The origin is chosen when the draft is created and **never changes**: a lease made in
   CasaZen cannot become an imported one, and the other way round.
2. `PATCH /api/leases/{id}/draft` for steps 1 to 4 (property, parties, conditions, dates). The flow of an imported contract has steps 5 and 6 `NotApplicable`
   and step 7 `signed_copy_missing`, which stays `Locked` while a block is left in the first four.
3. `POST /api/leases/{id}/import`: the signed PDF and the day it was signed (§ 3). The lease keeps its `id`; no second contract is created.
4. If the contract is registered already: `POST /api/leases/{id}/registration/manual` (number, date and receipt PDF, `lease.register`), the declaration that
   exists since LT-01: the lease becomes `Registered`. Otherwise it stays `Signed` and the registration deadline is the one of every signed lease
   (`RegistrationDeadline`, LT-04). **Nothing invents a registration.**

## 2. What an imported draft is allowed (and a draft made in CasaZen is not)

| Rule | Imported | Made in CasaZen |
|---|---|---|
| Energy certificate (APE) and cadastral data of the property | missing = **warning** of the review (`ape_missing`, `cadastral_missing`), never a block (since LR-12) | block |
| Start date in the past | allowed, and not even a warning | warning `start_in_past` |
| Type | `Libero`, `Concordato` and **`Transitorio`**. The student lease is not a type of CasaZen: `lease_type_not_available` (no `type` in the params) | `Libero`, `Concordato`; `Transitorio` is `lease_type_not_available` |
| Term | the one the contract states: `endDate` (step 4, `yyyy-MM-dd`), **inside the limits of the type** (libero from 48 months, concordato from 36, transitory 1 to 18, `LeaseContractTerms.CheckTerm`). Without `endDate` the term follows the type (48 or 36 months from the start); the transitory type has none of its own and needs it | the term of the type, always (`endDate` is not read) |
| Canone concordato | **no range, no characteristics**: the unit's data, the agreement data of the comune and the rent range are not asked, not checked and not stored (`LeaseConcordatoAssessment` stays empty) | range computed by the server; a rent outside a verified range blocks |
| Contract template, review, "downloaded for signature" | not applicable (`lease_review_not_applicable`, `lease_sent_not_applicable`) | needed |

Three needs of step 4 exist for this: `end_missing` (transitory without `endDate`), `end_invalid` (`endDate` is not a date) and `term_out_of_range`
(`reason` `lease_end_before_start`, `lease_term_too_short` or `lease_term_too_long`, `type`, `minMonths`, `maxMonths`); the codes are in
[`long-rent-lease-draft.md`](long-rent-lease-draft.md) § 3. `GET /api/long-rent/rules` tells which types an imported contract can have
(`importedTypes`) and the free term of the transitory one (`transitoryTermMonths { min, max }`), so none of it is written in the interface (LR-D29).
`GET …/draft` shows the typed `endDate` in `values` and the term in `computed { endDate, termMonths }`.

## 3. The endpoint

`POST /api/leases/{id}/import`, `multipart/form-data`: `stipulaDate` (a day, **not after today** in Rome) and `signedContract` (a PDF: the content is
checked, `%PDF-`, at most 20 MB). Answers **200 with the masked detail of the lease** (the same body as `signed-document`): `status` `Signed`, `origin`
`Imported`, `hasSignedPdf`, `stipulaDate`, `registrationDeadline`, and in `events` `AllPartiesSigned` and `Imported`.

**Who may call it**: `lease.create` **and** `lease.sign` on the property of the lease, as a row of that property (TN-3, AM-03): another org's lease is
**404**, a property the caller may not handle (or a role with `lease.create` only) is **403**. The brief said `lease.create`; the import records a signature, so
it asks for the permission that signs. Every seeded role that has the one has the other (owner, administrator, property manager): nothing changes for them.

| Code | Status | Meaning |
|---|---|---|
| `lease_not_draft` | 409 | The lease is no longer a draft (signed, registered…): **a second import always ends here**; nothing was changed |
| `lease_draft_conflict` | 409 | The lease is busy (a save, a deletion, another import holds it for more than 5 seconds, or the database stopped the transaction in a race): nothing was written, try again |
| `lease_import_not_imported_origin` | 422 | The lease was not created with `origin: Imported`. A contract made in CasaZen is **not** importable: it is signed from its template (LR-D28) |
| `lease_review_blocked` | 422 | The review has a block (a need of steps 1 to 4: a party, the rent, the term…): read `GET …/review` |
| `lease_signed_contract_invalid` | 422 | Not a PDF, empty, or over 20 MB (the same code and text as `signed-document`) |
| `lease_stipula_date_in_future` | 422 | The day of the signature is after today (the same code as `signed-document`; the brief's `lease_import_stipula_future` was not added: one code per meaning) |
| `lease_not_found` | 404 | Not visible to the caller's org |
| `validation_error` | 400 | `stipulaDate` or `signedContract` missing |

**The gate of the models stays everywhere else** (decisions LR-D28 and D-C): `signed-document`, `contract.pdf`, `sent` and the provider signature of a contract
made in CasaZen still answer 422 `contract_template_not_approved` while its template is not approved, and the import of such a lease is refused with 422 above, so
the import opens **no exemption**. `LeaseImportIntegrationTests` proves it with the same PDF on both leases.

## 4. What it does

1. Before anything is written: the day is not in the future, the PDF is a PDF, and the lease is importable (draft, imported, nothing blocking). A request that is
   going to be refused writes nothing to the bucket.
2. Two files in the private bucket (§ 5).
3. **One transaction under the row lock of the lease**, the lock of the guided draft (`SELECT … FOR UPDATE`, `lock_timeout` 5 s): the lease is read again once
   nobody else can change it and checked again (a second request waiting for the lock finds it signed: 409); if its typed state is still only in `DraftData`
   the columns and the parties are written first, as a save would (`LeaseDraftWriter`); the term of the type is checked (`LeaseContractTerms.EnsureTerm`); then
   `Status = Signed`, `DraftData` cleared, `SignedPdfStoragePath`, the stipula and the registration deadline (`RecordStipula`, LT-04), `StipulaDeclaredByUserId`,
   every party a signer `Signed` (method `Offline`, `SignedAt` = the stipula), the document (§ 5) and two events: `AllPartiesSigned` with payload `imported` and
   `Imported` (no payload, one microsecond later so the timeline lists them always the same way).
4. Any failure (a refusal, a lost race, a database error) **removes the two files**; nothing else changed.

Not called: `ILeaseTemplateService.EnsureFinalContractAvailable`, `IApeComplianceService` (no APE is asked), the calculator of the range of the concordato.
No personal data in the logs (`LeaseId`, the stipula and the deadline).

## 5. The signed PDF in the Documents section (one archive for the user)

The same PDF is also a `LeaseDocument` of the lease, `Kind = Contract`, so `GET /api/leases/{id}/documents` shows it and
`…/documents/{documentId}/download` serves it (the archive of lease attachments, `lease-documents.md`). Its name is the one the file had when it is a sound one
(a name, not a path, no control character, ending in `.pdf`, at most 240 characters), otherwise `contratto-firmato.pdf`.

**Two files, not one shared key** (`leases/{org}/{lease}/signed-contract/…` for `signed-document`, `orgs/{org}/leases/{lease}/documents/…` for the document). Both
are written once and are never overwritten. The reason is deletion: the draft deleter (`LeaseDraftDeleter`) and a future "delete document" of the Documents section
remove files by key; with one shared key, deleting the document would take the signed contract away from the lease (and from the RLI registration, which needs it),
and the other way round. With two keys each file has one owner. The price is up to 20 MB twice per imported contract. Today nothing deletes either: a lease with a
signed copy is not deletable, and the Documents section has no delete.

## 6. After the import

- The lease is **a lease like any other**: list, detail, the deadlines (registration while it is not declared, end of the term, notice), Questura for an
  extra-EU tenant. `origin` only tells the timeline it was brought in. `GET …/draft` and `GET …/review` answer 404 `lease_not_draft`.
- **The rent plan is not created.** It starts from the start date of the contract (`RentInstallmentPlan`), so a contract already running would get installments
  "late" from the first day: starting it from a chosen month is LR-04a.
- **What is sent, and when.** The import itself sends nothing and writes to no third party. The existing daily job `rli-deadline-reminder` handles the imported lease like
  any signed lease not registered yet: the reminders of the registration deadline go to the **landlords of the lease** that have an e-mail address (once per
  threshold, "overdue" included when the deadline has passed), and those of the Questura communication for an extra-EU tenant. A contract signed long ago and
  registered elsewhere therefore shows an overdue deadline and gets that reminder until the registration is declared (§ 1, step 4): the interface should say
  it before the import (LR-06b). Nothing reaches the tenants because of the import: the rent requests and reminders to them exist only once the user activates a rent
  plan for the lease (LR-04a makes the online request opt-in).
- The activity log of the org (AM-02b) has a closed list of types: nothing is written there (the decision is the log's, not this task's).

## 7. Concurrency

Everything that changes the lease takes the same row lock: a save, a confirmation, a deletion, the retention job and the import wait for each other for at most
5 seconds. Two imports of the same draft at the same moment: one commits, the other finds the lease signed and answers **409 `lease_not_draft`** (or, if it waited
too long, 409 `lease_draft_conflict`); it removes its own two files. A save that arrives after the import finds a lease that is not a draft (409). The retention job
never deletes a lease with a signed copy, and checks again under the lock: a draft imported while the job read its list is left alone.

## 8. Database and rollback

**No migration.** `LeaseEventType` gains `Imported` **at the end** (the value is the next number: 20 at the time of writing, check the enum; the column is the number).
`LeaseDraftValues.EndDate` is a new optional member of the JSON stored (encrypted) in `LeaseContract.DraftData`: a draft written before reads it as empty, and the code
before ignores it. The new rows are ordinary ones (`LeaseDocuments`, `LeaseSigners`, `LeaseEvents`).

Rollback: roll the code back; there is nothing to undo in the schema. Leases already imported stay valid (`Signed`, with their signed copy and their document): the
previous code reads them like any signed lease. If the previous code must not meet the new event, delete it first:
`DELETE FROM "LeaseEvents" WHERE "EventType" = 20` (the number `Imported` has in the enum at the merge).

## 9. Decisions applied and what is not here

| Decision | Applied |
|---|---|
| LR-D2 | A contract signed outside enters with the data of the wizard (steps 1 to 4), the signed PDF and the day of the signature; **no model required**. The registration is not part of the import (reduced scope): the manual declaration that exists |
| LR-D12 (a) | The transitory type enters only as an imported contract; the student lease does not exist and is refused with `lease_type_not_available` |
| LR-D13 (a) | A concordato imported has **no check of the range** |
| LR-D28 | The gate of the models stays for every contract made in CasaZen; the import is the way for contracts in force |

Not here (on purpose): the registration inside the import (declare it afterwards), reading the PDF (OCR), importing many files at once, inviting the tenant, a rent plan
(LR-04a), editing a contract already signed (LR-14), a "delete document" in the Documents section, and a flag (the endpoint is always on).

## 10. After the deploy: what to check

- `POST /api/leases/{id}/import` on an imported draft with the steps 1 to 4 complete answers 200, the lease is `Signed`, `GET /api/leases/{id}/documents` lists one
  `Contract` document and `GET /api/leases/{id}/signed-document` serves the PDF.
- The same call on a lease created without `origin` answers 422 `lease_import_not_imported_origin` and the lease is unchanged.
- A 500 on the import is not expected: look in the logs for `Signed contract imported` (success) and `Lease busy: the import was refused` (a lost race, with the id of the lease).
- Orphan files: a request that fails after the upload removes its files; one that could not be removed is logged as `Could not delete an orphan signed contract from the private bucket`.
