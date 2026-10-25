# Lease documents: reading a contract on the server (NL-DOCS)

Task NL-DOCS of the redesign wave. The owner who already has the signed contract uploads it (a PDF, or photos of its pages put in one PDF by the
browser) and CasaZen fills in the data by reading it, asking only what is missing. This runbook is the **reading**: the text of the document and what
is read from it. How the values enter the draft is [`long-rent-lease-draft.md`](long-rent-lease-draft.md) §16 and the final import with the PDF already uploaded is
[`long-rent-lease-import.md`](long-rent-lease-import.md) §11. The upload of the originals is [`lease-documents.md`](lease-documents.md); the
multi-page photos and the OCR of the energy certificate are [`documenti-multipagina.md`](documenti-multipagina.md). ADR-009 is the decision of the
first pipeline (Helix); this task replaces its reader and its parser.

**Nothing leaves the host.** No external service and no LLM reads a contract: it holds names, fiscal codes and addresses, and no subprocessor is
approved for them. The text is read in the API process (PdfPig) and by the tools of the image (`pdftoppm`, `tesseract` Italian and English, run under
`prlimit`). The text, the names, the codes and the addresses are never logged (ADR-009-R10): a log carries ids, counts and status codes.

## 1. The text (`ContractTextReader`)

| Case | How it is read |
|---|---|
| A digital PDF (the text is in the file) | Page by page with PdfPig (`GetWords`), in process: no tool, no wait |
| A scan, or the PDF of the photos of a contract | A page with fewer than 60 letters and digits has no text of its own: it is rendered in gray (`pdftoppm -scale-to 2400`) and read by Tesseract (`ita+eng`, `--psm 3`, Sauvola thresholding for the uneven light of a photo), the same calls as the energy certificate |
| A PDF PdfPig cannot open (damaged, odd generator) | Read by OCR whole (`pdfinfo` gives the pages) or refused (`InvalidDataException`) |

Limits (`LocalOcr`; CF-DOCS-REAL raised two of them, because the one file that the guided service delivers — contract, attestation and calculation
sheet — has 14 scanned pages in the first real sample): at most **60 pages** in the file, at most **20 pages read by OCR** (12 before; the rest are
left out and `ContractText.Truncated` says so; the pages are then in `unreadablePages`), **240 seconds** in all (110 before; about 7 seconds a page; the wait
for a slot counts), **two OCR runs at a time** per API process shared with the
certificate reader, 1 MB of text per page file, every tool under `prlimit` (768 MiB, 60 s of CPU), a private temporary folder deleted on every way out.
When OCR cannot run (tools missing, busy, out of time) the pages that carry text are still read; only when nothing at all could be read the answer is
`ContractTextUnavailableException`. A photo is never read as such: the upload turns a single photo into a one-page PDF (`PhotoDocumentPdf`) and the
browser puts several photos in one PDF, so the reader only meets PDFs.

## 2. What is read (`LeaseContractTextParser`)

A pure function of the text (and of the address of the property of the draft): no I/O, no clock. The text is flattened to one line first, because a
contract wraps its sentences wherever the page ends. A value is returned only when the text states it next to a word that says what it is, with a
**confidence** from 0 to 100; **70 and above** is what a draft takes (`LeaseContractTextParser.AcceptedConfidence`); a field the text does not state,
or states in two different ways, is **not** returned — a wrong fiscal code or rent costs more than a field the owner types.

