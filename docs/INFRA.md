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
         ▲ deployed automatically on PR open/update
```

Two Supabase schemas (`casazen_test`, `casazen_prod`) in one free project — saves the free-tier limit.

---

## Prerequisite: SQL Server → PostgreSQL Migration

CasaZen currently uses `Microsoft.EntityFrameworkCore.SqlServer`. Supabase and Railway both offer PostgreSQL. This is a **one-time** migration.

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
- `test` (default branch: `main`, auto-deploy on push)
- `production` (deploy only on tag `v*` via GitHub Actions webhook)

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
```

### Get service URLs

After first deploy:
- Test: shown in Railway service → Settings → Networking → Public URL
- Production: same, for the production environment service

### Get Railway tokens for CI/CD

Railway → Account Settings → API Tokens → New token.

Add to GitHub repo **Secrets**:

```
RAILWAY_TOKEN           → Railway API token
```

Add to GitHub repo **Variables** (not secret):

```
RAILWAY_TEST_URL        → https://casazen-api-test.up.railway.app
RAILWAY_PROD_URL        → https://casazen-api.up.railway.app
RAILWAY_SERVICE_TEST    → service ID (from Railway URL: railway.app/project/.../service/ID)
RAILWAY_SERVICE_PROD    → service ID for production service
```

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
    ├─ GitHub Actions: deploy BE branch to Railway test env
    ├─ Vercel: auto-creates FE preview URL
    │
    ▼
Stage 04 — Code Review
    │
    ▼
Stage 05 — Release
    ├─ Phase A: CI validation (dotnet test, build, format)
    ├─ Phase B: Test environment smoke tests
    │           GET $RAILWAY_TEST_URL/api/health → 200
    │           GET $VERCEL_PREVIEW_URL → 200
    │
    ├─ Phase C: HITL — Human validates feature on test environment
    │           (runs acceptance criteria from Stage 01 manually)
    │
    ├─ Phase D: Bundle check
    │           Are all issues in the Epic deployed to test AND verified?
    │           (check Sessions/bundle-<epic>.md)
    │
    └─ Phase E: Production promotion
                Merge PR → main (squash)
                Create git tag vX.Y.Z
                Tag triggers Railway prod deploy
                Vercel auto-deploys from main → prod
                Health check prod URLs
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

## GitHub Secrets / Variables Checklist

| Type | Name | Value |
|---|---|---|
| Secret | `RAILWAY_TOKEN` | Railway API token |
| Secret | `SUPABASE_CONNECTION_STRING_TEST` | Supabase URI with `casazen_test` schema |
| Secret | `SUPABASE_CONNECTION_STRING_PROD` | Supabase URI with `casazen_prod` schema |
| Variable | `RAILWAY_TEST_URL` | `https://casazen-api-test.up.railway.app` |
| Variable | `RAILWAY_PROD_URL` | `https://casazen-api.up.railway.app` |
| Variable | `RAILWAY_SERVICE_TEST` | Railway service ID (test) |
| Variable | `RAILWAY_SERVICE_PROD` | Railway service ID (production) |

---

## Monitoring

| Dashboard | URL | What to watch |
|---|---|---|
| Railway | https://railway.app/project/[id] | API logs, CPU/memory, deploy status |
| Supabase | https://app.supabase.com/project/[ref] | DB connections, storage, query logs |
| Vercel | https://vercel.com/[team]/casazen | FE deploys, preview URLs, error tracking |
| Health endpoint | `GET /api/health` | Liveness check — target < 200 ms |

---

**Last Updated**: 2026-06-02
