# Test plan — attempt 7

Environment: local only. API `http://localhost:5000`, frontend `http://localhost:5173`, Postgres `casazen_dev`, AVD `casazen` / `emulator-5554`. No production URLs. No InMemory.

Shared seed: Playwright L3 writes `frontend/e2e/.auth/gj-seed.json` (`bookingId`, `serviceRequestId`, `propertyId`, `supplierOrgId`, `guestName`). Maestro M2–M7 must pass that `BOOKING_ID` via `cmd.exe` (`-e`).

## Commands

```powershell
cd C:\Users\luca.la-malfa\private-project\casazen\frontend
$env:E2E_LOCAL = "1"
npx playwright test --project=local e2e/golden-journey-web.spec.ts -g "sequential against live API"
npx playwright test --project=local e2e/golden-journey-supplier-mobile.spec.ts

$seed = Get-Content e2e\.auth\gj-seed.json | ConvertFrom-Json
$bid = $seed.bookingId
cd C:\Users\luca.la-malfa\private-project\casazen\mobile
$env:MAESTRO_CLI_NO_ANALYTICS = "1"
$maestro = "$env:LOCALAPPDATA\maestro\maestro\bin\maestro.bat"
foreach ($f in @('m1-calendar.yaml','m2-booking-detail.yaml','m3-push.yaml','m4-create-service-request.yaml','m5-service-status.yaml','m6-mark-paid.yaml','m7-checkout.yaml')) {
  cmd /c "`"$maestro`" test -e BOOKING_ID=$bid e2e\$f"
}
```

## Pass / fail

| Id | Pass |
|---|---|
| S-WEB | L3 exit 0; SR created with `bookingId`; no API 500 |
| S-F12 | F1–F2 clicks take + complete; GET status `Completato` |
| S-M1 | Calendar shows `Mario Rossi` for the current month |
| S-M2 | Deep link / booking detail shows `Mario Rossi` + `Richiedi fornitore` |
| S-M3 | `casazen://bookings/{BOOKING_ID}` shows `Mario Rossi` |
| S-M4 | `Invia richiesta` then required `Richiesto` |
| S-M5 | Required `Pagato` on that booking |
| S-M6 | `Pagato` still visible (optional Segna pagato if Completato) |
| S-M7 | `Check-out rapido` + `Nessun badge critico` |
| S-AC12 | 0 ANR / crash dialogs |
