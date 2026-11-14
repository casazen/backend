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

## Tax rules (`FiscalDocuments:TaxRules`, E11/F01/US01/T02)

The rules of the documents are one effective-dated catalog in `appsettings.json` (`FiscalTaxRulesOptions`, D2), which replaced the rules per regime
(`FiscalDocuments:Rules`) and the stamp duty of the section. A rule set has a `Jurisdiction` (`IT` or `CH`), a `Regime` of that country, `ValidFrom` and
an optional `ValidTo` (both days included), a `Version` (stored on every document, `FiscalDocument.RuleVersion`), a `SourceUrl`, a `DocumentKind`
(`Receipt` or `Invoice`), a VAT rate for **every** line type (`Rates`: `Percent` and, for an Italian 0%, its `Nature`), and in Italy the codes of
FatturaPA (`FatturaPaRegimeCode`, `TD01`/`TD04`), the `StampDuty` (`Threshold`, `Amount`, `ChargedToRecipient`) and the nature of an exempt stay
(`StayExemptNature`).

- **Which rule.** `IFiscalTaxRuleResolver` picks the set of the country and the regime of the issuer whose range holds the date: the **check-out** of
  the stay for a document of a guest (a stay that ended before a new version keeps the old rate), the last day of the period for an invoice to an
  owner (E14). Then the choices of the issuer: `ExemptArt10` gives the stay the exempt nature (`N4`, Italian ordinary only), and the cleaning that
  `FollowsStay` takes the VAT of the stay (the rate of `Cleaning` is the one of the cleaning as a service). No rule = 422 `document_rule_not_configured`.
- **Checked at startup** (`ValidateOnStart`): overlapping ranges of one country and regime, a line type without a rate, an Italian 0% without a
  nature, a nature with a rate above 0, a Swiss rule with a nature, a stamp duty or an exempt nature, a receipt with VAT, a repeated version, a kind
  other than receipt or invoice: the application does not start and the message names the rule set (`FiscalDocuments:TaxRules:{index} ({version} ...)`).
- **Adding a version** (never edit a published one: the documents keep its name): close the old set with `ValidTo` the day before, add a new set from
  the first day with a new `Version` and its `SourceUrl`, ship it with the deploy. An empty catalog starts and nothing is issued.
- **Shipped defaults (⚖, prudent and configurable, to be confirmed by an accountant):**

  | Version | Country, regime | Document | Rates |
  |---|---|---|---|
  | `ch-vat-2024.1` | CH `SwissVatRegistered`, from 2024-01-01 (F1) | Invoice | stay 3.8; cleaning 3.8 with the stay (C2) or 8.1 as a service; extras, commission, recharges, other 8.1; tourist tax and advanced expenses 0, outside the VAT |
  | `ch-novat-2024.1` | CH `SwissNotRegistered`, from 2024-01-01 | Receipt | every rate 0, no VAT shown |
  | `it-private-2026.1` | IT `PrivatePerson`, from 2026-01-01 | Receipt | 0 `N2.2` (internal only), tourist tax and advanced expenses 0 `N1`; stamp duty 77.47 / 2.00 (I4) |
  | `it-ordinary-2026.1` | IT `VatOrdinary`, from 2026-01-01, `RF01` | Invoice | stay 10, or 0 `N4` when exempt (I1); cleaning with the stay or 22; extras 22 (I2); commission, recharges, other 22; tourist tax 0 `N1` (I3); advanced expenses 0 `N1` (art. 15); stamp duty 77.47 / 2.00 |
  | `it-flatrate-2026.1` | IT `VatFlatRate`, from 2026-01-01, `RF19` | Invoice | 0 `N2.2`; tourist tax and advanced expenses 0 `N1`; stamp duty 77.47 / 2.00 (I5) |

  Choices of this delivery beyond the brief, to confirm with the rest of the table: the line type `Other` takes the normal rate (8.1 in CH, 22 in IT
  ordinary); the advanced expenses are `N1` (art. 15) in every Italian regime; the Italian ordinary rule keeps the stamp duty the section had for every
  regime (due only above 77.47 of amounts without VAT: an exempt stay, a large tourist tax). The Swiss source URL is the page of the Federal Tax
  Administration on the VAT rates (F1: to confirm on estv.admin.ch).
