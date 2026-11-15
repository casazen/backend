# Wire transfers to the owners: the pain.001 files (epics E06 and E16, feature F01)

How an agency pays its owners with a bank file, what the file is made of, and how it is validated before a batch is saved (E06/F05/US01/T01, E16/F01/US01/T01,
E16/F01/US02/T01). Requirements NF-25 («validazione pain.001») and NF-113 (Swiss documents and payments, `pain.001.001.09.ch.03` with structured addresses, validated with
the official schema); ADR-017 (the amounts of a statement are rounded once, at their total), ADR-010 and ADR-012 (agency plan, privacy of the IBAN). The owner accounting
that surrounds the wires, and the payout a wire pays, are in [`owner-accounting.md`](owner-accounting.md).

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
- **The reads of the wire page** (FIX/RES-PAYOUTS): the page asks the server which payouts can still be paid and which ones a batch pays, so that after a reload a payout that
  is already in a batch is not offered again. `GET api/wire-transfers/payable-payouts?currency=CHF` (or `EUR`) lists the payouts a file of that currency can still pay, the
  ones `POST api/wire-transfers/batch` accepts: **confirmed** (never reversed), of a statement version **published** to the owner, with a **net amount above zero** and **in no
  batch**, oldest first (the order of the transfers of a batch), at most 1000. **One currency per list**, never mixed (a code stored in lower case is the same currency, as a
  batch reads it); `currency` is required, and a missing one or one with no wire file is 422 `wire_currency_not_supported`: there is no list of every currency.
  `GET api/wire-transfers/batch/{id}/payouts` gives a batch as the list of the batches shows it (`batch`, with its state `Generated`, `Downloaded` or `Paid`) and its
  `payouts`, with the same members, in the order of the transfers of the file; a payout reversed after its batch was made is in it and says `Reversed`, and a batch of before
  the payouts (made from statements) has none. Each payout has `payoutId`, the owner (`ownerId`, `ownerName`), `periodYear` and `periodMonth` of its statement, `amount` (the net:
  the amount of the transfer), `currency`, `confirmedAt`, `status` and `ownerHasIban`, **a yes or a no and never the IBAN**, no address, no debit account, no file (the
  batch is read by the columns of the list, so the file is not even decrypted). Both reads need `agency.wire.manage` (the policy of the controller, which neither action overrides: `WireReads_AreBehindTheWireManagePolicy_Test`) **and the whole org**: a caller limited to some
  properties gets 403 (`WireReads_PropertyLimitedCaller_IsForbidden_Test`), the org is asked on every table (a batch of another org is a 404, the payouts of another org are never
  listed: `WireBatchPayouts_OfAnotherOrganization_AreNotFound_Test`), and the answers are not cached (`Cache-Control: no-store`: they hold the names of the owners).
- **The organization is checked explicitly** on every endpoint above (the service filters on the org of the caller, a `Find` by key would not apply the global
  tenant filter): a batch of another org is a **404**, and its state does not move (`WireBatch_ConfirmOtherOrganization404_Test`).
- All of it needs the agency permission `agency.wire.manage` (`WireTransferController`), held by the **owner and the administrator** of an org on the agency plan
  and by no other role (`OrgRoleCatalog.AgencyPermissionsOf`). Both reach every property of the org, so there is no property scope to apply: a collaborator, the
  one role that can be limited to «Solo alcuni» properties, never sees or validates a wire. The proof is
  `WireTransferPermission_IsHeldOnlyByTheRolesThatReachEveryProperty_Test`, which fails when a role is given the permission by mistake.
- There is no flag: the wire feature is gated by the agency plan and the permission, and a file cannot be produced until the official schema of its currency is in
  the build (below).

## The two messages, and the cutoff of the old one

| Currency | Message (`WireBatch.MessageFormat`) | Namespace | Writer | Schema folder |
|---|---|---|---|---|
| CHF | `pain.001.001.09.ch.03` (Swiss Payment Standards, ISO 20022 `pain.001.001.09`) | `urn:iso:std:iso:20022:tech:xsd:pain.001.001.09` | `Pain001V09Writer`, flavor `Swiss` | `Payments/Schemas/Swiss/` |
| EUR | `pain.001.001.09` (ISO 20022, with the rules of the SEPA credit transfer of the EPC) | `urn:iso:std:iso:20022:tech:xsd:pain.001.001.09` | `Pain001V09Writer`, flavor `Sepa` | `Payments/Schemas/Iso/` |

