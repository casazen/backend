# Wire transfers to the owners: the pain.001 files (epic E06, feature F05)

How an agency pays its owners with a bank file, and how that file is validated before a batch is saved (E06/F05/US01/T01). Requirement NF-25
(«validazione pain.001»); ADR-017 (the amounts of a statement are rounded once, at their total), ADR-010 and ADR-012 (agency plan, privacy of the IBAN).
The owner accounting that surrounds the wires is in [`owner-accounting.md`](owner-accounting.md).

## What the batch is

- `POST api/wire-transfers/batch` (`statementIds`, `debitAccount`) turns **published** owner statements into one file: **one transfer per owner** (the
  statements of an owner in the request are added up), one file per currency and debit account. EUR is the SEPA file, CHF the Swiss one; both are
  `pain.001.001.03` documents written by `WireTransferService` with `XDocument` and `XmlWriter` (every text escaped, amounts in invariant culture with two
  decimals). `GET api/wire-transfers/batch/{id}/file` downloads it and marks the batch downloaded; the agency uploads it to its bank. CasaZen sends nothing
  to a bank.
- Both endpoints need the agency permission `agency.wire.manage` (`WireTransferController`), held by the **owner and the administrator** of an org on
  the agency plan and by no other role (`OrgRoleCatalog.AgencyPermissionsOf`). Both reach every property of the org, so there is no property scope to apply:
  a collaborator, the one role that can be limited to «Solo alcuni» properties, never sees or validates a wire. The proof is
  `WireTransferPermission_IsHeldOnlyByTheRolesThatReachEveryProperty_Test`, which fails when a role is given the permission by mistake.
- A statement is in at most one batch (`statements_already_in_batch`).

## What is checked before anything is saved

The batch, its statements and its file are written by **one** `SaveChanges`, after every check below; a refusal leaves no row behind (before E06/F05/US01/T01
the batch was saved first and the file checked after: an invalid file left a batch without a file). Every refusal is 422 with a stable code, and **no
message, argument, exception or log line carries an IBAN or a name**.

| Order | Check | Code |
|---|---|---|
| 1 | The list of statements is not empty (an empty list is never «all the statements») | `no_statements_provided` |
| 2 | The debit account passes the IBAN check digits (mod 97) | `invalid_debit_account_iban` |
| 3 | The statements exist in the org, are published and are in one currency | `statements_not_found_or_not_owned`, `statements_not_published`, `mixed_currencies_not_allowed` |
| 4 | The currency has a wire file: EUR (SEPA) or CHF (Swiss), whatever the case or the spaces of the stored code | `wire_currency_not_supported` |
| 5 | No statement is already in a batch | `statements_already_in_batch` |
| 6 | Every owner has an IBAN and it passes the check digits | `owner_missing_iban`, `owner_invalid_iban` |
| 7 | The amount of every transfer is greater than zero and has at most two decimals (never zero, never negative, never rounded in silence) | `wire_amount_invalid` |
| 8 | The file validates against the **official schema of its currency** (`Pain001SchemaProvider`, `XsdValidator`) | `wire_batch_xml_invalid` (the number of errors and the line and position of the first five), `wire_batch_schema_unavailable` |
| 9 | The file is what its lines say (`Pain001Checks`): the control sum and the number of operations of the header and of the payment are the sum and the count of the transfers, and the batch's; every amount is positive with two decimals and has the currency of the batch written next to it; every end-to-end id is there and is unique | `wire_batch_content_invalid` (the names of the broken ISO elements: `CtrlSum`, `NbOfTxs`, `InstdAmt`, `Ccy`, `EndToEndId`) |

The amounts are exact decimals from the statements to the file and back (never a floating point number): the control sum is checked as a sum of the amounts
as written, not recomputed. The text of a schema error can quote a value of the file, so it is never used: only the position is.

## Official schemas (precondition of check 8)

The schemas are **not** written or rebuilt by hand: they are the files their publishers give, committed unchanged and embedded in the assembly
(`Casazen.Infrastructure.csproj`, logical names `Casazen.Infrastructure.Payments.Schemas.<Sepa|Swiss>.<file>`). Each currency has its own folder and its own
cached schema set. CasaZen writes both files in the ISO namespace `urn:iso:std:iso:20022:tech:xsd:pain.001.001.03`; that the Swiss schema has the same target
namespace is an **unverified premise** (the files were not available when this was written): see «CHF: adding the file may not be enough» below.

| File | Folder | Source | SHA-256 |
|---|---|---|---|
| The ISO 20022 `pain.001.001.03` schema (Customer Credit Transfer Initiation), the base of the SEPA credit transfer (EUR) | `Casazen.Infrastructure/Payments/Schemas/Sepa/` | ISO 20022 registration authority, site iso20022.org, message definitions, payments initiation (`pain`) | to record when the file is added |
| The XML schema of the Swiss Payment Standards for `pain.001.001.03` (CHF), published under the name `pain.001.001.03.ch.02.xsd` (confirm the name, the version and the target namespace on the publication) | `Casazen.Infrastructure/Payments/Schemas/Swiss/` | SIX Group, site six-group.com, Swiss Payment Standards, ISO 20022 customer-to-bank implementation guidelines | to record when the file is added |

