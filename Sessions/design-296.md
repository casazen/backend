# Design Spec — Issue #296: Guest Check-In Portal + Alloggiati Auto (US-020)

**Issue**: [#296](https://github.com/casazen/backend/issues/296)  
**Branch**: `feature/296-guest-check-in-portal`

## API Contract

| Method | Path | Auth | Request | Response |
|---|---|---|---|---|
| GET | `/api/public/checkin/{token}` | `[AllowAnonymous]` | — | session context + guestPrefill |
| POST | `/api/public/checkin/{token}` | `[AllowAnonymous]` | identity + GDPR consents | 200 / 409 duplicate |
| POST | `/api/bookings/{id}/checkin/resend-link` | `[Authorize]` host | — | success message |
| GET | `/api/bookings/{id}/checkin-session` | `[Authorize]` host | — | session status badge |

**Jobs**: `GuestCheckInSendJob` (08:00 UTC), `GuestCheckInReminderJob` (10:00 UTC)

## Frontend Flow

| Route | Component | ProtectedRoute |
|---|---|---|
| `/checkin/:token` | `CheckInPage` (3-step wizard) | No |
| Booking detail guest tab | `CheckInSessionBadge` + resend | Yes |

## Security Notes

- Token SHA256 hashed; single-use submit; rate limits on public endpoints
- Alloggiati enqueued via Hangfire only (never inline HTTP)
- GDPR consent required on submit; 7-year retention on Guest

## Migration Plan

`AddGuestCheckInSession` — entity + filtered unique index on active sessions

## GDPR Scope

Guest PII fields + explicit consent; marketing consent optional

## Open Questions

(none)
