## Summary

Production E2E flow test (Chrome DevTools, user `luca.lamal@hotmail.it`) shows that **property creation is blocked** even with valid form data.

## Reproduction

1. Login at https://casazen-app.vercel.app
2. Go to **Proprietà** → **Add Property**
3. Fill all required fields with valid data
4. Click **Create Property**

**Expected**: `POST /api/properties` → 201, property appears in list

**Actual**: `POST /api/properties` → **403**
```json
{"error":"No organization context","code":"no_org_context"}
```

## Root cause

- `GET /api/users/me` returns `orgId: null`, `org: null`, `email: ""`
- User has Auth0 roles (`Admin`, `PropertyOwner`, `LongTermLandlord`) but never completed onboarding
- `CompleteOnboardingAsync` calls `EnsureOrgForUserAsync` — skipped because:
  - `needsOnboarding()` returns `false` for Admin and any user with `roles.length > 0`
  - Profile link "Modifica tipo" → `/onboarding` redirects away immediately

## Downstream impact (blocked flows)

- Cannot create property → cannot test edit, photo upload, adaptive pricing, bookings
- `PUT /api/orgs/me/plan` → 404 (no org)
- `GET /api/orgs/me/entitlement` → 404

## Suggested fix

1. **Backfill**: migration/script to create org for existing users with `OrgId IS NULL` and roles assigned
2. **FE**: Allow re-onboarding (`PUT /api/users/onboarding`) from profile even when roles exist, if `orgId` is null
3. **FE**: Map `no_org_context` to actionable Italian message + CTA to onboarding
4. **BE**: Consider auto-provisioning org on first authenticated request for PropertyOwner

## Evidence

Spec: `Sessions/specs/spec-production-e2e-flow-verification.md`
Tested: 2026-06-09 on casazen-app.vercel.app + casazen-api.up.railway.app
