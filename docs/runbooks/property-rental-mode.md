# Runbook: rental mode of the properties (short or long, `Property.RentalMode`)

Task PM-01 of the redesign wave (decisions D16 and D19; report `gap/06` §4.4). Until PM-01 a property had no mode: the only
marker of a property kept for long-term leases was **implicit**, no guests and no nightly rate (`MaxGuests = 0` and
`NightlyRate = 0`, audit A7-06), and every predicate that must ignore such a property (the public site, the bookings, the
compliance, the CIN alerts) forgot it: for instance `CinDeadlineAlertService` wrote also to the hosts who only have leases.
PM-01 adds the mode, migrates the existing rows and makes those predicates look at it. **Backend only**: no screen, no
scheduled change of mode (PM-02), no wizard (PM-03). The flow is not behind a flag: a `Long` property exists only after the
backfill below or when a client asks for it, and nothing changes for a property in `Short` mode.

## 1. The mode

`Property.RentalMode` (`Casazen.Core/Entities/Enums/RentalMode.cs`), column `Properties.RentalMode`, `integer NOT NULL`,
default `0`. **Stored as an integer, append only**: never renumber or reuse a value.

| Value | Name | Meaning |
|---|---|---|
| `0` | `Short` | short stays: the booking site, the calendar, the CIN and the compliance of D.L. 145/2023. The default |
| `1` | `Long` | long-term leases: not published, not bookable, outside the short-rent compliance |
| `2` | (`Both`, not now) | left free for a future "hybrid" property; the mode is **exclusive** today (decision of the demo) |

The API reads and writes the name (`"Short"`, `"Long"`, case-insensitive; a number is refused as for every enum).
`rentalMode` is in `GET /api/properties` (each row), `GET /api/properties/{id}`, `GET /api/properties/{id}/detail` and in the
body of `POST /api/properties` (the created property). Index `IX_Properties_OrgId_RentalMode` serves the lists.

### How a property gets its mode

| When | Rule |
|---|---|
| **Creation** (`POST /api/properties`) | the `rentalMode` of the body, when it is there. **Compatibility rule** (`PropertyRentalModeRules.ResolveForCreation`): without it, a request with `maxGuests = 0` **and** `nightlyRate = 0` (what the long-rent form has always sent) creates a `Long` property; any other request creates a `Short` one. A short-rent client that creates a property without guests and rate and wants it short must say `"rentalMode": "Short"` |
| **Update** (`PUT /api/properties/{id}`) | `rentalMode` is accepted so that a form can send the whole record back, but it must be **the stored one**: a different value is refused, `422 property_rental_mode_change_not_allowed`, and nothing is saved. `PropertyRepository.UpdateAsync` never writes the column (as for the photo list), so a stale copy of the row cannot put an old mode back |
| **Migration** (existing rows) | section 3 (D19) |
| **Change of mode** | PM-02 (scheduled, checked against the stays and the leases, at midnight of Rome). Until it exists nothing changes the mode of a property |

## 2. What `Long` changes

One definition of "short rent": `PropertyRentalModeRules.IsShortRent` (`Casazen.Core/Services/PropertyRentalModeRules.cs`).
`PublicListing.IsPublished` repeats its comparison (a test keeps the two together).

