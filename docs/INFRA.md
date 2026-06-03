# CasaZen — Infrastructure Guide

> Zero-cost production stack: **Supabase** (PostgreSQL) + **Railway** (.NET API) + **Vercel** (React SPA)

---

## Overview

| Layer | Provider | Plan | Monthly cost | Notes |
|---|---|---|---|---|
| Database | Supabase | Free | $0 | 500 MB, 2 GB bandwidth; pauses after 7 days inactivity |
| Backend API | Railway | Hobby | ~$5 | $5 free credit/month; no cold starts |
| Frontend | Vercel | Hobby | $0 | Unlimited deployments, 100 GB bandwidth |

Railway is effectively ~$0–$2/month for a low-traffic .NET API once you stay within the $5 credit.  
Alternative for truly $0: **Render** free tier (same DX, but the service sleeps after 15 min of inactivity — acceptable for test, not ideal for prod).

---

## Environment Architecture

```
┌──────────────────────────────────────────────────────────┐
│  PRODUCTION                                              │
│  FE: casazen.vercel.app                                  │
│  BE: casazen-api.up.railway.app                          │
│  DB: supabase.co (schema: casazen_prod)                  │
└──────────────────────────────────────────────────────────┘
         ▲ promoted by Stage 05 release-manager
┌──────────────────────────────────────────────────────────┐
│  TEST (shared staging)                                   │
│  FE: preview-[hash].vercel.app  (per PR, auto)           │
│  BE: casazen-api-test.up.railway.app                     │
│  DB: supabase.co (schema: casazen_test)                  │
└──────────────────────────────────────────────────────────┘
         ▲ Railway: push to `main` (test) · Vercel: PR preview (FE)
```

Two Supabase schemas (`casazen_test`, `casazen_prod`) in one free project — saves the free-tier limit.

---

## Deploy model: native GitHub integrations

CasaZen uses **native deploys** from each provider’s GitHub app. GitHub Actions only **build, test, and verify** — they do **not** call `railway up` or push containers.

| Provider | Who deploys | Where runtime env vars live |
|---|---|---|
| **Railway** | Railway GitHub integration → `casazen/backend` | Railway dashboard → each environment (`test` / `production`) |
| **Vercel** | Vercel GitHub integration → `casazen/frontend` | Vercel dashboard → Preview / Production |
| **Supabase** | Hosted DB (no app deploy) | Supabase dashboard; optional secrets synced to GitHub by Supabase integration |

### What GitHub Actions still do

| Workflow | Trigger | Purpose |
|---|---|---|
| `ci-cd.yml` | PR, push `main`, tag `v*` | `dotnet test`, format, build |
| `ci-cd.yml` → `verify-test` | Push `main` | Wait for Railway native deploy, then `GET /api/health` |
| `ci-cd.yml` → `verify-prod` | Tag `v*` | Wait for Railway prod deploy, health + smoke |
| `deploy-preview.yml` | PR | Comment with BE/FE URLs (no deploy) |
| `supabase-keepalive.yml` | Weekly cron | Optional Supabase ping |

### What is **not** synced automatically

Integrations link repos and trigger deploys. They **do not** copy env vars across platforms:

- Supabase connection string → must be set on **Railway** (`ConnectionStrings__DefaultConnection`)
- Auth0 / Stripe / SendGrid → **Railway** only
- `VITE_*` → **Vercel** only
- Public API URLs → **GitHub Variables** (`RAILWAY_TEST_URL`, `RAILWAY_PROD_URL`) for CI health checks and PR comments only

You do **not** need `RAILWAY_TOKEN` or `RAILWAY_SERVICE_*` in GitHub unless you add custom scripts later.

---

## Manual setup checklist (one-time)

Do these once per project. Tick in order.

### 1. Supabase

- [ ] Create project `casazen` (region `eu-central-1`)
- [ ] Run SQL: create schemas `casazen_test`, `casazen_prod` (see below)
- [ ] Copy database URI for each schema
- [ ] Apply EF migrations to **test** schema (local `dotnet ef database update` with test connection string)
- [ ] (Optional) Enable **Supabase ↔ GitHub** integration — may add `SUPABASE_*` secrets to GitHub for keep-alive / CLI; **does not** configure Railway

