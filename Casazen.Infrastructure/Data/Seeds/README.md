# Seeds

## `comuni-istat.csv`: official ISTAT list of the comuni (SU-04)

The list of the Italian comuni that the comune pickers, the validation of `Property.ComuneIstatCode` and of the comuni of a
supplier profile, the regional rules and the CIN check are built on. It is **an official file, not written by hand**: it must never be
edited row by row. To update it, replace the whole file with a newer official one (see "Updating" below).

| What | Value |
|---|---|
| Source | ISTAT, *Elenco dei comuni italiani* (permalink CSV) |
| Catalog | https://www.istat.it/classificazione/codici-dei-comuni-delle-province-e-delle-regioni/ |
| File | https://www.istat.it/storage/codici-unita-amministrative/Elenco-comuni-italiani.csv |
| Reference date | **2026-02-21** ("aggiornato al 21 febbraio 2026") |
| Retrieved | **2026-10-09** |
| SHA-256 of this CSV | `57eaf945182fc64fa05f80f1a5fb2a84cff55ebde2af6e3947a731776e7809e0` |
| Rows | 7,896 comuni, 20 regions, 107 province codes |

The CSV is the official permalink file as downloaded (UTF-8, separator `;`, quoted header cells that contain line breaks). No value was changed. The cadastral code comes from the Agenzia delle Entrate (`N.d.` = not available). The Hangfire job `official-reference-data-refresh` re-downloads this URL every day.

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

## `comuni-centroidi.csv`: the centre of every comune (public position of a property)

Where the public maps put a property whose host gave no position (`Property.Latitude` and `Longitude` both `0` = "not set", which is what
every property created with the property wizard has: it asks for no coordinates). `ComuneCentroids` reads it once from the assembly and
`PublicPropertyPosition` uses it in the public projections (the booking site, the search, the property page); the stored property is never
changed, and the crawler pages (JSON-LD) still publish no position for it. The centre is the middle of the comune, not of the house, and
it is shown rounded to about a kilometre.

| What | Value |
|---|---|
| Source | Wikidata, **CC0 1.0**: comuni of Italy (Q747074, or a sparse municipality, Q954172) with an ISTAT code (P635) and a coordinate (P625), not dissolved (P576) |
| Query | `scripts/comuni-centroidi.rq`, asked to `https://query.wikidata.org/sparql` as `text/csv` |
| Built by | `scripts/build-comuni-centroidi.mjs`, which checks every row and writes `comuni-centroidi.csv` and `comuni-centroidi.source.json` |
| Columns | `istat_code,latitude,longitude`: 6 digits (leading zeros kept), degrees with 4 decimals, `.` as decimal point, LF |
| Coverage | every comune of `comuni-istat.csv` (7,896); 137 of them by name, see below |
| Rows | 8,033: the 7,896 codes of the official list plus the 137 new codes of Sardinia |

**The codes of Sardinia.** Since 2025 the provinces of Sardinia changed and Wikidata already carries the new ISTAT codes (`112001` for
Alghero) while the official list still has the old ones (`090003`). The builder matches those comuni by name (only against codes the
official list does not have, and only when the match is unique) and writes **both** codes with the same centre, so a property that
holds either is found. Wikidata also keeps comuni that no longer exist (merged or suppressed): their codes are left out.

`.gitattributes` keeps the bytes of the CSV (`-text`): `seedSha256` in `comuni-centroidi.source.json` is the SHA-256 of exactly this
file and a test compares them. The same test file checks that every row is readable and inside Italy, that the file has the rows
the source says, and that at least 99 % of the comuni of the official list have a centre.

### Building it again

After replacing `comuni-istat.csv` (the official list changed), or when comuni were born of a merger:

1. Ask Wikidata (the folder must be new and empty: the answer is data of a third party):
   `curl -X POST https://query.wikidata.org/sparql -H "Accept: text/csv" -H "User-Agent: CasaZen-comune-centroids/1.0 (https://github.com/casazen/backend)" --data-urlencode "query@scripts/comuni-centroidi.rq" -o <new folder>/wikidata-comuni.csv`
2. `node scripts/build-comuni-centroidi.mjs <new folder>/wikidata-comuni.csv Casazen.Infrastructure/Data/Seeds/comuni-istat.csv Casazen.Infrastructure/Data/Seeds`
   It stops, naming the row, if anything looks wrong (a position outside Italy, two positions of one code far apart, a comune far from the
   others of its province, a well-known place at the wrong coordinates) and prints how many comuni got a centre.
3. Commit the two files it wrote together, and update the table above (rows, coverage).

## `tourist-tax/rates.csv`

Tourist tax rates researched by task RS-7 (see `TouristTaxRateSeed`). There is **no national open dataset**. The daily job re-reads only the institutional URLs already stored for the pilot comuni; it never invents amounts. HTML/PDF extraction is not deterministic, so a fetch is logged in `OfficialSourceFetches` (`extract_failed`) and existing amounts stay as they are.

## `alloggiati/`

Official Alloggiati Web code tables (CO-12), public download 2026-10-09, no login. See `alloggiati/source.json` for URLs and SHA-256. Loaded at startup when a table is empty; the same daily job refreshes them when the portal file changed.
