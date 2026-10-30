# Swiss Municipalities Reference Data

## Overview

CasaZen maintains a reference table of Swiss municipalities (communes/gemeinden) for properties located in Switzerland (Country = 'CH'). This data is used for property location tracking and future compliance requirements specific to Swiss cantons and municipalities.

## Data Source

**Authority**: Ufficio federale di statistica (BFS) / Office fédéral de la statistique (OFS) / Federal Statistical Office (FSO)

**Official Source**: Swiss Official Commune Register  
**URL**: https://www.bfs.admin.ch/bfs/en/home/basics/swiss-official-commune-register.html

**Import Date**: October 24, 2026

**Data Format**: The BFS maintains an official register of all Swiss municipalities with unique identifiers (BFS numbers / Gemeindenummern).

## Structure

The `SwissMunicipalities` table contains:

- **MunicipalityCode**: Official BFS municipality number (Gemeindenummer / Numéro OFS / Numero UST)
  - Primary key
  - Examples: "5113" (Lugano), "261" (Zürich), "351" (Bern)
  - Stable identifier used by Swiss federal authorities

- **CantonCode**: ISO 3166-2:CH canton code (2 characters)
  - Examples: "TI" (Ticino), "ZH" (Zürich), "BE" (Bern), "GE" (Geneva), "VD" (Vaud)
  - See: https://en.wikipedia.org/wiki/ISO_3166-2:CH

- **OfficialName**: Official municipality name
  - Examples: "Lugano", "Zürich", "Bern", "Genève", "Lausanne"
  - For bilingual/trilingual cantons, this is typically the primary official language variant

## Canton Codes Reference

Switzerland has 26 cantons. The main cantons represented in the seed data:

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

## Usage in Property Entity

The `Property` entity has an optional `SwissMunicipalityCode` field that references this table:

```csharp
/// <summary>
/// Swiss municipality code (official BFS number) for properties in Switzerland (Country = 'CH').
/// Null for non-Swiss properties. Foreign key to SwissMunicipalities table.
/// </summary>
[MaxLength(10)]
public string? SwissMunicipalityCode { get; set; }
```

- This field is **nullable** and applies only to Swiss properties (Country = 'CH')
- For Italian properties, the existing `ComuneIstatCode` field is used instead
- No foreign key constraint is enforced (similar to `ComuneIstatCode`) to allow flexibility

## Maintenance

The seed data includes major Swiss municipalities across all 26 cantons, with a focus on:
- Major cities (Zürich, Geneva, Bern, Basel, Lausanne, etc.)
- Tourist destinations (Zermatt, St. Moritz, Davos, Interlaken, Montreux, etc.)
- Regional centers likely to have short-term rental properties

**Update Process**: When the BFS releases updated municipality registers (typically due to municipal mergers or administrative changes), the data should be refreshed following the same import pattern used for Italian comuni.

**Future Enhancement**: Consider implementing an admin import interface similar to the Italian comuni import (`AdminComuniController`) if Swiss municipality data requires regular updates.

## Differences from Italian Comuni

Unlike the Italian `Comuni` table which includes:
- ISTAT codes (6 digits)
- Cadastral (Belfiore) codes
- Province codes
- Regional codes
- Active/inactive status with validity dates
- Import tracking with SHA-256 hashing

The Swiss municipalities table is simpler:
- BFS municipality codes (variable length, typically 3-4 digits)
- Canton codes (2 characters, ISO standard)
- Official names only (no bilingual variants in separate fields)
- No deactivation tracking (Swiss municipalities rarely cease to exist)
- Initial seed only (no import tracking infrastructure yet)

This reflects the different administrative structures and data availability between Italy and Switzerland.

## References

- Swiss Federal Statistical Office: https://www.bfs.admin.ch/
- ISO 3166-2:CH (Canton codes): https://en.wikipedia.org/wiki/ISO_3166-2:CH
- Swiss Administrative Units: https://www.bfs.admin.ch/bfs/en/home/basics/swiss-official-commune-register.html
- ADR-001 (Multi-country properties): `kb/adr/drafts/ADR-001-custom-domain-booking.md`
- NF-02 (Property country and currency): `kb/intake/requirements.md`
