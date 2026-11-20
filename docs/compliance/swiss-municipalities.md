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
  and the ones the file no longer has removed (a merger, or a row of the old hand-written list) **unless a property still carries the number**: that
  row is kept as it is (the stored place of the property and the canton of the tax lines of its documents do not change), counted, and told by a
  warning of the startup log and by the stamp row of `OfficialSourceFetches` («to be confirmed by the hosts»); localities added, corrected or removed;
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
3. Update the table of `Data/Seeds/swiss/README.md` (edition, dates, SHA-256, counts) and commit the seed with it. The next deploy loads it. A
   municipality the new edition dropped (a merger) that a property still carries is **kept** in `SwissMunicipalities` and counted in a startup
   warning: list those properties with the query below and ask their hosts to confirm the new municipality in the property form; the edition
   after that drops the row. A kept municipality has no locality left, which is how the query finds it:

   ```sql
   SELECT p."Id", p."OrgId", p."Name", p."SwissMunicipalityCode"
   FROM "Properties" p
   WHERE p."SwissMunicipalityCode" IS NOT NULL
     AND NOT EXISTS (SELECT 1 FROM "SwissLocalities" l WHERE l."MunicipalityCode" = p."SwissMunicipalityCode");
   ```

Before the **first** deploy of the directory on an environment, check that no property carries a number of the old hand-written list (nothing wrote
the column before the E29 property form, so no row is expected): `SELECT "Id", "OrgId", "Name", "SwissMunicipalityCode" FROM "Properties" WHERE
"SwissMunicipalityCode" IS NOT NULL;`. A row with 5113 (it was Lugano, it is Locarno now), 5093 or 5190 (dropped) or 5237 (it was Ascona, it is Alto
Malcantone) needs its municipality confirmed by the host.

## Read API

Reference data, no personal data, for whoever may read properties (`SharedPropertyRead`, as the property form; never public):

- `GET /api/swiss/localities?query=&limit=`: `query` is the start of a postal code (one to four digits, `CH-` ignored) or a fragment of a name
  (case, accents and extra spaces ignored); up to `limit` items (default and most 20), exact matches first, each
  `{ postalCode, name, municipalityCode, municipalityName, cantonCode }`, one for each municipality a locality lies in. `6900` gives Lugano,
  Massagno and Paradiso; `locarn` gives Locarno. A query that matches nothing (or cannot: blank, one letter, five digits) gives `[]`; longer than
  100 characters is 400 `validation_error`. A search that could match and finds nothing because **the directory is not loaded** (the load at
  startup failed or was turned off: no municipality or no locality at all) is 422 `swiss_places_unavailable`, never `[]`.
- `GET /api/swiss/municipalities/{bfs}`: `{ municipalityCode, name, cantonCode }`, 404 `swiss_municipality_unknown`, or 422
  `swiss_places_unavailable` when the directory is not loaded (as the save of a municipality on a property answers).

The BFS number is a **text of digits** everywhere in the API (`"5192"`), as the entity and the year-end report already had it.

## The place of a property (one resolver)

`IPropertySwissPlaceResolver` (`PropertySwissPlaceResolver`, E29/F01/US01/T02, decisions D3 and D4) is the one place that says where a Swiss property
is. Every rule that depends on the canton reads it, instead of a copied «country CH and canton TI» test: the Ticino police notification
(`TicinoNotificationService`: the day, the file, the days to notify of the cockpit), the OTR report (`OtrTaxService`), the year-end count of Ticino
(`TouristTaxSummaryService.GetTicinoYearlyCountAsync` and its CSV/Excel), the policy of the identity documents (`IdentityVerificationFlowService`)
and `isInTicino` of `GET /api/properties/{id}`.

Rules (`SwissPlace { MunicipalityCode, MunicipalityName, CantonCode, Source, Ambiguous }`):

1. A property whose country is not `CH` has no place.
2. A stored `Property.SwissMunicipalityCode` that the directory has wins: `Source = Stored`.
3. Otherwise the place is derived from the postal code and the city (`Source = Derived`): the four digits of the postal code (a `CH-` prefix and
   spaces are ignored, `690000` gives `6900`; anything else, an Italian CAP for one, gives no place). The localities of that postal code all in one
   municipality: that one. Several: the one whose official name equals the city (lower case, accents ignored), else the one with a locality of
   that postal code named as the city. `6900` «Lugano» is Lugano (5192, TI); `6600` «Locarno» is Locarno (5113), although the locality Locarno
   of 6600 lies in Muralto too: the name of the municipality decides before the one of the locality, but **only when all the candidates lie in one
   canton**. A postal code that lies in two cantons (`6533`: Lumino TI and San Vittore GR, its postal locality «Lumino» lies in both) is the case
   where the choice changes the legal regime: there a name never decides, unless a locality of that name lies in one municipality only; `6533`
   «Lumino» and `6533` «San Vittore» are ambiguous.
4. Still several (or a city that names none of them) is **ambiguous**: `Ambiguous = true` with no municipality and no canton. The resolver never
   chooses silently; the property is not «in Ticino» until the host chooses its municipality.
5. Resolving only reads: a derived municipality is never stored behind the host's back.

On the property:

- `POST /api/properties` and `PUT /api/properties/{id}` accept `swissMunicipalityCode` (a text of one to four digits, 400 otherwise): checked
  against the directory (422 `swiss_municipality_unknown`, or `swiss_places_unavailable` when the directory is not loaded) and accepted only for a
  property in Switzerland (422 `swiss_municipality_not_applicable`; the create request has no country, so every property it creates is Italian and
  refuses it). On update `null` or empty clears it, leaving it out keeps it, a `city` or `postalCode` changed without it clears it (as for the
  comune), and the correction of the country of an imported draft clears it as it always did.
- `GET /api/properties/{id}` gives `swissMunicipalityCode` (stored), `swissMunicipalityName` and `swissCantonCode` (of the stored municipality),
  `swissPlace { municipalityCode, municipalityName, cantonCode, source, ambiguous }` (where the resolver places it, also when derived or ambiguous)
  and `isInTicino` from it. The lists carry the stored code only.

Not moved to the resolver (they read only a stored municipality, as before): the tourist tax line of a Swiss document
(`GuestDocumentLineBuilder`, `TouristTaxCollectionService`) and the canton of the Swiss tourist tax in the accountant package
(`AccountantPackageFacts.SwissCantonsAsync`): money is charged on a municipality the host confirmed. The year-end count marks a derived municipality
(`municipalityDerived`, note «to be confirmed») so that a stay without a tourist tax line there is explained.

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
