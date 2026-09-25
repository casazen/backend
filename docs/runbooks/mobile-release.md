# Runbook: mobile app release (EAS Build)

Task MO-02. Audit defects: A6-02, A9-37, A6-30 (assets and version part). Product decision D9: code plus runbook, the app reports missing configuration at startup.

Repository: `casazen/mobile` (Expo SDK 52, `expo-router`, EAS Build). The values below are applied by the product owner; nothing in this runbook is a secret stored in git.

## What changed

| Topic | Before | Now |
|---|---|---|
| API / web / Auth0 values in a release build | Fallback to `http://localhost:5000` and the **dev** Auth0 tenant when `.env` was missing (EAS never receives `.env`: it is gitignored) | Fallbacks only in `__DEV__`. Any other build needs the five `EXPO_PUBLIC_*` variables (https, no local address). Otherwise the app shows **only** the screen "Configurazione dell'app incompleta" with code `CONFIG_ENV_INVALID` and the names of the bad variables (never their values) |
| EAS Build | No `env` per profile, no link to EAS environment variables | Each profile reads the EAS environment of the same name (`"environment"` in `eas.json`) and sets `APP_VARIANT`. On the EAS worker a `preview` / `production` build **fails** in `app.config.ts` if a variable is missing or invalid |
| EAS project id | `extra.eas.projectId` = `00000000-0000-0000-0000-000000000000` in `app.json` | Read by `app.config.ts` from `EAS_PROJECT_ID` (on the EAS worker from `EAS_BUILD_PROJECT_ID`). All-zero or malformed values are refused. Without an id the app skips push registration and logs `PUSH_PROJECT_ID_MISSING` instead of crashing (push setup: section 9, task MO-03) |
| Icon, splash, notification icon | None | Neutral **placeholder** files in `assets/` (section 6), to be replaced before the store release |
| Native dependencies | `react-native` 0.76.3, `react-native-screens` 4.1.0, `expo-font` / `expo-file-system` / `expo-keep-awake` nested under `expo/` (not autolinked) | Aligned with SDK 52: `react-native` 0.76.9, `react-native-screens` ~4.4.0, the three modules are direct dependencies |
| App version | `0.1.0` in `app.json`, `0.2.0` in `package.json` | `app.config.ts` takes `version` from `package.json` |

Code: `mobile/app.config.ts`, `mobile/eas.json`, `mobile/src/config/config-rules.js` (rules shared by the app and `app.config.ts`), `mobile/src/config/env.ts`, `mobile/src/config/eas.ts`, `mobile/src/components/ConfigErrorScreen.tsx`. Tests: `mobile/src/config/__tests__/`.

## 0. Prerequisites

- An Expo account that owns (or is a member of the organization that owns) the project. Use a recent CLI: `npx eas-cli@latest ...` (`eas.json` requires `>= 12.0.0`; that version already reads `"environment"` from a build profile).
- Auth0: one **Native** application per tenant, see [auth0.md](auth0.md) sections 1, 2 and 7 (the mobile app does not use the SPA client).
- The public https URLs of the backend (Railway `test` / `production`) and of the web app (Vercel `develop` / Production), see `.claude/rules/infra.md`.

## 1. Create or link the EAS project (`eas init`), once

```bash
cd mobile
npx eas-cli@latest login
npx eas-cli@latest init
```

`eas init` creates (or finds) the project `@<owner>/casazen-host` on expo.dev. The app config is dynamic (`app.config.ts`), so the CLI **cannot write** the id into it: it prints a warning with a JSON block containing `"projectId": "<uuid>"` and stops with `Cannot automatically write to dynamic config at: app.config.ts`. That is expected. Copy the UUID (it is also shown by `npx eas-cli@latest project:info` and on expo.dev → project → Overview).

The project id is **not a secret**. Provide it as `EAS_PROJECT_ID`:

| Where | How | Why |
|---|---|---|
| Shell or CI job that runs `eas-cli` (`build`, `env:*`, `submit`, `credentials`) | `export EAS_PROJECT_ID=<uuid>` (or prefix each command) | EAS CLI evaluates `app.config.ts` locally to find the project. It does **not** read `.env` (it runs `expo config` with `EXPO_NO_DOTENV=1`) |
| `mobile/.env` of each developer | `EAS_PROJECT_ID=<uuid>` | `npx expo start` (development builds) reads `.env`; push tokens need the id |
| EAS Build worker | nothing to do | `app.config.ts` falls back to `EAS_BUILD_PROJECT_ID`, set by EAS for the project being built |

