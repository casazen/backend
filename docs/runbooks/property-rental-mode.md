# Runbook: rental mode of the properties (short or long, `Property.RentalMode`)

Task PM-01 of the redesign wave (decisions D16 and D19; report `gap/06` §4.4). Until PM-01 a property had no mode: the only
marker of a property kept for long-term leases was **implicit**, no guests and no nightly rate (`MaxGuests = 0` and
`NightlyRate = 0`, audit A7-06), and every predicate that must ignore such a property (the public site, the bookings, the
compliance, the CIN alerts) forgot it: for instance `CinDeadlineAlertService` wrote also to the hosts who only have leases.
PM-01 adds the mode, migrates the existing rows and makes those predicates look at it. **Backend only**: no screen, no
wizard (PM-03). The mode itself is not behind a flag: a `Long` property exists only after the backfill below, when a client
asks for it or after a scheduled change, and nothing changes for a property in `Short` mode. Task PM-02 adds, behind the flag
`PropertyModeChange` (off by default), the **scheduled change of mode** between the two modes: section 8.

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
| **Change of mode** | the scheduled change of PM-02 (section 8, behind the flag `PropertyModeChange`): programmed for a day, checked against the stays, the imported calendar blocks and the leases, applied at midnight of Rome by the hourly job. It is the only code that changes the mode after the creation |

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
export of a `Long` property still shows its bookings and blocks (a property switched by PM-02 also exports the block that
closes its dates, section 8.4; one made `Long` by the backfill has none), the contracts have no constraint on the mode (no
lease is refused because the property is `Short`: the warning "switch to long" is the interface's, PM-03), the iCal import
and the OTA stays of a `Long` property still create their blocks and stays (they are the channel's reservations, section
8.4) and a `ServiceRequest` is not touched.

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

A property wrongly classified (rare, the marker is restrictive) is put back with the scheduled change of PM-02 once the flag
`PropertyModeChange` is on (section 8: tomorrow at the earliest, and only when no lease stands in the way). Until then, or when
the owner of the data cannot wait, **by hand**: `UPDATE "Properties" SET "RentalMode" = 0 WHERE "Id" = '<id>';` (the generic
API cannot change the mode, section 1; section 8.7 says what else to check if the property had a scheduled change or
the block of a change). Its compliance status was not touched by PM-01 and the next re-evaluation of the nightly check takes
it back in.

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
runs them. `PropertyRentalModeIntegrationTests` runs on the EF InMemory fallback locally and on PostgreSQL in the CI. The
tests of the scheduled change (PM-02) are in section 8.10.

## 7. For the next tasks

* **PM-02** (scheduled change): done, section 8. It is the only writer of the mode after the creation, under
  `BookingRepository.LockPropertyDatesAsync`, and a property that goes back to `Short` is evaluated again
  (`IPropertyComplianceStatusService.ReevaluateAsync`): its status was frozen while it was `Long`.
* **PM-03** (screens): the wizard and the entries from the property pages read the preview and write the change through
  the endpoints of section 8.3; the strings of the block reason `ModeChange` and of the error codes are theirs (section 8.9).
* **`Both`**: if a hybrid property is ever added, `PropertyRentalModeRules.IsShortRent` and `PublicListing.IsPublished` are the
  two places to decide what it means for the public site and for the compliance, and `PropertyModeRules.Opposite` and the
  rules of section 8.1 (a change always goes to "the other mode") must be rethought.
* The dashboard of the short-rent host should leave the `Long` properties out of its occupancy (not done here).

## 8. The scheduled change of mode (PM-02, decision D16)

Task PM-02 of the redesign wave (report `gap/06` §4.4 and §7 P6, `gap/04` §4.2). **Backend only** (the screens are PM-03),
behind the flag `Features:PropertyModeChange` (`Features__PropertyModeChange`, **off by default**, section 8.8). The owner
programs the passage of a property from short stays to long-term leases, or back, for a day; the property changes mode by
itself at midnight of Rome, never over a stay or a lease. Code: `Core/Services/IPropertyModeService.cs` (contract, error
codes), `Core/Services/PropertyModeRules.cs` (the rules, without a database), `Core/Entities/PropertyModeChange.cs`,
`Infrastructure/Services/PropertyModeService.cs`, `Web/Controllers/PropertyModeController.cs`,
`Web/BackgroundJobs/PropertyModeChangeJob.cs`, migration `AddPropertyModeChange`.

### 8.1 Rules

One rule for both directions. A stay, a block or a lease **stands in the way of a day `D`** when its last day is on or after
`D`; the property is free **from the day after** (`FreeFrom`). The first day a change can start is
`max(tomorrow, last FreeFrom)`; "today" is the calendar day of Europe/Rome (`TimeProvider.TodayInRome()`), and the first
possible day is tomorrow because the job applies the change at the first run after midnight. At most ten years ahead
(`PropertyModeRules.MaxYearsAhead`: a lease runs up to 4+4 years, and a typo like the year 2206 must not reach the calendar).

| Direction | What stands in the way | Its last day | Error when `D` is too early |
|---|---|---|---|
| short → long | **stays** of the property in status `Pending`, `Confirmed` or `CheckedIn`, whatever their source (the site, the host, a portal). A `Pending` host booking counts; a checkout hold of the public site past its time (`CheckoutHolds`, BK-21) does not; `Cancelled` and `CheckedOut` never do | the departure (`CheckOutDate`) | `409 property_mode_blocked_by_bookings` |
| short → long | **blocks imported from a portal calendar** (`CalendarBlocks.Source = ICalImport`) that are not already counted through an OTA stay (`PropertyOccupancy.IsRepresentedByStay`, CO-21). The reservations of the portals arrive as blocks. A block whose OTA stay was cancelled counts again on its own | the first free day of the block (`EndUtc`) | same |
| long → short | **leases** whose status is not `Draft` nor `Rejected` (the rule that refuses to delete a property, `PropertyRepository.SoftDeleteAsync`, made to depend on a day), ended or not | `EndDate` (decision D16: notice and early termination are entered by hand later, with the lease lifecycle) | `409 property_mode_blocked_by_lease` |
| long → short | **draft leases**: no day frees the property from a draft, it has to be **deleted first** (D16). Today the API has no endpoint to delete a draft (it arrives with the lease lifecycle of Affitti lunghi): a property with a draft cannot go back to short stays until then | none | `409 property_mode_blocked_by_draft_lease` |

Not in the way: the manual blocks of the host (owner stay, works), a lease of a property that goes long-term (there is no
constraint on leases, PM-01), the stays of a property that goes short. Other errors: `422 property_mode_unchanged` (already
in that mode), `422 property_mode_date_too_early` (today or the past), `422 property_mode_date_too_far`, `409
property_mode_change_exists` (one change waits at a time). The messages with a day carry the first free day, written as the
language of the request writes a date.

### 8.2 Data

`PropertyModeChanges` (`ITenantOwned`, migration `AddPropertyModeChange`, no data touched): `Id`, `OrgId`, `PropertyId`
(FK, cascade), `FromMode`, `ToMode`, `EffectiveDate` (the calendar day of Rome as midnight UTC), `Status`
(`PropertyModeChangeStatus`, integers, append only: `0` Scheduled, `1` Applied, `2` Cancelled, `3` Failed),
`CreatedByUserId` (Auth0 subject, never sent to a client), `CreatedAt`, `AppliedAt`, `CancelledAt`, `CancelledByUserId`,
`FailedAt`, `FailureReason` (a stable error code, never a text nor personal data). The rows stay: they are the history of the
modes of the property. Indexes: **`UIX_PropertyModeChanges_PropertyId_Scheduled`**, unique on `PropertyId` where
`"Status" = 0` (one change waiting per property; a 23505 on it answers `property_mode_change_exists`), `(Status,
EffectiveDate)` for the job, `(PropertyId, CreatedAt)` for the history.

### 8.3 API

Behind the flag: off, every request answers `404 not_found` before authentication. Policies (TN-3): the property core is
shared by short-rent hosts and long-term landlords (A7-06), so `SharedPropertyRead` for the state and `SharedPropertyWrite`
for the preview (it lists stays and leases), the creation and the cancellation, plus the check on the property row
(`SharedPropertyOperations`: its owner or an org-wide role, in the caller's org; another org's property is `404
property_not_found`). **No new permission**: the team permissions (`org.*`, AM-01) are not in this base, and the mode is a
field of the property, so whoever may write the property may change it. A collaborator with `property.read` only reads the
state. Dates are plain days (`2026-12-01`); a full date-time is a `400`. Enums are names (`Short`, `Long`).

| Request | Answer |
|---|---|
| `GET /api/properties/{id}/mode` | 200 `{ propertyId, rentalMode, scheduledChange, lastChange }`: the change waiting for its day (or `null`) and the last one that is over (applied, cancelled or failed) |
| `GET /api/properties/{id}/mode/preview?to=short\|long&date=2026-12-01` | 200 `{ propertyId, currentMode, targetMode, today, earliestDate, date, canSchedule, issues, blockers, calendarClosedUntil, scheduledChange }`. `date` optional (the first possible day); `issues` are the error codes the creation would answer, `[]` when it can be scheduled; `blockers` are what stands in the way of `date` (`kind` `Stay`, `ImportedBlock`, `Lease`, `DraftLease`; `id`, `start`, `end`, `freeFrom`, `status`, `source`: **ids, dates and a status, never a name, an e-mail or the text of a portal**). 200 even when the change is not possible: the answer says why. `400 property_mode_target_invalid` for a `to` that is not a mode; `422 property_mode_unchanged` |
| `POST /api/properties/{id}/mode/change` `{ to, effectiveDate }` | 201 with the change (`Scheduled`). The same checks as the preview, made again under the property lock. The host gets an e-mail. To long-term the calendar closes from the day (section 8.4) |
| `DELETE /api/properties/{id}/mode/change/{changeId}` | 204: the change is `Cancelled`; to long-term the calendar opens again. `404 property_mode_change_not_found` (also a change of another property), `409 property_mode_change_not_scheduled` when it is already applied, cancelled or failed |

Errors are ProblemDetails (FD-05) with a stable `code` and a message in the language of the request (`SharedResources`, IT
and EN). The messages that name a day carry the first free day. The same codes are the `issues` of the preview and the
`FailureReason` of a change the job could not apply.

| Status | Code | Message key | When |
|---|---|---|---|
| 400 | `property_mode_target_invalid` | `PropertyModeTargetInvalid` | `to` of the preview is not `short` or `long` (in the body of the creation a bad mode or a missing `to` or `effectiveDate` is a `400 validation_error`) |
| 404 | `property_not_found` | `PropertyNotFound` | no property with this id in the caller's org (also a deleted one) |
| 404 | `property_mode_change_not_found` | `PropertyModeChangeNotFound` | no change with this id on the property |
| 409 | `property_mode_change_exists` | `PropertyModeChangeExists` | the property already has a change waiting for its day |
| 409 | `property_mode_blocked_by_bookings` | `PropertyModeBlockedByBookings` | to long-term: a stay or an imported block ends on or after the day. Carries the first free day |
| 409 | `property_mode_blocked_by_lease` | `PropertyModeBlockedByLease` | to short stays: a lease (not a draft, not rejected) ends on or after the day. Carries the first free day |
| 409 | `property_mode_blocked_by_draft_lease` | `PropertyModeBlockedByDraftLease` | to short stays: the property has a draft lease |
| 409 | `property_mode_change_not_scheduled` | `PropertyModeChangeNotScheduled` | `DELETE` of a change that is no longer waiting |
| 422 | `property_mode_unchanged` | `PropertyModeAlreadySet` | the property is already in that mode |
| 422 | `property_mode_date_too_early` | `PropertyModeDateTooEarly` | today or the past. Carries tomorrow |
| 422 | `property_mode_date_too_far` | `PropertyModeDateTooFar` | more than ten years ahead. Carries the last day |
| — | `property_mode_changed` | — | failure reason only: on its day the property was not in the mode the change started from (section 8.7) |

Validation messages of the body: `PropertyModeTargetRequired`, `PropertyModeDateRequired`. On the manual blocks:
`calendar_block_invalid_reason` for `ModeChange` and `calendar_block_held_by_mode_change` (`CalendarBlockHeldByModeChange`) for
the deletion of the block of a mode change.

### 8.4 The calendar block (`CalendarBlockReason.ModeChange` = 3)

A change **to long-term** closes the dates of the property with a **manual** calendar block (`Source = Manual`, no feed,
`ManualReason = ModeChange`, no note) from the day of the change for two years (`PropertyModeRules.CalendarBlockYears`;
iCal has no event without an end, so the closure is long, not endless). It is the only thing that makes the portals, which
read the iCal export (`ICalExportService.ExportsBlock`: an all-day event `block-{id}` with the neutral summary, never the
reason), stop selling the property, and it is taken by the single occupancy rule (`PropertyOccupancy`) like any block.

* **Written when the change is programmed**, not only when it is applied: from then on the booking site, the host's own
  bookings and the portals refuse the nights from the day, so a stay cannot arrive between the programming and the day
  (the risk named by `gap/06` §4.4). At the application the block is ensured again (an identical one is kept, so the portals
  keep the same event; a stale one of an earlier round is replaced). **Removed** if the change is cancelled or fails, and when
  the property goes back to short stays.
* The **host cannot create it** (`422 calendar_block_invalid_reason`) **nor delete it** (`422
  calendar_block_held_by_mode_change`): it would reopen the dates. It is listed with the other manual blocks
  (`GET /api/properties/{id}/blocks`, reason `ModeChange`) and shown by the calendar as a manual block (the current screens
  show an unknown reason as "Other": PM-03 gives it a label).
* The block is written in the transaction of the change, not through `ICalendarBlockService` (its checks are the host's:
  one year at most, not in the past, no overlap with other manual blocks).
* **Limits, by design of the task**: after the two years the block ends and the dates would open on the portals again (no
  job renews it: follow-up BE-PM02-3; the query is in section 8.7); a property made `Long` by the PM-01 backfill has no
  block; a portal reservation made before the portal reads the new export arrives as an imported block or an OTA stay (the
  block check never refuses a channel's reservation, CO-21): it is in the way of the change at the application (section 8.5)
  or, after it, it shows in the calendar for the host to handle. CasaZen closes dates, it does not edit the host's listings
  on the portals (the e-mail says so).

### 8.5 The job `property-mode-change`

Hourly, minute 0 UTC (`PropertyModeChangeJob`, registered in `RecurringJobsRegistration` **only with the flag on**, removed
with `RemoveIfExists` otherwise, `[DisableConcurrentExecution]` 300 s). Hourly and not daily so that a change is applied in
the hour after midnight of Rome (22:00 UTC in summer, 23:00 in winter) and a run lost to a restart is made up at the next one.
`PropertyModeService.ApplyDueAsync`:

1. takes the session advisory lock `PropertyModeChangeRun` (key 1_501, "property-mode-change"; a run that finds it taken does
   nothing and says `Skipped`), on top of Hangfire's lock;
2. lists the `Scheduled` changes with `EffectiveDate <= today` (Rome), oldest day first;
3. for each one, in its own transaction under the dates lock of the property (the lock every booking takes): reads the change
   again (a second run, a retry or a cancellation has moved it: nothing to do), then **checks again** against today (a stay
   that left before today is no obstacle; the rule is the one of the creation). Nothing in the way: the mode is changed, the
   calendar block ensured (to long-term) or removed (to short stays) and the change is `Applied`. Something in the way: the
   change is `Failed` with the code of the rule in `FailureReason`, the property keeps its mode, the closure of a change to
   long-term is removed and the host is told with the first day that is free now. A property deleted since fails the change
   without telling anybody; a property whose mode was put back by hand since (section 8.7) fails it with `property_mode_changed`;
4. after the commit: back to short stays, `ReevaluateAsync` (BE-PM01-2: the compliance status was frozen while the property was
   long-term, and an active property that lost a requirement is suspended now, with the usual e-mail; a failure there is
   logged and the nightly check takes it up); then the e-mail to the host.

**Idempotent**: a change leaves `Scheduled` in the same transaction as its effect, so two runs, a retry or a manual trigger from
the dashboard are one application, one block and one e-mail. A change that raises an unexpected error stays scheduled and the
next run tries again (logged as an error with its id). A change whose day passed while the job was stopped is applied when it
runs again.

### 8.6 E-mails to the host

Queued on Hangfire after the commit (`IEmailQueue`), to the contact address of the org, Italian (default) and English texts
in `EmailTexts` (`PropertyMode*` keys), a link to the property page of the right area (`PublicSiteLinks.HostProperty`) or, back
to short stays, to the activation wizard. A failure to send never undoes the change.

| Template | When |
|---|---|
| `property-mode-change-scheduled` | the change was programmed: the day, what stays until then, what closes (to long-term) or what is needed (to short stays), how to withdraw it |
| `property-mode-change-applied` | the property changed mode: to long-term, until when the dates stay closed and that the portals' listings are the host's to remove; to short stays, the dates are free and the requirements to check |
| `property-mode-change-failed` | the job could not apply it: why (stays or portal reservations, a lease, a draft lease, anything else), the first free day, how to try again |

A cancellation sends nothing (the host did it).

### 8.7 Operations

```sql
-- the changes waiting, and their day
SELECT c."OrgId", c."PropertyId", c."FromMode", c."ToMode", c."EffectiveDate", c."CreatedAt"
FROM "PropertyModeChanges" AS c WHERE c."Status" = 0 ORDER BY c."EffectiveDate";

-- the changes that failed, and why
SELECT c."OrgId", c."PropertyId", c."ToMode", c."EffectiveDate", c."FailedAt", c."FailureReason"
FROM "PropertyModeChanges" AS c WHERE c."Status" = 3 ORDER BY c."FailedAt" DESC;

-- the closures that end within six months (nothing renews them: BE-PM02-3)
SELECT b."PropertyId", b."StartUtc", b."EndUtc"
FROM "CalendarBlocks" AS b
JOIN "Properties" AS p ON p."Id" = b."PropertyId"
WHERE b."ManualReason" = 3 AND p."RentalMode" = 1 AND b."EndUtc" < now() + interval '6 months' ORDER BY b."EndUtc";
```

* **A change that must not run** (flag turned off with changes waiting, a wrong day): while the flag is off the endpoints answer
  404 and the job is not registered, so a waiting change waits, untouched, until the flag is on again (then the job applies it
  if its day has come, after checking it). To withdraw one without the API:
  `UPDATE "PropertyModeChanges" SET "Status" = 2, "CancelledAt" = now() WHERE "Id" = '<id>' AND "Status" = 0;` and, for a
  change to long-term, delete its closure: `DELETE FROM "CalendarBlocks" WHERE "PropertyId" = '<id>' AND "ManualReason" = 3;`.
* **A property put back by hand** (`UPDATE "Properties" SET "RentalMode" = …`, section 4): if it had a waiting change, cancel it
  as above (otherwise the job fails it with `property_mode_changed`); put back to `Short` also delete the block of reason `3`
  (the closure of a long-term property: it is not removed by the update); put to `Long` the property gets no block (create the
  change instead).
* **Checks after the deploy with the flag on** (test environment): `GET /api/public/features` has `propertyModeChange: true`;
  `GET /api/properties/{id}/mode/preview?to=long` on a property with a stay answers its `earliestDate`; programme a change
  for tomorrow, see `UID:block-{id}` in the iCal export and the nights taken in
  `GET /api/public/bookings/property/{id}/availability`; cancel it, both go away; programme it again, wait for the job at
  midnight of Rome (or trigger `property-mode-change` from the Hangfire dashboard once tomorrow has come) and see
  `rentalMode: "Long"`, the property gone from the public site, the "applied" e-mail.

### 8.8 Flag and rollback

`Features__PropertyModeChange` (default `false`; `docs/runbooks/feature-flags.md`). Off: nothing of this section is reachable,
the job is not registered, and everything else works as after PM-01. The data is additive: the table is new, the new enum value
is written only by this service. Rollback of the application to a version without PM-02 leaves the table and the blocks in
place: a change waiting is never applied by the old code, and the closure it wrote stays in the calendar and the export (the old
code does not know the reason `3`: the screens show it as "Other", and the host can delete it, which reopens the dates; for a
property that is long-term at that moment, check the dates on the portals). `dotnet ef database update AddPropertyRentalMode`
drops the table (the history of the changes is gone); delete the blocks of reason `3` first if the properties go back to short
stays (section 8.7).

### 8.9 For PM-03 (screens)

The wizard calls the preview with the other mode and a day, shows `blockers` and `earliestDate`, and posts the change. The
data the demo asks in "Ultimi dati per la nuova modalità" (CIN, price, guests, APE, rent) is not part of the change: it is
written with the existing endpoints (`PUT /api/properties/{id}`, `PUT …/cin`) before or after. The demo's option "close the
calendar" is not an option here: a change to long-term always closes it (section 8.4). A long-term landlord has no short-rent
context: the pages after the return to short stays need that area (outside this task). The `apiErrors.codes.*` of the web
client need the codes of section 8.1 and `calendar_block_held_by_mode_change`; the block reason `ModeChange` needs its label in
the calendar and in the list of blocks; `GET /api/public/features` carries `propertyModeChange`.

### 8.10 Tests

| Test | Covers |
|---|---|
| `PropertyModeRulesTests` | the rule in both directions without a database: first day, last day + 1, drafts, today and the past, ten years, the issues and their order, the exceptions with the day, the stored integers |
| `PropertyModePreviewTests` | what is read from the stays (statuses, expired holds, other properties), the imported blocks (channel, OTA stay, cancelled stay), the manual blocks, the leases (every status, rejected, ended), the drafts; the state |
| `PropertyModeScheduleTests` | one change at a time, cancel and schedule again, each refusal writes nothing, the calendar block written and removed, the notice and its failure, cancel in every status |
| `PropertyModeApplyTests` | applied on its day (summer and winter midnight of Rome), idempotent (two runs, two contexts), checked again (stays, imported blocks, leases, drafts), late run, deleted property, mode put back by hand, the compliance evaluated after the save, notice and evaluation failures |
| `PropertyModeCalendarBlockTests` | the export event, the single occupancy rule, the host cannot create nor delete the block |
| `PropertyModeNotificationTests`, `PropertyModeResourcesTests` | the three e-mails (IT/EN, links, encoding), the messages with a day in both languages |
| `PropertyModeControllerTests`, `AuthorizationAttributeTests` | the routes, the policies, 404/403 before the service, the parsing of the target and of the day, the JSON shape |
| `PropertyModeChangeJobTests`, `RecurringJobsConcurrencyTests`, `RecurringJobsFeatureFlagTests` | registered hourly only with the flag, removed otherwise, never overlaps |
| `PropertyModeIntegrationTests`, `PropertyModeFlagOffIntegrationTests` | the real HTTP pipeline, with the flag on and off: access, preview, programming and cancellation in IT/EN, the closed calendar (site, export, host), the job from the service graph |
| `PropertyModeChangePostgresTests` (PostgreSQL) | the partial unique index, change against booking (and against cancel) at the same time under the lock, concurrent runs apply once, the run lock, the migration's indexes |
