# Runbook: tourist tax rates (seed, admin page, activation wizard)

Task CO-03 (audit defects A5-06, A8-28). The code applies everything by itself when the backend starts on
Railway; this page says what changes, how to check it after the deploy and what the admin still has to do.

## What changes

| Area | Behaviour | Code |
|---|---|---|
| Activation wizard, step "Imposta di soggiorno" | **Warning, never a blocker** (PLANNING, Wizard 1 step 5). Rate known → step complete, the host sees amount, max nights, exemption age, start date and source. No rate → warning "comune senza tariffa in CasaZen" and, if a *reviewed* page exists, a link to `/p/tassa-soggiorno/{comune}`. "Completa" is no longer refused because of the tax. | `ComplianceWizardService.BuildTouristTaxStep`, resx `ActivationTouristTaxNoRate` / `ActivationTouristTaxCityMissing` |
| Wizard, step "CIN" | The guidance link comes from `Compliance:CinGuidanceUrl` (field `linkUrl`), no domain written in the frontend. Default: the BDSR page of the Ministero del Turismo. | `appsettings.json`, `ComplianceOptions.DefaultCinGuidanceUrl` |
| Admin API `POST/PUT/DELETE /api/tourist-tax-rates` | Explicit body `TouristTaxRateRequest` without id: create → 201 with a server id; update/delete of an unknown id → `404 tourist_tax_rate_not_found`; validation → `400 validation_error` with IT/EN field messages. Delete is a soft delete (`IsActive = false`). | `TouristTaxRatesController`, `TouristTaxService` |
| Rate record | New columns `SourceUrl` and `VerificationLevel` (`Official` = U, `Deduced` = D, `ThirdParty` = T, legend of RS-7). Shown in the admin page. | migration `AddTouristTaxRateSourceAndSeed` |

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

Not loaded (the model is extended by task BK-03): Roma (amount by accommodation category), Venezia (cadastral
group and season), Bologna (percentage with a cap), Torino (amount from a third-party source, nights capped per year),
Seveso and Cesano Maderno (no rate found; an empty rate is never 0). Hosts in these comuni see the warning.

The seeded rows have fixed ids (MD5 of ISTAT code + start date) and are inserted once, by the migration. An admin
change or delete is never overwritten.

### Check after the deploy (test, then production)

Read-only queries on the environment schema (`casazen_test` / `casazen_prod`):

```sql
-- The 4 seeded rates, with source and level (expected: 4 rows, VerificationLevel = 'Official')
SELECT "City", "RatePerPersonPerNight", "MaxNights", "MinimumAge", "EffectiveFrom", "VerificationLevel", "SourceUrl"
FROM "TouristTaxRates" WHERE "SourceUrl" IS NOT NULL ORDER BY "City";

-- More than one active rate for the same comune and start date (expected: 0 rows)
SELECT lower("City"), "EffectiveFrom", count(*) FROM "TouristTaxRates"
WHERE "IsActive" GROUP BY lower("City"), "EffectiveFrom" HAVING count(*) > 1;
```

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
