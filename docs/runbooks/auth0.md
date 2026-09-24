# Runbook: Auth0 (tenants, Management API, Action, mobile client)

Task FD-14 (audit defects A1-02, A4-01, A1-29, A4-30, A9-31); section 7 (mobile Native application):
task MO-01 (A6-01, A6-21, A6-31); sections 7.4 and 7.9 (refresh token, logout, 401 vs 403 in the app): task MO-05
(A6-14, A6-15, A1-33). The code is in place; the product owner applies the Auth0, Railway,
Vercel and EAS steps below, once per tenant.

The general developer guide (SPA app, API, local setup) stays in [`docs/AUTH0_SETUP.md`](../AUTH0_SETUP.md).

## What the backend does

| Concern | Behaviour | Code |
|---|---|---|
| Management API token | OAuth2 **client credentials** with an M2M application, cached until `expires_in − 60 s` (singleton, renewed on HTTP 401) | `Casazen.Infrastructure/Services/Auth0ManagementTokenProvider.cs` |
| Legacy static token | `Auth0:ManagementApiToken` is still read **only** when no M2M client is configured, with a `deprecated` warning in the logs. Static tokens expire (24 h by default) and cannot be renewed | same file |
| Role assignment | **Additive only** (`POST /users/{id}/roles`): other roles are never removed. Role ids are cached for 1 h | `Casazen.Infrastructure/Services/Auth0ManagementService.cs` |
| Role removal | Explicit, only the named roles (`DELETE /users/{id}/roles`): admin role change (previous role only) and onboarding (unselected `PropertyOwner` / `LongTermLandlord` only) | same file, `UserService.ChangeRoleAsync` / `CompleteOnboardingAsync` |
| Supplier role | Assigned when the account is linked to a supplier profile: signed-in registration or invite (`POST /api/suppliers/register`) and claim of an anonymous registration (`POST /api/suppliers/claim`, SU-02; each call retries it). No Management API call on `/api/supplier/*` requests | `SuppliersController.Register` / `Claim`, `SupplierOrgContextResolver` |
| Outcome | Never swallowed. Onboarding: `rolesSynced` / `rolesSyncError` in the response (DB already updated). Supplier registration and claim: same fields. Admin role change: **502** `{ code }` and no change applied | `UsersController`, `SuppliersController` |
| DB memberships | `UserContextMemberships` written for **every** onboarding role and revoked when a role is removed, so backend context authorization does not depend on the JWT | `UserContextMembershipService` |
| Per-request DB reads | User flags, supplier link and memberships read once per request and cached 60 s per user (`Authorization:UserCacheSeconds`, `0` disables), invalidated on every role/membership/link change of this instance | `UserAuthorizationSnapshotStore` |
| Deactivated users (PL-03) | Every authenticated request of a user with `Users.IsActive = false` gets **403 `account_inactive`**, before any policy, whatever the roles in the token. The flag is read with the tenant (one query per request, no cache) | `InactiveAccountMiddleware`, `TenantContext` |
| Deactivation / reactivation | `DELETE /api/users/{id}`: DB first, then Auth0 `blocked=true` and removal of the user's CasaZen roles (remembered in `Users.SuspendedAuth0Roles`). `POST /api/users/{id}/reactivate`: roles given back, then `blocked=false`. Outcome in `auth0Synced` / `auth0SyncError` / `message`; section 10 | `UserService.DeactivateUserAsync` / `ReactivateUserAsync` |

Error codes returned to clients: `auth0_management_not_configured`, `auth0_management_token_failed`,
`auth0_role_not_found`, `auth0_rate_limited`, `auth0_management_error`. Deactivation (PL-03): `account_inactive` (403),
`cannot_deactivate_self`, `last_active_admin`, `user_inactive` (422).

## 1. Tenants: one for test, one for production

