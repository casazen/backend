# Fiscal documents (epics E06 and E11)

What CasaZen issues, how the numbers, the amounts and the FatturaPA XML are made, and where the official schema comes from. Requirements NF-83,
NF-84, NF-87, NF-112, NF-113; ADR-019 (amended twice), ADR-033-R7 (retention).

## Boundary

- **CasaZen is the only issuer (decision of the PO of 2026-11-14, E11/F01/US01/T01).** CasaZen issues the document of every active booking of
  every source: direct, manual, local and the portals imported through Octorate (`IDocumentIssuerService`, flag `Features:FiscalDocumentIssuance`, off
  by default, on in test). A booking that is not active (a request, a hold, cancelled, a no-show: the one definition of the code) gets no document.
  Nobody issues outside CasaZen; the accountant only receives the exports.
- **Retired modes.** `DocumentIssuerMode` keeps its numbers for the stored rows: `CasaZen = 2` (formerly `CasaZenDirectOnly`) is the only mode;
  `Octorate = 1` and `Accountant = 3` are `[Obsolete]`, the migration `AddSoleIssuerModel` moved every profile to 2 and `PUT api/fiscal/documents/settings`
  answers 422 `issuer_mode_retired` for them (the old name `CasaZenDirectOnly` is still read as `CasaZen`).
- **Octorate documents never block.** The import of the documents Octorate issued stores them read-only and links them to their booking; it never
  locks the issuer of a booking and never raises `two_issuers_detected`. Its job selects no organization unless `FiscalDocuments:ImportOctorateDocuments`
  is `true` (default `false`), so it never runs by accident. A booking a legacy row locked to Octorate before the decision keeps that lock (Octorate
  issued it, CasaZen never issues it twice: 409 `document_issuer_conflict`, reason `issuer_locked_octorate`). The write-back of the billing data to
  Octorate is unchanged and inert: it runs only for such a booking.
- **Owner statements are not fiscal documents.** A rendiconto (owner statement) is a management report: it has no fiscal series or number, never
  draws from the counter of the fiscal numbers and never produces FatturaPA XML. The invoice of the agency to an owner, with lines, is a fiscal
  document of CasaZen again (purpose `OwnerInvoice`, epic E14): the model already has its owner, statement version, period and the unique index of one
  live invoice per owner and period.
- **No real SDI sending.** No intermediary is chosen (PO-06): an issued invoice has the state `to_send` and its XML can be downloaded; nothing is
  transmitted by CasaZen.

## Issuer (`OrgFiscalIssuerProfile`, E11/F01/US01/T01)

One profile per organization, saved by the wizard (`PUT api/fiscal/documents/settings`, the whole org, `payment.read` + `property.write`):

- **Country and regime.** `CountryCode` `IT` (default) or `CH` decides the valid regimes: `PrivatePerson`, `VatOrdinary`, `VatFlatRate` in Italy,
  `SwissVatRegistered`, `SwissNotRegistered` in Switzerland; a regime of the other country is 422 `issuer_regime_country_mismatch`. `VatRegistered`
  follows the regime (a contradicting value is 422 on `vatRegistered`).
- **Italy** keeps its checks: province, the FatturaPA regime code and the VAT number of the organization (VAT regimes) or its fiscal code (private person).
  **Switzerland** needs no Italian tax id: a registered issuer gives its UID, `SwissVatNumber`, accepted with or without separators or the suffix
  `MWST`/`TVA`/`IVA` and stored as `CHE-123.456.789`; the format and the check digit are verified (eCH-0097 v5.2.0 § 2.4.2: weights 5, 4, 3, 2, 7, 6,
  5, 4, modulus 11, a result of 10 is never assigned; example `CHE-109.322.551`). The Italian codes of a Swiss profile and the UID of an Italian one are
  dropped.
- **Bank accounts** `Iban` (mod 97, not a QR-IBAN) and `QrIban` (an IBAN of CH or LI whose institution id is 30000 to 31999), **encrypted at rest**
  (`Casazen.OrgFiscalIssuerProfile`).
- **Treatments** (⚖, prudent configurable defaults): `StayVatTreatment` `Standard` (default) or `ExemptArt10` (I1); `CleaningFeeTreatment` `FollowsStay`
  (default, C2) or `Service`.
- **Series** `GuestDocumentSeries` (default: the year) and `OwnerInvoiceSeries` (default `PM{year}`): at most 10 characters, letters, digits, `. _ / -`
  and the placeholder `{year}`.
- **Issue and delivery** `AutoIssueGuestDocuments` and `EmailGuestDocuments` (both off); `PlatformCollectedTouristTaxChannels` (the `BookingSource`
  names whose portal collects the tourist tax, jsonb; empty for Italy).
