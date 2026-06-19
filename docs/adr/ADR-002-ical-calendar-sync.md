# ADR-002: iCal import/export and sync job

**Status:** Accepted (Fase 0 spike)  
**Date:** 2026-06-19  
**Issue:** #289  
**Informs:** `spec-ical-calendar-sync` (US-018, Fase 1)

## Context

MVP OTA integration uses RFC 5545 iCal only — no partner API contracts. Hosts paste Airbnb/Booking.com calendar URLs; CasaZen imports busy periods and exports a feed for OTAs. Must prevent double-booking between direct site and OTA calendars.

## Decision

### Parser library — Ical.Net

Use **[Ical.Net](https://github.com/rianjs/ical.net)** (NuGet) for RFC 5545 parsing in .NET:

- Mature VEVENT/VTIMEZONE handling.
- MIT license, active maintenance.
- **Rejected:** NodaTime.iCal — less ecosystem fit; **Rejected:** custom parser — unnecessary risk.

### Domain model

```csharp
CalendarBlock { Id, PropertyId, OrgId, Source (ICalImport|Manual), ExternalUid?, StartUtc, EndUtc, Summary?, LastSyncedAt }
PropertyICalFeed { PropertyId, ImportUrl (encrypted), ExportToken, LastImportAt, LastImportStatus, LastError? }
```

Unique index: `(PropertyId, ExternalUid)` for idempotent upsert.

### Sync interval — 15 minutes

Hangfire recurring job `ICalSyncJob` every **15 minutes** (not 30):

- Acceptable delay for MVP per planning trade-off.
- Configurable via `Ical:SyncIntervalMinutes` for ops tuning.

Job flow per active feed:

1. HTTP GET `ImportUrl` (https only, 30s timeout).
2. Parse VEVENTs → upsert `CalendarBlock` by `ExternalUid`.
3. Delete blocks no longer in feed (tombstone by sync generation).
4. Update `LastImportAt` / `LastError` on failure.

### Export feed

`GET /api/public/ical/{exportToken}` → `text/calendar` with VEVENT for confirmed bookings + manual blocks. **No PII** in SUMMARY/DESCRIPTION.

Export token: 32-byte cryptographically random, rotatable by host.

### Availability integration

`IBookingService.IsPropertyAvailableAsync` checks overlapping `Booking` + `CalendarBlock`.

`GET /api/bookings/calendar` returns blocks with `type: "ical-block"` distinct from bookings.

### Encryption

`ImportUrl` encrypted at rest via ASP.NET Data Protection (same pattern as Stripe keys).

## PoC scope (Fase 0)

Spike validates:

1. Parse sample Airbnb `.ics` fixture → in-memory `CalendarBlock` list.
2. Overlap detection against a test booking range.
3. Export VEVENT round-trip readable by Google Calendar import.

PoC code lives in `Casazen.Tests/Spikes/ICalParserSpikeTests.cs` (Fase 1 implementation PR).

## Consequences

- Fase 1: EF migration, Hangfire job, property settings UI, calendar UI legend.
- Failed imports surface Italian error in host console; do not block direct bookings.
