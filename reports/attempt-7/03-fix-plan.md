# Fix plan — attempt 7 (dev/review loop)

Dependency order used this attempt:

1. **D-AC10-LIST** (backend) — `GET /api/service-requests` ignored mobile `bookingId`, so booking detail could not show the L3 SR.
2. **D-AC9-SEED** (frontend L3) — create SR with `bookingId`; retry booking dates when the window is taken.
3. **D-AC13** (frontend F1–F2) — create Richiesto then required take + complete on inbox; GET `Completato`.
4. **D-AC6-SEED** (mobile Maestro) — M2–M7 open `casazen://bookings/${BOOKING_ID}`; tap Calendario tab so M1 is not the properties list.
5. **D-AC9-CACHE** (mobile) — invalidate `service-requests` after create; `refetchOnMount: 'always'` on booking detail.
6. **D-AC11** (mobile) — checkout screen shows `Nessun badge critico`.
