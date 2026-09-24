# Runbook: Auth0 (tenants, Management API, Action, mobile client)

Task FD-14 (audit defects A1-02, A4-01, A1-29, A4-30, A9-31). The code is in place; the product owner
applies the Auth0, Railway, Vercel and EAS steps below, once per tenant.

The general developer guide (SPA app, API, local setup) stays in [`docs/AUTH0_SETUP.md`](../AUTH0_SETUP.md).

## What the backend does

| Concern | Behaviour | Code |
|---|---|---|
| Management API token | OAuth2 **client credentials** with an M2M application, cached until `expires_in − 60 s` (singleton, renewed on HTTP 401) | `Casazen.Infrastructure/Services/Auth0ManagementTokenProvider.cs` |
| Legacy static token | `Auth0:ManagementApiToken` is still read **only** when no M2M client is configured, with a `deprecated` warning in the logs. Static tokens expire (24 h by default) and cannot be renewed | same file |
| Role assignment | **Additive only** (`POST /users/{id}/roles`): other roles are never removed. Role ids are cached for 1 h | `Casazen.Infrastructure/Services/Auth0ManagementService.cs` |
| Role removal | Explicit, only the named roles (`DELETE /users/{id}/roles`): admin role change (previous role only) and onboarding (unselected `PropertyOwner` / `LongTermLandlord` only) | same file, `UserService.ChangeRoleAsync` / `CompleteOnboardingAsync` |
| Supplier role | Assigned **once**, at supplier registration (`POST /api/suppliers/register`). No Management API call on `/api/supplier/*` requests | `SuppliersController.Register`, `SupplierOrgContextResolver` |
| Outcome | Never swallowed. Onboarding: `rolesSynced` / `rolesSyncError` in the response (DB already updated). Supplier registration: same fields. Admin role change: **502** `{ code }` and no change applied | `UsersController`, `SuppliersController` |
| DB memberships | `UserContextMemberships` written for **every** onboarding role and revoked when a role is removed, so backend context authorization does not depend on the JWT | `UserContextMembershipService` |
| Per-request DB reads | User flags, supplier link and memberships read once per request and cached 60 s per user (`Authorization:UserCacheSeconds`, `0` disables), invalidated on every role/membership/link change of this instance | `UserAuthorizationSnapshotStore` |

Error codes returned to clients: `auth0_management_not_configured`, `auth0_management_token_failed`,
`auth0_role_not_found`, `auth0_rate_limited`, `auth0_management_error`.

## 1. Tenants: one for test, one for production

Use two separate Auth0 tenants (for example `casazen-test` and `casazen`, region **EU**). Every step below
is repeated in both tenants; never point the test environment at the production tenant.

| Environment | Auth0 tenant | Backend (Railway) | Web (Vercel) | Mobile (EAS) |
|---|---|---|---|---|
| test | `casazen-test.eu.auth0.com` | Railway environment `test` | Preview + `develop` | profile `preview` |
| production | `casazen.eu.auth0.com` | Railway environment `production` | Production | profile `production` |

Users, roles and the Action are **not** shared between tenants: create the roles and deploy the Action in
each one. Test users (E2E, demo) live only in the test tenant.

## 2. API (resource server)

Applications → APIs → **Create API** (per tenant):

- Identifier (audience): the value of `Auth0__Audience` / `VITE_AUTH0_AUDIENCE` / `EXPO_PUBLIC_AUTH0_AUDIENCE`
  (today `https://casazen-api`).
- Signing algorithm: RS256.
- Settings → **Allow Offline Access**: on (the mobile app requests `offline_access` for refresh tokens).

## 3. Roles

User Management → Roles → create exactly these names (they must match `Auth0ManagementService.RoleNames`):
`Admin`, `PropertyOwner`, `LongTermLandlord`, `Supplier` (plus `PropertyManager`, `Staff`, `Guest` if the
admin console must be able to assign them). A missing role makes the sync fail with `auth0_role_not_found`.

## 4. M2M application for the Management API

Applications → Applications → **Create Application** → *Machine to Machine* → name `CasaZen Backend (Management API)`.

1. Authorize it for **Auth0 Management API** with these scopes only:

   | Scope | Used for |
   |---|---|
   | `read:users` | profile backfill (email/name) in admin listings and supplier email fallback |
   | `update:users` | user updates such as blocking a deactivated user |
   | `read:roles` | resolving role ids (cached) |
   | `create:role_members` | adding a role to a user (`POST /users/{id}/roles`) |
   | `delete:role_members` | removing a named role from a user (`DELETE /users/{id}/roles`) |
   | `read:role_members` | optional, only for manual checks |

2. Copy **Client ID** and **Client Secret** into Railway (next section). Never commit them.
3. Rotate the secret from the same page when needed: update Railway, redeploy, then revoke the old one.

## 5. Railway variables (per environment)

| Variable | Value | Notes |
|---|---|---|
| `Auth0__Domain` | login domain of the tenant (or its custom domain) | JWT issuer, already set |
| `Auth0__Audience` | API identifier | already set |
| `Auth0__ClientId` | SPA client id | used by the supplier registration page |
| `Auth0__ManagementApiDomain` | canonical tenant domain, e.g. `casazen-test.eu.auth0.com` | **required when `Auth0__Domain` is a custom domain**: the Management API audience is always the canonical domain |
| `Auth0__ManagementClientId` | M2M client id | secret |
| `Auth0__ManagementClientSecret` | M2M client secret | secret |
| `Auth0__ManagementApiToken` | — | **delete it** once the two variables above are set (deprecated) |
| `Authorization__UserCacheSeconds` | `60` (default) | optional; `0` disables the cross-request cache |

