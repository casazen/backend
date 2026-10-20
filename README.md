# CasaZen — Backend API

API .NET 10 per la gestione di immobili, prenotazioni, ospiti, organizzazioni, affitti lunghi, adempimenti e servizi ai proprietari. La webapp e l’app mobile sono repository separati:

- [Webapp](https://github.com/casazen/frontend)
- [App mobile](https://github.com/casazen/mobile)

## Documentazione

- [Indice tecnico e operativo](docs/index.md)
- [Architettura e responsabilità](docs/technical/architecture.md)
- [Sviluppo e verifica locale](docs/technical/development.md)
- [Riferimento API e modello dati](docs/TECHNICAL.md)
- [Runbook per argomento](docs/runbooks/index.md)
- [Checklist di deploy](docs/runbooks/deploy-checklist.md)
- **Guida utenti:** nella webapp, dopo il login, aprire **Guida** nell’intestazione. [Contenuti e manutenzione](https://github.com/casazen/frontend/blob/develop/docs/product/README.md).

## Avvio rapido

Prerequisiti: .NET 10 SDK, PostgreSQL 16 ed EF CLI coerente con i pacchetti del progetto (attualmente 10.0.12). I test di integrazione usano database dedicati.

```bash
git clone https://github.com/casazen/backend.git
cd backend
dotnet restore
```

Configurare la connessione locale tramite user secrets del progetto Web, senza salvare credenziali reali nel repository:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=casazen_dev;Username=postgres;Password=dev" --project Casazen.Web
dotnet ef database update --project Casazen.Infrastructure --startup-project Casazen.Web
dotnet run --project Casazen.Web
```

`postgres/dev` è solo un esempio per un database locale. Per autenticazione e servizi esterni usare i valori dell’ambiente di sviluppo e i [runbook](docs/runbooks/index.md).

- API locale: `http://localhost:5000/api`
- Swagger, in sviluppo: `http://localhost:5000/swagger`
- Liveness: `/api/health/live`
- Readiness: `/api/health/ready` (verificare anche il contenuto della risposta)

Non esiste un `docker-compose.yml` alla radice. Il `Dockerfile` descrive l’immagine dell’API; per l’infrastruttura leggere [INFRA.md](docs/INFRA.md) e le note sullo stato dell’hosting.

## Build e test

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes
```

Per un’esecuzione riproducibile dei test di integrazione, impostare `TEST_POSTGRES_CONNECTION` verso un PostgreSQL locale dedicato ai test; le factory creano database temporanei. Dettagli e limiti nella [guida sviluppo](docs/technical/development.md).

## Contribuire

Il flusso è Web → Core/Infrastructure; il dominio resta in Core. Ogni modifica a un comportamento operativo deve aggiornare il relativo runbook e, se cambia il flusso visibile, la guida utenti della webapp. Pianificazione e interviste in `Sessions/` sono materiale di lavoro, non il riferimento tecnico delle funzioni consegnate.
