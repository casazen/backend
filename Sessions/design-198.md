# Design — Issue #198 Role-Based Onboarding

## Summary

First-time Auth0 users with zero JWT roles are guided through `/onboarding` to choose short-term, long-term, or both operator types. Backend persists `User.RentalType` and syncs Auth0 roles via `Auth0ManagementService.AssignOnboardingRolesAsync`.

## API

| Method | Path | Auth | Body | Response |
|---|---|---|---|---|
| POST | `/api/users/onboarding` | JWT | `{ rentalType }` | `{ rolesAssigned, rentalType }` |
| PUT | `/api/users/onboarding` | JWT | `{ rentalType }` | same (idempotent update) |

## Frontend routing

```
ProtectedRoute
├── /onboarding (standalone, outside guard)
└── OnboardingGuard
    └── WorkspaceProvider + app routes
```

## Mapping

| rentalType | Auth0 roles | Default route |
|---|---|---|
| ShortTerm | PropertyOwner | `/app/short-rent` |
| LongTerm | LongTermLandlord | `/app/long-rent/leases` |
| Both | both | `/app/short-rent` (+ switcher) |

## Migration

`AddUserRentalType` — nullable `RentalType` column on `Users`.