Check: `EAS_PROJECT_ID=<uuid> npx expo config --type public | grep projectId` prints the id. A wrong value (not a UUID, or the old all-zero placeholder) makes every Expo command fail with a message that points here.

## 2. Variables per EAS environment

EAS has three environments: `development`, `preview`, `production` (expo.dev → project → **Environment variables**). Set these variables in each environment you build:

| Variable | `preview` (test) | `production` | `development` (optional) |
|---|---|---|---|
| `EXPO_PUBLIC_API_URL` | Railway `test` public URL, `https://<railway-test-host>` (same as GitHub variable `RAILWAY_TEST_URL`) | Railway `production` public URL (`RAILWAY_PROD_URL`) | Usually unset: developers use `.env` (`http://<LAN IP>:5000`) |
| `EXPO_PUBLIC_WEB_URL` | Vercel staging deployment of `develop`, `https://<staging-host>` | `https://casazen-app.vercel.app` | unset |
| `EXPO_PUBLIC_AUTH0_DOMAIN` | test tenant, e.g. `casazen-test.eu.auth0.com` (host name only, no `https://`) | production tenant, e.g. `casazen.eu.auth0.com` | unset |
| `EXPO_PUBLIC_AUTH0_CLIENT_ID` | client id of the **Native** app `CasaZen Host (mobile)` in the test tenant ([auth0.md](auth0.md) section 7), never the SPA client id | client id of the **Native** app in the production tenant | unset: developers set it in `.env` (Native app of the dev/test tenant). It has **no** local default |
| `EXPO_PUBLIC_AUTH0_AUDIENCE` | API identifier of the test tenant (auth0.md section 2, e.g. `https://casazen-api`) | API identifier of the production tenant | unset |
| `EXPO_PUBLIC_E2E_DEMO` | **do not set** | **do not set** | optional, `1` only for the Maestro demo button in Metro bundles |

Rules enforced by the app and by the EAS build:

- every variable is required outside `__DEV__`; `EXPO_PUBLIC_AUTH0_CLIENT_ID` is required in `__DEV__` too
  (the login configuration error `AUTH_CONFIG_INVALID` is shown without it);
- `EXPO_PUBLIC_E2E_DEMO` is ignored outside `__DEV__`: a preview / production build never shows the demo
  button, and the demo code is not in the release bundle (mobile CI checks it);
- URLs must be absolute `https://` URLs without query string, and must not point to `localhost`, `127.x`, `10.0.2.2` (Android emulator) or `0.0.0.0`. Android release builds block cleartext http anyway;
- trailing slashes are removed; `EXPO_PUBLIC_API_URL` has **no** `/api` suffix (the app adds it);
- `EXPO_PUBLIC_AUTH0_DOMAIN` is a bare host name.

Visibility: **Plain text** (`plaintext`). `EXPO_PUBLIC_*` values are compiled into the JavaScript bundle and readable by anyone who has the app, so they must never contain a real secret (the Native Auth0 app has no client secret: PKCE). `eas-cli` reads only Plain text and Sensitive variables when it evaluates the config locally.

From the CLI (repeat per variable and environment, or use the dashboard):

```bash
export EAS_PROJECT_ID=<uuid>
npx eas-cli@latest env:set --environment preview --name EXPO_PUBLIC_API_URL \
  --value https://<railway-test-host> --visibility plaintext
npx eas-cli@latest env:list --environment preview
```

Do **not** put these variables:

- in `eas.json` → `build.<profile>.env`: a key there **overrides** the EAS environment variable with the same name (EAS CLI warns "The values from the build profile configuration will be used"), even with a `null` value. The profiles only set `APP_VARIANT`;
- in git, in `.env` committed files, or in GitHub (GitHub keeps only `RAILWAY_TEST_URL` / `RAILWAY_PROD_URL` for CI health checks).

## 3. Build profiles (`mobile/eas.json`)