| Field | Read from | Notes |
|---|---|---|
| People (role, fiscal code, names, residence, citizenship) | A **fiscal code that passes the check character** (a person without one is not read); the name before it after the last "Sig./Sig.ra/Dott.…" or label ("Cognome e Nome:"); the role from "(di seguito Locatore)", "denominato Conduttore", "in qualità di…", or the heading right before the name | The first six letters of the code give the surname and the name, so "ROSSI MARIO" and "Mario Rossi" both come out right; two landlords or tenants under one plural definition share the role. A residence is taken only when something ends it (the next label, the fiscal code, the next section). Only "cittadinanza italiana" gives a citizenship (`IT`): any other is asked. Companies (11-digit codes) are not read |
| Fiscal code with an OCR confusion | A 0/O, 1/I, 5/S… in a place where only the other can be | Put right only if exactly one correction passes the check character; marked `Repaired`, confidence 75 |
| Type | "canone concordato", "3+2", "art. 2 comma 3" · "4+4", "art. 2 comma 1", "canone libero" · "transitorio" (not "non … transitorio"; at most 18 months) | The first lines weigh more than the rest; two types named → the one named first, 72; none named → 48 months = libero, 36 = concordato, marked worked out; the student lease is not a type of CasaZen: nothing |
| Tax regime | The sentence around "cedolare secca": "ha optato / si avvale" or "non ha optato", "regime ordinario" | A condition ("qualora opti…"), a mere reference, or a yes and a no in one contract say nothing |
| Monthly rent | An amount after "canone" / "rata mensile" with its period named ("mensile", "annuo", "al mese") and nothing in between that says it is another sum | The month's and the year's agreeing: 95; the year's alone ÷ 12: 78, **worked out**; two different monthly rents, or no period: not read |
| Security deposit | An amount after "deposito cauzionale" / "cauzione", or "N mensilità" (× the rent of the contract, or the rent read) | Marked worked out when it comes from months; two different amounts: not read |
| Start, end, length | "decorrenza / a decorrere dal …", "scadenza / fino al …", "dal … al …", "durata di 4 (quattro) anni / 12 mesi" | The end is worked out (start + length − 1 day) only when it is not stated, and marked so |
| Day of the signature | "data di stipula …", "stipulato il …", "Letto, confermato e sottoscritto … Milano, li 15/10/2026" | 88 / 80 / 72 |
| The property | The clause "immobile … sito in …" against the address of the property chosen | Every word of the street and its number there: match; none of the street's words: mismatch; else nothing |

## 3. What the job keeps (`ExtractionResult`) and its statuses

`ExtractLeaseDocumentDataJob` (Hangfire, 3 attempts) calls `LeaseDocumentExtractionService`: it refuses a document whose `OrgId` differs from its lease's (`tenant_isolation_violation`: the job runs with no org filter),
reads the file from the private bucket, the text, and then:

- **an imported draft, document of kind `Contract`**: the pages are told apart (§4), the **contract pages** go to the parser → `ILeaseDraftService.ApplyContractReadingAsync` (the row lock, the one writer; `long-rent-lease-draft.md` §16). `ExtractionResult` is
  `{ version: 3, kind: "contract", source: embedded|ocr|mixed, pages, pagesOcr, truncated, stipulaDate, sections: [{ kind, firstPage, lastPage }], containsAttestation, containsCalculationSheet, attestation: { verdict, read: [names] }, sheet: { verdict, read: [names] }, warnings: [{ code, pages }], fields: [{ field, partyId, role, state, confidence, derived }] }`: **field names, fates and codes, never a value** (the day of the signature is the one date);
- **a document of kind `Registration`, on any lease**: the receipt of the Agenzia delle Entrate (§6), read as a digital PDF with no OCR. `ExtractionResult` is `{ version: 3, kind: "registration", outcome: Registered|Rejected, pages, read: [names], warnings: [codes] }`;
- **an imported draft, any other kind** (an attestation, an identity document): `Skipped`, not read;
- **any other lease** (a lease that exists; a ConcordatoFacile draft is an imported draft since CF-IMPORT, so it goes through the full reading above): the first pipeline of ADR-009 as it was (a few fields by simple rules, written to the lease only when every one is certain and none
  contradicts it), with the new text reader and a result of `{ version: 1, fields: [{ name, confidence }], conflicts: [{ field, reason }] }` — **no value** (before, the names of the people were kept here in clear). A `Contract` document of such a lease (the contract that was missing, uploaded later) is read on its contract pages only and its result also has `sections`, `containsAttestation`, `containsCalculationSheet` and `warnings`.

| `ExtractionStatus` | Meaning |
|---|---|
| `Processing` | Set by the upload, until the job ends |
| `Completed` | Something was read (draft: and nothing contradicts what was typed; receipt: registered, and nothing disagrees with the lease) |
| `NeedsReview` | The contract says something else than what was typed (`Conflict` in `fields`), or the first pipeline found a conflict; a receipt that disagrees with the lease, or the Agenzia **discarded** the request |
| `Failed` + `ExtractionError` | `file_missing`, `unreadable` (not a PDF that opens, over 60 pages), `ocr_unavailable`, `no_text`, `nothing_found`, `error` (unexpected: the job is tried again). Never a message, never a word of the document |
| `Skipped` | Not read: another kind of document of an imported draft, a duplicate (same SHA-256 on the same lease), or the draft was imported or deleted while it was read |

`GET /api/leases/{id}/draft/reading` summarizes them for the screen (for each contract document also its `sections`, `containsAttestation`, `containsCalculationSheet` and `warnings`), and `GET /api/leases/{id}/documents` carries `sections`, `containsAttestation` and `containsCalculationSheet` on every contract document. A single photo uploaded to `POST /api/leases/{id}/documents` is turned into a one-page PDF first (`PhotoDocumentPdf`, as for the documents of a
property); each document is read on its own and applied to the draft in the order they finish, so the pages of one contract sent as several files fill the draft together.