- **Recipient.** A Swiss document (any kind) and an Italian receipt need only the name of the recipient (the address is printed when there is one, an
  Italian fiscal code is never asked); an Italian invoice keeps the checks of FatturaPA (the fiscal code of a person or the country abroad; the VAT
  number of a company with its SDI code or PEC).
- **Swiss documents** have no nature code, no stamp duty, no XML (`FatturaPaXml` null) and the SDI state `not_applicable`; their tourist tax line reads
  «Tassa di soggiorno (non soggetta all'IVA)».
- **The preview** `GET api/fiscal/documents/issuer-preview` (without `bookingId`: the organization) and `GET api/fiscal/documents/settings` return the rule
  in force today for the profile (`rule`: version, dates, source, kind, the rate of every line type, the stamp duty): the summary of the wizard.

## Swiss document (`SwissFiscalDocumentPdf`, MWSTG art. 26, E11/F01/US01/T03)

`FiscalDocumentPdfs` lays out the document of an issuer whose snapshot is in Switzerland (`CountryCode` `CH`) with the Swiss layout (MigraDoc through
`IPdfDocumentRenderer`, embedded DejaVu fonts), in the language asked for (the guest's; Italian by default, the endpoint passes the language of the
request). It carries the data of art. 26:

- **Issuer**: legal name, address and, for a registered issuer, «N. IVA CHE-123.456.789 IVA» (IT) / «VAT no. CHE-123.456.789 VAT» (EN), from the
  snapshot (the issuer as it was at the issue).
- **Recipient**: the name, plus the address when there is one (never an Italian fiscal code or VAT number).
- **Title** «Fattura» / «Ricevuta» (IT), «Invoice» / «Receipt» (EN) with the number; the date of the document; the period of the stay
  (`ServicePeriodFrom`/`To`) and the property.
- **Lines**: description, quantity, unit price, amount and VAT rate (`3.8%`, `8.1%`; `—` for a line outside the VAT); the tourist tax is always labelled
  «Tassa di soggiorno (non soggetta all'IVA)» / «Tourist tax (not subject to VAT)».
- **VAT per rate**: base, rate and VAT, the stored VAT of the lines of each rate; then the taxable total, the VAT and the total in CHF, written with the
  it-CH culture and its apostrophe pinned (`1'234.50`).
- **Payment**: «Pagata tramite il portale» when the portal collected (`PaymentChannel` `Portal`), «Pagata» when the money is collected, «Da incassare»
  otherwise; never the name of a portal or of a supplier.
- **Not registered**: a receipt without the VAT column, without the summary and with the note «Non soggetto all'IVA» (⚖ wording, the resource
  `FiscalDocPdfChNotVatRegistered`).

A Swiss document never shows the Italian courtesy copy, nature codes, stamp duty or SDI wording. The labels are the resources `FiscalDocPdf*` (the
Italian layout's) and `FiscalDocPdfCh*`, in Italian and English. The Italian layout shows no nature code on a receipt (they stay internal).

## Amounts (`FiscalDocumentTotals`, computation `fiscal-totals-2026.11-1`)

- The lines come from what the guest paid for the booking, **VAT included**, each with its type and the VAT the rule gives it: lodging
  (`BasePrice - CleaningFee`, `Accommodation`), cleaning (`CleaningFee`, `Cleaning`) and the tourist tax (`TouristTax`, outside the VAT).
- Per VAT rate (and nature code): taxable `T = round(G × 100 / (100 + rate), 2)` half away from zero, VAT `G − T`: the total of the document is what
  the guest paid. The net of each line is computed the same way; a cent of difference goes to the largest line of the group. The VAT of each line
  (`FiscalDocumentLine.VatAmount`, its gross less its net) is stored: the summaries of the PDF and of the XML add up the stored VAT of the lines of
  each rate (`FiscalDocumentTotals.SummaryOf`), never `taxable × rate` again, so a document with several rates adds up to its total in the XML too.
- Stamp duty: only under a rule that has one (Italy, never a CHF document): due when the amounts without VAT exceed its `Threshold` (77.47), of its
  `Amount` (2.00); added to the total only with `ChargedToRecipient` (off: the issuer bears it).
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
- The series: the pattern of the profile (`GuestDocumentSeries`, `{year}` is the year of the issue), the year when it has none.

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
