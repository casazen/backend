# Infrastructure Rules

## Hosting stack

| Layer | Provider | Environment |
|---|---|---|
| Database | Supabase (PostgreSQL) | Single project, two schemas: `casazen_test` / `casazen_prod` |
| Backend API | Railway (Docker, .NET 10) | Two Railway environments: `test` / `production` |
| Frontend | Vercel (Vite SPA) | Preview URLs per PR (auto), `casazen.vercel.app` for production |

Full setup guide: `docs/INFRA.md`

## Database

- **Provider**: PostgreSQL via Supabase (NOT SQL Server)
- **EF Core provider**: `Npgsql.EntityFrameworkCore.PostgreSQL`
- **Migrations**: `dotnet ef migrations add <Name> --project Casazen.Infrastructure`
- **Connection string format**: `Host=db.[REF].supabase.co;Port=5432;Database=postgres;Username=postgres;Password=[PW];SearchPath=casazen_test;SSL Mode=Require`
- **NEVER** hardcode connection strings — use Railway environment variables or GitHub Secrets

## Backend API port

Railway terminates TLS at the edge. The .NET app must listen on plain HTTP:

```dockerfile
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
```

HTTPS is NOT used inside the container — Railway handles it externally.

## Environment URLs

- Test BE: `$RAILWAY_TEST_URL` (GitHub variable — set once in repo settings)
- Test FE: Vercel preview URL (per PR, from Vercel bot comment)
- Production BE: `$RAILWAY_PROD_URL` (GitHub variable)
- Production FE: `https://casazen.vercel.app`

## Deployment rules

- **Test auto-deploy**: triggered on PR open/update (`deploy-preview.yml`) and on push to `main` (ci-cd.yml `deploy-test` job)
- **Production deploy**: triggered ONLY by pushing a `v*` tag — never deploy prod manually
- **Never deploy to production** without Stage 05 bundle check (all features in the Epic verified on test)

## Secrets management

All credentials live in GitHub Secrets or Railway environment variables — **never in code or appsettings.Development.json committed to the repo**.

Required GitHub Secrets:
- `RAILWAY_TOKEN` — Railway API token

Required GitHub Variables (not secret):
- `RAILWAY_TEST_URL` — Railway test service public URL
- `RAILWAY_PROD_URL` — Railway production service public URL
- `RAILWAY_SERVICE_TEST` — Railway service ID for test environment
- `RAILWAY_SERVICE_PROD` — Railway service ID for production environment

## Supabase keep-alive

Supabase free tier pauses after 7 days of inactivity. Set a GitHub Actions scheduled ping or configure the Supabase auto-pause setting (Dashboard → Settings → General → Pause).

## Release bundles

When a feature spans both BE and FE (e.g., BE #165 + FE #177), they form a Release Bundle tracked in `Sessions/bundle-<epic>.md`. Both must be verified on test before either is promoted to production. See Stage 05 harness for the full bundle gate specification.