The two messages are written in **the same namespace** (the Swiss schema restricts the ISO one and keeps it): the namespace of a file never says which schema it was
validated against, the currency does (`Pain001SchemaProvider`), and the files of the two folders are told apart by their name.

- **Cutoff.** The Swiss payment standards' parallel phase ends with the November 2026 release: after it only `pain.001.001.09.ch.03` is valid and
  `pain.001.001.03.ch.02` is never built again (`fatturazione/analisi-tecnica.md` §4). CasaZen therefore **no longer writes the Swiss `pain.001.001.03` file** (the one
  with the service level `URGP`): CHF is the `.09.ch.03` message since E16/F01/US01/T01. **EUR is the `pain.001.001.09` message since E16/F01/US02/T01**: CasaZen no
  longer writes the SEPA `pain.001.001.03` file either (`WriteSepa` is gone, and so is the `.03` schema folder). Both are fed by the payouts.
- **CBI is not built (⚖ T3).** Many Italian corporate banking channels prefer the CBI format. CasaZen writes the ISO 20022 / EPC file, which the banks that take
  `pain.001.001.09` accept; a bank that takes only CBI cannot take the file as it is. Building the CBI message is a follow-up of the plan (it is listed in the pull request
  of E16/F01/US02/T01), not a part of this one, and nothing in CasaZen calls it CBI.
- **The request of before is refused.** `POST api/wire-transfers/batch` with `statementIds` and a `debitAccount` is still read, so the client gets a code and not an
  error of shape, and it is refused with **422 `wire_batch_from_statement_retired`**; any `statementIds` or `debitAccount` in the request is refused, so an account the
  caller typed is never ignored in silence.
- **Batches of before.** A batch made by the statement flow keeps its stored file (`pain.001.001.03` in both currencies, `MessageFormat` empty, `ExecutionDate`
  empty) and its `WireBatchStatement` rows; it is still listed, downloaded and confirmed, and when it is paid the ledger still posts it
  (`OwnerAccountingService`). The batches of the payout flow post nothing when they are paid: the payout is already in the ledger. A **EUR batch made by E16/F01/US01/T01**
  (before the EUR file moved) has `MessageFormat` `pain.001.001.03` and keeps its stored file: it is listed and handed out byte for byte as it was written, never written or
  validated again (`WireBatch_EurBatchOfBeforeTheMove_KeepsItsStoredFile_Test`). The `format` of a EUR batch of the payout flow is `pain.001.001.09` from now on.

## The Swiss file (`Pain001V09Writer`)

- **Group header:** a unique `MsgId` (`CZ` and the id of the batch without hyphens, 34 characters), `CreDtTm` (Europe/Rome clock, no offset), the number of
  transactions and the control sum of the whole file, the initiating party (the agency).
- **One `PmtInf` per requested execution date** (the API takes one date, so one payment; the writer keeps the rule of the message): its own unique id, `TRF`, count and
  control sum, `ReqdExctnDt/Dt`, the **debtor** (the agency: name and structured address from the issuer profile), its IBAN and the debtor agent: the **institution
  identifier** of the debit IBAN (characters 5 to 9 of a Swiss or Liechtenstein IBAN) as a member of the Swiss bank clearing (`ClrSysMmbId`, `ClrSysId/Cd` `CHBCC`),
  because the Swiss schema takes a BIC or a clearing member there and no `Othr`; an IBAN that carries no institution identifier leaves the bank unnamed (an empty
  `FinInstnId`, which the schema also takes: the bank reads the account from the IBAN). Batch booking is on.
- **One `CdtTrfTxInf` per payout:** the unique `EndToEndId` (`CZ` and the id of the payout without hyphens, 34 characters), the amount in the currency with two
  decimals, the **creditor** (the owner: the legal name, or the first and last name, and the structured address of the owner registry, E13), the creditor IBAN and the
  remittance **«Rendiconto MM/AAAA»** (the period of the statement version of the payout). No creditor agent: a domestic IBAN identifies the bank.