| Profile | EAS environment | `APP_VARIANT` | Distribution | Release checks |
|---|---|---|---|---|
| `development` | `development` | `development` | internal, development client | none: JS comes from Metro on the developer machine (`.env`, local fallbacks) |
| `preview` | `preview` | `preview` | internal (testers) | build fails without valid variables; app shows `CONFIG_ENV_INVALID` if a bundle is built without them |
| `production` | `production` | `production` | store, `autoIncrement` | same as preview |

A new profile without `APP_VARIANT` is treated as a release build (checks on). Versions: `version` comes from `mobile/package.json` (bump it there); build numbers are managed remotely by EAS (`appVersionSource: remote`).

## 4. Build

```bash
cd mobile
export EAS_PROJECT_ID=<uuid>
npx eas-cli@latest build --platform android --profile preview
npx eas-cli@latest build --platform all --profile production
```

In the build output check the line `Environment variables with visibility "Plain text" and "Sensitive" loaded from the "preview" environment on EAS: EXPO_PUBLIC_API_URL, ...` with all five names. If one is missing or invalid, the build stops while reading the app config with:

```text
Build profile "preview" (APP_VARIANT=preview) has an incomplete configuration:
- EXPO_PUBLIC_AUTH0_CLIENT_ID: missing
Set the variables in the EAS environment of this profile (expo.dev → project → Environment variables); ...
```

Fix the variable in the EAS environment and start the build again. The check runs where EAS sets `EAS_BUILD=true` (cloud workers and `eas build --local`); builds started from the expo.dev GitHub integration were not tried: run one `preview` build that way before relying on it. iOS internal builds also need the testers' devices registered (`npx eas-cli@latest device:create`).

Smoke test of each build: install it, the app must open the login screen (not "Configurazione dell'app incompleta"), log in against the tenant of that environment, and the calendar must load from the matching backend. The full login check (callback URLs per platform, Auth0 logs, token claims) is in [auth0.md](auth0.md) section 7.7. Before the first build of a profile, the Native application of its tenant must list the two callback URLs built with that profile's `EXPO_PUBLIC_AUTH0_DOMAIN` (auth0.md section 7.2).

Local release bundles (`npx expo run:android --variant release`, `npx expo export`) are not checked at build time: without the variables they build, and the app shows the `CONFIG_ENV_INVALID` screen at startup. The mobile CI (`expo export`) relies on this and needs no variables.

## 5. Where credentials live

| Credential | Where |
|---|---|
| Android upload keystore, iOS distribution certificate and provisioning profiles | EAS managed credentials (`npx eas-cli@latest credentials`, created on the first build). Never in the repository: `.gitignore` excludes `*.jks`, `*.p8`, `*.p12`, `*.key`, `*.mobileprovision` |
| Push: FCM v1 service account key (Android), APNs key `.p8` (iOS) | EAS credentials (`npx eas-cli@latest credentials`), section 9 |
| Push: `google-services.json` of the Firebase Android app | EAS **file** environment variable `GOOGLE_SERVICES_JSON` (section 9.2). Gitignored, never in the repository |
| Store submission: Google Play service account JSON, App Store Connect API key | EAS submit credentials (expo.dev → project → Credentials), never in the repository |
| `EXPO_TOKEN` (only if builds are started from GitHub Actions in the future) | GitHub Actions secret of `casazen/mobile` |

## 6. Placeholder assets to replace before the store release

The files in `mobile/assets/` are **neutral placeholders** (grey circle), only there so that prebuild and EAS builds do not fail. Replace them with the final artwork at the same paths and within the same constraints before submitting to the stores:

| File | Used for | Requirements |
|---|---|---|
| `assets/icon.png` | App icon (iOS, Android fallback) | 1024×1024 PNG, **no transparency** (App Store rejects alpha) |
| `assets/adaptive-icon.png` | Android adaptive icon foreground | 1024×1024 PNG, transparent background, subject inside the central 66%; background colour `android.adaptiveIcon.backgroundColor` in `app.json` |
| `assets/splash.png` | Splash screen image | PNG with transparency, drawn centred (`resizeMode: contain`) on `splash.backgroundColor` |
| `assets/notification-icon.png` | Android notification small icon (`expo-notifications` plugin) | 96×96 PNG, white shape on transparent background (Android uses only the alpha channel) |

