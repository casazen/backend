# AGENTS.md — CasaZen backend

> Verificato il 2026-11-27 su `develop@232b21fb`. Letto da tutti gli agenti (Claude Code tramite `CLAUDE.md`, Cursor, Codex).
> **Questo repository è PUBBLICO** (`casazen/backend`): niente segreti, niente documenti riservati (business plan, listino, pareri legali o fiscali, contratti, dati personali o di clienti).

## Il progetto in breve

API **.NET 10** + **PostgreSQL 16** per CasaZen: immobili, prenotazioni, ospiti, organizzazioni, affitti lunghi, adempimenti (Alloggiati, ISTAT, Svizzera), fatturazione, fornitori. Webapp e app mobile sono repository separati: `casazen/frontend` (pubblico) e `casazen/mobile` (privato).

| Progetto | Responsabilità |
|---|---|
| `Casazen.Core` | entità, regole di dominio, interfacce di servizio e repository, `Features/FeatureFlags.cs` |
| `Casazen.Infrastructure` | EF Core e migrazioni, repository, servizi, integrazioni esterne |
| `Casazen.Web` | controller, DTO, autenticazione e policy, middleware, DI, job Hangfire |
| `Casazen.Tests` | test unitari, di integrazione (PostgreSQL reale) e vincoli architetturali |

Il flusso delle dipendenze è Web → Core/Infrastructure; il dominio resta in Core. L'organizzazione (`OrgId`) e l'ambito sugli immobili sono controlli di accesso ai dati, non filtri opzionali: usa lo scope e le policy esistenti e non accettare un `OrgId` del client come autorità. Stack: Auth0 (JWT su ogni `/api`), Stripe (webhook con firma verificata), Resend (email in coda su Hangfire), Hangfire (uno schema per ambiente). Ogni funzione nuova va dietro un **feature flag** spento di default (`Features:<Nome>`, variabile `Features__<Nome>`; elenco e regole in `documentazione/03-documenti-ai/runbook/backend/feature-flags.md`).

## Ambiente locale (e Cursor Cloud)

| Servizio | Note |
|---|---|
| PostgreSQL 16 | database locale `casazen_dev` sulla porta 5432; stringa d'esempio in `Casazen.Web/appsettings.Development.example.json` (il file `appsettings.Development.json` non si committa) |
| API `Casazen.Web` | `http://localhost:5000`, `https://localhost:5001`; Swagger solo in sviluppo su `/swagger` |
| Hangfire | incluso; schema `Hangfire:Schema` o `hangfire_<SearchPath>` (`documentazione/03-documenti-ai/runbook/backend/hangfire.md`) |

Su una VM Cursor Cloud .NET sta in `~/.dotnet` (`export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"`) e PostgreSQL può non partire da solo: `sudo pg_ctlcluster 16 main start`.

```bash
dotnet restore
dotnet build --configuration Release
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=casazen_dev;Username=postgres;Password=<password locale>" --project Casazen.Web
dotnet ef database update --project Casazen.Infrastructure --startup-project Casazen.Web   # dotnet-ef 10.0.12, come i pacchetti EF
dotnet run --project Casazen.Web
```

- Health: `GET /api/health/live` → 200; `/api/health/ready` e `/api/health` → 200 `degraded` se Stripe, email o Auth0 M2M non sono configurati in locale, 503 se database o Hangfire sono giù (`documentazione/03-documenti-ai/runbook/backend/health-checks.md`). `GET /api/properties` senza bearer → 401.
- Non esiste un `docker-compose.yml` alla radice (solo `observability/docker-compose.yml`). Database e hosting: `documentazione/03-documenti-ai/runbook/backend/INFRA.md` (da rivedere, vedi `docs/README.md`).
- Le chiavi Auth0/Stripe/Resend dell'esempio sono segnaposto: i servizi esterni sono opzionali per gli smoke test locali.

