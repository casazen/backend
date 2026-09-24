# Protocollo agente di risanamento CasaZen

Risolvi **UN SOLO task** del piano di risanamento. Non toccare nulla fuori dal perimetro del tuo task.

## Contesto
- Repo principali: `/home/user/backend` (.NET 10, EF Core + PostgreSQL), `/home/user/frontend` (React + Vite + TS), `/home/user/mobile` (Expo). **Non modificarli mai direttamente**: sono il branch di integrazione `claude/app-analysis-fixes-plan-p0mx0a`. Lavora solo nel tuo worktree.
- Piano e registro task: `/home/user/backend/Sessions/risanamento/PIANO-ESECUZIONE.md` e `tasks.json` (stessa cartella).
- **Decisioni vincolanti** del product owner: `/home/user/backend/Sessions/risanamento/DECISIONI.md`.
- Difetti: `/home/user/backend/Sessions/audit-2026-09-23/*.md`. Cerca gli ID del tuo task (es. `grep -rn "A2-01" /home/user/backend/Sessions/audit-2026-09-23/`) per descrizione, file:riga, scenario e fix proposto. I numeri di riga possono essere cambiati: verifica sul codice attuale.
- Regole di progetto: `/home/user/backend/.claude/rules/*.md`. Contesto normativo: `/home/user/backend/.claude/context/regulations/`.

## Passi
1. `source /home/user/wt/bin/env.sh && /home/user/wt/bin/start-task.sh <TASK> <repo...>` crea `/home/user/wt/<TASK>/<repo>` sul branch `fix/<TASK>`. Lavora solo lì. Se ti serve un repo in più, aggiungilo con lo stesso comando.
2. Leggi i difetti del task e il codice attuale. Verifica che il problema esista ancora: altri task o PR potrebbero averlo già risolto. In quel caso documentalo ed esci con esito `GIA_RISOLTO`.
3. Implementa una correzione **completa e minimale** per il perimetro del task, senza refactoring estranei.
   - **Niente stub che dichiarano successo.**
   - **Niente dati normativi, aliquote, codici o testi legali inventati.** Se servono e non sono in `.claude/context/regulations/` o in fonti ufficiali verificate, rendili configurabili o disattivati e segnalalo in DUBBI.
   - **Dubbi di prodotto** non coperti da `DECISIONI.md`: non scegliere a caso. Implementa la parte non ambigua, lascia il resto e riportalo in DUBBI.
   - **i18n:** ogni stringa FE via `t()` con chiavi in `it.json` **e** `en.json`, senza `defaultValue`. Messaggi BE nuovi via `IStringLocalizer` e `.resx` IT/EN. Se l'infrastruttura di localizzazione non funziona ancora, usa un codice errore stabile più il messaggio in italiano e segnalalo.
   - **FE:** stati di caricamento, errore e vuoto. Un errore API non si mostra mai come "lista vuota".
   - **Sicurezza:** niente segreti, niente PII o token nei log, filtro tenant/ownership su ogni query.
   - **Date:** `DateTime` sempre UTC; date di soggiorno senza ora; "oggi" calcolato in Europe/Rome.
   - **Migrazioni EF:** solo con `dotnet ef migrations add <Nome> --project Casazen.Infrastructure --startup-project Casazen.Web`, mai scritte a mano. Se al merge confliggono snapshot o migrazioni: elimina la tua migrazione, completa il merge, rigenerala.
   - **Configurazioni esterne** (Auth0, Railway, Vercel, Supabase, Stripe, Resend, EAS/FCM): scrivi il codice e documenta i passi in `backend/docs/runbooks/<tema>.md` (crea o aggiorna).
   - **Test:** aggiungi o aggiorna test che dimostrano la fix (nomi `MethodName_Scenario_ExpectedBehavior`). Un test esistente che codifica il bug va corretto. Niente test che verificano solo "è visibile".
   - **Dipendenze npm:** `node_modules` nel worktree è un symlink condiviso, non usare `npm install` lì. Se devi aggiungere un pacchetto: `rm node_modules && npm ci && npm install <pkg>` dentro il worktree.
