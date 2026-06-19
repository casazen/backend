# Design — #285 Admin onboarding org skip

## API Contract

N/A — `POST /api/users/onboarding` already calls `EnsureOrgForUserAsync`.

## Frontend Flow

| Module | Change |
|---|---|
| `lib/onboarding.ts` | `needsOrgSetup` checked **before** `onboardingCompletedAt` short-circuit |
| `onboarding-page.tsx` | Existing `isOrgBackfill` path handles admin without org |

## Security Notes

Admin without org must complete onboarding to get tenant — same as property owner.

## Migration Plan

N/A

## GDPR Scope

N/A