### 2. Railway (`casazen/backend`)

- [ ] New project → **Deploy from GitHub** → repo `casazen/backend`
- [ ] Environments: `test` + `production`
- [ ] **test**: trigger deploy on push to branch `main`
- [ ] **production**: trigger deploy on git tags matching `v*` (or your release policy)
- [ ] (Recommended) Enable **PR deployments** if you want a backend URL per PR; otherwise use shared test URL after merge
- [ ] Per environment, set **all** variables (see Railway section) — especially `ConnectionStrings__DefaultConnection` with correct `SearchPath`
- [ ] Enable **Public networking**; copy each environment’s HTTPS URL
- [ ] First deploy green in Railway dashboard

### 3. GitHub repo `casazen/backend` (variables only)

- [ ] **Variable** `RAILWAY_TEST_URL` = Railway test public URL (no trailing slash)
- [ ] **Variable** `RAILWAY_PROD_URL` = Railway production public URL
- [ ] (Optional) **Variables** `SUPABASE_PROJECT_URL`, `SUPABASE_DB_HOST` + **Secrets** `SUPABASE_ANON_KEY` for `supabase-keepalive.yml` — often already present if Supabase GitHub app is installed

You do **not** need: `RAILWAY_TOKEN`, `RAILWAY_SERVICE_TEST`, `RAILWAY_SERVICE_PROD`.

### 4. Vercel (`casazen/frontend`)

- [ ] Import repo; preset Vite; build `npm run build`; output `dist`
- [ ] Set `VITE_API_BASE_URL`, `VITE_AUTH0_*` for **Preview** and **Production** (see Vercel section)
- [ ] Confirm PR previews and production deploy work

### 5. Smoke test

- [ ] `GET {RAILWAY_TEST_URL}/api/health` → 200
- [ ] `GET {RAILWAY_TEST_URL}/api/properties` without token → 401
- [ ] Open a PR → Vercel bot comment + backend link comment from `deploy-preview.yml`

---

## PostgreSQL migration (completed in codebase)

The backend uses **Npgsql** and PostgreSQL migrations. For a fresh database:

### 1 — Swap NuGet package

```xml
<!-- Casazen.Infrastructure/Casazen.Infrastructure.csproj -->
<!-- REMOVE: -->
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.*" />
<!-- ADD: -->
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.*" />
```

### 2 — Update DbContext registration

```csharp
// Casazen.Web/Extensions/ServiceCollectionExtensions.cs
// Replace:  options.UseSqlServer(connectionString)
// With:
options.UseNpgsql(connectionString,
    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"));
```

### 3 — Rebuild migrations

```bash
# Delete all existing SQL Server migrations
rm -rf Casazen.Infrastructure/Migrations/

# Regenerate for PostgreSQL
dotnet ef migrations add InitialCreate --project Casazen.Infrastructure
dotnet ef database update --project Casazen.Infrastructure \
  --connection "Host=localhost;Database=casazen_dev;Username=postgres;Password=dev"
```

### 4 — Update integration tests

Replace `UseInMemoryDatabase` or SQL Server test setup with SQLite in-memory or a test Supabase connection:

```csharp
// Casazen.Tests/Integration/ApiTestBase.cs
options.UseNpgsql(
    Environment.GetEnvironmentVariable("TEST_CONNECTION_STRING")
    ?? "Host=localhost;Database=casazen_test;Username=postgres;Password=dev");
```

### 5 — Update Dockerfile

```dockerfile
# Remove HTTPS self-signed cert requirement — Railway terminates TLS at the edge
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
```

---

## Supabase Setup

### Create project

1. https://supabase.com → New project
2. Name: `casazen` | Region: `eu-central-1` (Frankfurt)
3. Save the database password — used in every connection string
4. Wait ~2 min for provisioning

### Create schemas

Run in Supabase SQL Editor:

```sql
CREATE SCHEMA IF NOT EXISTS casazen_test;
CREATE SCHEMA IF NOT EXISTS casazen_prod;
GRANT ALL ON SCHEMA casazen_test TO postgres;
GRANT ALL ON SCHEMA casazen_prod TO postgres;
```

