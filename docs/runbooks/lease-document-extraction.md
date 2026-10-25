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

Limits (as the certificate's, `LocalOcr`): at most **60 pages** in the file, at most **12 pages read by OCR** (the rest are left out and
`ContractText.Truncated` says so), **110 seconds** in all (the wait for a slot counts), **two OCR runs at a time** per API process shared with the
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

- **an imported draft, document of kind `Contract`**: parser → `ILeaseDraftService.ApplyContractReadingAsync` (the row lock, the one writer; `long-rent-lease-draft.md` §16). `ExtractionResult` is
  `{ version: 2, source: embedded|ocr|mixed, pages, pagesOcr, truncated, stipulaDate, fields: [{ field, partyId, role, state, confidence, derived }] }`: **field names and fates, never a value** (the day of the signature is the one date);
- **an imported draft, any other kind** (an attestation, a receipt): `Skipped`, not read;
- **any other lease** (a lease that exists; a ConcordatoFacile draft is an imported draft since CF-IMPORT, so it goes through the full reading above): the first pipeline of ADR-009 as it was (a few fields by simple rules, written to the lease only when every one is certain and none
  contradicts it), with the new text reader and a result of `{ version: 1, fields: [{ name, confidence }], conflicts: [{ field, reason }] }` — **no value** (before, the names of the people were kept here in clear).

| `ExtractionStatus` | Meaning |
|---|---|
| `Processing` | Set by the upload, until the job ends |
| `Completed` | Something was read (draft: and nothing contradicts what was typed) |
| `NeedsReview` | The contract says something else than what was typed (`Conflict` in `fields`), or the first pipeline found a conflict |
| `Failed` + `ExtractionError` | `file_missing`, `unreadable` (not a PDF that opens, over 60 pages), `ocr_unavailable`, `no_text`, `nothing_found`, `error` (unexpected: the job is tried again). Never a message, never a word of the document |
| `Skipped` | Not read: another kind of document of an imported draft, a duplicate (same SHA-256 on the same lease), or the draft was imported or deleted while it was read |

`GET /api/leases/{id}/draft/reading` summarizes them for the screen. A single photo uploaded to `POST /api/leases/{id}/documents` is turned into a one-page PDF first (`PhotoDocumentPdf`, as for the documents of a
property); each document is read on its own and applied to the draft in the order they finish, so the pages of one contract sent as several files fill the draft together.

## 4. After the deploy

- The image needs `poppler-utils`, `tesseract-ocr` with `ita` and `eng`, and `util-linux` (`prlimit`): the Dockerfile has them (APE). Without them a
  digital PDF is still read and a scan answers "unavailable".
- A contract read in a few seconds is a digital one; a scan of eight pages takes about a minute.

## 5. Not here (follow-ups)

- **NL-DOCS-FU1** companies as parties (11-digit code, registered office, legal representative).
- **NL-DOCS-FU2** nationalities other than the Italian one (a table with the ISO codes, checked by the owner of the product).
- **NL-DOCS-FU3** the unit's data (surface, cadastral data, APE of the contract), the guarantor, the ISTAT clause, the expenses.
- **NL-DOCS-FU4** a rent that changes over the years, and a deposit stated only in words.
- **NL-DOCS-FU5** a comparison with the register of the municipality for the address (today only with the property chosen).
