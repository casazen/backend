# Sviluppo e verifica backend

## Avvio

Seguire il [README](../../README.md) per .NET 10, PostgreSQL 16, connessione e migrazioni. Il progetto di startup è `Casazen.Web`; quello delle migrazioni è `Casazen.Infrastructure`. Non copiare file appsettings inesistenti dalla radice.

Per la configurazione locale usare user secrets del progetto Web o variabili d’ambiente. Le chiavi reali non vanno nel repository. `ConnectionStrings__DefaultConnection` è l’equivalente della chiave `ConnectionStrings:DefaultConnection` come variabile d’ambiente. Configurare Auth0 e i servizi necessari attraverso i [runbook](../runbooks/index.md).

Le credenziali di esempio per PostgreSQL locale non sono credenziali di staging. Non puntare i test a database operativi.

## Controlli

```bash
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes
```

La CI esegue la suite completa, senza filtri di esclusione, dopo la build Release. Il workflow corrente è in `.github/workflows/ci-cd.yml`; per una verifica mirata locale si può aggiungere `--filter`, indicando nel resoconto i gruppi effettivamente eseguiti.

Per test di integrazione affidabili impostare `TEST_POSTGRES_CONNECTION` a un PostgreSQL **dedicato ai test**, con permessi di creazione e rimozione database. Le factory creano e migrano database `it_<guid>` e li eliminano al termine. In assenza della variabile usano Testcontainers; l’eventuale fallback locale InMemory non verifica i vincoli PostgreSQL. Vedere [testing](../TECHNICAL.md#testing) e [CI backend](../runbooks/ci-backend.md).

Usare un filtro sul dominio modificato per iterazioni rapide; prima del merge verificare i controlli richiesti sulla revisione effettiva della PR. Per L3 web e mobile: [Golden Journey](../runbooks/golden-journey-l3.md), [Maestro](../runbooks/mobile-e2e.md).

## Diagnostica locale

- `/api/health/live`: il processo risponde.
- `/api/health/ready`: verificare codice e JSON; un `200 degraded` può indicare servizi esterni non configurati e non prova un’integrazione funzionante.
- `/api/properties` senza bearer: deve restare protetto.
- `/swagger`: solo in sviluppo.

Per condizioni e dipendenze esatte leggere [health check](../runbooks/health-checks.md); per configurazione e rilascio [deploy checklist](../runbooks/deploy-checklist.md). Le vecchie indicazioni Railway nell’infrastruttura sono storiche: non assumere un host attivo senza verificarlo.
