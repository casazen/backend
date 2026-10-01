# Seeds

## `comuni-istat.csv`: official ISTAT list of the comuni (SU-04)

The list of the Italian comuni that the comune pickers, the validation of `Property.ComuneIstatCode` and of the comuni of a
supplier profile, the regional rules and the CIN check are built on. It is **an official file, not written by hand**: it must never be
edited row by row. To update it, replace the whole file with a newer official one (see "Updating" below).

| What | Value |
|---|---|
| Source | ISTAT, *Elenco dei comuni italiani* (ISTAT website, "Classificazioni e strumenti", *Codici dei comuni, delle province e delle regioni*) |
| Reference date | **2026-02-21** (the sheet is named `CODICI al 21_02_2026`) |
| Original file | `Elenco-comuni-italiani.xlsx`, delivered by the product owner on 2026-10-01 (RS-6) |
| SHA-256 of the original file | `83842076860450f7e482daecea6b7a769f5f93d0bf5b0d48802b44896d7a26d5` |
| SHA-256 of this CSV | `b90e79d45c2a81be38657ff37b76b013b23521b14aadb8382efd56273ac1a93b` |
| Rows | 7,894 comuni (the header and 7,894 lines), 20 regions |

The CSV is the first sheet of the workbook converted as it is: UTF-8, separator `;`, no quotes, the header cells with their
line breaks turned into spaces. No value was changed (checked cell by cell against the workbook). The other sheets of the workbook
(`NOTE`, `Legenda`) are the footnotes and the legend of the columns: the cadastral code comes from the Agenzia delle Entrate
(`N.d.` = not available), the ISTAT code of a comune is the (historical) province code and the progressive number of the comune.

### Columns (headers are the ISTAT ones, in this order)

The import (`ComuneImportService`) reads these columns by their headers and ignores the others:

| Header | Used for |
|---|---|
| `Codice Comune formato alfanumerico` | `Comuni.IstatCode`: 6 digits, 3 of the province + 3 of the comune, **text** (leading zeros matter, `001272` is Torino) |
| `Denominazione in italiano` | `Name`, the name shown and stored as the city of a property |
| `Denominazione (Italiana e straniera)` | `DisplayName` (`Bolzano/Bozen`), also searched |
| `Codice Catastale del Comune` | `CadastralCode`, the Belfiore code: a letter and 3 digits (`H501`), `N.d.` stored as null |
| `Sigla automobilistica` | `ProvinceCode`, the two letters of the province (`MI`) |
| `Codice Regione` | `RegionIstatCode`, `01` to `20`; checked against the 20 regions of `ItalianRegions` |
| `Denominazione Regione` | `RegionName`; must agree with the region code of the same row |

The other 20 columns (supra-municipal unit, geographic area, NUTS, the numeric codes of the older provincial subdivisions...) are not
imported.

### How it is loaded

- **At startup**: `ComuneSeedHostedService` imports this file (embedded in the `Casazen.Infrastructure` assembly) when the database
  has no list or an older one (by reference date), with `comuni-istat.source.json` giving the source and the reference date.
  Nothing is invented: a seed file without that metadata is refused and logged. It never replaces a list that is as recent
  or more recent (for example one an admin imported later). `Comuni__SeedOnStartup=false` turns it off.
- **By an admin**: `POST /api/admin/comuni/import` with the same file layout, any later official file: see
  `docs/runbooks/comuni-istat.md`.
- The import is atomic, idempotent (upsert by ISTAT code) and recorded in `ComuneImports` (file name, SHA-256, version, reference
  date, counts).

### Updating

1. Download the new *Elenco dei comuni italiani* from ISTAT, open the sheet with the codes and save it as CSV, UTF-8, separator `;`.
   Do not edit any cell. Header wording may differ slightly; the import finds the columns by synonyms and names any column it cannot find.
2. Replace `comuni-istat.csv`, and update `comuni-istat.source.json` (`sourceVersion`, `referenceDate` as `yyyy-MM-dd`,
   `originalFileSha256`) and the table above. Commit them together.
3. The next deploy imports it (its reference date is newer). Or import it at once as admin, without a deploy.

### `comuni-istat.source.json`

`sourceVersion` (text recorded with the import) and `referenceDate` (`yyyy-MM-dd`) are required; `source` and `originalFileSha256`
document the origin. Do not write a date the official file does not state.

## `tourist-tax/rates.csv`

Tourist tax rates researched by task RS-7 (see `TouristTaxRateSeed`).
