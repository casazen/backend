---
id: US-018
slug: ical-calendar-sync
title: iCal calendar import/export (OTA bridge)
phase: 1
type: feature
priority: P0
status: specced
issue:
depends_on: [tenant-boundary]
blocks: [golden-journey-e2e]
exit_contributes_to: Unified calendar — direct + OTA blocks; GJ step 5
last_reviewed: 2026-06-19
---

# Spec — iCal Calendar Sync (US-018)

## Overview

MVP OTA integration uses **RFC 5545 iCal** only (no partner API). Hosts paste external calendar URLs (Airbnb, Booking.com, etc.); CasaZen imports busy periods and exports a feed for OTAs to subscribe. Prevents double-booking between direct site and OTA calendars.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

As a host, I want to connect my Airbnb/Booking calendar via iCal URL so that my CasaZen direct booking site shows correct availability without double bookings.

As CasaZen, we want periodic sync without OTA API contracts in MVP.

---

## Acceptance Criteria

### Backend

- **AC1**: New entity `CalendarBlock` `{ Id, PropertyId, OrgId, Source (ICalImport|Manual), ExternalUid?, StartUtc, EndUtc, Summary?, LastSyncedAt }` with `OrgId` FK (RF1).

- **AC2**: `PropertyICalFeed` `{ PropertyId, ImportUrl (encrypted at rest), ExportToken, LastImportAt, LastImportStatus, LastError? }`.

- **AC3**: `PUT /api/properties/{id}/ical` (authenticated, owner) saves `importUrl`; validates URL scheme `https://`; returns `{ lastImportStatus }`.

- **AC4**: `GET /api/properties/{id}/ical/export` returns public feed URL `{ exportUrl }` (tokenized, unguessable).

- **AC5**: `GET /api/public/ical/{exportToken}` (`[AllowAnonymous]`) returns `text/calendar` with `VEVENT` for confirmed bookings + blocked periods (no PII).

- **AC6**: `ICalSyncJob` (Hangfire, every 15 min) fetches each active `ImportUrl`, parses VEVENTs, upserts `CalendarBlock` by `ExternalUid`; marks failures on `PropertyICalFeed.LastError`.

- **AC7**: `IBookingService.IsPropertyAvailableAsync` returns false if any `CalendarBlock` or confirmed `Booking` overlaps requested range.

- **AC8**: `GET /api/bookings/calendar` includes `CalendarBlock` entries with `type: "ical-block"` distinct from bookings.

- **AC9**: Idempotent import — re-running job does not duplicate blocks (unique on `PropertyId + ExternalUid`).

### Frontend (host web)

- **AC10**: Property settings → "Calendario OTA" card: paste import URL, show last sync time, error message, copy export URL.

- **AC11**: Calendar UI shows iCal blocks in distinct color; legend in Italian.

### App host

- **AC12**: Calendar screen shows same blocks as web (AC8); last sync timestamp on property detail.

### Regression

- **AC13**: Direct checkout availability (US-002) respects imported blocks — E2E in golden journey step 4–5.

---

## Technical Notes

| File | Action |
|---|---|
| `Casazen.Core/Entities/CalendarBlock.cs` | Create |
| `Casazen.Core/Entities/PropertyICalFeed.cs` | Create |
| `Casazen.Infrastructure/Services/ICalImportService.cs` | Create — RFC 5545 parser |
| `Casazen.Web/BackgroundJobs/ICalSyncJob.cs` | Create — register in `Program.cs` |
| `Casazen.Web/Controllers/PropertiesController.cs` | Modify — iCal config endpoints |
| `Casazen.Web/Controllers/PublicIcalController.cs` | Create — export feed |
| `Casazen.Infrastructure/Services/BookingService.cs` | Modify — availability includes blocks |

**Complexity:** M  
**Migration:** yes — `AddCalendarBlocksAndICalFeeds`  
**Dependencies:** `spec-tenant-boundary`

---

## Out of Scope

- OTA API push/pull (#31–#35 frozen)
- Two-way real-time sync (<15 min latency acceptable)