- **Italian reminders** `StampDutyReference` (the authorization of the virtual stamp or the number of the physical stamp, ⚖ I4),
  `ConservationAgreementConfirmed`, `VirtualStampConfirmed`.
- **Draft or confirmed.** Every save needs the flag on (422 `document_issuance_not_available`). A profile that cannot issue yet is saved as a draft and
  `missingFields` says what is left; `confirmed: true` with something missing is 422 `issuer_profile_incomplete`, and `ConfirmedAt` is set only when
  nothing is missing.

## The issuer as it was (`FiscalDocument.IssuerSnapshot`)

At the issue the document keeps the issuer as JSON (`FiscalIssuerSnapshot`: legal name, structured address, Italian VAT number and fiscal code of the
organization, Swiss VAT number, regime, country, FatturaPA regime code, IBAN masked), **encrypted at rest** like the recipient, and the version of its
rule (`RuleVersion`). The PDF and the XML of an issued document read the issuer from the snapshot; only a draft, and a document issued before the
snapshot existed, show the profile of today. The document also keeps its `Purpose` (`GuestStay`, `OwnerInvoice`, `SelfBilling`), its property, the
period of the stay (`ServicePeriodFrom`/`To`, from the check-in and the check-out) and its `PaymentChannel` (`Unknown` until E12 fills it). Its lines
carry a `LineType` (`Accommodation`, `Cleaning`, `ExtraService`, `TouristTax`, `Commission`, `Recharge`, `AdvancedExpense`, `Other`); the stored lines
were migrated (the tourist tax line, recognized by its fixed description, became `TouristTax`; the others `Accommodation`).

## Official schema (precondition of the XML)

The XML is validated against the official FatturaPA schema before it is stored (ADR-019-R6). The schema files are **not** written or rebuilt by
hand: they are the files the Agenzia delle Entrate publishes, committed as they are under `Casazen.Infrastructure/Fiscal/Schemas/` and embedded
in the assembly (`Casazen.Infrastructure.csproj`, logical names `Casazen.Infrastructure.Fiscal.Schemas.<file>`).

| File | Source | SHA-256 |
|---|---|---|
| The FatturaPA schema in force, format FPR12 (namespace `http://ivaservizi.agenziaentrate.gov.it/docs/xsd/fatture/v1.2`) | Agenzia delle Entrate, site of the electronic invoice (fatturapa.gov.it), page «Formato FatturaPA» | to record when the file is added |
| The XML signature schema it imports (`xmldsig-core-schema.xsd`) | the same publication | to record when the file is added |

**State of this build: the files are not in the repository yet.** They could not be taken from the publication while this change was written (no
network access was allowed to the delivery). Until they are added, `FatturaPaSchemaProvider.HasOfficialSchema` is false and every issue of an
invoice answers 422 `fatturapa_schema_unavailable`: no XML is ever produced without the validation. Issuance is off anyway (`Features:FiscalDocumentIssuance`).

**Adding or updating the schema (operator):**

1. Download the schema files from the publication of the Agenzia delle Entrate (the page above), never from a mirror.
2. Put them in `Casazen.Infrastructure/Fiscal/Schemas/` unchanged (no reformatting: the checksum must be the one of the published file).
3. Record in the table above the exact URL of each file, the version and the SHA-256 (`sha256sum <file>`).
4. Run `FatturaPaProofTests`: with the files, `FatturaPa_Xml_ValidatesAgainstOfficialSchema_Test` builds a document and validates it, and checks that
   an altered one fails.

The schemas are compiled without a resolver (the import of the signature schema is satisfied by the embedded file, never fetched); the documents
are read with the DTD prohibited and no resolver (`XsdValidator`): no external entity, no network.

## Amounts (`FiscalDocumentTotals`, rule `fiscal-totals-2026.11-1`)

- The lines come from what the guest paid for the booking, **VAT included**: lodging (`BasePrice - CleaningFee`), cleaning (`CleaningFee`) and the
  tourist tax (`TouristTax`) as a line without VAT with the nature code of the rule (`TouristTaxNatureCode`).
- Per VAT rate (and nature code): taxable `T = round(G × 100 / (100 + rate), 2)` half away from zero, VAT `G − T`: the total of the document is what
  the guest paid. The net of each line is computed the same way; a cent of difference goes to the largest line of the group.
- Stamp duty: due when the amounts without VAT exceed `FiscalDocuments:StampDutyThreshold` (77.47), of `FiscalDocuments:StampDutyAmount` (2.00);
  added to the total only with `FiscalDocuments:StampDutyChargedToRecipient` (off by default).
