# Infrastructure Rules

## Hosting stack

| Layer | Provider | Environment |
|---|---|---|
| Database | Supabase (PostgreSQL) | Single project, two schemas: `casazen_test` / `casazen_prod` |
| Backend API | Railway (Docker, .NET 10) | Two Railway environments: `test` / `production` |
| Frontend | Vercel (Vite SPA) | Preview URLs per PR; `develop` → staging; `main` → production |

Full setup guide: `docs/INFRA.md`

## Deploy model (native integrations)

- **Railway** deploys `casazen/backend` via GitHub integration — not via `railway up` in Actions
- **Vercel** deploys `casazen/frontend` via GitHub integration
- **GitHub Actions** (`ci-cd.yml`): build, test, format; `verify-test` / `verify-prod` curl health URLs only
- **PR comments** (`deploy-preview.yml`): links to Railway test URL + Vercel preview — no deploy

## Database

- **Provider**: PostgreSQL via Supabase (NOT SQL Server)
- **EF Core provider**: `Npgsql.EntityFrameworkCore.PostgreSQL`
- **Migrations**: `dotnet ef migrations add <Name> --project Casazen.Infrastructure`
- **Connection string format**: `Host=db.[REF].supabase.co;Port=5432;Database=postgres;Username=postgres;Password=[PW];SearchPath=casazen_test;SSL Mode=Require`
- **NEVER** hardcode connection strings — use **Railway** environment variables

## Backend API port

Railway terminates TLS at the edge. The .NET app must listen on plain HTTP:

```dockerfile
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
```

HTTPS is NOT used inside the container — Railway handles it externally.

## Environment URLs

- Test BE: `$RAILWAY_TEST_URL` (GitHub **variable** — public URL for CI only; runtime config on Railway)
- Test FE: Vercel deployment for branch `develop` (Preview env vars) + per-PR previews
- Production BE: `$RAILWAY_PROD_URL` (GitHub variable)
- Production FE: `https://casazen-app.vercel.app`

## Deployment rules

- **Test deploy**: Railway native — push to `develop` → test environment
- **Production deploy**: Railway native — push to `main` → production environment (never manual prod deploy)
- **PR backend**: Railway PR deploys (if enabled) OR validate on shared test after merge to `develop`
- **Version tags** (`v*`): GitHub Release / changelog only — **do not** trigger Railway or Vercel deploys
- **Never deploy to production** without verifying all features in the epic on the test environment

## Secrets management

| Where | What |
|---|---|
| **Railway** (test + prod) | `ConnectionStrings__DefaultConnection`, Auth0, Stripe, SendGrid, CORS, Hangfire |
| **Vercel** | All `VITE_*` |
| **GitHub Variables** | `RAILWAY_TEST_URL`, `RAILWAY_PROD_URL` (public URLs for CI only) |
| **GitHub Secrets (optional)** | `SUPABASE_ANON_KEY` for keep-alive workflow |

**Not required on GitHub:** `RAILWAY_TOKEN`, `RAILWAY_SERVICE_*`

Never commit secrets in code or committed `appsettings.*.json`.

## Supabase keep-alive

Supabase free tier pauses after 7 days of inactivity. Use `supabase-keepalive.yml` (optional GitHub vars) or Supabase dashboard auto-pause settings.

## Release bundles

When a feature spans both BE and FE (e.g., BE #165 + FE #177), they form a Release Bundle tracked in `Sessions/bundle-<epic>.md`. Both must be verified on test before either is promoted to production.
