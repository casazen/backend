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
| **Missing**: no `VersionId` or no file | `422 contract_template_not_approved` | PDF marked `BOZZA - template non approvato`, every section `[TESTO DELLA CLAUSOLA NON FORNITO]` with the lease data |
| **Incomplete**: a required section without text, a required placeholder not used, unknown placeholder, no title | `422 contract_template_not_approved` | same marker, provided texts shown, the rest marked |
| **NotApproved**: complete, but `Approved=false` or the approval is not valid | `422 contract_template_not_approved` | same marker, full text |
| **Approved** | PDF of the approved texts with the lease data; `422 contract_data_missing` if the template uses a datum the lease does not have | marked `ANTEPRIMA - documento non valido per la firma` |

- The lease stays `Draft` on a 422: no signing session is created, no signed contract is accepted, no event is written.
  `GET /api/leases/{id}/signers` reports it in advance (`contractAvailable: false`, `contractUnavailableCode`), so the
  lease page disables download and upload and offers only the preview (LT-02).
- The messages are localized (`LeaseContractTemplateNotApproved`, `LeaseContractDataMissing` in `SharedResources.resx` / `.en.resx`); `contract_data_missing` lists the missing placeholders.
- The preview needs `lease.sign` on the lease (it carries the parties' full fiscal codes); another org's lease answers 404.
- Registration uses the PDF signed through the signing flow, so a contract generated from an unapproved template can
  never reach it.

Code: `Casazen.Core/Leases/` (structure, term, approval rules), `Casazen.Infrastructure/Services/LeaseContracts/`
(parser, catalog, document, startup validator), `Casazen.Infrastructure/External/LeaseContractTemplateService.cs`.

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
| `dati_catastali` | cadastral data of the unit | **not in the data model yet** |
| `ape_estremi` | APE identification (code, energy class, date) | **not in the data model yet** |
| `deposito_cauzionale` | security deposit | **not in the data model yet** |

A template may use the last three, but until the data model has them every final contract of that regime answers
`422 contract_data_missing` (the preview shows `[DATO MANCANTE: …]`). Adding those fields (lease/property, API,
form) is a separate task.

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

## Prerequisite before the first approval

The PDF writer used today (`FiscalPdfWriter`) prints one page and cuts the text at 4000 characters without wrapping
(defect A7-14, task LT-09). A real contract does not fit: **do not approve any template before LT-09 is
integrated**, otherwise the signed PDF would be truncated.
