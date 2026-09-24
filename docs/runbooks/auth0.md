# Runbook: Auth0 (tenants, Management API, Action, mobile client)

Task FD-14 (audit defects A1-02, A4-01, A1-29, A4-30, A9-31); section 7 (mobile Native application):
task MO-01 (A6-01, A6-21, A6-31). The code is in place; the product owner applies the Auth0, Railway,
Vercel and EAS steps below, once per tenant.

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

What the user sees (web, task PL-01): with `rolesSynced: false` the onboarding page does not redirect silently but
explains that the workspace is ready and the new permissions reach the sign-in at the next login or with a new
attempt. It offers **Retry** (repeats the idempotent `PUT /api/users/onboarding`) and **Renew sign-in and continue**
(fresh access token, or an interactive login when a silent renewal is refused). The backend already authorizes the
user through the DB memberships, so either choice leads into the app.

Users who have Auth0 roles but no org (roles assigned by hand, org never provisioned) are no longer stuck: platform
admins and supplier-only users skip the host onboarding, hosts complete it and `PUT`/`POST /api/users/onboarding`
create or link the org. The first org is created only together with the legal consents: a `PUT` without them from a
user without org answers **422 `consents_required`** and the web app shows the consents step.

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

Task MO-01 (audit A6-01, A6-21, A6-31). The mobile app never uses the web SPA client: its redirect URI is
not a web origin, and the SPA client answered **Callback URL mismatch** to every native login.

Create one Native application **per tenant** used by a mobile build: test tenant (EAS profile `preview`),
production tenant (profile `production`) and, if developers log in from local builds, the dev tenant set in
`mobile/.env` (`EXPO_PUBLIC_AUTH0_DOMAIN`).

### 7.1 Create the application

Applications → Applications → **Create Application** → type **Native** → name `CasaZen Host (mobile)`.

### 7.2 Settings tab

| Field | Value |
|---|---|
| Allowed Callback URLs | the two redirect URIs below (comma separated) |
| Allowed Logout URLs | the same two URLs |
| Allowed Web Origins, Allowed Origins (CORS) | empty (not a browser app) |

The app builds its redirect URI from the app scheme and the bundle id / package of `mobile/app.json`
(`src/auth/auth-config.ts`), with the Auth0 domain of the build (`EXPO_PUBLIC_AUTH0_DOMAIN`, in lower case):

```text
casazen://<EXPO_PUBLIC_AUTH0_DOMAIN>/ios/it.casazen.host/callback
casazen://<EXPO_PUBLIC_AUTH0_DOMAIN>/android/it.casazen.host/callback
```

Test tenant example (`EXPO_PUBLIC_AUTH0_DOMAIN=casazen-test.eu.auth0.com`):

```text
casazen://casazen-test.eu.auth0.com/ios/it.casazen.host/callback, casazen://casazen-test.eu.auth0.com/android/it.casazen.host/callback
```

Rules:

- `<EXPO_PUBLIC_AUTH0_DOMAIN>` is exactly the value set for that EAS profile. If the app uses a custom
  domain (for example `login.casazen.app`), the callback URLs contain the custom domain, not the canonical
  `*.auth0.com` one. Changing the domain, the scheme (`expo.scheme`) or the bundle id / package
  (`expo.ios.bundleIdentifier`, `expo.android.package`) changes the redirect URI: update this list first.
- No wildcard, no `casazen://` alone (the old value): Auth0 compares the full URL.
- Development builds (Metro) print the URI in the log: `[auth] Auth0 redirect URI: casazen://...`.
- Logout URLs: the app does not log out at Auth0 yet (task MO-05); registering the same two URLs now lets
  MO-05 use them as `returnTo` without another change in the tenant.

### 7.3 Credentials and grant types

- Credentials tab → **Authentication Method: None**. The app is a public client (authorization code +
  PKCE); there is no client secret, and nothing secret is in the app bundle.
- Settings → Advanced Settings → **Grant Types**: tick only **Authorization Code** and **Refresh Token**.
  Untick Implicit, Password, Client Credentials, Device Code and the others.
- Settings → Advanced Settings → Device Settings (iOS Team ID, Android package + SHA-256 fingerprints):
  not needed. They are only for `https` callbacks (Universal Links / App Links), and the app uses its
  custom scheme.

### 7.4 Refresh tokens

The app requests the scopes `openid profile email offline_access` and receives a refresh token (it is
stored in SecureStore; the refresh flow itself is task MO-05).

- API from section 2 → Settings → **Allow Offline Access**: on (without it Auth0 issues no refresh token).
- Application → Settings → **Refresh Token Rotation**: **Allow Refresh Token Rotation** on. Each refresh
  returns a new refresh token and invalidates the previous one; a reused token revokes the whole family.
- **Refresh Token Expiration**: set both the absolute (maximum) lifetime and the inactivity (idle) lifetime;
  do not leave "never expire". The values are a product decision (how long a host stays logged in on the
  phone), still open.