### Connection strings

Dashboard → Settings → Database → URI:

```
# Test
postgresql://postgres:[PASSWORD]@db.[REF].supabase.co:5432/postgres?options=-csearch_path%3Dcasazen_test

# Production
postgresql://postgres:[PASSWORD]@db.[REF].supabase.co:5432/postgres?options=-csearch_path%3Dcasazen_prod
```

### Apply migrations to both schemas

```bash
# Test schema
dotnet ef database update --project Casazen.Infrastructure \
  --connection "Host=db.[REF].supabase.co;Port=5432;Database=postgres;Username=postgres;Password=[PW];SearchPath=casazen_test;SSL Mode=Require;Trust Server Certificate=true"

# Production schema
dotnet ef database update --project Casazen.Infrastructure \
  --connection "Host=db.[REF].supabase.co;Port=5432;Database=postgres;Username=postgres;Password=[PW];SearchPath=casazen_prod;SSL Mode=Require;Trust Server Certificate=true"
```

### Supabase keep-alive (free tier pauses after 7 days)

Add a scheduled GitHub Actions ping or use the Supabase dashboard to configure the keep-alive option.

---

## Railway Setup (Backend API)

### Create project

1. https://railway.app → New Project → Deploy from GitHub → `casazen/backend`
2. Service name: `casazen-api`

### Create two environments

Railway → Project → **Environments** → Create:
- `test` — **Settings → Source**: deploy on push to `main` (GitHub integration)
- `production` — deploy on git tags `v*` (configure in Railway deployment triggers)

**PR previews (optional):** Railway → Service → Settings → enable PR deployments if you want a distinct backend URL per PR. Otherwise validate backend on shared test URL after merge to `main`.

### Environment variables per Railway environment

Set in Railway dashboard (Variables tab), per environment:

```
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:8080
PORT=8080
ConnectionStrings__DefaultConnection=[SUPABASE_URI_WITH_SCHEMA]
Auth0__Domain=[your-tenant.auth0.com]
Auth0__Audience=[https://api.casazen.app]
Stripe__SecretKey=[sk_live_... or sk_test_...]
Stripe__WebhookSecret=[whsec_...]
Email__SendGridApiKey=[SG....]
Hangfire__DashboardEnabled=false
Cors__AllowedOrigins=https://casazen.vercel.app,https://casazen.app
```

Also add preview origins or use host suffix `*.vercel.app` if configured in app (see `AddCasazenCors`).

### Get service URLs → GitHub Variables

After first native deploy:
1. Railway → each environment → Service → **Networking** → copy public HTTPS URL
2. GitHub → `casazen/backend` → **Settings → Secrets and variables → Actions → Variables**:

| Variable | Example |
|---|---|
| `RAILWAY_TEST_URL` | `https://casazen-api-test.up.railway.app` |
| `RAILWAY_PROD_URL` | `https://casazen-api.up.railway.app` |

Used only for CI health checks and PR link comments — **not** for Railway runtime (that uses Railway env vars above).

---

## Vercel Setup (Frontend)

### Create project

1. https://vercel.com → New Project → Import `casazen/frontend`
2. Framework preset: **Vite**
3. Build command: `npm run build`
4. Output directory: `dist`

### Environment variables

In Vercel dashboard → Settings → Environment Variables:

| Variable | Preview | Production |
|---|---|---|
| `VITE_API_BASE_URL` | `https://casazen-api-test.up.railway.app/api` | `https://casazen-api.up.railway.app/api` |
| `VITE_AUTH0_DOMAIN` | `dev-casazen.auth0.com` | `casazen.auth0.com` |
| `VITE_AUTH0_CLIENT_ID` | `[dev client id]` | `[prod client id]` |
| `VITE_AUTH0_AUDIENCE` | `https://casazen-api` | `https://api.casazen.app` |

### Auto-deploy behaviour (built-in, no extra config)

| Event | Result |
|---|---|
| PR opened / updated | Preview URL created automatically → `https://preview-[hash].vercel.app` |
| Push to `main` | Production deploy → `https://casazen.vercel.app` |

Vercel posts a comment on every PR with the preview URL.

---

