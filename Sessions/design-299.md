# Design Spec — Issue #299: Native host app Expo (US-025)

**Issue**: [#299](https://github.com/casazen/backend/issues/299)  
**Feature**: feat(MVP F1): native host app Expo — calendar, push, GJ M1–M7  
**Spec**: `Sessions/specs/spec-native-host-app.md`  
**Status**: complete  
**Date**: 2026-07-17  
**Branch**: `feature/299-native-host-app` (backend + mobile)  
**Depends on**: #293, #294, #295, #296 (shipped)  
**Blocks**: #301 (golden-journey-e2e Maestro M1–M7)

---

## Summary

Ship Expo SDK 52 host app in `casazen/mobile` with Auth0 PKCE, React Query data layer, and MVP screens (calendar, booking detail, service request, mark paid, quick checkout, property list). Backend adds `DeviceRegistration` entity, `POST /api/devices`, and `PushNotificationService` (Expo Push API) integrated into guest check-in reminder, checkout reminder, and service-request status changes.

Web-only flows deep-link to existing web routes. Mobile reuses existing booking/calendar/service-request/compliance APIs — no duplicate surface.

---

## Data model

### New entity — `DeviceRegistration` (migration `AddDeviceRegistrations`)

| Column | Type | Notes |
|---|---|---|
| `Id` | `Guid` PK | |
| `UserId` | `string` MaxLength 128 | Auth0 `sub` |
| `OrgId` | `Guid` FK → Org | Tenant boundary |
| `Platform` | `string` MaxLength 16 | `ios` \| `android` |
| `PushToken` | `string` MaxLength 512 | Expo push token |
| `DeviceId` | `string` MaxLength 128 | Client-stable id (expo installation id) |
| `CreatedAt` | `DateTime` | UTC |
| `UpdatedAt` | `DateTime` | UTC |

**Indexes:**
- Unique `(UserId, DeviceId)`
- Index `(UserId)` for push fan-out

---

## API Contract

| Status | Method | Path | Auth | Request | Response |
|---|---|---|---|---|---|
| **New** | POST | `/api/devices` | `[Authorize]` | `RegisterDeviceRequest` | 201 `DeviceRegistrationDto` / 400 / 401 |
| **New** | DELETE | `/api/devices/{deviceId}` | `[Authorize]` | — (path = client `deviceId`) | 204 / 401 / 404 |
| Existing | GET | `/api/bookings/calendar` | `[Authorize]` | `propertyId`, `startDate`, `endDate`, `timezone?` | 200 `CalendarResponseDto` |
| Existing | GET | `/api/bookings/{id}` | `[Authorize]` | — | 200 booking detail |
| Existing | POST | `/api/service-requests` | `[Authorize]` | `CreateServiceRequestRequest` | 201 |
| Existing | POST | `/api/service-requests/{id}/mark-paid` | `[Authorize]` | — | 200 |
| Existing | POST | `/api/bookings/{id}/checkout-wizard/start` | `[Authorize]` | — | 200 |
| Existing | GET | `/api/properties` | `[Authorize]` | — | 200 property list |
| Existing | GET | `/api/users/me` | `[Authorize]` | — | 200 user context |

### POST `/api/devices`

**Auth**: `[Authorize]` — caller `sub` must match stored `UserId`; `OrgId` from JWT org context.

**Request `RegisterDeviceRequest`:**

| Field | Type | Required |
|---|---|---|
| `platform` | `string` | yes — `ios` or `android` |
| `pushToken` | `string` | yes — max 512 |
| `deviceId` | `string` | yes — max 128, client-stable |

**Response `201` — `DeviceRegistrationDto`:** `{ id, platform, deviceId, updatedAt }`

Upsert semantics: if `(UserId, DeviceId)` exists, update `PushToken` + `UpdatedAt`.

### Push payload contract (Expo)

All pushes include:

```json
{
  "title": "...",
  "body": "...",
  "data": {
    "type": "guest-checkin-incomplete|service-request-update|checkout-reminder",
    "bookingId": "<guid>",
    "route": "/bookings/<guid>"
  }
}
```

---

## Frontend Flow

> Mobile app (`casazen/mobile`) — Expo Router file-based routes. Auth gate via session check (redirect to `/login` if no token). Equivalent to web `<ProtectedRoute>`.

### Route map

| Route | Screen | Auth | Issue AC |
|---|---|---|---|
| `/login` | `LoginScreen` | public | AC2 |
| `/(tabs)/calendar` | `CalendarScreen` | session required | AC4 |
| `/bookings/[id]` | `BookingDetailScreen` | session required | AC5 |
| `/bookings/[id]/service-request` | `CreateServiceRequestScreen` | session required | AC6 |
| `/bookings/[id]/checkout` | `QuickCheckoutScreen` | session required | AC8 |
| `/(tabs)/properties` | `PropertyListScreen` | session required | AC9 |

### Component breakdown

| Component | Location | Responsibility |
|---|---|---|
| `AuthProvider` | `src/auth/AuthProvider.tsx` | Auth0 PKCE, secure-store tokens |
| `ApiClient` | `src/api/client.ts` | Axios + JWT interceptor, 401→login, 5xx IT message |
| `useCalendar` | `src/hooks/use-calendar.ts` | React Query, staleTime 30s, refetchOnFocus |
| `useBooking` | `src/hooks/use-booking.ts` | Detail + service requests |
| `OfflineBanner` | `src/components/OfflineBanner.tsx` | Cached timestamp banner AC14 |
| `PushNotificationHandler` | `src/notifications/PushHandler.tsx` | Register token, deep link on tap AC12 |
| `DeepLinkWebButton` | `src/components/DeepLinkWebButton.tsx` | Opens web for AC18 flows |

### Deep links to web (AC18)

| Action | Web URL |
|---|---|
| Property activation | `{WEB_URL}/app/short-rent/properties/{id}/activation` |
| iCal settings | `{WEB_URL}/app/short-rent/properties/{id}/settings/ical` |
| Custom domain | `{WEB_URL}/app/short-rent/settings/domain` |
| Connect KYC / billing | `{WEB_URL}/app/short-rent/settings/billing` |

---

## Security Notes

| Surface | Requirement |
|---|---|
| `POST /api/devices` | `[Authorize]` — user can only register devices for own `sub`; `OrgId` from resolved context |
| `DELETE /api/devices/{deviceId}` | `[Authorize]` — IDOR: filter by `UserId` + `DeviceId` |
| Push tokens | Stored server-side only; never logged; removed on `DeviceNotRegistered` from Expo |
| Mobile API client | Same JWT audience/tenant as web; tokens in `expo-secure-store` only |
| Existing booking/service APIs | Unchanged auth — property org boundary |

**Threat summary:** Push token hijack mitigated by Auth0 on registration; no cross-user device upsert; Expo server key in env only (`Expo:AccessToken` optional for enhanced API).

---

## Migration Plan

| Migration | Entity |
|---|---|
| `AddDeviceRegistrations` | `DeviceRegistration` table + indexes |

Register in `AppDbContext`, configure unique index `(UserId, DeviceId)`.

---

## GDPR Scope

**N/A** — Device registration stores platform + opaque push token + Auth0 user id (account data, not Guest PII). Booking screens display existing host-authorized guest fields; no new Guest data collection in mobile beyond web parity.

---

## Backend service design

### `IPushNotificationService`

```csharp
Task SendToUserAsync(string userId, PushNotificationPayload payload, CancellationToken ct = default);
Task SendGuestCheckInIncompleteAsync(Guid bookingId, CancellationToken ct = default);
Task SendServiceRequestUpdateAsync(Guid serviceRequestId, string statusLabel, CancellationToken ct = default);
Task SendCheckoutReminderAsync(Guid bookingId, CancellationToken ct = default);
```

Implementation calls `https://exp.host/--/api/v2/push/send`. On `DeviceNotRegistered` error, delete stale registration.

### Integration points

| Caller | Event |
|---|---|
| `GuestCheckInReminderJob` | After email, push host users for org |
| `CheckoutReminderJob` / `NotificationService` | Push checkout reminder |
| `ServiceRequestService.TakeAsync` / `CompleteAsync` | Push host on status change |

Host user resolution: users with `PropertyOwner`/`PropertyManager` membership on booking's org via `UserContextMembership`.

---

## Mobile E2E (Maestro)

| Flow | File | AC |
|---|---|---|
| M1 Calendar | `mobile/e2e/m1-calendar.yaml` | AC4 |
| M2 Booking detail | `mobile/e2e/m2-booking-detail.yaml` | AC5 |
| M3 Push tap | `mobile/e2e/m3-push.yaml` | AC12 |
| M4 Create service request | `mobile/e2e/m4-create-service-request.yaml` | AC6 |
| M5 Service status | `mobile/e2e/m5-service-status.yaml` | AC5 |
| M6 Mark paid | `mobile/e2e/m6-mark-paid.yaml` | AC7 |
| M7 Checkout | `mobile/e2e/m7-checkout.yaml` | AC8 |

---

## Open Questions

(none — all resolved)
