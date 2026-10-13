# Spec — Pricing Adapter Verification (Issue #158 + Prod Smoke)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

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

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Come property owner, voglio che il sistema di prezzo adattivo AI funzioni correttamente
end-to-end: posso abilitarlo, vedo la preview dei prezzi suggeriti per i prossimi 90 giorni,
triggero una sincronizzazione manuale, e verifico la history degli adattamenti.

Come operations team, voglio avere test automatici che verifichino la funzionalità del
pricing adapter ad ogni deploy, così da rilevare regressioni prima che raggiungano produzione.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Integration Tests — Backend (Issue #158)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

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

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC11**: `PricingAdapterService` → coverage ≥ 80% su tutti i metodi pubblici
  - `GetOrCreateConfigAsync` — crea se assente, aggiorna se presente
  - `ComputePreviewAsync` — restituisce 90 items, mai prezzi negativi
  - `TriggerManualSyncAsync` — enqueue corretto su Hangfire
  - `GetHistoryAsync` — paginazione corretta (page/pageSize/totalCount)

### Functional Smoke Tests — Post-Deploy

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC12**: `GET {RAILWAY_TEST_URL}/hangfire` accessibile (non 404) → `DynamicPricingJob` visibile come recurring job schedulato `0 2 * * *`
- **AC13**: Sequenza trigger manuale → history: `POST /sync/{id}` → attesa max 30s → `GET /history/{id}` mostra ≥ 1 riga con `syncStatus: "Synced"` (oppure `"Pending"` se OTA non configurate)
- **AC14**: `GET /api/pricing-adapter/preview/{propertyId}` risponde in < 2000 ms (performance contract)
- **AC15**: Nessun errore con livello `Error` o `Critical` in Railway logs per la chiave `DynamicPricingJob` nelle ultime 24h

### E2E Tests — Frontend (Playwright)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC16**: Navigare a `/properties/:id/pricing` → sezione configurazione visibile (titolo "Prezzi AI" o equivalente)
- **AC17**: Toggle "Abilita prezzi AI" (OFF → ON) → config salvata → toast success → badge diventa "Attivo"
- **AC18**: Pulsante "Sincronizza ora" → spinner visibile → toast "Sincronizzazione avviata" → nessun errore console
- **AC19**: Tabella history: almeno 1 riga (se history presente) con colonne data, prezzo precedente, nuovo prezzo, confidenza AI (%)
- **AC20**: Sezione preview: grafico o tabella con ≥ 7 righe di date future + prezzi suggeriti

### Deployment Verification (CI)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC21**: `ci-cd.yml` job `verify-test` include step che verifica il pricing endpoint:
  - `GET {RAILWAY_TEST_URL}/api/pricing-adapter/config/{KNOWN_PROPERTY_ID}` → non 500
- **AC22**: GitHub Actions CI: tutti i test di integrazione passano su `develop` push

---


## UX / UI Quality



**Required** (Frontend ACs present). Testable bar for Stage 03.



| Criterion | Required | How to verify |

|---|---|---|

| Primary path clear | User completes happy path without guessing | L3 scripted flow below |

| Language | End-user strings Italian | L2/L3 assert Italian primary labels |

| Empty state | No blank dead-end when data length = 0 | L2 empty fixture |

| Error state | 4xx/5xx as human Italian message | L2/L3 forced error |

| Destructive / legal copy | Confirmations/disclaimers as in ACs | Assert documented phrases |



**Happy-path script:**



1. Enter the primary route for `pricing-adapter-verification`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | `POST /api/pricing-adapter/config/{propertyId}` → 201 con config abilitata (`isEnabled: true`) | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | `GET /api/pricing-adapter/config/{propertyId}` → 200 con tutti i campi config (`adaptationFrequency`, `includeSeasonality`, `includePubli... | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | `DELETE /api/pricing-adapter/config/{propertyId}` → 204; GET successivo → config disabilitata o 404 | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | `GET /api/pricing-adapter/preview/{propertyId}` → 200 con array di esattamente 90 elementi | Outcome not met; wrong status; silent no-op |
| AC5 | L1 + L2 + L3 | `POST /api/pricing-adapter/sync/{propertyId}` → 202 Accepted con body `{ jobId: "<guid>" }` | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC6 | L1 + L2 + L3 | `GET /api/pricing-adapter/history/{propertyId}` → 200 con envelope paginato `{ items, totalCount, page, pageSize }` | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC7 | L1 | Tutti gli endpoint → 401 senza JWT header | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | Tutti gli endpoint su property non di proprietà del caller → 403 | Outcome not met; wrong status; silent no-op |
| AC9 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC10 | L1 | `DynamicPricingJob` — test di isolamento: se l'elaborazione di una property fallisce, il batch continua per le altre (nessuna eccezione n... | Outcome not met; wrong status; silent no-op |
| AC11 | L1 | `PricingAdapterService` → coverage ≥ 80% su tutti i metodi pubblici | Outcome not met; wrong status; silent no-op |
| AC12 | L1 | `GET {RAILWAY_TEST_URL}/hangfire` accessibile (non 404) → `DynamicPricingJob` visibile come recurring job schedulato `0 2 * * *` | Outcome not met; wrong status; silent no-op |
| AC13 | L1 | Sequenza trigger manuale → history: `POST /sync/{id}` → attesa max 30s → `GET /history/{id}` mostra ≥ 1 riga con `syncStatus: "Synced"` (... | Outcome not met; wrong status; silent no-op |
| AC14 | L1 | `GET /api/pricing-adapter/preview/{propertyId}` risponde in < 2000 ms (performance contract) | Outcome not met; wrong status; silent no-op |
| AC15 | L1 | Nessun errore con livello `Error` o `Critical` in Railway logs per la chiave `DynamicPricingJob` nelle ultime 24h | Outcome not met; wrong status; silent no-op |
| AC16 | L2 + L3 | Navigare a `/properties/:id/pricing` → sezione configurazione visibile (titolo "Prezzi AI" o equivalente) | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC17 | L1 | Toggle "Abilita prezzi AI" (OFF → ON) → config salvata → toast success → badge diventa "Attivo" | Outcome not met; wrong status; silent no-op |
| AC18 | L1 | Pulsante "Sincronizza ora" → spinner visibile → toast "Sincronizzazione avviata" → nessun errore console | Outcome not met; wrong status; silent no-op |
| AC19 | L1 | Tabella history: almeno 1 riga (se history presente) con colonne data, prezzo precedente, nuovo prezzo, confidenza AI (%) | Outcome not met; wrong status; silent no-op |
| AC20 | L1 | Sezione preview: grafico o tabella con ≥ 7 righe di date future + prezzi suggeriti | Outcome not met; wrong status; silent no-op |
| AC21 | L1 | `ci-cd.yml` job `verify-test` include step che verifica il pricing endpoint: | Outcome not met; wrong status; silent no-op |
| AC22 | L1 | GitHub Actions CI: tutti i test di integrazione passano su `develop` push | Outcome not met; wrong status; silent no-op |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — File da creare

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

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

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Azione |
|---|---|
| `e2e/pricing-adapter.spec.ts` | Creare — scenari AC16-AC20 |

Prerequisito E2E: property con ID fisso nel DB test (seed o fixture Playwright).

### CI Gate aggiuntivo

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

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

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

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

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Non richiede** modifiche al codice esistente (solo aggiunta test + step CI)
- **Collegato a**: Issue #152 — la property detail page include `PricingAdapterSummary` che referenzia lo stesso backend
- **Sblocca**: Issue #158 chiusura (gates: test scritti + CI verde)

## Test expectations (process contract)



| Layer | Allowed | Forbidden as sole proof |

|---|---|---|

| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |

| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |

| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |



Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

## Regulatory / Legal Gates

- None

## Out of Scope

- See Acceptance Criteria non-goals / PLANNING freeze list

## Open Questions

- None (or list with owner/date before Stage 03)