## Multi-Environment Promotion Flow

```
PR opened (BE or FE)
    │
    ├─ GitHub Actions: build & test only (ci-cd.yml)
    ├─ Railway: native PR deploy OR wait for merge → test (your Railway setting)
    ├─ Vercel: auto preview URL on PR (frontend)
    ├─ deploy-preview.yml: PR comment with links
    │
    ▼
Merge PR → main
    │
    ├─ Railway (native): deploy test environment
    ├─ ci-cd.yml verify-test: GET $RAILWAY_TEST_URL/api/health → 200
    ├─ Vercel (native): production FE deploy
    │
    ▼
Stage 05 — Release
    ├─ Create git tag vX.Y.Z
    ├─ Railway (native): deploy production environment
    ├─ ci-cd.yml verify-prod: health on $RAILWAY_PROD_URL
    └─ Human: bundle check + acceptance on test before tagging
```

---

## Release Bundle Concept

A **Release Bundle** groups related BE and FE features that must reach production together (e.g., BE issue #165 and FE issue #177 are part of the same Epic).

### Bundle file

Created in Stage 05 when an Epic-linked feature is released:

**`Sessions/bundle-<epic-number>.md`**

```markdown
# Release Bundle — Epic #<N>: <Epic title>

## Status: collecting | test-deployed | test-verified | released

## Features
| Issue | Repo | Branch | Test status | Prod status |
|---|---|---|---|---|
| #165 | backend | feature/165-long-term-lease | ✅ deployed, ✅ verified | — |
| #177 | frontend | feature/177-lease-ui | ✅ deployed, ✅ verified | — |

## Test URLs
- BE: https://casazen-api-test.up.railway.app
- FE: https://preview-abc123.vercel.app

## Production Release
- Version: v1.3.0
- Tag: (pending)
- BE prod: https://casazen-api.up.railway.app
- FE prod: https://casazen.vercel.app
- Released: (pending)
```

### Bundle gate in Stage 05

Before allowing production promotion, the coordinator checks:
1. Does `Sessions/bundle-<epic>.md` exist?
2. Is every row in the Features table showing `✅ deployed, ✅ verified`?
3. If not: block and inform which features are still pending.

---

## GitHub Secrets / Variables (backend repo)

### Required for CI (public URLs only)

| Type | Name | Purpose |
|---|---|---|
| Variable | `RAILWAY_TEST_URL` | Health check after push to `main`; PR comment link |
| Variable | `RAILWAY_PROD_URL` | Health check after tag `v*` |

### Optional

| Type | Name | Purpose |
|---|---|---|
| Secret | `SUPABASE_ANON_KEY` | `supabase-keepalive.yml` REST ping |
| Variable | `SUPABASE_PROJECT_URL` | Keep-alive (`https://[ref].supabase.co`) |
| Variable | `SUPABASE_DB_HOST` | Keep-alive TCP (`db.[ref].supabase.co`) |

Often auto-created by **Supabase ↔ GitHub** integration. Not used by Railway runtime.

### Not required (native deploy model)

| Name | Why omitted |
|---|---|
| `RAILWAY_TOKEN` | No `railway up` in Actions |
| `RAILWAY_SERVICE_*` | Railway knows service from GitHub link |
| `SUPABASE_CONNECTION_STRING_*` in GitHub | Connection string lives on **Railway** env vars only |

### Where secrets actually live

| Secret / config | Set on |
|---|---|
| Database password / connection string | **Railway** per environment |
| Auth0, Stripe, SendGrid | **Railway** per environment |
| `VITE_*` | **Vercel** Preview + Production |
| Supabase service role (if needed for admin scripts) | **Supabase** dashboard or GitHub (optional) |

---

## Monitoring

| Dashboard | URL | What to watch |
|---|---|---|
| Railway | https://railway.app/project/[id] | API logs, CPU/memory, deploy status |
| Supabase | https://app.supabase.com/project/[ref] | DB connections, storage, query logs |
| Vercel | https://vercel.com/[team]/casazen | FE deploys, preview URLs, error tracking |
| Health endpoint | `GET /api/health` | Liveness check — target < 200 ms |

---

**Last Updated**: 2026-06-03
