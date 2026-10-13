# Runbook: host app E2E (Maestro on an Android emulator, ephemeral stack)

Task FN-04 (audit defect A6-22; gate G2 of `Sessions/PLANNING.md`). Before it, the Maestro flows of the app asserted text
that is also true of a broken app (`.*Pagato.*` matches the button "Segna pagato"), tapped steps as `optional`, dismissed
ANR dialogs automatically, never logged in (they relied on a session typed in by hand) and never ran in CI. Now the suite
logs in for real, asserts by `testID`, checks the backend after every flow that changes data, and runs on an emulator in
GitHub Actions against the same ephemeral stack as the Golden Journey L3 ([`golden-journey-l3.md`](golden-journey-l3.md)).

## 0. What is proven and what is not (read this first)

The author's environment has no Android emulator, no Maestro CLI and no access to `dl.google.com`. So:

| Part | Status |
|---|---|
| Flow files: YAML syntax, every `id:` is a `testID` of the app, every `${VAR}` is provided by the seed, no `optional` step, helper parameters passed | **Verified** (`npm run e2e:check`, also a jest test, with negative tests of the checker) |
| Ephemeral stack (`e2e/stack/up.sh`): database, real backend, mock IdP and mail | **Verified** locally (started, seeded, stopped) |
| Seed (`e2e/seed/seed-host-app.mjs`): invite, supplier activation, host onboarding, 4 properties, 4 stays `CheckedIn`, requests `Richiesto` and `Completato`, one property published | **Verified** locally against the real backend |
| API checks between flows (`e2e/tools/api-check.mjs`): device row, booking and request status, supplier take / complete, no 5xx | **Verified** locally (each one exercised with the calls the app makes) |
| E2E build config: `app.config.ts` guards, env rules, network security plugin, `expo prebuild` output, Metro bundle with the E2E values | **Verified** (jest, `expo prebuild`, `expo export`) |
| Push registration seam (fixed token on the emulator) | **Verified** by unit tests; the real call to `POST /api/devices` in the emulator is **not** run here |
| Workflow syntax | **Verified** with `actionlint` |
| Gradle build of the APK, emulator boot, Maestro install, IdP certificate in the emulator, Chrome Custom Tab login, every flow, share sheet, logcat check | **Only runs in CI, never run yet: UNPROVEN.** Expect first-run fixes (selectors of the Chrome login page, the share sheet text, versions) |

The first green run of the job is the proof. Until then do not make the check required (section 3).

## 1. What runs

Flows in `casazen/mobile/e2e/`, run in this order by `e2e/tools/run-suite.sh`. After the flows marked "API" the script reads
the backend as the host (or acts as the supplier) through the mock IdP: the screen alone is never the proof.

| Flow | Asserts (by `testID`) | API after |
|---|---|---|
| `m0-login` | the real login: `login-button` → system browser with the Universal Login page of the IdP → back in the app; `calendar-month`; Profile shows the signed-in email | |
| `m1-calendar` | property chips `calendar-property-{id}`; the stay card `calendar-booking-{id}` of each property (guest, status) and only of that property | |
| `m2-booking-detail` | `booking-status-badge` ("Check-in effettuato"), `sr-status-{id}` = "Richiesto" for the seeded request, `request-supplier`, `quick-checkout`; no "Segna pagato" for a request not completed | |
| `m3-push` | Profile `push-settings-status` says the phone is registered (only true after `POST /api/devices` answered 2xx); the booking route of a push (`/bookings/{id}`) opens the detail | API: the device row with the E2E token exists |
| `m4-create-service-request` (MO-10) | stay without requests; supplier `supplier-{orgId}` listed; notes typed; submit; dialog "Richiesta inviata a"; exactly one `sr-status-*` = "Richiesto" | API: one request on the stay, `Richiesto`, with the typed notes |
| `m5a/b/c` | `sr-status-{id}`: `Richiesto`, then (after the supplier took it through the API) `Preso in carico` after a pull-to-refresh, then `Completato` and `sr-mark-paid-{id}` | API: `PresoInCarico`, `Completato` |
| `m6-mark-paid` | `sr-mark-paid-{id}` on the seeded `Completato` request, confirm dialog, `sr-status-{id}` = "Pagato", button gone | API: `Pagato` |
| `m7-checkout` (MO-09) | `checkout-close-stay` disabled until `checkout-confirm-departure`, early-departure dialog, "Soggiorno chiuso", `checkout-title` = "Check-out completato", `booking-status-badge` = "Check-out effettuato" | API: `CheckedOut` |
| `m8-share-link` (MO-11) | published property: `property-share-{id}` enabled, no `property-share-unavailable-{id}`; unpublished property: disabled with the explanation; tap opens the share sheet showing `…/property/{slug}` | |
| end | | API: no 5xx answered by the backend; `adb logcat`: no `FATAL EXCEPTION`, no `ANR in it.casazen.host` |

