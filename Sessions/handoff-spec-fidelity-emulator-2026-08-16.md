# Handoff — spec-fidelity loop + Android emulator / Maestro

**Date:** 2026-08-16  
**Stopped by user:** do not continue the Gradle/Maestro work in the previous chat. Start from this file.  
**Goal of the previous session:** finish `/spec-fidelity-loop` for GJ-001 by installing an Android emulator and running Maestro M1–M7 against the real local API (no mocks, no production).

Suggested first message for a new chat:

> Read `Sessions/handoff-spec-fidelity-emulator-2026-08-16.md` and continue from the “Next actions” section. Do not hit production. Do not commit unless I ask.

---

## What this work is

CasaZen is three repos on Windows:

| Repo | Path | Branch at stop |
|---|---|---|
| Backend | `C:\Users\luca.la-malfa\private-project\casazen\backend` | `develop` (in sync with origin) |
| Frontend | `C:\Users\luca.la-malfa\private-project\casazen\frontend` | `develop` (**behind origin by 4 commits**) |
| Mobile | `C:\Users\luca.la-malfa\private-project\casazen\mobile` | `develop` |

**Spec:** `Sessions/specs/spec-golden-journey-e2e.md` (GJ-001).  
**Superseded:** `spec-production-e2e-flow-verification.md` — do **not** hit `casazen-app.vercel.app` or set `E2E_PROD_SMOKE`.  
**Loop artifacts:** `reports/attempt-1/` … `reports/attempt-5/`.  
**Last verdict:** `reports/attempt-5/05-final-verdict.md` → `GOAL_NON_RAGGIUNTO` solely because Maestro/device was missing. That is now only partly true (CLI + AVD exist; app not installed/run).

There is no `development` branch. Use `develop`.

---

## What already works (do not redo)

### Spec-fidelity loop (attempts 1–5)

- Agent prompts written under `backend/.claude/agents/` (cataloger, test-designer, auditor, fix-planner, dev, review, final-verifier). Untracked.
- Frontend/mobile were on feature branches with WIP; **stashed** as `spec-fidelity-loop: stash before develop checkout` then checked out `develop`.
- Local Postgres via Docker container `casazen-dev-pg` (`postgres:16`, user `postgres` / `dev`, db `casazen_dev`, port 5432).
- API at `http://localhost:5000` with real Postgres (not InMemory). Health was **200** at handoff.
- Frontend Vite at `http://localhost:5173` with `VITE_API_BASE_URL=http://localhost:5000/api`. HTTP **200** at handoff.
- **Do not** use `frontend/scripts/start-backend-local.ps1` — it clears the connection string and falls back to InMemory.
- L3 Playwright succeeded (16 Aug):

```text
cd frontend
$env:E2E_LOCAL = "1"
npx playwright test --project=local e2e/golden-journey-web.spec.ts -g "sequential against live API"
# 2 passed (setup + 12-step), ~28s
```

Filter `-g "steps 1-12"` **fails** because the title uses an en-dash: `steps 1–12 sequential against live API`.

Auth0 test user lives in gitignored `frontend/.env.e2e` (`E2E_AUTH0_EMAIL` / password). **Never copy secrets into reports.**

API must be started with the real Auth0 tenant (placeholders break profile as “Caricamento...”):

```powershell
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Port=5432;Database=casazen_dev;Username=postgres;Password=dev"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:5000"
$env:Auth0__Domain = "dev-mp6wadq7j6bophl5.us.auth0.com"
$env:Auth0__Audience = "https://casazen-api"
dotnet run --project Casazen.Web --no-launch-profile
```

### Harness code (uncommitted)

**Backend**

- `Sessions/golden-journey-runbook.md` — 12-step + M1–M7 + F1–F2
- `.github/workflows/e2e-golden-journey.yml` — pointer to frontend workflow
- `reports/` — loop evidence

**Frontend (`develop`, dirty)**

- `e2e/golden-journey-web.spec.ts` — L2 demo skipped when `E2E_LOCAL=1`; L3 walks steps 1–12 against `http://localhost:5000/api` with unique `gj-{timestamp}` slugs, no `page.route`
- `e2e/golden-journey-supplier-mobile.spec.ts` — F1–F2, viewport 375×812
- `e2e/auth.setup.ts` — after Auth0 login, accept onboarding landing (fresh DB)
- `playwright.config.ts` — local project includes the new specs
- `.github/workflows/e2e-golden-journey.yml` — PR runs L2 GJ web; Maestro job gated on `main` / nightly / label `e2e-app`

