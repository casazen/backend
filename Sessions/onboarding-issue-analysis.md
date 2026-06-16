# Onboarding Loop Bug — Analysis & User Flow Fix

**Issue**: Utente accede al portale e **ogni volta** viene reindirizzato all'onboarding, anche dopo averlo completato una volta.

**Root Cause**: La logica di guardia (`OnboardingGuard`) rievaluta `needsOnboarding()` ad ogni render, ma manca il **flagging persistente** che marca l'onboarding come completato.

---

## Current Flow (Broken)

```
User sign up (Auth0)
    ↓
First login → OnboardingPage (rental type + plan selection)
    ↓
Complete onboarding → calls `/api/users/complete-onboarding`
    ↓
Backend: saves orgId + assigns role
    ↓
Frontend: refreshes token, navigates to home
    ↓
[PROBLEM] User refreshes page OR logs out/in again
    ↓
OnboardingGuard.needsOnboarding() checks:
  - profile.orgId? ✅ (exists now)
  - user roles? ❌ (JWT refreshed but roles might not sync yet, OR profile fetch returns stale data)
    ↓
Redirect to /onboarding (UNWANTED)
```

---

## Issues to Fix

### 1. **Missing persistent onboarding flag**
- Backend completes onboarding but frontend relies only on `orgId + roles` to detect completion
- If role assignment is delayed or JWT cache stale → guard fails

### 2. **Profile data fetch race condition**
- `OnboardingGuard` calls `useMe()` to fetch profile
- If fetch is slow or profile returns cached orgId=null → guard fires before real data arrives
- Then data arrives → guard re-evaluates but timing is fragile

### 3. **No explicit "onboarding completed" marker**
- Should have a flag in User profile (e.g. `onboardingCompletedAt: DateTime?`)
- This becomes the **source of truth** for the guard

### 4. **Edit mode allows re-entering onboarding**
- `/onboarding?mode=edit` is allowed for existing users who want to change rental type
- Guard logic has special handling (`isEditMode`) but it's fragile

---

## Proposed Solution

### Backend Changes (Casazen.Infrastructure)

1. **Add `OnboardingCompletedAt` field to User entity**
   ```csharp
   public class User {
       // existing...
       public DateTime? OnboardingCompletedAt { get; set; }
   }
   ```

2. **Mark timestamp when onboarding is completed**
   ```csharp
   // In OnboardingService.CompleteOnboardingAsync()
   user.OnboardingCompletedAt = DateTime.UtcNow;
   await userRepository.UpdateAsync(user);
   ```

3. **Return onboarding status in UserDto**
   ```csharp
   public class UserDto {
       public string Id { get; set; }
       public string Email { get; set; }
       public string? OrgId { get; set; }
       public DateTime? OnboardingCompletedAt { get; set; }
       public RentalType? RentalType { get; set; }
       // ...
   }
   ```

### Frontend Changes

1. **Update `needsOnboarding()` to check the timestamp**
   ```typescript
   export function needsOnboarding(
     user: UserWithRoles,
     profile?: { orgId?: string | null; onboardingCompletedAt?: string | null } | null,
   ): boolean {
     // Only needs onboarding if NEVER completed before
     if (profile?.onboardingCompletedAt) {
       return false;  // Already completed, don't show onboarding again
     }
     
     // First time: must set up org
     if (!profile?.orgId) {
       return true;
     }
     
     // Must have at least one role (except admin)
     if (isAdmin(user)) {
       return false;
     }
     
     return getUserRoles(user).length === 0;
   }
   ```

2. **Separate concerns: `canEditOnboarding()` for mode=edit**
   ```typescript
   export function canEditOnboarding(
     user: UserWithRoles,
     profile?: { orgId?: string | null; onboardingCompletedAt?: string | null } | null,
   ): boolean {
     // Only allow edit if onboarding WAS completed and org exists
     return !!(profile?.onboardingCompletedAt && profile?.orgId);
   }
   ```

3. **Update `OnboardingPage` to handle edit mode separately**
   ```typescript
   // If mode=edit but !canEditOnboarding() → redirect to home
   if (isEditMode && !canEditOnboarding(user, profile)) {
     navigate(getHomeRouteForUser(user), { replace: true });
   }
   ```

---

## User Flow — Corrected

### Scenario A: First Sign Up (New User)

