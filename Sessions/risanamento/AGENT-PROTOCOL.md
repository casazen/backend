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