### 7.5 Connections and access to the API

- Connections tab: enable the **same** connections as the web SPA application (database and social), so a
  host logs in with the same account on web and mobile.
- The Post-login Action of section 6 runs for every application of the tenant: mobile access tokens carry
  the same `https://casazen.app/roles` and profile claims as the web ones. No change needed.
- The app asks for `audience=<EXPO_PUBLIC_AUTH0_AUDIENCE>` (API of section 2). If the tenant restricts
  user access to the API per application (APIs → the API → **Application Access**, where available), give
  `CasaZen Host (mobile)` the same user access as the SPA application.

### 7.6 EAS variables

Per EAS environment (`preview` → test tenant, `production` → production tenant): `EXPO_PUBLIC_AUTH0_DOMAIN`,
`EXPO_PUBLIC_AUTH0_CLIENT_ID` (Client ID of **this Native application**, not the SPA one),
`EXPO_PUBLIC_AUTH0_AUDIENCE`. Set them in the EAS environment of each profile, not in `eas.json`:
[mobile-release.md](mobile-release.md) section 2. Local development: the same three values in `mobile/.env`
(the client id has no default).

A build without these values, or whose `app.json` lacks the scheme or the bundle id / package, does not
start: it shows the screen "Configurazione dell'app incompleta" with code `CONFIG_ENV_INVALID` or
`AUTH_CONFIG_INVALID`.

### 7.7 Check on a device (after each tenant setup)

These steps need a real device or emulator; they were not run when the code was written.

1. Install the `preview` build (or `production` before the store release) and tap **Continua con Auth0**:
   the Auth0 login page of the right tenant opens (check the domain in the browser).
2. Log in with a test user (test tenant only): the app returns to the calendar and loads data from the
   matching backend (API answers 200, not 401). A user who has not completed the web onboarding with the legal
   consents sees "Completa l'attivazione sul sito" instead (PL-02, [`onboarding-consents.md`](onboarding-consents.md)).
3. On a fresh install (or after clearing the app data: the app has no logout button yet), tap
   **Continua con Auth0** and close the browser without logging in: the app stays on the login screen, no
   error.
4. Auth0 → Monitoring → Logs, filtered on `CasaZen Host (mobile)`: `Success Login`, then
   `Success Exchange` (*Authorization Code for Access Token*). Decode the access token (jwt.io, test tenant
   only): `aud` contains the API identifier, `azp` is the Native client id, `scope` contains `offline_access`.
5. Repeat on iOS and Android: each platform uses its own callback URL.

| Symptom | Cause | Action |
|---|---|---|
| Auth0 page "Callback URL mismatch" | The redirect URI of this build is not in Allowed Callback URLs (wrong domain, custom domain, old `casazen://` value) or the build still uses the SPA client id | Compare with section 7.2 and with the `[auth] Auth0 redirect URI` log; check `EXPO_PUBLIC_AUTH0_CLIENT_ID` |
| Login screen shows `AUTH0_TOKEN_EXCHANGE_FAILED: unauthorized_client` | Grant type Authorization Code off, or Authentication Method not None | Section 7.3 |
| `AUTH0_TOKEN_EXCHANGE_FAILED: invalid_grant` | Code already used or PKCE verifier mismatch (for example the app restarted during the login) | Retry the login; if it persists, check the Auth0 logs |
| `AUTH0_AUTHORIZE_FAILED: access_denied` | User refused, API access not allowed for this application, or the Action denied the login | Auth0 logs; section 7.5 |
| Page "Service not found" | `EXPO_PUBLIC_AUTH0_AUDIENCE` differs from the API identifier of this tenant | Section 2 and EAS variables |
| Login works but no refresh token in the Auth0 logs | Allow Offline Access off, or grant Refresh Token off | Sections 7.3 and 7.4 |
| `AUTH_EXPO_GO_UNSUPPORTED` on the login screen | The app runs in Expo Go, which cannot receive the `casazen://` redirect | Use `npx expo run:android` / `run:ios` or an EAS build |

### 7.8 End-to-end tests (Maestro)

The app no longer accepts injected tokens: the `casazen://e2e-auth?access_token=…` deep link and
`EXPO_PUBLIC_E2E_ACCESS_TOKEN` were removed (any web page could log a tester into another account). The
demo mode (`EXPO_PUBLIC_E2E_DEMO=1`) works only in development bundles and stores a placeholder token the
API rejects.

Maestro flows log in for real with a **test user of the test tenant** (never production): create the user
(Auth0 → User Management → Users, database connection), give it the `PropertyOwner` role (section 3), complete the
web onboarding once with it (rental type and legal consents: the role alone opens no host feature, PL-02) and
seed its host data on the test backend. Credentials are passed to Maestro at run time
(`maestro test -e E2E_AUTH0_EMAIL=... -e E2E_AUTH0_PASSWORD=...`), never committed: `mobile/README.md`,
section "Maestro E2E". The automated login flow belongs to task FN-04.

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