- **Structured addresses only:** `StrtNm`, `BldgNb` (when there is one), `PstCd`, `TwnNm`, `Ctry`. Never an address in free lines. An owner is complete when the street,
  the postal code, the town and the country (two letters) are there; the building number is optional.
- **Texts:** every text is brought to the characters the Swiss messages accept (letters of Latin-1, digits, space and `/ - ? : ( ) . , ' + &`; other accented letters lose
  their accent, signs become spaces, what has no equivalent a point) and cut to the length of its element (`Nm` and `StrtNm` 70, `BldgNb` and `PstCd` 16, `TwnNm` 35,
  `Ustrd` 140). The XML escaping is the writer's. A name, a street, a postal code or a town that is nothing but points and signs after that (a name in Cyrillic, Greek or
  Chinese, which the message cannot write) is not a name or an address (`Pain001Text.HasContent`): the owner is refused like one with no name, in both files.
- **Amounts** are exact decimals (never a floating point number), invariant culture, two decimals; the control sum is a sum of the amounts as written.
- The element order of the message follows the ISO 20022 sequences; `Pain001V09_ChfFile_IsTheSwissMessageWithStructuredAddresses_Test` keeps it checked on its own, and
  `Pain001V09_ChfBatch_ValidAgainstOfficialXsd_Test` checks the whole file against the official schema.
- **Confirmed against the official schema** (FIX/WIRE-XSD, 2026-11-15; the schema is in the build, see below): the Swiss file validates, with one change of the writer.
  The first validation refused the debtor agent the writer had, `FinInstnId/Othr/Id` = `NOTPROVIDED` (the way the EUR file writes it): the Swiss schema restricts the
  debtor agent to a BIC (`BICFI`), a clearing member (`ClrSysMmbId`) or a LEI and has no `Othr` there («invalid child element `Othr`, expected `BICFI, ClrSysMmbId,
  LEI`»). The writer changed, never the schema: the bank of the debit IBAN is named by its institution identifier as above (`Pain001V09Writer.DebtorAgent`; proofs
  `Pain001V09_ChfFile_IsTheSwissMessageWithStructuredAddresses_Test` and `Pain001V09Writer_SwissDebtorAgent_IsTheInstitutionOfTheIban_OrNoBank_AndValidates_Test`).
  Nothing else of the group header or of the payments is required that the file does not write. The schema is the part of the Swiss standard in the repository (the
  implementation guidelines of SIX are not): what a bank asks beyond it is confirmed with the bank before relying on the file.

## The EUR file (`Pain001V09Writer`, flavor `Sepa`)

The EUR file is the same ISO 20022 `pain.001.001.09` message as the Swiss one (group header, one payment per execution date, one transfer per payout, structured addresses
only, the same amounts, sums, ids and remittance), with **the rules of the SEPA credit transfer of the EPC** on top of it, the ones the ISO schema leaves open:

- **Service level `SEPA`:** every payment has `PmtTpInf/SvcLvl/Cd` = `SEPA`, written after the control sum and before the requested execution date (the order of the ISO
  sequence).
- **Charge bearer `SLEV`:** every payment has `ChrgBr` = `SLEV`, written after the debtor agent and before the transfers. Neither is in a transfer.
- **BIC optional, so none is written:** the debtor agent is `FinInstnId/Othr/Id` = `NOTPROVIDED` (the bank reads it from the IBAN) and there is **no creditor agent at all**.
  The `Bic` of the owner registry is not used by the file: an IBAN of the SEPA area is reached with the IBAN alone, and the one case where a BIC is needed, an IBAN outside
  the SEPA area, is refused (below).
- **Structured addresses:** the debtor (the agency) and every creditor (the owner) are written with `StrtNm`, `BldgNb` (when there is one), `PstCd`, `TwnNm` and `Ctry`,
  never with free lines alone. The rules also accept the **hybrid** form (a town, a country and up to two address lines): CasaZen does not write it, because the owner
  registry keeps the address in structured fields (E13), but the check of the finished file accepts it. An owner paid in EUR needs a complete address like one paid in CHF
  (`wire_owner_address_missing`): the EUR file of before wrote the name alone.
