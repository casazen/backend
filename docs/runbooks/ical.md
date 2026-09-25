# Runbook: iCal import and export (property OTA calendars, supplier calendars)

Tasks PC-10 (audit defects A2-10, A2-12, A2-23, A9-13), PC-11 (A2-11, A2-20) and PC-12 (A2-22). The download of the
feed (anti-SSRF client, size and time limits, error codes) is described in [external-fetch.md](external-fetch.md)
(FD-16). This page covers the import feeds of a property (many per property, URL encrypted), what happens after the
download (how a feed is read, when it is an error), how the sync job isolates feeds, and the export feed read by the
OTAs ([Export feed](#export-feed-pc-12)).

Nothing has to be configured: the defaults below apply when the `ICalImport` section is missing. The encryption of the
import URLs uses the Data Protection key ring of [storage.md](storage.md) (FD-07), already required by the OTA secrets.

## Where the code is

| What | Code |
|---|---|
| Reader of a feed (Ical.Net 4.3.1, no known vulnerability: `dotnet list package --vulnerable` on 2026-09-24) | `Casazen.Infrastructure/Services/ICal/ICalFeedParser.cs` |
| Recurrence window, mapping to property blocks and supplier busy days | `Services/ICal/ICalImportService.cs`, `ICalImportOptions.cs` |
| Export feed: format (all-day events) and content (what is published, no echo), PC-12 | `Services/ICal/ICalFeedWriter.cs`, `Services/ICalExportService.cs`, `PropertyICalSyncService.BuildPublicExportAsync`, `Web/Controllers/PublicIcalController.cs` |
| Property feeds (list, add, remove, sync now) and sync (per feed, per batch) | `Services/PropertyICalSyncService.cs`, job `Web/BackgroundJobs/PropertyICalSyncJob.cs`, actions `ical/*` of `Web/Controllers/PropertiesController.cs` |
| Encryption of the import URL, startup re-encryption | `Data/Encryption/EncryptedStringConverter.cs`, `Data/Encryption/PropertyICalFeedUrlEncryption.cs` |
| Masked URL of the API | `Web/Infrastructure/ICalFeedUrlMask.cs` |
| Export link of a property, token and its regeneration (PC-12) | entity `PropertyICalExport` (table `PropertyICalExports`), `PropertyICalSyncService.RegenerateExportTokenAsync` |
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
| `POST /api/properties/{id}/ical/export-url/regenerate` (no body; `PropertyWrite`) | 200 `{ "exportUrl": "https://<App:ApiBaseUrl>/api/public/ical/<new uuid>" }` (same shape as `GET …/export-url`): the old link answers 404 from now on. 404 when the property is not visible (other org), 403 without write permission (PC-12, see [Export link token](#export-link-token)) |

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

**Limit of the converter noted by FD-20: verified and fixed.** EF caches the model (converters included) and by
default keys the cache on the context type only: the first `AppDbContext` of the process fixed the Data Protection
provider of every later one. It never mattered in production (one DI container, one provider), but in the tests the
hosts encrypted with the key ring of the first one, possibly already disposed, and the result depended on the order of
the tests. Since PC-11 `AppDbContext` replaces the cache key (`Data/Encryption/DataProtectionModelCacheKeyFactory.cs`):
the model is cached per provider, so each context encrypts and decrypts with its own provider; the application still
builds one model. Read and write the encrypted column **through EF only** (never raw SQL with a protector of its own).
The test hosts share one provider (`CasazenWebApplicationFactory.SharedDataProtectionProvider`) so they share one
model.

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

## Export feed (PC-12)

`GET /api/public/ical/{token}` (anonymous, `text/calendar`) is the link the host pastes on Airbnb, Booking.com and any
other channel so that they close the dates taken on CasaZen. It is built by `ICalExportService` on every request.

### Format

- Every event is **all-day**: `DTSTART;VALUE=DATE:yyyyMMdd` and `DTEND;VALUE=DATE:yyyyMMdd`, no time, no `TZID`, no
  `VTIMEZONE`. Before PC-12 the events were `DATE-TIME` in UTC (`...T000000Z`): an OTA converting that instant to its
  own zone could move a stay by one day.
- `DTEND` is **exclusive**: it is the departure day (check-out date of a booking, end date of a block), which stays
  free for a new arrival. A stay 10-12 October is `DTSTART;VALUE=DATE:20261010` / `DTEND;VALUE=DATE:20261012` and
  blocks the nights of 10 and 11 October, as the importer reads it (see [How events are read](#how-events-are-read)).
- **UID stable per event**: `booking-{bookingId}` (unchanged since #294, so the events the OTAs already read keep their
  identity) and `block-{blockId}` for a manual block (before PC-12 a block without UID got a new random one at every
  download). Events are ordered by date, then UID. `METHOD:PUBLISH`, `PRODID:-//CasaZen//Export//EN`.
- **SUMMARY always neutral**: `Occupato` (Italian, the default) or `Booked` when the request asks for English
  (`Accept-Language: en`), resource `ICalExportBusySummary`. Never a guest name, an email, a note, the booking code, or
  the SUMMARY of an imported event (channel managers put the guest's name there). No `DESCRIPTION`, `LOCATION` or
  attendee is written.
- A stay or block that takes no night (end date not after start date, the `PropertyOccupancy` rule) is not written.

### Content

| Published | Not published |
|---|---|
| Confirmed, checked-in and checked-out bookings of CasaZen: host bookings (`Manual`, PC-01), direct checkout, accepted "pay at the property" requests | Cancelled bookings |
| Checkout holds still valid (Pending with a PaymentIntent/SetupIntent inside the hold TTL, or already paying): they take their dates on the booking site too (BK-05, BK-21) | Expired holds (TTL passed, even before the expiry job cancels them) |
| Manual blocks of the host (`CalendarBlocks.Source = Manual`) | Pending "pay at the property" requests (BK-06: an anonymous request must not close the OTAs before the host accepts it) |
| | **Every block imported from an iCal feed** (`Source = ICalImport`, any channel) |
| | Bookings whose source is an OTA channel (`Airbnb`, `BookingCom`, `Expedia`, `Vrbo`, `TripAdvisor`, `Agoda`: `FiscalCopy.IsOtaBookingSource`) |

Which bookings take their dates is the single rule of the booking site: `CheckoutHolds.OccupiesDates` (the same TTL and
the same clock, `TimeProvider`, as public availability and the booking checks), then `OnSiteRequests.IsExportedToOtas`; the export adds only the two
"no echo" filters (`ICalExportService.ExportsBooking`, `ExportsBlock`). The nights are the dates of `PropertyOccupancy`.

### No echo: why imported blocks are never exported

The export has **one link per property**: it cannot know whether Airbnb or Booking.com is reading it (no reliable
signal in the request). Sending the imported blocks back would echo each OTA's reservations to itself: a reservation
cancelled on Airbnb would stay closed on Airbnb until CasaZen removed the block and Airbnb read the export again (hours),
and the guest's name written by some channel managers in SUMMARY would be published on an anonymous URL.

Chosen solution (the simplest safe one): **the export never contains imported blocks**, from any channel, nor bookings
that came from an OTA. Consequence for the host: CasaZen does **not** relay one OTA's reservations to another. Airbnb
learns about Booking.com reservations only if the host also links the Booking.com export on Airbnb (and the other way
round), which is how OTAs sync with each other without a channel manager. The alternative (one export link per
channel, each without that channel's own blocks, so CasaZen relays between OTAs) needs a token per channel and UI; it
is left to a product decision (PC-12 DUBBI).

### Export link token

- The token is the last part of the link (`/api/public/ical/{uuid}`). New tokens are 128 bits from
  `RandomNumberGenerator` (`PropertyICalExport.NewExportToken`), in UUID form so the links already pasted on the OTAs
  keep their shape; the tokens created before PC-12 (`Guid.NewGuid`, 122 random bits) stay valid until regenerated.
  An unknown token answers 404, like a regenerated one.
- **Regenerate**: `POST /api/properties/{id}/ical/export-url/regenerate` (policy `PropertyWrite` plus the resource check
  of the property: another org gets 404, a user without write permission 403). The new link is returned and shown by
  `GET .../ical/export-url` and `.../ical/status`; the old link answers 404 at once. The host must paste the new link
  on every channel where the old one was: until then those channels see no CasaZen booking (tell the host before
  regenerating). The log line `iCal export link of property … regenerated` names the property, never the token.
- **Contract for the host UI** (button of PC-13):
  - request: `POST /api/properties/{propertyId}/ical/export-url/regenerate`, authenticated (JWT), no body;
  - 200: `{ "exportUrl": "<App:ApiBaseUrl>/api/public/ical/<new uuid>" }` (`PropertyIcalExportUrlDto`, same shape as
    `GET /api/properties/{propertyId}/ical/export-url`): show and copy this link, and refresh the `ical/status` and
    `ical/export-url` queries;
  - 404: property not found or of another org; 403: the user has no write permission on the property;
    errors go through `getProblemMessage(err, t)`;
  - ask for a confirmation first: the old link stops working at once and the host has to paste the new one on every
    channel.
- **Stored in clear, not hashed** (decision documented here): the host must be able to copy the link again at any time
  (`GET .../ical/export-url`, used by the calendar settings page), so the server has to be able to rebuild it; a hash
  would show the link only once, at creation or regeneration. The feed publishes only busy dates, which the database
  holds anyway (whoever can read the table also reads the bookings), and for a published property the same nights are
  public on the booking site (`GET /api/public/bookings/property/{id}/availability`). A leaked link is handled by
  regenerating it. Never log the token or the full link.
- **Rate limit** (FD-10): policy `PublicIcal`, fixed window per client IP, default 60 requests per minute
  (`RateLimiting__PublicIcal__PermitLimit`, `RateLimiting__PublicIcal__WindowSeconds`); over the limit 429 `rate_limited`
  with `Retry-After`. The OTAs poll each link every few hours at most, but a host with many properties is polled by the
  same OTA servers: raise the limit on Railway if the log shows `Rate limit exceeded: policy PublicIcal` for OTA
  traffic. Enumeration of tokens is not a practical threat at 128 bits.

### Checks after a deploy

1. Open the export link of a property with a confirmed booking: the events have `DTSTART;VALUE=DATE:` and
   `DTEND;VALUE=DATE:` lines (no `T…Z`), `SUMMARY:Occupato`, `UID:booking-…`, and no name or email anywhere.
2. A property that imports an Airbnb calendar: the Airbnb reservations are **not** in its export.
3. Regenerate the link (`POST …/ical/export-url/regenerate`): the old URL answers 404, the new one 200; paste the new
   one on the OTAs.

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
| `iCal export link of property … regenerated` | Information | Host replaced the export token (PC-12): the old link answers 404 |

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