Check after the deploy:

1. Complete an onboarding (or `PUT /api/users/onboarding`) with a test user: the response has `"rolesSynced": true`.
2. The logs show `Auth0 Management API: obtained client-credentials token valid for …s` once, then no new token until it expires.
3. No log line contains `deprecated static Auth0:ManagementApiToken`.
4. In Auth0 → Users → the test user → Roles: the expected roles are there and roles added by hand (for example `Supplier`) are still there.

If `rolesSynced` is `false`, `rolesSyncError` tells why: `auth0_management_not_configured` (variables missing),
`auth0_management_token_failed` (wrong client id/secret, or the M2M app is not authorized for the Management API),
`auth0_role_not_found` (role missing in this tenant), `auth0_rate_limited`, `auth0_management_error` (see logs).

## 6. Post-login Action (roles + profile claims on the access token)

Actions → Library → **Build from scratch** → name `CasaZen claims`, trigger **Login / Post Login**:

```javascript
exports.onExecutePostLogin = async (event, api) => {
  const ns = 'https://casazen.app';
  const roles = event.authorization?.roles ?? [];

  // Roles: access token (backend) and ID token (web/mobile guards).
  api.accessToken.setCustomClaim(`${ns}/roles`, roles);
  api.idToken.setCustomClaim(`${ns}/roles`, roles);

  // Profile claims on the access token: the API never sees the ID token.
  if (event.user.email) {
    api.accessToken.setCustomClaim(`${ns}/email`, event.user.email);
  }
  if (event.user.name) {
    api.accessToken.setCustomClaim(`${ns}/name`, event.user.name);
  }
  api.accessToken.setCustomClaim(`${ns}/email_verified`, event.user.email_verified === true);
};
```

Deploy it, then Actions → Flows → **Login** → drag it into the flow → **Apply** (replace the older
`Add Roles to Token` Action if present: keep only one Action that writes `https://casazen.app/roles`).

The backend code that **reads** `https://casazen.app/email`, `/name` and `/email_verified` belongs to task
PL-04; adding the claims now is harmless.

Roles are copied into tokens at login: after a role change the client must get a new token
(web: `getAccessTokenSilently({ cacheMode: 'off' })`; mobile: refresh-token grant). Backend authorization
already uses the DB memberships and does not wait for the new token.

## 7. Native application for the mobile app (Expo)

Applications → Applications → **Create Application** → *Native* → name `CasaZen Mobile`:

- **Allowed Callback URLs**: `casazen://` (the app builds its redirect with
  `AuthSession.makeRedirectUri({ scheme: 'casazen' })`, scheme from `mobile/app.json`). If a build logs a
  different redirect URI (e.g. with a path), add exactly that value.
- **Allowed Logout URLs**: `casazen://`
- Advanced → Grant Types: **Authorization Code** and **Refresh Token** (PKCE, no client secret; Token
  Endpoint Authentication Method = *None*).
- Refresh Token Rotation: **on**; absolute and inactivity lifetimes as per product policy.
- APIs: authorize the API from section 2.

EAS / Expo variables per profile: `EXPO_PUBLIC_AUTH0_DOMAIN`, `EXPO_PUBLIC_AUTH0_CLIENT_ID` (the Native app,
not the SPA), `EXPO_PUBLIC_AUTH0_AUDIENCE`.
Set them in the EAS environment of each profile (`preview` → test tenant, `production` → production
tenant), not in `eas.json`: [mobile-release.md](mobile-release.md) section 2.

## 8. Web SPA (reminder)

Vercel variables per environment: `VITE_AUTH0_DOMAIN`, `VITE_AUTH0_CLIENT_ID`, `VITE_AUTH0_AUDIENCE`,
pointing at the tenant of that environment (Preview/`develop` → test tenant, Production → production tenant).

## 9. One-off repair of roles removed by the old sync (A4-01)

Before FD-14 every visit to the supplier console **removed all other Auth0 roles** of dual-role users, and
static Management tokens made many syncs fail silently. After configuring the M2M client, repair the
existing users in each tenant (read-only query on the matching Supabase schema):

```sql
-- Users with a supplier link and/or an onboarding choice: compare with their roles in Auth0.
SELECT "Id", "Email", "Role", "RentalType", "SupplierOrgId" IS NOT NULL AS is_supplier
FROM "Users"
WHERE "SupplierOrgId" IS NOT NULL OR "RentalType" IS NOT NULL OR "Role" = 0
ORDER BY "Email";
```

Expected Auth0 roles: `RentalType` 0 → `PropertyOwner`, 1 → `LongTermLandlord`, 2 → both; `Role` 0 → `Admin`;
`is_supplier` → `Supplier`. Add the missing ones from Auth0 → Users → user → Roles (adding is safe; do not
remove roles you did not expect). The user sees them at the next login or token refresh.

## Multi-instance note

The 60 s authorization cache and the role-id cache are per process. A role change is visible immediately on
the instance that made it and within `Authorization__UserCacheSeconds` on the other instances.