No flow taps "Wait" on an ANR dialog and none uses `optional`: an ANR or a missing button fails the run. The confirm button of
a native alert is `android:id/button1` (React Native maps the last button of `Alert.alert` to the positive one).

Language: the flows accept Italian and English labels (`Richiesto|Requested`), because the app follows the device language.

## 2. How it works

**Stack** (`mobile/e2e/stack/up.sh`, `down.sh`): the same pieces as the Golden Journey stack, without the web frontend. A
throw-away database `app_e2e_<run>`, the real `Casazen.Web` in `Development`, and the mock IdP + mock mail of the frontend repo
(`frontend/e2e/stack/mock-services.mjs`, FN-03). The backend validates the mock tokens with its **unchanged** JwtBearer setup;
it trusts the throw-away certificate through `SSL_CERT_FILE`. Nothing in production authentication is weakened or bypassed. Two
test-only settings: `Entitlement__Tiers__Starter__MaxProperties=10` (the seed makes four properties; the Starter plan allows
three) and request logging (`Microsoft.AspNetCore.Hosting.Diagnostics` at Information) for the no-5xx check. `Email__ApiUrl`
(FN-03) points the mail client at the mail catcher; it is rejected outside Development / Testing.

**Real login.** The E2E APK is a normal release build of the app (no `__DEV__`, no demo mode, no token injection): "Continua con
Auth0" opens the system browser on `https://localhost:9443/authorize` (the mock IdP), the flow types the test user's
credentials into that page, and the IdP redirects to `casazen://localhost:9443/android/it.casazen.host/callback`, which the app
exchanges for tokens with PKCE. The emulator reaches the stack on the runner with `adb reverse` (API and IdP ports), so the IdP
is `localhost:9443` for the app, the browser and the backend (same issuer). The IdP's throw-away certificate is installed in the
emulator's **user** CA store (`e2e/tools/android-trust-cert.sh`, needs a `google_apis` image where `adb root` works), and the E2E
build's network security config trusts user CAs in addition to the system ones. TLS validation is not disabled anywhere.

**E2E build** (`APP_VARIANT=e2e` + `EXPO_PUBLIC_E2E_BUILD=1`), local only:

- `src/config/config-rules.js`: with the flag, `http` and local addresses and `localhost:<port>` as Auth0 domain are accepted (every
  variable is still required). Without the flag the release rules are unchanged (tests: `env.test.ts`).
- `plugins/with-e2e-network-security.js`, added by `app.config.ts` only for this variant: cleartext to the local API and user CAs.
- `EXPO_PUBLIC_E2E_PUSH_TOKEN`: an emulator has no FCM, so the app registers this fixed token (`registerWithGivenToken` in
  `src/notifications/push-registration.ts`) and the suite proves the **registration**. Without the flag `env.e2ePushToken` is
  `undefined` and the branch never runs.
- **Guards**: `app.config.ts` fails any EAS build (every profile) that carries `APP_VARIANT=e2e`, `EXPO_PUBLIC_E2E_BUILD` or
  `EXPO_PUBLIC_E2E_PUSH_TOKEN`; the mobile `ci.yml` fails when the release bundle contains the E2E values or the demo / token
  injection code. The E2E APK is never uploaded anywhere but the CI artifacts of the job.

