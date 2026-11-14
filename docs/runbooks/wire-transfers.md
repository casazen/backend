# Wire transfers to the owners: the pain.001 files (epics E06 and E16, feature F01)

How an agency pays its owners with a bank file, what the file is made of, and how it is validated before a batch is saved (E06/F05/US01/T01, E16/F01/US01/T01).
Requirements NF-25 («validazione pain.001») and NF-113 (Swiss documents and payments, `pain.001.001.09.ch.03` with structured addresses, validated with the official
schema); ADR-017 (the amounts of a statement are rounded once, at their total), ADR-010 and ADR-012 (agency plan, privacy of the IBAN). The owner accounting that
surrounds the wires, and the payout a wire pays, are in [`owner-accounting.md`](owner-accounting.md).

## What the batch is

- **The source of the amounts is the confirmed payout** (`OwnerPayout`: the net after the withholding, E06/F05/US02/T03), never the old `OwnerStatement.NetPayable`.
  `POST api/wire-transfers/batch` takes `{ "payoutIds": [...], "executionDate": "yyyy-MM-dd" }` and turns those payouts into **one file**: **one transfer per payout**
  (a payout is never added to another one, so a movement on the bank statement is told from the payout it pays by its `EndToEndId`), one file per currency. The
  debtor and the account to debit are the **issuer profile** of the org (`OrgFiscalIssuerProfile`: legal name, structured address, IBAN: the «Fatturazione» setup),
  so the request names no account. A payout is in **one batch only**, ever.
- A payout can be wired when it is **confirmed** (not reversed), when its statement version was **published** to the owner (a version that was published and
  superseded later still counts: the payout is a fact of its own), and when it is in no other batch.
- `GET api/wire-transfers/batches` lists the batches of the org, newest first (at most 200): id, state (`Generated`, `Downloaded`, `Paid`), currency, number of
  transfers, total, execution date, message (`format`) and the creation, download and payment times. **Never an IBAN, a name or the file.**
  `GET api/wire-transfers/batch/{id}/file` downloads the file and marks the batch downloaded; `POST .../confirm-download` and `.../confirm-paid` move the state. The
  agency uploads the file to its bank. CasaZen sends nothing to a bank.
- **The organization is checked explicitly** on every endpoint above (the service filters on the org of the caller, a `Find` by key would not apply the global
  tenant filter): a batch of another org is a **404**, and its state does not move (`WireBatch_ConfirmOtherOrganization404_Test`).
- All of it needs the agency permission `agency.wire.manage` (`WireTransferController`), held by the **owner and the administrator** of an org on the agency plan
  and by no other role (`OrgRoleCatalog.AgencyPermissionsOf`). Both reach every property of the org, so there is no property scope to apply: a collaborator, the
  one role that can be limited to «Solo alcuni» properties, never sees or validates a wire. The proof is
  `WireTransferPermission_IsHeldOnlyByTheRolesThatReachEveryProperty_Test`, which fails when a role is given the permission by mistake.
- There is no flag: the wire feature is gated by the agency plan and the permission, and the CHF file cannot be produced until the official schema is in the
  build (below).

## The two messages, and the cutoff of the old one

| Currency | Message (`WireBatch.MessageFormat`) | Namespace | Writer | Schema folder |
|---|---|---|---|---|
| CHF | `pain.001.001.09.ch.03` (Swiss Payment Standards, ISO 20022 `pain.001.001.09`) | `urn:iso:std:iso:20022:tech:xsd:pain.001.001.09` | `Pain001V09Writer` | `Payments/Schemas/Swiss/` |
| EUR | `pain.001.001.03` (SEPA) | `urn:iso:std:iso:20022:tech:xsd:pain.001.001.03` | `WireTransferService.WriteSepa`, as before | `Payments/Schemas/Sepa/` |

- **Cutoff.** The Swiss payment standards' parallel phase ends with the November 2026 release: after it only `pain.001.001.09.ch.03` is valid and
  `pain.001.001.03.ch.02` is never built again (`fatturazione/analisi-tecnica.md` §4). CasaZen therefore **no longer writes the Swiss `pain.001.001.03` file** (the one
  with the service level `URGP`): CHF is the `.09.ch.03` message from this change. EUR keeps the SEPA `.03` writer **until E16/F01/US02** moves it to
  `pain.001.001.09`; it is fed by the payouts like CHF. The writer of the Swiss file is the place where that change starts (`Pain001V09Writer` is the Swiss flavor).
