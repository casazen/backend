# Test case manuale — Tenant boundary (Org + OrgId + entitlement) · v1.1.6

> **Issue**: #202 · **Release**: v1.1.6 · **Spec**: `Sessions/specs/spec-tenant-boundary.md`
> **Cosa verifica**: ogni utente appartiene a un'**Org**, i dati (proprietà/prenotazioni/affitti/pagamenti) sono isolati per Org, e il piano **Starter** limita le nuove proprietà (default **3**).

---

## 1. Pre-requisiti

| # | Requisito |
|---|---|
| P1 | Utente Auth0 con ruolo **`PropertyOwner`** e contesto **short-rent** attivo (permessi `property.read` / `property.write`). |
| P2 | (Opzionale, per TC-05) Secondo utente su **Org diversa**, con almeno una proprietà. |
| P3 | (Opzionale, per TC-08) Utente con ruolo **`AdminOnly`**. |
| P4 | Browser + DevTools (Network) **oppure** Postman/curl con token JWT. |

### Ambienti

| Ambiente | Frontend | Backend API |
|---|---|---|
| **Produzione** (consigliato post-release) | https://casazen-app.vercel.app | https://casazen-api.up.railway.app |
| **Staging / test** | deploy Vercel branch `develop` | https://casazen-api-test.up.railway.app |

### Ottenere il token JWT

1. Accedi all'app con Auth0.
2. DevTools → **Network** → filtra `users/me` o qualsiasi chiamata `/api/`.
3. Copia l'header `Authorization: Bearer <token>`.

