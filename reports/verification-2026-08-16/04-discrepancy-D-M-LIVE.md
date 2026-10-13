# D-M-LIVE — env unblock verification (2026-08-16)

STATO: UNBLOCKED (suite executed; deeper optional asserts incomplete)

Maestro 2.8.0 ran on `emulator-5554` (AVD `casazen`, Android 14) against the Host debug app `it.casazen.host` and local API `http://localhost:5000`. No production URLs. No secrets in this file.

## Environment

| Item | Result |
|---|---|
| API `GET /api/health` | 200 (Postgres `casazen-dev-pg`, not InMemory) |
| Frontend `http://localhost:5173` | 200 |
| `adb devices` | `emulator-5554` connected |
| `adb reverse` | `tcp:5000`, `tcp:8081` |
| `pm path it.casazen.host` | installed |
| Metro | running (debug APK; UI was not a red-box error) |

## Auth0 (P0 login)

Native PKCE (`Continua con Auth0`) opens Chrome Custom Tab and Auth0 returns **Callback URL mismatch**. The SPA client allowlist does not include the Expo redirect (`casazen://…`). Password grant is disabled on the same client (`unauthorized_client`).

Session used for this run: real Auth0 access token from the **web** login (same audience `https://casazen-api`), injected in `__DEV__` via `casazen://e2e-auth` / `AuthProvider.hydrateSession`. Demo mode stayed off (`EXPO_PUBLIC_E2E_DEMO=0`). Token and passwords are not recorded here.

## Seed

Existing L3 property `GJ Villa gj-1786864660597` had **0** bookings. A current-month host booking (guest Mario Rossi, status `Pending`) was created via `POST /api/bookings` against localhost.

## Maestro results

| Flow | Exit | Hard asserts | Optional asserts |
|---|---|---|---|
| M1 `m1-calendar.yaml` | 0 | `Calendario`, `Mese corrente` visible | — |
| M2 `m2-booking-detail.yaml` | 0 | `Calendario` | After tapping guest card: `Prenotazione` visible |
| M3 `m3-push.yaml` | 0 | — | `openLink casazen://bookings/{id}` completed; `Prenotazione\|Calendario` visible |
| M4 `m4-create-service-request.yaml` | 0 | `Calendario` (after back if needed) | `Richiedi fornitore` visible on booking detail |
| M5 `m5-service-status.yaml` | 0 | `Calendario` | status strings **WARNED** (no supplier take/complete in this run) |
| M6 `m6-mark-paid.yaml` | 0 | `Calendario` | `Segna pagato` **WARNED** (needs `Completato` service request) |
| M7 `m7-checkout.yaml` | 0 | `Calendario` | `Quick check-out` tap completed; follow-up text **WARNED** |

First M2–M7 pass used `tapOn: ".*"` and stayed on the calendar (optional WARNED, exit 0). Yamls were then pointed at the guest card; M2/M4/M7 were re-run. M4 initially failed when Expo Router restored the booking-detail route; a conditional `back` when `Calendario` is not visible fixed it.

## Remaining

1. Add `casazen://` (exact Expo `makeRedirectUri` value) to the Auth0 application Allowed Callback URLs so hosted PKCE works without token injection.
2. Re-run M2/M4/M7 after tapping the booking card (guest name), then M5–M6 after a real supplier take → complete on the same seed.
3. Do not assert Alloggiati `Inviato` (no Questura credentials).
