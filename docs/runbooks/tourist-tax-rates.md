# Runbook: tourist tax rates (seed, admin page, activation wizard, checkout, calculator)

Tasks CO-03 (audit defects A5-06, A8-28) and BK-03 (A3-02, R-05, A5-16, A8-12, A8-23). The code applies
everything by itself when the backend starts on Railway; this page says what changes, how to check it after the
deploy and what the admin still has to do.

## One source, one engine (BK-03)

- **Single source: `TouristTaxRates`.** The old `TaxRates` table (read by the checkout, never written, so every
  booking recorded `TouristTax = 0`) and `TaxCalculationService` are gone: migration `UnifyTouristTaxOnTouristTaxRates`
  drops the table. The authenticated `POST /api/tourist-tax-rates/calculate` is removed too (unused).
- **Single engine: `Casazen.Core/TouristTax/TouristTaxCalculator`**, reached through `ITouristTaxQuoteService`. Used by
  the checkout quote, the direct booking, the host (manual) booking, the activation wizard and the public calculator.
- **Comune lookup:** by ISTAT code when both the rate and the comune carry one (public pages: `ItalianComuneRegistry`),
  otherwise by normalized name: case, accents, spaces, apostrophes and hyphens do not matter (`" ROMA "` = `roma`,
  `Forlì` = `forli`). Properties have no ISTAT code yet, so they match by city name.

### Rules of the calculation