- Credit notes: the amount (the whole document by default) is spread over the lines of the credited document in proportion; never more than the
  document minus what was already credited. The cap (and «one document per booking» for an invoice or a receipt) is checked once before the transaction
  (a quick refusal) and **again inside it, right after the number is reserved**, under the lock of the series (a credit note takes the series of its
  original, so the same lock): two requests at the same moment never both pass (`FiscalDocumentCreditPostgresTests`).
- A document is issued only for an **active** booking (the one definition of the code, `AlloggiatiStatusRules`: a status that owes the police communication
  and no no-show): a request, a hold, a cancelled booking or a no-show gets no draft, no number and no lock of the issuer (422 `booking_not_active`). A
  credit note of an issued document is not refused for the state of its booking (it is what is done after a cancellation).
- The payment state (`Paid` / `ToCollect`) of a document CasaZen issued is the **current** one, computed when it is read (the completed and partially
  refunded payments of the booking, net of the refunds, against the total) by the document API and by the payment ledger; the value stored with the draft is
  only a starting point. A document Octorate issued keeps the state Octorate gave it.
- The rule of a regime (`FiscalDocuments:Rules`): kind of document, VAT rate, nature codes, `TD01`/`TD04`, `RF01`/`RF19`, series pattern. Nothing is
  issued with a rule the accountant did not approve (`document_rule_not_configured`).

## XML (`FatturaPaXmlBuilder`)

- Written with `XDocument`/`XmlWriter`, never by concatenation: every text is escaped; decimals in invariant culture (two places, unit prices and
  quantities up to eight); days as `yyyy-MM-dd` (Europe/Rome calendar days).
- `IdTrasmittente`: the issuer's own fiscal id (no intermediary configured). `CodiceDestinatario`: the recipient's code; `0000000` with the PEC or
  with nothing (the recipient's fiscal drawer); `XXXXXXX` for a recipient outside Italy.
- `CedentePrestatore` from the issuer profile (`OrgFiscalIssuerProfile`) and the organization (VAT number, fiscal code); `CessionarioCommittente`
  from the recipient snapshot of the document (a company with `Denominazione`, a person with `Nome` and `Cognome`).
- A document that does not validate is 422 `fatturapa_xml_invalid`: the message names the number of errors and the line:position of the first ones,
  never a value of the document; the log carries ids and counts only.

## Numbers (`FiscalNumberingService`)

- Per org and series, consecutive from 1, no gap, no duplicate: the counter (`FiscalNumberSequences`) is incremented under the advisory lock
  `FiscalNumbering` (1_601) in the READ COMMITTED transaction that saves the document; a rollback gives the number back.
- At the first use of a series the counter starts above the highest number already used in it, the legacy `AgencyInvoices` included.
- A series has at most 10 characters (the display `{series}/{number}` fits the 20 characters of `Numero`): longer is 422 `series_too_long`.

## Export of the list (`FiscalDocumentExports`, `XlsxWriter`)

- `GET api/fiscal/documents/export?format=csv|xlsx`: the issued documents (CasaZen's and the imported ones) that the caller reaches, the same rows as the list, in a
  file named `documenti-yyyy-MM`; the same fourteen columns in both formats: kind, number, issue date, booking code, property, recipient, taxable base, VAT, stamp
  duty, total, currency, payment state, SDI state, source.
- The amounts are the stored ones, to two places (a credit note keeps its positive amounts: its kind says what it is); the payment state is the current one, as in the
  list. The recipient is the name only: no fiscal code, VAT number or address leaves in a file. Drafts, discarded drafts, the lines and the XML are not in it, and the
  channel manager is never named (its documents say «Emesso dal tuo channel manager»).
- The Excel file is written by `XlsxWriter` (no package, only `System.IO.Compression`): amounts are numbers, days are dates, every text is an inline string, so a text
  that starts with `= + - @` is never run (the CSV puts an apostrophe before it). More than 9,999 documents is 422 `csv_too_many_rows` or `xlsx_too_many_rows`: narrow the period.

## Retention and privacy

- A fiscal document is kept ten years (ADR-033-R7): the booking, the property, the owner, the statement version, the credited and the replaced document
  are restrict foreign keys; the erasure of a guest leaves the recipient snapshot of an issued document in place.
- The recipient snapshot, the issuer snapshot and the XML are encrypted at rest (`Casazen.FiscalDocument`, `docs/runbooks/encryption.md`); so are the
  IBAN and the QR-IBAN of the issuer (`Casazen.OrgFiscalIssuerProfile`). The logs carry ids, codes and field names, never a value.
