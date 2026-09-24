# Runbook: lease contract templates (approval gate, clause texts from the product owner)

Task LT-03 (audit defect A7-03, P0). Decision D14: the legal texts come from the product owner; the code only gives
the structure of the contract and fills in the lease data. No clause is written in code or in this repository until
the product owner provides it.

Before LT-03 the base `appsettings.json`, valid in production too, marked every regime `Approved: true` with
`VersionId: dev-stub`, and the "contract" sent to signature was a 13-line draft that wrote "3+2" whatever the dates
and, for cedolare secca / regime ordinario, did not even name the parties.

## Behaviour

| Template state (per fiscal regime) | Final contract: `GET /api/leases/{id}/contract.pdf`, `POST signed-document` (offline signature, LT-02), `POST signing` (provider, off) | `GET /api/leases/{id}/contract/preview` |
|---|---|---|
| **Missing**: no `VersionId` or no file | `422 contract_template_not_approved` | PDF marked `BOZZA - template non approvato` (watermark `BOZZA` on every page), every section `[TESTO DELLA CLAUSOLA NON FORNITO]` with the lease data |
| **Incomplete**: a required section without text, a required placeholder not used, unknown placeholder, no title | `422 contract_template_not_approved` | same marker, provided texts shown, the rest marked |
| **NotApproved**: complete, but `Approved=false` or the approval is not valid | `422 contract_template_not_approved` | same marker, full text |
| **Approved** | PDF of the approved texts with the lease data; `422 contract_data_missing` if the template uses a datum the lease does not have | marked `ANTEPRIMA - documento non valido per la firma` (watermark `ANTEPRIMA` on every page) |

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

`Casazen.Web/LeaseTemplates/<FiscalRegime>/<VersionId>.md`, for example
`Casazen.Web/LeaseTemplates/CanoneConcordato/2026-11-avv-rossi-v1.md`.

- `<FiscalRegime>` is `CedolareSecca`, `RegimeOrdinario` or `CanoneConcordato` (exact spelling).
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
order) but must have text.

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

The last three are entered by the landlord (LT-10): the cadastral data on the property page
(`PUT /api/properties/{id}/cadastral`), the APE code and class on the APE document (`PUT
/api/properties/{id}/documents/{docId}/ape`), the deposit in the lease form (`securityDeposit`). Only lengths and, for
the energy class, 1-3 letters/digits/"+" are checked: no pattern is imposed on the codes. While a datum used by the
template is missing, the final contract answers `422 contract_data_missing` and the preview shows
`[DATO MANCANTE: …]`.

**Transitorio leases** (LT-10): the contract type exists (1-18 months) but has no template, whatever the tax regime:
the final contract answers `422 contract_template_not_approved` and the preview is a BOZZA. The template of a
regime is a 4+4 or 3+2 contract and must not be used for them. Student leases are not modelled.

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
Transitional (up to 18 months) and student leases are not modelled, so they have no template. The minimum term per
lease type is validated by LT-10, not here: the contract only states the actual term.

## Approving a template (product owner)

1. Get the clause texts from the lawyer (or the signatory association for the concordato) for **every** section of
   the checklist of the regime.
2. Write the file `Casazen.Web/LeaseTemplates/<Regime>/<VersionId>.md` and open a PR.
3. On the **test** environment set only the version (Railway variables, Production environment):
   `LeaseTemplates__Variants__<Regime>__VersionId=<VersionId>`. Open a draft lease of that regime and download
   `GET /api/leases/{id}/contract/preview`: the header must say `stato: modello completo ma non approvato`, with no
   "Sezioni senza testo" and no "Problemi di formato". The API log line `Lease contract template <Regime>: …` lists
   the problems.
4. Have the lawyer review that exact file (the PDF of the preview and the file in the PR).
5. Approve, on test and then on production:
   - `LeaseTemplates__Variants__<Regime>__Approved=true`
   - `LeaseTemplates__Variants__<Regime>__ApprovalReference=<who approved, opinion or protocol number>` and/or
     `LeaseTemplates__Variants__<Regime>__ApprovedAt=<YYYY-MM-DD>`
6. Redeploy. The preview now says `ANTEPRIMA - documento non valido per la firma` and signing produces the final PDF.

Any change of text: new file, new `VersionId`, new review and approval. Never edit an approved file in place.

## Startup validation

Outside Development and Testing (both Railway environments run as `Production`) the API does **not start** when a
variant is declared `Approved=true` but:

- `VersionId` is empty, `dev-stub` or not a valid file name;
- neither `ApprovalReference` nor `ApprovedAt` is set;
- the file is missing or the template is incomplete.

The failure lists `LeaseTemplates:Variants:<Regime>: …` and Railway keeps the previous deployment. In Development and
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
3. Tests: `MigraDocPdfDocumentRendererTests`, `LeaseContractTemplateServiceTests` (contract of more than 20,000
   characters on 3+ A4 pages with every word in order, non-Latin names, watermark), `FiscalRegimeServiceTests`.

## Prerequisite before the first approval

LT-09 is integrated: the contract is laid out on as many A4 pages as it needs, without truncation. Before approving,
download the preview of the real template and check page count, headings and the absence of `[DATO MANCANTE: …]`.