| Rule | Behaviour |
|---|---|
| Nights | Each night is dated by its Europe/Rome calendar date. The rate of a night is the active one whose validity and season contain that date: a stay across a season change or a new rate is taxed night by night. |
| Cap of nights | Only the first `MaxNights` nights of the stay are taxed (per stay; yearly caps such as Torino's are not modelled). |
| Ages | Adults count as 18 and are never exempt by age. A minor younger than `MinimumAge` is exempt; from `MinimumAge` to `ReducedRateMaxAge` included pays `ReducedRatePerPersonPerNight` (the amount the comune publishes, never computed). Age = age at check-in, given by the guest. |
| Category | A rate with `AccommodationCategory` applies only to that category; without category, to every accommodation. Properties have no category yet: in comuni whose rates are all per category (Roma, Venezia) the property quote is "not available". |
| Percentage | `PercentOfNightlyPrice`% of the night price divided by the number of guests (all of them), capped at `CapPerPersonPerNight`. For a property the night price is its nightly rate (cleaning excluded). |
| Rounding | Every amount per guest per night is rounded to the cent, half away from zero (0,005 → 0,01), then summed: the total is exact to the cent. Only percentage rates produce fractions. |
| No rate | If any night of the stay has no rate, the status is `RateUnavailable`: **no amount, never 0 or an estimate**, and the checkout goes on without the tax. |

### Checkout and booking (A3-02, R-05)

- `POST /api/public/bookings/quote` (public, rate limit `PublicRead`) returns lodging, cleaning, tourist tax
  (`status`, `amount`, `taxableNights`, `ageRulesApply`) and total. The checkout page shows **only** these numbers.
- `POST /api/public/bookings` computes the same quote and records `TouristTax` = `TouristTaxAmount` = the quoted
  amount and `TotalPrice` = base + tax; the Stripe PaymentIntent (or the on-site payment) is for that total. The
  response carries `touristTaxStatus`.
- **Collection:** the tax is included in the total charged with the payment (spec-direct-checkout AC6, "included in the
  charged total"); with "Paga in struttura" the whole total, tax included, is paid on site. The checkout labels it
  "Tassa di soggiorno (inclusa nel totale)".
- Minors: when the rate exempts or reduces minors by age (`ageRulesApply`), the checkout asks the age of each minor;
  a booking without them answers `422 tourist_tax_child_ages_required`. Milano (every minor exempt) never asks.
- Host bookings (`POST /api/bookings`): the form has the number of guests only, so every guest counts as an adult
  (the maximum the stay can owe).

### Public calculator and SEO (A8-12, A8-23)

- `GET /api/public/content/tassa-soggiorno/{comune}` returns `touristTaxRates` (every rate in force today, per
  category and season; empty when none). Without rates the page shows "Tariffa non ancora disponibile" instead of
  the calculator.
- `POST /api/public/tourist-tax/calculate` answers 200 with a `status` (`RateUnavailable`, `CategoryRequired`,
  `ChildAgesRequired`, `NightlyPriceRequired`), 404 only for an unknown comune slug, 400 `tourist_tax_quote_invalid`
  for invalid input (at most 366 nights).
- `/sitemap-compliance.xml` leaves out the calculator pages of comuni without a rate in force today.

## What changes

| Area | Behaviour | Code |
|---|---|---|
| Activation wizard, step "Imposta di soggiorno" | **Warning, never a blocker** (PLANNING, Wizard 1 step 5). Rate known → step complete, the host sees amount, max nights, exemption age, start date and source. No rate → warning "comune senza tariffa in CasaZen" and, if a *reviewed* page exists, a link to `/p/tassa-soggiorno/{comune}`. "Completa" is no longer refused because of the tax. | `ComplianceWizardService.BuildTouristTaxStep`, resx `ActivationTouristTaxNoRate` / `ActivationTouristTaxCityMissing` |
| Wizard, step "CIN" | The guidance link comes from `Compliance:CinGuidanceUrl` (field `linkUrl`), no domain written in the frontend. Default: the BDSR page of the Ministero del Turismo. | `appsettings.json`, `ComplianceOptions.DefaultCinGuidanceUrl` |
| Admin API `POST/PUT/DELETE /api/tourist-tax-rates` | Explicit body `TouristTaxRateRequest` without id: create → 201 with a server id; update/delete of an unknown id → `404 tourist_tax_rate_not_found`; validation → `400 validation_error` with IT/EN field messages. Delete is a soft delete (`IsActive = false`). | `TouristTaxRatesController`, `TouristTaxService` |
| Rate record | New columns `SourceUrl` and `VerificationLevel` (`Official` = U, `Deduced` = D, `ThirdParty` = T, legend of RS-7). Shown in the admin page. | migration `AddTouristTaxRateSourceAndSeed` |
| Rate record (BK-03) | New columns `IstatCode`, `AccommodationCategory`, `SeasonStart`/`SeasonEnd` (`MM-dd`), `CalculationMethod` (`PerPersonPerNight` / `PercentOfNightlyPrice`), `PercentOfNightlyPrice`, `CapPerPersonPerNight`, `ReducedRateMaxAge`, `ReducedRatePerPersonPerNight`. All editable in the admin page; `MinimumAge` is now 0-18. | migration `UnifyTouristTaxOnTouristTaxRates` |
| Activation wizard (BK-03) | Same lookup as the checkout. A comune with rates only per category shows "la tariffa dipende dalla categoria della struttura" (`ActivationTouristTaxCategoryRequired`). | `ComplianceWizardService.ResolveTouristTaxAsync` |

## Seed `AddTouristTaxRateSourceAndSeed`

Source: `Casazen.Infrastructure/Data/Seeds/tourist-tax/rates.csv` and `.claude/context/regulations/imposta_soggiorno.md`
("Tariffe verificate (2026-09)", task RS-7, consulted 2026-09-23). Only the rows the current model represents
exactly are loaded: one fixed amount per person per night for the whole comune, with an official source (U).
Values are frozen in `TouristTaxRateSeed`; `TouristTaxRateSeedTests` checks them against the CSV.

| Loaded | €/person/night | Max nights | Exempt under | From |
|---|---|---|---|---|
| Milano | 9,50 | 14 | 18 | 2026-01-01 |
| Como | 3,00 | 4 | 14 | 2024-01-01 |
| Firenze | 6,00 | 7 | 12 | 2025-02-01 |
| Napoli | 6,00 | 14 | 14 | 2026-05-01 |

The seeded rows have fixed ids (MD5 of ISTAT code + start date) and are inserted once, by the migration. An admin
change or delete is never overwritten.

## Seed `UnifyTouristTaxOnTouristTaxRates` (BK-03)

Sets `IstatCode` on the 4 rows above and loads the official (U) rows the extended model represents
(`TouristTaxRateSeed.BuildCategoryAndSeasonRates`, checked against the CSV by `TouristTaxRateSeedTests`):

| Comune | Category | Season | €/person/night | Reduced (ages 10-16) | Max nights | Exempt under | From |
|---|---|---|---|---|---|---|---|
| Roma | CAV categoria 1 | all year | 6,00 | — | 10 | 10 | 2023-10-01 |
| Roma | CAV categoria 2 | all year | 5,00 | — | 10 | 10 | 2023-10-01 |
| Venezia | Gruppo 1 (A/1, A/8, A/9) | 01/02-31/12 | 5,00 | 2,50 | 5 | 10 | 2025-04-01 |
| Venezia | Gruppo 2 (A/2, A/3, A/6, A/7, A/11) | 01/02-31/12 | 4,00 | 2,00 | 5 | 10 | 2025-04-01 |
| Venezia | Gruppo 1 | 01/01-31/01 | 3,50 | 1,70 | 5 | 10 | 2025-04-01 |
| Venezia | Gruppo 2 | 01/01-31/01 | 2,80 | 1,40 | 5 | 10 | 2025-04-01 |
| Venezia | Gruppo 3 (A/4, A/5) | 01/01-31/01 | 2,10 | 1,00 | 5 | 10 | 2025-04-01 |

Not loaded, on purpose:

- **Roma, "alloggi per uso turistico / locazione breve" 6,00 €**: amount from third parties (T).
- **Venezia, Gruppo 3 high season 3,00 €**: deduced (D). A Gruppo 3 stay from February is "tariffa non disponibile".
- **Bologna, 10,5% max 7,00 €**: the percentage and the cap are official, but "per person" (price divided by the guests)
  comes from third parties only (T) and the start date 01/01/2026 is deduced (D). Once confirmed, the admin adds it:
  City `Bologna`, ISTAT `037006`, region `EMR`, type "Percentuale del prezzo", 10,5 %, cap 7,00, max nights 5,
  minimum age 14, from the confirmed date, source B1 of `imposta_soggiorno.md`.
- **Torino**: amount from third parties (T), nights capped per year (not modelled).
- **Seveso, Cesano Maderno**: no rate found (an empty rate is never 0). Hosts see the warning.

Roma and Venezia rates are per accommodation category: the public calculator asks the category; property checkouts
there show "tariffa non disponibile" until properties have a category (open product question, see below).

### Check after the deploy (test, then production)

Read-only queries on the environment schema (`casazen_test` / `casazen_prod`):

```sql
-- The seeded rates, with source and level (expected: 11 rows - Milano, Como, Firenze, Napoli, 2 Roma, 5 Venezia -
-- all VerificationLevel = 'Official', IstatCode set)
SELECT "City", "IstatCode", "AccommodationCategory", "SeasonStart", "SeasonEnd", "RatePerPersonPerNight",
       "ReducedRatePerPersonPerNight", "MaxNights", "MinimumAge", "EffectiveFrom", "VerificationLevel"
FROM "TouristTaxRates" WHERE "SourceUrl" IS NOT NULL ORDER BY "City", "AccommodationCategory", "SeasonStart";

-- More than one active rate for the same comune, category, season and start date (expected: 0 rows)
SELECT lower("City"), "AccommodationCategory", "SeasonStart", "EffectiveFrom", count(*) FROM "TouristTaxRates"
WHERE "IsActive" GROUP BY lower("City"), "AccommodationCategory", "SeasonStart", "EffectiveFrom" HAVING count(*) > 1;

-- The old table is gone (expected: 0 rows)
SELECT 1 FROM information_schema.tables WHERE table_name = 'TaxRates';
```

Checks on the API (test environment, any Firenze property that is active and compliant, dates in the future):

1. `POST /api/public/bookings/quote` with 2 adults, 3 nights → `touristTax.status = Calculated`, `amount = 36.00`.
   With 1 child and no `childrenAges` → `ChildAgesRequired`.
2. A booking with the same data records `TouristTaxAmount = 36.00` and `TotalPrice = basePrice + 36.00`.
3. A property in a comune without rate (e.g. Seveso) → `status = RateUnavailable`, `amount = null`, total without tax.
4. `/sitemap-compliance.xml` has no `/p/tassa-soggiorno/palermo`.

The same list is in the app: **Admin → Tassa di Soggiorno** (`/app/admin/compliance/tax-rates`).

## To do by the admin / product owner

1. **Confirm every seeded row on the official PDF** from a network that can open the comune sites: RS-7 read them
   through search-engine extracts only (the proxy blocked the direct reading). Edit the row in the admin page if a
   value differs.
2. **Milano:** the 9,50 € rate is for 2026 only (delib. G.C. 1418/2025). When the comune confirms it, set
   "Valida fino al" to 31/12/2026, and add the 2027 rate when it is published. Until then the rate stays in force.
3. **Napoli:** the rate from 01/01 to 30/04/2026 is not verified and not loaded; the exemption of the 14-year-old
   is ambiguous (see RS-7 "Dubbi aperti").
4. **CIN guidance link:** if Railway overrides `Compliance__CinGuidanceUrl`, check that it points to an official page.
5. **Venezia:** confirm on the official PDF (V1, V2) the reduced band "da 10 a 16 anni" (seeded as 10 to 16 included)
   and whether the 10-year-old is exempt or reduced; edit `MinimumAge` / `ReducedRateMaxAge` if needed. Confirm the
   Gruppo 3 high-season amount (3,00 €, not loaded) and add it.
6. **Bologna:** confirm that 10,5% applies to the price per person and the start date, then add the rate (see above).
7. **Product (open):** properties have no accommodation category nor ISTAT code. Until they do, Roma and Venezia
   property checkouts show "tariffa non disponibile", and the lookup uses the city name.