**Seed** (`e2e/seed/seed-host-app.mjs`, audit "seed dedicato per flow"). Runs against the empty database of the run, only through
the public API with tokens of the mock IdP (headless authorization code + PKCE, `e2e/lib/idp.mjs`), like the Golden Journey does
from the UI; it reuses the GJ's stack, IdP, mail catcher, account conventions (`gj-admin-*` is the platform admin) and
relative dates (today in Europe/Rome). It does not read `gj-seed.json`: that file describes a journey that ends with the stay
`CheckedOut`, while the app flows need stays in a known earlier state. Created: supplier (admin invite → mail → register →
activation, as SU-05), host (onboarding with the current consents), four properties in Roma (ISTAT 058091) and, on each, a stay
that contains today, registered as arrived (`CheckedIn`): **alpha** with a request `Richiesto` (calendar, detail, M5), **beta**
with a request `Completato` (M6), **gamma** with none (M4), **delta** with none (M7). Alpha is also published. Two SQL
statements stand in for hosted pages (as `linkConnectedAccount` in the GJ): publishing alpha (`ComplianceStatus` = Active; the
activation wizard with document upload is covered by the GJ) — nothing else touches the database. The seed writes
`e2e/.stack/seed.json` (no secret; uploaded as an artifact); no token is written to disk.

## 3. CI

Workflow `casazen/mobile/.github/workflows/e2e-android.yml`, job **`Maestro on Android emulator`** (75 minutes at most):
runs on PRs to `main` (the release PR `develop` → `main`), on PRs of any branch with the label **`e2e-app`**, on pushes to `main`,
nightly (02:45 UTC) and manually. It checks out mobile, backend and (only `e2e/stack`) frontend at the same line of development,
starts `postgres:16`, builds the backend, starts the stack, builds the APK (`expo prebuild` + `./gradlew assembleRelease`, x86_64
only), installs Maestro (`MAESTRO_VERSION`, pinned), boots a `google_apis` API 34 emulator
(`reactivecircus/android-emulator-runner`) and runs `e2e/tools/run-suite.sh`. Uploads `mobile-e2e` (JUnit reports, Maestro
debug output with screenshots of the failure, `logcat.txt`, stack logs, `seed.json`). No `continue-on-error`.

One-time setup (repository admin):

| Kind | Where | Name | Value |
|---|---|---|---|
| Secret | `casazen/mobile` | `BACKEND_REPO_TOKEN` | fine-grained PAT, **read** on `casazen/backend` contents |
| Secret | `casazen/mobile` | `FRONTEND_REPO_TOKEN` | fine-grained PAT, **read** on `casazen/frontend` contents (only the mock IdP is used) |

Nothing else: no Auth0 tenant (the IdP is the mock), no Stripe, no EAS token, no Firebase / FCM, no Expo account.

**Fail-visible rules** (a check that cannot fail is not a gate):

- Missing `BACKEND_REPO_TOKEN` / `FRONTEND_REPO_TOKEN` (also for fork PRs and Dependabot): the job **fails** at its first step
  with an `::error::` annotation. It is never reported as passed.
- `run-suite.sh` fails with an `::error::` annotation, before running anything, when `adb`, `maestro`, `node`, `psql`, the
  stack, the APK or the push token are missing, or the seed fails.
- If the login (`m0-login`) fails the suite stops (nothing after it means anything); any other flow or check failure is
  collected, the run continues and the final status is failed with the list.
- A PR to a branch other than `main` without the label does not run the job at all: GitHub shows the check as skipped. Label
  `e2e-app` on any PR that touches the app screens, auth, push or the E2E files.
- Required check: after a few green runs, add **`Maestro on Android emulator`** as required on `main` only (release gate G2), with
  the command of `ci-frontend.md` section 1 on `repos/casazen/mobile`. `develop` keeps the fast gate (`Typecheck, lint, unit tests, bundle`).

## 4. Run it locally

