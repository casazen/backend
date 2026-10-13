# Final verdict — attempt 6 (continued)

STATO: GOAL_PARZIALE

Web L3 and F1–F2 remain PASS. Host Maestro M1–M7 now **exit 0** on `emulator-5554` without `force-stop` and without ANR after the calendar helper waits, then backs out of booking-detail / supplier-form.

| Flow | Exit | Notes |
|---|---|---|
| M1 | 0 | `Calendario` + `Mese corrente` |
| M2 | 0 | Tapped Mario Rossi; `Prenotazione` visible |
| M3 | 0 | `openLink` completed; `Prenotazione\|Calendario` visible |
| M4 | 0 | `Richiedi fornitore` tapped (form opened; no supplier in category) |
| M5 | 0 | Back from form → calendar → booking; status OR matched `Prenotazione` |
| M6 | 0 | `Segna pagato` WARNED (needs Completato SR) |
| M7 | 0 | `Quick check-out` tapped; follow-up copy WARNED |
| ANR | none this pass | `launchApp.stopApp: false` + wait-then-back |
| D-AC3-ADDR | code | Create maps unique/slug conflict to **409**; API process still on old binaries (file lock) |

Not GOAL_RAGGIUNTO: supplier take→complete seed missing (AC9/AC10), Auth0 native PKCE still broken, 409 not loaded in the running API until restart.