| Point | Behaviour for a `Long` property | Code |
|---|---|---|
| Public listing: search, property page, org booking site, availability, SEO page, featured properties, sitemaps | **not published**: `404` (`public_property_not_found` for the availability) | `PublicListing.IsPublished` |
| Public checkout and its quote (`POST /api/public/bookings`, `…/quote`) | `422 property_not_bookable_in_long_mode`, before anything is written, whatever the other state of the property (the public pages never lead there: a stale page or a hand-made request) | `BookingService.GetBookablePropertyAsync` |
| Host: price of a stay (`POST /api/bookings/quote`), new manual booking (`POST /api/bookings`), change of dates or guests of a manual booking (`PUT /api/bookings/{id}`) | `422 property_not_bookable_in_long_mode`, before the check on the guests (a long-term property has no capacity) | `BookingService.PriceHostStayAsync`, `BookingController.Create`, `BookingService.CreateManualBookingAsync` |
| Activation of the listing (`POST …/compliance/activation/complete`) | `422 property_not_bookable_in_long_mode`: a long-term property is never published | `ComplianceWizardService.CompleteActivationAsync`, `PropertyComplianceStatusService.ActivateAsync` |
| Compliance status (CO-06): re-evaluation after an edit, a document, the checklist; nightly check (`property-compliance-check`) | **nothing**: not evaluated, not suspended, not reactivated, no email to the host. The stored status is left as it is | `PropertyComplianceStatusService.EvaluateAsync`, `RecalculateAllAsync` |
| CIN alerts (CO-20, job `cin-deadline-alert`) | never alerted nor claimed | `CinDeadlineAlertService.CandidateProperties` |
| CIN summary of the host (`GET /api/properties/cin-compliance`) and of the staff (`GET /api/admin/cin-compliance`, `CinValid/CinMissing/CinInvalid/CinTotal` of the admin stats) | not counted, not listed | `PropertyRepository.GetByScopeForComplianceAsync`, `AdminService` |
| Cockpit of the duties (`GET /api/compliance/summary`) | not offered "activate the property". The sections about **stays** (check-in, check-out, Alloggiati, turnovers) are not filtered: they are legal and operational duties of stays that happened, whatever the property is now | `ComplianceWizardService.GetSummaryAsync` |
| Activation checklist of the short-rent host (`GET /api/onboarding/status`) | not counted as "created", "without a CIN" nor "waiting for the activation" (`Published` follows `IsPublished`) | `OnboardingService.LoadPropertyFactsAsync` |
| Lists (`GET /api/properties`) | **listed**, as before. `?mode=short` or `?mode=long` narrows the list; without the parameter nothing changes; any other value is `400 validation_error` | `PropertiesController.GetAll` |
| Plan limit (`MaxProperties`, `GET /api/orgs/me/entitlement`) | **counted**, like any other property | `EntitlementService` |

