# Runbook: mobile app release (EAS Build)

Task MO-02. Audit defects: A6-02, A9-37, A6-30 (assets and version part). Product decision D9: code plus runbook, the app reports missing configuration at startup.

Repository: `casazen/mobile` (Expo SDK 52, `expo-router`, EAS Build). The values below are applied by the product owner; nothing in this runbook is a secret stored in git.

## What changed

| Topic | Before | Now |
|---|---|---|
| API / web / Auth0 values in a release build | Fallback to `http://localhost:5000` and the **dev** Auth0 tenant when `.env` was missing (EAS never receives `.env`: it is gitignored) | Fallbacks only in `__DEV__`. Any other build needs the five `EXPO_PUBLIC_*` variables (https, no local address). Otherwise the app shows **only** the screen "Configurazione dell'app incompleta" with code `CONFIG_ENV_INVALID` and the names of the bad variables (never their values) |
| EAS Build | No `env` per profile, no link to EAS environment variables | Each profile reads the EAS environment of the same name (`"environment"` in `eas.json`) and sets `APP_VARIANT`. On the EAS worker a `preview` / `production` build **fails** in `app.config.ts` if a variable is missing or invalid |
| EAS project id | `extra.eas.projectId` = `00000000-0000-0000-0000-000000000000` in `app.json` | Read by `app.config.ts` from `EAS_PROJECT_ID` (on the EAS worker from `EAS_BUILD_PROJECT_ID`). All-zero or malformed values are refused. Without an id the app skips push registration and logs `PUSH_PROJECT_ID_MISSING` instead of crashing (push setup: task MO-03) |
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
| Push: FCM v1 service account key, APNs key | EAS credentials, task MO-03 |
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