## 4. A contract with its attestation and its calculation sheet (CF-DOCS-REAL)

The guided service delivers **one file**: the signed contract, the attestation of conformity ("Attestazione di Rispondenza", D.M. 16 gennaio 2017) and the
calculation sheet of the rent ("Scheda Riepilogativa Canone"), scanned, 14 pages in the first real sample (contract 1–6, attestation 7–10, sheet 11–14, each with
its own "Pag. N" numbering). The uploaded document is a `Contract` like any other; what is new is that the pages are told apart before they are read, because the
attestation and the sheet repeat the rent, the dates and the people with other meanings (the contract read as a whole text found two different rents and took none).

**The sections** (`ContractDocumentSections`, a pure function of the text of each page, `ContractText.PageTexts()`): every page gets a score for each kind. A **title at
the top** of the page weighs most ("Contratto di Locazione…", "Attestazione di Rispondenza", "Scheda Riepilogativa Canone"), looked for in the first 240 letters
with an approximate match (a letter wrong in six is tolerated: an OCR splits, glues and mistakes letters); phrases of the **body** weigh less ("Articolo 3 (…)" and
"Letto, approvato e sottoscritto" for the contract; "attestano", "Punteggio totale", "Elementi oggettivi" for the attestation; "Riepilogo degli importi", "Accordo e
parametri" for the sheet). The kind of each page is the path of the best total score where **changing kind costs** (a page with no sign of its own stays with the section it
follows, one word cannot split a section, a page whose title the OCR could not read is still put right by its body). The mention of the attestation in the last page of the
contract ("la rispondenza … è oggetto di Attestazione di Rispondenza separata") is not a title: only the top of a page is. A document with no sign of an attestation or a sheet is
one contract: **no section found is not an error**, "contract without attestation" is an honest state. Pages with fewer than 25 letters and digits are `unreadablePages` (and keep their section).

**Each part is read by its own reader**: the contract pages by `LeaseContractTextParser` (§2), the attestation by `AttestationReader` (municipality and province, the
territorial agreement and its date, the organizations of the owners and of the tenants, the verdict "rientrano nei valori previsti" / "non rientrano", the attested monthly rent
and the lowest and highest rent for the house, zone, sub-band, surface, points, the period and its length, the day next to "Luogo e data" when the OCR can read the handwriting,
the valid fiscal codes of the people) and the sheet by `CalculationSheetReader` (zone and OMI zone, sub-band, surface, the rent it works out, the lowest and highest rent, the outcome
"conforme all'Accordo"). What they read is used **in memory** to check the document (§5); what is kept with the document is the kind and the pages of each section, the
verdicts and the **names** of the fields read: no name, fiscal code, town, organization or amount.

What the parser of the contract learned from the real wording: the rent in "Il canone annuo di locazione, secondo quanto stabilito dall'Accordo … del 20/05/2025 …, è convenuto in EUR 7.140,00"
(a long clause with a date between the label and the amount) and in "n. 12 (dodici) rate eguali anticipate di EUR 595,00"; the deposit in "A garanzia delle obbligazioni … il conduttore versa … una somma
di EUR 1.785,00 … pari a n. 3 mensilità"; the **end of the lease is not the "scadenza" of the passport of the tenant or of the energy certificate** that the first page of the contract
describes ("…con scadenza 03/03/2030"); "denominata **conduttrice**" is a tenant (it was read as a landlord); "Locazione Abitativa Agevolata", "articolo 2, comma 3" are a canone concordato; the
day of the signature is not the day the territorial agreement "è sottoscritto in Milano il 20 maggio 2025", and "Letto, approvato e sottoscritto. Bollate, li 19-9-26" (two digits of year) is read.

## 5. What is checked: the warnings (`ContractDocumentValidator`)

The sections are checked for coherence with each other and with the draft. The answer is a list of **stable codes** (the frontend puts each in words; the server writes no sentence),
each with the pages it is about when it is about pages. A warning never blocks anything: the person goes on, with the data to check.