**State of this build: the files are not in the repository yet.** They could not be taken from their publications while this change was written (no network
access was allowed to the delivery, and a schema is never rebuilt from memory). Until they are added, `Pain001SchemaProvider.HasOfficialSchema(currency)` is
false and **every wire batch of that currency answers 422 `wire_batch_schema_unavailable`: no file is produced without its validation and nothing is saved**.
This is on purpose (a made-up success would put an unchecked payment file in the hands of the agency); it is the same rule as `fatturapa_schema_unavailable`
for the electronic invoices (`fiscal-documents.md`). Who is affected: the orgs on the agency plan. The wire page of the frontend is behind the flag `agencyTier` (off
by default); the backend endpoints are **not** behind a feature flag (`WireTransferController` has no `FeatureGate`: only the agency plan and the permission), so the
refusal applies to any org on the agency plan whatever the flag says.

**Adding or updating a schema (operator):**

1. Download the file from the publication of its publisher (the pages above), never from a mirror or a package.
2. Put it in the folder of its currency unchanged (no reformatting, no change of line endings: `.gitattributes` marks `Payments/Schemas/**/*.xsd` as `-text`
   so the bytes, and the checksum, are the same on every machine). Nothing a schema refers to is ever fetched (no resolver): the files must be self-contained.
3. Record in the table above the exact address of the file, its version and its SHA-256 (`sha256sum <file>`).
4. Run `Pain001ValidationProofTests`: with the file in, `WireBatch_OfficialSchema_ValidatesTheFile_OrTheBatchIsRefused_Test` writes a batch of that
   currency, validates the file against the official schema and checks that an altered file does not validate; `Pain001SchemaProvider_EmbeddedFiles_CompileOrSayWhy_Test`
   fails with the reason (`Pain001SchemaProvider.LoadProblem`) when a file is embedded and does not compile.
5. If the file CasaZen writes does not validate (the Swiss file is the likely one: it is written like the SEPA one, with the service level `URGP`), the generator is
   what has to change, not the schema: that is what the validation is for. Swiss banks have moved to newer versions of the Swiss Payment Standards: confirm with the
   bank which version it accepts before relying on the Swiss file. When a file does not validate, the proof of step 4 fails with the code and the arguments of the
   refusal and with the line, position and text of every error of the schema on the same file (written with invented data): correct the generator from that.

**CHF: adding the file may not be enough.** The premise that the Swiss schema has the ISO target namespace was written without the file, and for the Swiss
Payment Standards (`pain.001.001.03.ch.02.xsd`) the target namespace is probably another one (an address of the publisher). The loader accepts a set only when it
has the namespace of the files CasaZen writes (`Pain001SchemaProvider.Namespace`), so a Swiss file with another namespace stays «unavailable»
(`Pain001SchemaProvider.LoadProblem("CHF")` says «no schema has the target namespace …») and never lets a file pass without being validated. If it is so, the
fix is a change of code and not only of files: the namespace per currency in `Pain001SchemaProvider` and `Pain001Checks`, and the Swiss generator
(`WireTransferService.GenerateSwissPaymentXml`: namespace, service level `URGP`). **Keep the guard** that the set contains the namespace of the file written:
without it `XsdValidator` only warns that it has no schema for the root element, and the file passes.

The schema sets are compiled without a resolver (nothing is fetched), and the files are read with the DTD prohibited and no resolver (`XsdValidator`): **no
external entity and no network connection** is ever opened by the validation (`WireFile_DtdAndExternalEntity_AreNeverRead_Test`).

## Privacy

- The IBAN of an owner is encrypted at rest on `PropertyOwner` (`EncryptedColumns`, ADR-012). The validation reads it to write the file and **never writes it
  anywhere else**: the errors above carry codes, counts, line and position and element names; the logs carry the batch, the owner id, counts and currencies.
- The file itself holds the IBANs and the names (it is the payment instruction) and is stored as text in `WireBatches.XmlContent`, as before this change.
  Protecting that column the way `PropertyOwner.Iban` is protected is a separate decision: it needs a migration and a step for the batches already stored.

## Known limits

- «A statement is in one batch only» is checked by the service, not by a unique index on `WireBatchStatements.OwnerStatementId`: two requests for the same
  statements at the very same moment can both pass the check. A unique index (a migration) would close it.
- The wire pays the `NetPayable` of the statement; the withholding of a payout is described in `owner-accounting.md` (E06/F05/US02/T04).

## Tests

`Pain001ValidationProofTests` (no database, no network): `WireBatch_InvalidXml_NeverSaved_Test` (money: an invalid file is refused and no batch row is written),
the amounts and the lines of the file (`WireBatch_AmountNotPositiveOrNotInCents_IsRefused_Test`, `WireFile_AmountsAreExactDecimals_SumAndCountMatchTheRows_Test`,
`Pain001Checks_ReportABrokenRule_ByTheNameOfTheElement_Test`), the currency (`WireBatch_CurrencyWithoutAWireFile_IsRefused_Test`,
`WireBatch_UsesTheSchemaOfItsCurrency_Test`), the empty list (`WireBatch_EmptyStatementList_Is422AndNeverAll_Test`), the IBAN
(`WireBatch_InvalidIban_IsRefusedWithoutTheIbanOrTheName_Test`), the official schema (`WireBatch_OfficialSchema_ValidatesTheFile_OrTheBatchIsRefused_Test`), the
loader that compiles the embedded files (`Pain001SchemaProvider_Load_CompilesTheFiles_AndSaysWhyWhenItCannot_Test`, with stand-in files: a good one, none, a broken
one, one with a DTD, another schema) and the permission. Until the official files are in, the tests that need to get past the validation give the service a schema
of their own (`Pain001TestSchemas`: an accepting one and a rejecting one, stand-ins that are not copies of the official schemas).