4. **Verifica obbligatoria nel worktree.** Tutto deve essere verde, senza nuovi warning del compilatore.
   - backend: `dotnet build Casazen.sln -c Release`, `dotnet test Casazen.sln -c Release`, `dotnet format --verify-no-changes`.
   - frontend: `npx tsc -b --noEmit`, `npx eslint <file toccati>`, `npx vitest run`, `npx vite build`.
   - mobile: `npx tsc --noEmit`, più lint se configurato.
   - Se serve l'API locale: usa una porta libera tra 5100 e 5199 e un database dedicato (`createdb -h localhost -U postgres wt_<task>`, password `dev`). Elimina il database alla fine.
5. **Commit nel worktree** in formato Conventional Commits, con scope = ID del task (es. `fix(PC-01): keep host bookings confirmed`).
   - Corpo: cosa e perché, più gli ID dei difetti chiusi.
   - Ultima riga: `Claude-Session: https://claude.ai/code/session_01AgXMZuyYXCkVG7ew2fjxbG`.
   - Mai `Co-Authored-By`.
6. **Integrazione**, per ogni repo toccato:
   - `/home/user/wt/bin/integrate.sh <TASK> <repo> sync`
     - Se risponde `CONFLICT`: risolvi mantenendo l'intento di entrambe le parti e fai commit.
     - Se risponde `MERGED` o hai risolto un conflitto: ricompila e riesegui i test.
   - `/home/user/wt/bin/integrate.sh <TASK> <repo> publish`
     - Se risponde `MOVED`: ripeti sync, test e publish.
   - Pubblica **solo** con build e test verdi. Se dopo il merge i test falliscono per cause esterne al tuo task, non pubblicare e riportalo.
7. Chiudi con `/home/user/wt/bin/finish-task.sh <TASK>`.

## Risposta finale
È il tuo valore di ritorno: massimo 200 parole, in italiano, esattamente in questo formato.
```
TASK: <id>
ESITO: DONE | PARTIAL | BLOCKED | GIA_RISOLTO
DIFETTI_CHIUSI: <id ...>
DIFETTI_APERTI: <id: motivo> | -
COMMIT: <repo>@<sha breve> ...
TEST: <comandi e risultato sintetico>
RUNBOOK: <file creati/aggiornati> | -
DUBBI: <domande per il product owner> | -
```

## Convenzioni introdotte dai task di fondamenta (usale, non reinventarle)
- **Errori backend (FD-05):**
  - Nei servizi lancia `DomainRuleException(code, key, args)` (→ 422) o `DomainConflictException` (→ 409), entrambe in `Casazen.Core.Exceptions`, oppure `NotFoundException` (→ 404).
  - Nei controller usa `this.ApiProblem(status, code, key, args)`.
  - I codici generici sono in `Casazen.Web.Infrastructure.ProblemCodes`.
  - Ogni chiave va in `Casazen.Web/Resources/SharedResources.resx` (IT) **e** `SharedResources.en.resx`: il test `SharedResourcesLocalizationTests` fallisce altrimenti.
  - `InvalidOperationException` ora diventa 500 generico: non usarla per errori di dominio. `UnauthorizedAccessException` diventa 403 con code `forbidden`.
- **Test su PostgreSQL (FD-04):** le factory d'integrazione creano un DB `it_<guid>` reale quando `TEST_POSTGRES_CONNECTION` è impostata (lo fa `env.sh`). Per test solo-Postgres usa `[PostgresFact]`. Esegui sempre i test con `source /home/user/wt/bin/env.sh`.
- **Client HTTP frontend (FD-08):**
  - Le chiamate pubbliche si dichiarano esplicitamente (vedi `src/lib/axios.ts` / `src/api/client.ts`).
  - Negli `onError` usa `getProblemMessage(err, t)`.
  - Niente `fetch` diretto (regola ESLint).