- **Characters:** the Latin characters of the SEPA messages, letters, digits, `/ - ? : ( ) . , ' +` and the space. An accented letter loses its accent (`è` is `e`), `ß` is
  `ss`, `Æ` is `AE`, `Ø` is `O`, the ampersand is the plus sign, and what has no equivalent is a point (`Pain001Text`, flavor `Sepa`). The Swiss message keeps the letters of
  Latin-1 with their accent and the ampersand.
- **Currency:** every `InstdAmt` is `EUR`. The SEPA area is the one the owner registry already defines (`OwnerFieldRules.IsSepaIban`): an owner whose IBAN is not in it is
  refused for the batch with 422 **`wire_owner_iban_not_sepa`** and the owner ids (a transfer to that account is not a SEPA credit transfer), after the checks on the missing
  address, the missing IBAN, the check digits and the QR-IBAN, which are the same as in CHF.
- **What a schema cannot say is checked on the finished file** (`Pain001Checks`, before anything is saved): `SvcLvl`, `ChrgBr` and `PstlAdr` (a postal address that is
  structured or hybrid, which is a town, a country and at most two free lines) of the debtor and of every creditor, by the name of the element and never by a value
  (`wire_batch_content_invalid`).
- **Confirmed against the official schema** (FIX/WIRE-XSD, 2026-11-15; as for the Swiss file): the EUR file validates as the writer wrote it, with no change (the position of
  `PmtTpInf` and `ChrgBr`, the debtor agent `Othr/Id`, which the ISO schema takes). `Pain001V09_EurBatch_ValidAgainstIsoXsd_Test` writes a batch, validates it against the
  official schema and checks that an altered file does not validate; if a change of the writer breaks it, it prints every error of the schema with its line and
  position, and the writer is what changes, never the schema.

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
| 11 | Every owner has a complete structured address (both files write it), an IBAN, that has the length of its country and passes the check digits (the definition of the owner registry, `OwnerFieldRules.IsValidIban`), that is **not a QR-IBAN** (the account of a QR-bill, never the creditor account of a normal transfer: institution 30000 to 31999 of a Swiss or Liechtenstein IBAN) and that is in the area of the file: in CHF Swiss or Liechtenstein (a CHF transfer to a foreign IBAN is not a domestic payment, which this file does not cover), in EUR in the SEPA area (`OwnerFieldRules.IsSepaIban`: a EUR transfer to another IBAN is not a SEPA credit transfer, which this file does not cover). One refusal at a time, in this order, listing every owner that fails it | `wire_owner_address_missing`, `wire_owner_iban_missing`, `owner_invalid_iban`, `wire_owner_qr_iban`, `wire_owner_iban_not_swiss`, `wire_owner_iban_not_sepa` |
| 12 | The file validates against the **official schema of its currency** (`Pain001SchemaProvider`, `XsdValidator`: EUR the ISO `pain.001.001.09.xsd`, CHF the Swiss `pain.001.001.09.ch.03.xsd`) | `wire_batch_xml_invalid` (the number of errors and the line and position of the first five), `wire_batch_schema_unavailable` |
| 13 | The file is what its lines say (`Pain001Checks`, in the namespace of the message of the currency): the control sum and the number of operations of the header and of the payment are the sum and the count of the transfers, and the batch's; every amount is positive with two decimals and has the currency of the batch written next to it; every end-to-end id is there and is unique. **In EUR also the rules of the SEPA credit transfer:** every payment has the service level `SEPA` and the charge bearer `SLEV`, and the debtor and every creditor have a structured or hybrid address | `wire_batch_content_invalid` (the names of the broken ISO elements: `CtrlSum`, `NbOfTxs`, `InstdAmt`, `Ccy`, `EndToEndId`, and for EUR `SvcLvl`, `ChrgBr`, `PstlAdr`) |

The text of a schema error can quote a value of the file, so it is never used: only the position is.

## Official schemas (precondition of check 12)