**Mobile (`develop`, dirty)**

- `e2e/m1-calendar.yaml` … `m7-checkout.yaml` — `EXPO_PUBLIC_API_URL=http://localhost:5000`, demo flag removed
- `appId` changed from `casazen-host` to **`it.casazen.host`** (matches `app.json` android.package)
- M3 deep link: `casazen://bookings/${BOOKING_ID}`

### Android environment (installed this session)

| Item | Value |
|---|---|
| SDK | `%LOCALAPPDATA%\Android\Sdk` |
| Java 17 | `C:\Users\luca.la-malfa\scoop\apps\openjdk17\current` |
| Maestro 2.8.0 | `%LOCALAPPDATA%\maestro\maestro\bin\maestro.bat` |
| AVD | `casazen` — Pixel 6, Android 14, `google_apis/x86_64` |
| AVD path | `%USERPROFILE%\.android\avd\casazen.avd` |
| Emulator | `emulator-5554` was **booted and connected** at handoff |
| NDK | `ndk\27.1.12297006` (complete; was briefly in `27.1.12297006-2` after a race) |
| Platforms | android-34 (AVD) + android-36 (Expo compile) |
| Build-tools | 34.0.0 and 36.0.0 |

Emulator localhost → host machine: use `adb reverse tcp:5000 tcp:5000` (and `8081` for Metro). Guest `10.0.2.2` is the alternative.

```powershell
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:JAVA_HOME = "C:\Users\luca.la-malfa\scoop\apps\openjdk17\current"
& "$env:ANDROID_HOME\platform-tools\adb.exe" devices -l
& "$env:ANDROID_HOME\emulator\emulator.exe" -avd casazen -no-snapshot-load -gpu auto
```

NVIDIA Vulkan driver is old → emulator uses SwiftShader/lavapipe. Fine for Maestro.

---

## Where we stopped

The Host app is **not ready for Maestro**.

1. `npx expo prebuild --platform android` created `mobile/android/` (untracked).
2. First `npx expo run:android` **failed**: Gradle and sdkmanager raced installing NDK 27 → `DirectoryNotEmptyException` / incomplete `ndk\27.1.12297006`.
3. NDK was moved into the expected folder. Second `npx expo run:android` **failed** on CMake/ninja:

```text
Filename longer than 260 characters
.../cursor-sandbox-cache/<hash>/gradle/caches/8.14.3/transforms/.../libreactnative.so
```

Gradle was using Cursor’s sandbox cache path, which blows Windows MAX_PATH.

4. Third attempt (still running when the user said stop; **process was killed**):
   - `GRADLE_USER_HOME=C:\Users\luca.la-malfa\.gradle`
   - `TEMP`/`TMP` reset to `%LOCALAPPDATA%\Temp`
   - `android/gradle.properties` → `reactNativeArchitectures=x86_64` only (emulator is `sdk_gphone64_x86_64`)
   - Expo printed `Port 8081 is being used` and, in non-interactive mode, **skipped the Metro bundler**
   - Gradle had passed configure and was compiling Java/Kotlin (`:app:checkDebugAarMetadata`). No `BUILD SUCCESSFUL` yet.
   - `android/app/build/outputs/apk/debug/app-debug.apk` **exists on disk** — treat as **incomplete / not verified**. Do not assume it is installed on the AVD.

Maestro was last run before the AVD existed → `0 devices connected`. It has **not** been re-run against a working Host APK.

---

## Current problems (ordered)

### P0 — Native Android build not finished

- Windows 260-char path + Cursor `cursor-sandbox-cache` Gradle home.
- Mitigation started: short `GRADLE_USER_HOME`, x86_64-only, NDK 27 in the right folder.
- Third build was interrupted. Resume with the env block below; do not let Gradle write caches under `AppData\Local\Temp\cursor-sandbox-cache`.
- Optional extra: enable Windows long paths (`HKLM\...\LongPathsEnabled=1`) if you have admin.

### P0 — Debug APK needs Metro

