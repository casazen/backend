# Runbook: iCal import (property OTA calendars, supplier calendars)

Task PC-10 (audit defects A2-10, A2-12, A2-23, A9-13). The download of the feed (anti-SSRF client, size and time
limits, error codes) is described in [external-fetch.md](external-fetch.md) (FD-16). This page covers what happens
after the download: how a feed is read, when it is an error, and how the sync job isolates feeds.

Nothing has to be configured: the defaults below apply when the `ICalImport` section is missing.

## Where the code is

| What | Code |
|---|---|
| Reader of a feed (Ical.Net 4.3.1, no known vulnerability: `dotnet list package --vulnerable` on 2026-09-24) | `Casazen.Infrastructure/Services/ICal/ICalFeedParser.cs` |
| Recurrence window, mapping to property blocks and supplier busy days | `Services/ICal/ICalImportService.cs`, `ICalImportOptions.cs` |
| Export writer (unchanged, the format is PC-12) | `Services/ICal/ICalFeedWriter.cs`, `Services/ICalExportService.cs` |
| Property sync (per feed, per batch) | `Services/PropertyICalSyncService.cs`, job `Web/BackgroundJobs/PropertyICalSyncJob.cs` |
| Supplier sync | `Services/CalendarSyncService.cs`, job `IcalSupplierSyncJob` |

The former F0 spike (`Casazen.Infrastructure/ICalSpike`, `ICalImportSpike`) no longer exists.

## When a feed is valid

| Downloaded document | Result | Stored |
|---|---|---|
| Starts with `BEGIN:VCALENDAR` (after a BOM or blank lines) and Ical.Net reads it, **even with 0 events** | Success. The imported blocks missing from the feed are **removed** (an OTA reservation cancelled → dates free again) | `LastImportStatus = Success`, `LastError = null` |
| Empty body, HTML login page, JSON, anything not starting with `BEGIN:VCALENDAR` | Error, blocks kept | `Failure`, `ical_invalid_format` |
| Truncated or broken calendar (Ical.Net cannot read it) | Error, blocks kept | `Failure`, `ical_invalid_format` |
| Download failure | Error, blocks kept | `Failure`, `ical_unreachable` / `ical_too_large` / `ical_invalid_url` (FD-16) |
| Database write rejected, or any other unexpected failure of that feed | Error, blocks kept | `Failure`, `ical_sync_failed` |

The API contract of FD-16 is unchanged: `lastErrorCode` / `calendarSyncErrorCode` carry the code, `lastError` /
`calendarSyncError` its localized text. No new code was added.

## How events are read

- `STATUS:CANCELLED` and `TRANSP:TRANSPARENT` (free time) events block nothing. A cancelled instance of a recurring
  event (override with `RECURRENCE-ID` and `STATUS:CANCELLED`) frees that instance only.
- **All-day** values (`VALUE=DATE`) are calendar dates, taken as written: `DTSTART;VALUE=DATE:20261010` +
  `DTEND;VALUE=DATE:20261012` blocks the nights of 10 and 11 October (DTEND is the check-out day). Without DTEND the
  event lasts one day. The server's time zone plays no part (before PC-10 the dates moved back one day on a server east
  of UTC).
- **Timed** values are instants (`Z`, or `TZID`), converted to Europe/Rome before taking their date. A floating time
  (no `Z`, no `TZID`) or an unknown `TZID` is read as Europe/Rome wall clock. The nights are [Rome date of the start,
  Rome date of the end): 10 Oct 15:00 → 12 Oct 10:00 blocks 10 and 11. An event inside one day (10:00-12:00) blocks no
  night of a property; for a supplier it marks that day busy.
- **Recurring** events (`RRULE`, `RDATE`, minus `EXDATE` and the instances replaced by a `RECURRENCE-ID` override) are
  expanded from `RecurrenceMonthsBack` months before today (Europe/Rome) to `RecurrenceMonthsAhead` months after. Each
  instance is a block with key `UID#yyyyMMdd`. Rules that repeat more than once a day (`FREQ=HOURLY`, `BYHOUR` lists,
  ...) are not expanded: they block no night and could produce millions of instances.
- Single events are imported whatever their dates (the feed decides what it publishes).
- An event that cannot be read (no DTSTART, end before start) is skipped and counted; the other events are imported.
  The blocks it created at earlier syncs (same UID) are **kept**: an unreadable event is not proof that the
  reservation is gone.