The schemas are **not** written or rebuilt by hand: they are the files their publishers give, committed unchanged and embedded in the assembly
(`Casazen.Infrastructure.csproj`, logical names `Casazen.Infrastructure.Payments.Schemas.<Iso|Swiss>.<file>`). Each currency has its own folder and its own cached
schema set; a set is accepted only if it has the namespace of the message CasaZen writes for that currency (`Pain001SchemaProvider.NamespaceOf`), because otherwise the
validation would only warn that it has no schema for the root element and the file would pass. The two messages share their namespace, so a file in the wrong folder would
compile all the same: the file names are fixed (`pain.001.001.09.xsd` in `Iso`, `pain.001.001.09.ch.03.xsd` in `Swiss`) and
`Pain001SchemaProvider_EmbeddedFiles_CompileOrSayWhy_Test` fails when a folder holds anything else.

| File | Folder | Version | Source (taken on 2026-11-15) | SHA-256 |
|---|---|---|---|---|
| `pain.001.001.09.xsd`, the ISO 20022 schema of the customer credit transfer initiation (`CustomerCreditTransferInitiationV09`), target namespace `urn:iso:std:iso:20022:tech:xsd:pain.001.001.09` (EUR) | `Casazen.Infrastructure/Payments/Schemas/Iso/` | message `pain.001.001.09`, in the message set «Payments Initiation V10» of the ISO 20022 catalogue (release of 2019-01-01, archived; the file says «Generated by Standards Editor (build:R1.6.15) on 2019 Feb 14» and the publisher serves it dated 2020-02-13) | ISO 20022 Registration Authority, message archive (the current catalogue lists only a newer version of the message, `pain.001.001.13`): page <https://www.iso20022.org/catalogue-messages/iso-20022-messages-archive>, message set «Payments Initiation V10», business area `pain`, message `pain.001.001.09`, link «XSD»; the file is <https://www.iso20022.org/message/14346/download>, served as `pain.001.001.09.xsd` (the XSD of the message itself: no archive, no implementation guide) | `de038b373e47b0077b1832ddd81f4b2f1eb25d35721f62da1e38b7f5a09fda24` (56,918 bytes) |
| `pain.001.001.09.ch.03.xsd`, the schema of the Swiss Payment Standards for the customer credit transfer (CHF), target namespace `urn:iso:std:iso:20022:tech:xsd:pain.001.001.09` | `Casazen.Infrastructure/Payments/Schemas/Swiss/` | `ch.03`, as published with the Swiss Payment Standards 2026 (SPS 2026): Implementation Guidelines for Credit Transfers, version 2.3, published 2026-02-20, in force from 2026-11-14 (the file says «(C) Copyright 2021, SIX» and the archive dates it 2022-03-07) | SIX Group, page <https://www.six-group.com/en/products-services/banking-services/payment-standardization/standards/iso-20022.html> (reached from <https://www.six-group.com/en/products-services/banking-services/payment-standardization/swiss-payments.html>), link «XML schema» of the Implementation Guidelines for Credit Transfers (SPS 2026); SIX offers the file only inside the archive <https://www.six-group.com/dam/download/banking-services/standardization/sps/ig-credit-transfer-xml-schema-sps-2026-en.zip> (7,146 bytes, SHA-256 `bb02cc24bda59d04e7ad51a1706d68901a4517b0be8a1da55b055390e5e35f68`), whose only entry is this file | `8daf0973bc1d8eaec5b494214d843d1dd4ccfe10b2bf61739004fa0c9f30d675` (74,674 bytes) |

**State of this build: both files are in the repository** (FIX/WIRE-XSD, added on 2026-11-15: the Swiss one of E16/F01/US01/T01 and the ISO one of E16/F01/US02/T01). Each
is the file its publisher gives, taken on that day from the address in the table (the SIX one out of its archive; these two files and nothing else, with the explicit
permission of the operator, whose step it was) and committed unchanged: `.gitattributes` marks `Payments/Schemas/**/*.xsd` as `-text`, so the bytes are the ones of the
SHA-256 above on every machine. Before they were added each was read as text, never executed: well-formed XML, root `xs:schema`, target namespace
`urn:iso:std:iso:20022:tech:xsd:pain.001.001.09`, no `xs:import`, `xs:include` or `xs:redefine` (the provider has no resolver, so a schema has to be self-contained: both
are), and a schema set that compiles without a resolver. `Pain001SchemaProvider.HasOfficialSchema(currency)` is therefore true for EUR and for CHF, and **a wire batch of
either currency is validated against the official schema of its currency before anything is saved** (check 12) instead of answering 422 `wire_batch_schema_unavailable`. That
answer remains for a build that lacks the files, on purpose (a made-up success would put an unchecked payment file in the hands of the agency): the same rule as
`fatturapa_schema_unavailable` for the electronic invoices (`fiscal-documents.md`). Who is affected: the orgs on the agency plan, whose wire endpoints now produce a file.
The wire page of the frontend is behind the flag `agencyTier` (off by default); the backend endpoints are **not** behind a feature flag (`WireTransferController` has no
`FeatureGate`: only the agency plan and the permission).

