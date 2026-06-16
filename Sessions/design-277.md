# Design Spec — Issue #277: Onboarding Loop Fix

**Issue**: #277 | **Type**: Bug + Feature | **Priority**: High | **Status**: Design  
**Author**: Stage 02 Design Coordinator  
**Date**: 2026-06-16

---

## Problem Statement

Users who complete onboarding on first login are **redirected back to the onboarding page on every subsequent page refresh or login**, even though they have already completed the flow and received an organization ID and role assignment.

### Root Cause

The `OnboardingGuard` (frontend `src/lib/onboarding.ts`) relies on implicit inference: checking if `orgId` is present and roles are assigned to determine whether a user has completed onboarding. This approach is **fragile** because:

1. **No persistent completion flag**: Backend sets `orgId` and assigns roles, but there is no explicit timestamp marking onboarding as complete.
2. **Race conditions**: Profile fetch may be slow, causing `orgId` to be `null` momentarily and triggering a false positive redirect.
3. **JWT/role sync delays**: Role assignment in Auth0 may not immediately reflect in cached profile data.
4. **Fragile inference logic**: Determining "user needs onboarding" by checking `orgId` and roles is implicit and doesn't capture true intent.

---

## Solution Overview

Add a persistent **`OnboardingCompletedAt`** timestamp to the `User` entity. This timestamp becomes the **single source of truth** for whether a user has completed onboarding:

- **Backend**: Add nullable `DateTime? OnboardingCompletedAt` column to `Users` table via EF Core migration.
- **Backend Service**: `OnboardingService.CompleteOnboardingAsync()` sets the timestamp when onboarding is finalized.
- **Backend API**: `GET /api/users/me` returns the timestamp in `UserDetailDto`.
- **Frontend**: `needsOnboarding()` checks for the timestamp first (if present → never needs onboarding); `canEditOnboarding()` checks for both timestamp AND `orgId` to allow edit mode.

---

## API Contract

### Endpoints Affected

| Endpoint | Method | Changes | Auth |
|---|---|---|---|
| `GET /api/users/me` | GET | Response now includes `onboardingCompletedAt: DateTime \| null` | [Authorize] |
| `POST /api/users/onboarding` | POST | Behavior unchanged; sets `OnboardingCompletedAt` on success | [Authorize] |
| `PUT /api/users/onboarding` | PUT | Behavior unchanged (idempotent re-onboarding); does NOT update timestamp (immutable once set) | [Authorize] |
| `GET /api/onboarding/status` | GET | Behavior unchanged; status now reflects timestamp presence | [Authorize] |

### Request / Response Schema Changes

**GET /api/users/me — Response**

```json
{
  "id": "auth0|123456",
  "email": "owner@example.com",
  "firstName": "Marco",
  "lastName": "Rossi",
  "role": "PropertyOwner",
  "rentalType": "ShortTerm",
  "isActive": true,
  "createdAt": "2026-06-10T14:30:00Z",
  "phoneNumber": "+39334567890",
  "updatedAt": "2026-06-15T09:15:00Z",
  "orgId": "550e8400-e29b-41d4-a716-446655440000",
  "org": {
    "id": "550e8400-e29b-41d4-a716-446655440000",
    "name": "Marco's Rentals",
    "slug": "marcos-rentals",
    "planTier": "Starter"
  },
  "onboardingCompletedAt": "2026-06-10T14:32:15Z"
}
```

**Field Details**:
- `onboardingCompletedAt`: ISO 8601 timestamp (UTC) when user completed onboarding. `null` if not yet completed.
- Type: `DateTime | null`
- Read-only: set by backend only; no PUT endpoint modifies it.

### DTOs Modified

**UserDetailDto**
```csharp
namespace Casazen.Web.DTOs.Users;

public class UserDetailDto : UserSummaryDto
{
    public string? PhoneNumber { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? OnboardingCompletedAt { get; set; }  // NEW

    /// <summary>The caller's organization summary (AC9), or <c>null</c> when the user has no org.</summary>
    public OrgSummaryDto? Org { get; set; }
}
```

**UserSummaryDto** — No change (timestamp not needed in summary).

---

## Frontend Flow

### Route Changes

No new routes. Existing onboarding flow:
- `GET /onboarding` — entry point if user needs onboarding
- `PUT /onboarding?mode=edit` — edit mode (user can re-answer rental type)

### Component Changes

**`frontend/src/lib/onboarding.ts`**

**Old logic:**
```typescript
export function needsOnboarding(profile: UserProfile | null): boolean {
  if (!profile) return true;
  // Fragile: checking orgId and roles
  return !profile.orgId || !profile.roles?.length;
}
```

