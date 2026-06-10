## Summary

Bookings calendar page is **stuck on "Loading calendar..."** in production.

## Reproduction

1. Login → **Calendario** (`/app/short-rent/bookings/calendar`)
2. Page never renders calendar widget

**Network**: repeated `GET /api/bookings/calendar` → **404** body: `Property not found`

## Root cause (FE/BE contract mismatch)

- **Frontend** (`bookings.api.ts`): calls `GET /bookings/calendar` with optional params
- **Backend** (`BookingController.GetCalendar`): requires `propertyId` (Guid), `startDate`, `endDate` query params
- FE sends request **without params** → empty Guid → property lookup fails → 404
- React Query retries indefinitely; no error state in `calendar-page.tsx`

## Suggested fix

**Option A (FE)**: Pass default date range + first property id (or org-wide aggregate endpoint)
**Option B (BE)**: New endpoint `GET /bookings/calendar` without required propertyId — returns all org bookings
**Option C (FE)**: Handle 404/error — show empty calendar instead of infinite spinner

## Evidence

Spec: `Sessions/specs/spec-production-e2e-flow-verification.md`
Tested: 2026-06-09
