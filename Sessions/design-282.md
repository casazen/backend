# Design — #282 Calendar FE infinite loop

## API Contract

N/A — no backend changes. Existing `GET /api/bookings/calendar` requires `propertyId`, `startDate`, `endDate`.

## Frontend Flow

| Route | Change |
|---|---|
| `/app/short-rent/bookings/calendar` | `CalendarPage` only calls `useBookingCalendar` when `propertyId` resolved; empty state without API calls |

### Components

- `calendar-page.tsx` — property picker, empty state (IT), error state
- `use-bookings.ts` — `enabled: !!params?.propertyId`, `retry: 1`

### E2E

- `e2e/calendar-property-guard.spec.ts` — demo mode: open calendar, assert ≤1 calendar API request after load; empty state copy visible when no properties

## Security Notes

Authenticated route behind `<OnboardingGuard>` / `<ProtectedRoute>`. No new endpoints.

## Migration Plan

N/A — no schema changes.

## GDPR Scope

N/A

## Open Questions

None.