## Prima di consegnare

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build        # in locale si può filtrare con --filter e dirlo nel resoconto
dotnet format --verify-no-changes
```

- La CI (`.github/workflows/ci-cd.yml`) esegue **la suite completa** in 6 shard su `postgres:16`, più il controllo dei pacchetti vulnerabili e `dotnet format`. Nessuna esclusione di classi: se un test è rosso, si ripara.
- I test di integrazione usano PostgreSQL reale: esporta `TEST_POSTGRES_CONNECTION="Host=localhost;Port=5432;Username=postgres;Password=<password locale>"` (ogni factory crea, migra e cancella un database `it_<guid>`). Senza la variabile si usa Testcontainers (serve Docker); l'ultimo ripiego, EF InMemory, vale solo in locale e non verifica i vincoli di PostgreSQL.
- Ogni cambio di schema ha una migrazione EF (`dotnet ef migrations add <Nome> --project Casazen.Infrastructure`), provata in locale.
- Ogni stringa visibile ha le sue chiavi in italiano e inglese (backend: `IStringLocalizer<T>` + `.resx`; frontend: `t()`), e ogni funzione visibile a host, ospite, fornitore o staff aggiorna la **demo** (`DemoShowcaseCatalog`).
- Insidie ricorrenti (altre in `.claude/rules/gotchas.md`): `DateTime.UtcNow` internamente; fuso dell'immobile come stringa IANA (`Europe/Rome` di default) e conversioni con `TimezoneHelper`; tutti gli endpoint `/api` richiedono il JWT Auth0 salvo quelli esplicitamente pubblici (health, prenotazione e pagine pubbliche, testi legali, webhook); i webhook rispondono entro 3 secondi e delegano a Hangfire; la firma dei webhook Stripe si verifica sempre; le aliquote dell'imposta di soggiorno stanno nelle entità `TaxRate`, mai nel codice.

## Flusso di consegna

- Branch `feature/<nome>`, `fix/<nome>` o `hotfix/<nome>` **da `develop`**; commit in Conventional Commits (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`), **senza** riga `Co-Authored-By`.
- **Mai push o merge diretto su `develop` o `main`.** Apri una PR verso `develop` (`gh pr create --base develop`): corpo con Riepilogo, Piano di prova e `Closes #N`; si ferma lì. `main` solo con una PR di rilascio e solo su richiesta esplicita dell'utente. Non si fa force-push su rami condivisi.
- Le regole dettagliate per zona stanno in `.claude/rules/*.md` (stile del codice, conformità italiana e CIN, demo, git, GitHub Flow, infrastruttura, integrazioni, sicurezza, insidie): leggi quelle della zona che tocchi. Non contraddicono questo file; se lo fanno, vale il più restrittivo e va segnalato.

## Documentazione: tienila pulita e aggiornata

La documentazione di progetto sta nella cartella **`documentazione/` del workspace CasaZen** (la cartella che contiene `backend/`, `frontend/` e `mobile/`), **fuori dai repository** di proposito. `01-documenti-ufficiali/` è scritta o approvata dall'utente o da professionisti; `03-documenti-ai/` è scritta dall'AI (requisiti, punti aperti, decisioni, revisioni, integrazioni, ADR, consegne). Regole:

1. **Un solo posto.** Analisi, requisiti, punti aperti, decisioni, revisioni legali e fiscali, ADR, specifiche e report vivono solo in `documentazione/`. **Non creare `.md` sparsi in questo repository** e niente log, JSON di stato, cartelle «attempt», report o appunti di sessione (sono rumore e restano nella storia git). **Nessuna eccezione**: anche i runbook, il riferimento esteso (`TECHNICAL.md`) e le guide di infrastruttura vivono in `documentazione/03-documenti-ai/runbook/backend/` (indice in `runbook/README.md`); dal 2026-11-27 nessun test del repository li legge più, e si aggiornano lì nella stessa PR del codice.
2. **La PR che cambia un comportamento aggiorna i documenti toccati**: lo stato dei requisiti (`documentazione/03-documenti-ai/requisiti/<area>.md`), i punti aperti (quelli chiusi vanno in `decisioni.md` con data e decisione), i runbook, **la guida utente** (`frontend/guida/**` sorgenti e `frontend/public/guida/**` pagine generate con `npm run guida:build`, più i testi `productGuide` della guida in-app in `frontend/src/i18n/locales/{it,en}.json`) e **la documentazione tecnica ufficiale** (`documentazione/01-documenti-ufficiali/documentazione-tecnica/`, sorgenti e PDF: si rigenera quando cambiano architettura, stack, integrazioni, sicurezza o moduli). Se questi due artefatti non sono ancora in `develop`, citali comunque nella PR.
3. **Una sola fonte per argomento**: collega, non copiare. Se trovi due versioni dello stesso documento, tieni la corrente e togli l'altra.
4. **Date assolute** (AAAA-MM-GG) e, in testa a ogni documento vivo, l'ultima verifica: `Verificato il AAAA-MM-GG su develop@<sha>` solo se hai davvero controllato i fatti contro codice o `gh`; altrimenti `Stato: NON verificato — fonte: <file>`. Non scrivere «consegnato» ciò che è specifica o placeholder.
5. **A fine lavoro rimuovi i file temporanei** che hai creato (script, log, patch, bozze).
6. **Documenti riservati** (business plan, listino, pareri, contratti, dati di clienti) **mai** in questo repository pubblico.
7. I **documenti ufficiali** (`01-documenti-ufficiali/`) non si riscrivono senza richiesta dell'utente. Eccezione: la documentazione tecnica (`01-documenti-ufficiali/documentazione-tecnica/`) si aggiorna come da regola 2 e il PDF si rigenera con `node documentazione/01-documenti-ufficiali/documentazione-tecnica/sorgenti/build-pdf.mjs` (servono Chrome o Edge e Python con `pypdfium2`).
8. Se una regola di questa sezione confligge con quanto trovi nel repository, **segnalalo** (nella PR o nel report) invece di scegliere in silenzio.

Senza accesso al workspace (per esempio in una sandbox cloud) non inventare né ricreare documenti qui: scrivi nella PR cosa andrebbe aggiornato in `documentazione/`.