- Control characters are removed (a NUL byte would make the whole feed unreadable and PostgreSQL refuses it).

## Keys and column limits (A2-12)

Blocks go into `CalendarBlocks` (unique index `PropertyId + ExternalUid`, `ExternalUid` and `Summary` varchar(500)).

- `SUMMARY` is cut to 500 characters (never in the middle of a surrogate pair).
- The key is the UID. An event without UID gets a hash of its dates and summary, the same at every sync (Ical.Net's
  own random UID is ignored). A key longer than 500 characters is stored as `sha256:<hex>`.
- A UID repeated in the same feed: identical copies are merged; copies with other dates get one key per start date
  (`UID#yyyyMMdd`), so no busy night is dropped; same UID and start with different ends keep the longest.

## Isolation and concurrency (A2-12)

- `property-ical-sync` (every 15 min, `[DisableConcurrentExecution]`, FD-11) syncs the feeds least recently synced
  first. Each feed runs in its **own DI scope** (own `AppDbContext`) and its own try/catch: a feed that fails, even
  with an unexpected exception, is logged, gets its own `Failure` state (`ical_sync_failed`) and the batch goes on.
- Inside a feed the blocks are written in one transaction holding the PostgreSQL advisory lock
  `PropertyICalSync` on the property: the recurring job and `PropertyICalSyncJob.SyncFeedAsync` (first sync of a new
  URL) wait for each other instead of inserting the same UIDs twice.
- If the import URL changed while the old one was downloading, the old result is discarded (the job of the new URL
  writes).
- After a rejected write the context is cleared before the `Failure` state is saved: the same changes are never
  saved twice.
- The supplier batch has the same per-supplier try/catch.

## Supplier calendars: known limit

A supplier feed marks busy the days its events touch (`SupplierAvailability.Available = false`). A valid calendar
without events is a success (no error shown), but days marked busy by earlier syncs are **not** freed when their events
disappear: `SupplierAvailability` does not record whether a day came from the feed or from the supplier, so the sync
cannot tell which days it may release. Fixing it needs a source column (migration) and is left open (see PC-10 report).

## Logs

Every line names the feed id and the property id (or the supplier org id), never the URL: export links carry secret
tokens. A document that is not iCalendar is logged with the failure kind and the exception type only (the parser's
message can quote the document). Skipped events are logged as counts with the type of the first error.

| Message (start) | Level | Meaning |
|---|---|---|
| `iCal sync completed for feed …` | Information | Blocks written, removed, cancelled/free events ignored, duplicates merged |
| `iCal feed … : N unreadable events and M sub-daily recurrences skipped` | Warning | The feed is imported without those events |
| `iCal feed … is not a readable iCalendar` | Warning | `ical_invalid_format` stored, blocks kept |
| `iCal sync failed for feed …` | Error | Database write rejected or other unexpected failure, `ical_sync_failed` stored |
| `iCal sync of property … failed; continuing with the next feed` | Error | Even the failure state could not be saved (e.g. database down); the batch goes on |
| `iCal feed … changed during the sync: result discarded` | Information | URL replaced during the download |

## Configuration (Railway variables, optional)

| Variable | Meaning | Default |
|---|---|---|
| `ICalImport__RecurrenceMonthsAhead` | Months after today (Europe/Rome) in which recurring events are expanded (1-60) | `18` |
| `ICalImport__RecurrenceMonthsBack` | Months before today still expanded, for the calendar views (0-12) | `1` |

## Checks after a deploy

1. Property with an Airbnb or Booking.com import URL and at least one reservation: `GET /api/properties/{id}/ical/status`
   shows `lastImportStatus: "Success"` and `blockCount` > 0.
2. Cancel the only reservation on the OTA (or point a test property to a calendar with no events, e.g. a new empty
   Google Calendar made public): after the next sync (max 15 min, or save the URL again) `blockCount` is 0,
   `lastImportStatus` is `Success` and `lastErrorCode` is null.
3. Save as import URL a public https page that is not a calendar: the status becomes `Failure` with
   `lastErrorCode: "ical_invalid_format"` and the existing blocks stay.
4. In the logs of the `property-ical-sync` job, a failing feed is followed by the `iCal sync completed` lines of the
   other feeds.