What the proofs do now: `Pain001V09_ChfBatch_ValidAgainstOfficialXsd_Test` and `Pain001V09_EurBatch_ValidAgainstIsoXsd_Test` are no longer skipped: each writes a batch with
the service, validates the file against the official schema of its currency (no stand-in) and checks that an altered file does not validate (the EUR one also the SEPA rules
of `Pain001Checks`); `WireBatch_OfficialSchema_ValidatesTheFile_OrTheBatchIsRefused_Test` takes its validating branch in both currencies;
`Pain001SchemaProvider_EmbeddedFiles_CompileOrSayWhy_Test` checks that each folder holds exactly its file and that both compile; and `Wire_ThePayoutFile_ValidatesAgainstTheOfficialSwissSchema`
of the acceptance scenario of the Swiss agency (a PostgreSQL test, so in CI) validates its CHF file against the official schema too. The EUR file validated as the writer
wrote it; the Swiss one needed one change of the writer, the debtor agent (see «The Swiss file»).

**Adding or updating a schema (operator):**

1. Download the file from the publication of its publisher (the pages above), never from a mirror or a package.
2. Put it in the folder of its currency unchanged (no reformatting, no change of line endings: `.gitattributes` marks `Payments/Schemas/**/*.xsd` as `-text`
   so the bytes, and the checksum, are the same on every machine). Nothing a schema refers to is ever fetched (no resolver): the files must be self-contained.
3. Record in the table above the exact address of the file, its version and its SHA-256 (`sha256sum <file>`).
4. Run `Pain001V09Tests` and `Pain001ValidationProofTests`: with the file in, `Pain001V09_EurBatch_ValidAgainstIsoXsd_Test` (or the CHF one) stops being skipped, writes a
   batch, validates the file against the official schema and checks that an altered file does not validate; `Pain001SchemaProvider_EmbeddedFiles_CompileOrSayWhy_Test`
   fails with the reason (`Pain001SchemaProvider.LoadProblem`) when a file is embedded and does not compile, has another target namespace or is not the file of its folder.
5. If the file CasaZen writes does not validate, the writer is what has to change, not the schema: that is what the validation is for. When a file does not validate, the
   proof of step 4 fails with the code and the arguments of the refusal and with the line, position and text of every error of the schema on the same file (written with
   invented data): correct `Pain001V09Writer` from that. Banks accept the versions their publisher lists: confirm with the bank which one it accepts before relying on the
   file.

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
- EUR is the ISO 20022 `pain.001.001.09` message with the SEPA rules of the EPC since E16/F01/US02/T01. It is **not CBI** (⚖ T3): a bank that takes only the CBI format of the
  Italian corporate banking channels cannot take it as it is, and the CBI message is a follow-up of the plan, not built.
- Only Swiss and Liechtenstein IBANs are paid in CHF (a domestic payment); cross-border CHF payments need creditor agent data this file does not carry.
- Only IBANs of the SEPA area are paid in EUR (`wire_owner_iban_not_sepa`); an owner on another IBAN is paid outside this file, because that payment needs the BIC and the
  charges of a cross-border transfer, which a SEPA credit transfer does not carry. The SEPA area is the list the owner registry keeps (`OwnerFieldRules`): a country that joined
  the scheme after it was written is refused until the list is updated there, never paid in a file that says `SEPA` by mistake.
- The EUR file never writes a BIC and never writes the hybrid form of the address, though the rules allow both (the IBAN alone is enough, and the registry has the structured address).

## Tests

