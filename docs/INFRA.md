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
│  FE: casazen-app.vercel.app  (⚠️ see issue #187)         │
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
         ▲ Railway: push to `develop` (test) · Vercel: `develop` + PR previews (FE)
```

Two Supabase schemas (`casazen_test`, `casazen_prod`) in one free project — saves the free-tier limit.

---

## Git branch model

| Branch | Purpose | Deploy target |
|---|---|---|
| `develop` | Integration / shared test | Railway `test` + Vercel Preview (staging FE) |
| `main` | Production | Railway `production` + Vercel Production |
| `feature/*`, `fix/*`, `hotfix/*` | Short-lived work | PR → `develop` only |

- **Default branch** on GitHub: `develop` (feature PRs open against `develop`).
- **Release**: Stage 05 opens a release PR `develop` → `main`; only `release-manager` merges it.
- **Tags** `v*`: version label and GitHub Release only — not used as deploy triggers.

### One-time migration (if `develop` predates this model)

Align `develop` with current production code before switching Railway triggers:

```bash
# Backend and frontend (run in each repo)
git checkout develop && git pull
git merge origin/main   # resolve conflicts if any
git push origin develop
```

GitHub default branch is already `develop` on `casazen/backend` and `casazen/frontend`.

---

## Deploy model: native GitHub integrations

CasaZen uses **native deploys** from each provider’s GitHub app. GitHub Actions only **build, test, and verify** — they do **not** call `railway up` or push containers.

| Provider | Who deploys | Where runtime env vars live |
|---|---|---|
| **Railway** | Railway GitHub integration → `casazen/backend` | Railway dashboard → each environment (`test` / `production`) |
| **Vercel** | Vercel GitHub integration → `casazen/frontend` | Vercel dashboard → Preview / Production; `vercel.json` sets `outputDirectory: dist` |

**Production FE sanity check** (Stage 05 G10/G17): `curl -sf https://casazen-app.vercel.app` must return HTML containing `id="root"`. Do **not** use `https://casazen.vercel.app` (mislinked domain — issue #187). If the body shows `.env` placeholders or `GEMINI_API_KEY`, the Vercel project/domain is mislinked — fix the project root and output directory in the Vercel dashboard before promoting to `main`.
| **Supabase** | Hosted DB (no app deploy) | Supabase dashboard; optional secrets synced to GitHub by Supabase integration |

### What GitHub Actions still do

| Workflow | Trigger | Purpose |
|---|---|---|
| `ci-cd.yml` | PR, push `develop` / `main` | `dotnet test`, format, build |
| `ci-cd.yml` → `verify-test` | Push `develop` | Wait for Railway native deploy, then `GET /api/health` |
| `ci-cd.yml` → `verify-prod` | Push `main` | Wait for Railway prod deploy, health + smoke |
| `deploy-preview.yml` | PR | Comment with BE/FE URLs (no deploy) |
| `supabase-keepalive.yml` | Weekly cron | Optional Supabase ping |

### What is **not** synced automatically

Integrations link repos and trigger deploys. They **do not** copy env vars across platforms:

- Supabase connection string → must be set on **Railway** (`ConnectionStrings__DefaultConnection`)
- Auth0 / Stripe / Email (SMTP) → **Railway** only
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

### 2. Git branches (`casazen/backend` + `casazen/frontend`)

- [ ] Long-lived branch **`develop`** exists on both repos (integration / test)
- [ ] Long-lived branch **`main`** exists (production only)
- [ ] GitHub **default branch** = `develop` (new PRs target `develop`)
- [ ] Feature PRs: `feature/*` → `develop`
- [ ] Release PR (Stage 05): `develop` → `main` (squash merge by release-manager)

### 3. Railway (`casazen/backend`)

- [ ] New project → **Deploy from GitHub** → repo `casazen/backend`
- [ ] Environments: `test` + `production`
- [ ] **test**: trigger deploy on push to branch **`develop`**
- [ ] **production**: trigger deploy on push to branch **`main`** (disable autodeploy from `develop`)
- [ ] (Recommended) Enable **PR deployments** if you want a backend URL per PR; otherwise use shared test URL after merge
- [ ] Per environment, set **all** variables (see Railway section) — especially `ConnectionStrings__DefaultConnection` with correct `SearchPath`
- [ ] Enable **Public networking**; copy each environment’s HTTPS URL
- [ ] First deploy green in Railway dashboard

### 4. GitHub repo `casazen/backend` (variables only)

- [ ] **Variable** `RAILWAY_TEST_URL` = Railway test public URL (no trailing slash)
- [ ] **Variable** `RAILWAY_PROD_URL` = Railway production public URL
- [ ] (Optional) **Variables** `SUPABASE_PROJECT_URL`, `SUPABASE_DB_HOST` + **Secrets** `SUPABASE_ANON_KEY` for `supabase-keepalive.yml` — often already present if Supabase GitHub app is installed

You do **not** need: `RAILWAY_TOKEN`, `RAILWAY_SERVICE_TEST`, `RAILWAY_SERVICE_PROD`.

### 5. Vercel (`casazen/frontend`)

- [ ] Import repo; preset Vite; build `npm run build`; output `dist`
- [ ] **Production Branch** = `main` (Settings → Git)
- [ ] Enable deployments for branch **`develop`** (Preview env vars → Railway test API)
- [ ] Set `VITE_API_BASE_URL`, `VITE_AUTH0_*` for **Preview** and **Production** (see Vercel section)
- [ ] Confirm: push `develop` → staging FE; push `main` → production FE (see issue #187 for canonical URL confirmation)

### 6. Smoke test

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

Dashboard → **Project Settings → Database**:

| Value | Where |
|---|---|
| Host | `db.xxxxxxxxx.supabase.co` (copy from **Host** field — this is your `[REF]`) |
| Password | Project database password (set at project creation) |
| Database | `postgres` |
| Schema | `casazen_test` or `casazen_prod` (via `SearchPath`, not a separate database) |

**URI format** (Supabase dashboard — auto-converted at startup; include the full `options` query, not `?options` alone):

```
# Test
postgresql://postgres:YOUR_PASSWORD@db.YOUR_REF.supabase.co:5432/postgres?options=-csearch_path%3Dcasazen_test

# Production
postgresql://postgres:YOUR_PASSWORD@db.YOUR_REF.supabase.co:5432/postgres?options=-csearch_path%3Dcasazen_prod
```

**Npgsql format** (recommended for Railway `ConnectionStrings__DefaultConnection` and for `dotnet ef`):

```
Host=db.YOUR_REF.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=YOUR_PASSWORD;SearchPath=casazen_test;SSL Mode=Require;Trust Server Certificate=true
```

Replace `YOUR_REF` and `YOUR_PASSWORD` with real values — do not paste `[REF]` / `[PW]` literally.

### Apply migrations (scripts — one-time setup)

Credentials live in **`secrets/supabase.local.env`** (gitignored) and are copied once to **dotnet user-secrets** on your machine. You do not pass `--connection` manually after setup.

#### One-time setup

```powershell
# From backend repo root (Windows)
Copy-Item secrets\supabase.local.env.example secrets\supabase.local.env
# Edit secrets\supabase.local.env → SUPABASE_HOST + SUPABASE_PASSWORD

.\scripts\setup-supabase.ps1    # saves to user-secrets (outside repo)
```

```bash
# macOS / Linux
cp secrets/supabase.local.env.example secrets/supabase.local.env
# edit host + password
./scripts/setup-supabase.sh
```

`secrets/supabase.local.env` example:

```env
SUPABASE_HOST=db.abcdefghijklmnop.supabase.co
SUPABASE_PASSWORD=your-database-password
```

Host: use **Connect → Session pooler** host on Windows (IPv4), e.g. `aws-0-eu-west-1.pooler.supabase.com`, with `SUPABASE_USERNAME=postgres.YOUR_PROJECT_REF`. Direct `db.*.supabase.co` is IPv6-only and often fails locally.

Copy the exact host and username from Supabase **Connect** (not the project URL `https://ref.supabase.co`).

#### Run migrations (every time you add a migration)

```powershell
.\scripts\migrate.ps1              # default: casazen_test
.\scripts\migrate.ps1 -Target prod   # casazen_prod (before production release)
```

```bash
./scripts/migrate.sh test
./scripts/migrate.sh prod
```

Install EF tools once if needed: `dotnet tool install --global dotnet-ef`

#### What gets stored where

| Store | Contents | Committed? |
|---|---|---|
| `secrets/supabase.local.env` | Host + password | No (gitignored) |
| dotnet user-secrets (`casazen-backend-local`) | Full connection strings test/prod | No (local machine) |
| Railway env vars | Same connection string for runtime | No (Railway dashboard) |

`AppDbContextFactory` and `migrate.ps1` read from the env file or user-secrets — not from `localhost` in `appsettings.Development.json`.

#### Expected result

```
Applying migrations to Supabase schema: casazen_test
Build succeeded.
Applying migration '..._InitialCreate'.
Migrations applied successfully to casazen_test.
```

#### Troubleshooting

| Error | Fix |
|---|---|
| `secrets/supabase.local.env not found` | Copy from `.example` and fill in |
| `Failed to connect to 127.0.0.1:5432` | Run `.\scripts\migrate.ps1` — do not run bare `dotnet ef database update` |
| `password authentication failed` | Check password in Supabase; reset if needed |
| `schema "casazen_test" does not exist` | Run **Create schemas** SQL first |

### Supabase keep-alive (free tier pauses after 7 days)

Add a scheduled GitHub Actions ping or use the Supabase dashboard to configure the keep-alive option.

---

## Railway Setup (Backend API)

### Create project

1. https://railway.app → New Project → Deploy from GitHub → `casazen/backend`
2. Service name: `casazen-api`

### Create two environments

Railway → Project → **Environments** → Create:
- `test` — **Settings → Source**: deploy on push to **`develop`** (GitHub integration)
- `production` — **Settings → Source**: deploy on push to **`main`** (GitHub integration)

**PR previews (optional):** Railway → Service → Settings → enable PR deployments if you want a distinct backend URL per PR. Otherwise validate backend on shared test URL after merge to `develop`.

**Wait for CI (test env):** enable on the `develop` service if you want Railway to wait for `ci-cd.yml` on push to `develop`.

### Environment variables per Railway environment

Set in Railway dashboard (Variables tab), per environment:

```
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:8080
PORT=8080
ConnectionStrings__DefaultConnection=Host=db.YOUR_REF.supabase.co;Port=5432;Database=postgres;Username=postgres;Password=YOUR_PASSWORD;SearchPath=casazen_test;SSL Mode=Require;Trust Server Certificate=true
Auth0__Domain=[your-tenant.auth0.com]
Auth0__Audience=https://casazen-api
Stripe__SecretKey=[sk_live_... or sk_test_...]
Stripe__WebhookSecret=[whsec_...]
# Email — SMTP (MailKit). Use Gmail free tier or any SMTP provider.
# Option A: Direct SMTP (recommended)
Email__SmtpHost=smtp.gmail.com
Email__SmtpPort=587
Email__SmtpUsername=casazen@gmail.com
Email__SmtpPassword=[16-char-app-password]
# Option B: SendGrid SMTP relay (legacy, 100 emails/day free)
# Email__SendGridApiKey=SG....
Email__FromAddress=noreply@casazen.app
App__PublicSiteBaseUrl=https://casazen-app.vercel.app
Hangfire__DashboardEnabled=false
Cors__AllowedOrigins=https://casazen-app.vercel.app,https://casazen.app
```

`App__PublicSiteBaseUrl` is required for **supplier invite emails** (`POST /api/admin/suppliers/invite`): the signup link is built as `{PublicSiteBaseUrl}/login?inviteToken=…&email=…&comune=…`. Use the Vercel URL for the matching environment (test → preview/staging FE, production → `https://casazen.app` or production Vercel URL).

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

| Variable | Preview (develop / PR) | Production (main) |
|---|---|---|
| `VITE_API_BASE_URL` | `https://casazen-api-test.up.railway.app/api` | `https://casazen-api.up.railway.app/api` |
| `VITE_AUTH0_DOMAIN` | `dev-mp6wadq7j6bophl5.us.auth0.com` | same until prod Auth0 tenant is ready |
| `VITE_AUTH0_CLIENT_ID` | `[dev client id]` | same SPA client until prod tenant is ready |
| `VITE_AUTH0_AUDIENCE` | `https://casazen-api` | **`https://casazen-api`** (must match Railway `Auth0__Audience` on **both** environments) |

> **Critical:** Preview and Production must point to **different** `VITE_API_BASE_URL` hosts (test vs prod Railway). If Production accidentally uses the test API URL, staging will look fine while production users hit the wrong backend/schema. After every `main` deploy, CI runs `prod-deploy-smoke` to catch this.

### Git branches and deploy mapping

| Branch | Vercel | Railway |
|---|---|---|
| `develop` | Preview deployment (staging FE, Preview env vars) | `test` environment |
| `main` | Production → `https://casazen-app.vercel.app` (⚠️ see issue #187) | `production` environment |
| PR → `develop` | Per-PR preview URL | Optional PR deploy or shared test after merge |

Configure in Vercel → **Settings → Git**:
- **Production Branch**: `main`
- Leave automatic Preview deployments enabled (covers PRs and `develop` pushes)

### Auto-deploy behaviour

| Event | Result |
|---|---|
| PR opened / updated (base `develop`) | Preview URL → `https://preview-[hash].vercel.app` |
| Push to `develop` | Staging FE deploy (Preview env vars, points to test API) |
| Push to `main` | Production deploy → `https://casazen-app.vercel.app` (⚠️ confirm via issue #187) |

Vercel posts a comment on every PR with the preview URL.

---

## Multi-Environment Promotion Flow

```
PR opened → develop (BE or FE)
    │
    ├─ GitHub Actions: build & test (ci-cd.yml)
    ├─ Railway: optional PR deploy OR shared test after merge
    ├─ Vercel: per-PR preview URL
    ├─ deploy-preview.yml: PR comment with links
    │
    ▼
Merge feature PR → develop
    │
    ├─ Railway (native): deploy test environment
    ├─ ci-cd.yml verify-test: GET $RAILWAY_TEST_URL/api/health → 200
    ├─ Vercel (native): staging FE from develop branch
    └─ Human: bundle check + acceptance on test
    │
    ▼
Stage 05 — Release (release PR: develop → main)
    ├─ release-manager squash-merges develop → main
    ├─ Railway (native): deploy production environment
    ├─ ci-cd.yml verify-prod: health on $RAILWAY_PROD_URL
    ├─ Vercel (native): production FE from main
    └─ git tag vX.Y.Z on main (changelog only — no deploy trigger)
```

### Version tags

Tags `vMAJOR.MINOR.PATCH` remain the release version label (GitHub Releases, bundle files). They are **not** wired to Railway or Vercel deploy triggers.

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
- FE prod: https://casazen-app.vercel.app
- Released: (pending)

### Stage 05 Phase D checklist (mandatory)

After merge `develop` → `main`:

1. `.\scripts\migrate.ps1 -Target prod` — apply EF migrations to `casazen_prod` **before** relying on prod traffic
2. `.\scripts\release-smoke.ps1` — health + auth gates + FE SPA
3. `E2E_PROD_SMOKE=1 npm run test:e2e -- prod-deploy-smoke` (frontend repo) — authenticated prod FE + prod API
4. Confirm GitHub Actions `verify-prod` + frontend `e2e-deploy-smoke` on `main` are green
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
| Variable | `RAILWAY_TEST_URL` | Health check after push to `develop`; PR comment link |
| Variable | `RAILWAY_PROD_URL` | Health check after push to `main` |
| Variable | `STAGING_FE_URL` | Vercel develop deployment URL for staging FE smoke (frontend repo) |

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
| Auth0, Stripe, Email (SMTP) | **Railway** per environment |
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

**Last Updated**: 2026-06-03 (branch model: `develop` → test, `main` → prod)
