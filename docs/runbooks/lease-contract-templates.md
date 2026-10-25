# Runbook: lease contract templates (approval gate, clause texts from the product owner)

Task LT-03 (audit defect A7-03, P0). Decision D14: the legal texts come from the product owner; the code only gives
the structure of the contract and fills in the lease data. No clause is written in code or in this repository until
the product owner provides it.

Before LT-03 the base `appsettings.json`, valid in production too, marked every regime `Approved: true` with
`VersionId: dev-stub`, and the "contract" sent to signature was a 13-line draft that wrote "3+2" whatever the dates
and, for cedolare secca / regime ordinario, did not even name the parties.

> **Update 2026-10-08 (task LG-02).** The product owner decided that, **for now**, the agent writes the legal texts
> and the contract templates, as drafts that a lawyer reviews before any use. The repository therefore now holds three
> **draft, not approved** templates (`2026-11-bozza-v1`) and nothing approves or switches them on: see
> [Bozze 2026-11](#bozze-2026-11-draft-templates-task-lg-02), with the list of what the lawyer must check first.
> Everything else in this runbook (format, approval steps, behaviour) is unchanged.

## Behaviour

| Template state (per fiscal regime) | Final contract: `GET /api/leases/{id}/contract.pdf`, `POST signed-document` (offline signature, LT-02), `POST signing` (provider, off) | `GET /api/leases/{id}/contract/preview` |
|---|---|---|
| **Missing**: no `VersionId` or no file | `422 contract_template_not_approved` | PDF marked `BOZZA - template non approvato` (watermark `BOZZA` on every page), every section `[TESTO DELLA CLAUSOLA NON FORNITO]` with the lease data |
| **Incomplete**: a required section without text, a required placeholder not used, unknown placeholder, no title | `422 contract_template_not_approved` | same marker, provided texts shown, the rest marked |
| **NotApproved**: complete, but `Approved=false` or the approval is not valid | `422 contract_template_not_approved` | same marker, full text |
| **Approved** | PDF of the approved texts with the lease data; `422 contract_data_missing` if the template uses a datum the lease does not have | marked `ANTEPRIMA - documento non valido per la firma` (watermark `ANTEPRIMA` on every page) |

- **The one exception is a contract signed outside CasaZen** (LR-13, decision LR-D28): `POST /api/leases/{id}/import` brings in the PDF the parties signed with the
  owner's own model and needs no template, but only for a draft created as an imported contract (`origin: Imported`); a lease made in CasaZen is refused there
  (422 `lease_import_not_imported_origin`) and keeps this gate on every path above. See [`long-rent-lease-import.md`](long-rent-lease-import.md).
- The lease stays `Draft` on a 422: no signing session is created, no signed contract is accepted, no event is written.
  `GET /api/leases/{id}/signers` reports it in advance (`contractAvailable: false`, `contractUnavailableCode`), so the
  lease page disables download and upload and offers only the preview (LT-02).
- The messages are localized (`LeaseContractTemplateNotApproved`, `LeaseContractDataMissing` in `SharedResources.resx` / `.en.resx`); `contract_data_missing` lists the missing placeholders.
- The preview needs `lease.sign` on the lease (it carries the parties' full fiscal codes); another org's lease answers 404.
- Registration uses the PDF signed through the signing flow, so a contract generated from an unapproved template can
  never reach it.

Code: `Casazen.Core/Leases/` (structure, term, approval rules), `Casazen.Infrastructure/Services/LeaseContracts/`
(parser, catalog, document, startup validator), `Casazen.Infrastructure/External/LeaseContractTemplateService.cs`.
The PDF layout (A4, pagination, fonts, watermark) is described in the section "PDF (LT-09)" below.

## Where the files go

`Casazen.Web/LeaseTemplates/<Variant>/<VersionId>.md`, for example
`Casazen.Web/LeaseTemplates/CanoneConcordato/2026-11-avv-rossi-v1.md`.

- `<Variant>` is `CedolareSecca`, `RegimeOrdinario` or `CanoneConcordato` (the three fiscal regimes of the canone libero and concordato)
  or, since TR, `Transitorio` (the model of the transitory contract, whatever its tax regime). Exact spelling. The variant of a lease is
  `LeaseTemplateVariants.For(contractType, fiscalRegime)`: `Transitorio` for a transitory lease, otherwise the one that has the name of its
  fiscal regime.
- `<VersionId>`: letters, digits, `.`, `-`, `_`; never `dev-stub`. A changed text is a **new file with a new
  version**: the approval refers to one exact version.
- The files are committed (versioned by git, reviewed in a PR) and shipped with the API (`Casazen.Web.csproj` copies
  `LeaseTemplates/**/*.md` to the build and publish output). `LeaseTemplates:TemplatesDirectory` can point elsewhere
  (absolute or relative to the content root).
- The files are read once per process: redeploy or restart after a change.

## File format

UTF-8 text. Lines:

| Line | Meaning |
|---|---|
| `# <title>` | Title of the contract. Once, before the first section. Required. No placeholders |
| `## <section_id>` or `## <section_id> \| <heading>` | Starts a section. Without a heading the default heading of the table below is used |
| `<!-- ... -->` on one whole line | Comment, ignored (notes for the lawyer) |
| any other line | Clause text of the current section, line breaks kept |

Placeholders: `{{name}}` (spaces inside the braces allowed). An unknown name or a broken `{{` makes the template
incomplete. Sections can come in any order; sections not in the checklist are allowed (they are printed in file
order) but must have text. The heading of a section (after `|`) can carry placeholders like its text (the title cannot): the
transitory model's art. 2 is `Articolo 2 (Esigenza del {{esigenza_parte}})`, "del locatore" or "del conduttore" as the lease says.

Skeleton (the texts are for the product owner to provide; the placeholders shown are the required ones):

```
# <titolo del contratto>

## parti
<testo> {{locatori}} <testo> {{conduttori}}

## immobile
<testo> {{immobile_indirizzo}}

## durata
<testo> {{durata}} <testo> {{data_decorrenza}} <testo> {{data_scadenza}}

## canone
<testo> {{canone_mensile}} (oppure {{canone_annuo}})

## rinnovo_disdetta
<testo>
...
```

### Placeholders

Computed from the lease at generation time; nothing is written in the template or in code.

| Placeholder | Value | Example |
|---|---|---|
| `locatori`, `conduttori` | every party of that role: name surname (C.F. …), separated by `;` | `Mario Rossi (C.F. RSSMRA80A01H501U)` |
| `immobile_indirizzo` | address, postal code and comune of the property | `Via Roma 1, 20822 Seveso` |
| `immobile_comune` | comune of the property | `Seveso` |
| `data_decorrenza`, `data_scadenza` | start and end date of the lease | `01/09/2026` |
| `durata` | actual term from the dates, end date inclusive: whole years, otherwise months, then days | `4 anni`, `3 anni`, `18 mesi`, `2 mesi e 10 giorni` |
| `canone_mensile`, `canone_annuo` | monthly rent and 12 × monthly rent, **without currency** (write `euro {{canone_mensile}}`) | `1.200,00` |
| `dati_catastali` | cadastral data of the property (LT-10): sheet, parcel, subaltern when present, category and income; present only when sheet, parcel, category and income are all set | `Foglio 12, particella 345, subalterno 6, categoria A/2, rendita catastale euro 512,30` |
| `ape_estremi` | code and energy class of the **latest APE document** of the property (LT-10) | `codice 1510800012345, classe energetica B` |
| `deposito_cauzionale` | security deposit of the lease, **without currency** (LT-10) | `2.400,00` |
| `deposito_versamento` | the alternative "versa / non versa" of art. 5 of the transitory model, resolved by the deposit: `versa` above 0, `non versa` at 0; nothing while the deposit is not declared (TR) | `versa` |
| `deposito_mensilita` | the deposit in monthly rents (deposit / rent, two decimals at most): the "n. … mensilità del canone" of art. 5 of the transitory model (TR) | `2`, `1,5` |
| `esigenza_parte` | **Transitory contract only** (TR; D.M. 16/01/2017 art. 2 c. 4): who has the need that justifies the short term, `locatore` or `conduttore` | `conduttore` |
| `esigenza_motivo` | transitory only: the need as the landlord wrote it (at most 500 characters), on one line, without the full stop that would meet the comma of the sentence | `Lavoro a Milano da novembre ad aprile` |
| `esigenza_documentazione` | transitory only: the documentation attached, at most 300 characters. A contract that lasts more than 30 days (`LeaseContractTerms.TransitorioDocumentationAfterDays`) must have it (art. 2 c. 4). For one of 30 days or less the datum is the statement «nessuno, trattandosi di un contratto di durata non superiore a 30 giorni», because the model asks for the documents "in caso di durata superiore a 30 giorni" | `Lettera di assunzione` |

The deposit, the cadastral data and the APE are entered by the landlord (LT-10): the cadastral data on the property page
(`PUT /api/properties/{id}/cadastral`), the APE code and class on the APE document (`PUT
/api/properties/{id}/documents/{docId}/ape`), the deposit in the lease form (`securityDeposit`). Only lengths and, for
the energy class, 1-3 letters/digits/"+" are checked: no pattern is imposed on the codes. While a datum used by the
template is missing, the final contract answers `422 contract_data_missing` and the preview shows
`[DATO MANCANTE: …]`.

The need of a transitory contract (TR) is entered in the guided draft (`transitoryParty` and `transitoryNeed` at step 3,
`transitoryDocumentation` at step 4: [`long-rent-lease-draft.md`](long-rent-lease-draft.md)) and lives in the columns
`LeaseContracts.TransitoryParty`, `TransitoryNeed` and `TransitoryDocumentation`, where the contract reads it; the two texts are
**encrypted at rest** ([`encryption.md`](encryption.md)). A transitory contract made by the complete creation `POST /api/leases` (not used by
the interface any more) has none of them, so its final contract would answer `422 contract_data_missing`: the wizard is the way to make one.

**Transitorio leases** (LT-10, TR): a transitory contract (1-18 months) uses its own variant, `Transitorio`, whatever the tax regime of
the lease: the template of a fiscal regime is a 4+4 or 3+2 contract and is never used for it. The model is the **official Allegato B** of
the D.M. 16/01/2017 (file `Casazen.Web/LeaseTemplates/Transitorio/2026-11-bozza-v1.md`), **not approved** until the product owner sets the
variables of the variant: until then the final contract answers `422 contract_template_not_approved` and the preview is a BOZZA, as for
the other three. See [Transitorio, bozza 2026-11-bozza-v1](#transitorio-bozza-2026-11-bozza-v1-scelte-rispetto-allallegato-b). Student
leases are not modelled. The 2026-10-08 AI drafts of the transitorio and student contracts are kept as reference only, outside
`LeaseTemplates/`, in `docs/legal-drafts/leases/` (see [Bozze 2026-11](#bozze-2026-11-draft-templates-task-lg-02)).

### Required sections (checklist)

Every template of the regime must contain these sections with non-empty text; the required placeholders must appear
in the section. The references say **why** the section is there; they are not texts to copy. Classes: U official
source, T third-party source only, A audit finding (to be confirmed by the lawyer).

| Section id | Default heading | Regimes | Required placeholders | Reference |
|---|---|---|---|---|
| `parti` | Parti | all | `locatori`, `conduttori` | A7-03: the old generic draft had no parties (A) |
| `immobile` | Oggetto della locazione | all | `immobile_indirizzo` | A7-03: no cadastral data (A); the RLI filing needs the cadastral data (`docs/integrations/rli-esign.md`, U) |
| `durata` | Durata | all | `durata`, `data_decorrenza`, `data_scadenza` | Free rent: 4+4 (`.claude/context/regulations/canone_concordato.md`, intro). Canone concordato: L. 431/1998 art. 2 c. 3, at least 3 years (`canone_concordato.md`, "Durate e altre regole", U) |
| `rinnovo_disdetta` | Rinnovo e disdetta | all | — | A7-03 (A) |
| `recesso` | Recesso del conduttore | all | — | A7-03 (A) |
| `canone` | Canone | all | `canone_mensile` or `canone_annuo` | — |
| `accordo_territoriale` | Accordo territoriale | CanoneConcordato | — | L. 431/1998 art. 2 c. 3; D.M. 16/01/2017 (`canone_concordato.md`, U) |
| `attestazione_conformita` | Attestazione di conformità | CanoneConcordato | — | Required for non-assisted contracts after 30/03/2017 (Ris. AdE 31/E/2018); in Monza e Brianza issued jointly by one landlord and one tenant organisation (`canone_concordato.md`, U) |
| `aggiornamento_canone` | Aggiornamento del canone | all | — | Concordato MB: at most 75% of the FOI change, not while the cedolare option is active (`canone_concordato.md`, U). Cedolare secca: rent updates waived on long leases (`.claude/context/regulations/fiscale.md`) |
| `opzione_cedolare` | Opzione per la cedolare secca | CedolareSecca | — | D.Lgs. 23/2011 art. 3 (`canone_concordato.md`); no registration tax and stamp duty with the option (`fiscale.md` L4, U) |
| `deposito` | Deposito cauzionale | all | — | Concordato MB: at most 3 months' rent (`canone_concordato.md`, U); deposit paid by the tenant: no registration tax (`fiscale.md` L9, U) |
| `oneri_accessori` | Oneri accessori | all | — | A7-03 (A) |
| `ape` | Attestato di prestazione energetica (APE) | all | — | D.Lgs. 192/2005 art. 6 c. 3: in leases subject to registration a clause where the tenant declares to have received the information and documentation on the APE, with a copy of the APE attached (except for single units), and administrative fines if missing. **T**: checked only on third-party pages ([biblus.acca.it](https://biblus.acca.it/art-6-dlgs-192-05/), [studiolegaleberto.net](https://studiolegaleberto.net/art-6-del-d-lgs-n-192-del-2005/)); normattiva.it was not reachable. The lawyer confirms the current text |

`CedolareSecca` and `RegimeOrdinario` are free-rent (canone libero) leases, `CanoneConcordato` the agreed-rent lease.
Student leases are not modelled, so they have no template. The minimum term per lease type is validated by LT-10, not
here: the contract only states the actual term.

**The `Transitorio` variant has its own checklist** (TR; `LeaseContractTemplateStructure.RequiredSections(LeaseTemplateVariant.Transitorio)`),
because the D.M. 16/01/2017 makes the Allegato B the only model of a transitory contract, as a whole (art. 2 c. 7): the preamble
(`parti`: parties, unit, cadastral data and APE: placeholders `locatori`, `conduttori`, `immobile_indirizzo`, `dati_catastali`,
`ape_estremi`), the 17 articles (`durata` art. 1; `esigenza` art. 2 with `esigenza_parte`, `esigenza_motivo`, `esigenza_documentazione`;
`inadempimento` 3; `canone` 4 with `canone_mensile`; `deposito` 5 with `deposito_versamento`, `deposito_cauzionale`, `deposito_mensilita`;
`oneri_accessori` 6; `bollo_registrazione` 7; `pagamento` 8; `uso` 9; `recesso` 10; `consegna` 11; `modifiche_danni` 12; `assemblee` 13;
`impianti` 14; `accesso` 15; `commissione` 16; `varie` 17), the signatures (`firme`) and the specific approval of the clauses
(`clausole_approvate`). A template missing any of them is incomplete, so it cannot be approved.

## Approving a template (product owner)

1. Get the clause texts from the lawyer (or the signatory association for the concordato) for **every** section of
   the checklist of the regime.
2. Write the file `Casazen.Web/LeaseTemplates/<Variant>/<VersionId>.md` and open a PR.
3. On the **test** environment set only the version (Railway variables, Production environment):
   `LeaseTemplates__Variants__<Variant>__VersionId=<VersionId>`. Open a draft lease of that variant (for `Transitorio`, a transitory one)
   and download `GET /api/leases/{id}/contract/preview`: the header must say `stato: modello completo ma non approvato`, with no
   "Sezioni senza testo" and no "Problemi di formato". The API log line `Lease contract template <Variant>: …` lists
   the problems.
4. Have the lawyer review that exact file (the PDF of the preview and the file in the PR).
5. Approve, on test and then on production:
   - `LeaseTemplates__Variants__<Variant>__Approved=true`
   - `LeaseTemplates__Variants__<Variant>__ApprovalReference=<who approved, opinion or protocol number>` and/or
     `LeaseTemplates__Variants__<Variant>__ApprovedAt=<YYYY-MM-DD>`
6. Redeploy. The preview now says `ANTEPRIMA - documento non valido per la firma` and signing produces the final PDF.

Any change of text: new file, new `VersionId`, new review and approval. Never edit an approved file in place.

## Startup validation

Outside Development and Testing (both Railway environments run as `Production`) the API does **not start** when a
variant is declared `Approved=true` but:

- `VersionId` is empty, `dev-stub` or not a valid file name;
- neither `ApprovalReference` nor `ApprovedAt` is set;
- the file is missing or the template is incomplete.

The failure lists `LeaseTemplates:Variants:<Variant>: …` and Railway keeps the previous deployment. In Development and
Testing the same variant starts but counts as not approved (422). After deploying LT-03, check that no Railway
variable `LeaseTemplates__*` still carries the old `dev-stub` approval.

## PDF (LT-09)

Task LT-09 (audit defect A7-14, P1). Until LT-09 every PDF came from `FiscalPdfWriter`: one US Letter page, no
wrapping, text cut at 4000 characters, ASCII only ("Müller" became "M?ller"). It is removed; every PDF generated by
the API now goes through one service.

### What renders the PDFs

`IPdfDocumentRenderer` (`Casazen.Core/Documents/`), implemented by `MigraDocPdfDocumentRenderer`
(`Casazen.Infrastructure/Documents/`, singleton). The callers build a `PdfDocumentContent` (title, headings,
paragraphs, tables, optional watermark) and never lay out text themselves.

**One document at a time per process (BE-PDF-01).** PDFsharp and MigraDoc share state between documents (the global
font resolver, the caches of font faces and glyphs) and lose or garble words when two documents are built at the same
time: with 16 threads 13 to 60 of 192 PDFs came out incomplete, none with one thread. `Render` therefore takes a static
lock around everything that touches the libraries, whichever instance it is called on, and two contracts or reports
requested together are rendered one after the other (tens of milliseconds for an ordinary contract). The cost is a
waiting thread per concurrent request: if the volume of PDFs ever grows, generate them in a background job instead of
removing the lock. For the same reason every PDF goes through `IPdfDocumentRenderer`: PDFsharp or MigraDoc used
elsewhere would not be covered by the lock.

| PDF | Built by | Endpoint |
|---|---|---|
| Final contract | `LeaseContractTemplateService.GeneratePdfAsync` | `GET /api/leases/{id}/contract.pdf` |
| Contract preview (watermarked) | `LeaseContractTemplateService.GeneratePreviewPdfAsync` | `GET /api/leases/{id}/contract/preview` |
| RLI prefill | `RliExportService` | `GET /api/leases/{id}/rli/export` |
| IMU communication draft | `ComuneImuNotificationService` | `GET /api/leases/{id}/canone-concordato/imu-notification/export` |
| Fiscal reports | `FiscalService.ToPdf` | `GET /api/fiscal/reports/{annual,withholding}/{year}?format=pdf` |

No other PDF is generated in code: the APE, the signed contract and the RLI receipt are uploaded files, stored as
received. The APE inspector (`PdfLiteralTextExtractor`) only reads uploads.

Layout:

- A4 portrait (210 × 297 mm, 595 × 842 pt); margins 2 cm left, right and top, 2.5 cm bottom; footer
  `Pagina N di M`, centred. The documents are Italian, and so is the footer.
- Title, then headings (kept on the same page as the paragraph that follows) and paragraphs wrapped to the page
  width and split across pages. In a plain-text body a blank line starts a new paragraph and a single line break is
  kept, the same rule as the template files. **No length limit.**
- Tables: header row repeated on every page, cell text wrapped inside the cell. A row is never split across pages, so
  a single row must fit one page: long text goes in paragraphs, not in cells.
- Watermark: the preview has one on **every page**, light grey, diagonal, behind the text: `BOZZA` while the template
  is not approved (missing, incomplete, not approved), `ANTEPRIMA` for an approved template (the preview is still not
  the document to sign; LT-03 already marks it `ANTEPRIMA - documento non valido per la firma` instead of BOZZA). The
  final contract has none.
- Text is written as Unicode with a ToUnicode map: it can be searched and copied, and accents and non-Latin names are
  kept (`àèìòù`, `ß`, `ł`, `č`, Greek, Cyrillic). Not covered by the font: CJK, and scripts that need shaping
  (Arabic, Indic): their characters print as an empty box.

### Library and licence (checked 2026-09-24)

| Package | Version | Licence (read from the package) | Why it fits a commercial SaaS |
|---|---|---|---|
| `PDFsharp-MigraDoc` (empira Software), with its dependency `PDFsharp` | 6.2.4 | MIT (`<license type="expression">MIT</license>` in both nuspecs) | Permissive: commercial use, modification and redistribution allowed, the only condition is keeping the copyright and licence notice. No revenue threshold, no per-developer or per-server licence, no registration |
| `PdfPig` (tests only, not shipped) | 0.1.16 | Apache-2.0 | Used by the tests to extract the text of the generated PDFs |

- Fully managed: the package contains only .NET assemblies (no `runtimes/*/native`), so the Railway container needs
  nothing more: no SkiaSharp, no libgdiplus, no fontconfig. Its transitive dependencies
  (`System.Security.Cryptography.Pkcs`, `Microsoft.Extensions.Logging.Abstractions`) resolve to the 10.0.12 already
  used by the solution.
- `dotnet list package --vulnerable --include-transitive`: no vulnerable package. The NuGet audit gate of
  `Directory.Build.props` (FD-02) keeps checking at every restore.
- Not chosen: QuestPDF 2026.9.0. Its `LICENSE.md` (License Selection Guide v3.0, effective 6 July 2026) is a dual
  licence: the free Community licence only for eligible users (with revenue thresholds), otherwise a paid
  Professional/Enterprise licence; it also counts code written by an AI agent as written by a Developer of the
  organisation. The package also ships native Skia and qpdf libraries (about 50 MB).
- Upgrades: keep the MIT line (PDFsharp 6.x); read the licence of the new version before a major upgrade
  (7.0 is in preview).

### Fonts

- DejaVu Sans 2.37, regular and bold, unmodified, from the upstream release
  (`dejavu-fonts-ttf-2.37.tar.bz2`, github.com/dejavu-fonts/dejavu-fonts, tag `version_2_37`):
  `Casazen.Infrastructure/Documents/Fonts/DejaVuSans.ttf`, `DejaVuSans-Bold.ttf`, licence
  `DejaVu-LICENSE.txt` next to them.
- Licence: Bitstream Vera Fonts licence (DejaVu changes in the public domain; some glyphs under the equivalent Arev
  licence). Use, embedding and redistribution are free, also inside a software package that is sold; the fonts cannot
  be sold on their own, a modified font must be renamed, and the notice must travel with the fonts: keep
  `DejaVu-LICENSE.txt` in the repository.
- The two files are compiled into `Casazen.Infrastructure.dll` (`EmbeddedResource`). `EmbeddedFontResolver` maps
  every font family to them, so rendering never reads the fonts of the host (the `aspnet` image has none) and a
  missing system font can never break a PDF. Each PDF embeds a subset of the glyphs it uses (`FontFile2`).
- Italic is not used; if requested it is simulated.
- To change font: add the TTF files and their licence under `Documents/Fonts/`, declare them as `EmbeddedResource`
  with a `LogicalName`, update the face names in `EmbeddedFontResolver`, rerun `MigraDocPdfDocumentRendererTests`.

### PDF/A: not produced

PDFsharp 6.2.4 has no PDF/A-2b output. The only switch, `PdfDocument.SetPdfA()`, is marked by empira as a
"temporary hack": it declares PDF/A-1a (tagged PDF) in the metadata, and no validator (veraPDF) is available in CI to
check the result, so a PDF/A claim would not be verified. The generated PDFs are therefore plain PDF 1.x with embedded
fonts. For the RLI the Agenzia wants the **signed** copy in PDF/A-1a/1b or TIFF (`docs/integrations/rli-esign.md`
§2.1): with the offline signature (LT-02) that file is the one the landlord uploads. Open point for the product owner.

### Check on the test environment

1. Download the preview of a draft lease: A4 (viewer → document properties → page size 21 × 29.7 cm), `Pagina N di M`
   at the bottom, `BOZZA` on every page, names with accents intact.
2. Document properties → fonts: only `DejaVuSans` / `DejaVuSans-Bold`, "embedded subset".
3. Tests: `MigraDocPdfDocumentRendererTests`, `MigraDocPdfDocumentRendererConcurrencyTests` (16 threads render one
   document: every PDF has the text of the serial render), `LeaseContractTemplateServiceTests` (contract of more than 20,000
   characters on 3+ A4 pages with every word in order, non-Latin names, watermark), `FiscalRegimeServiceTests`.

## Prerequisite before the first approval

LT-09 is integrated: the contract is laid out on as many A4 pages as it needs, without truncation. Before approving,
download the preview of the real template and check page count, headings and the absence of `[DATO MANCANTE: …]`.

## Bozze 2026-11: draft templates (task LG-02)

> **Bozza redatta da un agente AI su incarico del PO: richiede revisione di un legale prima dell'uso in produzione.**

Written on 2026-10-09 (task LG-02, decision D27 of the wave spec) after the product owner's decision of 2026-10-08: the
legal texts and the contract templates are written by the agent **for now**, as drafts that a lawyer reviews before any
use. The texts derive from the four demo drafts of 2026-10-08 (`contratto-libero`, `contratto-concordato`,
`contratto-transitorio`, `contratto-studenti`; same clauses, same wording). **Nothing is approved and nothing is
switched on**: the committed `appsettings.json` keeps every variant `Approved: false` with no `VersionId`.

### What exists

| File (`Casazen.Web/LeaseTemplates/…/2026-11-bozza-v1.md`) | Regime | Derived from | What differs |
|---|---|---|---|
| `CedolareSecca/` | `CedolareSecca` | `contratto-libero` (4+4) | art. 17 `opzione_cedolare` (the option, the waiver of rent updates, the revocation); art. 7 no ISTAT update while the option lasts; art. 16 no registration tax or stamp duty |
| `RegimeOrdinario/` | `RegimeOrdinario` | `contratto-libero` (4+4) | art. 7 ISTAT update with a percentage to fill in; art. 16 registration tax and stamp duty split between the parties; art. 17 ordinary regime, with the cedolare option as a possibility for later years |
| `CanoneConcordato/` | `CanoneConcordato` | `contratto-concordato` (3+2) | art. 1 `accordo_territoriale`, art. 8 `attestazione_conformita` (both required for this regime); the tax regime (cedolare secca or ordinary) is chosen by hand in art. 19 |

Every file is complete: all the required sections of its regime with text and required placeholders, only the 12 known
placeholders, no format problem. Its state is therefore `NotApproved`, and the product behaves as in the third row of
the table "Behaviour": the preview is the full contract marked `BOZZA - template non approvato` (watermark `BOZZA` on
every page) and the final contract (download, signed upload, signing) still answers `422 contract_template_not_approved`.

The two drafts that have no pipeline are **not** in `LeaseTemplates/` (a file there for `Transitorio` or `Studenti`
would be ignored by the catalog and could mislead): they are kept as reference in `docs/legal-drafts/leases/`
(`contratto-transitorio.md`, `contratto-studenti.md`), with the notice "Bozza non approvata: nessuna pipeline di
prodotto", the variables with their examples and the product placeholder that would cover each, and their own points
for the lawyer.

### How the demo drafts were adapted to the product format

- **Articles.** The format does not number articles: they are numbered by hand (`## id | Art. N · Title`), as are the
  internal references ("articolo N") and the list "Clausole approvate specificamente" (arts. 1341-1342 c.c.). If an
  article is added or removed, renumber all three. `LeaseTemplateDraftsTests` checks that numbers are consecutive, that
  every reference points to the right section and that the list matches the headings.
- **Required sections.** Where the demo had one article for two required sections (term and renewal) they are split;
  the data of the agreement moved from the recitals to its own article in the concordato.
- **Data the product has** are placeholders: parties (`locatori`, `conduttori`), address, comune and cadastral data
  (`immobile_indirizzo`, `immobile_comune`, `dati_catastali`), APE (`ape_estremi`), term and dates (`durata`,
  `data_decorrenza`, `data_scadenza`), rent (`canone_mensile`, `canone_annuo`), deposit (`deposito_cauzionale`). The
  term is always the one computed from the dates, never "quattro anni" written by hand.
- **Data the product does not have** are **not invented**: they are blank fields to fill in by hand on the printed
  contract (`__________`), or the clause is worded so as not to need them. Every choice has a `<!-- LEGALE: … -->`
  comment in the file (comments are not printed).

| Datum asked by the 2026-10-08 draft | In the templates | Where |
|---|---|---|
| Residence of landlord and tenant, citizenship of the tenant | blank | `parti` |
| Floor, composition, surface, furnishing | blank; furnishing is a choice to strike out | `immobile` |
| Months of notice of the contractual withdrawal | blank | `recesso` (free-rent templates) |
| Rent in words | dropped; the yearly rent computed by the product is printed instead | `canone` |
| Payment day, landlord's IBAN | blank | `canone` |
| ISTAT percentage | blank in the free-rent templates (the concordato states the 75% of the agreement) | `aggiornamento_canone` |
| Monthly advance on charges | blank | `oneri_accessori` |
| Details and deposit date of the agreement, rent band, body that attests | blank | concordato: `accordo_territoriale`, `canone`, `attestazione_conformita` |
| Tax regime | from the template (free rent); a choice to strike out (concordato) | `regime_fiscale` / `opzione_cedolare` |
| Email of the parties | blank | `comunicazioni` |
| Place and date of the signature | blank | `firme` |

**Consequence of the blanks.** The printed contract has fields to complete by hand, so it can only be signed offline
(LT-02: download, complete and sign on paper, upload the signed copy). The provider signature path (flag
`ESignProvider`, off) would send the PDF with the blanks empty. Turning blanks into placeholders needs new data in the
product and new entries in `LeaseContractPlaceholders` (task proposed in the PR: LG-02b).

### Try the preview on a test environment

1. On the **test** environment set only the version, for the regimes to look at (never `Approved`):
   `LeaseTemplates__Variants__CedolareSecca__VersionId=2026-11-bozza-v1`, and the same for `RegimeOrdinario` and
   `CanoneConcordato`. Redeploy (the files are read once per process).
2. Open a draft lease of that regime and download `GET /api/leases/{id}/contract/preview`. The first lines must say
   `stato: modello completo ma non approvato` with no "Sezioni senza testo" and no "Problemi di formato", and every page
   must carry the `BOZZA` watermark. `[DATO MANCANTE: …]` appears only for data the lease lacks: the cadastral data
   (`PUT /api/properties/{id}/cadastral`), the APE code and class (`PUT /api/properties/{id}/documents/{docId}/ape`)
   and the deposit.
3. The final contract keeps answering `422 contract_template_not_approved` (download, upload of the signed copy,
   signing). On production leave the variables unset.

**Approving.** A lawyer reviews this exact text (the PDF of the preview and the file). Even when nothing changes,
publish the reviewed text as a **new version** without "bozza" in its name and without the draft notice, and then follow
"Approving a template" above; never set `Approved=true` on `2026-11-bozza-v1` (replaced on 2026-10-24 by the product owner's order:
see [Attivazione (2026-10-24)](#attivazione-2026-10-24)).

### Points for the lawyer, first

1. **The concordato, transitorio and studenti texts are rephrased and are not the official model contracts of the
   D.M. 16 January 2017** (Allegati A, B and C). Differences may put at risk the attestation of conformity and the tax
   benefits. Compare first: the tenant's withdrawal (concordato art. 6; transitorio art. 5 and studenti art. 5 of the
   reference drafts), the passage of the transitorio to a 4+4 when the need is not confirmed or ceases (transitorio
   art. 3), extension and renewal (concordato art. 5), and what the attestation needs (concordato art. 8). The free-rent
   drafts have no official model to compare with.
2. **Citations.** An earlier wording (and the task brief) cited "art. 7 D.L. 59/1978", which is wrong: the *cessione di fabbricato* is
   art. 12 of D.L. 59/1978 (absorbed by the registration of the contract) and the communication within 48 hours for
   non-EU citizens is art. 7 of D.Lgs. 286/1998 (the registration does not replace it). The drafts write "secondo la
   normativa vigente" and name no article: decide whether to cite them (free-rent art. 18, concordato art. 20). All the
   other statutory citations were copied from the 2026-10-08 drafts and **not re-read on Normattiva** (not reachable
   from here): check each one.
3. **Fiscal clauses.** (a) Whether a clause of the contract can replace the registered letter by which the landlord
   waives the rent updates when opting for the cedolare secca (D.Lgs. 23/2011 art. 3). (b) Whether the ISTAT updates of
   the cedolare years are lost after a revocation of the option, as the drafts say (free-rent art. 7 and 17, concordato
   art. 9 and 19). (c) The cedolare rate that applies to student and transitory leases (the drafts print no rate; the
   concordato draft states the 10% "where the conditions apply", valid only in the municipalities with a housing
   shortage). From 1 January 2027 the registration tax and the cedolare move to new codes (D.Lgs. 123/2025 and
   D.Lgs. 117/2026): the references to D.P.R. 131/1986 and to D.Lgs. 23/2011 will need updating.

### Points for the lawyer, then

4. **Clauses approved in writing (arts. 1341-1342 c.c.).** The list is the one of the demo drafts: term, renewal and
   notice, withdrawal, deposit, use and ban on sublease, innovations, access. Check it, whether to add the clause on
   default (free-rent art. 6, concordato art. 7) and the waiver of updates, and whether one specific approval at the end
   is enough.
5. **Withdrawal and notice.** The rule cited for withdrawal for serious reasons (the drafts say art. 3 of L. 431/1998),
   the minimum notice of the contractual withdrawal (a blank in the free-rent drafts) and what counts as a serious reason.
6. **Several landlords or tenants.** The product supports them (LT-14); the drafts do not regulate joint liability
   (only the student draft has an optional clause). Decide.
7. **Payment.** Bank transfer is the only method written: check it against the rules on traceable payment of rents, and
   the default rule (twenty days; two months of charges).
8. **Deposit.** Three months, legal interest and return within thirty days (the thirty days are a proposal of the draft).
9. **Registration.** The deadlines (thirty days; communication within sixty days to the tenant and the condominium
   administrator) and the split in equal parts of registration tax and stamp duty.
10. **APE.** The clause follows D.Lgs. 192/2005 art. 6 c. 3, checked so far only on third-party pages (see the table of
    required sections).
11. **Concordato.** Where the agreement of Monza e Brianza requires the attestation to be issued jointly by a landlords'
    and a tenants' body, the draft refers to "the number and the forms the agreement requires"; the 75% of the ISTAT
    update is the maximum of that agreement; the *allegato di calcolo* and the attestation are not produced by the
    product.
12. **Data the product does not have** (table above): whether some of them should become placeholders (task proposed
    in the PR: LG-02b), rather than blanks to fill in by hand.

### Tests

`Casazen.Tests/Unit/Services/LeaseContracts/LeaseTemplateDraftsTests.cs`: each draft loads as `NotApproved` with no
missing section and no issue; every file under `LeaseTemplates/` is in the right folder, uses only known placeholders and
is `NotApproved`; the committed `appsettings*.json` name no version and approve nothing; the preview PDF of a test
lease with complete data has the `BOZZA` watermark on every page, no `[TESTO DELLA CLAUSOLA NON FORNITO]` and no
`[DATO MANCANTE: …]`; the final contract still answers 422; numbering, internal references and the list of the clauses
approved in writing agree; no example datum (email, IBAN, amount) slipped in where the product has none; the reference
drafts exist in `docs/legal-drafts/leases/`. For the transitory model (TR) the same class checks that it loads complete and not
approved with the 17 articles of the Allegato B in order and no blank of the form left (no dots, no note marker), that its preview is the
whole contract marked BOZZA with the data of the lease printed, that the final contract answers `422 contract_template_not_approved`, and what
`LeaseContractDocument.ResolveData` gives for the need, its documentation and the deposit.

## Attivazione (2026-10-24)

The product owner ordered the activation of the three templates `2026-11-bozza-v1` (`CedolareSecca`, `RegimeOrdinario`,
`CanoneConcordato`); the legal opinions were obtained. This replaces the line "never set `Approved=true` on `2026-11-bozza-v1`" of
[Bozze 2026-11](#bozze-2026-11-draft-templates-task-lg-02).

- **Only with environment variables on the hosts.** For each regime, on the API host of the environment (test, then production):
  `LeaseTemplates__Variants__<Regime>__VersionId=2026-11-bozza-v1`, `LeaseTemplates__Variants__<Regime>__Approved=true` and
  `LeaseTemplates__Variants__<Regime>__ApprovalReference=<who approved: opinion or protocol number>` (and/or `…__ApprovedAt=<YYYY-MM-DD>`),
  then redeploy (the files are read once per process). The repository cannot approve them: `LeaseTemplateDraftsTests` fails if any
  `appsettings*.json` names a version or sets `Approved=true`. A wrong value stops the startup ("Startup validation"): Railway keeps the
  previous deployment.
- **Rollback:** `LeaseTemplates__Variants__<Regime>__Approved=false` and redeploy. The final contract answers
  `422 contract_template_not_approved` again and the preview goes back to `BOZZA`.
- **From then on the security deposit is a required datum of every contract made in CasaZen** (LR-DEP): the three templates print
  `{{deposito_cauzionale}}` in the "Deposito cauzionale" article, so `contract.pdf` and `POST …/sent` answer `422 contract_data_missing` for
  a lease without one. The guided draft therefore asks for it at step 3 (need `deposit_missing`; `0` is a valid answer, "none") and the
  review cannot be confirmed without it ([`long-rent-lease-draft.md`](long-rent-lease-draft.md)). An imported contract is not affected:
  it has no template. A draft whose review was confirmed before this change, without a deposit, is blocked again (step 3 asks for it, and
  typing it withdraws the old confirmation).
- **The code and the energy class of the APE are required too** (LR-APE): the three templates print `{{ape_estremi}}` ("Estremi dell'APE"),
  which is the code and class of the **latest APE document of the property that has both** (`LeaseContractDocument.FormatApe`; a later
  document without them does not take its place). The review used to pass with an APE the reading took for valid but whose code and
  class were never saved, and the download then answered `422 contract_data_missing`. The guided draft therefore asks for them at step 1
  (need `ape_identification_missing`, the same rule as the contract: some APE document of the property has both) and the review cannot be
  confirmed without them ([`long-rent-lease-draft.md`](long-rent-lease-draft.md)). An imported contract is not affected. A draft whose
  review was confirmed before this change on a property whose APE has no identification is blocked again (the download for signature
  answers `422 lease_review_blocked`); once the code and class are typed on the APE, the confirmation stands.
- **The `Transitorio` variant is not part of this activation** (TR, 2026-10-25): its model is another file and stays **not approved** until the
  product owner sets, on the host, `LeaseTemplates__Variants__Transitorio__VersionId=2026-11-bozza-v1`,
  `LeaseTemplates__Variants__Transitorio__Approved=true` and `LeaseTemplates__Variants__Transitorio__ApprovalReference=<…>` (see the next
  section). Until then a transitory contract made in CasaZen cannot be downloaded for signature (`422 contract_template_not_approved`).

## Transitorio, bozza 2026-11-bozza-v1: scelte rispetto all'Allegato B

> **Bozza trascritta da un agente AI su incarico del PO: richiede la revisione di un legale prima dell'approvazione.** Questa sezione
> elenca ogni scelta e ogni scostamento dal modello ufficiale, perché il legale li rilegga.

Task TR (25/10/2026), ordine del PO: «cerca un modello ufficiale, per il modello transitorio e caricalo con casazen». Il contratto di tipo
`Transitorio` (da 1 a 18 mesi) si prepara in CasaZen con il **modello ufficiale**: l'Allegato B del decreto interministeriale (MIT e MEF)
16 gennaio 2017, «Locazione abitativa di natura transitoria» (Gazzetta Ufficiale n. 62 del 15-3-2017, pagg. 95-101), che l'art. 2, comma 7, del
decreto rende l'unico tipo di contratto utilizzabile. Il testo degli articoli 1-17 e della clausola di approvazione specifica degli artt. 1341 e
1342 c.c. è quello ufficiale; le note 1-12 del decreto sono istruzioni di compilazione e non entrano nel contratto. La bozza AI di
`docs/legal-drafts/leases/contratto-transitorio.md` **non** è usata come testo.

**Il modello non è approvato**: nessuna variabile `Approved`, nessuna riga negli `appsettings` (`LeaseTemplateDraftsTests` vieta `Approved=true` in
qualunque `appsettings*.json`). Lo approva il PO con le variabili d'ambiente dell'host, dopo il parere del suo legale: vedi «Approving a template»
sopra, con la variante `Transitorio`.

- File: `Casazen.Web/LeaseTemplates/Transitorio/2026-11-bozza-v1.md`, **lo stesso per la cedolare secca e per il regime ordinario**: il testo
  ufficiale non cambia (l'art. 7 dice «ove dovuta» per la registrazione; la nota 12 sulla cedolare è un'istruzione, non testo).
- Fonte della trascrizione: il testo estratto dal PDF della G.U. (gli spazi spuri dentro le parole sono stati riparati a mano; i passi dubbi sono
  stati confrontati con le copie del modello pubblicate dai Comuni). **Il legale confronti il file con la G.U.**
- Le alternative del modello («cancellare la parte che non interessa», nota 4) sono risolte con i dati che CasaZen ha; dove il dato manca si è
  scelta la formula più neutra tra quelle del modello, oppure si è tolta la frase che lo chiede. Nessun testo nuovo al posto di quelli ufficiali.
  Nel PDF non restano puntini né alternative: gli unici campi in bianco sono luogo, data e righe di firma (si firma su carta).
- Il locatore indica, nel passo 3 della bozza, chi ha l'esigenza e quale è; nel passo 4 la data di fine (da 1 a 18 mesi dall'inizio) e, se il
  contratto dura più di 30 giorni, la documentazione che allega ([`long-rent-lease-draft.md`](long-rent-lease-draft.md)).

### Scelte e scostamenti, articolo per articolo

| Dove | Originale | Nel modello di CasaZen | Perché |
|---|---|---|---|
| Premessa · parti | «Il/La sig./soc. (1) … di seguito denominato/a locatore … al/alla sig. (1) … denominato/a conduttore» | `{{locatori}}`, «di seguito denominato locatore», «a `{{conduttori}}`», «di seguito denominato conduttore» | Nome e codice fiscale di ogni parte (anche più persone, nell'ordine di inserimento) sono i dati che il prodotto stampa. Il genere e la forma (persona o società) non si conoscono: maschile singolare, anche con più parti. Luogo e data di nascita e domicilio della nota 1 non sono stampati, come nei tre modelli esistenti (TR-FU3) |
| Premessa · assistenza | «(assistito/a da (2) … in persona di …)», due volte | tolto | L'assistenza delle organizzazioni è facoltativa (nota 2) e CasaZen non la conosce: è un contratto non assistito |
| Premessa · identificazione | «identificato/a mediante (3) …» | tolto | CasaZen non conserva documenti d'identità (LR-D3) |
| Premessa · unità | A) l'unità immobiliare «posta in … via … n. … piano … scala … int. …, composta di n. … vani, oltre cucina e servizi, e dotata altresì dei seguenti elementi accessori …» / B) una porzione | A), con «l'unità immobiliare posta in `{{immobile_indirizzo}}`» (via, numero, CAP e comune) | Il contratto riguarda l'unità intera. Piano, scala, interno, vani ed elementi accessori non sono dati del prodotto |
| Premessa · arredo | «non ammobiliata/ammobiliata (4) come da elenco a parte sottoscritto dalle parti» | tolto | Il prodotto non sa se l'unità è arredata e non produce l'elenco. Il contratto non dice nulla sull'arredo: **punto per il legale** |
| Premessa · elenco a)-d) | a) estremi catastali, b) prestazione energetica, c) sicurezza impianti, d) tabelle millesimali | a) `{{dati_catastali}}`, b) `{{ape_estremi}}`; c) e d) tolti | Foglio, particella, subalterno, categoria e rendita; codice e classe dell'APE (obbligatori in un contratto fatto in CasaZen, LR-APE). Sicurezza degli impianti e tabelle millesimali non sono dati del prodotto |
| Art. 1 | «……… mesi/giorni (6), dal … al …» | `{{durata}}`, dal `{{data_decorrenza}}` al `{{data_scadenza}}` | La durata è calcolata sulle date, come negli altri modelli («6 mesi», «1 anno», «14 mesi e 10 giorni»); il limite da 1 a 18 mesi è controllato alla creazione |
| Art. 2 · alternativa | A) dichiarazione dell'esigenza / B) parti assistite dalle organizzazioni | A) | La B) richiede il supporto delle organizzazioni, che CasaZen non conosce |
| Art. 2 · soggetto | «Il locatore/conduttore» (rubrica, testo e clausola di approvazione) | `{{esigenza_parte}}`: «locatore» o «conduttore», come indicato dal locatore | La rubrica diventa «Esigenza del locatore» o «Esigenza del conduttore» (l'intestazione di una sezione può contenere segnaposto) |
| Art. 2 · accordo | «e dall'Accordo territoriale tra … depositato il … presso il Comune di …, ovvero dall'Accordo integrativo sottoscritto tra … in data … (7)» | «e dall'Accordo territoriale» | Gli estremi dell'accordo (parti, data, comune di deposito) non sono dati del prodotto (TR-FU2). **Punto per il legale**: l'esigenza dichiarata deve essere tra quelle dell'accordo locale (D.M. art. 2 c. 4); la schermata lo ricorda, CasaZen non lo verifica |
| Art. 2 · esigenza e documentazione | «… la seguente esigenza che giustifica la transitorietà del contratto ……, e che documenta, in caso di durata superiore a 30 giorni, allegando ………» | «… del contratto: `{{esigenza_motivo}}`, e che documenta, in caso di durata superiore a 30 giorni, allegando: `{{esigenza_documentazione}}`.» | L'esigenza (al massimo 500 caratteri) e la documentazione (al massimo 300, obbligatoria oltre i 30 giorni) le scrive il locatore; aggiunti i due punti e tolto il punto finale che incontrerebbe la virgola. Per un contratto di 30 giorni o meno il prodotto scrive «nessuno, trattandosi di un contratto di durata non superiore a 30 giorni», perché il modello chiede i documenti solo oltre i 30 giorni |
| Art. 3 | testo ufficiale | invariato, con l'avvertenza «(Il presente articolo non si applica ai contratti con durata pari o inferiore ai 30 giorni)» | Vedi le avvertenze sotto |
| Art. 4 | tre periodi: A) canone convenuto; B) comuni con più di 10.000 abitanti, canone secondo l'Accordo territoriale; C) canone secondo il decreto di cui all'art. 4 c. 3 L. 431/1998 | solo A): «euro `{{canone_mensile}}` al mese, … a mezzo di bonifico bancario, in rate eguali anticipate» | CasaZen non conosce gli abitanti del comune né l'accordo e non calcola le fasce (TR-FU1): la revisione avvisa che nei comuni sopra i 10.000 abitanti il canone deve rientrare nelle fasce (D.M. art. 2 c. 2 e c. 6). Dentro A): scelto il bonifico (tolti «nel domicilio del locatore ovvero» e «ovvero …»); tolti numero e importo delle rate e le date (il piano dei canoni si fa dopo la firma); «al mese» aggiunto perché il modello non dice a che periodo si riferisce l'importo (lo si deduce dalle rate). **Punto per il legale, tra i primi**: se in un comune sopra i 10.000 abitanti basti A) o servano B) e C) |
| Art. 5 | «versa/non versa (4)», «una somma di euro … pari a n. … mensilità del canone (8)», «Altre forme di garanzia: … (9)» | `{{deposito_versamento}}` («versa» se il deposito è maggiore di 0, «non versa» se è 0), `{{deposito_cauzionale}}`, `{{deposito_mensilita}}` (deposito diviso canone); «Altre forme di garanzia» tolte | Il deposito è un dato obbligatorio in un contratto fatto in CasaZen (0 se non c'è, LR-DEP) e non può superare tre mensilità (controllato). Con deposito 0 la frase dice «non versa … euro 0,00 pari a n. 0 mensilità» (lo stesso limite di LR-DEP-FU2). «(che con la firma del contratto ne rilascia, in caso, quietanza)» è invariato |
| Art. 6 | formulazione dell'articolo / formulazione sostitutiva della nota 10 (immobili non in condominio, con le tabelle millesimali) | formulazione dell'articolo, con «condominiali/comuni» risolto in «comuni», «e altro ……» in «e altro», e tolta l'ultima frase («il conduttore versa una quota di euro … (11), salvo conguaglio») | Il prodotto non sa se l'edificio è in condominio, non ha le tabelle millesimali né la quota degli oneri |
| Art. 7 | testo ufficiale | invariato | Il testo dice già «ove dovuta» per la registrazione; la nota 12 (cedolare secca: imposte di bollo e registro non dovute) è un'istruzione |
| Art. 8 | testo ufficiale | invariato | |
| Art. 9 | «… civile abitazione del conduttore e delle seguenti persone attualmente con lui conviventi: …» | «… civile abitazione del conduttore.» | Le persone conviventi non sono un dato del prodotto. **Punto per il legale** |
| Art. 10 | «… lettera raccomandata almeno … prima» | «… lettera raccomandata.» | Il preavviso lo scrivono le parti nel modello e CasaZen non lo conosce: nessun termine minimo scritto. **Punto per il legale** (per esempio i sei mesi dell'art. 3 c. 6 L. 431/1998 per il recesso del 4+4) |
| Art. 11 | ultima frase: «Le parti danno atto, in relazione allo stato dell'unità immobiliare, ai sensi dell'articolo 1590 del Codice civile, di quanto segue: … / di quanto risulta dall'allegato verbale di consegna.» | tolta | Il prodotto non ha lo stato dell'unità e non produce un verbale di consegna. Senza descrizione vale la presunzione dell'art. 1590 c.c. |
| Artt. 12, 13, 14, 16 | testo ufficiale | invariati | |
| Art. 15 | «… una volta la settimana, per almeno due ore, con esclusione dei giorni festivi oppure con le seguenti modalità: …» | la prima modalità | La seconda non ha dati |
| Art. 17 | «Altre clausole ……» | tolta | Il prodotto non ha clausole aggiuntive. Il resto è invariato |
| Firme | «Letto, approvato e sottoscritto», luogo e data, «Il locatore …», «Il conduttore …» | le stesse parole; luogo, data e firme sono righe di trattini bassi, con i nomi delle parti accanto | Si firma su carta. L'intestazione «Firme» è un'etichetta del prodotto (il modello non ha un titolo qui) |
| Approvazione specifica | «A mente degli articoli 1341 e 1342, del Codice civile, … 2 (Esigenza del locatore/conduttore), 3 (Cessazione delle condizioni di transitorietà), … 8 (Pagamento, risoluzione) …» | testo ufficiale, con `{{esigenza_parte}}` nell'art. 2 | Nella G.U. i titoli degli artt. 3 e 8 nell'elenco non coincidono con quelli degli articoli («Inadempimento delle modalità di stipula», «Pagamento»): lasciati come stampati. L'intestazione «Clausole approvate specificamente» è un'etichetta del prodotto |

**Avvertenze sui 30 giorni.** «(Il presente articolo non si applica ai contratti con durata pari o inferiore ai 30 giorni)» (artt. 3, 6, 7, 10, 13 e 14) e
«(Il secondo periodo non si applica …)» (art. 15) sono nel testo ufficiale e restano: il formato dei modelli non ha testo condizionale, quindi un
contratto di 30 giorni o meno le stampa tutte, come il modello a stampa. Anche i periodi B e C dell'art. 4 e la B) dell'art. 2 non sono stampati
per nessuno.

**Aggiunto al testo ufficiale**: solo punteggiatura (i due punti prima dell'esigenza e della documentazione, la virgola prima di «dichiara»),
«al mese» all'art. 4, i valori dei dati e il «nessuno, trattandosi di un contratto di durata non superiore a 30 giorni» dell'art. 2.

### Punti per il legale, per primi

1. Confrontare la trascrizione con il testo della G.U. (l'estrazione automatica del PDF ha spazi spuri dentro le parole).
2. Se i contratti fatti con questo modello stanno nel D.M. 16/01/2017: art. 4 con il solo periodo A) anche nei comuni sopra i 10.000 abitanti;
   art. 2 con l'accordo territoriale non identificato e solo nella versione senza assistenza; contratti non assistiti, per i quali l'accordo
   prevede l'attestazione di rispondenza di un'organizzazione firmataria (D.M. art. 2 c. 8): CasaZen non la produce.
3. Le frasi tolte: arredo (premessa), persone conviventi (art. 9), preavviso del recesso (art. 10), stato dell'unità (art. 11), altre garanzie (art. 5),
   quota degli oneri (art. 6), altre clausole (art. 17).
4. L'APE: il modello ufficiale non ha la clausola dell'art. 6 c. 3 D.Lgs. 192/2005 (dichiarazione di aver ricevuto le informazioni e
   l'attestato); ha solo «b) prestazione energetica», che CasaZen compila con codice e classe. Aggiungere la clausola (per esempio tra le «Altre
   clausole»)?
5. L'identificazione delle parti (nota 1) ridotta a nome e codice fiscale; la forma «denominato» al maschile singolare con più parti.
6. Il deposito 0 («non versa … euro 0,00 pari a n. 0 mensilità») e le avvertenze dei 30 giorni stampate sempre.
7. Le righe di firma, luogo e data in bianco.

### Provarlo e approvarlo

1. Sull'ambiente di **test** si imposta solo la versione: `LeaseTemplates__Variants__Transitorio__VersionId=2026-11-bozza-v1` (mai `Approved`), si
   fa il redeploy (i file si leggono una volta per processo), si prepara un contratto transitorio e si scarica `GET /api/leases/{id}/contract/preview`:
   deve dire `stato: modello completo ma non approvato`, con la filigrana `BOZZA` su ogni pagina, senza «Sezioni senza testo», «Problemi di formato»
   o `[DATO MANCANTE: …]`. «Scarica da firmare» risponde ancora 422 `contract_template_not_approved`.
2. Dopo il parere del legale su quel file, il PO approva su test e poi su produzione:
   `LeaseTemplates__Variants__Transitorio__Approved=true` e `LeaseTemplates__Variants__Transitorio__ApprovalReference=<chi ha approvato: parere o
   protocollo>` (e/o `…__ApprovedAt=<AAAA-MM-GG>`), poi redeploy. Un testo cambiato è un file nuovo con una versione nuova.
3. Rollback: `LeaseTemplates__Variants__Transitorio__Approved=false` e redeploy.

### Debiti (ID proposti)

- **TR-FU1**: calcolatore delle fasce dell'accordo territoriale e controllo del canone dei transitori nei comuni sopra i 10.000 abitanti (oggi c'è solo
  l'avviso informativo `transitory_rent_band_to_check` della revisione).
- **TR-FU2**: accordo territoriale per comune: gli estremi (parti, data e comune di deposito) negli artt. 2 e 4, l'elenco delle esigenze ammesse, i
  periodi B e C dell'art. 4 e la versione assistita dell'art. 2.
- **TR-FU3**: stampare residenza, cittadinanza e dati di nascita delle parti (nota 1) e gli altri dati oggi tolti (insieme a LG-02b), e un testo
  condizionale nel formato dei modelli (avvertenze dei 30 giorni, deposito 0, documentazione facoltativa).
- **TR-FU4**: mostrare l'esigenza nella scheda del contratto: il dettaglio mascherato di `GET /api/leases/{id}` non la espone, oggi sta nella bozza e
  nel contratto.
