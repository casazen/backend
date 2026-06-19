# Design — #283 Billing/plan without org

## API Contract

N/A — no new endpoints. `GET /api/orgs/me/entitlement` returns 404 without org context.

## Frontend Flow

| Route | Change |
|---|---|
| `/app/short-rent/settings/plan` | Redirect to `/onboarding` when `needsOrgSetup(user)` |
| `useEntitlement` | `enabled: !!org?.id`, `retry: false` |

## Security Notes

No change — plan routes remain behind auth + onboarding guard.

## Migration Plan

N/A

## GDPR Scope

N/A