Oppure, in console (se esposto dall'SDK Auth0):

```javascript
// Esempio — adatta al tuo hook Auth0
const token = await getAccessTokenSilently();
console.log(token);
```

Per i test API sostituisci:

```bash
export API="https://casazen-api.up.railway.app"
export TOKEN="<incolla JWT>"
```

---

## 2. Matrice test (riepilogo)

| ID | Area | Priorità | Tipo |
|---|---|---|---|
| TC-01 | Health + auth gate | Alta | API |
| TC-02 | Org sull'utente corrente | Alta | API + UI |
| TC-03 | Badge org + piano in header | Alta | UI |
| TC-04 | Entitlement (limiti piano) | Alta | API + UI |
| TC-05 | Regression: dati pre-esistenti | Alta | UI |
| TC-06 | Isolamento cross-org (IDOR) | Alta | API |
| TC-07 | Blocco creazione oltre limite Starter | Media | UI |
| TC-08 | Admin: statistiche cross-org | Bassa | API (solo Admin) |

---

## TC-01 — Health e protezione endpoint

**Obiettivo**: confermare che l'API è su e che i nuovi endpoint richiedono auth.

| Step | Azione | Risultato atteso |
|:---:|---|---|
| 1 | `GET /api/health` **senza** token | **200**, JSON con `"status":"healthy"` |
| 2 | `GET /api/orgs/me/entitlement` **senza** token | **401** |
| 3 | `GET /api/users/me` **senza** token | **401** |

**curl (step 2)**:

```bash
curl -s -o /dev/null -w "%{http_code}\n" "$API/api/orgs/me/entitlement"
# Atteso: 401
```

---

## TC-02 — Org sull'utente corrente (`GET /api/users/me`)

**Obiettivo**: AC9 — l'utente autenticato ha `orgId` e oggetto `org`.

| Step | Azione | Risultato atteso |
|:---:|---|---|
| 1 | `GET /api/users/me` con token | **200** |
| 2 | Ispeziona JSON | Campi presenti: `orgId` (Guid), `org: { id, name, slug, planTier }` |
| 3 | Verifica `planTier` | Uno tra `"Starter"`, `"Pro"`, `"Scale"` (utenti migrati → di default **Starter**) |

**curl**:

```bash
curl -s "$API/api/users/me" -H "Authorization: Bearer $TOKEN" | jq '{orgId, org}'
```

**Pass**: `orgId` non null e `org.name` valorizzato.

---

## TC-03 — Badge org + piano in header (UI)

**Obiettivo**: AC11 — la console mostra nome org e badge piano.

| Step | Azione | Risultato atteso |
|:---:|---|---|
| 1 | Login su https://casazen-app.vercel.app | Redirect alla console `/app/...` |
| 2 | Guarda l'**header** in alto | Visibile **nome organizzazione** + badge piano (Starter / Pro / Scale) |
| 3 | Ricarica pagina (F5) | Badge ancora presente (nessun flash permanente vuoto) |

**Pass**: nome org leggibile; badge coerente con `planTier` di TC-02.

**Fail tipico**: header senza org → controlla TC-02 (`org` null = utente senza backfill Org).

---

## TC-04 — Entitlement (`GET /api/orgs/me/entitlement`)

**Obiettivo**: AC8 — il server espone limiti e uso corrente.

| Step | Azione | Risultato atteso |
|:---:|---|---|
| 1 | `GET /api/orgs/me/entitlement` con token | **200** |
| 2 | Ispeziona JSON | `{ orgId, planTier, limits: { maxProperties }, usage: { properties }, canAddProperty }` |
| 3 | Confronta | `usage.properties` = numero proprietà che vedi in lista; `limits.maxProperties` = **3** se Starter |
| 4 | Coerenza | Se `usage.properties < limits.maxProperties` → `canAddProperty: true`, altrimenti `false` |

**curl**:

```bash
curl -s "$API/api/orgs/me/entitlement" \
  -H "Authorization: Bearer $TOKEN" | jq .
```

---

## TC-05 — Regression: dati pre-migrazione ancora visibili

**Obiettivo**: AC10 — niente dati persi dopo la migrazione Org.

| Step | Azione | Risultato atteso |
|:---:|---|---|
| 1 | Vai a **Proprietà** (`/app/short-rent/properties`) | Elenco uguale a **prima** del release (stesse unità) |
| 2 | Apri il dettaglio di una proprietà nota | Dati intatti (nome, indirizzo, CIN, ecc.) |
| 3 | (Se presenti) Controlla **Prenotazioni** e **Pagamenti** | Stessi record di prima, nessun elenco vuoto inspiegabile |

**Pass**: zero regressioni visibili; conteggio proprietà = `usage.properties` di TC-04.

---

## TC-06 — Isolamento cross-org (IDOR)

**Obiettivo**: AC7 — un utente **non** vede i dati di un'altra Org.

> Serve il **Guid** di una proprietà appartenente a **un altro** account (chiedi a un collega o usa un secondo tenant di test).

| Step | Azione | Risultato atteso |
|:---:|---|---|
| 1 | Con **Utente A**, `GET /api/properties` | Solo proprietà della propria Org |
| 2 | Con **Utente A**, `GET /api/properties/{id-altrui}` (id di Utente B) | **404** (non 200 con dati altrui) |
| 3 | Con **Utente A**, `GET /api/bookings/{id-altrui}` se disponibile | **404** o lista vuota |

**curl (step 2)**:

```bash
curl -s -o /dev/null -w "%{http_code}\n" \
  "$API/api/properties/<GUID-ALTRA-ORG>" \
  -H "Authorization: Bearer $TOKEN_UTENTE_A"
# Atteso: 404
```

**Pass**: mai JSON con dati di un'altra org; sempre 404 o array vuoto.

**Fail critico**: 200 con proprietà/prenotazione di un altro cliente → segnala come bug IDOR.

---

## TC-07 — Limite piano Starter (creazione proprietà)

**Obiettivo**: AC8 + AC12 — oltre il limite (default **3** proprietà Starter) la creazione è bloccata con messaggio italiano.

### Pre-condizione

- `planTier = Starter` (TC-02)
- `usage.properties >= 3` **oppure** crea proprietà fino ad arrivare a 3

| Step | Azione | Risultato atteso |
|:---:|---|---|
| 1 | Vai a **Nuova proprietà** (`/app/short-rent/properties/create`) | Form visibile se sotto limite |
| 2 | Se già a 3 proprietà, prova a crearne una **4ª** e invia | **Errore UI** in italiano: *"Hai raggiunto il limite del tuo piano"* |
| 3 | Verifica link/CTA upgrade | Punta verso `/app/billing/upgrade` (pagina upgrade arriverà con `spec-saas-billing`; oggi può essere 404 — accettabile) |
| 4 | (DevTools) Ispeziona risposta `POST /api/properties` | **403** o **409** con body `code: "plan_limit_reached"` |

**Pass**: blocco lato server + messaggio italiano in UI.

---

## TC-08 — Admin: statistiche piattaforma (solo Admin)

**Obiettivo**: verificare fix review F-H1 — l'admin vede dati **cross-org**, non zero.

> Solo se hai un utente **AdminOnly**.

| Step | Azione | Risultato atteso |
|:---:|---|---|
| 1 | Login come Admin | Accesso area admin |
| 2 | Apri dashboard stats / CIN compliance (endpoint admin esistenti) | Numeri **> 0** se in piattaforma ci sono proprietà/booking |
| 3 | Confronto | Non tutti zeri/vuoti (regressione pre-fix) |

**Fail tipico post-bug**: admin con `OrgId` null vede **0** proprietà totali.

---

## 3. Checklist rapida (5 minuti)

Usa questa se hai poco tempo dopo un deploy:

- [ ] TC-01 step 2 → `/api/orgs/me/entitlement` = **401** senza token
- [ ] TC-02 → `/api/users/me` contiene `org` + `orgId`
- [ ] TC-03 → header mostra nome org + badge piano
- [ ] TC-05 → le tue proprietà ci sono ancora tutte
- [ ] TC-06 (se hai 2 utenti) → id altrui = **404**

---

## 4. Test automatici (già in CI)

Se vuoi ripetere in locale ciò che ha validato la pipeline:

```bash
# Backend (repo backend, branch develop/main)
dotnet test --filter "FullyQualifiedName~TenantBoundary|FullyQualifiedName~Entitlement|FullyQualifiedName~AdminService"

# Frontend (repo frontend)
npm test -- org-badge entitlement-error
npm run test:e2e -- tenant-boundary
```

---

## 5. Criteri di uscita

| Esito | Condizione |
|---|---|
| **PASS** | TC-01, TC-02, TC-03, TC-05 passano; TC-06 passa se testabile; TC-07 passa se account Starter con ≥3 proprietà |
| **PASS con riserva** | TC-07 non testato (account Pro/Scale o <3 proprietà) — documentare "non applicabile" |
| **FAIL** | TC-05 dati mancanti; TC-06 IDOR (200 su id altrui); TC-02 `org` null su utente con proprietà |

---

## 6. Segnalazione bug

Apri issue su GitHub con:

- Ambiente (prod / test)
- TC-ID fallito
- Utente (solo email, no password)
- Request/response (sanitizzati) o screenshot
- Timestamp approssimativo

Riferimento release: **v1.1.6** · Issue **#202**.
