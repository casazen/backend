# Spec — Pricing Adapter Verification (Issue #158 + Prod Smoke)

## Overview

Verificare che il modulo di prezzo adattivo AI (`PricingAdapter`) sia correttamente
rilasciato e funzionante sia in ambiente test che in produzione. Include test di
integrazione backend (Issue #158), test E2E frontend (Playwright), e smoke test
post-deploy del job schedulato Hangfire.

Il backend e il frontend del pricing adapter sono **già implementati** — questa spec
riguarda esclusivamente verifica, test e monitoring, non sviluppo di nuove funzionalità.

Issue di riferimento: **#158** (OPEN — test di integrazione + regressione mancanti)  
Stage di ingresso: **Stage 03 Development** (testing task — nessun design aggiuntivo richiesto)

---

## User Story

Come property owner, voglio che il sistema di prezzo adattivo AI funzioni correttamente
end-to-end: posso abilitarlo, vedo la preview dei prezzi suggeriti per i prossimi 90 giorni,
triggero una sincronizzazione manuale, e verifico la history degli adattamenti.

Come operations team, voglio avere test automatici che verifichino la funzionalità del
pricing adapter ad ogni deploy, così da rilevare regressioni prima che raggiungano produzione.

---

## Acceptance Criteria

### Integration Tests — Backend (Issue #158)

- **AC1**: `POST /api/pricing-adapter/config/{propertyId}` → 201 con config abilitata (`isEnabled: true`)
- **AC2**: `GET /api/pricing-adapter/config/{propertyId}` → 200 con tutti i campi config (`adaptationFrequency`, `includeSeasonality`, `includePublicHolidays`, `nextScheduledRunAt`)
- **AC3**: `DELETE /api/pricing-adapter/config/{propertyId}` → 204; GET successivo → config disabilitata o 404
- **AC4**: `GET /api/pricing-adapter/preview/{propertyId}` → 200 con array di esattamente 90 elementi
- **AC5**: `POST /api/pricing-adapter/sync/{propertyId}` → 202 Accepted con body `{ jobId: "<guid>" }`
- **AC6**: `GET /api/pricing-adapter/history/{propertyId}` → 200 con envelope paginato `{ items, totalCount, page, pageSize }`
- **AC7**: Tutti gli endpoint → 401 senza JWT header
- **AC8**: Tutti gli endpoint su property non di proprietà del caller → 403
- **AC9 (Regressione)**: Body serializzato di qualsiasi risposta non contiene il campo `apiKey` (ricerca case-insensitive su stringa JSON)
- **AC10**: `DynamicPricingJob` — test di isolamento: se l'elaborazione di una property fallisce, il batch continua per le altre (nessuna eccezione non gestita propagata)

### Unit Tests — Backend

- **AC11**: `PricingAdapterService` → coverage ≥ 80% su tutti i metodi pubblici
  - `GetOrCreateConfigAsync` — crea se assente, aggiorna se presente
  - `ComputePreviewAsync` — restituisce 90 items, mai prezzi negativi
  - `TriggerManualSyncAsync` — enqueue corretto su Hangfire
  - `GetHistoryAsync` — paginazione corretta (page/pageSize/totalCount)

### Functional Smoke Tests — Post-Deploy

- **AC12**: `GET {RAILWAY_TEST_URL}/hangfire` accessibile (non 404) → `DynamicPricingJob` visibile come recurring job schedulato `0 2 * * *`
- **AC13**: Sequenza trigger manuale → history: `POST /sync/{id}` → attesa max 30s → `GET /history/{id}` mostra ≥ 1 riga con `syncStatus: "Synced"` (oppure `"Pending"` se OTA non configurate)
- **AC14**: `GET /api/pricing-adapter/preview/{propertyId}` risponde in < 2000 ms (performance contract)
- **AC15**: Nessun errore con livello `Error` o `Critical` in Railway logs per la chiave `DynamicPricingJob` nelle ultime 24h

### E2E Tests — Frontend (Playwright)

- **AC16**: Navigare a `/properties/:id/pricing` → sezione configurazione visibile (titolo "Prezzi AI" o equivalente)
- **AC17**: Toggle "Abilita prezzi AI" (OFF → ON) → config salvata → toast success → badge diventa "Attivo"
- **AC18**: Pulsante "Sincronizza ora" → spinner visibile → toast "Sincronizzazione avviata" → nessun errore console
- **AC19**: Tabella history: almeno 1 riga (se history presente) con colonne data, prezzo precedente, nuovo prezzo, confidenza AI (%)
- **AC20**: Sezione preview: grafico o tabella con ≥ 7 righe di date future + prezzi suggeriti

### Deployment Verification (CI)

- **AC21**: `ci-cd.yml` job `verify-test` include step che verifica il pricing endpoint:
  - `GET {RAILWAY_TEST_URL}/api/pricing-adapter/config/{KNOWN_PROPERTY_ID}` → non 500
- **AC22**: GitHub Actions CI: tutti i test di integrazione passano su `develop` push

---

## Technical Notes

### Backend — File da creare

| File | Azione |
|---|---|
| `Casazen.Tests/Integration/PricingAdapterIntegrationTests.cs` | Creare — WebApplicationFactory + TestAuthHandler |
| `Casazen.Tests/Unit/Services/PricingAdapterServiceTests.cs` | Verificare esistenza + completare coverage |

Pattern test di integrazione:
```csharp
// Usare WebApplicationFactory<Program> con DB Npgsql in-memory o SQLite via EF
// TestAuthHandler: inietta JWT mock con sub e ruolo PropertyOwner
// Seed: creare Property con OwnerId = sub del test user prima di ogni test
```

Pattern test di isolamento `DynamicPricingJob`:
```csharp
// Configurare 3 property: 2 valide, 1 che lancia eccezione nel servizio
// Verificare che la batch elabori tutte e 3 (2 con successo, 1 loggata come errore)
// Verificare che l'eccezione sia loggata (ILogger mock) ma non propagata
```

### Frontend — File da creare

| File | Azione |
|---|---|
| `e2e/pricing-adapter.spec.ts` | Creare — scenari AC16-AC20 |

Prerequisito E2E: property con ID fisso nel DB test (seed o fixture Playwright).

### CI Gate aggiuntivo

Nel job `verify-test` di `ci-cd.yml`, dopo health check esistente:
```yaml
- name: Verify pricing adapter endpoint
  run: |
    STATUS=$(curl -sf -o /dev/null -w "%{http_code}" \
      "${{ vars.RAILWAY_TEST_URL }}/api/pricing-adapter/config/${{ vars.TEST_PROPERTY_ID }}" \
      -H "Authorization: Bearer ${{ secrets.TEST_JWT }}" || echo "000")
    # 200 (config presente) o 404 (property senza config) sono entrambi OK; 500 è failure
    [[ "$STATUS" == "200" || "$STATUS" == "404" ]] || exit 1
```

---

## Stato attuale della codebase

Gli endpoint backend e il frontend sono già implementati e registrati:

| Componente | File | Stato |
|---|---|---|
| Controller | `Casazen.Web/Controllers/PricingAdapterController.cs` | DONE |
| Config entity | `Casazen.Core/Entities/PricingAdapterConfig.cs` | DONE |
| History entity | `Casazen.Core/Entities/PricingHistory.cs` | DONE |
| Job | `Casazen.Web/BackgroundJobs/DynamicPricingJob.cs` | DONE (cron `0 2 * * *`) |
| Service | `Casazen.Infrastructure/Services/PricingAdapterService.cs` | DONE |
| UI Dashboard | `frontend/src/features/pricing/pricing-dashboard-page.tsx` | DONE |
| UI History | `frontend/src/features/pricing/pricing-history-page.tsx` | DONE |

---

## Dependencies

- **Non richiede** modifiche al codice esistente (solo aggiunta test + step CI)
- **Collegato a**: Issue #152 — la property detail page include `PricingAdapterSummary` che referenzia lo stesso backend
- **Sblocca**: Issue #158 chiusura (gates: test scritti + CI verde)
