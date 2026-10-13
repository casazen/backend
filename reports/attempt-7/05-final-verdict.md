# Final verdict — attempt 7

STATO: GOAL_RAGGIUNTO

Web L3, F1–F2, and host Maestro M1–M7 all passed on the local/dev stack against the same L3 seed (`bookingId` `570e94f3-edff-4d7f-bb86-f803afab25c7`). No BLOCKED remaining.

| Flow | Exit | Notes |
|---|---|---|
| L3 web 1–12 | 0 | Unique address; free booking window; SR `bookingId`; take→complete→Pagato; seed written |
| F1–F2 | 0 | Inbox `Presa in carico` + `Completa`; GET `Completato` |
| M1 | 0 | Calendar tab + `Mario Rossi` |
| M2 | 0 | L3 booking detail |
| M3 | 0 | Deep link `casazen://bookings/{id}` |
| M4 | 0 | `Invia richiesta` → `Richiesto` |
| M5 | 0 | `Pagato` on that booking |
| M6 | 0 | `Segna pagato` + `Pagato` |
| M7 | 0 | `Check-out rapido` + `Nessun badge critico` |
| ANR | none | `launchApp.stopApp: false` |

Product fixes in this loop: service-request list `bookingId` filter; mobile query invalidation / refetch; checkout compliance line; calendar tab navigation in Maestro.
