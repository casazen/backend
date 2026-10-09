# Runbook: official ISTAT list of the comuni

Task SU-04 (audit defects A4-12, A5-34, A8-24, A5-19), data of task RS-6. This page explains what the list is, where it comes from,
how it is loaded and updated, what the product does with it and what it does while it is missing.

## Status of RS-6 (the official dataset)

| | |
|---|---|
| Source | ISTAT, *Elenco dei comuni italiani*, permalink CSV of the classification page |
| Catalog | https://www.istat.it/classificazione/codici-dei-comuni-delle-province-e-delle-regioni/ |
| File | https://www.istat.it/storage/codici-unita-amministrative/Elenco-comuni-italiani.csv |
| Reference date | **2026-02-21** ("aggiornato al 21 febbraio 2026" on the catalog page) |
| Retrieved | **2026-10-09** from the permalink above (official file, not rewritten) |
| SHA-256 of the CSV | `57eaf945182fc64fa05f80f1a5fb2a84cff55ebde2af6e3947a731776e7809e0` |
| In the repository | `Casazen.Infrastructure/Data/Seeds/comuni-istat.csv` (the official permalink file as downloaded), `comuni-istat.source.json` with URL, authority, date and SHA-256 |
| State | **Done.** Loaded at startup from the seed; the Hangfire job `official-reference-data-refresh` re-downloads the permalink every day at 04:30 UTC and imports only when the SHA-256 changed |

The cadastral code of the file comes from the Agenzia delle Entrate (`N.d.` = not available, kept as null). Sheets `NOTE` and `Legenda`
of the workbook explain the columns: the ISTAT code of a comune is the (historical) province code plus the progressive number.

### Where to get a newer file

The scheduled job downloads the permalink CSV every day. To replace the seed of the deploy (optional, the job is enough):

1. ISTAT, page **"Codici dei comuni, delle province e delle regioni"**: https://www.istat.it/classificazione/codici-dei-comuni-delle-province-e-delle-regioni/
2. Permalink CSV: https://www.istat.it/storage/codici-unita-amministrative/Elenco-comuni-italiani.csv
3. ISTAT updates it after every change of the territorial classification (mergers, name changes, province changes); the "aggiornato al" date is on the catalog page.