- **The request of before is refused.** `POST api/wire-transfers/batch` with `statementIds` and a `debitAccount` is still read, so the client gets a code and not an
  error of shape, and it is refused with **422 `wire_batch_from_statement_retired`**; any `statementIds` or `debitAccount` in the request is refused, so an account the
  caller typed is never ignored in silence.
- **Batches of before.** A batch made by the statement flow keeps its stored file (`pain.001.001.03` in both currencies, `MessageFormat` empty, `ExecutionDate`
  empty) and its `WireBatchStatement` rows; it is still listed, downloaded and confirmed, and when it is paid the ledger still posts it
  (`OwnerAccountingService`). The batches of this change post nothing when they are paid: the payout is already in the ledger.

## The Swiss file (`Pain001V09Writer`)

- **Group header:** a unique `MsgId` (`CZ` and the id of the batch without hyphens, 34 characters), `CreDtTm` (Europe/Rome clock, no offset), the number of
  transactions and the control sum of the whole file, the initiating party (the agency).
- **One `PmtInf` per requested execution date** (the API takes one date, so one payment; the writer keeps the rule of the message): its own unique id, `TRF`, count and
  control sum, `ReqdExctnDt/Dt`, the **debtor** (the agency: name and structured address from the issuer profile), its IBAN and the debtor agent (`Othr/Id` =
  `NOTPROVIDED`: the bank reads it from the IBAN). Batch booking is on.
- **One `CdtTrfTxInf` per payout:** the unique `EndToEndId` (`CZ` and the id of the payout without hyphens, 34 characters), the amount in the currency with two
  decimals, the **creditor** (the owner: the legal name, or the first and last name, and the structured address of the owner registry, E13), the creditor IBAN and the
  remittance **«Rendiconto MM/AAAA»** (the period of the statement version of the payout). No creditor agent: a domestic IBAN identifies the bank.
- **Structured addresses only:** `StrtNm`, `BldgNb` (when there is one), `PstCd`, `TwnNm`, `Ctry`. Never an address in free lines. An owner is complete when the street,
  the postal code, the town and the country (two letters) are there; the building number is optional.
- **Texts:** every text is brought to the characters the Swiss messages accept (letters of Latin-1, digits, space and `/ - ? : ( ) . , ' + &`; other accented letters lose
  their accent, signs become spaces, what has no equivalent a point) and cut to the length of its element (`Nm` and `StrtNm` 70, `BldgNb` and `PstCd` 16, `TwnNm` 35,
  `Ustrd` 140). The XML escaping is the writer's.
- **Amounts** are exact decimals (never a floating point number), invariant culture, two decimals; the control sum is a sum of the amounts as written.
- The element order of the message follows the ISO 20022 sequences; `Pain001V09_ChfFile_IsTheSwissMessageWithStructuredAddresses_Test` keeps it checked while the
  official schema is not in the build.
- **To confirm against the official schema when it is added** (it was not available when this was written, see below): that the debtor agent of the Swiss message is
  written the way the schema takes it (`Othr/Id` `NOTPROVIDED`), and that nothing of the group header or of the payments is required that the file does not write. The
  proof of the next section prints every error of the schema with its line and position if one of these is wrong: the writer is what changes, never the schema.

## What is checked before anything is saved

The batch, its payouts and its file are written by **one** `SaveChanges`, after every check below, under the lock of the owner accounting of the org (the one a payout
confirmation and a reversal take; the unique index of `WireBatchPayouts` is the backstop of two requests the lock could not tell apart); a refusal leaves no row
behind. Every refusal is 422 with a stable code, and **no message, argument, exception or log line carries an IBAN or a name**: the refusals about owners list their
**ids** (`ownerIds` in the problem).