- **CI frontend (FD-01):** `npm run lint` deve restare a 0 errori.
- **Date (FD-06):** le date in ingresso (JSON, query, route) e su EF sono già normalizzate in UTC da infrastruttura globale: niente `SpecifyKind` ad hoc. Per "oggi" di calendario usa `RomeCalendar.TodayInRome()` (basato su `TimeProvider`, registrato in DI); nei test usa un `FakeTimeProvider`.
- **Email (FD-13):** usa solo `IEmailService` con i template in `Casazen.Infrastructure/Email/Templates` (testi `.resx` IT/EN, valori HTML-encoded). L'invio va accodato su Hangfire, mai dentro la richiesta. I link si costruiscono da `App:PublicSiteBaseUrl` (`PublicSiteLinks`). Niente HTML costruito a mano.
- **File (FD-07):** usa solo `IFileStorage`. Bucket public per le foto (URL assoluti); bucket private per documenti, scansioni e PDF, scaricabili solo tramite endpoint autenticato con controllo tenant. Niente scritture su `wwwroot` o sul disco locale.
- **Tenant (TN-1, TN-2):** le entità con `OrgId` implementano `ITenantOwned` e il filtro si registra da solo. Una nuova entità tenant va resa `ITenantOwned`, altrimenti il test architetturale `TenantQueryFilterArchitectureTests` fallisce; l'allow-list va motivata. `Guest` è per org. Ogni `IgnoreQueryFilters` richiede un commento con il motivo.
- **Autorizzazione (TN-3):**
  - Le policy sono in `Casazen.Web/Authorization/CasazenPolicies`: `Authenticated` solo per endpoint dell'utente stesso (allow-list motivata), `AdminOnly`, `Supplier`, `OrgBillingAdmin`, `Property/Booking/Payment/Guest/Ota` Read-Write, `Lease` Read/Create/Sign/Register.
  - A livello di classe metti la policy di lettura, sulle azioni di scrittura quella di scrittura.
  - Per la singola risorsa usa `authorizationService.IsAuthorizedAsync(User, HostResource, XxxOperations.Write)`; per le liste usa `User.GetHostScope(orgId)` filtrato in SQL.
  - I servizi non controllano mai i ruoli.
  - Se tocchi un controller non ancora migrato (Bookings, Properties, Alloggiati, Ota), migralo al nuovo schema nelle parti che modifichi.
- **Feature flag (FD-20):**
  - Backend: aggiungi la costante in `Casazen.Core/Features/FeatureFlags.cs` e la voce in `All`, con default in appsettings `Features:X` (se manca = off). Usa `[FeatureGate(FeatureFlags.X)]` sugli endpoint (404 prima dell'auth) e `IFeatureFlags` nei servizi e nei job. Per i job ricorrenti con flag off usa `RemoveIfExists`.
  - Frontend: chiave e default in `src/config/feature-flags.ts`, `featureFlag:` nelle voci di `ROUTE_MANIFEST`, `useFeatureFlags()` e `enabled:` sulle query. I flag arrivano da `GET /api/public/features` e, se la chiamata fallisce, sono tutti off.
  - Runbook: `docs/runbooks/feature-flags.md`. Le API OTA partner sono dietro flag OFF (D10): non riattivarle.
- **Tenant context (TN-4):**
  - L'`OrgId` viene caricato da un middleware asincrono dopo l'autenticazione.
  - Dopo aver creato o trovato un'org, chiama `IRequestTenantContext.SetOrgId`.
  - Per la creazione di property usa `CreatePropertyWithinLimitAsync`: limite del piano atomico sotto advisory lock.
  - Le corse di creazione vanno gestite con un advisory lock oppure con la gestione di 23505 e rilettura, mai con check-then-insert.
- **Advisory lock Postgres:** prima di scegliere una chiave, cerca quelle esistenti (`grep -rn "AdvisoryLock\|pg_advisory" --include=*.cs`) e usa un valore nuovo e univoco. Se al merge trovi collisioni, tieni valori distinti.
- **iCal (PC-10):** usa il parser in `Services/ICal` e la sincronizzazione per feed isolata. Il runbook è `docs/runbooks/ical.md`.
- **Disponibilità (BK-05):** le notti occupate si calcolano solo con `PropertyOccupancy`, usato da disponibilità pubblica, creazione e controllo sovrapposizioni. Non scrivere un'altra logica di occupazione.
