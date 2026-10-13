# Fix plan — attempt 6 (dev/review loop)

Dependency order. Product before harness.

1. **D-AC9-PICKER** (backend) — `GetActiveByComune` drops Active suppliers with empty `CategoriesJson`, so M4 shows "Nessun fornitore disponibile". Empty categories must match any category (activation allows categories later).
2. **D-AC9-SEED** (frontend L3) — register used a mailinator org that stays `Pending`. Link/activate the authenticated host as supplier (`PUT /api/supplier/profile` + `POST .../activation/complete`), then assert SR **201** `Richiesto`, take → complete → mark-paid. Persist `bookingId` for Maestro.
3. **D-AC10** (frontend F1–F2 + Maestro) — after the seed, inbox take/complete and M5–M6 can see `PresoInCarico`/`Completato`/`Pagato`.
4. **D-AC3-ADDR** (backend, already patched) — 409 on unique address; load via API restart + unit tests.
5. **D-M-ANR** (mobile, already patched) — wait-then-back helper; do not reopen.
