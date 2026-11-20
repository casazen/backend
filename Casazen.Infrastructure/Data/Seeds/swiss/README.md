# `swiss-localities.csv`: official Swiss localities and municipalities (E29/F01/US01/T01, NF-124)

The directory of the Swiss localities (postal code, add-on, name) and of the municipality each one lies in, with the BFS number and the canton
of the municipality. It is **derived from an official file, never written by hand**: do not edit rows; rebuild it from a newer official file
(see "Refreshing" below). The `SwissMunicipalities` table (every municipality of the file) and the `SwissLocalities` table are loaded from it.

| What | Value |
|---|---|
| Source | swisstopo, *Amtliches Ortschaftenverzeichnis mit Postleitzahl und Perimeter* (official directory of the localities with postal code and perimeter) |
| Page | https://www.swisstopo.admin.ch/de/amtliches-ortschaftenverzeichnis |
| File | STAC item `ortschaftenverzeichnis_plz` of the collection `ch.swisstopo-vd.ortschaftenverzeichnis_plz` (https://data.geo.admin.ch/api/stac/v0.9/collections/ch.swisstopo-vd.ortschaftenverzeichnis_plz/items), asset `ortschaftenverzeichnis_plz_2056.csv.zip`, which holds `AMTOVZ_CSV_LV95/AMTOVZ_CSV_LV95.csv` |
| Edition | asset updated **2026-11-01** (the CSV inside the zip is dated 2026-10-29) |
| Downloaded | **2026-11-20** |
| Licence | open government data, free use with the source credit «swisstopo» (https://www.swisstopo.admin.ch/ogd-conditions) |
| SHA-256 of the zip | `f7a99833d11e773916a57baebe7374040f4fbde0c8821250c77fa7197a176978` |
| SHA-256 of the CSV read | `0710c74be9b072d813ed294a6d8e1383bf44fd66dc62237e0d0f08b331178fe0` |
| SHA-256 of this seed | `4203d14277f8ef010bb161abd452452cf2320abd5037d7ecfd725b61a0c6b9c7` |
| Rows | 5,697 (one per locality and municipality): 4,060 localities, 2,112 municipalities, 26 cantons |
| Left out | 20 rows of Liechtenstein (11 municipalities, no canton): the directory covers the Principality too |

Only the derived seed is committed: the zip and the CSV of swisstopo are not.

## Columns

UTF-8 without BOM, separator `;`, LF, one header line, sorted by postal code, add-on, locality and municipality number:

| Column | Meaning |
|---|---|
| `postal_code` | the four digits of the postal code (PLZ4) |
| `add_on` | the two-digit add-on of the postal code (Zusatzziffer): `6900` `00` is Lugano, `6900` `05` Massagno |
| `locality` | the name of the locality (Ortschaftsname) |
| `municipality_code` | the BFS number of the municipality the locality (or part of it) lies in |
| `municipality` | the official name of the municipality (Gemeindename) |
| `canton` | the two letters of the canton (Kantonskürzel) |

A locality that lies in several municipalities has one row for each (`6600` Locarno lies in Locarno and in Muralto). The share of addresses, the
coordinates and the language of the official file are not kept.

## How it is loaded

`SwissPlacesSeedHostedService` reads the file compiled into `Casazen.Infrastructure` at startup (`OfficialReferenceData__SeedSwissPlacesOnStartup`,
on by default): under an advisory lock it makes the two tables equal to the file (municipalities upserted by BFS number and the ones the file no
longer has removed, localities added, corrected or removed) and records the SHA-256 of the file in `OfficialSourceFetches` (dataset
`swisstopo_localities`). The next startup with the same file and the same row counts does nothing. A build without the file, or a file that
does not parse, only logs a warning or an error: the API starts anyway.

## Refreshing

1. Find the asset `ortschaftenverzeichnis_plz_2056.csv.zip` of the STAC item above, and download it into a **new, empty folder outside the
   repository** (the file is data of a third party):
   `curl.exe -f -L -A "CasaZen-swiss-places/1.0 (https://github.com/casazen/backend)" -o <new folder>/ortschaftenverzeichnis_plz_2056.csv.zip https://data.geo.admin.ch/ch.swisstopo-vd.ortschaftenverzeichnis_plz/ortschaftenverzeichnis_plz/ortschaftenverzeichnis_plz_2056.csv.zip`
2. List the zip (`unzip -l`) and extract only `AMTOVZ_CSV_LV95/AMTOVZ_CSV_LV95.csv` into another new folder.
3. From the repository: `node scripts/build-swiss-places.mjs <folder>/AMTOVZ_CSV_LV95/AMTOVZ_CSV_LV95.csv Casazen.Infrastructure/Data/Seeds/swiss`.
   It stops, naming the line, if a row looks wrong (a postal code that is not four digits, a canton that is not one of the 26, a municipality
   with two names or two cantons) or if one of the anchor municipalities (Lugano 5192, Locarno 5113, Bellinzona 5002, Mendrisio 5254, Ascona
   5091, Zürich 261, Bern 351, Genève 6621) changed: read the change before updating the anchors.
4. Update the table above (edition, dates, SHA-256, counts) and commit the seed with it. The next deploy loads it.

`.gitattributes` keeps the bytes of the seed (`-text`): the SHA-256 recorded at load time is the one of exactly this file.
