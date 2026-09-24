# Runbook: iCal import (property OTA calendars, supplier calendars)

Tasks PC-10 (audit defects A2-10, A2-12, A2-23, A9-13) and PC-11 (A2-11, A2-20). The download of the feed (anti-SSRF
client, size and time limits, error codes) is described in [external-fetch.md](external-fetch.md) (FD-16). This page
covers the import feeds of a property (many per property, URL encrypted), what happens after the download (how a feed
is read, when it is an error) and how the sync job isolates feeds.

Nothing has to be configured: the defaults below apply when the `ICalImport` section is missing. The encryption of the
import URLs uses the Data Protection key ring of [storage.md](storage.md) (FD-07), already required by the OTA secrets.

## Where the code is

| What | Code |
|---|---|
| Reader of a feed (Ical.Net 4.3.1, no known vulnerability: `dotnet list package --vulnerable` on 2026-09-24) | `Casazen.Infrastructure/Services/ICal/ICalFeedParser.cs` |
| Recurrence window, mapping to property blocks and supplier busy days | `Services/ICal/ICalImportService.cs`, `ICalImportOptions.cs` |
| Export writer (unchanged, the format is PC-12) | `Services/ICal/ICalFeedWriter.cs`, `Services/ICalExportService.cs` |
| Property feeds (list, add, remove, sync now) and sync (per feed, per batch) | `Services/PropertyICalSyncService.cs`, job `Web/BackgroundJobs/PropertyICalSyncJob.cs`, actions `ical/*` of `Web/Controllers/PropertiesController.cs` |
| Encryption of the import URL, startup re-encryption | `Data/Encryption/EncryptedStringConverter.cs`, `Data/Encryption/PropertyICalFeedUrlEncryption.cs` |
| Masked URL of the API | `Web/Infrastructure/ICalFeedUrlMask.cs` |
| Export link of a property | entity `PropertyICalExport` (table `PropertyICalExports`) |
| Supplier sync | `Services/CalendarSyncService.cs`, job `IcalSupplierSyncJob` |

The former F0 spike (`Casazen.Infrastructure/ICalSpike`, `ICalImportSpike`) no longer exists.

## Import feeds of a property (PC-11)

A property has **one feed per linked calendar** (table `PropertyICalFeeds`, many rows per property): the host links
Airbnb **and** Booking.com (and any other iCal source) and the dates of every one of them are taken on the booking site
(BK-05 `PropertyOccupancy` reads all the blocks of the property, whatever their feed).

- **Channel** (`Airbnb`, `BookingCom`, `Other`) and an optional **label** (free text, max 60 characters, no line
  breaks). When the host does not choose the channel it is taken from the host of the URL: `airbnb.<tld>` (and
  subdomains) is Airbnb, `booking.com` (and subdomains) is Booking.com, anything else is Other. Display only.
- **Blocks belong to their feed** (`CalendarBlocks.FeedId`, unique index `FeedId + ExternalUid`): a sync replaces only
  the blocks of its feed; an empty Booking.com calendar never frees the dates imported from Airbnb. Manual blocks have
  no feed.
- **Removing a feed** deletes its blocks and only those (FK `ON DELETE CASCADE`, and explicitly by the service): those
  dates become free on the site at once. The other feeds are untouched.
- **Limits**: at most `ICalImport:MaxFeedsPerProperty` feeds (default 10) and the same URL only once per property
  (compared on scheme, host, port, path and query).
- **Export link**: one per property (`PropertyICalExports`), unchanged by PC-11 (the tokens of the old single feed were
  moved there as they were, so the links already pasted on the OTAs keep working).

### API (TN-3: `PropertyRead` / `PropertyWrite` plus the resource check; another org gets 404)

| Request | Answer |
|---|---|
| `GET /api/properties/{id}/ical/feeds` | Feeds, oldest first: `id`, `channel`, `label`, `maskedImportUrl`, `createdAt`, `lastImportAt`, `lastImportStatus`, `lastErrorCode`, `lastError` (localized), `blockCount` |
| `GET /api/properties/{id}/ical/status` | `exportUrl`, `blockCount` (all blocks of the property), `feeds` (as above). The old top-level `importUrl` / `lastImportStatus` fields are gone |
| `POST /api/properties/{id}/ical/feeds` `{ channel?, label?, importUrl }` | 202 with the feed in `Syncing` and its first sync queued (Hangfire, never in the request). 400 `ical_invalid_url` (FD-16 validation: https, public host) or `ical_feed_invalid_label`; 409 `ical_feed_duplicate`; 422 `ical_feed_limit_reached` |
| `POST /api/properties/{id}/ical/feeds/{feedId}/sync` | "Sync now": 202, `Syncing`, one job queued. While the feed is already `Syncing` nothing more is queued (repeated clicks never pile up downloads) |
| `DELETE /api/properties/{id}/ical/feeds/{feedId}` | 204; 404 `ical_feed_not_found` when the feed is not one of the property's |
| `GET /api/properties/{id}/ical/export-url` | `exportUrl` (created on first use) |

