# Test plan — attempt 6

Environment: local only. API `http://localhost:5000`, frontend `http://localhost:5173`, Postgres `casazen_dev`, AVD `casazen` / `emulator-5554`. No production URLs. No InMemory.

Shared seed: Playwright L3 unique `gj-{timestamp}` emails/slugs/addresses. Maestro must reuse the same booking when asserting M2–M7.

## Commands

```powershell
# Web 1–12 + F1–F2 (Auth0 from frontend/.env.e2e, never copy secrets into reports)
cd C:\Users\luca.la-malfa\private-project\casazen\frontend
$env:E2E_LOCAL = "1"
npx playwright test --project=local e2e/golden-journey-web.spec.ts e2e/golden-journey-supplier-mobile.spec.ts

# Host app M1–M7
cd C:\Users\luca.la-malfa\private-project\casazen\mobile
$env:MAESTRO_CLI_NO_ANALYTICS = "1"
& "$env:LOCALAPPDATA\maestro\maestro\bin\maestro.bat" test e2e\m1-calendar.yaml
# then m2 … m7; M3 needs BOOKING_ID from the L3 seed
```

## Pass / fail

| Id | Pass |
|---|---|
| S-WEB | L3 `steps 1–12 sequential against live API` exit 0; no API 500 |
| S-F12 | F1–F2 supplier mobile spec exit 0 |
| S-M1 | Maestro M1: `Calendario` + `Mese corrente` |
| S-M2–M7 | Maestro M2–M7 exit 0 against the same booking seed |
| S-AC12 | 0 ANR / crash dialogs during the suite |
