# AGENTS.md

## Cursor Cloud specific instructions

This repository is the **CasaZen backend API** (.NET 10 + PostgreSQL). The React frontend lives in a separate repo (`casazen/frontend`).

### Services (this repo)

| Service | Required locally | Notes |
|---|---|---|
| PostgreSQL 16 | Yes | Local DB `casazen_dev` on port 5432 (`postgres` / `dev`) — see `Casazen.Web/appsettings.Development.json` |
| CasaZen API (`Casazen.Web`) | Yes | HTTP `http://localhost:5000`, HTTPS `https://localhost:5001` |
| Hangfire | Bundled | Uses PostgreSQL schema `hangfire`; dashboard at `/hangfire` when DB is configured |

### Environment variables / shell

`.NET` is installed under `~/.dotnet`. Ensure these are set (already in `~/.bashrc` on the Cloud VM):

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
```

### PostgreSQL startup

PostgreSQL may not auto-start after a fresh VM boot. Before migrations or running the API:

```bash
sudo pg_ctlcluster 16 main start
```

Connection string (default): `Host=localhost;Port=5432;Database=casazen_dev;Username=postgres;Password=dev`

### Common commands

See `README.md` and `.github/workflows/ci-cd.yml` for canonical commands:

```bash
dotnet restore
dotnet build --configuration Release
dotnet ef database update --project Casazen.Infrastructure --startup-project Casazen.Web
dotnet run --project Casazen.Web
dotnet test --configuration Release --filter "FullyQualifiedName!~PropertiesControllerIntegrationTests&FullyQualifiedName!~ApiTests&FullyQualifiedName!~UsersControllerIntegrationTests"
dotnet format --verify-no-changes
```

### Health / smoke checks

- `GET http://localhost:5000/api/health` → **200** (public)
- `GET http://localhost:5000/api/properties` → **401** (auth gate)
- Swagger: `http://localhost:5000/swagger`

### Gotchas

- Use local PostgreSQL or Supabase per `docs/INFRA.md`. There is no root `docker-compose.yml`.
- Integration tests that need a live DB are excluded in CI via `--filter` (see `ci-cd.yml`).
- `dotnet ef` global tool must be installed once: `dotnet tool install --global dotnet-ef --version 10.0.0`
- Auth0/Stripe/SendGrid keys in `appsettings.Development.json` are placeholders; external services are optional for local API smoke tests.
