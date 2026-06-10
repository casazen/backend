# Spec — Production E2E Flow Verification (Chrome DevTools)

## Overview

Verifica manuale/automatizzata dei **flussi operativi** su produzione (`https://casazen-app.vercel.app` + `https://casazen-api.up.railway.app`), non solo smoke delle pagine.

Ogni area funzionale richiede **≥ 3 scenari**: almeno 1 happy path e 2 bad path documentati con evidenza (URL, request/response, screenshot/snapshot).

**Tool**: Chrome DevTools MCP (`take_snapshot`, `fill_form`, `click`, `list_network_requests`, `list_console_messages`).

**Utente test**: `luca.lamal@hotmail.it` (La Malfa Luca) — credenziali in `frontend/.env.e2e`.

**Sessione**: 2026-06-09.

---

## Matrice flussi

| Area | Happy path | Bad path 1 | Bad path 2 | Stato prod |
|------|------------|------------|------------|------------|
| Auth login/logout | Login Auth0 → dashboard | Sessione scaduta → 401 API | — | ✅ / ⚠️ |
| Crea proprietà | Form valido → 201 → lista | Form vuoto → validazione Zod | POST valido → 403 `no_org_context` | ❌ bloccato |
| Modifica proprietà | Edit → PUT 200 | — | — | ⏸️ no property |
| Upload documenti/foto | Upload PDF → 201 | File >10MB → 400 | — | ⏸️ no property |
| Prezzo adattivo AI | `/properties/:id/pricing` toggle ON | — | — | ⏸️ no property |
| Crea prenotazione | Form completo → 201 | Form vuoto → validazione | No property in dropdown | ⚠️ parziale |
| Calendario prenotazioni | Vista mese con eventi | — | GET senza params → 404 loop | ❌ |
| Cambio piano (self) | Starter → PUT 200 | No org → 404 + toast | — | ❌ |
| Admin cambio ruolo | PUT `/users/{id}/role` 200 | — | — | ✅ |
| Admin cambio piano | Dialog piano → PATCH org | No orgId → bottone disabilitato | — | ⚠️ |
| Onboarding / org | POST onboarding → org creata | Utente con ruoli → redirect | Admin skip onboarding | ❌ |
| Public booking `/book/:slug` | Landing org pubblica | Slug inesistente → 404 | Non deployato → redirect | ❌ |

---

## Evidenze sessione 2026-06-09

### Proprietà — create (bad + blocked happy)

1. **Bad — validazione client**: submit form vuoto → messaggi Zod ("Name must be at least 3 characters", ecc.). ✅
2. **Happy attempt — dati validi**: `POST /api/properties` → **403** `{"error":"No organization context","code":"no_org_context"}`. UI: toast/errore generico "insufficient permissions", dialog resta aperto. ❌
3. **Root cause**: `GET /api/users/me` → `orgId: null`, `org: null`, `email: ""`. Utente ha ruoli JWT (`Admin`, `PropertyOwner`, `LongTermLandlord`) ma **mai completato onboarding** che chiama `EnsureOrgForUserAsync`.

### Piano — self-service

1. **Happy attempt**: click "Passa a questo piano" (Starter) → `PUT /api/orgs/me/plan` → **404** `No organization assigned to the current user`. Toast: "Impossibile aggiornare il piano". ❌
2. **Bad — entitlement**: `GET /api/orgs/me/entitlement` → 404 (coerente con assenza org).

### Prenotazione — create

1. **Bad — validazione**: submit vuoto → "Property is required", date/guest errors. ✅
2. **Bad — no inventory**: dropdown proprietà vuoto (0 properties). ⏸️
3. **Happy**: non testabile finché create property bloccato.

### Calendario

1. FE: `GET /api/bookings/calendar` **senza** `propertyId`, `startDate`, `endDate`.
2. BE: endpoint richiede `propertyId` (Guid) — con Guid vuoto → `404 Property not found`.
3. React Query ritenta → UI bloccata su "Loading calendar...". ❌

### Admin — utenti

1. **Happy — cambio ruolo**: dialog → PropertyOwner → `PUT /api/users/auth0|…/role` **200**, toast "Ruolo aggiornato con successo". ✅
2. **Bad — UI dati**: tabella mostra "Admin" / email "—"; dialog "Seleziona il nuovo ruolo per ." (nome vuoto). ❌
3. **Bad — cambio piano**: bottone "Piano" **disabled** (entrambi utenti `orgId: null`).

### Onboarding / Modifica tipo

1. Link profilo "Modifica tipo" → `/onboarding` → redirect immediato a `/app/admin` (utente ha ruoli). ❌
2. `needsOnboarding()` ritorna `false` per Admin e per `roles.length > 0` — **nessun percorso UI** per creare org retroattivamente.

### Public booking (issue #215)

1. `https://casazen-app.vercel.app/book/test-org` → redirect `/app/choose-context`.
2. `GET /api/public/orgs/test-org` → 404.
3. Feature non deployata su prod FE/BE.

---

## Gate di uscita

- [ ] Utente test può completare onboarding o backfill org
- [ ] Create property → 201 su prod
- [ ] Edit property + upload documento su property esistente
- [ ] Pricing adapter accessibile da detail
- [ ] Create booking end-to-end
- [ ] Calendario carica senza spinner infinito
- [ ] Cambio piano self-service funziona
- [ ] Public booking route risponde (post-deploy #215)

---

## Bug aperti (GitHub)

Vedi issues create nella sessione 2026-06-09 con label `e2e-verification`.
