# Audit — attempt 7

Stack at run: API 200 on `http://localhost:5000` after restart (bookingId filter loaded), Vite 200, Postgres `casazen_dev`, Maestro 2.8.0, AVD `casazen` / `emulator-5554`, `it.casazen.host`, Metro, `adb reverse` 5000/8081.

L3 seed: `bookingId=570e94f3-edff-4d7f-bb86-f803afab25c7`, guest Mario Rossi 22/8–25/8/2026, property `c2a82610-c0e9-4b1e-ba24-ed89692f6bd0`.

## Passed

| Id | Evidence |
|---|---|
| S-WEB | Playwright L3 `steps 1–12 sequential against live API` exit 0; SR created with `bookingId`; unique-date retry after 400 overlap |
| S-F12 | F1–F2 clicked `take-{id}` and `complete-{id}`; GET status `Completato` |
| S-M1 | Maestro M1 exit 0: `Calendario` + `Mese corrente` + `Mario Rossi` (after tapping calendar tab) |
| S-M2 | Deep link booking detail: `Prenotazione` + `Mario Rossi` + `Richiedi fornitore` |
| S-M3 | `casazen://bookings/{BOOKING_ID}` shows `Mario Rossi` + `Prenotazione` |
| S-M4 | `Invia richiesta` then required `.*Richiesto.*` |
| S-M5 | Required `.*Pagato.*` on L3 booking |
| S-M6 | Tapped `Segna pagato` + `Conferma`; `.*Pagato.*` |
| S-M7 | `Check-out rapido` + `Nessun badge critico` + `Avvia check-out` |
| S-AC12 | No ANR / crash dialog this pass |
| L1 bookingId | `ListForHostAsync_WhenBookingIdProvided_ReturnsOnlyMatchingRequests` passed |

## Discrepancies

Empty. Prior attempt-6 gaps (optional Maestro asserts, F1–F2 vacuous pass, SR list ignoring `bookingId`, M4 stale cache, calendar tab vs heading) were fixed in this loop and re-verified.