The colour `#1A2B3C` (splash background, adaptive icon background, notification accent) is the existing app colour; change it in `app.json` together with the artwork if needed. `npm test` checks that the four paths exist.

## 7. Dependencies (SDK 52)

After any dependency change run, in `mobile/`:

```bash
npx expo install --check     # versions expected by the installed SDK
npx expo-doctor
```

Both query `api.expo.dev` (and `expo-doctor` also `reactnative.directory`). Without network access use `EXPO_OFFLINE=1 npx expo install --check`, which checks against the versions bundled with `expo`. Keep `expo-asset`, `expo-font`, `expo-file-system` and `expo-keep-awake` as direct dependencies: when npm nests them under `expo/`, autolinking does not link them and the native build can crash at runtime.

## 8. Troubleshooting

| Symptom | Cause | Action |
|---|---|---|
| App shows "Configurazione dell'app incompleta" (`CONFIG_ENV_INVALID`) | Release bundle built without the listed `EXPO_PUBLIC_*` variables, or with http / local URLs | Set them in the EAS environment of the profile (section 2) and rebuild |
| App shows "Configurazione dell'app incompleta" (`AUTH_CONFIG_INVALID`) | Login cannot be configured: `EXPO_PUBLIC_AUTH0_CLIENT_ID` missing (development builds), or `app.json` lacks `scheme` / `ios.bundleIdentifier` / `android.package` | Set the client id of the Native app (auth0.md section 7) or restore `app.json` |
| Auth0 page "Callback URL mismatch" after "Continua con Auth0" | Callback URLs of the Native app do not match the build (domain of the profile, platform) or the build has the SPA client id | [auth0.md](auth0.md) sections 7.2 and 7.7 |
| `eas build` asks to create a project or says the project is not configured | `EAS_PROJECT_ID` not exported in the shell | Section 1 |
| Every Expo command fails with `EAS_PROJECT_ID="..." is not a valid EAS project id` | Placeholder or typo in `EAS_PROJECT_ID` | Use the UUID from `eas init` / `project:info` |
| Log `[push] PUSH_PROJECT_ID_MISSING` | Local build without `EAS_PROJECT_ID` | Set it in `.env` (development) or the shell; EAS builds get it automatically |
| EAS log warns that a variable is defined in both the build profile `env` and the EAS environment | Someone added it to `eas.json` | Remove it from `eas.json`: the value there wins |
| Push codes (`PUSH_FCM_NOT_CONFIGURED`, `PUSH_APNS_NOT_CONFIGURED`, `PUSH_BACKEND_*`, ...) in **Profilo → Notifiche** or in the logs | Push setup of the build | Section 9.6 |

## 9. Push notifications (FCM v1, APNs), task MO-03

Audit defects A6-05 (registration never succeeded), A6-06 (device id shared by phones on the same OS build), A6-19 (tap on a notification lost at cold start or when signed out). Code: `mobile/src/notifications/` (`push-registration.ts`, `installation-id.ts`, `notification-routes.ts`, `notification-navigation.ts`, `PushHandler.tsx`, `PushSettingsCard.tsx`), `mobile/app.config.ts`; backend `Casazen.Web/Controllers/DevicesController.cs`, `Casazen.Infrastructure/Services/PushNotificationService.cs`, `Casazen.Core/Services/PushRoutes.cs`.

### 9.1 How it works

1. After the login, once `GET /users/me` grants the host access (PL-02), the app registers the phone **at every start**:
   - Android: creates the notification channel `default` ("Notifiche CasaZen", high importance) **before** asking for the permission (Android 13+ shows the prompt only when a channel exists). The backend sends every push with `channelId: "default"` and the `expo-notifications` plugin sets it as FCM default channel;
   - permission: the first time the app explains what the notifications are for ("Vuoi attivare le notifiche?" → **Attiva** / **Non ora**, once per installation), then the system prompt. Later, **Profilo → Notifiche** shows the state with **Attiva notifiche**, **Apri impostazioni** (permission blocked in the system settings) or **Riprova**;
   - Expo push token with the EAS project id of the build (`getExpoPushTokenAsync({ projectId })`, section 1). No project id → no token, error `PUSH_PROJECT_ID_MISSING` logged and shown;
   - `POST /api/devices` with `{ platform, pushToken, deviceId }`. The `deviceId` is the **installation id**: a random UUID generated at the first launch and kept in SecureStore (`casazen_installation_id`). It is not a session value: it survives the logout. The id actually registered is also kept for the logout (`DELETE /api/devices/{id}`, MO-05).