`POST /api/properties/{id}/ical/import-url` (single URL) no longer exists. The per-feed state keeps the FD-16/PC-10
contract: `lastErrorCode` is the stable code, `lastError` its localized text; the frontend translates the code
(`apiErrors.codes.*`) and shows `lastError` only for a code it does not know.

### Import URL encrypted at rest (A2-20)

Airbnb links carry a token (`?s=…`) that gives access to the reservations: the URL is never stored, returned or logged
in clear.

- **Stored** encrypted by the EF value converter of `AppDbContext` (ASP.NET Data Protection, purpose
  `Casazen.PropertyICalFeed.ImportUrl`; same mechanism and key ring as the OTA secrets of FD-20). A raw
  `SELECT "ImportUrl"` shows a payload starting with `CfDJ8`. The column is `varchar(4096)` (the payload is longer
  than the URL, max 2048 characters).
- **Returned** only masked: host and last four characters (`www.airbnb.it/…9f3a`); paths shorter than 12 characters
  show the host only.
- **Logs** name feed and property ids, never the URL.
- **URLs saved before PC-11** are rewritten encrypted at every startup, right after the migrations
  (`PropertyICalFeedUrlEncryption.EncryptLegacyPlaintextUrlsAsync`, idempotent, log line
  `Encrypted N iCal import URLs stored in clear before PC-11`). Until then they are still readable (a value starting
  with `http://` or `https://` is read as it is). The same startup step **stops the application** if the context has
  no Data Protection (the URLs would otherwise be stored in clear).
- **Key ring lost** (table `DataProtectionKeys` emptied, certificate lost, see storage.md): the URLs cannot be read any
  more, the feeds list of those properties answers 500 and their sync fails. Remove those feeds
  (`DELETE FROM "PropertyICalFeeds" WHERE "PropertyId" = '…'`, their blocks go with them) and let the host link the
  calendars again.

**Verified limit of the converter (FD-20 note).** EF builds the model, converter included, once per process for all
the contexts created by the application's DI: the converter uses the Data Protection provider of the first context.
In the application there is one DI container, hence one provider, so encryption and decryption always use the
right key ring; contexts built by hand without the application's service provider (design time, some tests) get a
separate model and never replace it. Two rules follow: read and write the encrypted column **through EF only** (never
raw SQL with a protector taken from DI), and in tests share one provider between the web hosts of the process
(`CasazenWebApplicationFactory.SharedDataProtectionProvider`), otherwise every host would encrypt with the key ring of
the first one, possibly already disposed.

### Migration `AddICalMultiFeed` (single feed → list)

1. Every existing feed row gives its `ExportToken` to `PropertyICalExports` (one row per property, same token).
2. Each feed with a URL becomes the first feed of its property: same id, `Channel` from the host of the URL,
   `CreatedAt` = last import (or now). Its imported blocks get its `FeedId`: **no block is lost**, the next sync
   updates them in place.
3. Rows without URL (created only for the export link) are removed when no block points to them.
4. `ExportToken` leaves `PropertyICalFeeds`; the unique index on `PropertyId` becomes a plain index; `CalendarBlocks`
   gets the unique index `FeedId + ExternalUid` (instead of `PropertyId + ExternalUid`) and the FK with cascade.

The migration logs a notice: `AddICalMultiFeed: airbnb_feeds=…, booking_feeds=…, blocks_attached=…,
export_only_rows_removed=…, imported_blocks_without_feed=…`. `imported_blocks_without_feed` should be 0: imported
blocks of a property without any feed row (not produced by the code) stay as occupancy but no sync touches them; list
them with `SELECT * FROM "CalendarBlocks" WHERE "Source" = 0 AND "FeedId" IS NULL` and delete them if they are stale.

**Rollback** (`Down`): the oldest feed of each property stays, with the export token of the property; the other feeds
and their blocks are deleted. The URLs stay encrypted: the code before PC-11 cannot use them, those feeds turn to
`Failure` until the host saves the URL again.

## When a feed is valid

| Downloaded document | Result | Stored |
|---|---|---|
| Starts with `BEGIN:VCALENDAR` (after a BOM or blank lines) and Ical.Net reads it, **even with 0 events** | Success. The blocks of this feed missing from it are **removed** (an OTA reservation cancelled → dates free again); the other feeds' blocks are never touched | `LastImportStatus = Success`, `LastError = null` |
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

Blocks go into `CalendarBlocks` (unique index `FeedId + ExternalUid` since PC-11, `ExternalUid` and `Summary`
varchar(500)).

- `SUMMARY` is cut to 500 characters (never in the middle of a surrogate pair).
- The key is the UID. An event without UID gets a hash of its dates and summary, the same at every sync (Ical.Net's
  own random UID is ignored). A key longer than 500 characters is stored as `sha256:<hex>`.