`Pain001V09Tests` (no database, no network): the four proofs of E16/F01/US01/T01 (`Pain001V09_ChfBatch_ValidAgainstOfficialXsd_Test`, against the official Swiss schema;
`WireBatch_AmountIsPayoutNet_Test`; `WireBatch_XmlEncryptedAtRest_Test`; `WireBatch_ConfirmOtherOrganization404_Test`), the proof of E16/F01/US02/T01
(`Pain001V09_EurBatch_ValidAgainstIsoXsd_Test`, against the official ISO schema), the Swiss message and its element order, the debtor agent of the Swiss file against the
official schema (`Pain001V09Writer_SwissDebtorAgent_IsTheInstitutionOfTheIban_OrNoBank_AndValidates_Test`), the EUR message and its element order
(`Pain001V09_EurFile_IsTheSepaMessageWithStructuredAddresses_Test`, `Pain001V09Writer_EurPaymentsHaveTheSepaRules_Test`), the characters of the SEPA texts
(`Pain001Text_Clean_SepaCharacters_AreTheLatinSetOfTheEpc_Test`), the SEPA rules checked on the finished file
(`Pain001Checks_SepaRulesOfTheEurFile_AreCheckedByTheNameOfTheElement_Test`, `Pain001Checks_HybridAddressAndAnOptionalBic_AreSepaToo_AndTheSwissFileIsNotAsked_Test`), the owners
(`WireBatch_OwnerWithoutAddressOrIban_IsRefusedWithTheOwnerIds_Test`, `WireBatch_EurOwnerWithoutAddress_IsRefusedWithTheOwnerId_Test`,
`WireBatch_CreditorIban_IsACreditorAccountOfTheFile_OrRefused_Test`, with the IBAN outside the SEPA area), the EUR batch of before
(`WireBatch_EurBatchOfBeforeTheMove_KeepsItsStoredFile_Test`), the debtor, the payouts
(`WireBatch_PayoutAlreadyInABatch_IsRefused_Test`, `WireBatch_PayoutsThatCannotBePaid_AreRefused_Test`), the execution date, the retired request, the list without IBANs
and the texts and IBANs of the file. `Pain001ValidationProofTests`: an invalid file is refused and no batch row is written (`WireBatch_InvalidXml_NeverSaved_Test`), the
amounts and the lines of the file (`WireFile_AmountsAreExactDecimals_SumAndCountMatchTheRows_Test`, `Pain001Checks_ReportABrokenRule_ByTheNameOfTheElement_Test`), the
currency, the empty list, the official schema (`WireBatch_OfficialSchema_ValidatesTheFile_OrTheBatchIsRefused_Test`), the loader that compiles the embedded files
(`Pain001SchemaProvider_Load_CompilesTheFiles_AndSaysWhyWhenItCannot_Test`, with stand-in files: a good one, none, a broken one, one with a DTD, another schema, the schema of the
`pain.001.001.03` message CasaZen no longer writes), the names of the embedded files and the permission. The other tests that need to get past the validation give the
service a schema of their own (`Pain001TestSchemas`: an accepting one and a rejecting one, the same for both currencies because the two messages share a namespace, stand-ins
that are not copies of the official schemas): they test the rules of the service and not the file, which the proofs above check against the official files.

`WireBatchPostgresTests` (a real PostgreSQL database, so in CI only): two requests for the same payouts at the same moment make one batch (the lock of the owner accounting),
and a payout that another batch takes between the checks and the save of a request is refused by the unique index of `WireBatchPayouts` with 422 `payouts_already_in_batch`
and nothing left behind; the in-memory database of the unit tests applies neither the index nor the lock.

`WirePayableReadsTests` (no database, no network): the reads of the wire page, one proof for each rule (`PayablePayouts_ExcludeThoseAlreadyInABatchAndReversedOnes_Test`,
`PayablePayouts_AreNeverMixedAcrossCurrencies_Test`, `WireBatchPayouts_OfAnotherOrganization_AreNotFound_Test`, `WireReads_PropertyLimitedCaller_IsForbidden_Test`, `WireReads_AreBehindTheWireManagePolicy_Test`,
`WirePayouts_ShowOnlyWhetherTheOwnerHasAnIban_Test`). `WirePayableReadsNpgsqlTranslationTests`: both reads are translated to SQL by the Npgsql provider (no server), with the
org asked of every table.
