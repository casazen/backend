# Swiss Municipalities and Localities Reference Data

## Overview

CasaZen keeps the official directory of the Swiss municipalities (communes/Gemeinden/comuni) and of their localities with postal code, for the
properties located in Switzerland (Country = 'CH'). It says in which municipality, and so in which canton, a property is: the canton decides the
rules of guest registration (the Ticino police notification, the OTR report, the year-end count of Ticino, the policy of the identity documents).
Requirement NF-124, task E29/F01/US01/T01 (directory) and E29/F01/US01/T02 (place of a property).

## Data source

| What | Value |
|---|---|
| Authority | Federal Office of Topography **swisstopo** (the directory is kept by swisstopo under art. 24 of the ordinance on geographic names, GeoNV) |
| Dataset | *Amtliches Ortschaftenverzeichnis mit Postleitzahl und Perimeter* (official directory of the localities with postal code and perimeter) |
| Page | https://www.swisstopo.admin.ch/de/amtliches-ortschaftenverzeichnis |
| Download | STAC API of data.geo.admin.ch, collection `ch.swisstopo-vd.ortschaftenverzeichnis_plz`, asset `ortschaftenverzeichnis_plz_2056.csv.zip` |
| Licence | open government data: free use, the source must be credited as «swisstopo» (https://www.swisstopo.admin.ch/ogd-conditions) |
| Edition in the repository | asset of 2026-11-01, downloaded on 2026-11-20: 2,112 municipalities, 4,060 localities, 5,697 rows (one per locality and municipality) |

The BFS numbers (Gemeindenummern) of the directory are the ones of the Federal Statistical Office (BFS/OFS/UST), the stable identifiers used by the
Swiss authorities. Numbers checked against the file: **Lugano 5192, Locarno 5113, Bellinzona 5002, Mendrisio 5254, Ascona 5091**. The hand-written
list used before (48 municipalities, 2026-10-24) had Lugano as 5113 (which is Locarno), Locarno as 5093 and Mendrisio as 5190 (numbers that no
longer exist) and Ascona as 5237 (which is Alto Malcantone): it is replaced.

Only the derived seed is in the repository: `Casazen.Infrastructure/Data/Seeds/swiss/swiss-localities.csv`, with its provenance (SHA-256 of the
download and of the seed, counts) in `Casazen.Infrastructure/Data/Seeds/swiss/README.md`. The 20 rows of Liechtenstein that the directory also
covers (no canton) are left out.

## Structure

`SwissMunicipalities` (every municipality of the directory):

- **MunicipalityCode**: the BFS number, digits without leading zeros, primary key. Examples: "5192" (Lugano), "261" (Zürich), "351" (Bern)
- **CantonCode**: ISO 3166-2:CH canton code, 2 letters (TI, ZH, BE, GE, VD...)
- **OfficialName**: the official name of the municipality as the directory writes it ("Lugano", "Zürich", "Biel/Bienne")

`SwissLocalities` (one row per locality and municipality: a locality can lie in several municipalities, «6600 Locarno» lies in Locarno and in
Muralto):

- **PostalCode** (4 digits) and **AddOn** (2 digits, the Zusatzziffer: `6900` `00` is Lugano, `6900` `05` Massagno)
- **Name**: the name of the locality; **NormalizedName**: the same in lower case without diacritics, for the search
- **MunicipalityCode** and **CantonCode** of the municipality

Key: postal code, add-on, municipality and name (the key starts with the postal code, which is the index of the postal code lookups); indexes on the
normalised name and on the municipality. Migration `AddSwissLocalities`. No tenant filter: platform reference data, the same for every org.

## Canton codes

| Code | Canton Name (EN) | Canton Name (DE/FR/IT) |
|------|------------------|------------------------|
| AG   | Aargau | Aargau |
| AI   | Appenzell Innerrhoden | Appenzell Innerrhoden |
| AR   | Appenzell Ausserrhoden | Appenzell Ausserrhoden |
| BE   | Bern | Bern / Berne |
| BL   | Basel-Landschaft | Basel-Landschaft / Bâle-Campagne |
| BS   | Basel-Stadt | Basel-Stadt / Bâle-Ville |
| FR   | Fribourg | Freiburg / Fribourg |
| GE   | Geneva | Genf / Genève / Ginevra |
| GL   | Glarus | Glarus |
| GR   | Graubünden | Graubünden / Grisons / Grigioni |
| JU   | Jura | Jura |
| LU   | Lucerne | Luzern / Lucerne |
| NE   | Neuchâtel | Neuenburg / Neuchâtel |
| NW   | Nidwalden | Nidwalden |
| OW   | Obwalden | Obwalden |
| SG   | St. Gallen | St. Gallen / Saint-Gall |
| SH   | Schaffhausen | Schaffhausen |
| SO   | Solothurn | Solothurn / Soleure |
| SZ   | Schwyz | Schwyz |
| TG   | Thurgau | Thurgau / Thurgovie |
| TI   | Ticino | Tessin / Ticino |
| UR   | Uri | Uri |
| VD   | Vaud | Waadt / Vaud |
| VS   | Valais | Wallis / Valais |
| ZG   | Zug | Zug / Zoug |
| ZH   | Zürich | Zürich / Zurich |

## How the directory is loaded

`SwissPlacesSeedHostedService` loads the seed compiled into `Casazen.Infrastructure` at startup (`SwissMunicipalitySeed.ApplyAsync`), when
`OfficialReferenceData__SeedSwissPlacesOnStartup` is on (the default; off in the test configuration):

- every line of the file is checked (four-digit postal code, two-digit add-on, BFS number, names, one of the 26 cantons, one name and one canton per
  municipality, no row twice): a file with one wrong line is refused as a whole, logged as an error, and nothing is written;
- under an advisory lock (instances starting together load it once) the two tables are made equal to the file: municipalities upserted by BFS number
  and the ones the file no longer has removed (a merger, or a row of the old hand-written list), localities added, corrected or removed;
- the SHA-256 of the file is recorded in `OfficialSourceFetches` (dataset `swisstopo_localities`): the next startup with the same file and the same
  row counts does nothing (version stamp);
- a build without the file only logs a warning, and a failure is logged as an error: the API starts anyway.

A new database (a new environment, the template of the integration tests) gets no municipality from the migrations any more: the old migration
`AddSwissMunicipality` no longer inserts the hand-written list, and the startup load fills the tables.

## Refreshing the directory

1. Download the asset `ortschaftenverzeichnis_plz_2056.csv.zip` of the STAC item `ortschaftenverzeichnis_plz` into a **new, empty folder outside the
   repository** (the exact `curl.exe` command is in `Data/Seeds/swiss/README.md`), list it and extract only `AMTOVZ_CSV_LV95/AMTOVZ_CSV_LV95.csv`.
2. From the repository: `node scripts/build-swiss-places.mjs <folder>/AMTOVZ_CSV_LV95/AMTOVZ_CSV_LV95.csv Casazen.Infrastructure/Data/Seeds/swiss`.
   The generator reads the file as untrusted data (strict parsing, nothing executed, paths as arguments), stops naming the line at the first row
   that looks wrong, and stops when one of the anchor municipalities (Lugano 5192, Locarno 5113, Bellinzona 5002, Mendrisio 5254, Ascona 5091,
   Zürich 261, Bern 351, Genève 6621) changed: read the change (a merger, a renumbering) before updating the anchors.
3. Update the table of `Data/Seeds/swiss/README.md` (edition, dates, SHA-256, counts) and commit the seed with it. The next deploy loads it; the
   properties whose stored municipality the new edition removed get their place derived again from their postal code.

## Read API

Reference data, no personal data, for whoever may read properties (`SharedPropertyRead`, as the property form; never public):

- `GET /api/swiss/localities?query=&limit=`: `query` is the start of a postal code (one to four digits, `CH-` ignored) or a fragment of a name
  (case, accents and extra spaces ignored); up to `limit` items (default and most 20), exact matches first, each
  `{ postalCode, name, municipalityCode, municipalityName, cantonCode }`, one for each municipality a locality lies in. `6900` gives Lugano,
  Massagno and Paradiso; `locarn` gives Locarno. A query that matches nothing (or cannot: blank, one letter, five digits) gives `[]`; longer than
  100 characters is 400 `validation_error`.
- `GET /api/swiss/municipalities/{bfs}`: `{ municipalityCode, name, cantonCode }`, 404 `swiss_municipality_unknown`.

The BFS number is a **text of digits** everywhere in the API (`"5192"`), as the entity and the year-end report already had it.

## Differences from Italian Comuni

Unlike the Italian `Comuni` table (ISTAT codes, cadastral codes, provinces, regions, active/inactive rows with validity dates, an import log with
SHA-256 and an admin upload), the Swiss directory has: BFS numbers, cantons and official names; the localities with their postal codes; one load
log row per seed file in `OfficialSourceFetches`; no admin upload (a new edition comes with a deploy).

## References

- swisstopo, official directory of the localities: https://www.swisstopo.admin.ch/de/amtliches-ortschaftenverzeichnis
- Swiss Federal Statistical Office, official commune register: https://www.bfs.admin.ch/bfs/en/home/basics/swiss-official-commune-register.html
- ISO 3166-2:CH (canton codes): https://en.wikipedia.org/wiki/ISO_3166-2:CH
- ADR-001 (Multi-country properties): `docs/adr/ADR-001-custom-domain-booking.md`
- NF-02 (Property country and currency), NF-124 (Swiss location of a property)