Task PL-11 (audit defect A1-32). Until now the web app used **the same tenant** for Vercel Preview and Production
(`secrets/vercel.variables.example.json` had the same `VITE_AUTH0_DOMAIN` in both columns): a role given to a test
user was valid in production too, and the test users lived next to the real ones. Use two separate Auth0 tenants, in
the **EU** region, with names chosen by the product owner (this runbook writes `<test tenant>` and
`<production tenant>`: no tenant name is decided in the code). Every step of sections 2-7 is repeated in both tenants;
never point the test environment at the production tenant, nor the reverse.

| Environment | Auth0 tenant | Backend (Railway) | Web (Vercel) | Mobile (EAS) |
|---|---|---|---|---|
| test | `<test tenant>` | Railway environment `test` (`ASPNETCORE_ENVIRONMENT=Staging`) | Preview (PRs + `develop`) | profile `preview` |
| production | `<production tenant>` | Railway environment `production` | Production | profile `production` |

Users, roles and the Action are **not** shared between tenants: create the roles and deploy the Action in each one.
Test users (E2E, demo, Maestro) live only in the test tenant. The code holds no tenant: the backend reads
`Auth0__*`, the web app `VITE_AUTH0_*`, the mobile app `EXPO_PUBLIC_AUTH0_*`, the helper scripts of `scripts/`
`E2E_AUTH0_DOMAIN` / `E2E_AUTH0_CLIENT_ID` (or `Auth0__Domain` for `start-backend-local.ps1`).

### 1.1 Separating the tenants (one-time, product owner)

Decide first which of the two environments keeps the tenant used today. Keeping it for **production** keeps the real
users and their passwords where they are; the test environment then gets a new, empty tenant.

1. Auth0 Dashboard → tenant menu → **Create tenant**, region EU. Set it up with sections 2-7 (API with the same
   identifier `https://casazen-api`, roles, M2M application, Action, SPA and Native applications).
2. SPA application of the new tenant (Applications → *Single Page Application*): Allowed Callback URLs, Logout URLs and
   Web Origins with the web app URLs of **its** environment only (test: the `develop` deployment and, if used, the
   preview pattern of the Vercel project; production: the production domain). Remove the URLs of the other environment
   from the old tenant's SPA application.
3. **Railway** — environment of the new tenant (Variables tab), then redeploy:

   | Variable | New value |
   |---|---|
   | `Auth0__Domain` | login domain of the tenant (or its custom domain) |
   | `Auth0__ManagementApiDomain` | canonical `*.auth0.com` domain of the tenant (only with a custom domain) |
   | `Auth0__ManagementClientId`, `Auth0__ManagementClientSecret` | M2M application of this tenant (section 4) |
   | `Auth0__ClientId` | SPA client id of this tenant |
   | `Auth0__Audience` | unchanged (`https://casazen-api`), unless the API of the new tenant has another identifier |

4. **Vercel** — Settings → Environment Variables, one value per environment (never "All environments"), then redeploy
   `develop` and the production branch:

   | Variable | Preview | Production |
   |---|---|---|
   | `VITE_AUTH0_DOMAIN` | domain of `<test tenant>` | domain of `<production tenant>` |
   | `VITE_AUTH0_CLIENT_ID` | SPA client id of `<test tenant>` | SPA client id of `<production tenant>` |
   | `VITE_AUTH0_AUDIENCE` | API identifier (same value as Railway test `Auth0__Audience`) | API identifier (same as Railway production) |

5. **EAS** (mobile): `EXPO_PUBLIC_AUTH0_DOMAIN`, `EXPO_PUBLIC_AUTH0_CLIENT_ID` (Native application of that tenant),
   `EXPO_PUBLIC_AUTH0_AUDIENCE` per environment (section 7.6).
6. **GitHub** (frontend repo, Actions variables used by the staging E2E): `VITE_AUTH0_DOMAIN`, `VITE_AUTH0_CLIENT_ID`,
   `VITE_AUTH0_AUDIENCE` of the **test** tenant; secrets `E2E_AUTH0_EMAIL` / `E2E_AUTH0_PASSWORD` of a user of the test
   tenant. Local `.env` files of developers: the test (or a personal development) tenant, never production.