2. Every failure ends in a state with a stable code (section 9.6), logged without the push token, never as an unhandled error. The registration runs again at the next start, and when the app returns to the foreground after a transient failure or a refused permission.
3. On the emulator / simulator the app skips the registration: **Profilo → Notifiche** says that push notifications need a physical device, and development builds log `PUSH_UNSUPPORTED_DEVICE`. Development builds also show a diagnostics line (state, error code, device id) in the same card.
4. Tap on a notification: handled in the root layout, also when the tap **started the app** (`getLastNotificationResponseAsync`) or arrived **while signed out**: the destination is kept (in memory) and opened right after the login, above the calendar. The backend builds every `route` with `PushRoutes`; the app opens only these screens (`notification-routes.ts`):

| Push | `route` |
|---|---|
| Booking alerts (guest check-in incomplete, Alloggiati Web, ...) | `/bookings/{bookingId}` |
| Supplier update of a request tied to a stay | `/bookings/{bookingId}` |
| Supplier update of a request **without** a stay (long-rent, or short-rent created before SU-07) | `/properties` (the app has no service request screen; `/service-requests/{id}` used to open nothing) |
| Check-out reminder | `/bookings/{bookingId}/checkout` |

An unknown route falls back to `bookingId` when the push has one, otherwise the app just opens.

### 9.2 Android: Firebase and FCM v1 (once per Firebase project)

Expo delivers Android pushes through **FCM HTTP v1**; the legacy FCM server key is no longer accepted by Google.