Needs Android SDK with an emulator image `google_apis` (not Play), Maestro CLI, PostgreSQL, .NET 10, Node 22, Java 17, openssl.

```bash
export PGHOST=localhost PGUSER=postgres PGPASSWORD=dev
cd mobile && E2E_BACKEND_DIR=../backend E2E_FRONTEND_DIR=../frontend bash e2e/stack/up.sh
export APP_VARIANT=e2e EXPO_PUBLIC_E2E_BUILD=1 EXPO_PUBLIC_E2E_PUSH_TOKEN='ExponentPushToken[e2e-maestro-local]' \
  EXPO_PUBLIC_API_URL=http://localhost:5100 EXPO_PUBLIC_WEB_URL=http://localhost:5173 \
  EXPO_PUBLIC_AUTH0_DOMAIN=localhost:9443 EXPO_PUBLIC_AUTH0_CLIENT_ID=e2e-app-client EXPO_PUBLIC_AUTH0_AUDIENCE=https://casazen-api
npx expo prebuild --platform android --no-install && (cd android && ./gradlew assembleRelease -PreactNativeArchitectures=x86_64)
bash e2e/tools/run-suite.sh        # emulator running; seeds, installs the APK, runs flows and checks
bash e2e/stack/down.sh
```

Without an emulator you can still run the static check (`npm run e2e:check`), the stack and the seed
(`node e2e/seed/seed-host-app.mjs`), and the API checks. To debug one flow: `maestro studio`, or
`maestro test $(node e2e/tools/maestro-env.mjs | sed 's/^/-e /') e2e/m6-mark-paid.yaml` after the seed.

## 5. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Repository tokens present` fails | the two secrets of section 3 are missing or not available to this run |
| `m0-login` stays on "Mock Universal Login" or cannot find the fields | the page of the mock IdP changed: the flow taps "Email.*", "Password.*" and "Continue" (labels of `mock-services.mjs`); adjust `e2e/helpers/login.yaml` |
| The browser shows a certificate error / the login screen shows `AUTH0_TOKEN_EXCHANGE_FAILED` | the IdP certificate is not in the emulator's user store (`android-trust-cert.sh` output) or the image is not `google_apis` (no `adb root`) |
| Chrome shows a welcome / sign-in screen | its command-line file was not read: `android-trust-cert.sh` writes `/data/local/tmp/chrome-command-line` (works on debuggable images only) |
| The app shows `CONFIG_ENV_INVALID` | the APK was built without `EXPO_PUBLIC_E2E_BUILD=1` (the release rules then refuse http / localhost) |
| m3 fails on `push-settings-status` | `POST /api/devices` failed: Profile shows the code (`PUSH_BACKEND_*`); check `e2e/.stack/backend.log` |
| m8 fails on the share sheet text | the sheet of that Android version does not show the shared text: keep the enabled / disabled assertions and drop the text one, or read the link from `seed.json` |
| `no-5xx` fails | the message lists the status, method and path; find the cause in `backend.log` |
| Everything is in the wrong language | the emulator language is neither Italian nor English: the flows accept both, other languages fall back to Italian in the app |
| `maestro: command not found` / install fails | `MAESTRO_VERSION` in the workflow does not exist: pick a published CLI release |

## 6. Known limits

- **Push delivery and the tap on a notification are not tested.** An emulator has no FCM and no real Expo token. Covered: the
  registration (flow + database), the route of a push (deep link `casazen://bookings/{id}`) and, in jest, the parsing of the
  notification data (`notification-navigation`, `notification-routes`). Delivery needs a physical device ([`mobile-release.md`](mobile-release.md) section 9).
- Android only. iOS needs a macOS runner and a simulator with `xcrun simctl`; not planned in this task.
- The Universal Login page is the mock one. The real Auth0 Universal Login (Native app, callback URLs) is checked by hand
  ([`auth0.md`](auth0.md) 7.9) and with the staging build.
- Calendar blocks of iCal feeds (PC-09) are not seeded: the web suite and the unit tests cover them.