7. **Users**: test users are recreated in the test tenant (section 7.8 for the E2E user). If the existing tenant becomes
   the test one instead, real users must be moved to the new production tenant (Auth0 user import/export, with
   passwords only through Auth0's bulk import of password hashes): plan it with Auth0's documentation before switching.
   CasaZen stores users by their Auth0 `sub` (`Users.Id`), which changes with the tenant: a user moved to another
   tenant is a new CasaZen user unless the import keeps the same user id.
8. Remove from the old tenant the roles given to test users, and the test users themselves, once the test environment
   works on its own tenant.

Check: log in on the test web app → the Auth0 page shows the test tenant domain; log in on production → the production
domain. A token of the test tenant sent to the production API answers **401** (issuer and signing keys differ), and the
reverse.

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
   | `read:users` | profile backfill (email/name) in admin listings; account email and `email_verified` for supplier invites and claims when the access token lacks the Action claims (SU-02); roles of a user before its deactivation (PL-03) |
   | `update:users` | **blocking and unblocking** a deactivated / reactivated user (`PATCH /users/{id}` with `blocked`, PL-03) |
   | `read:roles` | resolving role ids (cached); roles of a user before its deactivation |
   | `create:role_members` | adding a role to a user (`POST /users/{id}/roles`); giving the roles back at the reactivation |
   | `delete:role_members` | removing a named role from a user (`DELETE /users/{id}/roles`); removing the roles at the deactivation |
   | `read:role_members` | roles of a user before its deactivation (`GET /users/{id}/roles`, PL-03, with `read:users` and `read:roles`): grant it. When a scope is missing the deactivation still applies in CasaZen and reports `auth0Synced: false` |

2. Copy **Client ID** and **Client Secret** into Railway (next section). Never commit them.
3. Rotate the secret from the same page when needed: update Railway, redeploy, then revoke the old one.

## 5. Railway variables (per environment)

| Variable | Value | Notes |
|---|---|---|
| `Auth0__Domain` | login domain of the tenant (or its custom domain) | JWT issuer, already set |
| `Auth0__Audience` | API identifier | already set |
| `Auth0__ClientId` | SPA client id | used by the supplier registration page |
| `Auth0__ManagementApiDomain` | canonical `*.auth0.com` domain of the tenant (Auth0 Dashboard → Settings → the tenant domain) | **required when `Auth0__Domain` is a custom domain**: the Management API audience is always the canonical domain |
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

The backend reads `https://casazen.app/email` and `/email_verified` for supplier invites and claims (SU-01, SU-02:
[`suppliers.md`](suppliers.md) §2): without `/email_verified` a supplier who lost the claim token of an anonymous
registration cannot link the profile unless the Management API (§4, `read:users`) is configured. Reading `/name`
belongs to task PL-04. The claims are copied at login: after verifying the email the client needs a new token
(`getAccessTokenSilently({ cacheMode: 'off' })`, which the web claim page does on *Riprova*).

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

Test tenant example (`EXPO_PUBLIC_AUTH0_DOMAIN=<test tenant>.eu.auth0.com`, the name chosen in section 1):

```text
casazen://<test tenant>.eu.auth0.com/ios/it.casazen.host/callback, casazen://<test tenant>.eu.auth0.com/android/it.casazen.host/callback
```

Rules:

- `<EXPO_PUBLIC_AUTH0_DOMAIN>` is exactly the value set for that EAS profile. If the app uses a custom
  domain (for example `login.casazen.app`), the callback URLs contain the custom domain, not the canonical
  `*.auth0.com` one. Changing the domain, the scheme (`expo.scheme`) or the bundle id / package
  (`expo.ios.bundleIdentifier`, `expo.android.package`) changes the redirect URI: update this list first.
- No wildcard, no `casazen://` alone (the old value): Auth0 compares the full URL.
- Development builds (Metro) print the URI in the log: `[auth] Auth0 redirect URI: casazen://...`.
- Logout URLs: the app logs out at Auth0 with `https://<domain>/v2/logout?client_id=<Native client id>&returnTo=<redirect
  URI of the build>` (MO-05, section 7.9). Auth0 accepts the `returnTo` only if it is in **Allowed Logout URLs** of
  this application: without the two URLs the logout page shows an Auth0 error (the app itself is already signed out,
  but the Auth0 browser session is not ended).

### 7.3 Credentials and grant types

- Credentials tab → **Authentication Method: None**. The app is a public client (authorization code +
  PKCE); there is no client secret, and nothing secret is in the app bundle.
- Settings → Advanced Settings → **Grant Types**: tick only **Authorization Code** and **Refresh Token**.
  Untick Implicit, Password, Client Credentials, Device Code and the others.
- Settings → Advanced Settings → Device Settings (iOS Team ID, Android package + SHA-256 fingerprints):
  not needed. They are only for `https` callbacks (Universal Links / App Links), and the app uses its
  custom scheme.

### 7.4 Refresh tokens

The app requests the scopes `openid profile email offline_access` and receives a refresh token, kept only in
SecureStore (Keychain / Keystore). It uses it to renew the access token (grant `refresh_token` on
`https://<domain>/oauth/token`, `client_id` of this Native application, no secret: public client, MO-05):

- when an API call answers **401**, then it sends that call again, **once**;
- shortly before the access token expires (60 s before `expires_in`), before sending the next call;
- **one refresh at a time**: calls that need a new token while a refresh is running wait for it and reuse its
  token. With rotation a refresh token used twice is treated as stolen and Auth0 revokes the whole family, so two
  parallel refreshes would sign the user out.

Tenant settings:

- API from section 2 → Settings → **Allow Offline Access**: on (without it Auth0 issues no refresh token, and the
  app asks for a new login each time the access token expires).
- Application → Settings → **Refresh Token Rotation**: **Allow Refresh Token Rotation** on. Each refresh
  returns a new refresh token and invalidates the previous one (the app stores the new one); a reused token revokes
  the whole family. **Rotation Overlap Period** (reuse interval): `0` is the strictest; a few seconds tolerate a
  refresh answer lost on a bad network (the app then retries with the previous token instead of asking for a new
  login). The app never reuses a token on purpose.
- **Refresh Token Expiration**: set both the absolute (maximum) lifetime and the inactivity (idle) lifetime;
  do not leave "never expire". The values are a product decision (how long a host stays logged in on the
  phone), still open. When the refresh token expires the app shows the login with "La sessione è scaduta".
- Access token lifetime: API from section 2 → Settings → **Maximum Access Token Lifetime** (default 86400 s). The app
  renews it silently, so the value only bounds how long an access token stays usable after a logout or a
  deactivation (the API refuses deactivated users anyway, section 10).

What the app does with the Auth0 answer of a refresh:

| Auth0 answer | Meaning | App |
|---|---|---|
| New tokens | Session renewed | Stores the new access and refresh token, sends the call again |
| `invalid_grant` (and any other OAuth error below) | Refresh token expired, revoked, reused, or user blocked (section 10) | Ends the session: cache and SecureStore cleared, login screen with "La sessione è scaduta"; after the login it reopens the screen the user was on |
| `server_error`, `temporarily_unavailable`, `too_many_requests`, network error | Auth0 not reachable | Keeps the session; the call fails like any network error and the next call tries again |

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
3. On a fresh install (or after **Profilo → Esci**), tap **Continua con Auth0** and close the browser without
   logging in: the app stays on the login screen, no error.
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

### 7.9 Session and logout in the app (MO-05)

**401 and 403 are different** (backend contract FD-05, PL-02, PL-03):

| API answer | Meaning | App |
|---|---|---|
| 401 | Access token missing, expired or invalid | Refresh (section 7.4), call sent again once; logout only if Auth0 refuses the refresh token |
| 403 `forbidden` | Valid session, action not allowed (also every `UnauthorizedAccessException` of the backend) | No logout: the error goes to the screen |
| 403 `onboarding_required` | Host activation or legal consents missing (PL-02) | No logout: "Completa l'attivazione sul sito" |
| 403 `account_inactive` | Account deactivated by an admin (PL-03, section 10) | No logout by itself: "Account disattivato" with the **Esci** button |

**Logout** (tab **Profilo → Esci**, also on the "Account disattivato" and activation screens), in this order:

1. `DELETE /api/devices/{deviceId}` with the current session: this phone stops receiving the user's pushes. The id is
   the one the app registered with `POST /api/devices` (kept in SecureStore). The backend removes only the caller's
   own registration (404 for any other user).
2. `POST https://<domain>/oauth/revoke` with the refresh token and the Native `client_id` (public client, no
   secret): the refresh token can no longer be exchanged for access tokens.
3. React Query cache cleared (`queryClient.clear()`), then every value in SecureStore removed (tokens, expiry,
   device id). A refresh still running cannot store its tokens any more.
4. `https://<domain>/v2/logout?client_id=…&returnTo=<redirect URI>` in the system browser: ends the Auth0 session of
   the browser, otherwise the next **Continua con Auth0** would sign the previous user in without asking for the
   password. On iOS the system asks "CasaZen wants to use auth0.com to sign in" (standard for the authentication
   browser): answering *Annulla* only skips this step.

Steps 1 and 2 are best effort with a 5 s limit each: offline, the user still gets out, with these consequences:

- device not removed: until another user registers the same push token (the backend then drops the previous
  owner's row), this phone may still receive notifications of the previous user; the host can remove the app's
  notification permission, or log in and out again when online;
- refresh token not revoked: it is no longer on the phone, and it expires with the Refresh Token Expiration of section
  7.4.

Access tokens are not revocable in Auth0: an access token copied before the logout stays valid until it expires
(Maximum Access Token Lifetime), which is why the app never logs or exports it.

When the session **expires** (Auth0 refuses the refresh token), the app cannot call the backend or Auth0 any more:
it clears the cache and SecureStore only. The device registration stays until the same push token is registered by
the next user, or until the same user logs in again (update of the same row).

Check on a device (test tenant, test users only):

1. Log in, open a booking, then in Auth0 → Monitoring → Logs, filtered on `CasaZen Host (mobile)`, wait for the
   access token to expire (or lower Maximum Access Token Lifetime of the test API to a few minutes, and set it back
   afterwards): the next action works without a login, and the logs show *Success Exchange* (Refresh Token for
   Access Token, `sertft`).
2. Revoke the test user's refresh tokens (Auth0 → User Management → Users → the user → **Devices**, or the
   Management API `DELETE /api/v2/device-credentials/{id}`), then use the app: it goes back to the login with "La sessione è scaduta"; after the login it reopens the booking.
3. **Profilo → Esci**: the logs show *Success Revocation* (`srrt`) and *Success Logout* (`slo`); on the test database
   the device row of that user is gone (`SELECT count(*) FROM "DeviceRegistrations" WHERE "UserId" = '<sub>'`).
   **Continua con Auth0** now asks for the credentials: log in with a second test user and check that no data of
   the first one appears and that the first user's pushes (e.g. a check-out reminder) no longer reach the phone.
4. Deactivate the test host (section 10) while the app is open: the next screen shows "Account disattivato";
   **Esci** returns to the login and Auth0 refuses the login ("user is blocked").

| Symptom | Cause | Action |
|---|---|---|
| Auth0 error page at logout mentioning `returnTo` / Allowed Logout URLs | The redirect URI of the build is not in Allowed Logout URLs | Section 7.2 |
| After **Esci**, **Continua con Auth0** signs the previous user in without the password | The Auth0 logout page was not completed (cancelled on iOS, or the logout URL was refused) | Check Allowed Logout URLs, then **Esci** again and let the page complete |
| The app asks for a login every day | No refresh token (Allow Offline Access or grant Refresh Token off), or the refresh is refused | Sections 7.3 and 7.4; Auth0 logs *Failed Exchange* (`fertft`) with the reason |
| Logins requested after a bad connection, logs say the refresh token was reused | Rotation reuse detection after a lost refresh answer | Section 7.4, Rotation Overlap Period |

## 8. Web SPA (reminder)

Vercel variables per environment: `VITE_AUTH0_DOMAIN`, `VITE_AUTH0_CLIENT_ID`, `VITE_AUTH0_AUDIENCE`,
pointing at the tenant of that environment (Preview/`develop` → test tenant, Production → production tenant):
section 1.1 step 4. The web app has no default tenant: without `VITE_AUTH0_DOMAIN` a build fails, a local
`npm run dev` cannot log in.

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

## 10. Deactivated users (PL-03, A1-04)

What happens when an admin deactivates a user from the admin console (`DELETE /api/users/{id}`):

1. **CasaZen (always)**: `Users.IsActive = false`. From the next request, on every API instance, every authenticated
   call of that user answers **403 `account_inactive`**: admin, host, supplier and self-service endpoints alike
   (`/api/users/me` included), whatever the roles still in its access token. The web app shows the page
   "Account disattivato" with the support contact and a logout button, instead of a UI full of errors; the mobile
   app shows the same screen with **Esci** (MO-05, section 7.9).
2. **Auth0 (best effort, reported)**: the account is **blocked** (`blocked: true`: no login, no new token, refresh
   tokens included), then its CasaZen roles are read, stored in `Users.SuspendedAuth0Roles` and **removed**. Roles of
   other applications of the tenant are not touched. Access tokens issued before the deactivation stay valid until
   they expire, but the API already refuses them (step 1).
3. The response says what happened: `auth0Synced` (true/false), `auth0SyncError` (codes above) and a localized
   `message`. With `auth0Synced: false` the user is still refused by the API; repeat the same deactivation when Auth0
   is reachable (it retries only the Auth0 part). The admin console shows a warning toast in that case.

Refused with 422: `cannot_deactivate_self` (own account) and `last_active_admin` (the last active user with the
`Admin` role in the CasaZen DB). Two admins deactivating each other at the same time cannot both succeed (advisory
lock). A role change of a deactivated user is refused with 422 `user_inactive`: reactivate it first.

Reactivation (`POST /api/users/{id}/reactivate`, button "Riattiva" in the admin console) is the inverse: DB flag back,
then the roles in `SuspendedAuth0Roles` are given back and only then the account is unblocked (if the roles cannot be
given back, the account stays blocked and the admin retries). The response lists `rolesRestored`. Roles assigned by
hand in the Auth0 dashboard after the deactivation are not touched.

Audit: there is no audit log table yet; every deactivation and reactivation writes a structured log line with the
ids only (no e-mail or name): `User deactivated: userId=… by=… at=… changed=… auth0Synced=… auth0Error=… rolesSuspended=[…]`
(`User reactivated: … rolesRestored=[…]`). Filter the Railway logs on `User deactivated` / `User reactivated`.

Web support contact (Vercel, optional): `VITE_SUPPORT_EMAIL`. When set, the "Account disattivato" page shows it as a
`mailto:` link; when missing, the page shows a generic text and no address.

Check after the deploy (test tenant, test users only):

1. Deactivate a test host from the admin console: the toast is a success (no warning); in Auth0 → Users the user is
   **Blocked** and has no CasaZen role.
2. With a session of that host already open, reload the web app: it shows "Account disattivato"; logging in again
   is refused by Auth0 ("user is blocked").
3. Reactivate it: Auth0 shows the user unblocked with the same roles as before; the host logs in and works again.
4. If step 1 shows the warning, read `auth0SyncError`: `auth0_management_not_configured` / `auth0_management_token_failed`
   (section 5), `auth0_management_error` with HTTP 403 in the logs = missing scope (section 4: `update:users`,
   `read:role_members`, `delete:role_members`).

## Multi-instance note

The 60 s authorization cache and the role-id cache are per process. A role change is visible immediately on
the instance that made it and within `Authorization__UserCacheSeconds` on the other instances. The deactivation is not
cached: every instance refuses the user from its next request.
