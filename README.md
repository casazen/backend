# CasaZen — Backend API

API .NET 10 per la gestione di immobili, prenotazioni, ospiti, organizzazioni, affitti lunghi, adempimenti e servizi ai proprietari. La webapp e l’app mobile sono repository separati:

- [Webapp](https://github.com/casazen/frontend)
- [App mobile](https://github.com/casazen/mobile)

## Documentazione

- [AGENTS.md](AGENTS.md): struttura, comandi, flusso di consegna e regole per chi sviluppa (anche agenti AI).
- Runbook, checklist di deploy e riferimento API e modello dati: nella cartella `docs/03-documenti-ai/runbook/` del workspace (indice in `README.md`, file in `backend/`), non più in questo repository.
- Requisiti, punti aperti, decisioni, revisioni legali e fiscali, ADR e integrazioni stanno nella cartella `docs/` del workspace di progetto, fuori dai repository (questo è pubblico).
- **Guida utenti:** nella webapp, dopo il login, aprire **Guida** nell’intestazione.

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

`postgres/dev` è solo un esempio per un database locale. Per autenticazione e servizi esterni usare i valori dell’ambiente di sviluppo e i runbook (`docs/03-documenti-ai/runbook/README.md`).

- API locale: `http://localhost:5000/api`
- Swagger, in sviluppo: `http://localhost:5000/swagger`
- Liveness: `/api/health/live`
- Readiness: `/api/health/ready` (verificare anche il contenuto della risposta)

Non esiste un `docker-compose.yml` alla radice. Il `Dockerfile` descrive l’immagine dell’API; per l’infrastruttura leggere INFRA.md (`docs/03-documenti-ai/runbook/backend/INFRA.md`) (da rivedere: vedere [docs/README.md](docs/README.md)).

## Build e test

```bash
dotnet build --configuration Release
dotnet test --configuration Release --no-build
dotnet format --verify-no-changes
```

Per un’esecuzione riproducibile dei test di integrazione, impostare `TEST_POSTGRES_CONNECTION` verso un PostgreSQL locale dedicato ai test; le factory creano database temporanei (dettagli in [AGENTS.md](AGENTS.md) e nel runbook CI (`docs/03-documenti-ai/runbook/backend/ci-backend.md`)).

## Contribuire

Il flusso è Web → Core/Infrastructure; il dominio resta in Core. Branch da `develop`, PR verso `develop`, mai push diretto su `develop` o `main` ([AGENTS.md](AGENTS.md)). Ogni modifica a un comportamento operativo aggiorna il relativo runbook, i documenti in `docs/` toccati e, se cambia il flusso visibile, la guida utenti della webapp.