Not changed by PM-01 (listed so that nobody looks for it): the host dashboard counts the active properties of every mode in
the occupancy of its period (`HostDashboardService.ActivePropertiesInScope`, follow-up of the dashboard task), the iCal
export of a `Long` property still shows its bookings and blocks (the block of the mode change is PM-02), the contracts have
no constraint on the mode (no lease is refused because the property is `Short`: the warning "switch to long" is the
interface's, PM-03) and a `ServiceRequest` is not touched.

## 3. Backfill of the existing rows (D19)

The migration `AddPropertyRentalMode` (`Casazen.Infrastructure/Migrations/20261008225123_AddPropertyRentalMode*.cs`) adds
the column with `0` everywhere, then runs the backfill (`AddPropertyRentalMode.BackfillSql`, same file family,
`….Data.cs`). A live property (not soft-deleted) becomes `Long` only when **the three signals agree**:

1. **contracts**: at least one lease that is not a draft nor rejected (`LeaseStatus` `0` and `7`), ended or not;
2. **no booking** at all, in any status (a booking is the proof that the property was let for short stays);
3. the **A7-06 marker**: `MaxGuests = 0` and `NightlyRate = 0`.

The **ambiguous** cases stay `Short` and nothing is guessed: contracts **and** bookings (whatever the marker), and contracts
without bookings but with guests or a rate. The host changes them with the scheduled change (PM-02). `UpdatedAt` is not
touched (a classification, not an edit by the host). The statement is idempotent (it updates only the rows still at `0`) and
logs the counts with `RAISE NOTICE` (`n properties set to long-term, n ambiguous…, n with contracts, no bookings and
short-stay data…`).

### Dry run before the merge (decision D19: the owner approves the count of the ambiguous ones)

Read-only, and it runs **before** the migration on the database as it is (it reads nothing the migration creates). Run it on
the **test database** (a copy of the data the owner wants to migrate), with `psql`, before the PR is merged and again, if
the data changed, before the production deploy. It shares its subquery with the backfill, so the list is exactly what the
migration changes. Ids and counts only, no names.

**Query 1: the cases per org, and the total** (the last row, without org). `ToLong` = what the migration sets to long-term;
`Ambiguous` = contracts and bookings, left short; `ContractsWithShortStayData` = contracts, no bookings, guests or a rate,
left short.

```sql
SELECT s."OrgId",
       count(*) FILTER (WHERE s."Contracts" > 0 AND s."Bookings" = 0 AND s."NoShortStayData") AS "ToLong",
       count(*) FILTER (WHERE s."Contracts" > 0 AND s."Bookings" > 0) AS "Ambiguous",
       count(*) FILTER (WHERE s."Contracts" > 0 AND s."Bookings" = 0 AND NOT s."NoShortStayData") AS "ContractsWithShortStayData"
FROM (SELECT p."Id" AS "PropertyId",
       p."OrgId" AS "OrgId",
       (SELECT count(*)
        FROM "LeaseContracts" AS l
        WHERE l."PropertyId" = p."Id"
          AND l."Status" NOT IN (0, 7)) AS "Contracts", -- LeaseStatus.Draft, LeaseStatus.Rejected
       (SELECT count(*)
        FROM "Bookings" AS b
        WHERE b."PropertyId" = p."Id") AS "Bookings",
       (p."MaxGuests" = 0 AND p."NightlyRate" = 0) AS "NoShortStayData"
FROM "Properties" AS p
WHERE p."IsDeleted" = false) AS s
WHERE s."Contracts" > 0
GROUP BY ROLLUP (s."OrgId")
ORDER BY s."OrgId" NULLS LAST
```

**Query 2: the properties of those cases**, one row each (`Case` = `to_long`, `ambiguous` or `contracts_with_short_stay_data`),
with the counts that decided it.

```sql
SELECT s."OrgId",
       s."PropertyId",
       CASE
           WHEN s."Bookings" > 0 THEN 'ambiguous'
           WHEN s."NoShortStayData" THEN 'to_long'
           ELSE 'contracts_with_short_stay_data'
       END AS "Case",
       s."Contracts",
       s."Bookings"
FROM (SELECT p."Id" AS "PropertyId",
       p."OrgId" AS "OrgId",
       (SELECT count(*)
        FROM "LeaseContracts" AS l
        WHERE l."PropertyId" = p."Id"
          AND l."Status" NOT IN (0, 7)) AS "Contracts", -- LeaseStatus.Draft, LeaseStatus.Rejected
       (SELECT count(*)
        FROM "Bookings" AS b
        WHERE b."PropertyId" = p."Id") AS "Bookings",
       (p."MaxGuests" = 0 AND p."NightlyRate" = 0) AS "NoShortStayData"
FROM "Properties" AS p
WHERE p."IsDeleted" = false) AS s
WHERE s."Contracts" > 0
ORDER BY s."OrgId", "Case", s."PropertyId"
```

How to read it. The total row of query 1 is the number to approve. A `to_long` property will disappear from the public site
and refuse bookings from the moment the migration runs: check in query 2 that none of them is a property the host still lets
for short stays (a property with a nightly rate set is never in this case; the marker is the safety). An org in
`ambiguous` is a host who did both: nothing changes for it. If the count is not the one expected, **do not merge**: the rule is
in `AddPropertyRentalMode.Data.cs` (`SignalsSql`) and a test (`AddPropertyRentalModeMigrationPostgresTests`) checks that
the dry run lists exactly the rows the migration changes.

## 4. Checks after the deploy

```sql
-- how many properties of each mode, per org with a long-term one
SELECT "OrgId", "RentalMode", count(*) FROM "Properties" WHERE "IsDeleted" = false GROUP BY "OrgId", "RentalMode" ORDER BY 1, 2;
```

* The `Long` rows are the `to_long` ones of the dry run (same ids).
* A `Long` property: `GET /api/public/bookings/property/{id}/availability` answers `404 public_property_not_found`;
  `POST /api/bookings` on it answers `422 property_not_bookable_in_long_mode` (Italian and English message).
* The hosts of those properties get no more `cin-deadline-alert` email and no suspension email.
* `GET /api/properties?mode=long` lists them, `?mode=short` the others, without the parameter all of them.

A property wrongly classified (rare, the marker is restrictive) is put back **by hand** until PM-02 exists, by the owner of
the data: `UPDATE "Properties" SET "RentalMode" = 0 WHERE "Id" = '<id>';` (the generic API cannot change the mode, section 1).
Its compliance status was not touched by PM-01 and the next re-evaluation of the nightly check takes it back in.

## 5. Rollback

* **Application** back to a version without PM-01 while the column stays: harmless, the old code does not read it and an
  old `INSERT` takes the default `0`. But the old code takes a property for long-term only when it has neither guests nor a
  rate, so the `Long` properties **with guests or a rate** would look short-stay again (publishable after an activation).
  List them before rolling back:

```sql
SELECT p."OrgId", p."Id" AS "PropertyId", p."MaxGuests", p."NightlyRate"
FROM "Properties" AS p
WHERE p."RentalMode" = 1 -- RentalMode.Long
  AND (p."MaxGuests" <> 0 OR p."NightlyRate" <> 0)
ORDER BY p."OrgId", p."Id"
```

* **Database**: `dotnet ef database update <previous migration>` runs `Down`: it drops the index and the column, and with
  them every mode (the information is gone: the `Long` properties go back to the implicit marker above). Take the list
  of the `Long` ids first (section 4) if you want to restore them. Nothing else is touched by the migration.

## 6. Tests

| Test | Covers |
|---|---|
| `PropertyRentalModeRulesTests` | stored integers, compatibility rule, `ToProperty`, the shared predicate, resource keys |
| `PropertyRentalModePublicListingTests` | search, org site, property page, availability, SEO featured properties, activation checklist facts |
| `PropertyRentalModeBookingTests` | public checkout and quote, host price, manual booking: 422 and nothing written |
| `PropertyRentalModeComplianceTests` | status service (re-evaluation, activation, nightly check), CIN alert, CIN summaries (host, staff), cockpit |
| `PropertyRentalModeRepositoryTests` | list filter and scope, the generic save never writes the mode, plan counter |
| `PropertiesControllerRentalModeTests` | `?mode=`, create, update, the records |
| `PropertyRentalModeRunbookTests` | the queries of this runbook are the ones of the migration |
| `PropertyRentalModeIntegrationTests` | the real HTTP pipeline: public 404s (page, org site, search, availability, SEO, sitemap), 422 in Italian and English (checkout, quote, manual booking, activation), `?mode=`, creation, update, plan limit, isolation between orgs |
| `AddPropertyRentalModeMigrationPostgresTests` (PostgreSQL) | the backfill with the cases (only contracts, only bookings, both, and the variants), soft-deleted rows, idempotence, `UpdatedAt` untouched, dry run = what changes, `Down` then `Up` |
| `PropertyRentalModePostgresTests` (PostgreSQL) | the daily CIN alert and the nightly compliance check as the jobs run them (session locks, conditional updates), the generic save, the index |

The `[PostgresFact]` tests do not run without a PostgreSQL server (the local machines of the redesign wave have none): the CI
runs them. `PropertyRentalModeIntegrationTests` runs on the EF InMemory fallback locally and on PostgreSQL in the CI.

## 7. For the next tasks

* **PM-02** (scheduled change): the only writer of the mode after the creation. It reuses the rule of the soft delete
  (`PropertyRepository.SoftDeleteAsync`: stays to come, leases not ended) with a date, under `BookingRepository.LockPropertyDatesAsync`.
  A property that goes back to `Short` should be re-evaluated (`IPropertyComplianceStatusService.ReevaluateAsync`): its
  status was frozen while it was `Long`.
* **`Both`**: if a hybrid property is ever added, `PropertyRentalModeRules.IsShortRent` and `PublicListing.IsPublished` are the
  two places to decide what it means for the public site and for the compliance.
* The dashboard of the short-rent host should leave the `Long` properties out of its occupancy (not done here).