As a cross-check, the **ANPR** archive of the comuni (Ministero dell'Interno) can show suppressed comuni; do not replace the ISTAT list with it.

## File format accepted

Used by the seed file and by the admin upload, the same code (`ComuneImportService`).

- Text file, at most **10 MB**, **UTF-8** (with or without BOM) or **Windows-1252**; separator detected on the header: `;` (the ISTAT one),
  `,`, tab or `|`; cells may be enclosed in `"`.
- First non-empty line: the header. Columns are found by the **text of the header** (accents, case, spaces and punctuation ignored), in any
  order; other columns are ignored. Required:

| Field | Header of the ISTAT file (found first) | Accepted synonyms |
|---|---|---|
| ISTAT code | `Codice Comune formato alfanumerico` | `Codice ISTAT`, `Codice ISTAT del Comune`, `CODISTAT`, `ISTAT`, `Codice Comune`; as a last resort `Codice Comune formato numerico` (the leading zeros are put back) |
| Name in Italian | `Denominazione in italiano` | `Denominazione italiana`, `DENOMINAZIONE_IT`, `Nome comune`, `Denominazione`, `Comune` |
| Cadastral code | `Codice Catastale del Comune` | `Codice catastale`, `Codice Belfiore`, `CODCATASTALE`, `Belfiore` |
| Province | `Sigla automobilistica` | `Sigla provincia`, `Sigla`, `Targa`, `Provincia` |
| Region | `Codice Regione` and/or `Denominazione Regione` | `CODREGIONE`, `Regione`, `Nome regione` |

  Optional: `Denominazione (Italiana e straniera)` (the bilingual name, also searched), `Data istituzione` / `Data inizio validità` and
  `Data cessazione` / `Data fine validità` (dates `dd/MM/yyyy`, `yyyy-MM-dd`, `dd-MM-yyyy`).
- If the official file words a header differently and no synonym covers it, the file is refused **naming the column**
  (`istat_column_missing`, ...). Rename only the header line, never the rows.
- Checks per row (all or nothing: **one invalid line rejects the whole file**, nothing is written, the response lists up to 50 lines):

| Error (`error` of a line) | Meaning |
|---|---|
| `column_count_mismatch` | the row has a different number of cells than the header |
| `istat_invalid` | not 6 digits (4 or 5 digits get their leading zeros back, anything else is refused) |
| `name_missing`, `name_too_long` | empty name, more than 100 characters |
| `cadastral_invalid` | not a letter and 3 digits (`H501`); `N.d.` is accepted as "not available" |
| `province_invalid` | not two letters |
| `region_unknown` | the region code is not one of `01`-`20`, or the name is not one of the 20 regions |
| `region_inconsistent` | the code and the name of the region of the same row do not agree (a shifted column) |
| `date_invalid`, `validity_inconsistent` | unreadable date; end of validity before the start |
| `duplicate_istat_code`, `duplicate_cadastral_code` | repeated code (the cadastral code must be unique among the **active** comuni) |
| `conflicts_with_stored_comuni` | the rows are valid but clash with comuni already stored (e.g. a partial file giving a cadastral code held by another active comune) |

  Whole-file errors: `empty_file`, `no_rows`, `file_too_large`, `unreadable_encoding`, `source_version_missing`,
  `reference_date_in_future`, `reference_date_older_than_current`, and the missing-column ones above.
- **Idempotent**: rows are upserted by ISTAT code and an unchanged row is not touched, so the same file again changes nothing (the import is
  logged anyway). **Full list by default**: the active comuni the file does not have are *deactivated*, never deleted (a code already stored
  keeps resolving; the row says when and by which import). `partial=true` leaves them alone.
- A file whose **reference date is older** than the list already imported is refused: an old file never undoes a newer one.

## How the list is loaded

### 1. At deploy (seed file)

`ComuneSeedHostedService` imports the embedded `comuni-istat.csv` at startup when the database has **no list or an older one** (by
reference date). It never replaces a list that is as recent or more (for example one an admin imported later), is safe with several
instances (advisory lock) and never stops the API if it fails (it logs the reason). `Comuni__SeedOnStartup=false` turns it off. A seed
file without `comuni-istat.source.json` (`sourceVersion`, `referenceDate`) is refused: nothing is invented for it.

The import takes about a second. After the first deploy the log says `Comuni list imported (StartupSeed): 7896 rows, 7896 new...`.

### 3. Every day (scheduled download)

Hangfire job `official-reference-data-refresh` (`OfficialReferenceDataRefreshJob`, 04:30 UTC): reads the catalog page for the "aggiornato al" date, downloads the permalink CSV, and imports it when the SHA-256 differs from the last `ComuneImports` row. Hosts outside ISTAT / `*.gov.it` / `comune.*.it` are refused. `OfficialReferenceData__Enabled=false` turns the job into a no-op.

### 2. By an admin (any later official file, no deploy)

```bash
API=https://<railway-url>   # test first, then production
curl -sS -X POST "$API/api/admin/comuni/import" \
  -H "Authorization: Bearer $ADMIN_JWT" \
  -F "file=@Elenco-comuni-italiani.csv" \
  -F "sourceVersion=ISTAT, Elenco dei comuni italiani, aggiornato al 31/12/2026" \
  -F "referenceDate=2026-12-31"          # the "aggiornato al" date of the file; add -F partial=true for a part of the list
curl -sS "$API/api/admin/comuni" -H "Authorization: Bearer $ADMIN_JWT"   # rows, last import, pilot comuni not in the list
```

200 `{ importId, rows, inserted, updated, unchanged, deactivated }`; 422 `comuni_import_invalid` with `lines: [{ line, error }]`; 400 for a
missing field; 403 for a non-admin. The same pattern as the Alloggiati tables (`alloggiati.md`, CO-12).

### Updating the seed file

Replace `Casazen.Infrastructure/Data/Seeds/comuni-istat.csv` with the new official file (same layout), update `comuni-istat.source.json`
(`sourceVersion`, `referenceDate`, `originalFileSha256`) and the table of the folder `README.md`, commit them together. The next deploy
imports it because its reference date is newer. Without a new reference date nothing is imported (use the admin upload).

## Check after an import

```sql
-- Rows, active rows, imports (expected after the seed: 7896 active).
SELECT count(*) AS total, count(*) FILTER (WHERE "IsActive") AS active FROM "Comuni";
SELECT "Origin", "SourceVersion", "ReferenceDate", "RowCount", "InsertedCount", "UpdatedCount", "UnchangedCount", "DeactivatedCount", "ImportedAt"
FROM "ComuneImports" ORDER BY "ImportedAt" DESC;

-- The cases the old registry got wrong (expected: Milano, Firenze, Torino, Genova, Varenna in LC).
SELECT "IstatCode", "Name", "CadastralCode", "ProvinceCode" FROM "Comuni"
WHERE "CadastralCode" IN ('F205', 'D612', 'H501') OR "IstatCode" IN ('001272', '010025', '097084', '013250', '013145');

-- Every active comune has a known region (expected: 0).
SELECT count(*) FROM "Comuni" WHERE "IsActive" AND "RegionIstatCode" NOT IN ('01','02','03','04','05','06','07','08','09','10','11','12','13','14','15','16','17','18','19','20');
```

`GET /api/health/ready` reports `comuni`: **degraded** while the list is not imported (the API works; the pickers say so) or when a pilot comune
of `Suppliers:PilotComuni` is not in it; **healthy** otherwise, with the reference date. `GET /api/comuni/status` is the public version.

## What the product does with it

| Where | Behavior |
|---|---|
| `GET /api/comuni?q=` | Public (open data, no org scope), rate limited (`RateLimiting:PublicComuni`, 120 per minute per IP). Active comuni whose name starts with, then contains, `q` (accents, case and punctuation ignored, also the name in the other language, e.g. `Bozen`); a 6-digit `q` is an ISTAT code, a letter and 3 digits a cadastral code. 2 to 100 characters, at most 25 results. **While the list is not imported: 200 `{ datasetAvailable: false, items: [] }`**, never an empty list that looks like "no match". `GET /api/comuni/{istatCode}` also returns a comune no longer in the list (`isActive: false`). |
| Property | `comuneIstatCode` (6 digits, nullable) and `regionCode` (CasaZen's code, `LOM`...) on create/update (`POST/PUT /api/properties`). Validated against the list (422 `comune_istat_unknown`, `comuni_dataset_unavailable`), the region follows it. **Never inferred from the free-text city**: existing properties keep `null` until the host chooses a comune. A `city` changed without a comune clears the stored one. Clear it with `comuneIstatCode: null`. |
| Regional documents (A5-19) | `Compliance:RequiredDocuments` is read by the property's **region code** (`LOM`, `LAZ`; other regions and properties with no comune get `default`). Choosing a comune in Lombardia therefore asks for the APE, in Lazio for the `PropertyLicense` (configuration as it is). **A property already `Active` that lacks a document required by its region is suspended at the next evaluation** (the nightly check or its next save) and the host is emailed: warn hosts before they choose the comune, or complete the documents. The activation wizard shows a non-blocking note on step `base-data` (`comune_istat_missing`) while the list is imported and no comune is chosen. |
| CIN | `CinFormat.HasIstatComuneMismatch(cin, property.ComuneIstatCode)`: non-blocking warning `cin_istat_comune_mismatch` on step `cin` of the wizard and `cinIstatMismatch` on `GET /api/properties/{id}` and `/detail`. The CIN is never changed or refused for it (relocations, reclassifications). |
| Tourist tax | Rates carrying an ISTAT code are matched by the property's code, the others by the normalized city name (as before): `TouristTaxComune.ForProperty`. The public page of a comune in the wizard is found by code. |
| Supplier profile | `comuneIstatCodes` (chosen from the list, validated, at most 100) next to the free text `comuni` (kept as written). `GET /api/supplier/profile` returns both and `operatingComuni` (name, province, region). A supplier covers the union of the two. |
| Supplier matching (A4-12) | `GET /api/suppliers`, the long-rent search, supplier match and service requests: **by ISTAT code** when the property has a chosen comune; the written entries of a supplier (ISTAT code, cadastral code such as `H501`, a unique name) are resolved against the list, and only when something cannot be resolved (list not imported, ambiguous name such as `Castro`, `Livo`) the written name is compared as before. English names (`Rome`) are no longer recognized. |
| Admin invite | With the list imported, `comuneCode` of `POST /api/admin/suppliers/invite` must be an active comune (ISTAT code, cadastral code or unique name) and is stored as its ISTAT code (422 `comune_istat_unknown`); without the list it stays as written. |
| Pilot comuni (`Suppliers:PilotComuni`) | The `Code` is the **ISTAT code** (`Suppliers__PilotComuni__0__Code=058091`). With the list imported it is validated and the name shown is the one of the list; a code that is not an active comune is not offered (self-serve is off when none remains) and is reported by the health check and by `GET /api/admin/comuni`. A cadastral code in the configuration is turned into the ISTAT one. Without the list the configuration is used as written. |
| SEO pages | The pilot comuni of the SEO content are **names and provinces** (`SeoPilotComuni`: Como, Bellagio, Menaggio, Varenna, Milano, Roma, Firenze, Torino, Napoli, Venezia, Bologna, Palermo); code, region and slugs come from the list. A pilot not found in it is skipped. The bootstrap writes no marker while there is no list, so it runs at the first start after the import. |

### Region codes

`Comune.RegionCode` is CasaZen's three-letter code, derived from the ISTAT region code with the closed table `ItalianRegions` (20 regions,
Costituzione art. 131, ISTAT numbering `01`-`20`). The eight codes already used by the configuration, the tourist tax rates and the SEO pages
keep their meaning (`PIE`, `LOM`, `VEN`, `EMR`, `TOS`, `LAZ`, `CAM`, `SIC`); the other twelve (`VDA`, `TAA`, `FVG`, `LIG`, `UMB`, `MAR`, `ABR`, `MOL`,
`PUG`, `BAS`, `CAL`, `SAR`) are CasaZen's own abbreviations, not an official code. The import refuses a row whose region code is not in the table
or does not match its region name.

## Errors found in the old hard-coded registry

`ItalianComuneRegistry` (12 comuni written by hand) is gone. Compared with the official list it had:

| Comune | Old registry | Official list |
|---|---|---|
| Torino | `010025` (that is **Genova**) | `001272`, cadastral `L219`, TO |
| Bellagio | `013040` (that is Campione d'Italia) | `013250`, `M335`, CO |
| Menaggio | `013133` (that is Lomazzo) | `013145`, `F120`, CO |
| Varenna | `013182` (no such comune) | `097084`, `L680`, province **LC** |
| Firenze | cadastral `F205` (invites) | `048017`, cadastral `D612`; **`F205` is Milano** (`015146`) |

The other eight (Como, Milano, Roma, Firenze, Napoli, Venezia, Bologna, Palermo) had the right ISTAT code.

### Data migration `AddComuniIstat`

Creates `Comuni` and `ComuneImports` (empty: the seed or an admin fills them), adds `Properties.ComuneIstatCode`/`RegionCode` and
`SupplierProfiles.ComuneIstatCodesJson` (`[]`), all empty: **nothing is inferred from existing free text**. It also corrects the codes that
the old registry wrote into data (SQL in the migration class, public constants, covered by a test): `SeoContentPages` and `SignupAttributions` of
Torino `010025` → `001272`, Bellagio `013040` → `013250`, Menaggio `013133` → `013145`, Varenna `013182` → `097084`
(the pages: only where the slug is the comune's, so a page of Genova can never be touched). Down restores the schema only.

```sql
-- No SEO page or attribution may still carry an old wrong code (expected: 0 rows).
SELECT "Slug", "ComuneCode" FROM "SeoContentPages" WHERE "ComuneCode" IN ('013040', '013133', '013182');
SELECT count(*) FROM "SignupAttributions" WHERE "ComuneCode" IN ('013040', '013133', '013182');
-- Properties that still have no comune chosen (hosts choose it in the property form): the audit list.
SELECT count(*) FILTER (WHERE "ComuneIstatCode" IS NULL) AS without_comune, count(*) AS total FROM "Properties" WHERE "IsActive";
```

Free-text comuni of suppliers and invites (`H501`, `Roma`) stay as written and keep matching through the list (see the table above); the supplier
and the admin replace them by choosing from the list.

## When the list is missing

Nothing breaks and nothing is invented: pickers show "list not available" and the forms fall back to free text (the city of a property, the
comuni of a supplier, the code of an invite); `POST`/`PUT` with a comune code answer 422 `comuni_dataset_unavailable`; supplier matching compares
the written names; the SEO bootstrap and `POST /api/admin/seo/generate` without codes say so (422) instead of queueing nothing; the health check
is `degraded`. Import the file as above to leave this state.

## Frontend

`ComunePicker` (`src/components/shared/comune-picker.tsx`): accessible autocomplete (ARIA combobox), IT/EN, states loading, search in progress,
no match, error with retry (never shown as "no result"), "list not available" with the free-text fallback of the form, region derived and shown
(never chosen). Used in the property form (`city` follows the chosen comune), in the supplier profile and activation (`SupplierComuniField`: chips,
text not linked to the list flagged), and in the admin invite page.