**New logic:**
```typescript
/**
 * Check if user has completed onboarding based on persistent timestamp.
 * If onboardingCompletedAt exists, user never needs onboarding again (idempotent).
 * Fallback to orgId + roles for backward compat with pre-migration users.
 */
export function needsOnboarding(profile: UserProfile | null): boolean {
  if (!profile) return true;
  
  // NEW: Timestamp is the single source of truth
  if (profile.onboardingCompletedAt) {
    return false; // User has completed onboarding; never redirect again
  }
  
  // Fallback for backward compat (pre-migration users without timestamp)
  return !profile.orgId;
}

/**
 * Check if user can enter edit mode.
 * Requires both: onboardingCompletedAt (proof of completion) AND orgId (org exists).
 */
export function canEditOnboarding(profile: UserProfile | null): boolean {
  if (!profile) return false;
  return !!profile.onboardingCompletedAt && !!profile.orgId;
}
```

**`frontend/src/features/onboarding/onboarding-page.tsx`**

Add guard for edit mode:
```typescript
export function OnboardingPage() {
  const { user, profile } = useAuth(); // or useUserStore
  const location = useLocation();
  const navigate = useNavigate();
  
  // Parse query string for mode
  const params = new URLSearchParams(location.search);
  const isEditMode = params.get('mode') === 'edit';

  useEffect(() => {
    // Guard: edit mode requires onboardingCompletedAt
    if (isEditMode && !canEditOnboarding(profile)) {
      navigate('/', { replace: true });
    }
  }, [isEditMode, profile, navigate]);

  // Only show edit mode UI if user can edit
  const showEditMode = isEditMode && canEditOnboarding(profile);

  return (
    <div>
      {showEditMode ? (
        <OnboardingEditForm />
      ) : (
        <OnboardingInitialForm />
      )}
    </div>
  );
}
```

**`frontend/src/types/user.types.ts`**

Add timestamp to `UserProfile`:
```typescript
export interface UserProfile {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
  role: UserRole;
  rentalType?: RentalType | null;
  isActive: boolean;
  createdAt: string;
  phoneNumber?: string | null;
  updatedAt: string;
  orgId?: string | null;
  org?: OrgSummary | null;
  onboardingCompletedAt?: string | null; // NEW
}
```

### OnboardingGuard Refactor

**File**: `frontend/src/components/auth/onboarding-guard.tsx` (or wherever the guard lives)

The guard should now use `needsOnboarding(profile)` which checks the timestamp first:

```typescript
export function OnboardingGuard({ children }: { children: React.ReactNode }) {
  const { profile, isLoading } = useAuth(); // or useMe() query hook
  const navigate = useNavigate();

  useEffect(() => {
    if (isLoading) return; // Wait for profile to load

    if (needsOnboarding(profile)) {
      navigate('/onboarding', { replace: true });
    }
  }, [profile, isLoading, navigate]);

  if (isLoading) return <LoadingScreen />;
  if (needsOnboarding(profile)) return null; // Will redirect
  
  return <>{children}</>;
}
```

---

## Security Notes

### Authentication Gates

| Endpoint | Current | Change | Justification |
|---|---|---|---|
| `GET /api/users/me` | `[Authorize]` | No change | Caller can only read their own profile |
| `POST /api/users/onboarding` | `[Authorize]` | No change | Caller must be authenticated |
| `PUT /api/users/onboarding` | `[Authorize]` | No change | Idempotent re-onboarding |
| `PUT /api/users/me` | `[Authorize]` | No change | Caller can only update their own profile |

### Data Protection

- **Timestamp is immutable**: Once `OnboardingCompletedAt` is set, it cannot be modified via any PUT endpoint. Backend business logic enforces this in `OnboardingService.CompleteOnboardingAsync()`.
- **No PII exposure**: `OnboardingCompletedAt` is a timestamp, not PII. GDPR erasure rules do not apply.
- **No leakage via error responses**: The timestamp is only returned in successful `200 OK` responses to authenticated users.

### IDOR Check

The existing `ToDetail()` mapper already ensures that callers can only access their own profile (via JWT `sub` claim matching in `UsersController.GetMe()`). No new IDOR risks.

---

## Migration Plan

### EF Core Migration

**File to create**: `Casazen.Infrastructure/Migrations/{timestamp}_AddOnboardingCompletedAtToUsers.cs`

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable enable