| Code | When |
|---|---|
| `pages_unreadable` (+ pages) | A page holds no readable text (blank, too dark, or beyond the 20 pages the OCR reads) |
| `pages_out_of_order` | The sections come in an unusual order, or one is split by pages of another |
| `fiscal_code_invalid` | A string shaped like a fiscal code in the contract fails its check character (that person is not read) |
| `attestation_not_conform` | The attestation says the rent does **not** conform to the agreement |
| `attestation_parties_differ` | The attestation names a person (valid fiscal code) the contract does not |
| `attestation_rent_differs` | The rent the attestation attests is not the contract's |
| `attestation_rent_out_of_range` | The contract's rent is outside the lowest–highest rent the attestation gives |
| `attestation_dates_differ` | The first or the last day in the attestation is not the contract's |
| `attestation_comune_differs` | The municipality of the attestation is not the property's |
| `attestation_sheet_differ` | The sheet and the attestation give another rent, zone, sub-band or surface |
| `sheet_not_conform` / `sheet_rent_differs` | The sheet's outcome is "non conforme" / the rent it works out is not the contract's |
| `term_too_short` | A canone concordato (or a document with an attestation) lasts less than 36 months |

Not checked (follow-up **CF-DOCS-REAL-FU1**): whether the attestation is **stamped and signed**. The stamps and the signatures are pictures, and the handwritten "Luogo e data" is
not reliably read by an OCR; a check from the image of the page (ink outside the text, below "Timbro e firma") needs a design of its own.

## 6. The receipt of the registration (`RegistrationReceiptReader`)

The Agenzia delle Entrate returns, when a lease is registered, a **digital PDF of three pages** (mod. RLI12): the notice of the file received ("registrati: 1 scartati: 0"), the
"Ricevuta di avvenuta registrazione contratti di locazione" with the office, the "Codice Identificativo del contratto", "registrato il … al n. … serie … e codice identificativo …", the figures of the
contract (duration, stipula, "Importo del canone", typology, the option for the cedolare secca) and the town of the house, and the list of the documents "acquisito" / "scartato".
A document of kind `Registration` is read like this: **in process, with no OCR** (`IContractTextReader.ReadDigitalAsync`; a scan or a photo is not read: `Failed` / `no_text`, and the person types
the number and the date as before). The reader (pure) returns the outcome — **a discarded request is `Rejected`, never a registration** —, the code, number and series, the day, the office, the
yearly rent, the period, the day of the stipula, the typology (and the type of contract it says), the cedolare secca and the town and province. No name, fiscal code or street is read.

`RegistrationReceiptCheck` compares it with the lease and answers codes, never a block: `receipt_rent_differs` (the year's rent is the month's times twelve), `receipt_start_differs`,
`receipt_end_differs`, `receipt_stipulation_differs`, `receipt_comune_differs`, `receipt_type_differs`, `receipt_regime_differs`.

**Nothing is declared by the reading.** What is kept with the document is the outcome, the names of the fields found and the codes (`ExtractionResult`, no value). The values go to the person only
when they ask: `GET /api/leases/{id}/documents/{documentId}/reading` (`lease.read`, `no-store`) reads the PDF again and answers `{ documentId, kind, state: Read|Unreadable|NotAReceipt, receipt: { outcome,
code, number, series, registrationDate, office, annualRent, start, end, stipulationDate, typology, cedolareSecca, comune, province, warnings } }`; a document that is not a `Registration` document of this lease
(another lease, another organization, another kind, a file that is gone) is 404 `document_not_found` and the file is not opened. The screen of the declaration (`POST /api/leases/{id}/registration/manual`,
which still wants the number and the date from the person) is filled with the code and the day, marked "read from the receipt — check it", and the person confirms.

## 7. After the deploy

- The image needs `poppler-utils`, `tesseract-ocr` with `ita` and `eng`, and `util-linux` (`prlimit`): the Dockerfile has them (APE). Without them a
  digital PDF is still read and a scan answers "unavailable".
- A contract read in a few seconds is a digital one; a scan of eight pages takes about a minute, the 14 pages of the contract with its attestation and its sheet about two.

## 8. Not here (follow-ups)

- **CF-DOCS-REAL-FU1** whether the attestation is stamped and signed (an analysis of the image of the last page of the attestation).
- **CF-DOCS-REAL-FU2** the receipt as a scan or a photo (OCR on the request, or in the job and the values kept encrypted).

- **NL-DOCS-FU1** companies as parties (11-digit code, registered office, legal representative).
- **NL-DOCS-FU2** nationalities other than the Italian one (a table with the ISO codes, checked by the owner of the product).
- **NL-DOCS-FU3** the unit's data (surface, cadastral data, APE of the contract), the guarantor, the ISTAT clause, the expenses.
- **NL-DOCS-FU4** a rent that changes over the years, and a deposit stated only in words.
- **NL-DOCS-FU5** a comparison with the register of the municipality for the address (today only with the property chosen).
