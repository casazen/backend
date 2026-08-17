# Audit — attempt 6

Stack at run: API 200, Vite 200, Postgres `casazen_dev`, Maestro 2.8.0, AVD `casazen` connected, `it.casazen.host` installed, Metro running, `adb reverse` 5000/8081.

## Passed

| Id | Evidence |
|---|---|
| S-WEB | Playwright L3 `steps 1–12 sequential against live API` passed after unique-address harness fix. Property `GJ Villa gj-1786887496057` in Postgres. |
| S-F12 | `F1–F2 inbox take and complete on phone viewport` passed (same Playwright run; 3 passed / 3 L2 skipped). |
| S-M1 (once) | First `maestro test e2e\m1-calendar.yaml` exit 0: launch + `Calendario` + `Mese corrente`. |

## Discrepancies

### D-AC3-ADDR

- **spec:** AC3 / AC5 — no API 500; idempotent unique seed
- **observed:** `POST /api/properties` returned 500 (`InternalServerError`, trace `0HNNRCE9C0Q6G:00000004`) when address/city/postal matched an existing active property. Unique index `Properties (Address, City, PostalCode, IsActive)`. L3 reused `Via Roma 10` / `Roma` / `00100`.
- **evidence:** L3 failure body; `\d` uniqueness in `AppDbContext`; only one row until address was uniqued. After `address: Via Roma ${run}` the create succeeded.
- **severity:** major (product still 500 on duplicate address; harness workaround unblocks GJ)

### D-M-ANR

- **spec:** AC6 live M1–M7; AC12 0 crashes
- **observed:** After a passing M1, M2/M4/M5/M6/M7 failed. Hierarchy showed Android ANR `CasaZen Host isn't responding` (Close app / Wait). M3 exit 0 with optional assert WARN (`BOOKING_ID` unset). Force-stop + M1 retry then failed (`Calendario` not visible; hierarchy had no calendar copy).
- **evidence:** Maestro logs `2026-08-16_151840` … `_153159`; retry `_154027`
- **severity:** blocker

### D-M3-SEED

- **spec:** AC8 M3 deep link opens the L3 booking
- **observed:** `openLink: casazen://bookings/${BOOKING_ID}` with unset env; optional `Prenotazione|Calendario` warned
- **severity:** major