namespace Casazen.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingCompletedAtToUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OnboardingCompletedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true,
                comment: "UTC timestamp when user completed onboarding. Used as source of truth for needsOnboarding() check.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OnboardingCompletedAt",
                table: "Users");
        }
    }
}
```

**Migration metadata**:
- Column type: `timestamp with time zone` (PostgreSQL) / `datetimeoffset` (SQL Server) — EF Core auto-translates
- Nullable: `true` — existing users have no timestamp; new users get one when they complete onboarding
- Default: `null` — no default value; explicitly set by service logic
- Index: not required (no queries filter by timestamp)

### Deployment Order

1. **Deploy migration** to test schema (`casazen_test`): `dotnet ef database update --project Casazen.Infrastructure`
2. **Verify**: Query the `Users` table; new column exists and is nullable
3. **Deploy backend changes** (DTOs, service, controller)
4. **Deploy frontend changes** (guard refactor, new functions)
5. **E2E test**: User completes onboarding → refreshes page → no redirect (AC1)

---

## GDPR Scope

**Impact**: None.

- `OnboardingCompletedAt` is a timestamp on the `User` entity, not on the `Guest` entity.
- It is not PII (it's a system timestamp, not personal data).
- No data erasure rules apply.
- If a user requests erasure (GDPR Art. 17), their entire `User` record is deleted, including this timestamp (handled by existing `GdprService`).

---

## Acceptance Criteria — Mapped to Implementation

| AC | Implementation | Test |
|---|---|---|
| **AC1**: User completes onboarding → refreshes page → stays on home | Add `onboardingCompletedAt` check to `needsOnboarding()` | E2E: complete onboarding, refresh, verify no redirect to `/onboarding` |
| **AC2**: User logs out → logs back in → goes to home (not onboarding) | JWT token refresh; `GET /api/users/me` returns timestamp | E2E: logout, login, verify redirect to home |
| **AC3**: User cannot re-trigger onboarding via manual URL nav | `OnboardingGuard` checks `needsOnboarding()` which checks timestamp | E2E: navigate to `/onboarding` directly; verify redirect to home |
| **AC4**: Edit mode only available with timestamp + orgId | `canEditOnboarding()` checks both conditions | E2E: try `?mode=edit` without completion → redirect; with completion → allow |
| **AC5**: All backend + frontend tests pass | Unit + integration + E2E coverage | Run `dotnet test` + `npm test` + `npm run test:e2e` |

---

## Backend Implementation Checklist

- [ ] Add `DateTime? OnboardingCompletedAt { get; set; }` to `Casazen.Core/Entities/User.cs`
- [ ] Create EF migration `AddOnboardingCompletedAtToUsers` — `dotnet ef migrations add`
- [ ] Update `Casazen.Web/DTOs/Users/UserDetailDto.cs` — add `OnboardingCompletedAt` property
- [ ] Update `ToDetail()` mapper in `UsersController.cs` to include timestamp:
  ```csharp
  onboardingCompletedAt: u.OnboardingCompletedAt,
  ```
- [ ] Verify `CompleteOnboardingAsync()` in `OnboardingService` sets timestamp (or update if missing):
  ```csharp
  user.OnboardingCompletedAt = DateTime.UtcNow;
  await db.SaveChangesAsync();
  ```
- [ ] **Unit test**: `UserService_CompleteOnboarding_SetsTimestamp()` — verify timestamp is set
- [ ] **Integration test**: `GET /api/users/me` returns non-null `onboardingCompletedAt` after POST onboarding
- [ ] Run `dotnet test` — all tests pass
- [ ] Run `dotnet format --verify-no-changes` — no formatting issues

---

## Frontend Implementation Checklist

- [ ] Update `frontend/src/types/user.types.ts` — add `onboardingCompletedAt?: string | null` to `UserProfile`
- [ ] Refactor `frontend/src/lib/onboarding.ts`:
  - [ ] Update `needsOnboarding()` to check timestamp first
  - [ ] Add `canEditOnboarding()` function
- [ ] Update `OnboardingPage` component to guard edit mode
- [ ] **Unit test**: `needsOnboarding()_with_timestamp` — verify returns `false`
- [ ] **Unit test**: `needsOnboarding()_without_timestamp` — verify returns `true` (backward compat)
- [ ] **Unit test**: `canEditOnboarding()_only_with_both_flags` — verify edit requires both
- [ ] **E2E tests**:
  - [ ] Regression: complete onboarding → refresh → no redirect (AC1)
  - [ ] Returning user: logout → login → home (AC2)
  - [ ] Edit mode guard: cannot access without timestamp (AC4)
- [ ] Run `npm test` — unit tests pass
- [ ] Run `npm run test:e2e` — E2E tests pass

---

## Open Questions

None. All acceptance criteria are clear and implementable.

---

## Related Issues / Context

- **Issue #271**: Self-serve onboarding PLG feature — this fix resolves the regression from that feature.
- **Related PRs**: None yet (design phase).

---

## Sign-Off

**Stage 02 gates — all PASS ✅**

| Gate | Status | Notes |
|---|---|---|
| G1: Design spec file exists | ✅ PASS | `Sessions/design-277.md` created |
| G2: API contract complete | ✅ PASS | `GET /api/users/me` includes `onboardingCompletedAt`; POST onboarding sets it |
| G3: Auth gates explicit | ✅ PASS | All endpoints remain `[Authorize]`; no new auth risks |
| G4: Frontend flow documented | ✅ PASS | Routes, components, `needsOnboarding()`, `canEditOnboarding()` specified |
| G5: ProtectedRoute coverage | ✅ PASS | `/onboarding` covered by existing guard; edit mode guard added |
| G6: Security notes | ✅ PASS | Timestamp immutable, no PII, no IDOR risk |
| G7: Migration plan complete | ✅ PASS | EF migration defined; deployment order specified |
| G8: GDPR scope documented | ✅ PASS | Not applicable (User, not Guest); no erasure rules |

---

**Ready for Stage 03: Development**

