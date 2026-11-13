# Fiscal documents of guests and direct bookings (epic E06)

What CasaZen issues, how the numbers, the amounts and the FatturaPA XML are made, and where the official schema comes from. Requirements NF-83,
NF-84, NF-87; ADR-019 (amended), ADR-033-R7 (retention).

## Boundary

- **Only guest and direct-booking documents.** CasaZen issues documents only for the direct bookings of an organization whose issuer is CasaZen
  (`IDocumentIssuerService`, mode `CasaZenDirectOnly`, flag `Features:FiscalDocumentIssuance`, off by default). Never for an OTA booking, never where
  Octorate is the issuer (its documents are imported read-only).
- **Owner statements are not fiscal documents.** A rendiconto (owner statement) is a management report: it has no fiscal series or number, never
  draws from the counter of the fiscal numbers and never produces FatturaPA XML. **No agency-to-owner XML is generated**: an invoice from the agency
  to an owner is issued by the agency's external fiscal system and only referenced (E06/F01/US02/T05, `docs/runbooks/owner-accounting.md`).
- **No real SDI sending.** No intermediary is chosen (PO-06): an issued invoice has the state `to_send` and its XML can be downloaded; nothing is
  transmitted by CasaZen.

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

- A fiscal document is kept ten years (ADR-033-R7): the booking, the credited and the replaced document are restrict foreign keys; the erasure of a
  guest leaves the recipient snapshot of an issued document in place.
- The recipient snapshot and the XML are encrypted at rest (`Casazen.FiscalDocument`, `docs/runbooks/encryption.md`).