| Order | Check | Code |
|---|---|---|
| 1 | The list of payouts is not empty (an empty list is never «all the payouts») and not longer than 1000; the same id twice is one transfer | `no_payouts_provided`, `wire_batch_too_large` |
| 2 | The execution date is today (Europe/Rome) or later, at most a year ahead (no date is refused) | `wire_execution_date_invalid` |
| 3 | The payouts exist in the org (another org's payout is «not found») | `payouts_not_found_or_not_owned` |
| 4 | Every payout is confirmed, not reversed | `wire_payouts_not_confirmed` |
| 5 | Every payout comes from a statement version that was published to the owner | `wire_payouts_not_published` |
| 6 | One currency, whatever the case or the spaces of the stored code, and a currency with a wire file: EUR (SEPA) or CHF (Swiss) | `mixed_currencies_not_allowed`, `wire_currency_not_supported` |
| 7 | The net amount of every transfer is greater than zero and has at most two decimals (never zero, never negative, never rounded in silence) | `wire_amount_invalid` |
| 8 | No payout is already in a batch | `payouts_already_in_batch` |
| 9 | The issuer profile has its legal name and a complete structured address (street, postal code, town, country) | `wire_debtor_incomplete` |
| 10 | The issuer profile has an IBAN and it passes the check digits (mod 97) | `wire_debit_account_missing`, `invalid_debit_account_iban` |
| 11 | Every owner has a complete structured address (the Swiss file writes it; the EUR file writes the name alone), an IBAN, that has the length of its country and passes the check digits (the definition of the owner registry, `OwnerFieldRules.IsValidIban`), that is **not a QR-IBAN** (the account of a QR-bill, never the creditor account of a normal transfer: institution 30000 to 31999 of a Swiss or Liechtenstein IBAN) and, in CHF, is Swiss or Liechtenstein (a CHF transfer to a foreign IBAN is not a domestic payment, which this file does not cover). One refusal at a time, in this order, listing every owner that fails it | `wire_owner_address_missing`, `wire_owner_iban_missing`, `owner_invalid_iban`, `wire_owner_qr_iban`, `wire_owner_iban_not_swiss` |
| 12 | The file validates against the **official schema of its currency** (`Pain001SchemaProvider`, `XsdValidator`) | `wire_batch_xml_invalid` (the number of errors and the line and position of the first five), `wire_batch_schema_unavailable` |
| 13 | The file is what its lines say (`Pain001Checks`, in the namespace of the message of the currency): the control sum and the number of operations of the header and of the payment are the sum and the count of the transfers, and the batch's; every amount is positive with two decimals and has the currency of the batch written next to it; every end-to-end id is there and is unique | `wire_batch_content_invalid` (the names of the broken ISO elements: `CtrlSum`, `NbOfTxs`, `InstdAmt`, `Ccy`, `EndToEndId`) |

The text of a schema error can quote a value of the file, so it is never used: only the position is.

## Official schemas (precondition of check 12)

The schemas are **not** written or rebuilt by hand: they are the files their publishers give, committed unchanged and embedded in the assembly
(`Casazen.Infrastructure.csproj`, logical names `Casazen.Infrastructure.Payments.Schemas.<Sepa|Swiss>.<file>`). Each currency has its own folder, its own target
namespace and its own cached schema set; a set is accepted only if it has the namespace of the message CasaZen writes for that currency
(`Pain001SchemaProvider.NamespaceOf`), because otherwise the validation would only warn that it has no schema for the root element and the file would pass.

| File | Folder | Source | SHA-256 |
|---|---|---|---|
| `pain.001.001.09.ch.03.xsd`, the schema of the Swiss Payment Standards for the customer credit transfer (CHF), target namespace `urn:iso:std:iso:20022:tech:xsd:pain.001.001.09` | `Casazen.Infrastructure/Payments/Schemas/Swiss/` | SIX Group, Swiss Payment Standards, ISO 20022 customer-to-bank messages: <https://www.six-group.com/en/products-services/banking-services/payment-standardization/swiss-payments.html> (the page of the project's analysis; take the file from the XSD of the Swiss implementation guidelines for `pain.001`, version `ch.03`) | **to record when the file is added**: the exact address of the file, the version and `sha256sum` |
| The ISO 20022 `pain.001.001.03` schema (Customer Credit Transfer Initiation), the base of the SEPA credit transfer (EUR, until E16/F01/US02) | `Casazen.Infrastructure/Payments/Schemas/Sepa/` | ISO 20022 registration authority, site iso20022.org, message definitions, payments initiation (`pain`) | to record when the file is added |

**State of this build: the files are not in the repository.** The delivery did not add them: a schema is never rebuilt from memory, and taking the file from its
publisher is a step of the operator (the PR lists it under «Manual follow-up»). Until they are added, `Pain001SchemaProvider.HasOfficialSchema(currency)` is false and
**every wire batch of that currency answers 422 `wire_batch_schema_unavailable`: no file is produced without its validation and nothing is saved**. This is on purpose
(a made-up success would put an unchecked payment file in the hands of the agency); it is the same rule as `fatturapa_schema_unavailable` for the electronic invoices
(`fiscal-documents.md`). Who is affected: the orgs on the agency plan. The wire page of the frontend is behind the flag `agencyTier` (off by default); the backend
endpoints are **not** behind a feature flag (`WireTransferController` has no `FeatureGate`: only the agency plan and the permission), so the refusal applies to any org
on the agency plan whatever the flag says. The proof `Pain001V09_ChfBatch_ValidAgainstOfficialXsd_Test` is **skipped, with this reason, while the Swiss file is missing**.

**Adding or updating a schema (operator):**

1. Download the file from the publication of its publisher (the pages above), never from a mirror or a package.
2. Put it in the folder of its currency unchanged (no reformatting, no change of line endings: `.gitattributes` marks `Payments/Schemas/**/*.xsd` as `-text`
   so the bytes, and the checksum, are the same on every machine). Nothing a schema refers to is ever fetched (no resolver): the files must be self-contained.
3. Record in the table above the exact address of the file, its version and its SHA-256 (`sha256sum <file>`).
4. Run `Pain001V09Tests` and `Pain001ValidationProofTests`: with the file in, `Pain001V09_ChfBatch_ValidAgainstOfficialXsd_Test` stops being skipped, writes a batch,
   validates the file against the official schema and checks that an altered file does not validate; `Pain001SchemaProvider_EmbeddedFiles_CompileOrSayWhy_Test` fails
   with the reason (`Pain001SchemaProvider.LoadProblem`) when a file is embedded and does not compile or has another target namespace.
5. If the file CasaZen writes does not validate, the writer is what has to change, not the schema: that is what the validation is for. When a file does not validate, the
   proof of step 4 fails with the code and the arguments of the refusal and with the line, position and text of every error of the schema on the same file (written with
   invented data): correct `Pain001V09Writer` from that. Swiss banks accept the versions their publisher lists: confirm with the bank which one it accepts before relying
   on the file.

The schema sets are compiled without a resolver (nothing is fetched), and the files are read with the DTD prohibited and no resolver (`XsdValidator`): **no
external entity and no network connection** is ever opened by the validation (`WireFile_DtdAndExternalEntity_AreNeverRead_Test`).

## Privacy

- The IBAN of an owner is encrypted at rest on `PropertyOwner` (`EncryptedColumns`, ADR-012), and so is its structured billing address. The validation reads them to
  write the file and **never writes them anywhere else**: the errors above carry codes, counts, line and position, element names and owner ids; the logs carry the batch, the
  owner ids, counts and currencies.
- **The file is encrypted at rest.** `WireBatches.XmlContent` (the payment instruction, with the IBAN, the name and the address of every owner) and
  `WireBatches.DebitAccount` (the IBAN of the agency) are `EncryptedColumns` entries with the purpose `Casazen.WireBatch.File`; the columns are text, so they keep their type
  and the converter writes a longer text in them. A row stored in clear before this change is still read (it is read as it is) and is re-encrypted at startup
  (`EncryptedColumns.EncryptLegacyPlaintextAsync`, idempotent). The proof is `WireBatch_XmlEncryptedAtRest_Test`: the stored text is a Data Protection payload with none of
  the IBANs, names or addresses, and the file read back through EF is the one written, to the byte.
- The list of the batches never reads the file or the debit account (the columns are chosen one by one), and its type has no member that could carry them.
- The end-to-end id and the message id carry ids of CasaZen (a payout, a batch), never a name or an account.

## Data and migration

- Migration `AddWireBatchPayouts`: the table `WireBatchPayouts` (one row per payout of a batch: `EndToEndId`, the net `Amount`, the owner; unique on the payout, unique on the
  `EndToEndId` in its batch, positive amount), two columns on `WireBatches` (`ExecutionDate`, `MessageFormat`) and the column `DebitAccount` from `varchar(34)` to `text` (it
  holds a Data Protection payload now). `XmlContent` keeps its type. No flag, no configuration, no seed.
- **Rollback.** Reverting the deploy is safe for the code, but the previous code cannot read an encrypted `XmlContent` or `DebitAccount`: it would hand the payload out as
  the file. Roll back only before a batch exists (the test environment has none), or decrypt and regenerate the batches. `Down` drops `WireBatchPayouts` and the two new
  columns and shortens `DebitAccount` again, which fails on a row that holds a payload longer than 34 characters, on purpose: nothing is cut in silence.

## Known limits

- The statement flow is gone (422 `wire_batch_from_statement_retired`); the frontend wire page of before sends `statementIds` and `debitAccount` and has to move to
  `payoutIds` and `executionDate` (its own change).
- A batch cannot be cancelled, and a payout that was **reversed after** it was put in a batch is not taken out of it: the reversal posts the opposite ledger lines and the
  file is unchanged. A batch that is not paid yet and pays such a payout is therefore **neither downloaded, nor confirmed as downloaded, nor marked as paid**: 422
  `wire_batch_payouts_reversed`, with the ids of the payouts in `payoutIds` (`WireBatch_PayoutReversedAfterTheBatch_IsNeitherDownloadedNorMarkedPaid_Test`); a batch that
  was paid stays readable as evidence. The other payouts of such a batch stay in it (a payout is in one batch only), so they are paid outside CasaZen until cancelling a
  batch (and freeing its payouts) exists, which is a later change; a file that was already uploaded to the bank cannot be recalled from here.
- The execution date is independent of the `PaidOn` day of the payouts (the day the withholding was decided on); the bank file asks for the date of the request.
- EUR is still the SEPA `pain.001.001.03` message, written as before (the name of the creditor alone, `NOTPROVIDED` agents), until E16/F01/US02.
- Only Swiss and Liechtenstein IBANs are paid in CHF (a domestic payment); cross-border CHF payments need creditor agent data this file does not carry.

## Tests

`Pain001V09Tests` (no database, no network): the four proofs of E16/F01/US01/T01 (`Pain001V09_ChfBatch_ValidAgainstOfficialXsd_Test`, skipped until the official schema is in;
`WireBatch_AmountIsPayoutNet_Test`; `WireBatch_XmlEncryptedAtRest_Test`; `WireBatch_ConfirmOtherOrganization404_Test`), the Swiss message and its element order, the owners
(`WireBatch_OwnerWithoutAddressOrIban_IsRefusedWithTheOwnerIds_Test`, `WireBatch_CreditorIban_IsACreditorAccountOfTheFile_OrRefused_Test`), the debtor, the payouts
(`WireBatch_PayoutAlreadyInABatch_IsRefused_Test`, `WireBatch_PayoutsThatCannotBePaid_AreRefused_Test`), the execution date, the retired request, the list without IBANs
and the texts and IBANs of the file. `Pain001ValidationProofTests`: an invalid file is refused and no batch row is written (`WireBatch_InvalidXml_NeverSaved_Test`), the
amounts and the lines of the file (`WireFile_AmountsAreExactDecimals_SumAndCountMatchTheRows_Test`, `Pain001Checks_ReportABrokenRule_ByTheNameOfTheElement_Test`), the
currency, the empty list, the official schema (`WireBatch_OfficialSchema_ValidatesTheFile_OrTheBatchIsRefused_Test`), the loader that compiles the embedded files
(`Pain001SchemaProvider_Load_CompilesTheFiles_AndSaysWhyWhenItCannot_Test`, with stand-in files: a good one, none, a broken one, one with a DTD, another schema, the schema of the
other message) and the permission. Until the official files are in, the tests that need to get past the validation give the service a schema of their own
(`Pain001TestSchemas`: an accepting one and a rejecting one for both messages, stand-ins that are not copies of the official schemas).
