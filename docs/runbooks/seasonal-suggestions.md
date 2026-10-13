# Runbook: Suggerimenti stagionali (seasonal price suggestions)

Task PC-15 (audit defects A2-14, A2-34, A8-17), decision D4 in `Sessions/risanamento/DECISIONI.md`: the former
"AI Dynamic Pricing" / "Prezzi AI" is renamed **"Suggerimenti stagionali"**. There is no AI, no demand model and no
confidence score: a suggestion is the property's **real nightly rate** (`Property.NightlyRate`) times one explicit rule.
Real dynamic pricing comes after the MVP.

Nothing has to be configured on Railway, Vercel or Supabase: no API key, no external service. The migration
`SeasonalPriceSuggestions` runs with the others on deploy.

## Where the code is

| What | Code |
|---|---|
| Italian national holidays (fixed dates + Easter / Easter Monday computed) | `Casazen.Core/Pricing/ItalianPublicHolidays.cs` |
| Rules, calculator, schedule by Rome dates | `Casazen.Core/Pricing/SeasonalPricing.cs` |
| Configuration (rules per property) | `Casazen.Core/Entities/PricingAdapterConfig.cs` (table `PricingAdapterConfigs`) |
| Suggestions (one row per property and stay date) | `Casazen.Core/Entities/SeasonalPriceSuggestion.cs` (table `SeasonalPriceSuggestions`) |
| Computation (upsert by date, advisory lock per property) | `Casazen.Infrastructure/Services/PricingAdapterService.cs` |
| Nightly job, 02:00 UTC, id `dynamic-pricing-adaptation` | `Casazen.Web/BackgroundJobs/DynamicPricingJob.cs` |
| API | `Casazen.Web/Controllers/PricingAdapterController.cs` (`/api/pricing-adapter/...`) |
| Frontend page | `src/features/pricing/*` (route `/app/short-rent/properties/:id/pricing`) |

## Rules

Each property has its own rules, editable by the host on the page:

| Rule | Default ("regola di esempio modificabile") |
|---|---|
| High season: months + multiplier | June, July, August, x1.30 |
| Low season: months + multiplier | November, December, January, February, x0.80 |
| National holidays: multiplier | x1.50 |

- The defaults are an **example**, not market data: the page says so and the host adapts them. Existing configurations
  got the same example values from the migration.
- One rule per day: a national holiday takes precedence over the season of its month (no product of multipliers), so
  the page can show "Festività: Ferragosto x1,50".
- A month belongs to one season only (validated in the API and in the page). Multipliers between 0.1 and 5.
- The two switches "Stagioni" and "Festività nazionali" turn each rule off.
- Suggested price = nightly rate x multiplier, rounded to the cent. With no nightly rate (0) nothing is suggested
  (status `BasePriceMissing`): there is no invented base price.

### National holidays

Only official national dates, computed locally (no external API, no local events, no patron saints):
Legge 27 maggio 1949 n. 260 art. 2 as amended (Legge 54/1977, DPR 792/1985) - 1 January, 6 January, Easter Sunday,
Easter Monday, 25 April, 1 May, 2 June, 15 August, 1 November, 8 December, 25 December, 26 December - and
**4 October (San Francesco d'Assisi) from 2026**, Legge 8 ottobre 2025 n. 151 (G.U. n. 236 of 10 October 2025, in force
from 1 January 2026). Easter uses the Gregorian computus (Meeus/Jones/Butcher): 5 April 2026, 28 March 2027.
The former `PublicHolidayService` (Nager.Date HTTP API) was removed.

## Suggestions are read-only

CasaZen has **no price model per date** yet: quotes, public checkout and host bookings price every night with
`Property.NightlyRate` (`BookingService.PriceStayAsync`, shared by `POST /api/public/bookings/quote`, the checkout and
`PriceHostStayAsync`, BK-03/BK-07). The suggestions are therefore shown as proposals only, and the page says that they
are not applied. Applying them to a date range needs a per-date rate table read by `PriceStayAsync` (and by the
tourist tax "percentage of the night price" rates): open product question, see the PC-15 report.

## Schedule (A2-34)

- `AdaptationFrequency` is `daily` or `weekly`. The next computation is due from the **Europe/Rome date**
  `date(LastAdaptedAt) + 1` (daily) or `+ 7` (weekly), `SeasonalSuggestionSchedule`. Instants are never compared: a
  job that starts at 01:59 after a run at 02:01 the day before still runs, and a weekly schedule runs once a week.
- A never computed configuration (`LastAdaptedAt = null`) is due at the next run: the first run is never skipped.
  Enabling or saving the rules from the page also computes the suggestions right away.
- `NextScheduledRunAt` was dropped; the API returns `nextRunOn` (Rome date) computed from `LastAdaptedAt`.
- "Ricalcola ora" (`POST /api/pricing-adapter/recalculate/{propertyId}`) runs the same computation, synchronously, without
  the due check; it moves the next due date like any run.
- A property without nightly rate stays due (it is computed as soon as the rate is set).

## Storage: one row per date, no history

Every computation upserts the rows of `SeasonalPriceSuggestions` for today (Rome) + 89 days and deletes the rows
outside that window. Unique index `(PropertyId, StayDate)`; runs of the same property are serialized by the advisory
lock `SeasonalPriceSuggestions` (scope 1019) in `PostgresAdvisoryLocks`. Turning the suggestions off deletes the rows.
Nothing is written to `PricingHistories` any more and nothing is sent to the OTAs (the OTA price push is in freeze, FD-20).

## Cleanup of the invented history

The old job wrote 91 rows a day per property into `PricingHistories` with a fixed base of 100 EUR, "confidence" 0.85 and
status `Pending`, even though no price was ever applied. The migration `SeasonalPriceSuggestions` **deletes** them
(`DeleteInventedHistorySql`: `PreviousPrice = 100 AND AiConfidence = 0.85 AND ChangeReason LIKE 'Dynamic pricing
adaptation (multiplier:%'`). Deleting rather than marking: the rows describe prices that never existed, nothing reads
them any more (the history endpoint and page were removed) and keeping them would only preserve false data. The rows
of the OTA batch push (`Batch OTA price update`, frozen feature) are kept. The deletion cannot be rolled back by `Down`.

To check a database after the deploy:

```sql
SELECT count(*) FROM "PricingHistories" WHERE "AiConfidence" = 0.85 AND "PreviousPrice" = 100;  -- expected 0
SELECT "PropertyId", count(*) FROM "SeasonalPriceSuggestions" GROUP BY 1;                      -- at most 90 each
```

## API

| Method | Route | Notes |
|---|---|---|
| GET | `/api/pricing-adapter/config/{propertyId}` | Example rule (disabled) when never saved |
| POST | `/api/pricing-adapter/config/{propertyId}` | Saves frequency and rules; enabled: computes now; disabled: deletes the rows |
| DELETE | `/api/pricing-adapter/config/{propertyId}` | Turns off and deletes the rows |
| GET | `/api/pricing-adapter/suggestions/{propertyId}` | Rows by date + `currentBasePrice`, `computedAt`, `nextRunOn` |
| POST | `/api/pricing-adapter/recalculate/{propertyId}` | 200 `Computed` / `BasePriceMissing`; 422 `pricing_suggestions_not_enabled` |

The former `GET history`, `GET preview` and `POST sync` endpoints were removed.