1. [Firebase console](https://console.firebase.google.com) → create (or open) the project `casazen` → **Add app → Android**, package **`it.casazen.host`** (`android.package` in `app.json`). Download `google-services.json`. It holds the Firebase app identifiers and an API key restricted to this app: not a server secret, but it stays **out of git** (`.gitignore`).
2. Upload it as an EAS **file** variable, in every environment you build Android in:
   ```bash
   export EAS_PROJECT_ID=<uuid>
   npx eas-cli@latest env:create --environment production --name GOOGLE_SERVICES_JSON --type file \
     --value ./google-services.json --visibility secret
   # repeat with --environment preview (and development if you build a development client on EAS)
   ```
   On the build worker `GOOGLE_SERVICES_JSON` is the path of the file; `app.config.ts` sets `android.googleServicesFile` to it. Locally, put the file at `mobile/google-services.json` (gitignored) or set `GOOGLE_SERVICES_JSON=<path>` in `mobile/.env`.
3. FCM v1 service account key: Firebase console → Project settings → **Service accounts** → **Generate new private key** (JSON). Upload it to Expo, **not** to git:
   ```bash
   npx eas-cli@latest credentials   # Android → production → Google Service Account → Manage your Google Service Account Key for Push Notifications (FCM V1) → Upload
   ```
   (or expo.dev → project → Credentials → Android → FCM V1 service account key). Delete the downloaded JSON from the disk afterwards.
4. Build checks: an Android **production** build fails without `GOOGLE_SERVICES_JSON` (`- GOOGLE_SERVICES_JSON: missing ...`); a **preview** build only warns in the build log, so internal testing of the other features is possible before Firebase is set up, and the app shows `PUSH_FCM_NOT_CONFIGURED` in **Profilo → Notifiche**.

### 9.3 iOS: APNs

1. The `expo-notifications` plugin adds the Push Notifications capability; `app.config.ts` sets `aps-environment` to `production` for EAS preview/production builds (ad hoc and App Store profiles) and `development` otherwise.
2. APNs key: at the first iOS build EAS asks "Would you like to set up Push Notifications for your project?" → **Yes**, it creates and stores the key. To manage it later: `npx eas-cli@latest credentials` → iOS → production → **Push Notifications: Manage your Apple Push Notifications Key** (create, or upload an existing `.p8` from Apple Developer → Keys with "Apple Push Notifications service" enabled). One key serves every app of the Apple team; the `.p8` never goes to git (`.gitignore`).
3. If the provisioning profile was created before the capability, regenerate it (`eas credentials` → iOS → Provisioning Profile → remove, the next build recreates it). Symptom: `PUSH_APNS_NOT_CONFIGURED` ("no valid aps-environment entitlement").

### 9.4 Backend: device registrations (A6-06)

- One row per (user, installation): unique index `UIX_DeviceRegistrations_UserId_DeviceId`, upsert that updates the token. Concurrent registrations of the same installation (unique violation) re-read and update instead of answering 500.
- An Expo push token belongs to one installation: registering it removes any other row carrying it (another user who used the phone, or the same user under an old device id).
- **Old device ids** (builds before MO-03 sent the OS build id, `Device.osInternalBuildId`, shared by every phone on the same OS build): **no data migration**. Each phone replaces its old row the first time the new build registers it with the same push token. A row whose token is never registered again (phone still on an old build, or whose token was overwritten by another phone of the same build) keeps working for that phone; Expo answers `DeviceNotRegistered` for dead tokens and `PushNotificationService` then deletes the row. Deleting all old rows in a migration was rejected: it would silence phones not updated yet. Check what is left: `SELECT count(*) FROM "DeviceRegistrations" WHERE "DeviceId" !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$';` (a cleanup of old rows not updated for months can be decided later).

### 9.5 Check on devices (not possible in CI, to do after each credentials change)

Use a `preview` build of the test environment, two physical phones (one Android, one iPhone) and a test host with a property and a booking.

1. First login: the app explains the notifications, then the system prompt. **Attiva** → **Profilo → Notifiche** says "Attive". On the test database: `SELECT "Platform", "DeviceId", "UpdatedAt" FROM "DeviceRegistrations" WHERE "UserId" = '<sub>';` shows one row per phone, `DeviceId` a UUID.
2. Send a push to the token from https://expo.dev/notifications (token from the `PushToken` column) with data `{"route":"/bookings/<bookingId>","bookingId":"<bookingId>"}` and channel `default`: it arrives with the app in background and closed.
3. Real pushes: a supplier update of a request of that booking (supplier account: take the request), a check-out reminder, an incomplete guest check-in alert. Tap each: the booking (or its check-out) opens, above the calendar.
4. Cold start: kill the app, tap a notification → the app opens on the booking, not on the calendar.
5. Signed out: **Profilo → Esci**, send a push again to the same token (the device row is gone, so use the Expo tool), tap it → login → after the login the booking opens.
6. Two phones of the same host on the same OS version: both receive every push (before MO-03 only the last registered one did).
7. Permission refused: deny the prompt → **Profilo → Notifiche** offers **Attiva notifiche** / **Apri impostazioni**; enable in the system settings, go back to the app → "Attive" without restarting.
8. Emulator / simulator: **Profilo → Notifiche** says a physical device is needed; no crash.

### 9.6 Codes

| Code | Meaning | Action |
|---|---|---|
| `PUSH_PROJECT_ID_MISSING` | Build without EAS project id | Section 1 (`EAS_PROJECT_ID`), rebuild |
| `PUSH_FCM_NOT_CONFIGURED` | Android build without `google-services.json` (Firebase not initialised) | Section 9.2, rebuild |
| `PUSH_APNS_NOT_CONFIGURED` | iOS build without the push entitlement / APNs key | Section 9.3, rebuild |
| `PUSH_TOKEN_UNAVAILABLE` | Expo token not obtained (Expo servers unreachable, FCM/APNs refusal) | Retried automatically; if it persists check the FCM V1 key / APNs key in EAS credentials |
| `PUSH_BACKEND_UNAVAILABLE` | `POST /api/devices` not answered (offline, 5xx) | Retried automatically |
| `PUSH_BACKEND_REJECTED` | `POST /api/devices` answered 4xx | Backend logs of the request |
| `PUSH_CHANNEL_FAILED`, `PUSH_PERMISSION_FAILED`, `PUSH_INSTALLATION_ID_UNAVAILABLE` | Native call failed (channel, permission, SecureStore) | Retried at the next start; report with the device model and OS |
| Expo receipt `InvalidCredentials` / `MismatchSenderId` (backend logs, Expo dashboard) | FCM V1 key missing or of another Firebase project; APNs key revoked | Sections 9.2 step 3, 9.3 step 2 |