- A UID repeated in the same feed: identical copies are merged; copies with other dates get one key per start date
  (`UID#yyyyMMdd`), so no busy night is dropped; same UID and start with different ends keep the longest.

## Isolation and concurrency (A2-12)

- `property-ical-sync` (every 15 min, `[DisableConcurrentExecution]`, FD-11) syncs the feeds least recently synced
  first, every feed of every property. Each feed runs in its **own DI scope** (own `AppDbContext`) and its own
  try/catch: a feed that fails, even with an unexpected exception, is logged, gets its own `Failure` state
  (`ical_sync_failed`) and the batch goes on, also with the other feeds of the same property.
- Inside a feed the blocks are written in one transaction holding the PostgreSQL advisory lock
  `PropertyICalSync` on the property: the recurring job and `PropertyICalSyncJob.SyncFeedAsync(feedId)` (first sync of
  a new feed, "sync now") wait for each other instead of inserting the same UIDs twice. Adding and removing a feed take
  the same lock (feed count, duplicate check, removal while a sync writes).
- If the feed was removed while it was downloading, the result is discarded. A `SyncFeedAsync` job queued before PC-11
  carries a property id: no feed has that id, the job does nothing and the 15-minute job syncs the feed.
- After a rejected write the context is cleared before the `Failure` state is saved: the same changes are never
  saved twice.
- The supplier batch has the same per-supplier try/catch.

## Supplier calendars: known limit

A supplier feed marks busy the days its events touch (`SupplierAvailability.Available = false`). A valid calendar
without events is a success (no error shown), but days marked busy by earlier syncs are **not** freed when their events
disappear: `SupplierAvailability` does not record whether a day came from the feed or from the supplier, so the sync
cannot tell which days it may release. Fixing it needs a source column (migration) and is left open (see PC-10 report).

## Logs

Every line names the feed id and the property id (or the supplier org id), never the URL: import and export links
carry secret tokens. A document that is not iCalendar is logged with the failure kind and the exception type only (the parser's
message can quote the document). Skipped events are logged as counts with the type of the first error.

| Message (start) | Level | Meaning |
|---|---|---|
| `iCal sync completed for feed …` | Information | Blocks written, removed, cancelled/free events ignored, duplicates merged |
| `iCal feed … : N unreadable events and M sub-daily recurrences skipped` | Warning | The feed is imported without those events |
| `iCal feed … is not a readable iCalendar` | Warning | `ical_invalid_format` stored, blocks kept |
| `iCal sync failed for feed …` | Error | Database write rejected or other unexpected failure, `ical_sync_failed` stored |
| `iCal sync of feed … failed; continuing with the next feed` | Error | Even the failure state could not be saved (e.g. database down, URL not decryptable); the batch goes on |
| `iCal feed … changed during the sync: result discarded` | Information | Feed removed during the download |
| `iCal feed … added to property …` / `iCal feed … removed from property … with N blocks` | Information | Host linked or disconnected a calendar |
| `Encrypted N iCal import URLs stored in clear before PC-11` | Information | Startup re-encryption (first start after the PC-11 deploy) |

## Configuration (Railway variables, optional)

| Variable | Meaning | Default |
|---|---|---|
| `ICalImport__RecurrenceMonthsAhead` | Months after today (Europe/Rome) in which recurring events are expanded (1-60) | `18` |
| `ICalImport__RecurrenceMonthsBack` | Months before today still expanded, for the calendar views (0-12) | `1` |
| `ICalImport__MaxFeedsPerProperty` | Import feeds a property may link (1-50); each is downloaded every 15 minutes | `10` |

## Checks after a deploy

1. First start after the PC-11 deploy: the log shows the notice of `AddICalMultiFeed` and, if URLs were saved before,
   `Encrypted N iCal import URLs stored in clear before PC-11`. `SELECT "ImportUrl" FROM "PropertyICalFeeds"` shows
   only `CfDJ8…` payloads; `GET /api/properties/{id}/ical/feeds` shows `maskedImportUrl` only, and the export link of
   the property is the same as before.
2. Property with an Airbnb and a Booking.com calendar: both feeds reach `lastImportStatus: "Success"` with their own
   `blockCount`, and the public availability lists the nights of both.
3. Cancel the only reservation on one OTA (or point a test feed to a calendar with no events): after the next sync
   (max 15 min, or "sync now") that feed has `blockCount` 0 and `Success`; the other feed keeps its blocks.
4. Add as import URL a public https page that is not a calendar: the feed becomes `Failure` with
   `lastErrorCode: "ical_invalid_format"`; the other feeds are unaffected. Disconnect it: its blocks disappear.
5. Public booking site (BK-05): on the page of a published property with imported blocks, the availability calendar
   shows those nights as taken (`GET /api/public/bookings/property/{propertyId}/availability` lists them in
   `bookedDates`, dates only), and a checkout over one of them answers 409 `booking_dates_unavailable`. The check-out
   day of a block stays free. A property not published (inactive or compliance not activated) answers 404
   `public_property_not_found`.