```
Auth0 signup
    ↓
JWT issued (no roles yet)
    ↓
OnboardingGuard.needsOnboarding():
  - profile.onboardingCompletedAt? ❌ (null)
  - profile.orgId? ❌ (null)
  → TRUE → navigate to /onboarding
    ↓
OnboardingPage: step 1 (rental type) → step 2 (plan)
    ↓
Click "Completa registrazione" 
    ↓
POST /api/users/complete-onboarding
    ↓
Backend:
  - Creates org (orgId)
  - Assigns role (PropertyOwner)
  - Sets user.OnboardingCompletedAt = UtcNow
    ↓
Frontend: refreshAccessToken()
    ↓
Navigate to /app/short-rent (home)
    ↓
[CORRECT] User refreshes page → OnboardingGuard checks:
  - onboardingCompletedAt? ✅ (timestamp exists)
  → FALSE → no redirect → show home page
```

### Scenario B: Returning User (Already Onboarded)

```
Log in (Auth0)
    ↓
JWT issued (with PropertyOwner role)
    ↓
OnboardingGuard.needsOnboarding():
  - profile.onboardingCompletedAt? ✅ (timestamp exists from before)
  → FALSE → no redirect → render home page
    ↓
[CORRECT] User is never bothered with onboarding again
```

### Scenario C: Edit Mode (Change Rental Type)

```
User navigates to /onboarding?mode=edit
    ↓
OnboardingPage.isEditMode = true
    ↓
Check: isEditMode && !canEditOnboarding(user, profile)?
  - canEditOnboarding(): onboardingCompletedAt ✅ && orgId ✅ → TRUE
  → no redirect, allow edit
    ↓
[CORRECT] User can modify rental type
```

---

## EF Core Migration

```csharp
// Casazen.Infrastructure/Migrations/AddOnboardingCompletedAt.cs
public override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddColumn<DateTime>(
        name: "OnboardingCompletedAt",
        table: "Users",
        type: "timestamp with time zone",
        nullable: true);
}

public override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DropColumn(
        name: "OnboardingCompletedAt",
        table: "Users");
}
```

---

## Implementation Checklist

- [ ] **Backend Stage 03**
  - [ ] Add EF migration `AddOnboardingCompletedAt`
  - [ ] Update User entity with `OnboardingCompletedAt` field
  - [ ] Update `CompleteOnboardingAsync()` to set timestamp
  - [ ] Update `UserDto` to include `onboardingCompletedAt`
  - [ ] Unit test: `UserService_CompleteOnboarding_SetsTimestamp`
  - [ ] Integration test: verify timestamp is persisted and returned

- [ ] **Frontend Stage 03**
  - [ ] Update `needsOnboarding()` logic
  - [ ] Add `canEditOnboarding()` function
  - [ ] Update `OnboardingPage` edit mode guard
  - [ ] Update type definitions (UserProfile includes `onboardingCompletedAt`)
  - [ ] Unit test: `needsOnboarding()` with timestamp
  - [ ] E2E test: "User completes onboarding, refreshes page, no redirect"

- [ ] **E2E Test (Regression)**
  - [ ] User A: sign up → onboarding (rental type step 1, plan step 2) → home
  - [ ] User A: refresh page → should stay on home (not redirect to onboarding)
  - [ ] User A: log out → log in → should go to home directly
  - [ ] User A: navigate to `/onboarding?mode=edit` → should see edit page
  - [ ] User A: change rental type → save → redirect to new home (long-rent vs short-rent)

---

## Files Changed Summary

| File | Change | Type |
|---|---|---|
| `Casazen.Core/Entities/User.cs` | Add `OnboardingCompletedAt: DateTime?` | Backend |
| `Casazen.Infrastructure/Services/OnboardingService.cs` | Set timestamp on complete | Backend |
| `Casazen.Web/DTOs/Users/UserDto.cs` | Add `OnboardingCompletedAt` | Backend |
| `Migrations/Add*.cs` | New migration | Backend |
| `frontend/src/lib/onboarding.ts` | Refactor `needsOnboarding()`, add `canEditOnboarding()` | Frontend |
| `frontend/src/features/onboarding/onboarding-page.tsx` | Add edit mode guard | Frontend |
| `frontend/src/types/user.types.ts` | Add `onboardingCompletedAt` to UserProfile | Frontend |
| Tests | Integration tests (BE), E2E tests (FE) | Both |

---

## Acceptance Criteria (Issue)

- [ ] AC1: User completes onboarding → refreshes page → stays on home (no redirect)
- [ ] AC2: User logs out after onboarding → logs back in → goes directly to home
- [ ] AC3: User who completed onboarding **cannot** accidentally trigger onboarding again by URL navigation
- [ ] AC4: Edit mode (`?mode=edit`) is only available to users who completed onboarding
- [ ] AC5: All integration and E2E tests pass

---

**Status**: Ready for Stage 01 Planning (GitHub Issue creation)
