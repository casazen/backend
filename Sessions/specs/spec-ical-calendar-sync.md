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
last_reviewed: 2026-08-13
---

# Spec — iCal Calendar Sync (US-018)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

MVP OTA integration uses **RFC 5545 iCal** only (no partner API). Hosts paste external calendar URLs (Airbnb, Booking.com, etc.); CasaZen imports busy periods and exports a feed for OTAs to subscribe. Prevents double-booking between direct site and OTA calendars.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a host, I want to connect my Airbnb/Booking calendar via iCal URL so that my CasaZen direct booking site shows correct availability without double bookings.

As CasaZen, we want periodic sync without OTA API contracts in MVP.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

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

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC10**: Property settings → "Calendario OTA" card: paste import URL, show last sync time, error message, copy export URL.

- **AC11**: Calendar UI shows iCal blocks in distinct color; legend in Italian.

### App host

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC12**: Calendar screen shows same blocks as web (AC8); last sync timestamp on property detail.

### Regression

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC13**: Direct checkout availability (US-002) respects imported blocks — E2E in golden journey step 4–5.

---


## UX / UI Quality



**Required** (Frontend ACs present). Testable bar for Stage 03.



| Criterion | Required | How to verify |

|---|---|---|

| Primary path clear | User completes happy path without guessing | L3 scripted flow below |

| Language | End-user strings Italian | L2/L3 assert Italian primary labels |

| Empty state | No blank dead-end when data length = 0 | L2 empty fixture |

| Error state | 4xx/5xx as human Italian message | L2/L3 forced error |

| Destructive / legal copy | Confirmations/disclaimers as in ACs | Assert documented phrases |



**Happy-path script:**



1. Enter the primary route for `ical-calendar-sync`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Export / Report Criteria



**Required** (export / feed / report ACs present).



### Feed / file



| Requirement | Required |

|---|---|

| Declared Content-Type matches payload (e.g. text/calendar, text/csv, application/pdf) | yes |

| Non-empty body when seed data exists | yes |

| No CF / P.IVA / secrets in filename or URL | yes |

| Documented columns/fields or VEVENT shape in AC / design | yes |



### PDF (when applicable)



| Requirement | Required |

|---|---|

| Real PDF bytes (%PDF) - not empty stub | yes |

| Readable labeled content for the intended audience | yes |

---


## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 + L2 + L3 | New entity `CalendarBlock` `{ Id, PropertyId, OrgId, Source (ICalImport/Manual), ExternalUid?, StartUtc, EndUtc, Summary?, LastSyncedAt }... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC2 | L1 | `PropertyICalFeed` `{ PropertyId, ImportUrl (encrypted at rest), ExportToken, LastImportAt, LastImportStatus, LastError? }`. | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | `PUT /api/properties/{id}/ical` (authenticated, owner) saves `importUrl`; validates URL scheme `https://`; returns `{ lastImportStatus }`. | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | `GET /api/properties/{id}/ical/export` returns public feed URL `{ exportUrl }` (tokenized, unguessable). | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | `GET /api/public/ical/{exportToken}` (`[AllowAnonymous]`) returns `text/calendar` with `VEVENT` for confirmed bookings + blocked periods ... | Outcome not met; wrong status; silent no-op |
| AC6 | L1 + L2 + L3 | `ICalSyncJob` (Hangfire, every 15 min) fetches each active `ImportUrl`, parses VEVENTs, upserts `CalendarBlock` by `ExternalUid`; marks f... | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC7 | L1 | `IBookingService.IsPropertyAvailableAsync` returns false if any `CalendarBlock` or confirmed `Booking` overlaps requested range. | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | `GET /api/bookings/calendar` includes `CalendarBlock` entries with `type: "ical-block"` distinct from bookings. | Outcome not met; wrong status; silent no-op |
| AC9 | L2 + L3 | Idempotent import — re-running job does not duplicate blocks (unique on `PropertyId + ExternalUid`). | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L2 + L3 | Property settings → "Calendario OTA" card: paste import URL, show last sync time, error message, copy export URL. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC11 | L2 + L3 | Calendar UI shows iCal blocks in distinct color; legend in Italian. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L2 + L3 | Calendar screen shows same blocks as web (AC8); last sync timestamp on property detail. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC13 | L2 + L3 | Direct checkout availability (US-002) respects imported blocks — E2E in golden journey step 4–5. | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

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

## Test expectations (process contract)



| Layer | Allowed | Forbidden as sole proof |

|---|---|---|

| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |

| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |

| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |



Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

---

## Out of Scope

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- OTA API push/pull (#31–#35 frozen)
- Two-way real-time sync (<15 min latency acceptable)

## Regulatory / Legal Gates

- None

## Open Questions

- None (or list with owner/date before Stage 03)
