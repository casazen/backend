# Design Spec — Issue #294: iCal Property OTA Calendar Sync

**Issue**: [#294](https://github.com/casazen/backend/issues/294)  
**Reuses**: `ICalImportSpike`, `CalendarSyncService` patterns from supplier iCal (#325)

## API Contract

| Method | Path | Auth | Request | Response |
|---|---|---|---|---|
| POST | `/api/properties/{id}/ical/import-url` | `[Authorize]` owner | `{ importUrl }` | 200 status DTO / 400 |
| GET | `/api/properties/{id}/ical/status` | `[Authorize]` owner | — | importUrl, exportUrl, lastImportAt, lastImportStatus, lastError, blockCount |
| GET | `/api/properties/{id}/ical/export-url` | `[Authorize]` owner | — | `{ exportUrl }` |
| GET | `/api/public/ical/{exportToken}` | `[AllowAnonymous]` | — | `text/calendar` |
| Modify | `GET /api/bookings/calendar` | `[Authorize]` | — | add `type: "ical-block"` entries |

**Background**: `PropertyICalSyncJob` Hangfire every 15 min.

## Frontend Flow

| Route | Component | ProtectedRoute |
|---|---|---|
| Property settings | `ical-settings.tsx` card | Yes (AppShell) |
| Booking calendar | legend + ical-block color | Yes |

## Security Notes

- ImportUrl HTTPS only; stored on PropertyICalFeed
- ExportToken unguessable Guid
- Public export: no PII in SUMMARY
- Property ownership via IPropertyAuthorizationService

## Migration Plan

`AddCalendarBlocksAndICalFeeds` — CalendarBlock + PropertyICalFeed

## GDPR Scope

N/A
