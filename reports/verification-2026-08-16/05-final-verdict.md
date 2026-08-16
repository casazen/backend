# Final verdict — env unblock verification (2026-08-16)

STATO: GOAL_PARZIALE

This is **not** loop attempt 6. Attempts 1–5 stopped at `GOAL_NON_RAGGIUNTO` solely because Maestro/device was missing. That environmental block is now lifted. Product code was not rewritten for a sixth fidelity pass.

| Item | Result |
|---|---|
| D-M-LIVE | UNBLOCKED — Maestro 2.8.0 executed M1–M7 on `emulator-5554` vs local API |
| M1 | PASS (hard: `Calendario` + `Mese corrente`) |
| M2 | PASS (booking detail `Prenotazione` after guest tap) |
| M3 | Exit 0 (`casazen://bookings/{id}` opened) |
| M4 | PASS (`Richiedi fornitore` on booking detail) |
| M5–M6 | Exit 0; optional supplier-status / mark-paid WARNED (no take→complete seed) |
| M7 | Exit 0; `Quick check-out` tap completed |
| Auth0 native PKCE | Still broken (callback URL mismatch). Session via real web token, `__DEV__` inject only |
| Production | Not touched |
| Secrets | Not written to reports |

How to finish the remaining gap: allowlist the Expo redirect on the Auth0 app, then re-run M2–M7 after opening the seeded booking card and completing supplier take/complete on the same localhost seed. See `04-discrepancy-D-M-LIVE.md`.