`expo run:android` builds a **debug** app. Without Metro the app shows “Unable to load script”.  
Last attempt skipped Metro because 8081 was busy. Next run must either:

- start Metro on a free port and keep it up, or
- build `--variant release` so JS is bundled (heavier; signing may need the debug keystore).

### P0 — Maestro cannot pass login as written

`m1-calendar.yaml` does `launchApp` then asserts `CasaZen Host` + `Calendario` + `Mese corrente`.  
The app always lands on **login** unless a token exists (`AuthProvider` + `app/_layout.tsx`).  
`EXPO_PUBLIC_E2E_DEMO` was removed from the yaml (spec: real API, no demo). Demo login button only appears when `EXPO_PUBLIC_E2E_DEMO=1`.  
Without a real Auth0 session, M1 fails even if the APK installs.

Options (pick one, do not silently re-enable demo for API calls):

1. Add Maestro steps to tap `Continua con Auth0` and complete the hosted login (credentials in `frontend/.env.e2e` only).
2. Inject a real access token into SecureStore after a resource-owner / device flow (no secrets in git/reports).
3. Keep demo **only** as an auth gate, still pointing `EXPO_PUBLIC_API_URL` at localhost — calendar will 401; M1 headings may still render (`Mese corrente` is static). Later M-steps that need bookings will fail.

### P1 — Expo / RN version drift

`package.json` still says `expo ~52` / `react-native 0.76.3`. Prebuild / node_modules resolved **Expo 54-class** modules (`expo-constants 18`, `expo-modules-core 3.0.30`) and Gradle compiled **react-android-0.81.5**. Do not “upgrade Expo” as a side quest unless the build is stuck on this mismatch.

### P1 — Frontend `develop` is 4 commits behind origin

Rebase/pull before more FE work if you need those commits. Local GJ harness edits must be kept.

### P2 — Do not assert Alloggiati `Inviato`

No Questura test credentials. Skip that assertion.

### P2 — Dirty trees, nothing committed

Do not commit unless asked. Do not commit `mobile/android/` unless the team wants a committed native project. Do not commit `reports/` secrets (there should be none).

Unrelated dirty file: `backend/Sessions/specs/spec-regime-fiscale-2026.md` (pre-existing, not part of this loop).

---

## Next actions (for the new agent)

1. Confirm API / FE / Postgres / AVD still up. Restart API with Auth0 env if health is down. Restart AVD if `adb devices` is empty.
2. Re-apply `adb reverse tcp:5000 tcp:5000` and `tcp:8081 tcp:8081`.
3. Finish the Host debug (or release) build:

```powershell
cd C:\Users\luca.la-malfa\private-project\casazen\mobile
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:JAVA_HOME = "C:\Users\luca.la-malfa\scoop\apps\openjdk17\current"
$env:GRADLE_USER_HOME = "C:\Users\luca.la-malfa\.gradle"
$env:TEMP = "$env:LOCALAPPDATA\Temp"
$env:TMP = $env:TEMP
$env:EXPO_PUBLIC_API_URL = "http://localhost:5000"
$env:EXPO_PUBLIC_E2E_DEMO = "0"
$env:PATH = "$env:JAVA_HOME\bin;$env:ANDROID_HOME\platform-tools;$env:ANDROID_HOME\emulator;$env:PATH"
# Start Metro first if using debug, then:
npx expo run:android
```

4. Confirm `adb shell pm path it.casazen.host` and that the UI is not a red Metro error.
5. Solve host-app Auth0 (see P0 login). Then:

```powershell
$env:MAESTRO_CLI_NO_ANALYTICS = "1"
& "$env:LOCALAPPDATA\maestro\maestro\bin\maestro.bat" test e2e\m1-calendar.yaml
# then m2 … m7; M3 needs BOOKING_ID from the same L3 seed
```

6. If M-suite passes, update `reports/` (new attempt or amend D-M-LIVE) and re-state the loop verdict. Loop outer cap was already 5; treat this as an **env unblock + verification**, not a sixth silent rewrite of the product.

---

## Hard constraints

- Language: generated docs/code comments English; end-user UI Italian.
- No production URLs. No InMemory for GJ evidence.
- No commit / push unless the user asks.
- Do not put Auth0 passwords in `reports/` or this file.
- Prefix shell with `rtk` when it helps; raw commands are OK for debugging.
)
